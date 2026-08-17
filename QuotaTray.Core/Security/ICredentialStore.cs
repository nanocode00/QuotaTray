using System;

namespace QuotaTray.Core.Security;

public interface ICredentialStore
{
    string? ReadCredential(string targetName);
    bool WriteCredential(string targetName, string secret);
    bool DeleteCredential(string targetName);
    string ProtectSecret(string plainText);
    string UnprotectSecret(string cipherText);
}
