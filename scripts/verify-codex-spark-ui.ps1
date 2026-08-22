param(
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

Write-Host 'QuotaTray Codex Spark UI verifier'
Write-Host 'Launches the real desktop app in DEBUG mode with a sanitized Spark quota fixture.'
Write-Host 'No Pro account or live Codex quota call is required.'
Write-Host ''

if (-not $NoBuild) {
    Write-Host 'Building QuotaTray.Desktop (Debug)...'
    dotnet build .\QuotaTray.Desktop\QuotaTray.Desktop.csproj -c Debug
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
    Write-Host ''
}

Write-Host 'Launching Spark mock UI...'
Write-Host 'Expected Codex detail view:'
Write-Host '  Codex                  -> 7d, 58% left'
Write-Host '  GPT-5.3-Codex-Spark    -> 5h 80% left / 7d 55% left'
Write-Host '  Plan badge              -> Pro (Mock)'
Write-Host ''
Write-Host 'If the app is in compact mode, switch to detailed mode to inspect both quota groups.'
Write-Host 'Close the launched QuotaTray instance when verification is complete.'
Write-Host ''

dotnet run --project .\QuotaTray.Desktop\QuotaTray.Desktop.csproj -c Debug --no-build -- --mock-codex-spark
exit $LASTEXITCODE
