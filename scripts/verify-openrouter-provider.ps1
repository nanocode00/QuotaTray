param()

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'tools\QuotaTray.OpenRouterVerifier\QuotaTray.OpenRouterVerifier.csproj'

Write-Host 'QuotaTray OpenRouter provider fixture verifier'
Write-Host 'No real OpenRouter API key is used by this test.'
Write-Host ''

dotnet run --project $project --configuration Debug
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
