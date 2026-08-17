using System;

namespace QuotaTray.Core.Models;

public class ModelQuotaInfo
{
    public string ModelId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public double RemainingPercent { get; set; }
    public DateTimeOffset? ResetTime { get; set; }
    public long ResetInSeconds { get; set; }
    public string FormattedResetIn { get; set; } = "";
}
