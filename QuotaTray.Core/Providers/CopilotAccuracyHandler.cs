using System;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace QuotaTray.Core.Providers;

/// <summary>
/// Filters Copilot quota snapshots that do not contain enough server data to compute
/// a real finite quota. In particular, entitlement without remaining would otherwise
/// be rendered as 0 remaining even though GitHub never reported that value.
/// </summary>
public sealed class CopilotAccuracyHandler : DelegatingHandler
{
    private static readonly string[] FiniteQuotaKeys =
    {
        "completions",
        "chat",
        "premium_interactions"
    };

    public CopilotAccuracyHandler(HttpMessageHandler? innerHandler = null)
        : base(innerHandler ?? new HttpClientHandler())
    {
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode
            || response.Content == null
            || !IsCopilotUsageRequest(request))
        {
            return response;
        }

        try
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            JsonNode? node = JsonNode.Parse(body);
            if (node is not JsonObject root || root["quota_snapshots"] is not JsonObject snapshots)
            {
                return response;
            }

            foreach (string key in FiniteQuotaKeys)
            {
                if (snapshots[key] is not JsonObject snapshot)
                {
                    continue;
                }

                bool unlimited = TryGetBoolean(snapshot["unlimited"], out bool unlimitedValue) && unlimitedValue;
                bool explicitlyNoQuota = TryGetBoolean(snapshot["has_quota"], out bool hasQuota) && !hasQuota;
                if (unlimited || explicitlyNoQuota)
                {
                    continue;
                }

                bool hasEntitlement = TryGetPositiveLong(snapshot["entitlement"], out _);
                bool hasRemaining = TryGetNonNegativeLong(snapshot["remaining"], out _)
                                    || TryGetNonNegativeLong(snapshot["quota_remaining"], out _);

                if (!hasEntitlement || !hasRemaining)
                {
                    snapshots.Remove(key);
                }
            }

            string sanitized = root.ToJsonString();
            HttpContent oldContent = response.Content;
            string mediaType = oldContent.Headers.ContentType?.MediaType ?? "application/json";
            response.Content = new StringContent(sanitized, Encoding.UTF8, mediaType);
            oldContent.Dispose();
        }
        catch
        {
            // Leave malformed/unknown responses to the provider's normal error path.
        }

        return response;
    }

    private static bool IsCopilotUsageRequest(HttpRequestMessage request)
    {
        Uri? uri = request.RequestUri;
        return uri != null
               && uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)
               && uri.AbsolutePath.Equals("/copilot_internal/user", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetBoolean(JsonNode? node, out bool value)
    {
        value = false;
        return node is JsonValue jsonValue && jsonValue.TryGetValue(out value);
    }

    private static bool TryGetPositiveLong(JsonNode? node, out long value)
    {
        value = 0;
        return node is JsonValue jsonValue
               && jsonValue.TryGetValue(out value)
               && value > 0;
    }

    private static bool TryGetNonNegativeLong(JsonNode? node, out long value)
    {
        value = 0;
        return node is JsonValue jsonValue
               && jsonValue.TryGetValue(out value)
               && value >= 0;
    }
}
