using System.Linq;
using System.Windows;
using Microsoft.Win32;
using YtDlpTool.Domain.Models;

namespace YtDlpTool.Services;

public sealed class ThemeService
{
    private readonly Application _app;

    public ThemeService(Application app) => _app = app;

    private ResourceDictionary? _currentPalette;

    public void Apply(ThemePreference pref)
    {
        var dark = pref switch
        {
            ThemePreference.Dark => true,
            ThemePreference.Light => false,
            _ => DetectSystemPrefersDark()
        };
        var uri = new System.Uri(
            dark ? "/Resources/Theme.Dark.xaml" : "/Resources/Theme.Light.xaml",
            System.UriKind.Relative);

        // Use the ctor that sets Source explicitly. Application.LoadComponent does NOT
        // preserve Source on the returned dictionary, which broke subsequent toggle
        // attempts (the "find by Source.Contains" pass below would never match the
        // previously-inserted dictionary again).
        var newDict = new ResourceDictionary { Source = uri };

        var dicts = _app.Resources.MergedDictionaries;
        var insertIndex = -1;

        // Prefer removing OUR previously-tracked palette (handles repeated toggles cleanly).
        if (_currentPalette is not null)
        {
            var idx = dicts.IndexOf(_currentPalette);
            if (idx >= 0)
            {
                insertIndex = idx;
                dicts.RemoveAt(idx);
            }
        }

        // First-call path (no tracked palette yet) — strip the default Theme.Light/Dark
        // that App.xaml loaded at startup.
        if (insertIndex < 0)
        {
            for (int i = 0; i < dicts.Count; i++)
            {
                var src = dicts[i].Source?.OriginalString ?? "";
                if (src.Contains("Theme.Light") || src.Contains("Theme.Dark"))
                {
                    insertIndex = i;
                    dicts.RemoveAt(i);
                    break;
                }
            }
        }

        if (insertIndex < 0) insertIndex = 0;
        dicts.Insert(insertIndex, newDict);
        _currentPalette = newDict;
    }

    private static bool DetectSystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int i && i == 0;
        }
        catch
        {
            return false;
        }
    }
}
