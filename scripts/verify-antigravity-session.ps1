param()

$ErrorActionPreference = 'Stop'

Write-Host 'QuotaTray Antigravity session coordinator verifier'
Write-Host 'No real Google or Antigravity credentials are used by this test.'
Write-Host ''

dotnet run --project "$PSScriptRoot\..\tools\QuotaTray.AntigravitySessionVerifier\QuotaTray.AntigravitySessionVerifier.csproj" --configuration Debug
exit $LASTEXITCODE
