using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace QuotaTray.TestCli;

internal static class OAuthClientAutoDiscovery
{
    private static readonly Regex ClientIdRegex = new(
        @"([0-9]{6,}-[0-9A-Za-z_-]+\.apps\.googleusercontent\.com)",
        RegexOptions.Compiled);

    [ModuleInitializer]
    internal static void Initialize()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTIGRAVITY_OAUTH_CLIENT_ID")))
        {
            return;
        }

        string? clientId = TryDiscoverClientId();
        if (!string.IsNullOrWhiteSpace(clientId))
        {
            Environment.SetEnvironmentVariable("ANTIGRAVITY_OAUTH_CLIENT_ID", clientId);
            Console.WriteLine("PoC bootstrap: discovered Antigravity OAuth client ID from the installed agy binary.");
            Console.WriteLine("agy does not need to be running; the binary is inspected only to bootstrap this PoC.");
        }
    }

    private static string? TryDiscoverClientId()
    {
        foreach (string path in GetCandidateAgyPaths())
        {
            if (!File.Exists(path)) continue;

            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                string text = Encoding.Latin1.GetString(bytes);
                MatchCollection matches = ClientIdRegex.Matches(text);

                foreach (Match match in matches)
                {
                    string value = match.Groups[1].Value;
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
            }
            catch
            {
                // PoC helper only: skip unreadable candidates and keep searching.
            }
        }

        return null;
    }

    private static IEnumerable<string> GetCandidateAgyPaths()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var candidates = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            candidates.Add(Path.Combine(localAppData, "agy", "bin", "agy.exe"));
            candidates.Add(Path.Combine(userProfile, ".gemini", "antigravity-cli", "bin", "agy.exe"));
        }
        else
        {
            candidates.Add(Path.Combine(userProfile, ".local", "bin", "agy"));
            candidates.Add(Path.Combine(userProfile, ".gemini", "antigravity-cli", "bin", "agy"));
            candidates.Add("/usr/local/bin/agy");
            candidates.Add("/usr/bin/agy");
        }

        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            char separator = OperatingSystem.IsWindows() ? ';' : ':';
            string executableName = OperatingSystem.IsWindows() ? "agy.exe" : "agy";

            foreach (string directory in pathEnv.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    candidates.Add(Path.Combine(directory, executableName));
                }
                catch
                {
                }
            }
        }

        foreach (string candidate in candidates)
        {
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(candidate);
            }
            catch
            {
                continue;
            }

            if (seen.Add(fullPath))
            {
                yield return fullPath;
            }
        }
    }
}
