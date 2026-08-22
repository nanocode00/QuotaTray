namespace QuotaTray.Core.Providers;

/// <summary>
/// Optional capability for providers that can use a separate management-level key
/// for read-only quota/analytics enrichment in addition to the normal API key.
/// </summary>
public interface IManagementKeyProvider
{
    void SetManagementKey(string? managementKey);
}
