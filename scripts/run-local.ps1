[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$values = azd env get-values
if ($LASTEXITCODE -ne 0) {
    throw "Could not read the active azd environment. Run 'azd env select' first."
}

foreach ($line in $values) {
    if ($line -match '^([A-Za-z_][A-Za-z0-9_]*)="(.*)"$') {
        [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2], "Process")
    }
}

dotnet run --project ./src/TasteTest --no-launch-profile
