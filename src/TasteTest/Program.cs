using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using TasteTest;
using TasteTest.Components;
using TasteTest.Services;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Bind and validate before the container is built so a misconfigured environment fails fast
// with every problem listed, instead of failing on the first model call.
TasteTestOptions tasteTestOptions = TasteTestOptions.FromConfiguration(builder.Configuration);
tasteTestOptions.Validate();

builder.Services.AddSingleton(Options.Create(tasteTestOptions));
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddTasteTestTelemetry(builder.Configuration);

if (tasteTestOptions.UseSampleResponses)
{
    builder.Services.AddKeyedChatClient("gpt", new SampleChatClient(
            ProviderKind.OpenAI, builder.Configuration["TasteTest:Gpt:Model"] ??
                throw new InvalidOperationException("TasteTest:Gpt:Model is required.")))
        .UseFunctionInvocation(configure: client =>
        {
            client.MaximumIterationsPerRequest = 4;
            client.MaximumConsecutiveErrorsPerRequest = 4;
        })
        .UseOpenTelemetry(sourceName: TasteTestTelemetry.SourceName,
            configure: telemetry => telemetry.EnableSensitiveData = false);
    builder.Services.AddKeyedChatClient("claude", new SampleChatClient(
            ProviderKind.Anthropic, builder.Configuration["TasteTest:Claude:Model"] ??
                throw new InvalidOperationException("TasteTest:Claude:Model is required.")))
        .UseFunctionInvocation(configure: client =>
        {
            client.MaximumIterationsPerRequest = 4;
            client.MaximumConsecutiveErrorsPerRequest = 4;
        })
        .UseOpenTelemetry(sourceName: TasteTestTelemetry.SourceName,
            configure: telemetry => telemetry.EnableSensitiveData = false);
}
else
{
    #region Snippet:KeyedChatClientSetup
    builder.AddCredentialFromConfig("TasteTest:Credential");
    builder.AddKeyedResponsesChatClient("gpt", "TasteTest:Gpt")
        .UseFunctionInvocation(configure: client =>
        {
            client.MaximumIterationsPerRequest = 4;
            client.MaximumConsecutiveErrorsPerRequest = 4;
        })
        .UseOpenTelemetry(sourceName: "TasteTest.Chat", configure: telemetry => telemetry.EnableSensitiveData = false);
    builder.AddKeyedAnthropicChatClient("claude", "TasteTest:Claude")
        .UseFunctionInvocation(configure: client =>
        {
            client.MaximumIterationsPerRequest = 4;
            client.MaximumConsecutiveErrorsPerRequest = 4;
        })
        .UseOpenTelemetry(sourceName: "TasteTest.Chat", configure: telemetry => telemetry.EnableSensitiveData = false);
    #endregion
}

builder.Services.AddSingleton<ILaneOrderRandomizer, CryptoLaneOrderRandomizer>();

// Scoped to the Blazor circuit so lane order is randomized per visitor.
builder.Services.AddScoped<TasteTestSession>();

WebApplication app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapHealthChecks("/health").AllowAnonymous();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

/// <summary>Exposed so integration tests can host the application with <c>WebApplicationFactory</c>.</summary>
public partial class Program;
