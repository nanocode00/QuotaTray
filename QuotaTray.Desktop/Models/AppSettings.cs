using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using QuotaTray.Core.Security;

namespace QuotaTray.Desktop.Models;

public class AppSettings
{
    private static readonly ICredentialStore CredStore = new CrossPlatformCredentialStore();

    public int AutoRefreshMinutes { get; set; } = 3;
    public int WarningThresholdPercent { get; set; } = 15;
    public bool EnableNotifications { get; set; } = true;
    public bool IsCompactMode { get; set; } = false;

    public List<string> EnabledProviders { get; set; } = new()
    {
        "codex",
        "antigravity",
        "copilot",
        "openrouter"
    };

    public Dictionary<string, string> CustomApiKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string? GetApiKey(string providerKey)
    {
        if (CustomApiKeys.TryGetValue(providerKey, out var encrypted) && !string.IsNullOrWhiteSpace(encrypted))
        {
            return CredStore.UnprotectSecret(encrypted);
        }
        return null;
    }

    public void SetApiKey(string providerKey, string? plainApiKey)
    {
        if (string.IsNullOrWhiteSpace(plainApiKey))
        {
            CustomApiKeys.Remove(providerKey);
        }
        else
        {
            CustomApiKeys[providerKey] = CredStore.ProtectSecret(plainApiKey.Trim());
        }
        Save();
    }

    private static string GetConfigFilePath()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuotaTray");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "settings.json");
    }

    public static AppSettings Load()
    {
        string path = GetConfigFilePath();
        if (File.Exists(path))
        {
            try
            {
                string json = File.ReadAllText(path);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings != null) return settings;
            }
            catch { }
        }

        var defaultSettings = new AppSettings();
        defaultSettings.Save();
        return defaultSettings;
    }

    public void Save()
    {
        try
        {
            string path = GetConfigFilePath();
            string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch { }
    }
}
