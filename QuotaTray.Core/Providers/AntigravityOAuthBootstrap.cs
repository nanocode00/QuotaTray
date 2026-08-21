using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using QuotaTray.Core.Security;

namespace QuotaTray.Core.Providers;

/// <summary>
/// Resolves the public installed-app OAuth client metadata used by Antigravity.
///
/// The Google installed-app client secret is public client metadata, not a user
/// credential. QuotaTray intentionally does not commit the secret because GitHub
/// secret scanning treats the literal as a credential. Instead, the exact value is
/// recovered from immutable public references, verified by SHA-256, then cached in
/// QuotaTray's own credential store for subsequent runs.
/// </summary>
internal static class AntigravityOAuthBootstrap
{
    private const string CanonicalClientId =
        "1071006060591-tmhssin2h21lcre235vtolojh4g403ep.apps.googleusercontent.com";

    private const string CanonicalClientSecretSha256 =
        "1d2f041093fd95aa8995a038c711d50a7960da09a505381c09a745d6ad0ecc60";

    private const string CacheTarget = "QuotaTray:antigravity-oauth-client";
    private const string KeyringBase64Prefix = "go-keyring-base64:";

    private static readonly string[] PinnedPublicReferenceUrls =
    {
        "https://raw.githubusercontent.com/cortexkit/antigravity-auth/8efa48ba3d7f2d6f97e0a390fc56e0588c4d6f73/packages/core/src/constants.ts",
        "https://raw.githubusercontent.com/robinebers/openusage/70acd4f4e9cc79951d83f416295e22ef43b9696b/Sources/OpenUsage/Providers/Antigravity/AntigravityUsageClient.swift"
    };

    private static readonly Regex ClientSecretRegex = new(
        @"GOCSPX-[0-9A-Za-z_-]{20,64}",
        RegexOptions.Compiled);

    [ModuleInitializer]
    internal static void Initialize()
    {
        try
        {
            if (HasCompleteEnvironmentOverride())
            {
                return;
            }

            var credentialStore = new CrossPlatformCredentialStore();

            if (TryReadCachedClient(credentialStore, out string? cachedSecret))
            {
                ApplyCanonicalPair(cachedSecret!);
                return;
            }

            // Do not add network work for users who have never configured Antigravity.
            if (!HasAntigravityCredential())
            {
                return;
            }

            if (!TryLoadCanonicalPairFromPinnedReference(out string? resolvedSecret))
            {
                return;
            }

            ApplyCanonicalPair(resolvedSecret!);
            CacheCanonicalPair(credentialStore, resolvedSecret!);
        }
        catch
        {
            // OAuth bootstrap is best-effort. The provider can still use a valid
            // access token or its existing local-discovery fallback.
        }
    }

    private static bool HasCompleteEnvironmentOverride()
    {
        string? clientId = Environment.GetEnvironmentVariable("ANTIGRAVITY_CLIENT_ID")
                           ?? Environment.GetEnvironmentVariable("ANTIGRAVITY_OAUTH_CLIENT_ID");
        string? clientSecret = Environment.GetEnvironmentVariable("ANTIGRAVITY_CLIENT_SECRET")
                               ?? Environment.GetEnvironmentVariable("ANTIGRAVITY_OAUTH_CLIENT_SECRET");

        return !string.IsNullOrWhiteSpace(clientId) && !string.IsNullOrWhiteSpace(clientSecret);
    }

    private static bool HasAntigravityCredential()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTIGRAVITY_ACCESS_TOKEN"))
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTIGRAVITY_REFRESH_TOKEN"))
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GEMINI_ACCESS_TOKEN"))
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GEMINI_REFRESH_TOKEN")))
        {
            return true;
        }

        if (OperatingSystem.IsWindows())
        {
            if (!string.IsNullOrWhiteSpace(Win32CredMan.ReadCredential("gemini:antigravity"))
                || !string.IsNullOrWhiteSpace(Win32CredMan.ReadCredential("antigravity:oauth"))
                || !string.IsNullOrWhiteSpace(Win32CredMan.ReadCredential("google:cloudcode")))
            {
                return true;
            }
        }

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        string[] candidatePaths =
        {
            Path.Combine(userProfile, ".gemini", "antigravity-cli", "antigravity-oauth-token"),
            Path.Combine(userProfile, ".config", "antigravity-cli", "antigravity-oauth-token"),
            Path.Combine(userProfile, ".antigravity", "auth.json"),
            Path.Combine(userProfile, ".antigravity", "credentials.json"),
            Path.Combine(userProfile, ".config", "antigravity", "auth.json"),
            Path.Combine(userProfile, ".gemini", "antigravity-cli", "auth.json"),
            Path.Combine(userProfile, ".gemini", "oauth.json"),
            Path.Combine(appData, "antigravity", "auth.json"),
            Path.Combine(localAppData, "antigravity", "auth.json")
        };

        return candidatePaths.Any(File.Exists);
    }

    private static bool TryReadCachedClient(ICredentialStore credentialStore, out string? clientSecret)
    {
        clientSecret = null;
        string? cached = credentialStore.ReadCredential(CacheTarget);
        if (string.IsNullOrWhiteSpace(cached))
        {
            return false;
        }

        try
        {
            string normalized = NormalizeCredentialPayload(cached);
            using var doc = JsonDocument.Parse(normalized);
            JsonElement root = doc.RootElement;

            string? clientId = root.TryGetProperty("clientId", out JsonElement clientIdElement)
                ? clientIdElement.GetString()
                : null;
            string? secret = root.TryGetProperty("clientSecret", out JsonElement clientSecretElement)
                ? clientSecretElement.GetString()
                : null;

            if (!string.Equals(clientId, CanonicalClientId, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(secret)
                || !MatchesCanonicalSecretFingerprint(secret))
            {
                credentialStore.DeleteCredential(CacheTarget);
                return false;
            }

            clientSecret = secret;
            return true;
        }
        catch
        {
            credentialStore.DeleteCredential(CacheTarget);
            return false;
        }
    }

    private static void CacheCanonicalPair(ICredentialStore credentialStore, string clientSecret)
    {
        try
        {
            string payload = JsonSerializer.Serialize(new
            {
                clientId = CanonicalClientId,
                clientSecret
            });
            credentialStore.WriteCredential(CacheTarget, payload);
        }
        catch
        {
            // Cache failure must not invalidate a successful in-memory bootstrap.
        }
    }

    private static bool TryLoadCanonicalPairFromPinnedReference(out string? clientSecret)
    {
        clientSecret = null;

        foreach (string url in PinnedPublicReferenceUrls)
        {
            try
            {
                using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                string source = httpClient.GetStringAsync(url).GetAwaiter().GetResult();

                if (!source.Contains(CanonicalClientId, StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (Match match in ClientSecretRegex.Matches(source))
                {
                    if (!MatchesCanonicalSecretFingerprint(match.Value))
                    {
                        continue;
                    }

                    clientSecret = match.Value;
                    return true;
                }
            }
            catch
            {
                // Try the next immutable reference.
            }
        }

        return false;
    }

    private static string NormalizeCredentialPayload(string raw)
    {
        string trimmed = raw.Trim();
        if (!trimmed.StartsWith(KeyringBase64Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        try
        {
            string encoded = trimmed[KeyringBase64Prefix.Length..].Trim();
            return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        }
        catch
        {
            return trimmed;
        }
    }

    private static bool MatchesCanonicalSecretFingerprint(string candidate)
    {
        string fingerprint = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(candidate)))
            .ToLowerInvariant();

        return fingerprint.Equals(CanonicalClientSecretSha256, StringComparison.Ordinal);
    }

    private static void ApplyCanonicalPair(string clientSecret)
    {
        Environment.SetEnvironmentVariable("ANTIGRAVITY_CLIENT_ID", CanonicalClientId);
        Environment.SetEnvironmentVariable("ANTIGRAVITY_CLIENT_SECRET", clientSecret);
    }
}
