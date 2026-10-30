[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$variableNames = @(
    'TasteTest__Credential__CredentialSource',
    'TasteTest__Credential__TenantId',
    'TasteTest__Credential__Scope',
    'TasteTest__Gpt__Options__Endpoint',
    'TasteTest__Gpt__Model',
    'TasteTest__Claude__Options__Endpoint',
    'TasteTest__Claude__Model',
    'TasteTest__MaxOutputTokens',
    'TasteTest__UseSampleResponses',
    'TASTE_TEST_TELEMETRY_CONSOLE',
    'OTEL_EXPORTER_OTLP_ENDPOINT'
)
$previousValues = @{}
$processOverrides = @($variableNames | Where-Object {
    ($_ -like 'TasteTest__*' -or $_ -in @('TASTE_TEST_TELEMETRY_CONSOLE', 'OTEL_EXPORTER_OTLP_ENDPOINT')) -and
    (Test-Path "Env:$_")
})
foreach ($name in $variableNames) {
    $previousValues[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

Push-Location $root
try {
    $values = azd env get-values
    if ($LASTEXITCODE -ne 0) {
        throw "Could not read the active azd environment. Run 'azd env select' first."
    }

    $settings = @{}
    foreach ($line in $values) {
        if ($line -match '^([A-Za-z_][A-Za-z0-9_]*)="(.*)"$') {
            $name = $Matches[1]
            $settings[$name] = $Matches[2]
            if ($name -like 'TasteTest__*' -and $variableNames -notcontains $name) {
                $variableNames += $name
                $previousValues[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
                if (Test-Path "Env:$name") { $processOverrides += $name }
            }
        }
    }
    $mapped = @{}
    $outputs = @{
        'GPT_ENDPOINT' = 'TasteTest__Gpt__Options__Endpoint'
        'CLAUDE_ENDPOINT' = 'TasteTest__Claude__Options__Endpoint'
        'AOAI_DEPLOYMENT_NAME' = 'TasteTest__Gpt__Model'
        'CLAUDE_DEPLOYMENT_NAME' = 'TasteTest__Claude__Model'
        'AZURE_TENANT_ID' = 'TasteTest__Credential__TenantId'
        'TASTE_TEST_MAX_OUTPUT_TOKENS' = 'TasteTest__MaxOutputTokens'
    }
    foreach ($output in $outputs.GetEnumerator()) {
        if ($settings.ContainsKey($output.Key)) { $mapped[$output.Value] = $settings[$output.Key] }
    }
    # Older environments store one account URL; translate it only at this configuration boundary.
    if ($settings['AZURE_FOUNDRY_ENDPOINT']) {
        $endpoint = $settings['AZURE_FOUNDRY_ENDPOINT'].TrimEnd('/')
        if (-not $mapped.ContainsKey('TasteTest__Gpt__Options__Endpoint')) {
            $mapped['TasteTest__Gpt__Options__Endpoint'] = "$endpoint/openai/v1/"
        }
        if (-not $mapped.ContainsKey('TasteTest__Claude__Options__Endpoint')) {
            $mapped['TasteTest__Claude__Options__Endpoint'] = "$endpoint/anthropic/"
        }
    }
    foreach ($name in $variableNames) {
        if ($settings.ContainsKey($name)) { $mapped[$name] = $settings[$name] }
    }
    foreach ($setting in $mapped.GetEnumerator()) {
        if ($processOverrides -notcontains $setting.Key) {
            [Environment]::SetEnvironmentVariable($setting.Key, $setting.Value, 'Process')
        }
    }

    $env:TasteTest__UseSampleResponses = 'false'
    dotnet run --project ./src/TasteTest --launch-profile http
    $exitCode = $LASTEXITCODE
}
finally {
    foreach ($name in $variableNames) {
        if ($null -eq $previousValues[$name]) {
            if (Test-Path "Env:$name") { Remove-Item "Env:$name" }
        }
        else {
            [Environment]::SetEnvironmentVariable($name, $previousValues[$name], 'Process')
        }
    }

    Pop-Location
}

exit $exitCode
