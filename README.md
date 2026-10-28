# OpenAI + Anthropic Taste Test using Microsoft.Extensions.AI

[![Build and test](https://github.com/Azure-Samples/openai-anthropic-taste-test/actions/workflows/ci.yml/badge.svg)](https://github.com/Azure-Samples/openai-anthropic-taste-test/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Azure Developer CLI](https://img.shields.io/badge/deploy-azd%20up-0078D4)](https://learn.microsoft.com/azure/developer/azure-developer-cli/)

Ask GPT and Claude the same question. Read both answers without knowing which is which. Vote. Then see who wrote what.

Both use `IChatClient`, but each keeps its official SDK and native API. The support-plan example also runs the same C# business tool on both models. Shared OpenTelemetry instrumentation reports the model calls and tool execution.

[![One IChatClient fans out to the OpenAI SDK for .NET over the Responses API and the Anthropic C# SDK over the Messages API, reaching gpt-6.1-sol and claude-sonnet-5-5 on Microsoft Foundry. Get the sample at aka.ms/meai/start.](docs/img/architecture.png)](https://aka.ms/meai/start)

## What you'll see

1. **Ask once.** You type one prompt. Both models answer at the same time.
2. **Stay blind.** The answers appear in two lanes named **A** and **B**. Nothing on the page says which model is which.
3. **Vote, then reveal.** Pick the answer you like. Only then does the app show the provider, model, SDK, and protocol behind each lane.

Try the **support-plan** prompt to see tool calling. Both models look up the same fictional Business and Enterprise plans, then recommend one for 80 seats. Each answer shows how many catalog lookups actually ran. The catalog is read-only and contains no customer data.

Live answers show time to first visible text, total time, and API-reported token counts through the same .NET types. Output token counts can include reasoning, not just the words on screen. Preview token counts are estimates.

On tool turns, the first visible text may be a short preamble before the lookup. Use total time to compare time to a completed recommendation.

![Preview of a support-plan comparison with blind response lanes, catalog lookup counts, latency, and estimated token metrics](docs/img/taste-test.png)

## Try it now, no Azure needed

You only need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), version **10.0.403 or later**.

```powershell
git clone https://github.com/Azure-Samples/openai-anthropic-taste-test
cd openai-anthropic-taste-test
dotnet run --project src/TasteTest --launch-profile sample
```

Open **http://localhost:5050**. Preview mode uses canned answers and estimated token counts. Its support-plan example exercises the local catalog tool through MEAI, but it does not call either vendor's API.

## Deploy the real thing

One command provisions everything and deploys the app:

```powershell
azd auth login
azd up
```

`azd` asks you a few questions (subscription, region, environment name, and your organization name for the Anthropic offer), then prints a URL. Open it and you have a live taste test.

**What you need first**

| | |
|---|---|
| An Azure subscription | Must be allowed to buy Anthropic models from Azure Marketplace |
| Permission to create resources | `Owner`, or `Contributor` + `Role Based Access Control Administrator` |
| Model quota | Enough capacity for one GPT and one Claude deployment |
| Tools | [.NET SDK 10.0.403+](https://dotnet.microsoft.com/download/dotnet/10.0) and [Azure Developer CLI 1.37.1+](https://learn.microsoft.com/azure/developer/azure-developer-cli/install-azd) |

Choose **eastus2** or **swedencentral**. Both default models are generally available there; the pair was provisioned and tested in **eastus2**.

Not every subscription can deploy Claude. CSP, free-trial, student, and some sponsored subscriptions are not eligible. See [Deploy and use Claude models in Microsoft Foundry](https://learn.microsoft.com/azure/foundry/foundry-models/how-to/use-foundry-models-claude).

When you're done, remove everything:

```powershell
azd down --purge --force
```

<details>
<summary><b>If you can't create role assignments</b></summary>

Hosting the app in Azure needs the `Microsoft.Authorization/roleAssignments/write` permission. `Contributor`, `Azure AI Developer`, and `Foundry Owner` do **not** include it. Ask your administrator for `Role Based Access Control Administrator` at subscription scope.

You don't have to wait. Local mode creates only the Foundry account and the two model deployments — no role assignments at all — and runs the app on your own machine under your own identity:

```powershell
azd env set HOSTING_MODE local
azd env set ASSIGN_INFERENCE_ROLE_TO_DEPLOYER false
azd provision
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\scripts\run-local.ps1
```

Use `azd provision`, not `azd up`, because this mode deliberately deploys no Azure hosting. Your account still needs data access to Foundry, which `Azure AI Developer`, `Foundry Owner`, or `Cognitive Services User` provides.

Switch to full Azure hosting later without recreating anything:

```powershell
azd env set HOSTING_MODE containerapp
azd up
```

</details>

<details>
<summary><b>Deploy without any prompts</b></summary>

Once you've signed in once, you can supply every answer up front:

```powershell
azd env new taste-test `
  --subscription <subscription-id> `
  --location eastus2 `
  --no-prompt

azd env set AZURE_TENANT_ID <tenant-id>
azd env set CLAUDE_ORGANIZATION_NAME "<legal-entity>"
azd env set CLAUDE_COUNTRY_CODE US
azd env set CLAUDE_INDUSTRY technology
azd provision --preview --no-prompt
azd up --no-prompt
```

The organization name is legal attestation data for the Anthropic Marketplace offer. It is the only value the template will not guess for you.

</details>

<details>
<summary><b>Let an AI agent set it up for you</b></summary>

This repository ships an [Agent Skill](.github/skills/openai-anthropic-taste-test/SKILL.md) for GitHub Copilot CLI and compatible agents. From this folder, ask:

> Set up and verify the OpenAI + Anthropic taste test with the least manual intervention.

The agent reuses your cached sign-in, picks full Azure hosting or the no-role-assignment local mode based on what you're allowed to do, previews the infrastructure, deploys, tests both models, checks that the page really is blind, and tells you how to clean up.

</details>

## How it works

This is the part worth reading. Two vendors, two protocols, one application loop.

| Lane | Vendor SDK | Foundry route | Protocol |
|---|---|---|---|
| GPT | OpenAI SDK for .NET | `/openai/v1` | Responses API |
| Claude | Anthropic C# SDK | `/anthropic` | Messages API |

Each SDK has its own client construction. Both become an `IChatClient`, with the same tool-calling and telemetry middleware. This is a shortened version; the [factory](src/TasteTest/Services/FoundryModelChatClientFactory.cs) also configures stateless Responses calls and incomplete-stream handling:

```csharp
var credential = new DefaultAzureCredential();

IChatClient gpt = new OpenAIClient(
        new BearerTokenPolicy(credential, "https://ai.azure.com/.default"),
        new OpenAIClientOptions { Endpoint = new($"{endpoint}/openai/v1/") })
    .GetResponsesClient()
    .AsIChatClient(aoaiDeployment)
    .AsBuilder()
    .UseFunctionInvocation(configure: client => client.MaximumIterationsPerRequest = 4)
    .UseOpenTelemetry(sourceName: "TasteTest.Chat",
        configure: telemetry => telemetry.EnableSensitiveData = false)
    .Build();

IChatClient claude = new AnthropicFoundryClient(
        new AnthropicFoundryIdentityTokenCredentials(credential, resourceName))
    .AsIChatClient(claudeDeployment)
    .AsBuilder()
    .UseFunctionInvocation(configure: client => client.MaximumIterationsPerRequest = 4)
    .UseOpenTelemetry(sourceName: "TasteTest.Chat",
        configure: telemetry => telemetry.EnableSensitiveData = false)
    .Build();
```

There are no API keys. `BearerTokenPolicy` and `AnthropicFoundryIdentityTokenCredentials` both take the same Entra `TokenCredential` and refresh tokens on their own.

After that, nothing in the app knows which vendor it is talking to. The comparison loop is plain `Microsoft.Extensions.AI` code — same call, same options, same streaming type, for both lanes:

```csharp
var responses = await Task.WhenAll(lanes.Select(async lane =>
{
    var messages = new List<ChatMessage>(lane.History) { new(ChatRole.User, prompt) };
    var updates = new List<ChatResponseUpdate>();

    await foreach (var update in lane.Client.GetStreamingResponseAsync(
        messages,
        new ChatOptions
        {
            MaxOutputTokens = 4096,
            Reasoning = new() { Effort = ReasoningEffort.Low },
            Tools = [DemoSupportCatalog.CreateTool(onLookup)]
        },
        cancellationToken))
    {
        updates.Add(update);
        lane.Buffer.Append(update.Text);
        await requestThrottledRender();
    }

    return updates.ToChatResponse();
}));
```

Here, `onLookup` is the per-turn callback that records catalog results for the page. The full implementation is in the [session](src/TasteTest/Services/TasteTestSession.cs).

`ToChatResponse()` also folds each provider's usage reporting into one `UsageDetails`, which is why the page can show token counts for both lanes without any provider-specific code.

The [completion adapter](src/TasteTest/Services/OpenAIStreamCompletionClient.cs) preserves finish reason and usage on incomplete OpenAI streams, which MEAI.OpenAI 10.11.0 does not yet convert. OpenAI calls use `store: false` and request encrypted reasoning content, so tool iterations can replay context without relying on a previously stored response ID.

Both lanes request low reasoning effort for short, responsive comparisons. Anthropic 13 maps this shared option to adaptive thinking and `output_config.effort`, rather than a fixed reasoning-token budget. This sample is not a maximum-effort benchmark.

Swapping either lane for a different model is a configuration change, not a rewrite.

### Shared business tool

The [demo catalog](src/TasteTest/Services/DemoSupportCatalog.cs) is a regular C# function exposed with `AIFunctionFactory.Create`. MEAI invokes it and sends the result back through the appropriate SDK. There is no separate OpenAI or Anthropic implementation of the lookup.

| Fictional plan | USD per seat/month | First-response target | Coverage |
|---|---|---|---|
| Business | $40 | 4 hours | 24/7 |
| Enterprise | $75 | 1 hour | 24/7 |

For 80 seats and a two-hour response requirement, Enterprise meets the target at $6,000/month. These are demo values, not commercial prices or guarantees. Tool execution is bounded to four iterations, followed by a final model call without tools. Usage includes all model calls in that answer.

A rejected lookup or tool error fails the turn. That answer cannot win a vote or enter completed conversation history.

### See the traces and metrics

To print telemetry locally:

```powershell
$env:TASTE_TEST_TELEMETRY_CONSOLE = 'true'
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\scripts\run-local.ps1
```

Or send it to an **OTLP HTTP/protobuf** collector:

```powershell
$env:OTEL_EXPORTER_OTLP_ENDPOINT = 'http://localhost:4318'
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\scripts\run-local.ps1
```

Start your collector first. In VS Code with Foundry Toolkit, run **Foundry Toolkit: Open Tracing** (`ai-mlstudio.tracing.open`). A standard OTLP collector works too. Keep tracing views off-screen until after the vote because model names appear in telemetry.

The [telemetry setup](src/TasteTest/Services/TasteTestTelemetry.cs) registers `TasteTest.Chat` as both an activity source and a meter. MEAI records model-call and tool spans, token usage, duration, and streaming timing. SDK time-to-first-chunk is different from the page's time-to-first-visible-text measurement.

Prompt, answer, and tool-argument capture is explicitly disabled. No exporter is enabled unless you set one of the variables above. For Azure hosting, set those values with `azd env set`, run `azd provision --preview`, then `azd up` to reapply settings and deploy the app. Use a collector reachable from the Container App, not your laptop's localhost. Console output goes to the existing application logs. No new Azure monitoring resource is required.

<details>
<summary><b>What this sample does not do</b></summary>

Being precise so nothing here is oversold:

- The only tool is the fictional read-only support catalog. There are no write actions, production business-system connections, or tool approvals.
- It does not request structured output.
- It does not reuse provider-side conversation IDs or cache model answers. Completed history, including tool calls and results, is replayed.
- Canceled, failed, truncated, and empty answers cannot win a vote. Voting stays disabled with an explanation until both answers complete. **Retry comparison** resends the last prompt to both lanes. Earlier attempts remain visible but are excluded from future context.
- Each lane has a 120-second deadline. Cancel stops both lanes; neither an abandoned comparison nor a failed follow-up changes completed conversation history.

</details>

## What `azd up` creates

| Resource | Why |
|---|---|
| Microsoft Foundry account and project | One endpoint for both model families |
| `gpt-6.1-sol` deployment | The OpenAI Responses lane |
| `claude-sonnet-5-5` (version 2, Hosted on Azure) | The Anthropic Messages lane |
| Azure Container App | Runs the Blazor Server app |
| User-assigned managed identity | Lets the app reach Foundry without secrets |
| Azure Container Registry | Stores the image built by `azd deploy` |
| Log Analytics workspace | Collects platform and application logs |

## Run against your deployed models

After `azd provision`, run the app on your machine against the real Foundry endpoint:

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\scripts\run-local.ps1
```

```bash
./scripts/run-local.sh
```

Both open **http://localhost:5050**, load your active `azd` environment, turn off canned answers, and sign in with the Azure credential you already have. A separate `az login` is not required if you have a usable cached credential.

The Windows command allows local scripts for that process only; it does not permanently change your execution policy. Organization-enforced policies still apply.

Before a demo, run a real comparison, vote, and ask a follow-up. The no-Azure preview does **not** verify model access or live inference.

<details>
<summary><b>Use different models</b></summary>

Default models provisioned and verified with real Entra-authenticated API calls on **October 21, 2026**:

| Setting | Default |
|---|---|
| `CLAUDE_MODEL_NAME` | `claude-sonnet-5-5` |
| `CLAUDE_MODEL_VERSION` | `2` (Hosted on Azure) |
| `CLAUDE_MODEL_CAPACITY` | `10` |
| `AOAI_MODEL_NAME` | `gpt-6.1-sol` |
| `AOAI_MODEL_VERSION` | `2026-09-29` |
| `AOAI_MODEL_CAPACITY` | `10` |
| `TASTE_TEST_MAX_OUTPUT_TOKENS` | `4096` |

GPT-6.1 Sol and Claude Sonnet 5.5 both completed provisioning and returned successful text responses through their native APIs. They are the tested defaults, not a claim that every subscription has access to every model.

Capacity is in thousands of tokens per minute: `10` means **10,000 TPM** per deployment. Sonnet also passed at `1` (**1,000 TPM**), but that allocation allowed only **one request per minute**. Use it only for a minimal smoke test; the default leaves room for comparison and follow-up calls. Increase capacity for larger audiences.

Other models remain configurable, but check availability, quota, and Marketplace eligibility before switching. See the [Azure model catalog](https://learn.microsoft.com/azure/foundry/foundry-models/concepts/models-sold-directly-by-azure) and [Claude availability](https://learn.microsoft.com/azure/foundry/foundry-models/concepts/claude-models).

The token budget covers reasoning as well as visible answers. If an existing environment still uses the old 900-token setting, update it and restart the local app:

```powershell
azd env set TASTE_TEST_MAX_OUTPUT_TOKENS 4096
```

For Azure hosting, run `azd provision --preview`, then `azd up` to update the setting and deploy the app.

</details>

<details>
<summary><b>Update an existing environment</b></summary>

Pulling the code does not change saved azd settings or existing Azure deployments. In an eastus2 or swedencentral environment, explicitly select the new models:

```powershell
azd env set AOAI_MODEL_NAME gpt-6.1-sol
azd env set AOAI_MODEL_VERSION 2026-09-29
azd env set AOAI_MODEL_CAPACITY 10
azd env set CLAUDE_MODEL_NAME claude-sonnet-5-5
azd env set CLAUDE_MODEL_VERSION 2
azd env set CLAUDE_MODEL_CAPACITY 10
azd env set TASTE_TEST_MAX_OUTPUT_TOKENS 4096
azd provision --preview
azd provision
```

Then run the local launcher, or `azd deploy` for Azure hosting. Review the preview first: changed model names create new deployments, and incremental provisioning does not remove old ones. If your existing resources are in another region, create a new environment in a supported region rather than moving them by changing a setting.

</details>

<details>
<summary><b>What it costs</b></summary>

You pay for model tokens, Azure Container Registry (Basic), Container Apps compute, and Log Analytics ingestion. The default keeps one replica warm so a live demo never cold-starts.

For non-demo environments, let it scale to zero:

```powershell
azd env set CONTAINER_MIN_REPLICAS 0
azd provision
```

</details>

<details>
<summary><b>Security</b></summary>

- Key-based authentication is disabled on the Foundry account.
- No API keys or connection strings exist in the repo or in app settings.
- The Container App uses a user-assigned managed identity with `Cognitive Services User`.
- That same identity gets only `AcrPull` on the container registry.
- Prompts stay in server-side Blazor circuit state and go straight to the model endpoints.
- Markdown headings, lists, emphasis, and code are rendered safely. Raw HTML is escaped, and model-generated links and images are inert text.

</details>

<details>
<summary><b>Runtime and dependency versions</b></summary>

Stable releases checked on **October 28, 2026**:

| Dependency | Version |
|---|---|
| .NET SDK / ASP.NET Core runtime | `10.0.403` / `10.0.14` |
| Microsoft.Extensions.AI / Microsoft.Extensions.AI.OpenAI | `10.11.0` |
| OpenAI | `2.14.0` |
| Anthropic / Anthropic.Foundry | `13.3.0` / `1.0.0` |
| Azure.Identity | `1.21.0` |
| Azure.Core / System.ClientModel | `1.64.0` / `1.16.0` |
| Markdig | `1.5.1` |
| OpenTelemetry hosting / console / OTLP exporters | `1.20.1` |
| Azure Developer CLI / Azure CLI / Bicep (devcontainer) | `1.37.1` / `2.91.0` / `0.48.1` |

.NET 10 is the latest stable LTS; .NET 11 is still a release candidate. This update includes the 10.0.14 security servicing release. CI reads [global.json](global.json), Docker pins matching SDK/runtime releases, and the devcontainer uses the .NET 10 Ubuntu Noble image.

MEAI.OpenAI 10.11.0 supports OpenAI 2.14.0, so the old OpenAI Dependabot exclusion has been removed. Credential, transport, and test dependencies are also pinned to current stable releases rather than older transitive minimums. Dependabot checks container images and devcontainer dependencies as well as NuGet and SHA-pinned GitHub Actions. CI builds the container in addition to building and testing the application.

If your package mirror lags, use NuGet.org or ask the mirror owner to sync these versions.

</details>

<details>
<summary><b>Troubleshooting</b></summary>

**GPT deployment fails with insufficient quota.** Lower `AOAI_MODEL_CAPACITY`, pick another region, use a smaller model, or request quota in the Azure portal.

**Claude deployment is rejected.** Check subscription Marketplace eligibility, your organization metadata, and that you're using a Hosted on Azure model version.

**One lane returns 403.** Role assignments can take a few minutes to propagate after the first deploy. Confirm the app identity has `Cognitive Services User` on the Foundry account, then restart the Container App revision.

**Blank or cut-off answer; voting disabled.** Check the lane's message. A reasoning model can consume its output budget without producing text. Use the 4096-token default, restart the app after changing settings, and retry. Both lanes must finish before voting; the successful answer isn't itself incomplete when the other lane fails.

**A request seems stuck.** The button changes to **Generating...**, a status message explains the wait, and **Cancel** remains available. A lane that exceeds 120 seconds fails with a retry message instead of waiting indefinitely.

**PowerShell says scripts are disabled.** Use the process-scoped Windows command in "Run against your deployed models." If an organizational policy still blocks execution, ask your administrator for the approved approach.

**Provisioning fails with `Authorization failed ... roleAssignments/write`.** You can create resources but not role assignments. Either ask for `Role Based Access Control Administrator` at subscription scope, or use local mode: `azd env set HOSTING_MODE local`, `azd env set ASSIGN_INFERENCE_ROLE_TO_DEPLOYER false`, then `azd provision` and the Windows launcher above.

**Claude deployment fails with `no valid payment method`.** Marketplace rejected the subscription for that offer. Confirm eligibility with your subscription administrator. Pointing `CLAUDE_MODEL_NAME` at an OpenAI model does not work around this — that lane always deploys an Anthropic model.

**Streaming disconnects after scaling.** This template fixes the Container App at one replica because Blazor Server circuit state lives in memory. Add Azure SignalR Service and distributed state before raising `maxReplicas`.

</details>

## Verify your changes

```powershell
dotnet test OpenAIAnthropicTasteTest.slnx
azd provision --preview
```

The tests cover streaming, cancellation, retries, safe Markdown, catalog lookup, and telemetry privacy. Transport tests drive the actual SDKs through native tool-call streams and verify the tool-result follow-up requests without calling Azure.

## Learn more

- [Claude models in Microsoft Foundry](https://learn.microsoft.com/azure/foundry/foundry-models/concepts/claude-models)
- [Deploy and use Claude models](https://learn.microsoft.com/azure/foundry/foundry-models/how-to/use-foundry-models-claude)
- [OpenAI .NET SDK](https://github.com/openai/openai-dotnet)
- [Anthropic C# SDK](https://github.com/anthropics/anthropic-sdk-csharp)
- [Microsoft.Extensions.AI](https://learn.microsoft.com/dotnet/ai/microsoft-extensions-ai)

## Contributing

Contributions and suggestions are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md).

## License

[MIT](LICENSE)
