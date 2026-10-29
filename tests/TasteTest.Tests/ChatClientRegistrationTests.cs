using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Anthropic;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TasteTest.Services;

namespace TasteTest.Tests;

public sealed class ChatClientRegistrationTests
{
    internal static Dictionary<string, string?> Configuration() => new()
    {
        ["TasteTest:Credential:CredentialSource"] = "ManagedIdentityCredential",
        ["TasteTest:Credential:ManagedIdentityIdKind"] = "ClientId",
        ["TasteTest:Credential:TenantId"] = "",
        ["TasteTest:Credential:Scope"] = "https://ai.azure.com/.default",
        ["TasteTest:Gpt:Credential"] = "$TasteTest:Credential",
        ["TasteTest:Gpt:Model"] = "gpt-test",
        ["TasteTest:Gpt:Options:Endpoint"] = "https://models.example.test/openai/v1/",
        ["TasteTest:Claude:Credential"] = "$TasteTest:Credential",
        ["TasteTest:Claude:Model"] = "claude-test",
        ["TasteTest:Claude:Options:Endpoint"] = "https://models.example.test/anthropic/"
    };

    [Theory]
    [InlineData("ManagedIdentityCredential", typeof(ManagedIdentityCredential))]
    [InlineData("AzureCliCredential", typeof(AzureCliCredential))]
    [InlineData("AzureDeveloperCliCredential", typeof(AzureDeveloperCliCredential))]
    public void CredentialSource_IsAnExplicitConfigChoice(string source, Type expectedType)
    {
        Dictionary<string, string?> configuration = Configuration();
        configuration["TasteTest:Credential:CredentialSource"] = source;
        HostApplicationBuilder builder = CreateBuilder(configuration);
        builder.AddCredentialFromConfig("TasteTest:Credential");
        using ServiceProvider services = builder.Services.BuildServiceProvider();

        Assert.IsType(expectedType, services.GetRequiredKeyedService<TokenCredential>("TasteTest:Credential"));
    }

    [Theory]
    [InlineData("DefaultAzureCredential")]
    [InlineData("UnrecognizedCredential")]
    [InlineData("")]
    public void CredentialSource_RejectsImplicitOrUnknownChains(string source)
    {
        Dictionary<string, string?> configuration = Configuration();
        configuration["TasteTest:Credential:CredentialSource"] = source;
        HostApplicationBuilder builder = CreateBuilder(configuration);

        Assert.Throws<InvalidOperationException>(() => builder.AddCredentialFromConfig("TasteTest:Credential"));
    }

    [Fact]
    public async Task KeyedClients_BindModelsEndpointsAndTheSharedCredential()
    {
        CountingCredential credential = new();
        using ResponseHandler gptTransport = new("gpt-test");
        using ResponseHandler claudeTransport = new("claude-test");
        using HttpClient gptHttp = new(gptTransport);
        using HttpClient claudeHttp = new(claudeTransport);
        HostApplicationBuilder builder = CreateBuilder(Configuration());
        builder.AddCredentialFromConfig("TasteTest:Credential");
        builder.Services.AddKeyedSingleton<TokenCredential>("TasteTest:Credential", credential);
        builder.AddKeyedResponsesChatClient("gpt", "TasteTest:Gpt", gptHttp);
        builder.AddKeyedAnthropicChatClient("claude", "TasteTest:Claude", claudeHttp);
        using ServiceProvider services = builder.Services.BuildServiceProvider();

        IChatClient gpt = services.GetRequiredKeyedService<IChatClient>("gpt");
        IChatClient claude = services.GetRequiredKeyedService<IChatClient>("claude");
        Assert.NotSame(gpt, claude);
        Assert.Same(gpt, services.GetRequiredKeyedService<IChatClient>("gpt"));
        Assert.Null(services.GetService<IChatClient>());
        Assert.Equal("gpt-test", gpt.GetService<ChatClientMetadata>()!.DefaultModelId);
        Assert.Equal("claude-test", claude.GetService<ChatClientMetadata>()!.DefaultModelId);
        await gpt.GetResponseAsync([new ChatMessage(ChatRole.User, "Hello")]);
        await claude.GetResponseAsync([new ChatMessage(ChatRole.User, "Hello")]);

        Assert.Equal("https://models.example.test/openai/v1/responses", Assert.Single(gptTransport.Urls));
        Assert.Equal("https://models.example.test/anthropic/v1/messages", Assert.Single(claudeTransport.Urls));
        Assert.Equal("Bearer", Assert.Single(gptTransport.Authorization).Scheme);
        Assert.Equal("Bearer", Assert.Single(claudeTransport.Authorization).Scheme);
        Assert.All(credential.Scopes, scopes => Assert.Equal(["https://ai.azure.com/.default"], scopes));
        Assert.Null(services.GetRequiredKeyedService<AnthropicClient>("claude").ApiKey);
    }

    [Theory]
    [InlineData("gpt", "TasteTest:Gpt")]
    [InlineData("claude", "TasteTest:Claude")]
    public async Task ApiKey_IsAnAlternativeToCredentialConfiguration(string key, string section)
    {
        Dictionary<string, string?> configuration = Configuration();
        configuration[section + ":Credential"] = null;
        configuration[section + ":ApiKey"] = "not-a-real-api-key";
        HostApplicationBuilder builder = CreateBuilder(configuration);
        using ResponseHandler transport = new(key == "gpt" ? "gpt-test" : "claude-test");
        using HttpClient http = new(transport);
        Register(builder, key, section, http);
        using ServiceProvider services = builder.Services.BuildServiceProvider();

        await services.GetRequiredKeyedService<IChatClient>(key)
            .GetResponseAsync([new ChatMessage(ChatRole.User, "Hello")]);

        Assert.Equal("not-a-real-api-key", Assert.Single(transport.Authorization).Parameter);
    }

    [Theory]
    [InlineData("gpt", "TasteTest:Gpt")]
    [InlineData("claude", "TasteTest:Claude")]
    public void Authentication_RejectsAmbiguousOrMissingConfiguration(string key, string section)
    {
        Dictionary<string, string?> configuration = Configuration();
        configuration[section + ":ApiKey"] = "not-a-real-api-key";
        Assert.Throws<InvalidOperationException>(() => Register(CreateBuilder(configuration), key, section));
        configuration[section + ":ApiKey"] = null;
        configuration[section + ":Credential"] = null;
        Assert.Throws<InvalidOperationException>(() => Register(CreateBuilder(configuration), key, section));
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("/v1/")]
    [InlineData("api.openai.com/v1")]
    [InlineData("http://models.example.test/v1/")]
    [InlineData("ftp://models.example.test/v1/")]
    [InlineData("")]
    public void Endpoint_RejectsRelativeOrInsecureAddressesForBothProviders(string endpoint)
    {
        foreach ((string key, string section) in new[] { ("gpt", "TasteTest:Gpt"), ("claude", "TasteTest:Claude") })
        {
            Dictionary<string, string?> configuration = Configuration();
            configuration[section + ":Options:Endpoint"] = endpoint;
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
                Register(CreateBuilder(configuration), key, section));
            Assert.Contains("Endpoint", error.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("http://localhost:5055/")]
    [InlineData("http://127.0.0.1:5055/")]
    [InlineData("http://[::1]:5055/")]
    [InlineData("https://models.example.test/")]
    public void Endpoint_AllowsHttpsAndLoopbackHttpForBothProviders(string endpoint)
    {
        foreach ((string key, string section) in new[] { ("gpt", "TasteTest:Gpt"), ("claude", "TasteTest:Claude") })
        {
            Dictionary<string, string?> configuration = Configuration();
            configuration[section + ":Options:Endpoint"] = endpoint;
            Register(CreateBuilder(configuration), key, section);
        }
    }

    [Fact]
    public async Task AnthropicCredentialCallback_RefreshesExpiringTokensThroughTheSdkCache()
    {
        CountingCredential credential = new(expireSoon: true);
        using ResponseHandler transport = new("claude-test");
        using HttpClient http = new(transport);
        HostApplicationBuilder builder = CreateBuilder(Configuration());
        builder.AddCredentialFromConfig("TasteTest:Credential");
        builder.Services.AddKeyedSingleton<TokenCredential>("TasteTest:Credential", credential);
        builder.AddKeyedAnthropicChatClient("claude", "TasteTest:Claude", http);
        using ServiceProvider services = builder.Services.BuildServiceProvider();
        IChatClient client = services.GetRequiredKeyedService<IChatClient>("claude");

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "First")]);
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Second")]);

        Assert.Equal(2, credential.Calls);
        Assert.Equal(["token-1", "token-2"], transport.Authorization.Select(header => header.Parameter));
    }

    [Fact]
    public async Task AnthropicCredentialCallback_ReusesAValidToken()
    {
        CountingCredential credential = new();
        using ResponseHandler transport = new("claude-test");
        using HttpClient http = new(transport);
        HostApplicationBuilder builder = CreateBuilder(Configuration());
        builder.AddCredentialFromConfig("TasteTest:Credential");
        builder.Services.AddKeyedSingleton<TokenCredential>("TasteTest:Credential", credential);
        builder.AddKeyedAnthropicChatClient("claude", "TasteTest:Claude", http);
        using ServiceProvider services = builder.Services.BuildServiceProvider();
        IChatClient client = services.GetRequiredKeyedService<IChatClient>("claude");

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "First")]);
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Second")]);

        Assert.Equal(1, credential.Calls);
        Assert.Equal(["token-1", "token-1"], transport.Authorization.Select(header => header.Parameter));
    }

    [Fact]
    public void ReadmeSetup_IsTheCompiledProgramSnippet()
    {
        string root = SdkLiveTests.RepositoryRoot();
        string program = File.ReadAllText(Path.Combine(root, "src", "TasteTest", "Program.cs"));
        string readme = File.ReadAllText(Path.Combine(root, "README.md"));
        Match source = Regex.Match(program, @"#region Snippet:KeyedChatClientSetup\s*\r?\n(?<code>[\s\S]*?)\s*#endregion");
        Match markdown = Regex.Match(readme, @"```csharp Snippet:KeyedChatClientSetup\s*\r?\n(?<code>[\s\S]*?)\r?\n```");
        Assert.True(source.Success);
        Assert.True(markdown.Success);
        string compiled = string.Join("\n", source.Groups["code"].Value.Replace("\r", "", StringComparison.Ordinal)
            .Split('\n').Select(line => line.StartsWith("    ", StringComparison.Ordinal) ? line[4..] : line)).Trim();
        Assert.Equal(compiled, markdown.Groups["code"].Value.Replace("\r", "", StringComparison.Ordinal).Trim());
        Assert.DoesNotContain("var ", compiled, StringComparison.Ordinal);
    }

    private static HostApplicationBuilder CreateBuilder(Dictionary<string, string?> configuration)
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Services.AddLogging();
        builder.Configuration.AddInMemoryCollection(configuration);
        return builder;
    }

    private static void Register(HostApplicationBuilder builder, string key, string section, HttpClient? http = null)
    {
        if (key == "gpt") { builder.AddKeyedResponsesChatClient(key, section, http); }
        else { builder.AddKeyedAnthropicChatClient(key, section, http); }
    }

    private sealed class CountingCredential(bool expireSoon = false) : TokenCredential
    {
        public int Calls { get; private set; }
        public List<string[]> Scopes { get; } = [];

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Scopes.Add(requestContext.Scopes);
            Calls++;
            return new AccessToken($"token-{Calls}", DateTimeOffset.UtcNow.AddSeconds(expireSoon ? 10 : 3600));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class ResponseHandler(string model) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];
        public List<AuthenticationHeaderValue> Authorization { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!.AbsoluteUri);
            Authorization.Add(request.Headers.Authorization!);
            string json = model.StartsWith("gpt", StringComparison.Ordinal)
                ? """
                  {"id":"resp_test","object":"response","created_at":1,"status":"completed","model":"gpt-test",
                  "output":[{"type":"message","id":"msg_test","role":"assistant","status":"completed",
                  "content":[{"type":"output_text","text":"Hello","annotations":[]}]}],
                  "usage":{"input_tokens":1,"output_tokens":1,"total_tokens":2}}
                  """
                : """
                  {"id":"msg_test","type":"message","model":"claude-test","role":"assistant",
                  "content":[{"type":"text","text":"Hello"}],"stop_reason":"end_turn",
                  "stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":1}}
                  """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
