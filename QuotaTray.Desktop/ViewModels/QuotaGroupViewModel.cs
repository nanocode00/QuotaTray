using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
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

    public string ToggleText => IsExpanded ? "▼ 모델 목록 접기" : $"▶ 포함 모델 보기 ({Models.Count})";

    public void Toggle()
    {
        IsExpanded = !IsExpanded;
    }

    public static QuotaGroupViewModel FromModel(QuotaGroup g)
    {
        var vm = new QuotaGroupViewModel
        {
            GroupId = g.GroupId,
            GroupName = g.GroupName,
            PrimaryRemainingPercent = g.PrimaryRemainingPercent,
            PrimaryRemainingPercentText = g.PrimaryRemainingPercentText
        };

        foreach (var m in g.ModelsList) vm.Models.Add(m);
        foreach (var w in g.Windows) vm.Windows.Add(w);

        return vm;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
