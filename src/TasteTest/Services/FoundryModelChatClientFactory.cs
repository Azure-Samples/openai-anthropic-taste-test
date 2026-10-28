using System.ClientModel.Primitives;
using Anthropic.Foundry;
using Azure.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Responses;

namespace TasteTest.Services;

public sealed class FoundryModelChatClientFactory : IModelChatClientFactory, IDisposable
{
    private readonly IChatClient _openAI;
    private readonly IChatClient _anthropic;
    private readonly TasteTestOptions _options;

    public FoundryModelChatClientFactory(
        IOptions<TasteTestOptions> options,
        TokenCredential credential,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory? loggerFactory = null)
    {
        _options = options.Value;

        var openAIOptions = new OpenAIClientOptions
        {
            Endpoint = new Uri($"{_options.FoundryEndpoint}/openai/v1/"),
            Transport = new HttpClientPipelineTransport(
                httpClientFactory.CreateClient(ModelClientNames.OpenAI))
        };

        var openAIClient = new OpenAIClient(
                new BearerTokenPolicy(credential, "https://ai.azure.com/.default"),
                openAIOptions)
            .GetResponsesClient()
            .AsIChatClient(_options.AoaiDeploymentName);

        _openAI = new OpenAIStreamCompletionClient(openAIClient)
            .AsBuilder()
            .ConfigureOptions(options =>
            {
                // Keep tool iterations stateless too: avoid depending on a stored Responses ID
                // becoming available on another backend before the next tool-result request.
                options.RawRepresentationFactory ??= _ => new CreateResponseOptions
                {
                    StoredOutputEnabled = false,
                    IncludedProperties = { IncludedResponseProperty.ReasoningEncryptedContent }
                };
            })
            .UseFunctionInvocation(loggerFactory, configure: invocation =>
            {
                invocation.MaximumIterationsPerRequest = 4;
                invocation.MaximumConsecutiveErrorsPerRequest = 4;
            })
            .UseOpenTelemetry(loggerFactory, sourceName: TasteTestTelemetry.SourceName,
                configure: telemetry => telemetry.EnableSensitiveData = false)
            .Build();

        var anthropicClient = new AnthropicFoundryClient(
            new AnthropicFoundryIdentityTokenCredentials(
                credential,
                _options.FoundryResourceName))
        {
            HttpClient = httpClientFactory.CreateClient(ModelClientNames.Anthropic)
        };

        _anthropic = anthropicClient
            .AsIChatClient(_options.ClaudeDeploymentName, _options.MaxOutputTokens)
            .AsBuilder()
            .UseFunctionInvocation(loggerFactory, configure: invocation =>
            {
                invocation.MaximumIterationsPerRequest = 4;
                invocation.MaximumConsecutiveErrorsPerRequest = 4;
            })
            .UseOpenTelemetry(loggerFactory, sourceName: TasteTestTelemetry.SourceName,
                configure: telemetry => telemetry.EnableSensitiveData = false)
            .Build();
    }

    public IChatClient GetClient(ProviderKind provider) =>
        provider switch
        {
            ProviderKind.OpenAI => _openAI,
            ProviderKind.Anthropic => _anthropic,
            _ => throw ModelCatalog.UnknownProvider(provider)
        };

    public ModelIdentity GetIdentity(ProviderKind provider) =>
        ModelCatalog.CreateIdentity(provider, _options);

    public void Dispose()
    {
        _openAI.Dispose();
        _anthropic.Dispose();
    }
}
