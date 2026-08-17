using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QuotaTray.Core.Models;

namespace QuotaTray.Core.Providers;

public class KimiQuotaProvider : IQuotaProvider
{
    public string ProviderKey => "kimi";
    public string ProviderTitle => "Kimi (Moonshot)";
    public string IconLetter => "K";
    public string IconColorHex => "#EC4899";
    public string IconBgColorHex => "#500724";
    public string AuthMethod => "API Key";
    public string? CliLoginCommand => null;
    public bool RequiresApiKey => true;

    private const string UsageUrl = "https://api.moonshot.ai/v1/users/me/balance";
    private readonly HttpClient _httpClient;
    private string? _customApiKey;

    public KimiQuotaProvider(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public void SetCustomApiKey(string? apiKey)
    {
        _customApiKey = apiKey;
    }

    public async Task<ProviderQuotaResult> FetchQuotaAsync(CancellationToken cancellationToken = default)
    {
        var result = new ProviderQuotaResult
        {
            ProviderKey = ProviderKey,
            ProviderTitle = ProviderTitle,
            IconLetter = IconLetter,
            IconColorHex = IconColorHex,
            IconBgColorHex = IconBgColorHex,
            AuthMethod = AuthMethod,
            CliLoginCommand = CliLoginCommand,
            RequiresApiKey = RequiresApiKey,
            IsBalanceProvider = true,
            FetchedAt = DateTimeOffset.UtcNow
        };

        try
        {
            string? key = !string.IsNullOrEmpty(_customApiKey)
                ? _customApiKey
                : Environment.GetEnvironmentVariable("MOONSHOT_API_KEY")
                  ?? Environment.GetEnvironmentVariable("KIMI_API_KEY")
                  ?? Environment.GetEnvironmentVariable("KIMI_KEY");

            if (string.IsNullOrEmpty(key))
            {
                result.IsSuccess = false;
                result.IsAuthMissing = true;
                result.AuthStatus = ProviderAuthStatus.NotConfigured;
                result.ErrorMessage = "API Key not set. Enter Kimi API Key in Settings";
                return result;
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            req.Headers.Add("Authorization", $"Bearer {key}");

            var resp = await _httpClient.SendAsync(req, cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                result.IsSuccess = false;
                result.IsAuthMissing = resp.StatusCode == System.Net.HttpStatusCode.Unauthorized;
                result.AuthStatus = resp.StatusCode == System.Net.HttpStatusCode.Unauthorized ? ProviderAuthStatus.Expired : ProviderAuthStatus.Error;
                result.ErrorMessage = $"API Error ({(int)resp.StatusCode})";
                return result;
            }

            string json = await resp.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("data", out var dataProp))
            {
                double available = dataProp.TryGetProperty("available_balance", out var ab) ? ab.GetDouble() : 0.0;
                double cash = dataProp.TryGetProperty("cash_balance", out var cb) ? cb.GetDouble() : 0.0;
                double voucher = dataProp.TryGetProperty("voucher_balance", out var vb) ? vb.GetDouble() : 0.0;

                result.BalanceAmount = available;
                result.BalanceCurrency = "$";
                result.BalanceFormatted = $"${available:F2}";
                result.DetailsSubtitle = $"Cash: ${cash:F2} · Voucher: ${voucher:F2}";
                result.PrimaryRemainingPercent = available > 0 ? 100.0 : 0.0;
                result.AuthStatus = ProviderAuthStatus.Connected;
                result.IsSuccess = true;
                return result;
            }

            result.IsSuccess = false;
            result.AuthStatus = ProviderAuthStatus.Error;
            result.ErrorMessage = "Invalid response format";
            return result;
        }
        catch (OperationCanceledException)
        {
            result.IsSuccess = false;
            result.AuthStatus = ProviderAuthStatus.Error;
            result.ErrorMessage = "Request timed out";
            return result;
        }
        catch (Exception ex)
        {
            result.IsSuccess = false;
            result.AuthStatus = ProviderAuthStatus.Error;
            result.ErrorMessage = ex.Message;
            return result;
        }
    }
}
