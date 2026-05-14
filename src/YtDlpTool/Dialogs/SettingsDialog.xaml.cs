using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using YtDlpTool.Domain.Models;

namespace YtDlpTool.Dialogs;

public partial class SettingsDialog : Window
{
    private readonly AppHost _host;
    private string _selectedSaveDir;

    public SettingsDialog(AppHost host)
    {
        InitializeComponent();
        _host = host;
        _selectedSaveDir = host.Config.DefaultSaveDirectory;
        LoadFromConfig();
    }

    private void LoadFromConfig()
    {
        DefaultDirText.Text = _selectedSaveDir;
        ConcurrencySlider.Value = _host.Config.ConcurrentDownloads;
        SelectComboTag(FrequencyCombo, _host.Config.AppCheckFrequency.ToString());
        EnableYtDlpUpdates.IsChecked = _host.Config.YtDlpCheckFrequency != UpdateCheckFrequency.Never;
        EnableFfmpegUpdates.IsChecked = _host.Config.FfmpegCheckFrequency != UpdateCheckFrequency.Never;
        SelectComboTag(ThemeCombo, _host.Config.Theme.ToString());
    }

    private static void SelectComboTag(ComboBox combo, string tag)
    {
        foreach (var item in combo.Items)
            if (item is ComboBoxItem ci && (string?)ci.Tag == tag) { combo.SelectedItem = ci; return; }
        combo.SelectedIndex = 0;
    }

    private void OnBrowseDefaultDir(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "選擇預設儲存資料夾",
            InitialDirectory = Directory.Exists(_selectedSaveDir) ? _selectedSaveDir : ""
        };
        if (dlg.ShowDialog() == true)
        {
            _selectedSaveDir = dlg.FolderName;
            DefaultDirText.Text = _selectedSaveDir;
        }
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e) => OpenFolder(_host.Paths.LogsDirectory);
    private void OnOpenDownloads(object sender, RoutedEventArgs e) => OpenFolder(_host.Config.DefaultSaveDirectory);
    private static void OpenFolder(string p)
    {
        try { Directory.CreateDirectory(p); System.Diagnostics.Process.Start(new ProcessStartInfo("explorer.exe", p) { UseShellExecute = true }); }
        catch { }
    }

    private void OnAbout(object sender, RoutedEventArgs e)
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        MessageBox.Show(
            $"YtDlpTool {version}\n\nSigstore 簽署者：" +
            "https://github.com/placeholder-owner/YtDlpTool/.github/workflows/release.yml\n\n" +
            "授權：MIT (本工具)\nyt-dlp：Unlicense\nffmpeg：GPL/LGPL",
            "關於 YtDlpTool", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var cfg = _host.Config;
        cfg.DefaultSaveDirectory = _selectedSaveDir;
        cfg.ConcurrentDownloads = (int)ConcurrencySlider.Value;
        _host.Queue.MaxConcurrency = cfg.ConcurrentDownloads;
        cfg.AppCheckFrequency = ParseFrequency(FrequencyCombo);
        cfg.YtDlpCheckFrequency = EnableYtDlpUpdates.IsChecked == true ? UpdateCheckFrequency.Weekly : UpdateCheckFrequency.Never;
        cfg.FfmpegCheckFrequency = EnableFfmpegUpdates.IsChecked == true ? UpdateCheckFrequency.Monthly : UpdateCheckFrequency.Never;
        cfg.Theme = ParseTheme(ThemeCombo);
        _host.ConfigStore.Save(cfg);
        DialogResult = true;
        Close();
    }

    private static UpdateCheckFrequency ParseFrequency(ComboBox c) =>
        c.SelectedItem is ComboBoxItem it && it.Tag is string tag &&
        Enum.TryParse<UpdateCheckFrequency>(tag, out var f) ? f : UpdateCheckFrequency.Weekly;

    private static ThemePreference ParseTheme(ComboBox c) =>
        c.SelectedItem is ComboBoxItem it && it.Tag is string tag &&
        Enum.TryParse<ThemePreference>(tag, out var t) ? t : ThemePreference.System;
}
