using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using QuotaTray.Core.Models;
using MediaColor = System.Windows.Media.Color;

namespace QuotaTray.App.ViewModels;

public class ProviderItemViewModel : INotifyPropertyChanged
{
    private static readonly SolidColorBrush GreenBrush = new(MediaColor.FromRgb(16, 185, 129));
    private static readonly SolidColorBrush OrangeBrush = new(MediaColor.FromRgb(245, 158, 11));
    private static readonly SolidColorBrush RedBrush = new(MediaColor.FromRgb(239, 68, 68));
    private static readonly SolidColorBrush MutedBrush = new(MediaColor.FromRgb(148, 163, 184));

    private static readonly SolidColorBrush GreenBg = new(MediaColor.FromArgb(40, 16, 185, 129));
    private static readonly SolidColorBrush OrangeBg = new(MediaColor.FromArgb(40, 245, 158, 11));
    private static readonly SolidColorBrush RedBg = new(MediaColor.FromArgb(40, 239, 68, 68));
    private static readonly SolidColorBrush MutedBg = new(MediaColor.FromArgb(30, 148, 163, 184));

    static ProviderItemViewModel()
    {
        GreenBrush.Freeze();
        OrangeBrush.Freeze();
        RedBrush.Freeze();
        MutedBrush.Freeze();
        GreenBg.Freeze();
        OrangeBg.Freeze();
        RedBg.Freeze();
        MutedBg.Freeze();
    }

    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public string IconLetter { get; set; } = "?";
    public SolidColorBrush IconColor { get; set; } = GreenBrush;
    public SolidColorBrush IconBgColor { get; set; } = GreenBg;

    public string AuthMethod { get; set; } = "OAuth";
    public string? CliLoginCommand { get; set; }
    public bool RequiresApiKey { get; set; }
    public bool HasCliLoginCommand => !string.IsNullOrEmpty(CliLoginCommand);

    private bool _isSuccess;
    private bool _isAuthMissing;
    private string _errorMessage = "";
    private string _planType = "";
    private string _detailsSubtitle = "";
    private double _primaryRemainingPercent;
    private string _primaryRemainingPercentText = "--";
    private string _resetText = "";
    private bool _isModelsExpanded;
    private bool _isBalanceProvider;
    private string _balanceFormatted = "";

    public ObservableCollection<QuotaWindow> Windows { get; } = new();
    public ObservableCollection<ModelQuotaInfo> Models { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsSuccess
    {
        get => _isSuccess;
        set => SetField(ref _isSuccess, value);
    }

    public bool IsAuthMissing
    {
        get => _isAuthMissing;
        set => SetField(ref _isAuthMissing, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        set => SetField(ref _errorMessage, value);
    }

    public string PlanType
    {
        get => _planType;
        set => SetField(ref _planType, value);
    }

    public string DetailsSubtitle
    {
        get => _detailsSubtitle;
        set => SetField(ref _detailsSubtitle, value);
    }

    public double PrimaryRemainingPercent
    {
        get => _primaryRemainingPercent;
        set
        {
            if (SetField(ref _primaryRemainingPercent, value))
            {
                OnPropertyChanged(nameof(RemainingBrush));
                OnPropertyChanged(nameof(RemainingBgBrush));
            }
        }
    }

    public string PrimaryRemainingPercentText
    {
        get => _primaryRemainingPercentText;
        set => SetField(ref _primaryRemainingPercentText, value);
    }

    public string ResetText
    {
        get => _resetText;
        set => SetField(ref _resetText, value);
    }

    public bool IsModelsExpanded
    {
        get => _isModelsExpanded;
        set
        {
            if (SetField(ref _isModelsExpanded, value))
            {
                OnPropertyChanged(nameof(ModelsToggleText));
            }
        }
    }

    public string ModelsToggleText => IsModelsExpanded ? "▼ 모델 목록 접기" : (Models.Count > 0 ? $"▶ 전체 {Models.Count}개 모델 보기" : "▶ 모델 보기");

    public bool HasWindows => Windows.Count > 0;
    public bool HasModels => Models.Count > 0;
    public bool HasModelsOnly => HasModels && !HasWindows;

    public bool IsBalanceProvider
    {
        get => _isBalanceProvider;
        set => SetField(ref _isBalanceProvider, value);
    }

    public string BalanceFormatted
    {
        get => _balanceFormatted;
        set => SetField(ref _balanceFormatted, value);
    }

    public SolidColorBrush RemainingBrush
    {
        get
        {
            if (!_isSuccess) return MutedBrush;
            if (_primaryRemainingPercent > 30.0) return GreenBrush;
            if (_primaryRemainingPercent >= 15.0) return OrangeBrush;
            return RedBrush;
        }
    }

    public SolidColorBrush RemainingBgBrush
    {
        get
        {
            if (!_isSuccess) return MutedBg;
            if (_primaryRemainingPercent > 30.0) return GreenBg;
            if (_primaryRemainingPercent >= 15.0) return OrangeBg;
            return RedBg;
        }
    }

    public void ToggleModels()
    {
        IsModelsExpanded = !IsModelsExpanded;
    }

    public void UpdateFromResult(ProviderQuotaResult res)
    {
        Key = res.ProviderKey;
        Title = res.ProviderTitle;
        IconLetter = res.IconLetter;
        AuthMethod = res.AuthMethod;
        CliLoginCommand = res.CliLoginCommand;
        RequiresApiKey = res.RequiresApiKey;

        try
        {
            var iconColor = (MediaColor)System.Windows.Media.ColorConverter.ConvertFromString(res.IconColorHex);
            IconColor = new SolidColorBrush(iconColor);
            IconColor.Freeze();

            var iconBgColor = (MediaColor)System.Windows.Media.ColorConverter.ConvertFromString(res.IconBgColorHex);
            IconBgColor = new SolidColorBrush(iconBgColor);
            IconBgColor.Freeze();
        }
        catch { }

        IsSuccess = res.IsSuccess;
        IsAuthMissing = res.IsAuthMissing;
        ErrorMessage = res.ErrorMessage ?? "";
        PlanType = res.PlanType ?? "";
        DetailsSubtitle = res.DetailsSubtitle ?? "";
        IsBalanceProvider = res.IsBalanceProvider;
        BalanceFormatted = res.BalanceFormatted ?? "";

        PrimaryRemainingPercent = res.PrimaryRemainingPercent;

        if (res.IsBalanceProvider && !string.IsNullOrEmpty(res.BalanceFormatted))
        {
            PrimaryRemainingPercentText = res.BalanceFormatted;
        }
        else if (res.IsSuccess)
        {
            PrimaryRemainingPercentText = $"{res.PrimaryRemainingPercent:F0}% left";
        }
        else
        {
            PrimaryRemainingPercentText = res.IsAuthMissing ? "Auth Required" : "Error";
        }

        ResetText = res.ResetText ?? res.FormattedNextResetIn;

        // Update Windows
        Windows.Clear();
        if (res.Windows != null)
        {
            foreach (var w in res.Windows)
            {
                Windows.Add(w);
            }
        }

        // Update Models
        Models.Clear();
        if (res.Models != null)
        {
            foreach (var m in res.Models)
            {
                Models.Add(m);
            }
        }

        OnPropertyChanged(nameof(HasWindows));
        OnPropertyChanged(nameof(HasModels));
        OnPropertyChanged(nameof(HasModelsOnly));
        OnPropertyChanged(nameof(HasCliLoginCommand));
        OnPropertyChanged(nameof(ModelsToggleText));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (!Equals(field, value))
        {
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
        return false;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
