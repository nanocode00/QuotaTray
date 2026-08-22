param(
    [ValidateSet("fixture", "live")]
    [string]$Mode = "fixture"
)

$ErrorActionPreference = "Stop"

$RootDir = Split-Path -Parent $PSScriptRoot
Push-Location $RootDir

try {
    $Project = "tools/QuotaTray.CodexVerifier/QuotaTray.CodexVerifier.csproj"

    switch ($Mode) {
        "fixture" {
            dotnet run --project $Project -c Release
            if ($LASTEXITCODE -ne 0) {
                exit $LASTEXITCODE
            }
        }
        "live" {
            dotnet run --project $Project -c Release -- --live
            if ($LASTEXITCODE -ne 0) {
                exit $LASTEXITCODE
            }
        }
    }
}
finally {
    Pop-Location
}
