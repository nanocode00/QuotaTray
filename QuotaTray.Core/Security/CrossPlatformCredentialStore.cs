using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace QuotaTray.Core.Security;

public class CrossPlatformCredentialStore : ICredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("QuotaTray.SecureToken.v1");

    public string? ReadCredential(string targetName)
    {
        if (OperatingSystem.IsWindows())
        {
            return Win32CredMan.ReadCredential(targetName);
        }
        else if (OperatingSystem.IsMacOS())
        {
            return ReadMacKeychain(targetName);
        }
        else if (OperatingSystem.IsLinux())
        {
            return ReadLinuxSecretService(targetName);
        }

        return null;
    }

    public bool WriteCredential(string targetName, string secret)
    {
        if (OperatingSystem.IsWindows())
        {
            return Win32CredMan.WriteCredential(targetName, secret);
        }
        else if (OperatingSystem.IsMacOS())
        {
            return WriteMacKeychain(targetName, secret);
        }
        else if (OperatingSystem.IsLinux())
        {
            return WriteLinuxSecretService(targetName, secret);
        }

        return false;
    }

    public bool DeleteCredential(string targetName)
    {
        if (OperatingSystem.IsWindows())
        {
            return Win32CredMan.DeleteCredential(targetName);
        }
        else if (OperatingSystem.IsMacOS())
        {
            return DeleteMacKeychain(targetName);
        }
        else if (OperatingSystem.IsLinux())
        {
            return DeleteLinuxSecretService(targetName);
        }

        return false;
    }

    public string ProtectSecret(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return "";

        try
        {
            if (OperatingSystem.IsWindows())
            {
                byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
                byte[] cipherBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
                return "dpapi:" + Convert.ToBase64String(cipherBytes);
            }
            else
            {
                // Cross-platform AES-GCM encryption with local hardware machine-id entropy
                byte[] key = GetMachineKey();
                byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
                byte[] nonce = new byte[12];
                RandomNumberGenerator.Fill(nonce);
                byte[] tag = new byte[16];
                byte[] cipherBytes = new byte[plainBytes.Length];

                using var aes = new AesGcm(key, 16);
                aes.Encrypt(nonce, plainBytes, cipherBytes, tag);

                byte[] combined = new byte[nonce.Length + tag.Length + cipherBytes.Length];
                Buffer.BlockCopy(nonce, 0, combined, 0, nonce.Length);
                Buffer.BlockCopy(tag, 0, combined, nonce.Length, tag.Length);
                Buffer.BlockCopy(cipherBytes, 0, combined, nonce.Length + tag.Length, cipherBytes.Length);

                return "aesgcm:" + Convert.ToBase64String(combined);
            }
        }
        catch
        {
            return plainText;
        }
    }

    public string UnprotectSecret(string cipherText)
    {
        if (string.IsNullOrEmpty(cipherText)) return "";

        try
        {
            if (cipherText.StartsWith("dpapi:") && OperatingSystem.IsWindows())
            {
                string base64 = cipherText["dpapi:".Length..];
                byte[] cipherBytes = Convert.FromBase64String(base64);
                byte[] plainBytes = ProtectedData.Unprotect(cipherBytes, Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plainBytes);
            }
            else if (cipherText.StartsWith("aesgcm:"))
            {
                string base64 = cipherText["aesgcm:".Length..];
                byte[] combined = Convert.FromBase64String(base64);
                if (combined.Length < 28) return "";

                byte[] key = GetMachineKey();
                byte[] nonce = new byte[12];
                byte[] tag = new byte[16];
                byte[] cipherBytes = new byte[combined.Length - 28];

                Buffer.BlockCopy(combined, 0, nonce, 0, 12);
                Buffer.BlockCopy(combined, 12, tag, 0, 16);
                Buffer.BlockCopy(combined, 28, cipherBytes, 0, cipherBytes.Length);

                byte[] plainBytes = new byte[cipherBytes.Length];
                using var aes = new AesGcm(key, 16);
                aes.Decrypt(nonce, cipherBytes, tag, plainBytes);

                return Encoding.UTF8.GetString(plainBytes);
            }
        }
        catch
        {
            return "";
        }

        return cipherText;
    }

    private static byte[] GetMachineKey()
    {
        string raw = Environment.MachineName + Environment.UserName + Environment.OSVersion.VersionString;
        return SHA256.HashData(Encoding.UTF8.GetBytes(raw));
    }

    private static string? ReadMacKeychain(string targetName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/usr/bin/security",
                Arguments = $"find-generic-password -s \"{targetName}\" -w",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            string output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit();
            return proc.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool WriteMacKeychain(string targetName, string secret)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/usr/bin/security",
                Arguments = $"add-generic-password -U -s \"{targetName}\" -a \"{Environment.UserName}\" -w \"{secret}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            proc.WaitForExit();
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool DeleteMacKeychain(string targetName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/usr/bin/security",
                Arguments = $"delete-generic-password -s \"{targetName}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            proc.WaitForExit();
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string? ReadLinuxSecretService(string targetName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "secret-tool",
                Arguments = $"lookup service \"{targetName}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            string output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit();
            return proc.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool WriteLinuxSecretService(string targetName, string secret)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "secret-tool",
                Arguments = $"store --label=\"QuotaTray {targetName}\" service \"{targetName}\"",
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            proc.StandardInput.Write(secret);
            proc.StandardInput.Close();
            proc.WaitForExit();
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool DeleteLinuxSecretService(string targetName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "secret-tool",
                Arguments = $"clear service \"{targetName}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            proc.WaitForExit();
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
