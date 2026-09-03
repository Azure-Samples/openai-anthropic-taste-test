using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;
using TasteTest;
using TasteTest.Components;
using TasteTest.Services;

var builder = WebApplication.CreateBuilder(args);

// Bind and validate before the container is built so a misconfigured environment fails fast
// with every problem listed, instead of failing on the first model call.
var tasteTestOptions = TasteTestOptions.FromConfiguration(builder.Configuration);
tasteTestOptions.Validate();

builder.Services.AddSingleton(Options.Create(tasteTestOptions));
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();

// Streaming responses stay open far longer than the 100-second default, so the request's own
// cancellation token bounds each call instead of the client timeout.
builder.Services.AddHttpClient(ModelClientNames.OpenAI)
    .ConfigureHttpClient(static client => client.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddHttpClient(ModelClientNames.Anthropic)
    .ConfigureHttpClient(static client => client.Timeout = Timeout.InfiniteTimeSpan);

// Entra only. Locally this resolves a developer credential; in Container Apps it resolves the
// user-assigned managed identity through AZURE_CLIENT_ID.
builder.Services.AddSingleton<TokenCredential>(static _ => new DefaultAzureCredential());

if (tasteTestOptions.UseSampleResponses)
{
    builder.Services.AddSingleton<IModelChatClientFactory, SampleModelChatClientFactory>();
}
else
{
    builder.Services.AddSingleton<IModelChatClientFactory, FoundryModelChatClientFactory>();
}

builder.Services.AddSingleton<ILaneOrderRandomizer, CryptoLaneOrderRandomizer>();

// Scoped to the Blazor circuit so lane order is randomized per visitor.
builder.Services.AddScoped<TasteTestSession>();

var app = builder.Build();

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
