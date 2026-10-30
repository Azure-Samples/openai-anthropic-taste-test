// Temporary MEAI integration until both SDKs offer keyed, config-driven chat registration
// with Entra credentials. Client construction and protocol handling stay in the official SDKs.
using System.ClientModel;
using System.ClientModel.Primitives;
using Anthropic;
using Anthropic.Credentials;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.AI;
using OpenAI.Responses;
using AnthropicAccessToken = Anthropic.Credentials.AccessToken;
using AnthropicClientOptions = Anthropic.Core.ClientOptions;

namespace TasteTest.Services;

// The native SDK configuration/DI and credential bridge APIs are experimental; this sample
// opts in so it can reuse SDK binding without an Azure-specific AI client or registration layer.
#pragma warning disable SCME0002
public static class ChatClientRegistration
{
    public static ChatClientBuilder AddKeyedResponsesChatClient(
        this IHostApplicationBuilder builder, string serviceKey, string sectionName, HttpClient? httpClient = null)
    {
        IConfigurationSection section = builder.Configuration.GetRequiredSection(sectionName);
        ValidateEndpoint(section);
        string model = Required(section, "Model");
        ValidateAuthentication(builder.Configuration, section);

        // Native binding creates the SCM BearerTokenPolicy from the resolved credential and scope.
        if (httpClient is null)
        {
            builder.AddKeyedResponsesClient(serviceKey, sectionName);
        }
        else
        {
            // Same SDK binder, with its settings callback supplying the test transport.
            builder.AddKeyedClient<ResponsesClient, ResponsesClientSettings>(serviceKey, sectionName,
                settings => settings.Options.Transport = new HttpClientPipelineTransport(httpClient));
        }

        return builder.Services.AddKeyedChatClient(serviceKey, services =>
            new OpenAIStreamCompletionClient(
                services.GetRequiredKeyedService<ResponsesClient>(serviceKey).AsIChatClient(model)))
            .ConfigureOptions(options =>
            {
                options.RawRepresentationFactory ??= _ => new CreateResponseOptions
                {
                    StoredOutputEnabled = false,
                    IncludedProperties = { IncludedResponseProperty.ReasoningEncryptedContent }
                };
            });
    }

    public static ChatClientBuilder AddKeyedAnthropicChatClient(
        this IHostApplicationBuilder builder, string serviceKey, string sectionName, HttpClient? httpClient = null)
    {
        IConfigurationSection section = builder.Configuration.GetRequiredSection(sectionName);
        Uri endpoint = ValidateEndpoint(section);
        string model = Required(section, "Model");
        string credentialPath = ValidateAuthentication(builder.Configuration, section);

        builder.Services.AddKeyedSingleton<AnthropicClient>(serviceKey, (services, _) =>
        {
            CredentialSettings credential = builder.Configuration.GetAzureCredentialSettings(
                credentialPath, [.. services.GetServices<CredentialResolver>()]) ??
                throw new InvalidOperationException($"{credentialPath} could not be resolved.");
            AnthropicClientOptions options = new()
            {
                BaseUrl = endpoint.AbsoluteUri.TrimEnd('/'),
                ApiKey = null,
                AuthToken = null
            };
            if (!string.IsNullOrWhiteSpace(credential.Key))
            {
                options.ApiKey = credential.Key;
            }
            else if (credential.TokenProvider is TokenCredential tokenCredential)
            {
                string scope = Required(builder.Configuration.GetRequiredSection(credentialPath), "Scope");
                options.Credentials = new EntraAccessTokenProvider(tokenCredential, scope);
            }
            else
            {
                throw new InvalidOperationException($"{credentialPath} must resolve to an API key or an Azure.Identity credential.");
            }
            if (httpClient is not null) { options.HttpClient = httpClient; }
            return new AnthropicClient(options);
        });
        return builder.Services.AddKeyedChatClient(serviceKey, services =>
            services.GetRequiredKeyedService<AnthropicClient>(serviceKey).AsIChatClient(model));
    }

    private static string Required(IConfigurationSection section, string name) =>
        !string.IsNullOrWhiteSpace(section[name])
            ? section[name]!
            : throw new InvalidOperationException($"{section.Path}:{name} is required.");

    private static Uri ValidateEndpoint(IConfigurationSection section)
    {
        string value = Required(section, "Options:Endpoint");
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttps &&
             !(endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback)))
        {
            throw new InvalidOperationException(
                $"{section.Path}:Options:Endpoint must be an absolute HTTPS URL; HTTP is allowed only for loopback.");
        }
        return endpoint;
    }

    private static string ValidateAuthentication(IConfiguration configuration, IConfigurationSection section)
    {
        IConfigurationSection credentialSection = section.GetSection("Credential");
        string credentialPath = credentialSection.Path;
        if (credentialSection.Value is string reference)
        {
            if (!reference.StartsWith('$') || reference.Length == 1 || credentialSection.GetChildren().Any())
            {
                throw new InvalidOperationException($"{credentialPath} must be a credential section or a $ reference, not both. " +
                    "Replace the $ reference before adding inline credential fields.");
            }
            credentialPath = reference[1..];
        }
        IConfigurationSection credential = configuration.GetRequiredSection(credentialPath);
        string source = Required(credential, "CredentialSource");
        if (source is "ApiKey" or "ApiKeyCredential")
        {
            Required(credential, "Key");
        }
        else
        {
            Required(credential, "Scope");
        }
        return credentialPath;
    }

    internal sealed class EntraAccessTokenProvider(TokenCredential credential, string scope) : IAccessTokenProvider
    {
        public async ValueTask<AnthropicAccessToken> GetTokenAsync(
            bool forceRefresh = false, CancellationToken cancellationToken = default)
        {
            Azure.Core.AccessToken token = await credential.GetTokenAsync(
                new TokenRequestContext([scope]), cancellationToken).ConfigureAwait(false);
            return new AnthropicAccessToken(token.Token, token.ExpiresOn.ToUnixTimeSeconds());
        }

        // Do not dispose the shared credential; the SDK owns this adapter and its token cache.
        public void Dispose() { }
    }
}
#pragma warning restore SCME0002
