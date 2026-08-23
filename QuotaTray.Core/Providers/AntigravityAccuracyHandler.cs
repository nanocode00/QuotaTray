using System;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace QuotaTray.Core.Providers;

/// <summary>
/// Removes Antigravity response fields that QuotaTray cannot verify safely before the
/// legacy parser sees them, and replaces machine-specific protocol headers with the
/// actual runtime OS/architecture.
/// </summary>
public sealed class AntigravityAccuracyHandler : DelegatingHandler
{
    public AntigravityAccuracyHandler(HttpMessageHandler? innerHandler = null)
        : base(innerHandler ?? new HttpClientHandler())
    {
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (IsCloudCodeRequest(request))
        {
            request.Headers.Remove("User-Agent");
            request.Headers.TryAddWithoutValidation("User-Agent", BuildRuntimeUserAgent());
        }

        HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode
            || response.Content == null
            || !IsQuotaSummaryRequest(request))
        {
            return response;
        }

        try
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            JsonNode? node = JsonNode.Parse(body);
            if (node is not JsonObject root || root["groups"] is not JsonArray groups)
            {
                return response;
            }

            var verifiedGroups = new JsonArray();
            foreach (JsonNode? groupNode in groups)
            {
                if (groupNode is not JsonObject group)
                {
                    continue;
                }

                string displayName = group["displayName"]?.GetValue<string>() ?? string.Empty;
                if (!IsRecognizedQuotaGroup(displayName)
                    || group["buckets"] is not JsonArray buckets)
                {
                    continue;
                }

                var verifiedBuckets = new JsonArray();
                foreach (JsonNode? bucketNode in buckets)
                {
                    if (bucketNode is not JsonObject bucket
                        || !TryGetFiniteDouble(bucket["remainingFraction"], out double remainingFraction)
                        || remainingFraction < 0.0
                        || remainingFraction > 1.0)
                    {
                        continue;
                    }

                    string window = bucket["window"]?.GetValue<string>() ?? string.Empty;
                    string bucketId = bucket["bucketId"]?.GetValue<string>() ?? string.Empty;
                    string? normalizedWindow = NormalizeKnownWindow(window, bucketId);
                    if (normalizedWindow == null)
                    {
                        continue;
                    }

                    string resetTime = bucket["resetTime"]?.GetValue<string>() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(resetTime)
                        || !DateTimeOffset.TryParse(resetTime, out _))
                    {
                        continue;
                    }

                    JsonObject clone = (JsonObject)bucket.DeepClone();
                    clone["window"] = normalizedWindow;
                    verifiedBuckets.Add(clone);
                }

                if (verifiedBuckets.Count == 0)
                {
                    continue;
                }

                JsonObject groupClone = (JsonObject)group.DeepClone();
                groupClone["buckets"] = verifiedBuckets;
                verifiedGroups.Add(groupClone);
            }

            root["groups"] = verifiedGroups;
            string sanitized = root.ToJsonString();

            HttpContent oldContent = response.Content;
            string mediaType = oldContent.Headers.ContentType?.MediaType ?? "application/json";
            response.Content = new StringContent(sanitized, Encoding.UTF8, mediaType);
            oldContent.Dispose();
        }
        catch
        {
            // If validation itself cannot understand the response, leave it untouched.
            // The provider/accuracy guard will fail closed rather than fabricate quota.
        }

        return response;
    }

    private static bool IsCloudCodeRequest(HttpRequestMessage request)
    {
        string host = request.RequestUri?.Host ?? string.Empty;
        return host.Equals("cloudcode-pa.googleapis.com", StringComparison.OrdinalIgnoreCase)
               || host.Equals("daily-cloudcode-pa.sandbox.googleapis.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsQuotaSummaryRequest(HttpRequestMessage request)
        => IsCloudCodeRequest(request)
           && request.RequestUri?.AbsolutePath.EndsWith(
               "retrieveUserQuotaSummary",
               StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsRecognizedQuotaGroup(string displayName)
    {
        return displayName.Contains("Gemini", StringComparison.OrdinalIgnoreCase)
               || displayName.Contains("Claude", StringComparison.OrdinalIgnoreCase)
               || displayName.Contains("GPT", StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeKnownWindow(string window, string bucketId)
    {
        string combined = $"{window} {bucketId}";
        if (combined.Contains("week", StringComparison.OrdinalIgnoreCase))
        {
            return "weekly";
        }

        if (combined.Contains("5h", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("5_h", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("5-h", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("five_hour", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("five-hour", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("five hour", StringComparison.OrdinalIgnoreCase))
        {
            return "5h";
        }

        return null;
    }

    private static bool TryGetFiniteDouble(JsonNode? node, out double value)
    {
        value = 0.0;
        return node is JsonValue jsonValue
               && jsonValue.TryGetValue(out value)
               && double.IsFinite(value);
    }

    private static string BuildRuntimeUserAgent()
    {
        string os = OperatingSystem.IsWindows()
            ? "windows"
            : OperatingSystem.IsLinux()
                ? "linux"
                : OperatingSystem.IsMacOS()
                    ? "darwin"
                    : "unknown";

        string arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "amd64",
            Architecture.X86 => "386",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()
        };

        return $"antigravity/{os}/{arch}";
    }
}
