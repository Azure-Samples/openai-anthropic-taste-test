# Contributing

This project welcomes contributions and suggestions. Most contributions require you to agree to a Contributor License Agreement declaring that you have the right to, and do, grant us the rights to use your contribution. See <https://cla.opensource.microsoft.com>.

This project follows the [Microsoft Open Source Code of Conduct](https://opensource.microsoft.com/codeofconduct/).

## Before opening a pull request

1. Keep the change focused and explain its user-visible effect.
2. Never commit API keys, access tokens, subscription IDs, tenant IDs, or `.azure/` state.
3. Keep the two model lanes behaviorally symmetric before reveal.
4. If model defaults change, update Bicep, README configuration tables, and the architecture source and PNG together.
   Keep SDK setup examples explicitly typed. Select a specific credential through configuration;
   do not add a default credential chain or a host-specific AI client package.
5. Run:

   ```powershell
   dotnet test OpenAIAnthropicTasteTest.slnx
   azd provision --preview
   ```

6. For UI changes, use sample mode and inspect the pre-reveal DOM for provider or model identity leaks.

For launcher changes, run the mocked PowerShell and Bash regression checks with PowerShell 7
and Bash (Git Bash on Windows):

```powershell
pwsh -NoProfile -File .\tests\Scripts\RunLocal.Tests.ps1
```

For infrastructure changes, compile and test the authentication policy without deploying:

```powershell
az bicep build --file .\infra\main.bicep --outfile "$env:TEMP\taste-test-arm.json"
pwsh -NoProfile -File .\tests\Scripts\Infrastructure.Tests.ps1 -TemplatePath "$env:TEMP\taste-test-arm.json"
```

Use Bicep 0.48.1 or later. CI runs both checks. The hosting test verifies required Entra
sign-in, the single-tenant issuer and audience, the health-probe exception, and managed-identity
federation without client secrets. It does not replace testing browser sign-in after deployment.

Client-registration changes must keep both providers keyed and configuration-driven. OpenAI
settings bind through the official SDK registration API. The README setup is extracted from a
compiled region in `Program.cs` and compared by a test. Keep endpoint/authentication behavior
covered by `ChatClientRegistrationTests`; run `Category=Live` only with explicitly configured
endpoints and CLI credentials.

## Local sample mode

```powershell
dotnet run --project src/TasteTest --launch-profile sample
```

## Trademark notice

This project may contain trademarks or logos for products and services. Use of Microsoft trademarks is subject to [Microsoft's Trademark & Brand Guidelines](https://www.microsoft.com/legal/intellectualproperty/trademarks/usage/general). Third-party marks remain subject to their owners' policies.
