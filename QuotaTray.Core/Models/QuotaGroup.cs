using System;
using System.Collections.Generic;
using System.Linq;

namespace QuotaTray.Core.Models;

public class QuotaGroup
{
    public string GroupId { get; set; } = "";
    public string GroupName { get; set; } = "";
    public List<string> ModelsList { get; set; } = new();
    public List<QuotaWindow> Windows { get; set; } = new();

    public double PrimaryRemainingPercent => Windows.Count > 0 ? Windows.Min(w => w.RemainingPercent) : 100.0;
    public string PrimaryRemainingPercentText => $"{Math.Round(PrimaryRemainingPercent)}% left";
    public string ResetText => Windows.Count > 0 ? Windows.OrderBy(w => w.RemainingPercent).First().FormattedResetIn : "";
}
