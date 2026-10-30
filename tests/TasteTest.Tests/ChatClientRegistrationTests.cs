using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics.CodeAnalysis;
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

// Exercise the experimental SDK config/DI contracts used by the sample.
#pragma warning disable SCME0002
public sealed class ChatClientRegistrationTests
{
    internal static Dictionary<string, string?> Configuration() => new()
    {
        ["TasteTest:Credential:CredentialSource"] = "ManagedIdentityCredential",
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
    public void CredentialSource_UsesTheNativeAzureResolver(string source, Type expectedType)
    {
        Dictionary<string, string?> configuration = Configuration();
        configuration["TasteTest:Credential:CredentialSource"] = source;
        HostApplicationBuilder builder = CreateBuilder(configuration);
        builder.AddAzureCredentialResolver();
        using ServiceProvider services = builder.Services.BuildServiceProvider();

        CredentialSettings? credential = builder.Configuration.GetAzureCredentialSettings(
            "TasteTest:Credential", [.. services.GetServices<CredentialResolver>()]);
        Assert.NotNull(credential);
        Assert.IsType(expectedType, credential.TokenProvider);
    }

    [Theory]
    [InlineData("DefaultAzureCredential")]
    [InlineData("UnrecognizedCredential")]
    [InlineData("")]
    public void NativeAzureResolver_DoesNotSelectImplicitOrUnknownChains(string source)
    {
        Dictionary<string, string?> configuration = Configuration();
        configuration["TasteTest:Credential:CredentialSource"] = source;
        HostApplicationBuilder builder = CreateBuilder(configuration);

        Assert.False(AzureCredentialResolver.Default.TryResolve(
            builder.Configuration.GetRequiredSection("TasteTest:Credential"), out _));
    }

    [Fact]
    public void ManagedIdentity_AcceptsTheNativeExplicitIdentitySchema()
    {
        Dictionary<string, string?> configuration = Configuration();
        configuration["TasteTest:Credential:ManagedIdentityIdKind"] = "ClientId";
        configuration["TasteTest:Credential:ManagedIdentityId"] = "00000000-0000-0000-0000-000000000001";
        HostApplicationBuilder builder = CreateBuilder(configuration);
        builder.AddAzureCredentialResolver();
        using ServiceProvider services = builder.Services.BuildServiceProvider();

        CredentialSettings? credential = builder.Configuration.GetAzureCredentialSettings(
            "TasteTest:Credential", [.. services.GetServices<CredentialResolver>()]);

        Assert.IsType<ManagedIdentityCredential>(credential?.TokenProvider);
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
        TestCredentialResolver resolver = AddTestCredentialResolver(builder, credential);
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
        Assert.Equal(1, resolver.Resolutions);
    }

    [Theory]
    [InlineData("gpt", "TasteTest:Gpt", "ApiKey")]
    [InlineData("gpt", "TasteTest:Gpt", "ApiKeyCredential")]
    [InlineData("claude", "TasteTest:Claude", "ApiKey")]
    [InlineData("claude", "TasteTest:Claude", "ApiKeyCredential")]
    public async Task ApiKey_UsesTheNativeInlineCredentialSchema(string key, string section, string source)
    {
        Dictionary<string, string?> configuration = Configuration();
        configuration[section + ":Credential"] = null;
        configuration[section + ":Credential:CredentialSource"] = source;
        configuration[section + ":Credential:Key"] = "not-a-real-api-key";
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
    public async Task ApiKey_CanUseASharedCredentialReference(string key, string section)
    {
        Dictionary<string, string?> configuration = Configuration();
        configuration["TasteTest:Credential:CredentialSource"] = "ApiKey";
        configuration["TasteTest:Credential:Key"] = "not-a-real-api-key";
        configuration.Remove("TasteTest:Credential:Scope");
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
        configuration[section + ":Credential:CredentialSource"] = "ApiKey";
        configuration[section + ":Credential:Key"] = "not-a-real-api-key";
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            Register(CreateBuilder(configuration), key, section));
        Assert.Contains("Replace the $ reference before adding inline credential fields.", error.Message, StringComparison.Ordinal);
        configuration.Remove(section + ":Credential:CredentialSource");
        configuration.Remove(section + ":Credential:Key");
        configuration[section + ":Credential"] = null;
        Assert.Throws<InvalidOperationException>(() => Register(CreateBuilder(configuration), key, section));
    }

    [Theory]
    [InlineData("gpt", "TasteTest:Gpt")]
    [InlineData("claude", "TasteTest:Claude")]
    public void Authentication_RequiresTheReferencedCredentialSourceAndScope(string key, string section)
    {
        Dictionary<string, string?> configuration = Configuration();
        configuration.Remove("TasteTest:Credential:CredentialSource");
        Assert.Throws<InvalidOperationException>(() => Register(CreateBuilder(configuration), key, section));
        configuration["TasteTest:Credential:CredentialSource"] = "AzureCliCredential";
        configuration.Remove("TasteTest:Credential:Scope");
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
        AddTestCredentialResolver(builder, credential);
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
        AddTestCredentialResolver(builder, credential);
        builder.AddKeyedAnthropicChatClient("claude", "TasteTest:Claude", http);
        using ServiceProvider services = builder.Services.BuildServiceProvider();
        IChatClient client = services.GetRequiredKeyedService<IChatClient>("claude");

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "First")]);
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Second")]);

        Assert.Equal(1, credential.Calls);
        Assert.Equal(["token-1", "token-1"], transport.Authorization.Select(header => header.Parameter));
    }

    [Fact]
    public void ReadmeSnippets_MatchEveryCompiledSourceRegion()
    {
        string root = SdkLiveTests.RepositoryRoot();
        string readme = File.ReadAllText(Path.Combine(root, "README.md"));
        Dictionary<string, string> snippets = new(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj")))
        {
            foreach (Match source in Regex.Matches(File.ReadAllText(file),
                @"(?m)^[ \t]*#region Snippet:(?<name>\w+)[ \t]*\r?\n(?<code>[\s\S]*?)^[ \t]*#endregion"))
            {
                Assert.True(snippets.TryAdd(source.Groups["name"].Value, NormalizeSnippet(source.Groups["code"].Value)),
                    $"Duplicate compiled snippet in {file}.");
            }
        }
        MatchCollection markdown = Regex.Matches(readme,
            @"```csharp Snippet:(?<name>\w+)[ \t]*\r?\n(?<code>[\s\S]*?)\r?\n```");
        Assert.NotEmpty(snippets);
        Assert.Equal(snippets.Count, markdown.Count);
        foreach (Match snippet in markdown)
        {
            string name = snippet.Groups["name"].Value;
            Assert.True(snippets.Remove(name, out string? compiled), $"Missing or duplicate compiled snippet: {name}.");
            Assert.Equal(compiled, NormalizeSnippet(snippet.Groups["code"].Value));
            Assert.DoesNotContain("var ", compiled!, StringComparison.Ordinal);
        }
        Assert.Empty(snippets);
    }

    private static string NormalizeSnippet(string code)
    {
        string[] lines = code.Replace("\r", "", StringComparison.Ordinal).Trim('\n').Split('\n');
        int indentation = lines.Where(line => !string.IsNullOrWhiteSpace(line))
            .Min(line => line.Length - line.TrimStart().Length);
        return string.Join("\n", lines.Select(line => line.Length >= indentation ? line[indentation..].TrimEnd() : "")).Trim();
    }

    internal static TestCredentialResolver AddTestCredentialResolver(IHostApplicationBuilder builder, TokenCredential credential)
    {
        TestCredentialResolver resolver = new(credential);
        builder.Services.AddSingleton<CredentialResolver>(resolver);
        builder.AddAzureCredentialResolver();
        return resolver;
    }

    internal sealed class TestCredentialResolver(TokenCredential credential) : CredentialResolver
    {
        public int Resolutions { get; private set; }

        public override bool TryResolve(
            IConfigurationSection section, [NotNullWhen(true)] out AuthenticationTokenProvider? tokenProvider)
        {
            if (section.Path.Equals("TasteTest:Credential", StringComparison.OrdinalIgnoreCase))
            {
                Resolutions++;
                tokenProvider = credential;
                return true;
            }
            tokenProvider = null;
            return false;
        }
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
#pragma warning restore SCME0002
