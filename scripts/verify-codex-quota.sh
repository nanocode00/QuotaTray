#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

MODE="${1:-fixture}"

case "$MODE" in
  fixture)
    dotnet run --project tools/QuotaTray.CodexVerifier/QuotaTray.CodexVerifier.csproj -c Release
    ;;
  live)
    dotnet run --project tools/QuotaTray.CodexVerifier/QuotaTray.CodexVerifier.csproj -c Release -- --live
    ;;
  *)
    echo "Usage: $0 [fixture|live]"
    exit 2
    ;;
esac
