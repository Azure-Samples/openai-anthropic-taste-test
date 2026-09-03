using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;
using TasteTest;
using TasteTest.Components;
using TasteTest.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var tasteTestOptions = TasteTestOptions.FromConfiguration(builder.Configuration);
tasteTestOptions.Validate();

builder.Services.AddSingleton<IOptions<TasteTestOptions>>(Options.Create(tasteTestOptions));
builder.Services.AddSingleton<TokenCredential>(_ => new DefaultAzureCredential());
builder.Services.AddHttpClient(ModelClientNames.OpenAI)
    .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddHttpClient(ModelClientNames.Anthropic)
    .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan);

if (tasteTestOptions.UseSampleResponses)
{
    builder.Services.AddSingleton<IModelChatClientFactory, SampleModelChatClientFactory>();
}
else
{
    builder.Services.AddSingleton<IModelChatClientFactory, FoundryModelChatClientFactory>();
}

builder.Services.AddSingleton<ILaneOrderRandomizer, CryptoLaneOrderRandomizer>();
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
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }))
    .ExcludeFromDescription();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

public partial class Program;
