using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace QuotaTray.Core.Security;

public static class Win32CredMan
{
    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredEnumerateW(string? filter, int flags, out int count, out IntPtr pCredentials);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredReadW(string target, int type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredWriteW(ref CREDENTIAL credential, int flags);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredDeleteW(string target, int type, int flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr credentialPtr);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }

    private static string DecodeBlob(byte[] blob)
    {
        if (blob == null || blob.Length == 0) return "";

        // Check if the blob is UTF-16LE encoded (contains null bytes for ASCII characters)
        int nullCount = 0;
        for (int i = 1; i < blob.Length; i += 2)
        {
            if (blob[i] == 0) nullCount++;
        }

        string text;
        if (blob.Length >= 2 && nullCount > blob.Length / 4)
        {
            text = Encoding.Unicode.GetString(blob);
        }
        else
        {
            text = Encoding.UTF8.GetString(blob);
        }

        return text.Trim('\0', ' ', '\r', '\n');
    }

    public static string? ReadCredential(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;

        if (CredReadW(target, CRED_TYPE_GENERIC, 0, out IntPtr credPtr))
        {
            try
            {
                var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
                if (cred.CredentialBlobSize > 0 && cred.CredentialBlob != IntPtr.Zero)
                {
                    byte[] blob = new byte[cred.CredentialBlobSize];
                    Marshal.Copy(cred.CredentialBlob, blob, 0, cred.CredentialBlobSize);
                    string decoded = DecodeBlob(blob);
                    if (!string.IsNullOrEmpty(decoded)) return decoded;
                }
            }
            catch
            {
                // Ignore parse failures silently without leaking
            }
            finally
            {
                CredFree(credPtr);
            }
        }
        return null;
    }

    public static List<(string TargetName, string Secret)> EnumerateCredentials(string? filter = null)
    {
        var results = new List<(string TargetName, string Secret)>();
        if (!OperatingSystem.IsWindows()) return results;

        if (CredEnumerateW(filter, 0, out int count, out IntPtr pCredentials))
        {
            try
            {
                for (int i = 0; i < count; i++)
                {
                    IntPtr credPtr = Marshal.ReadIntPtr(pCredentials, i * IntPtr.Size);
                    if (credPtr != IntPtr.Zero)
                    {
                        var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
                        if (cred.CredentialBlobSize > 0 && cred.CredentialBlob != IntPtr.Zero)
                        {
                            byte[] blob = new byte[cred.CredentialBlobSize];
                            Marshal.Copy(cred.CredentialBlob, blob, 0, cred.CredentialBlobSize);
                            string decoded = DecodeBlob(blob);
                            if (!string.IsNullOrEmpty(decoded))
                            {
                                results.Add((cred.TargetName ?? "", decoded));
                            }
                        }
                    }
                }
            }
            catch
            {
            }
            finally
            {
                CredFree(pCredentials);
            }
        }

        return results;
    }

    public static bool WriteCredential(string target, string secret)
    {
        if (string.IsNullOrWhiteSpace(target) || secret == null) return false;

        byte[] blob = Encoding.UTF8.GetBytes(secret);
        IntPtr blobPtr = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);
            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = target,
                CredentialBlobSize = blob.Length,
                CredentialBlob = blobPtr,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = Environment.UserName
            };

            return CredWriteW(ref cred, 0);
        }
        catch
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(blobPtr);
        }
    }

    public static bool DeleteCredential(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return false;
        return CredDeleteW(target, CRED_TYPE_GENERIC, 0);
    }
}
