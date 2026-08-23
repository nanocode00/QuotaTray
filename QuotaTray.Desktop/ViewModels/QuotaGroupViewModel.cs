using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using QuotaTray.Core.Models;

namespace QuotaTray.Desktop.ViewModels;

public class QuotaGroupViewModel : INotifyPropertyChanged
{
    private bool _isExpanded;
    public string GroupId { get; set; } = "";
    public string GroupName { get; set; } = "";
    public double PrimaryRemainingPercent { get; set; }
    public string PrimaryRemainingPercentText { get; set; } = "";
    public ObservableCollection<string> Models { get; } = new();
    public ObservableCollection<QuotaWindow> Windows { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded != value)
            {
                _isExpanded = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ToggleText));
            }
        }
    }

    public string ToggleText => Models.Count == 0
        ? ""
        : IsExpanded ? "▼ 모델 목록 접기" : $"▶ 포함 모델 보기 ({Models.Count})";

    public void Toggle()
    {
        if (Models.Count == 0) return;
        IsExpanded = !IsExpanded;
    }

    public static QuotaGroupViewModel FromModel(QuotaGroup g)
    {
        var vm = new QuotaGroupViewModel
        {
            GroupId = g.GroupId,
            // Keep the server/provider supplied group name. Do not reinterpret a metered
            // feature ID as a specific product/plan label that may not apply to every user.
            GroupName = g.GroupName,
            PrimaryRemainingPercent = g.PrimaryRemainingPercent,
            PrimaryRemainingPercentText = g.PrimaryRemainingPercentText
        };

        var modelNames = g.ModelsList
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .ToList();

        // Rendering-only groups and single-model groups whose model name is already the
        // raw group heading do not need an expandable list that repeats the same text.
        bool isRenderingOnlyGroup = g.GroupId.Equals("codex-shared", StringComparison.OrdinalIgnoreCase);
        bool repeatsRawGroupName = modelNames.Count == 1 &&
                                   modelNames[0].Equals(g.GroupName, StringComparison.OrdinalIgnoreCase);

        if (!isRenderingOnlyGroup && !repeatsRawGroupName)
        {
            foreach (var modelName in modelNames)
            {
                vm.Models.Add(modelName);
            }
        }

        foreach (var w in g.Windows) vm.Windows.Add(w);

        return vm;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
