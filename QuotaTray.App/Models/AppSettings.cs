using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuotaTray.App.Models;

public class AppSettings
{
    public int AutoRefreshMinutes { get; set; } = 3;
    public int WarningThresholdPercent { get; set; } = 15;
    public bool EnableNotifications { get; set; } = true;
    public bool IsCompactMode { get; set; } = false;
    public List<string> EnabledProviders { get; set; } = new() { "codex", "antigravity", "copilot", "openrouter" };

    // Stored on disk (encrypted via DPAPI)
    [JsonPropertyName("ApiKeys")]
    public Dictionary<string, string> RawApiKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // In-memory plain text keys
    [JsonIgnore]
    private readonly Dictionary<string, string> _plainApiKeys = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QuotaTray"
    );
    private static readonly string SettingsFilePath = Path.Combine(SettingsDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                string json = File.ReadAllText(SettingsFilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings != null)
                {
                    settings.EnabledProviders ??= new List<string> { "codex", "antigravity", "copilot", "openrouter" };
                    settings.RawApiKeys ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                    // Decrypt keys into _plainApiKeys
                    foreach (var kvp in settings.RawApiKeys)
                    {
                        string plain = DecryptValue(kvp.Value);
                        if (!string.IsNullOrEmpty(plain))
                        {
                            settings._plainApiKeys[kvp.Key] = plain;
                        }
                    }
                    return settings;
                }
            }
        }
        catch
        {
            // Fallback to default settings
        }

        return new AppSettings();
    }

    public string? GetApiKey(string key)
    {
        return _plainApiKeys.TryGetValue(key, out var val) && !string.IsNullOrWhiteSpace(val) ? val : null;
    }

    public void SetApiKey(string key, string? val)
    {
        if (string.IsNullOrWhiteSpace(val))
        {
            _plainApiKeys.Remove(key);
            RawApiKeys.Remove(key);
        }
        else
        {
            _plainApiKeys[key] = val.Trim();
            RawApiKeys[key] = EncryptValue(val.Trim());
        }
        Save();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);

            // Synchronize RawApiKeys
            RawApiKeys.Clear();
            foreach (var kvp in _plainApiKeys)
            {
                if (!string.IsNullOrWhiteSpace(kvp.Value))
                {
                    RawApiKeys[kvp.Key] = EncryptValue(kvp.Value);
                }
            }

            string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsFilePath, json);
        }
        catch
        {
            // Ignore disk errors
        }
    }

    private static string EncryptValue(string plainText)
    {
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(plainText);
            byte[] encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            return "dpapi:" + Convert.ToBase64String(encrypted);
        }
        catch
        {
            return plainText;
        }
    }

    private static string DecryptValue(string cipherText)
    {
        if (string.IsNullOrEmpty(cipherText)) return "";
        if (cipherText.StartsWith("dpapi:"))
        {
            try
            {
                byte[] bytes = Convert.FromBase64String(cipherText[6..]);
                byte[] decrypted = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(decrypted);
            }
            catch
            {
                return "";
            }
        }
        return cipherText;
    }
}
