// Temporary MEAI integration until both SDKs offer keyed, config-driven chat registration
// with Entra credentials. Client construction and protocol handling stay in the official SDKs.
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics.CodeAnalysis;
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
    public static void AddCredentialFromConfig(this IHostApplicationBuilder builder, string sectionName)
    {
        IConfigurationSection section = builder.Configuration.GetRequiredSection(sectionName);
        string source = Required(section, "CredentialSource");
        Required(section, "Scope");
        if (source is not ("ManagedIdentityCredential" or "AzureCliCredential" or "AzureDeveloperCliCredential"))
        {
            throw new InvalidOperationException($"{sectionName}:CredentialSource must select an explicit managed-identity or CLI credential.");
        }
        string? clientId = section["ClientId"];
        string? tenantId = string.IsNullOrWhiteSpace(section["TenantId"]) ? null : section["TenantId"];
        if (source == "ManagedIdentityCredential" &&
            section["ManagedIdentityIdKind"] is string kind && kind != "ClientId")
        {
            throw new InvalidOperationException($"{sectionName}:ManagedIdentityIdKind must be ClientId.");
        }

        builder.Services.AddKeyedSingleton<TokenCredential>(sectionName, (_, _) => source switch
        {
            "ManagedIdentityCredential" => new ManagedIdentityCredential(
                string.IsNullOrWhiteSpace(clientId)
                    ? ManagedIdentityId.SystemAssigned
                    : ManagedIdentityId.FromUserAssignedClientId(clientId)),
            "AzureCliCredential" => new AzureCliCredential(new AzureCliCredentialOptions { TenantId = tenantId }),
            "AzureDeveloperCliCredential" => new AzureDeveloperCliCredential(new AzureDeveloperCliCredentialOptions { TenantId = tenantId }),
            _ => throw new InvalidOperationException("Unsupported credential source.")
        });
        builder.Services.AddSingleton<CredentialResolver>(services =>
            new ConfiguredCredentialResolver(sectionName,
                services.GetRequiredKeyedService<TokenCredential>(sectionName)));
    }

    public static ChatClientBuilder AddKeyedResponsesChatClient(
        this IHostApplicationBuilder builder, string serviceKey, string sectionName, HttpClient? httpClient = null)
    {
        IConfigurationSection section = builder.Configuration.GetRequiredSection(sectionName);
        ValidateEndpoint(section);
        string model = Required(section, "Model");
        string? apiKey = section["ApiKey"];
        ValidateAuthentication(section, apiKey);

        // Native binding creates the SCM BearerTokenPolicy from the resolved credential and scope.
        IClientBuilder native = builder.AddKeyedResponsesClient(serviceKey, sectionName);
        native.PostConfigure(settings =>
        {
            ResponsesClientSettings responses = (ResponsesClientSettings)settings;
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                // The SDK schema nests key auth under Credential; the sample exposes ApiKey as its alternative.
                responses.Credential = new CredentialSettings(section) { CredentialSource = "ApiKey", Key = apiKey };
                responses.CredentialProvider = null;
            }
            if (httpClient is not null)
            {
                responses.Options.Transport = new HttpClientPipelineTransport(httpClient);
            }
        });

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
        string? apiKey = section["ApiKey"];
        string? credentialPath = ValidateAuthentication(section, apiKey);

        builder.Services.AddKeyedSingleton<AnthropicClient>(serviceKey, (services, _) =>
        {
            AnthropicClientOptions options = new()
            {
                BaseUrl = endpoint.AbsoluteUri.TrimEnd('/'),
                ApiKey = apiKey,
                AuthToken = null
            };
            if (credentialPath is not null)
            {
                string scope = Required(builder.Configuration.GetRequiredSection(credentialPath), "Scope");
                options.Credentials = new EntraAccessTokenProvider(
                    services.GetRequiredKeyedService<TokenCredential>(credentialPath), scope);
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

    private static string? ValidateAuthentication(IConfigurationSection section, string? apiKey)
    {
        string? reference = section["Credential"];
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            if (!string.IsNullOrWhiteSpace(reference))
            {
                throw new InvalidOperationException($"{section.Path} must set ApiKey or Credential, not both.");
            }
            return null;
        }
        if (string.IsNullOrWhiteSpace(reference) || !reference.StartsWith('$') || reference.Length == 1)
        {
            throw new InvalidOperationException($"{section.Path}:Credential must reference a registered credential section using $.");
        }
        return reference[1..];
    }

    private sealed class ConfiguredCredentialResolver(string path, TokenCredential credential) : CredentialResolver
    {
        public override bool TryResolve(
            IConfigurationSection section, [NotNullWhen(true)] out AuthenticationTokenProvider? tokenProvider)
        {
            bool matches = string.Equals(section.Path, path, StringComparison.OrdinalIgnoreCase);
            tokenProvider = matches ? credential : null;
            return matches;
        }
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

        // DI owns the TokenCredential; the SDK owns this stateless adapter and its token cache.
        public void Dispose() { }
    }
}
#pragma warning restore SCME0002
