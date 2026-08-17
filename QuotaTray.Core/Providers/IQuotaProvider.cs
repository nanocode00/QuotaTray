using System.Threading;
using System.Threading.Tasks;
using QuotaTray.Core.Models;

namespace QuotaTray.Core.Providers;

public interface IQuotaProvider
{
    string ProviderKey { get; }
    string ProviderTitle { get; }
    string IconLetter { get; }
    string IconColorHex { get; }
    string IconBgColorHex { get; }
    string AuthMethod { get; }
    string? CliLoginCommand { get; }
    bool RequiresApiKey { get; }

    void SetCustomApiKey(string? apiKey);
    Task<ProviderQuotaResult> FetchQuotaAsync(CancellationToken cancellationToken = default);
}
