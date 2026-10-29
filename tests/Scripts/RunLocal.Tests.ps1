[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$fixture = Join-Path ([IO.Path]::GetTempPath()) "taste-test-launchers-$([Guid]::NewGuid().ToString('N'))"
$null = New-Item -ItemType Directory -Path $fixture
$encoding = [Text.UTF8Encoding]::new($false)
$probe = Join-Path $fixture 'probe.ps1'
$azd = Join-Path $fixture 'azd'
$dotnet = Join-Path $fixture 'dotnet'
$names = @(
    'TASTE_TEST_TELEMETRY_CONSOLE', 'OTEL_EXPORTER_OTLP_ENDPOINT',
    'TASTE_TEST_MAX_OUTPUT_TOKENS', 'TASTE_TEST_USE_SAMPLE_RESPONSES',
    'MOCK_CONSOLE', 'MOCK_ENDPOINT', 'MOCK_BIN', 'MOCK_LAUNCHER'
)
$previous = @{}
foreach ($name in $names) {
    $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

function Assert-Capture($output, $name, $expected) {
    $line = @($output | Where-Object { "$_".StartsWith("${name}=") })
    if ($line.Count -ne 1 -or $line[0] -cne "${name}=${expected}") {
        throw "Expected ${name}=${expected}; got $($line -join ', ')."
    }
}

function ConvertTo-BashPath([string] $path) {
    $normalized = $path.Replace('\', '/')
    if ($env:OS -eq 'Windows_NT') {
        return '/' + $normalized.Substring(0, 1).ToLowerInvariant() + $normalized.Substring(2)
    }
    return $normalized
}

function Set-ProcessVariable([string] $name, $value) {
    if ($null -eq $value) {
        if (Test-Path "Env:$name") { Remove-Item "Env:$name" }
    } else {
        Set-Item "Env:$name" $value
    }
}

try {
    [IO.File]::WriteAllText($probe, @'
param([string] $Launcher)
$ErrorActionPreference = 'Stop'
function azd {
    'TASTE_TEST_TELEMETRY_CONSOLE="' + $env:MOCK_CONSOLE + '"'
    'OTEL_EXPORTER_OTLP_ENDPOINT="' + $env:MOCK_ENDPOINT + '"'
    'TASTE_TEST_MAX_OUTPUT_TOKENS="8192"'
    $global:LASTEXITCODE = 0
}
function dotnet {
    "CONSOLE=$env:TASTE_TEST_TELEMETRY_CONSOLE"
    "ENDPOINT=$env:OTEL_EXPORTER_OTLP_ENDPOINT"
    "TOKENS=$env:TASTE_TEST_MAX_OUTPUT_TOKENS"
    "SAMPLE=$env:TASTE_TEST_USE_SAMPLE_RESPONSES"
    $global:LASTEXITCODE = 0
}
& $Launcher
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
"RESTORED_CONSOLE=$env:TASTE_TEST_TELEMETRY_CONSOLE"
"RESTORED_ENDPOINT=$env:OTEL_EXPORTER_OTLP_ENDPOINT"
"RESTORED_CONSOLE_SET=$(Test-Path Env:TASTE_TEST_TELEMETRY_CONSOLE)"
"RESTORED_ENDPOINT_SET=$(Test-Path Env:OTEL_EXPORTER_OTLP_ENDPOINT)"
'@, $encoding)

    [IO.File]::WriteAllText($azd, @'
#!/usr/bin/env bash
printf 'TASTE_TEST_TELEMETRY_CONSOLE="%s"\n' "$MOCK_CONSOLE"
printf 'OTEL_EXPORTER_OTLP_ENDPOINT="%s"\n' "$MOCK_ENDPOINT"
printf 'TASTE_TEST_MAX_OUTPUT_TOKENS="8192"\n'
'@.Replace("`r`n", "`n"), $encoding)

    [IO.File]::WriteAllText($dotnet, @'
#!/usr/bin/env bash
printf 'CONSOLE=%s\nENDPOINT=%s\nTOKENS=%s\nSAMPLE=%s\n' \
  "${TASTE_TEST_TELEMETRY_CONSOLE-}" "${OTEL_EXPORTER_OTLP_ENDPOINT-}" \
  "$TASTE_TEST_MAX_OUTPUT_TOKENS" "$TASTE_TEST_USE_SAMPLE_RESPONSES"
'@.Replace("`r`n", "`n"), $encoding)

    $powershell = (Get-Command pwsh -ErrorAction Stop).Source
    $bash = if ($env:OS -eq 'Windows_NT') {
        Join-Path (Split-Path -Parent (Split-Path -Parent (Get-Command git).Source)) 'bin\bash.exe'
    } else {
        (Get-Command bash -ErrorAction Stop).Source
    }
    $launcher = Join-Path (Join-Path $root 'scripts') 'run-local.ps1'
    $env:MOCK_BIN = ConvertTo-BashPath $fixture
    $env:MOCK_LAUNCHER = ConvertTo-BashPath (Join-Path (Join-Path $root 'scripts') 'run-local.sh')
    $cases = @(
        @{ Name = 'Saved settings'; Console = $null; Endpoint = $null; SavedConsole = 'true'; SavedEndpoint = 'http://saved:4318'; ExpectedConsole = 'true'; ExpectedEndpoint = 'http://saved:4318' },
        @{ Name = 'Process overrides'; Console = 'true'; Endpoint = 'http://localhost:4318'; SavedConsole = 'false'; SavedEndpoint = ''; ExpectedConsole = 'true'; ExpectedEndpoint = 'http://localhost:4318' },
        @{ Name = 'Explicit false'; Console = 'false'; Endpoint = $null; SavedConsole = 'true'; SavedEndpoint = 'http://saved:4318'; ExpectedConsole = 'false'; ExpectedEndpoint = 'http://saved:4318' },
        @{ Name = 'Explicit empty endpoint'; Console = $null; Endpoint = ''; SavedConsole = 'false'; SavedEndpoint = 'http://saved:4318'; ExpectedConsole = 'false'; ExpectedEndpoint = '' }
    )

    foreach ($case in $cases) {
        Set-ProcessVariable 'TASTE_TEST_TELEMETRY_CONSOLE' $case.Console
        Set-ProcessVariable 'OTEL_EXPORTER_OTLP_ENDPOINT' $case.Endpoint
        $env:TASTE_TEST_MAX_OUTPUT_TOKENS = '512'
        $env:TASTE_TEST_USE_SAMPLE_RESPONSES = 'true'
        $env:MOCK_CONSOLE = $case.SavedConsole
        $env:MOCK_ENDPOINT = $case.SavedEndpoint

        $output = @(& $powershell -NoProfile -ExecutionPolicy RemoteSigned -File $probe -Launcher $launcher)
        if ($LASTEXITCODE -ne 0) { throw "PowerShell launcher failed: $($case.Name)." }
        Assert-Capture $output 'CONSOLE' $case.ExpectedConsole
        Assert-Capture $output 'ENDPOINT' $case.ExpectedEndpoint
        Assert-Capture $output 'TOKENS' '8192'
        Assert-Capture $output 'SAMPLE' 'false'
        Assert-Capture $output 'RESTORED_CONSOLE' $case.Console
        Assert-Capture $output 'RESTORED_ENDPOINT' $case.Endpoint
        Assert-Capture $output 'RESTORED_CONSOLE_SET' ($null -ne $case.Console)
        Assert-Capture $output 'RESTORED_ENDPOINT_SET' ($null -ne $case.Endpoint)

        $output = @(& $bash -c 'chmod +x "$MOCK_BIN/azd" "$MOCK_BIN/dotnet" && export PATH="$MOCK_BIN:$PATH" && exec bash "$MOCK_LAUNCHER"')
        if ($LASTEXITCODE -ne 0) { throw "Bash launcher failed: $($case.Name)." }
        Assert-Capture $output 'CONSOLE' $case.ExpectedConsole
        Assert-Capture $output 'ENDPOINT' $case.ExpectedEndpoint
        Assert-Capture $output 'TOKENS' '8192'
        Assert-Capture $output 'SAMPLE' 'false'
        Write-Output "PASS: $($case.Name), PowerShell and Bash"
    }
}
finally {
    foreach ($name in $names) {
        Set-ProcessVariable $name $previous[$name]
    }
    foreach ($path in @($probe, $azd, $dotnet)) {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
    }
    Remove-Item -LiteralPath $fixture
}
