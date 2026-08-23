using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QuotaTray.Core.Security;

namespace QuotaTray.Core.Providers;

/// <summary>
/// Coordinates Antigravity OAuth ownership without trying to detect where agy is running.
/// Stored credentials remain owned by agy. When they change, QuotaTray adopts them.
/// When they do not change, QuotaTray keeps using its own in-memory refreshed access token
/// instead of falling back to a stale stored access token on every polling cycle.
/// </summary>
public sealed class AntigravitySessionCoordinatorHandler : HttpMessageHandler
{
    private const string GoogleTokenHost = "oauth2.googleapis.com";
    private const string KeyringBase64Prefix = "go-keyring-base64:";

    private readonly HttpMessageHandler _innerHandler;
    private readonly HttpMessageInvoker _invoker;
    private readonly Func<AntigravityCredentialSnapshot> _credentialReader;
    private readonly TimeSpan _externalRefreshGracePeriod;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _stateLock = new();

    private bool _hasObservedStoredCredential;
    private string? _lastObservedStoredAccessToken;
    private string? _lastObservedStoredRefreshToken;
    private string? _preferredAccessToken;
    private string? _preferredRefreshToken;
    private string? _lastRejectedAccessToken;

    public AntigravitySessionCoordinatorHandler(
        HttpMessageHandler? innerHandler = null,
        Func<AntigravityCredentialSnapshot>? credentialReader = null,
        TimeSpan? externalRefreshGracePeriod = null)
    {
        _innerHandler = innerHandler ?? new HttpClientHandler();
        _invoker = new HttpMessageInvoker(_innerHandler, disposeHandler: false);
        _credentialReader = credentialReader ?? AntigravityCredentialSnapshot.ReadCurrent;
        _externalRefreshGracePeriod = externalRefreshGracePeriod ?? TimeSpan.FromMilliseconds(750);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (IsGoogleRefreshRequest(request))
        {
            return await HandleRefreshRequestAsync(request, cancellationToken);
        }

        if (IsAntigravityApiRequest(request))
        {
            SynchronizeExternalCredential();
            string? tokenUsed = RewriteStoredTokenToPreferredToken(request);
            HttpResponseMessage response = await _invoker.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.Unauthorized && !string.IsNullOrWhiteSpace(tokenUsed))
            {
                lock (_stateLock)
                {
                    if (string.Equals(tokenUsed, _preferredAccessToken, StringComparison.Ordinal))
                    {
                        _lastRejectedAccessToken = tokenUsed;
                    }
                }
            }

            return response;
        }

        return await _invoker.SendAsync(request, cancellationToken);
    }

    private async Task<HttpResponseMessage> HandleRefreshRequestAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            string? rejectedToken;
            lock (_stateLock)
            {
                rejectedToken = _lastRejectedAccessToken;
            }

            if (TryAdoptFreshExternalCredential(rejectedToken, out string? externalAccessToken))
            {
                return CreateSyntheticRefreshResponse(externalAccessToken!);
            }

            if (_externalRefreshGracePeriod > TimeSpan.Zero)
            {
                await Task.Delay(_externalRefreshGracePeriod, cancellationToken);
                if (TryAdoptFreshExternalCredential(rejectedToken, out externalAccessToken))
                {
                    return CreateSyntheticRefreshResponse(externalAccessToken!);
                }
            }

            await RewriteRefreshTokenIfNeededAsync(request, cancellationToken);
            HttpResponseMessage response = await _invoker.SendAsync(request, cancellationToken);

            if (response.IsSuccessStatusCode && response.Content != null)
            {
                await response.Content.LoadIntoBufferAsync(cancellationToken);
                string body = await response.Content.ReadAsStringAsync(cancellationToken);
                CacheSuccessfulRefresh(body);
            }

            return response;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private string? RewriteStoredTokenToPreferredToken(HttpRequestMessage request)
    {
        string? outgoingToken = request.Headers.Authorization?.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) == true
            ? request.Headers.Authorization.Parameter
            : null;

        if (string.IsNullOrWhiteSpace(outgoingToken))
        {
            return null;
        }

        string? preferred;
        string? stored;
        lock (_stateLock)
        {
            preferred = _preferredAccessToken;
            stored = _lastObservedStoredAccessToken;
        }

        // Do not touch explicitly supplied custom tokens. Only replace the token when the
        // provider is using the same token that was observed in agy's credential store.
        if (!string.IsNullOrWhiteSpace(preferred)
            && !string.IsNullOrWhiteSpace(stored)
            && string.Equals(outgoingToken, stored, StringComparison.Ordinal)
            && !string.Equals(outgoingToken, preferred, StringComparison.Ordinal))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", preferred);
            return preferred;
        }

        return outgoingToken;
    }

    private void SynchronizeExternalCredential()
    {
        AntigravityCredentialSnapshot snapshot = ReadCredentialSafely();
        lock (_stateLock)
        {
            if (!_hasObservedStoredCredential)
            {
                _hasObservedStoredCredential = true;
                _lastObservedStoredAccessToken = snapshot.AccessToken;
                _lastObservedStoredRefreshToken = snapshot.RefreshToken;
                _preferredAccessToken = snapshot.AccessToken;
                _preferredRefreshToken = snapshot.RefreshToken;
                return;
            }

            if (!string.IsNullOrWhiteSpace(snapshot.AccessToken)
                && !string.Equals(snapshot.AccessToken, _lastObservedStoredAccessToken, StringComparison.Ordinal))
            {
                _lastObservedStoredAccessToken = snapshot.AccessToken;
                _preferredAccessToken = snapshot.AccessToken;
                _lastRejectedAccessToken = null;
            }

            if (!string.IsNullOrWhiteSpace(snapshot.RefreshToken)
                && !string.Equals(snapshot.RefreshToken, _lastObservedStoredRefreshToken, StringComparison.Ordinal))
            {
                _lastObservedStoredRefreshToken = snapshot.RefreshToken;
                _preferredRefreshToken = snapshot.RefreshToken;
            }
        }
    }

    private bool TryAdoptFreshExternalCredential(string? rejectedToken, out string? accessToken)
    {
        AntigravityCredentialSnapshot snapshot = ReadCredentialSafely();
        accessToken = null;

        if (string.IsNullOrWhiteSpace(snapshot.AccessToken))
        {
            return false;
        }

        lock (_stateLock)
        {
            bool changed = !_hasObservedStoredCredential
                           || !string.Equals(snapshot.AccessToken, _lastObservedStoredAccessToken, StringComparison.Ordinal);

            _hasObservedStoredCredential = true;
            _lastObservedStoredAccessToken = snapshot.AccessToken;
            if (!string.IsNullOrWhiteSpace(snapshot.RefreshToken))
            {
                _lastObservedStoredRefreshToken = snapshot.RefreshToken;
            }

            if (!changed || string.Equals(snapshot.AccessToken, rejectedToken, StringComparison.Ordinal))
            {
                return false;
            }

            _preferredAccessToken = snapshot.AccessToken;
            if (!string.IsNullOrWhiteSpace(snapshot.RefreshToken))
            {
                _preferredRefreshToken = snapshot.RefreshToken;
            }
            _lastRejectedAccessToken = null;
            accessToken = snapshot.AccessToken;
            return true;
        }
    }

    private async Task RewriteRefreshTokenIfNeededAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Content == null)
        {
            return;
        }

        string? preferredRefreshToken;
        lock (_stateLock)
        {
            preferredRefreshToken = _preferredRefreshToken;
        }

        if (string.IsNullOrWhiteSpace(preferredRefreshToken))
        {
            return;
        }

        string body = await request.Content.ReadAsStringAsync(cancellationToken);
        Dictionary<string, string> fields = ParseFormBody(body);
        if (!fields.TryGetValue("refresh_token", out string? currentRefreshToken)
            || string.Equals(currentRefreshToken, preferredRefreshToken, StringComparison.Ordinal))
        {
            return;
        }

        fields["refresh_token"] = preferredRefreshToken;
        request.Content = new FormUrlEncodedContent(fields);
    }

    private void CacheSuccessfulRefresh(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            string? accessToken = root.TryGetProperty("access_token", out JsonElement access)
                ? access.GetString()
                : null;
            string? refreshToken = root.TryGetProperty("refresh_token", out JsonElement refresh)
                ? refresh.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return;
            }

            lock (_stateLock)
            {
                _preferredAccessToken = accessToken;
                if (!string.IsNullOrWhiteSpace(refreshToken))
                {
                    _preferredRefreshToken = refreshToken;
                }
                _lastRejectedAccessToken = null;
            }
        }
        catch
        {
            // The provider will handle malformed OAuth responses normally.
        }
    }

    private AntigravityCredentialSnapshot ReadCredentialSafely()
    {
        try
        {
            return _credentialReader();
        }
        catch
        {
            return AntigravityCredentialSnapshot.Empty;
        }
    }

    private static bool IsGoogleRefreshRequest(HttpRequestMessage request)
    {
        Uri? uri = request.RequestUri;
        return request.Method == HttpMethod.Post
               && uri != null
               && uri.Host.Equals(GoogleTokenHost, StringComparison.OrdinalIgnoreCase)
               && uri.AbsolutePath.Equals("/token", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAntigravityApiRequest(HttpRequestMessage request)
    {
        Uri? uri = request.RequestUri;
        return uri != null
               && (uri.Host.Equals("cloudcode-pa.googleapis.com", StringComparison.OrdinalIgnoreCase)
                   || uri.Host.Equals("daily-cloudcode-pa.sandbox.googleapis.com", StringComparison.OrdinalIgnoreCase));
    }

    private static HttpResponseMessage CreateSyntheticRefreshResponse(string accessToken)
    {
        string json = JsonSerializer.Serialize(new
        {
            access_token = accessToken,
            token_type = "Bearer",
            expires_in = 3600
        });

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static Dictionary<string, string> ParseFormBody(string body)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = pair.IndexOf('=');
            string rawKey = separator >= 0 ? pair[..separator] : pair;
            string rawValue = separator >= 0 ? pair[(separator + 1)..] : string.Empty;
            result[DecodeFormComponent(rawKey)] = DecodeFormComponent(rawValue);
        }
        return result;
    }

    private static string DecodeFormComponent(string value)
        => Uri.UnescapeDataString(value.Replace('+', ' '));

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refreshGate.Dispose();
            _invoker.Dispose();
            _innerHandler.Dispose();
        }
        base.Dispose(disposing);
    }

    public sealed record AntigravityCredentialSnapshot(string? AccessToken, string? RefreshToken)
    {
        public static AntigravityCredentialSnapshot Empty { get; } = new(null, null);

        public static AntigravityCredentialSnapshot ReadCurrent()
        {
            string? envAccess = Environment.GetEnvironmentVariable("ANTIGRAVITY_ACCESS_TOKEN")
                                ?? Environment.GetEnvironmentVariable("GEMINI_ACCESS_TOKEN");
            string? envRefresh = Environment.GetEnvironmentVariable("ANTIGRAVITY_REFRESH_TOKEN")
                                 ?? Environment.GetEnvironmentVariable("GEMINI_REFRESH_TOKEN");
            if (!string.IsNullOrWhiteSpace(envAccess) || !string.IsNullOrWhiteSpace(envRefresh))
            {
                return new AntigravityCredentialSnapshot(envAccess, envRefresh);
            }

            if (OperatingSystem.IsWindows())
            {
                string? credential = Win32CredMan.ReadCredential("gemini:antigravity")
                                     ?? Win32CredMan.ReadCredential("antigravity:oauth")
                                     ?? Win32CredMan.ReadCredential("google:cloudcode");
                AntigravityCredentialSnapshot parsed = ParseCredentialPayload(credential);
                if (!string.IsNullOrWhiteSpace(parsed.AccessToken) || !string.IsNullOrWhiteSpace(parsed.RefreshToken))
                {
                    return parsed;
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

            foreach (string path in candidatePaths)
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                try
                {
                    AntigravityCredentialSnapshot parsed = ParseCredentialPayload(File.ReadAllText(path));
                    if (!string.IsNullOrWhiteSpace(parsed.AccessToken) || !string.IsNullOrWhiteSpace(parsed.RefreshToken))
                    {
                        return parsed;
                    }
                }
                catch
                {
                }
            }

            return Empty;
        }

        private static AntigravityCredentialSnapshot ParseCredentialPayload(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return Empty;
            }

            string normalized = NormalizeCredentialPayload(raw);
            try
            {
                using JsonDocument document = JsonDocument.Parse(normalized);
                JsonElement root = document.RootElement;
                string? access = null;
                string? refresh = null;

                ReadTokenObject(root, ref access, ref refresh);

                if (root.TryGetProperty("token", out JsonElement token))
                {
                    if (token.ValueKind == JsonValueKind.String)
                    {
                        access = token.GetString();
                    }
                    else if (token.ValueKind == JsonValueKind.Object)
                    {
                        ReadTokenObject(token, ref access, ref refresh);
                    }
                }

                if (root.TryGetProperty("oauth", out JsonElement oauth) && oauth.ValueKind == JsonValueKind.Object)
                {
                    ReadTokenObject(oauth, ref access, ref refresh);
                }

                return new AntigravityCredentialSnapshot(access, refresh);
            }
            catch
            {
                return Empty;
            }
        }

        private static void ReadTokenObject(JsonElement element, ref string? access, ref string? refresh)
        {
            if (element.TryGetProperty("access_token", out JsonElement accessElement))
            {
                access = accessElement.GetString();
            }
            if (element.TryGetProperty("refresh_token", out JsonElement refreshElement))
            {
                refresh = refreshElement.GetString();
            }
        }

        private static string NormalizeCredentialPayload(string raw)
        {
            string trimmed = raw.Trim();
            if (!trimmed.StartsWith(KeyringBase64Prefix, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed;
            }

            string encoded = trimmed[KeyringBase64Prefix.Length..].Trim();
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            }
            catch
            {
                return trimmed;
            }
        }
    }
}
