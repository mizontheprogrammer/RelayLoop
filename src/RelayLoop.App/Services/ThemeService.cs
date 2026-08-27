using Microsoft.Win32;
using System.Windows;
using RelayLoop.App.Models;

namespace RelayLoop.App.Services;

public sealed class ThemeService : IDisposable
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private ResourceDictionary? _appliedDictionary;
    private bool _disposed;

    public ThemeService()
    {
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
    }

    public event EventHandler? ThemeChanged;

    public ThemePreference AppliedPreference { get; private set; }

    public void Apply(ThemePreference preference)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var useDark = preference switch
        {
            ThemePreference.Dark => true,
            ThemePreference.Light => false,
            _ => IsWindowsDarkTheme()
        };

        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var existing = _appliedDictionary ?? dictionaries.FirstOrDefault(static dictionary =>
            dictionary.Source?.OriginalString.Contains("Colors.", StringComparison.OrdinalIgnoreCase) == true);
        var replacement = SystemParameters.HighContrast
            ? CreateHighContrastDictionary()
            : new ResourceDictionary
            {
                Source = new Uri(useDark ? "Styles/Colors.Dark.xaml" : "Styles/Colors.Light.xaml", UriKind.Relative)
            };

        if (existing is null)
        {
            dictionaries.Insert(0, replacement);
        }
        else
        {
            dictionaries[dictionaries.IndexOf(existing)] = replacement;
        }

        _appliedDictionary = replacement;
        AppliedPreference = preference;
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
        GC.SuppressFinalize(this);
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (AppliedPreference == ThemePreference.System || SystemParameters.HighContrast)
        {
            ReapplyOnDispatcher();
        }
    }

    private void OnSystemParameterChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast))
        {
            ReapplyOnDispatcher();
        }
    }

    private void ReapplyOnDispatcher()
    {
        var application = Application.Current;
        if (_disposed || application is null)
        {
            return;
        }

        application.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_disposed)
            {
                Apply(AppliedPreference);
            }
        }));
    }

    private static ResourceDictionary CreateHighContrastDictionary() => new()
    {
        ["WindowBrush"] = SystemColors.WindowBrush,
        ["SurfaceBrush"] = SystemColors.ControlBrush,
        ["SurfaceRaisedBrush"] = SystemColors.ControlBrush,
        ["TextBrush"] = SystemColors.WindowTextBrush,
        ["TextMutedBrush"] = SystemColors.GrayTextBrush,
        ["BorderBrush"] = SystemColors.ActiveBorderBrush,
        ["AccentBrush"] = SystemColors.HighlightBrush,
        ["AccentSoftBrush"] = SystemColors.ControlBrush,
        ["RecordBrush"] = SystemColors.HotTrackBrush,
        ["RecordSoftBrush"] = SystemColors.ControlBrush,
        ["PlayBrush"] = SystemColors.HighlightBrush,
        ["PlaySoftBrush"] = SystemColors.ControlBrush,
        ["WarningBrush"] = SystemColors.HighlightBrush,
        ["ErrorBrush"] = SystemColors.HotTrackBrush,
        ["FocusBrush"] = SystemColors.HighlightBrush,
        ["ScrimBrush"] = SystemColors.WindowBrush,
        ["ScrimTextBrush"] = SystemColors.WindowTextBrush,
    };

    private static bool IsWindowsDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }
}
