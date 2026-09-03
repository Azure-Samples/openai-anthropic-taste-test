---
name: openai-anthropic-taste-test
description: >-
  End-to-end assistant skill for the OpenAI + Anthropic Taste Test on Microsoft
  Foundry. Provisions GPT and Claude with azd, chooses Azure or local hosting
  based on RBAC access, runs the .NET Blazor app, verifies both model lanes,
  diagnoses Marketplace/quota/auth failures, and tears down resources.
  USE FOR: set up the taste test, deploy the taste test, try GPT versus Claude,
  azd up, run locally, roleAssignments/write workaround, Marketplace failures,
  quota errors, model 401/403, demo preparation, and cleanup.
  DO NOT USE FOR: unrelated Foundry agents, fine-tuning, or general Azure
  architecture work.
---

# OpenAI + Anthropic Taste Test

Use this skill from the
[`Azure-Samples/openai-anthropic-taste-test`](https://github.com/Azure-Samples/openai-anthropic-taste-test)
workspace. Read the root `README.md` and `azure.yaml` before changing or deploying anything.

## Goal

Get the customer from a clean machine to a verified blind A/B page with the least interaction
possible:

1. Reuse existing authentication and azd environment state.
2. Select full Azure hosting when RBAC permits it; otherwise select local hosting automatically.
3. Preview infrastructure before deployment.
4. Provision with no API keys.
5. Verify both model endpoints and the browser flow.
6. Report the resource group, application URL or local URL, model IDs, and any remaining manual
   prerequisite.

## Hard rules

- Use Microsoft Entra authentication only. Never request, create, print, or store API keys.
- Run `azd provision --preview` before `azd provision` or `azd up`.
- Never guess `CLAUDE_ORGANIZATION_NAME`; it is customer attestation data. Ask only when it cannot
  be resolved from the active azd environment.
- Do not persist tenant IDs, subscription IDs, tokens, customer names, or deployment logs in tracked
  files. azd environment state belongs under the gitignored `.azure/` folder.
- Reuse cached browser authentication. Run `azd auth login --check-status` first, and invoke browser
  login only when the cache is missing or for the wrong tenant. Do not use device-code login unless
  the customer explicitly requests it.
- Do not weaken the infrastructure to key-based authentication to work around RBAC.
- On failure, clean up partially provisioned resources unless the customer asks to retain them.

## Choose a path

| Condition | Path |
|---|---|
| Customer only wants to see the UI | Sample mode |
| Customer has `Microsoft.Authorization/roleAssignments/write` | Azure-hosted mode |
| Customer lacks role-assignment permission but has Foundry data access | Local-hosted mode |
| Customer explicitly requests one mode | Honor that mode |

### Sample mode — no Azure required

```powershell
dotnet run --project src/TasteTest --launch-profile sample
```

Verify `http://localhost:5050/health`, open `http://localhost:5050`, submit a seed prompt, pick a
winner, and continue the winning conversation.

## PLAN — resolve context once

Resolve these values in order from explicit user input, `azd env get-values`, and the active Azure
context. Ask only for values still missing:

- Environment name
- Subscription ID
- Tenant ID when the subscription is outside the current tenant
- Azure location; default to `eastus2` if the customer has no preference
- Legal organization name for the Anthropic attestation
- Country code; default `US`
- Industry; default `technology`

Use the model defaults in `infra/main.parameters.json` unless the customer asks to change them.

Check authentication:

```powershell
azd auth login --check-status
```

When login is required, use the enterprise browser flow:

```powershell
azd auth login --tenant-id <tenant-id>
```

Create or select an environment:

```powershell
azd env new <environment> `
  --subscription <subscription-id> `
  --location eastus2 `
  --no-prompt

azd env set AZURE_TENANT_ID <tenant-id>
azd env set CLAUDE_ORGANIZATION_NAME "<legal-entity>"
azd env set CLAUDE_COUNTRY_CODE US
azd env set CLAUDE_INDUSTRY technology
```

## DEPLOY — full Azure hosting

Use this path when the principal can create role assignments. It creates the Foundry account and
project, both model deployments, Container Apps, ACR, managed identity, and Log Analytics.

```powershell
azd env set HOSTING_MODE containerapp
azd env set ASSIGN_INFERENCE_ROLE_TO_DEPLOYER true
azd provision --preview --no-prompt
azd up --no-prompt
```

After deployment:

1. Read `SERVICE_WEB_URI` from `azd env get-values`.
2. GET `<SERVICE_WEB_URI>/health` and require HTTP 200.
3. Open `SERVICE_WEB_URI`.
4. Complete the browser verification under [VERIFY](#verify).

## DEPLOY — no-RBAC local hosting

Use this path when the principal cannot perform
`Microsoft.Authorization/roleAssignments/write`. It creates no role assignments and no application
hosting. The signed-in developer must already have data-plane access through `Azure AI Developer`,
`Foundry Owner`, or `Cognitive Services User`.

```powershell
azd env set HOSTING_MODE local
azd env set ASSIGN_INFERENCE_ROLE_TO_DEPLOYER false
azd provision --preview --no-prompt
azd provision --no-prompt
```

The application uses `DefaultAzureCredential`, not the azd credential cache. Before starting it,
ensure a supported developer credential is signed into the same tenant. Prefer browser-based Azure
CLI login when needed:

```powershell
az login --tenant <tenant-id>
./scripts/run-local.ps1
```

On macOS or Linux:

```bash
az login --tenant <tenant-id>
./scripts/run-local.sh
```

Verify `http://localhost:5000/health` or the URL printed by `dotnet run`, then complete the browser
verification below.

## VERIFY — prove the experience

1. Before sending a prompt, inspect the DOM for provider/model strings. It must contain none of:
   `OpenAI`, `Anthropic`, `gpt-`, `claude-`, `Responses API`, or `Messages API`.
2. Select one of the seed prompts.
3. Submit it and confirm both lane A and lane B stream concurrently.
4. Confirm the vote buttons appear only after both lanes finish successfully.
5. Pick either lane.
6. Confirm both identities appear, the selected lane is marked `WINNER`, and only that response
   pane remains.
7. Submit a follow-up and confirm only the winning model responds.
8. Select **New taste test** and confirm both identities disappear from the DOM and lane ordering is
   randomized again.

For a direct Claude smoke test, use the provisioned endpoint and deployment from
`azd env get-values`. Acquire an Entra token for `https://ai.azure.com/.default`, POST a minimal
Messages API request to `<AZURE_FOUNDRY_ENDPOINT>/anthropic/v1/messages`, and require a successful
text response. Never print the token.

## DIAGNOSE

| Error | Meaning | Action |
|---|---|---|
| `roleAssignments/write` authorization failure | Contributor can provision resources but cannot grant managed-identity access | Switch to local hosting, or request `Role Based Access Control Administrator` |
| `no valid payment method` / plan cannot be purchased with a free subscription | The production Marketplace plan is not eligible on that subscription | Use an eligible subscription. Internal test subscriptions may instead expose an already accepted `*-test-plan`. |
| `AnthropicOrganizationCreationException` | Attestation fields are missing or invalid | Set the legal entity, two-letter country, and lowercase industry |
| `InsufficientQuota` | Model capacity is unavailable in the selected region | Lower capacity, select another supported region, or request quota |
| Local app returns 401/403 | `DefaultAzureCredential` resolved a different tenant/account or the user lacks data access | Sign Azure CLI into the target tenant and verify `Azure AI Developer`, `Foundry Owner`, or `Cognitive Services User` |
| Azure-hosted app returns 401/403 | Managed-identity role assignment has not propagated | Verify `Cognitive Services User` on the Foundry account and retry after propagation |
| One lane fails while the other succeeds | Provider-specific quota, deployment, auth, or endpoint problem | Check server logs; the browser intentionally hides provider details before reveal |

## MODIFY

Change models through azd environment values, then re-run preview and provision:

```powershell
azd env set CLAUDE_MODEL_NAME claude-sonnet-5
azd env set CLAUDE_MODEL_VERSION 2
azd env set AOAI_MODEL_NAME gpt-5.4-mini
azd env set AOAI_MODEL_VERSION 2026-03-17
azd provision --preview --no-prompt
azd provision --no-prompt
```

Always verify current model versions, region support, and quota before changing defaults.

## TEARDOWN

Confirm the selected environment and resource group before deleting:

```powershell
azd env get-values
azd down --purge --force --no-prompt
```

After a failed deployment, verify that the resource group and any soft-deleted Cognitive Services
account are gone. Do not delete resources outside the selected azd environment.
