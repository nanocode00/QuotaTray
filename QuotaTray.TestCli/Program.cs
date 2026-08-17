using System;
using System.Threading.Tasks;
using QuotaTray.Core.Providers;

namespace QuotaTray.TestCli;

class Program
{
    static async Task Main(string[] args)
    {
        var provider = new CodexQuotaProvider();
        var res = await provider.FetchQuotaAsync();

        Console.WriteLine($"IsSuccess: {res.IsSuccess}");
        Console.WriteLine($"Title: {res.ProviderTitle}");
        Console.WriteLine($"PlanType: {res.PlanType}");
        Console.WriteLine($"Subtitle: {res.DetailsSubtitle}");
    }
}
