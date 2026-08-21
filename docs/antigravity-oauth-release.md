# Antigravity OAuth release configuration

QuotaTray reads the user's existing Antigravity OAuth credential locally, but it must use the matching OAuth client configuration when refreshing an expired access token.

The release build injects that client configuration into `QuotaTray.Core` as assembly metadata. Runtime code does **not** launch or inspect `agy`.

## GitHub repository configuration

Before creating a `v*` release tag, configure these repository settings:

- Repository variable: `ANTIGRAVITY_OAUTH_CLIENT_ID`
- Repository secret: `ANTIGRAVITY_OAUTH_CLIENT_SECRET`

The release workflow fails early when either value is missing.

Do not commit either value to source files. The GitHub Secret keeps the value out of the repository and routine workflow output, but it is **not a confidentiality boundary for a distributed desktop application**: build-time OAuth client metadata can ultimately be recovered from a released binary.

## Runtime behavior

1. The user signs in to Antigravity normally once.
2. QuotaTray reads the existing local Antigravity credential from the OS credential store or the Antigravity token-file fallback.
3. If the access token is expired, QuotaTray refreshes it directly with the OAuth client configuration embedded in the release build.
4. Refreshed tokens remain in QuotaTray memory. QuotaTray does not overwrite Antigravity-owned credentials such as `gemini:antigravity`.

This keeps `agy` out of the runtime dependency chain while preserving Antigravity as the owner of the user's login session.

## Development override

Maintainers can override the build metadata locally with environment variables when needed:

- `ANTIGRAVITY_CLIENT_ID` or `ANTIGRAVITY_OAUTH_CLIENT_ID`
- `ANTIGRAVITY_CLIENT_SECRET` or `ANTIGRAVITY_OAUTH_CLIENT_SECRET`

End users should not need to set these variables.
