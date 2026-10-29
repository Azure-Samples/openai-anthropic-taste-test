# Draft upstream issue: config-driven credentials and keyed MEAI registration

Draft only; not submitted to `anthropics/anthropic-sdk-csharp`.

## Suggested title

Document Entra token-provider integration and add keyed, config-driven MEAI registration

## Context

The standard Anthropic 13.3.0 package already supports `IAccessTokenProvider` through
`ClientOptions.Credentials`, including token caching, refresh and cancellation. Its MEAI adapter
also ships in the core package. We use both successfully without a cloud-specific SDK dependency.

The remaining sample code is a small adapter from Azure.Identity's `TokenCredential` to
`IAccessTokenProvider`, plus configuration and keyed DI registration. We did not need an
authentication `DelegatingHandler`; this is not a request for a callback that already exists.

## Request

- Document the existing callback as the supported way to use an external bearer-token provider,
  including a `TokenCredential` example that returns the token's expiry and honors cancellation.
- Consider a provider-neutral callback helper or an optional `TokenCredential` adapter so
  applications can delete their local bridge without adding a host-specific client dependency.
- Add keyed, configuration-driven client/MEAI registration alongside the native client.
  Configuration should select the model, complete endpoint and either an API key or a credential.
- Document ownership: the SDK owns/disposes the callback adapter, but a DI-owned external
  credential must not be disposed by that adapter.
- Clarify which authentication headers the callback sends, including the OAuth beta header,
  for compatible API endpoints.

## Expected application shape

```csharp
builder.AddKeyedAnthropicChatClient("claude", "TasteTest:Claude")
    .UseFunctionInvocation()
    .UseOpenTelemetry(sourceName: "TasteTest.Chat");
```

Changing the configured credential from managed identity to CLI should not change application
code. Using the public API with an API key should not require different registration helpers.

## Validation in this sample

- Standard `AnthropicClient`, explicit `/anthropic/` base URL and its native MEAI adapter.
- Specific Azure.Identity credential through the SDK's `IAccessTokenProvider` callback.
- SDK-managed token reuse and mandatory refresh tested with a fake short-lived credential.
- Live streamed tool invocation and model/tool OpenTelemetry spans passed with cached
  Azure Developer CLI authentication.

This follow-up is not blocking the sample. The local adapter and registration helpers can be
removed when equivalent supported SDK integration is available.
