using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using TasteTest.Services;

namespace TasteTest.Tests;

public sealed class TasteTestTelemetryTests
{
    [Theory]
    [InlineData("")]
    [InlineData("/collector/")]
    public async Task AddTasteTestTelemetry_ExportsToBothHttpSignalPaths(string prefix)
    {
        var received = new ConcurrentQueue<string>();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var collector = builder.Build();
        var path = prefix.TrimEnd('/');
        collector.MapPost(path + "/v1/{signal}", async context =>
        {
            await context.Request.Body.CopyToAsync(Stream.Null, context.RequestAborted);
            received.Enqueue(context.Request.Path.Value!);
            context.Response.ContentType = "application/x-protobuf";
            context.Response.StatusCode = 200;
        });
        await collector.StartAsync();
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["OTEL_EXPORTER_OTLP_ENDPOINT"] = collector.Urls.Single().TrimEnd('/') + prefix
                }).Build();
            var services = new ServiceCollection().AddLogging();
            services.AddTasteTestTelemetry(configuration);
            using var provider = services.BuildServiceProvider();
            using var traces = provider.GetRequiredService<TracerProvider>();
            using var metrics = provider.GetRequiredService<MeterProvider>();
            using var source = new ActivitySource(TasteTestTelemetry.SourceName);
            using (source.StartActivity("export-test")) { }
            using var meter = new Meter(TasteTestTelemetry.SourceName);
            meter.CreateCounter<int>("test.count").Add(1);

            Assert.True(traces.ForceFlush(10_000));
            Assert.True(metrics.ForceFlush(10_000));

            Assert.Contains(path + "/v1/traces", received);
            Assert.Contains(path + "/v1/metrics", received);
        }
        finally
        {
            await collector.StopAsync();
        }
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("file:///C:/traces")]
    [InlineData("ftp://collector")]
    public void AddTasteTestTelemetry_RejectsInvalidCollectorUrls(string endpoint)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["OTEL_EXPORTER_OTLP_ENDPOINT"] = endpoint }).Build();

        var error = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddTasteTestTelemetry(configuration));

        Assert.Contains("OTEL_EXPORTER_OTLP_ENDPOINT", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddTasteTestTelemetry_RegistersTheSharedTraceAndMeterSource()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTasteTestTelemetry(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        using var traces = provider.GetRequiredService<TracerProvider>();
        using var metrics = provider.GetRequiredService<MeterProvider>();
        using var source = new ActivitySource(TasteTestTelemetry.SourceName);

        using var activity = source.StartActivity("test-configuration");

        Assert.NotNull(activity);
        Assert.True(activity.IsAllDataRequested);
    }

    [Fact]
    public async Task ChatClient_EmitsUsageMetricsWithoutMessageContent()
    {
        var measurements = new ConcurrentQueue<(string Name, double Value)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == TasteTestTelemetry.SourceName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<int>((instrument, measurement, _, _) =>
            measurements.Enqueue((instrument.Name, measurement)));
        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
            measurements.Enqueue((instrument.Name, measurement)));
        listener.SetMeasurementEventCallback<double>((instrument, measurement, _, _) =>
            measurements.Enqueue((instrument.Name, measurement)));
        listener.Start();
        using var client = new UsageChatClient().AsBuilder()
            .UseOpenTelemetry(sourceName: TasteTestTelemetry.SourceName,
                configure: telemetry => telemetry.EnableSensitiveData = false)
            .Build();

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "A private prompt")]);

        Assert.Contains(measurements, item => item.Name == "gen_ai.client.token.usage" && item.Value == 4);
        Assert.Contains(measurements, item => item.Name == "gen_ai.client.token.usage" && item.Value == 6);
        Assert.Contains(measurements, item => item.Name == "gen_ai.client.operation.duration" && item.Value >= 0);
    }

    private sealed class UsageChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "A private answer"))
            {
                Usage = new UsageDetails { InputTokenCount = 4, OutputTokenCount = 6, TotalTokenCount = 10 }
            });

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("This test exercises non-streaming metrics.");

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata("test", defaultModelId: "test-model") : null;

        public void Dispose() { }
    }
}
