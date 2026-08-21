using System;
using System.Threading.Tasks;
using QuotaTray.Core.Providers;

namespace QuotaTray.TestCli;

class Program
{
    static async Task Main(string[] args)
    {


        var provider = new AntigravityQuotaProvider();
        var res = await provider.FetchQuotaAsync();
        Console.WriteLine($"IsSuccess: {res.IsSuccess}");
        Console.WriteLine($"AuthStatus: {res.AuthStatus}");
        Console.WriteLine($"ErrorMessage: {res.ErrorMessage}");
        Console.WriteLine($"PlanType: {res.PlanType}");
        Console.WriteLine($"AccountEmail: {res.AccountEmail}");
        Console.WriteLine($"DetailsSubtitle: {res.DetailsSubtitle}");
        Console.WriteLine($"PrimaryRemainingPercent: {res.PrimaryRemainingPercent}%");
        Console.WriteLine($"ResetText: {res.ResetText}");
        Console.WriteLine($"Windows Count: {res.Windows.Count}");
        foreach (var w in res.Windows)
        {
            Console.WriteLine($" - {w.Name}: {w.RemainingPercent}% (used: {w.UsedPercent}%, reset: {w.FormattedResetIn})");
        }
        Console.WriteLine($"Groups Count: {res.Groups.Count}");
        foreach (var g in res.Groups)
        {
            Console.WriteLine($" [Group] {g.GroupName}: {g.PrimaryRemainingPercentText}");
            foreach (var w in g.Windows)
            {
                Console.WriteLine($"   * {w.Name}: {w.RemainingPercent}% ({w.FormattedResetIn})");
            }
        }
    }
}
