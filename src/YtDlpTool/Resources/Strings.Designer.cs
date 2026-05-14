using System.Windows;

namespace YtDlpTool.Resources;

public static class Strings
{
    public static string Get(string key)
    {
        var r = Application.Current?.TryFindResource(key);
        return r is string s ? s : key;
    }

    public static string Format(string key, params object[] args) =>
        string.Format(Get(key), args);
}
