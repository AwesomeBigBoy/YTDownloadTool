using System.Linq;
using System.Windows;
using Microsoft.Win32;
using YtDlpTool.Domain.Models;

namespace YtDlpTool.Services;

public sealed class ThemeService
{
    private readonly Application _app;

    public ThemeService(Application app) => _app = app;

    public void Apply(ThemePreference pref)
    {
        var dark = pref switch
        {
            ThemePreference.Dark => true,
            ThemePreference.Light => false,
            _ => DetectSystemPrefersDark()
        };
        var path = dark ? "/Resources/Theme.Dark.xaml" : "/Resources/Theme.Light.xaml";
        var uri = new System.Uri(path, System.UriKind.Relative);
        var newDict = (ResourceDictionary)Application.LoadComponent(uri);

        var dicts = _app.Resources.MergedDictionaries;
        // Find the existing palette dictionary (Theme.Light or Theme.Dark) and replace it
        // at the same index so the merged-dictionary lookup order is preserved.
        var existingIndex = -1;
        for (int i = 0; i < dicts.Count; i++)
        {
            var src = dicts[i].Source?.OriginalString ?? "";
            if (src.Contains("Theme.Light") || src.Contains("Theme.Dark"))
            {
                existingIndex = i;
                break;
            }
        }
        if (existingIndex >= 0)
        {
            dicts.RemoveAt(existingIndex);
            dicts.Insert(existingIndex, newDict);
        }
        else
        {
            // No palette dictionary found — insert at front so Theme.xaml can find brushes.
            dicts.Insert(0, newDict);
        }
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
