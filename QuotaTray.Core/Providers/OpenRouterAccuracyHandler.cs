using System;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace QuotaTray.Core.Providers;

/// <summary>
/// Keeps the optional OpenRouter Analytics request on the response shape that has been
/// verified live: model + variant dimensions. Provider parsing still only consumes
/// variant/request_count and therefore remains independent of any specific model name.
/// </summary>
public sealed class OpenRouterAccuracyHandler : DelegatingHandler
{
    public OpenRouterAccuracyHandler(HttpMessageHandler? innerHandler = null)
        : base(innerHandler ?? new HttpClientHandler())
    {
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (IsAnalyticsQuery(request) && request.Content != null)
        {
            try
            {
                string body = await request.Content.ReadAsStringAsync(cancellationToken);
                JsonNode? node = JsonNode.Parse(body);
                if (node is JsonObject root)
                {
                    root["dimensions"] = new JsonArray("model", "variant");
                    root["limit"] = 500;

                    string mediaType = request.Content.Headers.ContentType?.MediaType ?? "application/json";
                    request.Content = new StringContent(root.ToJsonString(), Encoding.UTF8, mediaType);
                }
            }
            catch
            {
                // Analytics is optional. Leave the original request untouched if it cannot
                // be normalized; the provider will simply omit analytics on failure.
            }
        }

        return await base.SendAsync(request, cancellationToken);
    }

    private static bool IsAnalyticsQuery(HttpRequestMessage request)
    {
        Uri? uri = request.RequestUri;
        return request.Method == HttpMethod.Post
               && uri != null
               && uri.Host.Equals("openrouter.ai", StringComparison.OrdinalIgnoreCase)
               && uri.AbsolutePath.Equals("/api/v1/analytics/query", StringComparison.OrdinalIgnoreCase);
    }
}
