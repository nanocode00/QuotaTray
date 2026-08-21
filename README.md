# QuotaTray

A lightweight tray application for monitoring AI coding assistant quotas and rate limits.

## Antigravity

Antigravity requires one successful sign-in through Antigravity/agy so a local OAuth session exists. After that, QuotaTray can refresh the Antigravity session itself and continue showing quota while the `agy` process is not running.

No Antigravity OAuth environment variables are required for normal users. QuotaTray resolves the public installed-app OAuth client metadata automatically, verifies it against a pinned SHA-256 fingerprint, and caches the verified metadata in QuotaTray's own credential store. QuotaTray does not overwrite the Antigravity-owned `gemini:antigravity` credential.

For troubleshooting, the direct refresh path can be tested with:

```powershell
dotnet run --project .\QuotaTray.TestCli -- refresh-test
```

See `docs/antigravity-oauth-runtime.md` for the detailed runtime flow.
