using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace QuotaTray.TestCli;

internal static class OAuthClientAutoDiscovery
{
    private const string GoogleClientSuffix = ".apps.googleusercontent.com";

    private static readonly Regex ClientIdRegex = new(
        @"([0-9]{6,}-[0-9A-Za-z._-]+\.apps\.googleusercontent\.com)",
        RegexOptions.Compiled);

    [ModuleInitializer]
    internal static void Initialize()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTIGRAVITY_OAUTH_CLIENT_ID")))
        {
            return;
        }

        string? clientId = TryDiscoverClientId(out string? agyPath, out bool binaryWasFound);
        if (!string.IsNullOrWhiteSpace(clientId))
        {
            Environment.SetEnvironmentVariable("ANTIGRAVITY_OAUTH_CLIENT_ID", clientId);
            Console.WriteLine($"PoC bootstrap: found agy at {agyPath}");
            Console.WriteLine("PoC bootstrap: discovered Antigravity OAuth client ID from the installed agy binary.");
            Console.WriteLine("agy does not need to be running; the binary is inspected only to bootstrap this PoC.");
        }
        else if (binaryWasFound)
        {
            Console.WriteLine($"PoC bootstrap: found agy at {agyPath}, but no Google OAuth client ID could be extracted from that binary.");
            Console.WriteLine("The install path is correct; the remaining issue is the client-ID representation inside the current agy binary.");
        }
        else
        {
            Console.WriteLine("PoC bootstrap: agy.exe was not found in the standard install locations or PATH.");
        }
    }

    private static string? TryDiscoverClientId(out string? foundAgyPath, out bool binaryWasFound)
    {
        foundAgyPath = null;
        binaryWasFound = false;

        foreach (string path in GetCandidateAgyPaths())
        {
            if (!File.Exists(path)) continue;

            foundAgyPath = path;
            binaryWasFound = true;

            try
            {
                byte[] bytes = File.ReadAllBytes(path);

                // Go binaries normally keep string constants as UTF-8/ASCII, but scan
                // UTF-16LE and raw suffix boundaries as well so the PoC is not tied
                // to one compiler/linker representation.
                foreach (string text in new[]
                {
                    Encoding.Latin1.GetString(bytes),
                    Encoding.Unicode.GetString(bytes)
                })
                {
                    MatchCollection matches = ClientIdRegex.Matches(text);
                    foreach (Match match in matches)
                    {
                        string value = match.Groups[1].Value;
                        if (LooksLikeGoogleClientId(value))
                        {
                            return value;
                        }
                    }
                }

                string? rawAscii = FindClientIdAroundAsciiSuffix(bytes);
                if (LooksLikeGoogleClientId(rawAscii)) return rawAscii;

                string? rawUtf16 = FindClientIdAroundUtf16Suffix(bytes);
                if (LooksLikeGoogleClientId(rawUtf16)) return rawUtf16;
            }
            catch
            {
                // PoC helper only: skip unreadable candidates and keep searching.
            }
        }

        return null;
    }

    private static string? FindClientIdAroundAsciiSuffix(byte[] bytes)
    {
        byte[] suffix = Encoding.ASCII.GetBytes(GoogleClientSuffix);
        int index = IndexOf(bytes, suffix);
        while (index >= 0)
        {
            int start = index - 1;
            int min = Math.Max(0, index - 256);
            while (start >= min && IsClientIdByte(bytes[start])) start--;
            start++;

            if (start < index)
            {
                string candidate = Encoding.ASCII.GetString(bytes, start, index + suffix.Length - start);
                if (LooksLikeGoogleClientId(candidate)) return candidate;
            }

            index = IndexOf(bytes, suffix, index + 1);
        }

        return null;
    }

    private static string? FindClientIdAroundUtf16Suffix(byte[] bytes)
    {
        byte[] suffix = Encoding.Unicode.GetBytes(GoogleClientSuffix);
        int index = IndexOf(bytes, suffix);
        while (index >= 0)
        {
            int start = index - 2;
            int min = Math.Max(0, index - 512);
            while (start >= min && start + 1 < bytes.Length && bytes[start + 1] == 0 && IsClientIdByte(bytes[start]))
            {
                start -= 2;
            }
            start += 2;

            if (start < index && (index - start) % 2 == 0)
            {
                string candidate = Encoding.Unicode.GetString(bytes, start, index + suffix.Length - start);
                if (LooksLikeGoogleClientId(candidate)) return candidate;
            }

            index = IndexOf(bytes, suffix, index + 2);
        }

        return null;
    }

    private static bool LooksLikeGoogleClientId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (!value.EndsWith(GoogleClientSuffix, StringComparison.Ordinal)) return false;

        string prefix = value[..^GoogleClientSuffix.Length];
        int dash = prefix.IndexOf('-');
        if (dash <= 0 || dash == prefix.Length - 1) return false;

        return prefix[..dash].All(char.IsDigit);
    }

    private static bool IsClientIdByte(byte value)
    {
        return (value >= (byte)'0' && value <= (byte)'9')
               || (value >= (byte)'A' && value <= (byte)'Z')
               || (value >= (byte)'a' && value <= (byte)'z')
               || value == (byte)'-'
               || value == (byte)'_'
               || value == (byte)'.';
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int startIndex = 0)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length) return -1;

        for (int i = Math.Max(0, startIndex); i <= haystack.Length - needle.Length; i++)
        {
            bool matches = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] == needle[j]) continue;
                matches = false;
                break;
            }

            if (matches) return i;
        }

        return -1;
    }

    private static IEnumerable<string> GetCandidateAgyPaths()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var candidates = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            // Current Windows installer default.
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

        // Also resolve agy exactly the same way the shell does: every PATH entry.
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
