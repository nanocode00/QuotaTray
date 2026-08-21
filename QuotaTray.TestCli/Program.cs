using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using QuotaTray.Core.Models;
using QuotaTray.Core.Providers;

namespace QuotaTray.TestCli;

class Program
{
    static async Task Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("refresh-test", StringComparison.OrdinalIgnoreCase))
        {
            await RunForcedRefreshProbeAsync();
            return;
        }

        if (args.Length > 0 && args[0].Equals("copilot", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("GitHub Copilot provider probe");
            Console.WriteLine("This uses QuotaTray's normal credential discovery and provider path. Tokens are never printed.");
            Console.WriteLine();

            var copilotProvider = new CopilotQuotaProvider();
            ProviderQuotaResult copilotResult = await copilotProvider.FetchQuotaAsync();
            PrintResult(copilotResult);
            return;
        }

        var provider = new AntigravityQuotaProvider();
        ProviderQuotaResult res = await provider.FetchQuotaAsync();
        PrintResult(res);
    }

    private static async Task RunForcedRefreshProbeAsync()
    {
        Console.WriteLine("Antigravity forced refresh probe");
        Console.WriteLine("This bypasses the currently-valid access token and tests QuotaTray's direct refresh path.");
        Console.WriteLine("agy does not need to be running. Tokens and OAuth secrets are never printed.");
        Console.WriteLine();

        var provider = new AntigravityQuotaProvider();
        Type providerType = typeof(AntigravityQuotaProvider);

        MethodInfo? loadCredentials = providerType.GetMethod(
            "LoadStoredCredentials",
            BindingFlags.Instance | BindingFlags.NonPublic);
        MethodInfo? refreshToken = providerType.GetMethod(
            "TryRefreshTokenAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        FieldInfo? lastRefreshError = providerType.GetField(
            "_lastRefreshError",
            BindingFlags.Instance | BindingFlags.NonPublic);

        if (loadCredentials == null || refreshToken == null || lastRefreshError == null)
        {
            Console.WriteLine("REFRESH PROBE FAILED: provider internals changed; update the diagnostic probe.");
            Environment.ExitCode = 2;
            return;
        }

        loadCredentials.Invoke(provider, null);

        object? invocationResult = refreshToken.Invoke(provider, new object[] { CancellationToken.None });
        if (invocationResult is not Task<bool> refreshTask)
        {
            Console.WriteLine("REFRESH PROBE FAILED: unexpected refresh method result.");
            Environment.ExitCode = 3;
            return;
        }

        bool refreshed = await refreshTask;
        if (!refreshed)
        {
            string? error = lastRefreshError.GetValue(provider) as string;
            Console.WriteLine($"REFRESH PROBE FAILED: {error ?? "unknown refresh error"}");
            Environment.ExitCode = 4;
            return;
        }

        Console.WriteLine("Direct OAuth refresh: OK");
        Console.WriteLine("Now querying quota with the refreshed in-memory access token...");

        ProviderQuotaResult result = await provider.FetchQuotaAsync();
        PrintResult(result);

        if (result.IsSuccess)
        {
            Console.WriteLine();
            Console.WriteLine("REFRESH PROBE SUCCESS: QuotaTray refreshed and queried Antigravity without an agy process.");
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine("REFRESH PROBE PARTIAL: refresh succeeded, but the quota query failed.");
            Environment.ExitCode = 5;
        }
    }

    private static void PrintResult(ProviderQuotaResult res)
    {
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
