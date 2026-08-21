# Antigravity OAuth runtime behavior

QuotaTray keeps Antigravity quota refresh working even when the `agy` process is not running.

The user must have signed in to Antigravity/agy successfully at least once so a local refresh token exists. After that, QuotaTray does not need to launch or keep `agy` running.

## Runtime flow

1. The user signs in to Antigravity/agy normally once.
2. QuotaTray reads the existing Antigravity credential from Windows Credential Manager or a supported token-file fallback.
3. QuotaTray uses the current access token while it is valid.
4. QuotaTray resolves the canonical public Antigravity installed-app OAuth client metadata automatically. A complete environment override is honored for development, otherwise QuotaTray first checks its own credential cache.
5. If the cache is empty, QuotaTray reads immutable, pinned public Antigravity integration references. A candidate is accepted only when the canonical client ID is present and the client-secret SHA-256 fingerprint matches the expected value.
6. The verified public client metadata is cached under QuotaTray's own credential target (`QuotaTray:antigravity-oauth-client`) so future runs do not depend on another fetch.
7. When the access token expires or is rejected, QuotaTray refreshes directly with Google's token endpoint and retries the quota request.

QuotaTray never overwrites the Antigravity-owned `gemini:antigravity` credential. The existing provider's local `agy` discovery remains only as a fallback if canonical bootstrap is unavailable.

## Why the client secret is handled this way

The Antigravity OAuth client is an installed-app/public client. Its client secret is public client metadata rather than a user credential or confidential server-side secret. GitHub secret scanning still treats the literal value as a credential, so QuotaTray does not commit it.

Instead, QuotaTray pins public source revisions, verifies the exact client-secret fingerprint, and stores the verified value locally in QuotaTray's own credential store. This avoids manual user configuration while preserving source-repository hygiene.

## User-facing expectation

There are no required OAuth environment variables and no GitHub repository variable/secret setup for normal users or release builds.

Expected flow:

`Antigravity login once -> QuotaTray runs -> agy can be closed -> QuotaTray refreshes independently when needed`

## Validation

Validated on Windows with `agy` closed:

```powershell
dotnet run --project .\QuotaTray.TestCli -- refresh-test
dotnet run --project .\QuotaTray.TestCli
```

The forced refresh probe reported `Direct OAuth refresh: OK` and `REFRESH PROBE SUCCESS`, then the normal provider path returned `IsSuccess: True` / `AuthStatus: Connected` with the expected quota groups.
