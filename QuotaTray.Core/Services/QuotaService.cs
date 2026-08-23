using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using QuotaTray.Core.Models;
using QuotaTray.Core.Providers;

namespace QuotaTray.Core.Services;

public record ProviderMetadata(
    string Key,
    string Title,
    string IconLetter,
    string IconColorHex,
    string IconBgColorHex,
    string AuthMethod,
    string? CliLoginCommand,
    bool RequiresApiKey
);

public class QuotaService
{
    private readonly Dictionary<string, IQuotaProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ProviderQuotaResult> _lastGoodCache = new(StringComparer.OrdinalIgnoreCase);

    public QuotaService()
    {
        RegisterProvider(new CodexQuotaProvider());

        var antigravityHandler = new AntigravitySessionCoordinatorHandler();
        var antigravityHttpClient = new HttpClient(antigravityHandler)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        RegisterProvider(new AntigravityQuotaProvider(antigravityHttpClient));

        RegisterProvider(new ClaudeQuotaProvider());
        RegisterProvider(new CopilotQuotaProvider());
        RegisterProvider(new OpenRouterQuotaProvider());
        RegisterProvider(new KimiQuotaProvider());
        RegisterProvider(new ZaiQuotaProvider());
        RegisterProvider(new SyntheticQuotaProvider());
    }

    public void RegisterProvider(IQuotaProvider provider)
    {
        _providers[provider.ProviderKey] = provider;
    }

    public IReadOnlyList<ProviderMetadata> GetAvailableProviders()
    {
        return _providers.Values
            .Select(p => new ProviderMetadata(
                p.ProviderKey,
                p.ProviderTitle,
                p.IconLetter,
                p.IconColorHex,
                p.IconBgColorHex,
                p.AuthMethod,
                p.CliLoginCommand,
                p.RequiresApiKey
            ))
            .ToList();
    }

    public async Task<List<ProviderQuotaResult>> RefreshSelectedAsync(
        IEnumerable<string> enabledKeys,
        Func<string, string?>? apiKeyResolver = null,
        CancellationToken cancellationToken = default)
    {
        var targetKeys = enabledKeys.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (targetKeys.Count == 0)
        {
            targetKeys = new List<string> { "codex", "antigravity" };
        }

        var tasks = new List<Task<ProviderQuotaResult>>();

        foreach (var key in targetKeys)
        {
            if (_providers.TryGetValue(key, out var provider))
            {
                provider.SetCustomApiKey(apiKeyResolver?.Invoke(provider.ProviderKey));

                if (provider is IManagementKeyProvider managementKeyProvider)
                {
                    managementKeyProvider.SetManagementKey(
                        apiKeyResolver?.Invoke($"{provider.ProviderKey}:management"));
                }

                tasks.Add(FetchWithCacheFallbackAsync(provider, cancellationToken));
            }
        }

        var results = await Task.WhenAll(tasks);
        return results.ToList();
    }

    private async Task<ProviderQuotaResult> FetchWithCacheFallbackAsync(IQuotaProvider provider, CancellationToken ct)
    {
        try
        {
            var result = await provider.FetchQuotaAsync(ct);
            if (result.IsSuccess)
            {
                _lastGoodCache[provider.ProviderKey] = result;
            }
            else if (!result.IsAuthMissing && _lastGoodCache.TryGetValue(provider.ProviderKey, out var cached))
            {
                return cached;
            }

            return result;
        }
        catch (Exception ex)
        {
            if (_lastGoodCache.TryGetValue(provider.ProviderKey, out var cached))
            {
                return cached;
            }

            return new ProviderQuotaResult
            {
                ProviderKey = provider.ProviderKey,
                ProviderTitle = provider.ProviderTitle,
                IconLetter = provider.IconLetter,
                IconColorHex = provider.IconColorHex,
                AuthMethod = provider.AuthMethod,
                CliLoginCommand = provider.CliLoginCommand,
                RequiresApiKey = provider.RequiresApiKey,
                IsSuccess = false,
                AuthStatus = ProviderAuthStatus.Error,
                ErrorMessage = ex.Message,
                FetchedAt = DateTimeOffset.UtcNow
            };
        }
    }
}
