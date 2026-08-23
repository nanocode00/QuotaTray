using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace QuotaTray.Core.Providers;

internal static class HttpContentCompatibilityExtensions
{
    /// <summary>
    /// .NET 8 does not expose a LoadIntoBufferAsync(CancellationToken) overload.
    /// Keep call sites cancellation-aware in signature while delegating to the
    /// available parameterless buffering API.
    /// </summary>
    public static Task LoadIntoBufferAsync(this HttpContent content, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return content.LoadIntoBufferAsync();
    }
}
