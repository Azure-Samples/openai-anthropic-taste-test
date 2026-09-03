# Contributing

This project welcomes contributions and suggestions. Most contributions require you to agree to a Contributor License Agreement declaring that you have the right to, and do, grant us the rights to use your contribution. See <https://cla.opensource.microsoft.com>.

This project follows the [Microsoft Open Source Code of Conduct](https://opensource.microsoft.com/codeofconduct/).

## Before opening a pull request

1. Keep the change focused and explain its user-visible effect.
2. Never commit API keys, access tokens, subscription IDs, tenant IDs, or `.azure/` state.
3. Keep the two model lanes behaviorally symmetric before reveal.
4. If model defaults change, update Bicep, README configuration tables, and the architecture source and PNG together.
5. Run:

   ```powershell
   dotnet test OpenAIAnthropicTasteTest.slnx
   azd provision --preview
   ```

6. For UI changes, use sample mode and inspect the pre-reveal DOM for provider or model identity leaks.

## Local sample mode

```powershell
dotnet run --project src/TasteTest --launch-profile sample
```

## Trademark notice

This project may contain trademarks or logos for products and services. Use of Microsoft trademarks is subject to [Microsoft's Trademark & Brand Guidelines](https://www.microsoft.com/legal/intellectualproperty/trademarks/usage/general). Third-party marks remain subject to their owners' policies.
