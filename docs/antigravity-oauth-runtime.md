# Antigravity OAuth runtime behavior

QuotaTray's requirement is that Antigravity quota continues to work when the `agy` process is not running.

The user must have signed in to Antigravity/agy successfully at least once so a local refresh token exists. After that, QuotaTray does not need to launch or keep `agy` running.

## Runtime flow

1. The user signs in to Antigravity/agy normally once.
2. QuotaTray reads the existing Antigravity credential from Windows Credential Manager or a supported token-file fallback.
3. QuotaTray uses the current access token while it is valid.
4. QuotaTray resolves the public Antigravity installed-app OAuth client metadata automatically. A complete environment override is honored for development, otherwise QuotaTray first checks its own credential cache.
5. If the cache is empty, QuotaTray reads the public installed-app client metadata from immutable, pinned public Antigravity integration references. The client secret is accepted only when its SHA-256 fingerprint matches the expected value.
6. The verified public client metadata is cached under QuotaTray's own credential target (`QuotaTray:antigravity-oauth-client`) so normal future runs do not depend on another fetch.
7. When the access token is rejected, QuotaTray refreshes directly with Google's token endpoint and retries the quota request.

QuotaTray never overwrites the Antigravity-owned `gemini:antigravity` credential. The existing provider's local `agy` discovery remains only as a fallback if canonical bootstrap is unavailable.

## Why the client secret is handled this way

The Antigravity OAuth client is an installed-app/public client. Its client secret ships in public clients and is not a user credential or a confidential server-side secret. However, GitHub secret scanning treats the literal value as a credential, so QuotaTray does not commit it.

Instead, QuotaTray pins public source revisions, verifies the exact client-secret fingerprint, and stores the verified value locally in QuotaTray's own credential store. This avoids manual user configuration while preserving source-repository hygiene.

## User-facing expectation

There are no required OAuth environment variables and no GitHub repository variable/secret setup for end users or release builds.

Expected flow:

`Antigravity login once -> QuotaTray runs -> agy can be closed -> QuotaTray refreshes independently when needed`

A forced diagnostic remains available through:

```powershell
dotnet run --project .\QuotaTray.TestCli -- refresh-test
```

A successful probe must report `Direct OAuth refresh: OK` followed by `REFRESH PROBE SUCCESS` while `agy` is not running.
