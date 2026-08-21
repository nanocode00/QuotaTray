# Antigravity OAuth runtime behavior

QuotaTray's requirement is that Antigravity quota continues to work when the `agy` process is not running.

QuotaTray may read the installed `agy` executable as a local source of the matching public OAuth client configuration. It does not launch `agy` and it does not require an `agy` background process.

## Runtime flow

1. The user signs in to Antigravity/agy normally once.
2. QuotaTray reads the existing Antigravity credential from Windows Credential Manager or a supported token-file fallback.
3. QuotaTray uses the current access token while it is valid.
4. If the access token is rejected, QuotaTray reads the matching OAuth client configuration from the installed `agy` binary and refreshes the session directly with Google's token endpoint.
5. The refreshed token is kept in QuotaTray memory. QuotaTray does not overwrite the Antigravity-owned `gemini:antigravity` credential.

The installed `agy` file may therefore be read when a refresh is needed, but `agy` itself does not need to be running.

## User-facing expectation

There are no required OAuth environment variables and no GitHub repository variable/secret setup for end users or release builds.

Expected flow:

`Antigravity login once -> QuotaTray runs -> agy can be closed -> QuotaTray refreshes independently when needed`

If no compatible local `agy` installation can be found, QuotaTray should report that the OAuth client configuration could not be discovered rather than asking the user to manually enter client metadata.
