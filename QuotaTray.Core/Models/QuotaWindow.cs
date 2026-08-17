using System;

namespace QuotaTray.Core.Models;

public class QuotaWindow
{
    public string Name { get; set; } = "";
    public long LimitWindowSeconds { get; set; }
    public double RemainingPercent { get; set; }
    public double UsedPercent { get; set; }
    public long ResetInSeconds { get; set; }
    public string FormattedResetIn { get; set; } = "";
}
