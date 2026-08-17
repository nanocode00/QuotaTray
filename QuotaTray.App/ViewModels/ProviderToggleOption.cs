using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using QuotaTray.Core.Models;
using MediaColor = System.Windows.Media.Color;

namespace QuotaTray.App.ViewModels;

public class ProviderToggleOption : INotifyPropertyChanged
{
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public string IconLetter { get; set; } = "";
    public string IconColorHex { get; set; } = "#10B981";
    public string AuthMethod { get; set; } = "API Key";
    public string? CliLoginCommand { get; set; }
    public bool RequiresApiKey { get; set; }

    private bool _isChecked;
    private string _apiKey = "";
    private bool _isEditingKey;
    private ProviderAuthStatus _authStatus = ProviderAuthStatus.NotConfigured;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? IsCheckedChanged;
    public event Action<string, string?>? ApiKeySaved;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked != value)
            {
                _isChecked = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsActionsEnabled));
                IsCheckedChanged?.Invoke();
            }
        }
    }

    public bool IsActionsEnabled => IsChecked;

    public ProviderAuthStatus AuthStatus
    {
        get => _authStatus;
        set
        {
            if (_authStatus != value)
            {
                _authStatus = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(AuthStatusBadgeText));
                OnPropertyChanged(nameof(AuthStatusBadgeBrush));
                OnPropertyChanged(nameof(AuthStatusBgBrush));
            }
        }
    }

    public string AuthStatusBadgeText => AuthStatus switch
    {
        ProviderAuthStatus.Connected => "연결됨",
        ProviderAuthStatus.NotConfigured => "미설정",
        ProviderAuthStatus.Expired => "만료됨",
        ProviderAuthStatus.Error => "오류",
        _ => "미설정"
    };

    public SolidColorBrush AuthStatusBadgeBrush => AuthStatus switch
    {
        ProviderAuthStatus.Connected => new SolidColorBrush(MediaColor.FromRgb(16, 185, 129)),
        ProviderAuthStatus.NotConfigured => new SolidColorBrush(MediaColor.FromRgb(148, 163, 184)),
        ProviderAuthStatus.Expired => new SolidColorBrush(MediaColor.FromRgb(245, 158, 11)),
        ProviderAuthStatus.Error => new SolidColorBrush(MediaColor.FromRgb(239, 68, 68)),
        _ => new SolidColorBrush(MediaColor.FromRgb(148, 163, 184))
    };

    public SolidColorBrush AuthStatusBgBrush => AuthStatus switch
    {
        ProviderAuthStatus.Connected => new SolidColorBrush(MediaColor.FromArgb(35, 16, 185, 129)),
        ProviderAuthStatus.NotConfigured => new SolidColorBrush(MediaColor.FromArgb(35, 148, 163, 184)),
        ProviderAuthStatus.Expired => new SolidColorBrush(MediaColor.FromArgb(35, 245, 158, 11)),
        ProviderAuthStatus.Error => new SolidColorBrush(MediaColor.FromArgb(35, 239, 68, 68)),
        _ => new SolidColorBrush(MediaColor.FromArgb(35, 148, 163, 184))
    };

    public string ApiKey
    {
        get => _apiKey;
        set
        {
            if (_apiKey != value)
            {
                _apiKey = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasCustomApiKey));
            }
        }
    }

    public bool HasCustomApiKey => !string.IsNullOrWhiteSpace(_apiKey);

    public bool IsEditingKey
    {
        get => _isEditingKey;
        set
        {
            if (_isEditingKey != value)
            {
                _isEditingKey = value;
                OnPropertyChanged();
            }
        }
    }

    public void SaveCurrentApiKey()
    {
        ApiKeySaved?.Invoke(Key, _apiKey);
        IsEditingKey = false;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
