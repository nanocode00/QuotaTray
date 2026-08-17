using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuotaTray.Core.Models;

public class AggregatedQuotaResult
{
    public ProviderQuotaResult Codex { get; set; } = new() { ProviderKey = "codex", ProviderTitle = "Codex" };
    public ProviderQuotaResult Antigravity { get; set; } = new() { ProviderKey = "antigravity", ProviderTitle = "Antigravity" };
    public DateTimeOffset FetchedAt { get; set; } = DateTimeOffset.UtcNow;

    public string ToTestCliJson()
    {
        var dict = new Dictionary<string, object>();

        if (Codex.IsSuccess)
        {
            var windowsList = new List<object>();
            foreach (var w in Codex.Windows)
            {
                windowsList.Add(new
                {
                    name = w.Name,
                    remaining = (int)Math.Round(w.RemainingPercent),
                    resetInSeconds = w.ResetInSeconds
                });
            }
            dict["codex"] = new
            {
                windows = windowsList
            };
        }
        else
        {
            dict["codex"] = new
            {
                error = Codex.ErrorMessage ?? "Unknown error"
            };
        }

        if (Antigravity.IsSuccess)
        {
            dict["antigravity"] = new
            {
                models = Antigravity.ModelCount,
                tightestModel = Antigravity.TightestModel?.DisplayName ?? Antigravity.TightestModel?.ModelId ?? "Unknown",
                remaining = (int)Math.Round(Antigravity.PrimaryRemainingPercent),
                resetInSeconds = Antigravity.NextResetInSeconds
            };
        }
        else
        {
            dict["antigravity"] = new
            {
                error = Antigravity.ErrorMessage ?? "Unknown error"
            };
        }

        return JsonSerializer.Serialize(dict, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }
}
