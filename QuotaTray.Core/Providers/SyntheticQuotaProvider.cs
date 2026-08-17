using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QuotaTray.Core.Models;

namespace QuotaTray.Core.Providers;

public class SyntheticQuotaProvider : IQuotaProvider
{
    public string ProviderKey => "synthetic";
    public string ProviderTitle => "Synthetic.new";
    public string IconLetter => "S";
    public string IconColorHex => "#14B8A6";
    public string IconBgColorHex => "#042F2E";
    public string AuthMethod => "API Key";
    public string? CliLoginCommand => null;
    public bool RequiresApiKey => true;

    private const string ValidationUrl = "https://api.synthetic.new/v1/models";
    private readonly HttpClient _httpClient;
    private string? _customApiKey;

    public SyntheticQuotaProvider(HttpClient? httpClient = null)
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
                : Environment.GetEnvironmentVariable("SYNTHETIC_API_KEY");

            if (string.IsNullOrEmpty(key))
            {
                result.IsSuccess = false;
                result.IsAuthMissing = true;
                result.AuthStatus = ProviderAuthStatus.NotConfigured;
                result.ErrorMessage = "API Key not set. Enter Synthetic.new API Key in Settings";
                return result;
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, ValidationUrl);
            req.Headers.Add("Authorization", $"Bearer {key}");

            var resp = await _httpClient.SendAsync(req, cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                result.IsSuccess = false;
                result.IsAuthMissing = resp.StatusCode == System.Net.HttpStatusCode.Unauthorized;
                result.AuthStatus = resp.StatusCode == System.Net.HttpStatusCode.Unauthorized ? ProviderAuthStatus.Expired : ProviderAuthStatus.Error;
                result.ErrorMessage = $"API Key invalid ({(int)resp.StatusCode})";
                return result;
            }

            result.PlanType = "Subscribed";
            result.DetailsSubtitle = "Account Active · Synthetic Connected";
            result.BalanceFormatted = "Active";
            result.PrimaryRemainingPercent = 100.0;
            result.AuthStatus = ProviderAuthStatus.Connected;
            result.IsSuccess = true;
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
