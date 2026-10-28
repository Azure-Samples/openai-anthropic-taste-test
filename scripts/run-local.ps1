[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$variableNames = @(
    'AZURE_FOUNDRY_ENDPOINT',
    'AZURE_FOUNDRY_RESOURCE_NAME',
    'AOAI_DEPLOYMENT_NAME',
    'CLAUDE_DEPLOYMENT_NAME',
    'AZURE_TENANT_ID',
    'TASTE_TEST_MAX_OUTPUT_TOKENS',
    'TASTE_TEST_TELEMETRY_CONSOLE',
    'OTEL_EXPORTER_OTLP_ENDPOINT',
    'TASTE_TEST_USE_SAMPLE_RESPONSES'
)
$previousValues = @{}
foreach ($name in $variableNames) {
    $previousValues[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

Push-Location $root
try {
    $values = azd env get-values
    if ($LASTEXITCODE -ne 0) {
        throw "Could not read the active azd environment. Run 'azd env select' first."
    }

    foreach ($line in $values) {
        if ($line -match '^([A-Za-z_][A-Za-z0-9_]*)="(.*)"$' -and
            $variableNames -contains $Matches[1]) {
            [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2], 'Process')
        }
    }

    $env:TASTE_TEST_USE_SAMPLE_RESPONSES = 'false'
    dotnet run --project ./src/TasteTest --launch-profile http
    $exitCode = $LASTEXITCODE
}
finally {
    foreach ($name in $variableNames) {
        [Environment]::SetEnvironmentVariable($name, $previousValues[$name], 'Process')
    }

    Pop-Location
}

exit $exitCode
