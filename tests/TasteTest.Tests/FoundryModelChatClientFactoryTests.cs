using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using TasteTest.Services;

namespace TasteTest.Tests;

public sealed class FoundryModelChatClientFactoryTests
{
    [Theory]
    [InlineData(ProviderKind.OpenAI, "/openai/v1/responses", "max_output_tokens")]
    [InlineData(ProviderKind.Anthropic, "/anthropic/v1/messages", "max_tokens")]
    public async Task GetResponseAsync_UsesTheNativeApiAndEntraToken(
        ProviderKind provider,
        string expectedPath,
        string outputLimitProperty)
    {
        var credential = new TestCredential();
        using var transport = new RecordingTransport();
        using var factory = new FoundryModelChatClientFactory(
            Options.Create(new TasteTestOptions
            {
                FoundryEndpoint = "https://test-account.services.ai.azure.com",
                FoundryResourceName = "test-account",
                AoaiDeploymentName = "gpt-test",
                ClaudeDeploymentName = "claude-test"
            }),
            credential,
            transport);

        var response = await factory.GetClient(provider).GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Test prompt")],
            new ChatOptions { MaxOutputTokens = 256 });

        var request = Assert.Single(transport.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(expectedPath, request.Path);
        Assert.Equal("Bearer test-token", request.Authorization, ignoreCase: true);
        Assert.False(request.HasApiKey);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal(provider == ProviderKind.OpenAI ? "gpt-test" : "claude-test",
            body.RootElement.GetProperty("model").GetString());
        Assert.Equal(256, body.RootElement.GetProperty(outputLimitProperty).GetInt32());
        Assert.Equal("Test response", response.Text);
        Assert.NotNull(response.Usage);
        Assert.Equal(4, response.Usage.InputTokenCount);
        Assert.Equal(6, response.Usage.OutputTokenCount);
        Assert.True(credential.Calls > 0);
    }

    private sealed class TestCredential : TokenCredential
    {
        public int Calls { get; private set; }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Contains("https://ai.azure.com/.default", requestContext.Scopes);
            Calls++;
            return new AccessToken("test-token", DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed record CapturedRequest(
        HttpMethod Method, string Path, string? Authorization, bool HasApiKey, string Body);

    private sealed class RecordingTransport : HttpMessageHandler, IHttpClientFactory
    {
        public List<CapturedRequest> Requests { get; } = [];

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(new CapturedRequest(
                request.Method,
                path,
                request.Headers.Authorization?.ToString(),
                request.Headers.Contains("api-key") || request.Headers.Contains("x-api-key"),
                await request.Content!.ReadAsStringAsync(cancellationToken)));

            var response = path.EndsWith("/responses", StringComparison.Ordinal)
                ? """
                  {
                    "id":"resp_test", "object":"response", "created_at":1, "status":"completed",
                    "model":"gpt-test",
                    "output":[{"type":"message","id":"msg_test","role":"assistant","status":"completed",
                      "content":[{"type":"output_text","text":"Test response","annotations":[]}]}],
                    "usage":{"input_tokens":4,"output_tokens":6,"total_tokens":10,
                      "input_tokens_details":{"cached_tokens":0},"output_tokens_details":{"reasoning_tokens":0}}
                  }
                  """
                : """
                  {
                    "id":"msg_test", "type":"message", "model":"claude-test", "role":"assistant",
                    "content":[{"type":"text","text":"Test response"}], "stop_reason":"end_turn",
                    "stop_sequence":null, "usage":{"input_tokens":4,"output_tokens":6}
                  }
                  """;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        }
    }
}
