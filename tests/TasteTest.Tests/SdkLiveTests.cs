using System.Collections.Concurrent;
using System.Diagnostics;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TasteTest.Services;
using Xunit.Abstractions;

namespace TasteTest.Tests;

// Exercise the SDK's experimental credential/configuration integration used in Program.cs.
#pragma warning disable SCME0002
public sealed class SdkLiveTests(ITestOutputHelper output)
{
    [LiveTheory]
    [Trait("Category", "Live")]
    [InlineData("gpt", "TasteTest:Gpt")]
    [InlineData("claude", "TasteTest:Claude")]
    public async Task KeylessSdk_StreamsAToolResultAndTelemetry(string key, string section)
    {
        string? endpoint = Environment.GetEnvironmentVariable(section.Replace(":", "__", StringComparison.Ordinal) + "__Options__Endpoint");
        Assert.False(string.IsNullOrWhiteSpace(endpoint), "Explicit live endpoint configuration is required.");
        string? source = Environment.GetEnvironmentVariable("TasteTest__Credential__CredentialSource");
        Assert.True(source is "AzureCliCredential" or "AzureDeveloperCliCredential",
            "Live checks require an explicit CLI credential source.");
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Services.AddLogging();
        builder.Configuration.AddJsonFile(Path.Combine(RepositoryRoot(), "src", "TasteTest", "appsettings.json"))
            .AddEnvironmentVariables();
        builder.AddAzureCredentialResolver();
        ChatClientBuilder chat = key == "gpt"
            ? builder.AddKeyedResponsesChatClient(key, section)
            : builder.AddKeyedAnthropicChatClient(key, section);
        chat.UseFunctionInvocation(configure: client =>
            {
                client.MaximumIterationsPerRequest = 4;
                client.MaximumConsecutiveErrorsPerRequest = 4;
            })
            .UseOpenTelemetry(sourceName: TasteTestTelemetry.SourceName,
                configure: telemetry => telemetry.EnableSensitiveData = false);
        using ServiceProvider services = builder.Services.BuildServiceProvider();
        IChatClient client = services.GetRequiredKeyedService<IChatClient>(key);
        ConcurrentQueue<Activity> activities = new();
        using ActivityListener listener = new()
        {
            ShouldListenTo = activitySource => activitySource.Name == TasteTestTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Enqueue
        };
        ActivitySource.AddActivityListener(listener);
        using Activity parent = new Activity("sdk-live-test").Start();
        int lookups = 0;
        AIFunction catalog = DemoSupportCatalog.CreateTool((result, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.NotNull(result.Plan);
            Interlocked.Increment(ref lookups);
            return Task.CompletedTask;
        });
        List<ChatResponseUpdate> updates = [];
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(120));
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "Use lookup_support_plan for Enterprise. State its USD price per seat and first-response hours.")],
            new ChatOptions
            {
                Instructions = "Use the supplied catalog tool before answering. Do not invent plan facts.",
                Tools = [catalog],
                MaxOutputTokens = 4096,
                Reasoning = new() { Effort = ReasoningEffort.Low }
            }, deadline.Token))
        {
            updates.Add(update);
        }
        ChatResponse response = updates.ToChatResponse();
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
        Assert.True(lookups > 0);
        Assert.False(string.IsNullOrWhiteSpace(response.Text));
        Assert.NotNull(response.Usage);
        Assert.True(response.Usage.InputTokenCount > 0);
        Assert.True(response.Usage.OutputTokenCount > 0);
        Activity[] spans = activities.Where(activity => activity.TraceId == parent.TraceId).ToArray();
        Assert.Contains(spans, span => span.OperationName.StartsWith("chat", StringComparison.Ordinal));
        Assert.Contains(spans, span => span.OperationName.StartsWith("execute_tool", StringComparison.Ordinal));
        output.WriteLine($"{key}: credential={source}, lookups={lookups}, model/tool spans={spans.Length}, " +
            $"input tokens={response.Usage.InputTokenCount}, output tokens={response.Usage.OutputTokenCount}");
    }

    internal static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "azure.yaml")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("The sample repository root was not found.");
    }

    private sealed class LiveTheoryAttribute : TheoryAttribute
    {
        public LiveTheoryAttribute()
        {
            if (Environment.GetEnvironmentVariable("TASTE_TEST_RUN_LIVE") != "1")
            {
                Skip = "Opt-in live model check: configure endpoints and CLI credential, then set TASTE_TEST_RUN_LIVE=1.";
            }
        }
    }
}
