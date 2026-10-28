using System.Net;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Diagnostics;
using Azure.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using TasteTest.Services;

namespace TasteTest.Tests;

public sealed class FoundryModelChatClientFactoryTests
{
    [Theory]
    [InlineData(ProviderKind.OpenAI)]
    [InlineData(ProviderKind.Anthropic)]
    public async Task GetStreamingResponseAsync_InvokesTheSameBusinessToolThroughBothSdks(ProviderKind provider)
    {
        using var transport = new RecordingTransport(invokeTools: true);
        using var factory = CreateFactory(transport);
        var plans = new List<string>();
        var tool = DemoSupportCatalog.CreateTool((plan, _) =>
        {
            plans.Add(plan.Plan!.Name);
            return Task.CompletedTask;
        });
        var updates = new List<ChatResponseUpdate>();

        await foreach (var update in factory.GetClient(provider).GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "Compare Business and Enterprise for 80 seats.")],
            new ChatOptions { Tools = [tool], MaxOutputTokens = 4096 }))
        {
            updates.Add(update);
        }

        Assert.Equal(["Business", "Enterprise"], plans);
        Assert.Equal(2, transport.Requests.Count);
        var response = updates.ToChatResponse();
        Assert.Equal("Test response", response.Text);
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
        Assert.NotNull(response.Usage);
        Assert.Equal(26, response.Usage.OutputTokenCount);
        using var first = JsonDocument.Parse(transport.Requests[0].Body);
        Assert.Single(first.RootElement.GetProperty("tools").EnumerateArray());
        var followUp = transport.Requests[1].Body;
        Assert.Contains("Business", followUp, StringComparison.Ordinal);
        Assert.Contains("Enterprise", followUp, StringComparison.Ordinal);
        Assert.Contains("75", followUp, StringComparison.Ordinal);
        if (provider == ProviderKind.OpenAI)
        {
            using var next = JsonDocument.Parse(followUp);
            Assert.False(next.RootElement.GetProperty("store").GetBoolean());
            Assert.False(next.RootElement.TryGetProperty("previous_response_id", out _));
            Assert.Contains("reasoning.encrypted_content",
                next.RootElement.GetProperty("include").EnumerateArray().Select(value => value.GetString()));
            Assert.Contains("function_call_output", followUp, StringComparison.Ordinal);
            Assert.Contains("Compare Business", followUp, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("tool_result", followUp, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task FunctionInvocation_IsBoundedAndMakesAFinalCallWithoutTools()
    {
        using var transport = new RecordingTransport(invokeTools: true, repeatTools: true);
        using var factory = CreateFactory(transport);
        var count = 0;
        var tool = DemoSupportCatalog.CreateTool((_, _) =>
        {
            count++;
            return Task.CompletedTask;
        });

        await foreach (var _ in factory.GetClient(ProviderKind.OpenAI).GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "Compare plans.")], new ChatOptions { Tools = [tool] }))
        {
        }

        Assert.Equal(8, count);
        Assert.Equal(5, transport.Requests.Count);
        using var last = JsonDocument.Parse(transport.Requests[^1].Body);
        Assert.True(!last.RootElement.TryGetProperty("tools", out var tools) || tools.GetArrayLength() == 0);
    }

    [Theory]
    [InlineData(ProviderKind.OpenAI)]
    [InlineData(ProviderKind.Anthropic)]
    public async Task Telemetry_ReportsToolAndModelSpansWithoutPromptArgumentsOrAnswers(ProviderKind provider)
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TasteTestTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Enqueue
        };
        ActivitySource.AddActivityListener(listener);
        using var parent = new Activity("private-test").Start();
        using var transport = new RecordingTransport(invokeTools: true);
        using var factory = CreateFactory(transport);
        var tool = DemoSupportCatalog.CreateTool((_, _) => Task.CompletedTask);

        await foreach (var _ in factory.GetClient(provider).GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "PROMPT_MUST_NOT_BE_EXPORTED")],
            new ChatOptions { Tools = [tool] }))
        {
        }

        var spans = stopped.Where(activity => activity.TraceId == parent.TraceId).ToArray();
        Assert.Contains(spans, span => span.OperationName.StartsWith("chat", StringComparison.Ordinal));
        Assert.Contains(spans, span => span.OperationName.StartsWith("execute_tool", StringComparison.Ordinal));
        var exportedText = string.Join("\n", spans.Select(span =>
            string.Join("\n", span.TagObjects.Select(tag => $"{tag.Key}={tag.Value}")) +
            string.Join("\n", span.Events.Select(evt => evt.Name + string.Join(",", evt.Tags)))));
        Assert.DoesNotContain("PROMPT_MUST_NOT_BE_EXPORTED", exportedText, StringComparison.Ordinal);
        Assert.DoesNotContain("Test response", exportedText, StringComparison.Ordinal);
        Assert.DoesNotContain("Enterprise", exportedText, StringComparison.Ordinal);
        Assert.DoesNotContain("Business", exportedText, StringComparison.Ordinal);
    }

    private static FoundryModelChatClientFactory CreateFactory(RecordingTransport transport) =>
        new(Options.Create(new TasteTestOptions
        {
            FoundryEndpoint = "https://test-account.services.ai.azure.com",
            FoundryResourceName = "test-account",
            AoaiDeploymentName = "gpt-test",
            ClaudeDeploymentName = "claude-test"
        }), new TestCredential(), transport);

    [Theory]
    [InlineData(ProviderKind.OpenAI, false, false)]
    [InlineData(ProviderKind.Anthropic, false, false)]
    [InlineData(ProviderKind.OpenAI, true, false)]
    [InlineData(ProviderKind.Anthropic, true, false)]
    [InlineData(ProviderKind.OpenAI, true, true)]
    public async Task GetStreamingResponseAsync_PreservesTerminalStatusAndUsage(
        ProviderKind provider, bool tokenLimit, bool partialText)
    {
        using var transport = new RecordingTransport(tokenLimit, partialText);
        using var factory = new FoundryModelChatClientFactory(
            Options.Create(new TasteTestOptions
            {
                FoundryEndpoint = "https://test-account.services.ai.azure.com",
                FoundryResourceName = "test-account",
                AoaiDeploymentName = "gpt-test",
                ClaudeDeploymentName = "claude-test"
            }), new TestCredential(), transport);
        var updates = new List<ChatResponseUpdate>();

        await foreach (var update in factory.GetClient(provider).GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "Test prompt")],
            new ChatOptions
            {
                MaxOutputTokens = 4096,
                Reasoning = new() { Effort = ReasoningEffort.Low }
            }))
        {
            updates.Add(update);
        }

        var response = updates.ToChatResponse();
        Assert.Equal(tokenLimit ? ChatFinishReason.Length : ChatFinishReason.Stop, response.FinishReason);
        Assert.Equal(tokenLimit && !partialText ? string.Empty : "Test response", response.Text);
        Assert.NotNull(response.Usage);
        Assert.Equal(4, response.Usage.InputTokenCount);
        Assert.Equal(tokenLimit ? 900 : 6, response.Usage.OutputTokenCount);
        var request = Assert.Single(transport.Requests);
        Assert.Equal(provider == ProviderKind.OpenAI ? "/openai/v1/responses" : "/anthropic/v1/messages", request.Path);
        using var body = JsonDocument.Parse(request.Body);
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal(4096, body.RootElement.GetProperty(
            provider == ProviderKind.OpenAI ? "max_output_tokens" : "max_tokens").GetInt32());
        if (provider == ProviderKind.OpenAI)
        {
            Assert.Equal("low", body.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
        }
        else
        {
            var thinking = body.RootElement.GetProperty("thinking");
            Assert.Equal("adaptive", thinking.GetProperty("type").GetString());
            Assert.False(thinking.TryGetProperty("budget_tokens", out _));
            Assert.Equal("low", body.RootElement.GetProperty("output_config").GetProperty("effort").GetString());
        }
    }

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

    private sealed class RecordingTransport(
        bool tokenLimit = false, bool partialText = false,
        bool invokeTools = false, bool repeatTools = false) : HttpMessageHandler, IHttpClientFactory
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

            using var requestBody = JsonDocument.Parse(Requests[^1].Body);
            if (requestBody.RootElement.TryGetProperty("stream", out var stream) && stream.GetBoolean())
            {
                var offersTools = requestBody.RootElement.TryGetProperty("tools", out var tools) && tools.GetArrayLength() > 0;
                var callTools = invokeTools && offersTools && (Requests.Count == 1 || repeatTools);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(callTools
                        ? CreateToolStream(path.EndsWith("/responses", StringComparison.Ordinal), Requests.Count)
                        : CreateStream(path.EndsWith("/responses", StringComparison.Ordinal), tokenLimit, partialText),
                        Encoding.UTF8, "text/event-stream")
                };
            }

            static string CreateToolStream(bool openAI, int iteration)
            {
                var events = new StringBuilder();
                void Event(object value)
                {
                    var json = JsonSerializer.Serialize(value);
                    using var document = JsonDocument.Parse(json);
                    events.Append("event: ").Append(document.RootElement.GetProperty("type").GetString())
                        .Append("\ndata: ").Append(json).Append("\n\n");
                }

                if (openAI)
                {
                    Event(new
                    {
                        type = "response.created", sequence_number = 0,
                        response = new { id = "resp_tools", @object = "response", created_at = 1, status = "in_progress", model = "gpt-test", store = false }
                    });
                    var items = new List<object>();
                    foreach (var (plan, index) in new[] { ("Business", 0), ("Enterprise", 1) })
                    {
                        var item = new
                        {
                            type = "function_call", id = $"fc_{iteration}_{index}", call_id = $"call_{iteration}_{index}",
                            name = DemoSupportCatalog.ToolName, arguments = JsonSerializer.Serialize(new { plan }), status = "completed"
                        };
                        Event(new { type = "response.output_item.added", sequence_number = index * 2 + 1, output_index = index, item });
                        Event(new { type = "response.output_item.done", sequence_number = index * 2 + 2, output_index = index, item });
                        items.Add(item);
                    }

                    Event(new
                    {
                        type = "response.completed", sequence_number = 5,
                        response = new
                        {
                            id = "resp_tools", @object = "response", created_at = 1, status = "completed", model = "gpt-test",
                            store = false, output = items,
                            usage = new { input_tokens = 4, output_tokens = 20, total_tokens = 24 }
                        }
                    });
                }
                else
                {
                    Event(new
                    {
                        type = "message_start",
                        message = new
                        {
                            id = "msg_tools", type = "message", model = "claude-test", role = "assistant",
                            content = Array.Empty<object>(), usage = new { input_tokens = 4, output_tokens = 0 }
                        }
                    });
                    foreach (var (plan, index) in new[] { ("Business", 0), ("Enterprise", 1) })
                    {
                        Event(new
                        {
                            type = "content_block_start", index,
                            content_block = new
                            {
                                type = "tool_use", id = $"call_{iteration}_{index}", name = DemoSupportCatalog.ToolName,
                                input = new { }
                            }
                        });
                        Event(new { type = "content_block_delta", index, delta = new { type = "input_json_delta", partial_json = JsonSerializer.Serialize(new { plan }) } });
                        Event(new { type = "content_block_stop", index });
                    }

                    Event(new { type = "message_delta", delta = new { stop_reason = "tool_use", stop_sequence = (string?)null }, usage = new { output_tokens = 20 } });
                    Event(new { type = "message_stop" });
                }

                return events.ToString();
            }

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

    private static string CreateStream(bool openAI, bool tokenLimit, bool partialText)
    {
        static string Event(string json)
        {
            using var document = JsonDocument.Parse(json);
            return $"event: {document.RootElement.GetProperty("type").GetString()}\ndata: {json}\n\n";
        }

        if (openAI)
        {
            var text = tokenLimit && !partialText ? string.Empty : "Test response";
            var status = tokenLimit ? "incomplete" : "completed";
            var usage = tokenLimit ? 900 : 6;
            var output = text.Length == 0
                ? "[]"
                : """[{"type":"message","id":"msg_test","role":"assistant","status":"completed","content":[{"type":"output_text","text":"Test response","annotations":[]}]}]""";
            var events = Event("""{"type":"response.created","sequence_number":0,"response":{"id":"resp_test","object":"response","created_at":1,"status":"in_progress","model":"gpt-test","output":[]}}""");
            if (text.Length > 0)
            {
                events += Event("""{"type":"response.output_item.added","sequence_number":1,"output_index":0,"item":{"type":"message","id":"msg_test","role":"assistant","status":"in_progress","content":[]}}""");
                events += Event("""{"type":"response.content_part.added","sequence_number":2,"item_id":"msg_test","output_index":0,"content_index":0,"part":{"type":"output_text","text":"","annotations":[]}}""");
                events += Event($$"""{"type":"response.output_text.delta","sequence_number":3,"item_id":"msg_test","output_index":0,"content_index":0,"delta":"{{text}}"}""");
                events += Event("""{"type":"response.output_item.done","sequence_number":4,"output_index":0,"item":{"type":"message","id":"msg_test","role":"assistant","status":"completed","content":[{"type":"output_text","text":"Test response","annotations":[]}]}}""");
            }

            events += Event(JsonSerializer.Serialize(new
            {
                type = $"response.{status}",
                sequence_number = 5,
                response = new
                {
                    id = "resp_test", @object = "response", created_at = 1, status, model = "gpt-test",
                    output = JsonSerializer.Deserialize<JsonElement>(output),
                    incomplete_details = tokenLimit ? new { reason = "max_output_tokens" } : null,
                    usage = new
                    {
                        input_tokens = 4, output_tokens = usage, total_tokens = usage + 4,
                        input_tokens_details = new { cached_tokens = 0 },
                        output_tokens_details = new { reasoning_tokens = tokenLimit ? 900 : 0 }
                    }
                }
            }));
            return events;
        }

        var anthropic = Event("""{"type":"message_start","message":{"id":"msg_test","type":"message","model":"claude-test","role":"assistant","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":4,"output_tokens":0}}}""");
        if (!tokenLimit)
        {
            anthropic += Event("""{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""");
            anthropic += Event("""{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Test response"}}""");
            anthropic += Event("""{"type":"content_block_stop","index":0}""");
        }

        anthropic += Event(JsonSerializer.Serialize(new
        {
            type = "message_delta",
            delta = new { stop_reason = tokenLimit ? "max_tokens" : "end_turn", stop_sequence = (string?)null },
            usage = new { output_tokens = tokenLimit ? 900 : 6 }
        }));
        anthropic += Event("""{"type":"message_stop"}""");
        return anthropic;
    }
}
