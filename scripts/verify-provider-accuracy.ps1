param()

$ErrorActionPreference = 'Stop'

Write-Host 'QuotaTray provider accuracy verifier'
Write-Host 'No real provider credentials are used by this test.'
Write-Host ''

dotnet run --project "$PSScriptRoot\..\tools\QuotaTray.ProviderAccuracyVerifier\QuotaTray.ProviderAccuracyVerifier.csproj" --configuration Debug
exit $LASTEXITCODE
