using Azure.Identity;
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
    ConfigureChatClient(builder.Services.AddKeyedChatClient("gpt", new SampleChatClient(
            ProviderKind.OpenAI, builder.Configuration["TasteTest:Gpt:Model"] ??
                throw new InvalidOperationException("TasteTest:Gpt:Model is required."))));
    ConfigureChatClient(builder.Services.AddKeyedChatClient("claude", new SampleChatClient(
            ProviderKind.Anthropic, builder.Configuration["TasteTest:Claude:Model"] ??
                throw new InvalidOperationException("TasteTest:Claude:Model is required."))));
}
else
{
    // Azure.Identity's credential resolver/configuration integration is intentionally experimental.
    #pragma warning disable SCME0002
    #region Snippet:KeyedChatClientSetup
    builder.AddAzureCredentialResolver();
    ConfigureChatClient(builder.AddKeyedResponsesChatClient("gpt", "TasteTest:Gpt"));
    ConfigureChatClient(builder.AddKeyedAnthropicChatClient("claude", "TasteTest:Claude"));
    #endregion
    #pragma warning restore SCME0002
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

#region Snippet:ChatClientMiddleware
static void ConfigureChatClient(ChatClientBuilder client)
{
    client.UseFunctionInvocation(configure: invocation =>
        {
            invocation.MaximumIterationsPerRequest = 4;
            invocation.MaximumConsecutiveErrorsPerRequest = 4;
        })
        .UseOpenTelemetry(sourceName: "TasteTest.Chat", configure: telemetry => telemetry.EnableSensitiveData = false);
}
#endregion

/// <summary>Exposed so integration tests can host the application with <c>WebApplicationFactory</c>.</summary>
public partial class Program;
