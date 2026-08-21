using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace QuotaTray.TestCli;

internal static class CanonicalAntigravityOAuthBootstrap
{
    // Public installed-app client ID used consistently by current Antigravity integrations.
    private const string CanonicalClientId =
        "1071006060591-tmhssin2h21lcre235vtolojh4g403ep.apps.googleusercontent.com";

    // SHA-256 fingerprint of the public installed-app client secret used by the
    // current Antigravity integrations we checked. The secret itself is never
    // committed or printed; the probe only accepts a local candidate whose
    // fingerprint matches this value.
    private const string CanonicalClientSecretSha256 =
        "1d2f041093fd95aa8995a038c711d50a7960da09a505381c09a745d6ad0ecc60";

    private static readonly Regex ClientSecretRegex = new(
        @"GOCSPX-[0-9A-Za-z_-]{20,64}",
        RegexOptions.Compiled);

    [ModuleInitializer]
    internal static void Initialize()
    {
        if (!Environment.CommandLine.Contains("refresh-test", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTIGRAVITY_CLIENT_ID"))
            && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTIGRAVITY_CLIENT_SECRET")))
        {
            return;
        }

        foreach (string artifact in GetCandidateArtifacts())
        {
            if (!File.Exists(artifact))
            {
                continue;
            }

            if (!TryFindCanonicalPair(artifact, out string? clientSecret))
            {
                continue;
            }

            Environment.SetEnvironmentVariable("ANTIGRAVITY_CLIENT_ID", CanonicalClientId);
            Environment.SetEnvironmentVariable("ANTIGRAVITY_CLIENT_SECRET", clientSecret);
            Console.WriteLine($"PoC bootstrap: exact canonical Antigravity OAuth client found in {artifact}");
            Console.WriteLine("PoC bootstrap: client secret matched by SHA-256 fingerprint; value is never printed or committed.");
            return;
        }

        Console.WriteLine("PoC bootstrap: exact canonical Antigravity OAuth pair was not found in the known local install artifacts.");
    }

    private static bool TryFindCanonicalPair(string path, out string? clientSecret)
    {
        clientSecret = null;

        try
        {
            byte[] bytes = File.ReadAllBytes(path);

            foreach (Encoding encoding in new[] { Encoding.Latin1, Encoding.Unicode })
            {
                string text = encoding.GetString(bytes);
                if (!text.Contains(CanonicalClientId, StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (Match match in ClientSecretRegex.Matches(text))
                {
                    string fingerprint = Convert.ToHexString(
                            SHA256.HashData(Encoding.UTF8.GetBytes(match.Value)))
                        .ToLowerInvariant();

                    if (!fingerprint.Equals(CanonicalClientSecretSha256, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    clientSecret = match.Value;
                    return true;
                }
            }
        }
        catch
        {
            // Diagnostic bootstrap only: skip unreadable candidates.
        }

        return false;
    }

    private static IEnumerable<string> GetCandidateArtifacts()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<string>();

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        if (OperatingSystem.IsWindows())
        {
            string[] installDirectories =
            {
                Path.Combine(localAppData, "Programs", "Antigravity IDE"),
                Path.Combine(localAppData, "Programs", "antigravity-ide"),
                Path.Combine(localAppData, "Programs", "Google Antigravity IDE"),
                Path.Combine(localAppData, "Programs", "Antigravity"),
                Path.Combine(localAppData, "Programs", "antigravity"),
                Path.Combine(localAppData, "Programs", "Google Antigravity"),
                Path.Combine(programFiles, "Antigravity IDE"),
                Path.Combine(programFiles, "Google Antigravity IDE"),
                Path.Combine(programFiles, "Antigravity"),
                Path.Combine(programFiles, "Google Antigravity"),
                Path.Combine(programFilesX86, "Antigravity IDE"),
                Path.Combine(programFilesX86, "Antigravity"),
                Path.Combine(userProfile, "scoop", "apps", "antigravity-ide", "current"),
                Path.Combine(userProfile, "scoop", "apps", "antigravity", "current")
            };

            foreach (string directory in installDirectories)
            {
                AddElectronArtifacts(candidates, directory);
            }

            candidates.Add(Path.Combine(localAppData, "agy", "bin", "agy.exe"));
            candidates.Add(Path.Combine(userProfile, ".gemini", "antigravity-cli", "bin", "agy.exe"));
        }
        else if (OperatingSystem.IsMacOS())
        {
            AddElectronArtifacts(candidates, "/Applications/Antigravity IDE.app/Contents");
            AddElectronArtifacts(candidates, "/Applications/Antigravity.app/Contents");
            AddElectronArtifacts(candidates, Path.Combine(userProfile, "Applications", "Antigravity IDE.app", "Contents"));
            AddElectronArtifacts(candidates, Path.Combine(userProfile, "Applications", "Antigravity.app", "Contents"));
            candidates.Add(Path.Combine(userProfile, ".local", "bin", "agy"));
            candidates.Add(Path.Combine(userProfile, ".gemini", "antigravity-cli", "bin", "agy"));
        }
        else
        {
            AddElectronArtifacts(candidates, "/opt/antigravity-ide");
            AddElectronArtifacts(candidates, "/opt/antigravity");
            candidates.Add(Path.Combine(userProfile, ".local", "bin", "agy"));
            candidates.Add(Path.Combine(userProfile, ".gemini", "antigravity-cli", "bin", "agy"));
            candidates.Add("/usr/local/bin/agy");
            candidates.Add("/usr/bin/agy");
        }

        string? pathValue = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathValue))
        {
            char separator = OperatingSystem.IsWindows() ? ';' : ':';
            string agyName = OperatingSystem.IsWindows() ? "agy.exe" : "agy";
            foreach (string directory in pathValue.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    candidates.Add(Path.Combine(directory, agyName));
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

    private static void AddElectronArtifacts(List<string> candidates, string installDirectory)
    {
        candidates.Add(Path.Combine(installDirectory, "resources", "app.asar"));
        candidates.Add(Path.Combine(installDirectory, "Resources", "app.asar"));
        candidates.Add(Path.Combine(installDirectory, "Antigravity IDE.exe"));
        candidates.Add(Path.Combine(installDirectory, "Antigravity.exe"));
        candidates.Add(Path.Combine(installDirectory, "MacOS", "Antigravity IDE"));
        candidates.Add(Path.Combine(installDirectory, "MacOS", "Antigravity"));
    }
}
