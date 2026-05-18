using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using YtDlpTool.Domain.Models;
using YtDlpTool.Domain.Updates;

namespace YtDlpTool.Dialogs;

public partial class SettingsDialog : Window
{
    private readonly AppHost _host;
    private string _selectedSaveDir;
    private CancellationTokenSource? _checkUpdateCts;
    private CancellationTokenSource? _redownloadCts;

    public SettingsDialog(AppHost host)
    {
        InitializeComponent();
        _host = host;
        _selectedSaveDir = host.Config.DefaultSaveDirectory;
        LoadFromConfig();
        Closed += (_, _) =>
        {
            try { _checkUpdateCts?.Cancel(); } catch { }
            try { _redownloadCts?.Cancel(); } catch { }
        };
    }

    private void LoadFromConfig()
    {
        DefaultDirText.Text = _selectedSaveDir;
        ConcurrencySlider.Value = _host.Config.ConcurrentDownloads;
        SelectComboTag(FrequencyCombo, _host.Config.AppCheckFrequency.ToString());
        EnableYtDlpUpdates.IsChecked = _host.Config.YtDlpCheckFrequency != UpdateCheckFrequency.Never;
        EnableFfmpegUpdates.IsChecked = _host.Config.FfmpegCheckFrequency != UpdateCheckFrequency.Never;
        SelectComboTag(ThemeCombo, _host.Config.Theme.ToString());
        AllowUntrustedCertificatesCheckbox.IsChecked = _host.Config.AllowUntrustedCertificates;
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
        MessageBox.Show(
            $"YtDlpTool v{MainWindow.GetAppVersion()}\n\nSigstore 簽署者：" +
            "https://github.com/AwesomeBigBoy/YTDownloadTool/.github/workflows/release.yml\n\n" +
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
        cfg.FfmpegCheckFrequency = EnableFfmpegUpdates.IsChecked == true ? UpdateCheckFrequency.Weekly : UpdateCheckFrequency.Never;
        cfg.Theme = ParseTheme(ThemeCombo);
        // v1.2.4: AllowUntrustedCertificates is read at YtDlpRunner construction time
        // (which happens in AppHost ctor at startup). Toggling here writes to disk but
        // won't affect the running app until restart. The description text below the
        // checkbox warns the user about this.
        var requireRestart = cfg.AllowUntrustedCertificates != (AllowUntrustedCertificatesCheckbox.IsChecked == true);
        cfg.AllowUntrustedCertificates = AllowUntrustedCertificatesCheckbox.IsChecked == true;
        _host.ConfigStore.Save(cfg);
        ((App)Application.Current).ThemeService.Apply(cfg.Theme);
        DialogResult = true;
        Close();
        if (requireRestart)
        {
            MessageBox.Show(
                "「允許不受信任憑證」設定已儲存，需重新啟動程式後生效。",
                "YtDlpTool", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private static UpdateCheckFrequency ParseFrequency(ComboBox c) =>
        c.SelectedItem is ComboBoxItem it && it.Tag is string tag &&
        Enum.TryParse<UpdateCheckFrequency>(tag, out var f) ? f : UpdateCheckFrequency.Weekly;

    private static ThemePreference ParseTheme(ComboBox c) =>
        c.SelectedItem is ComboBoxItem it && it.Tag is string tag &&
        Enum.TryParse<ThemePreference>(tag, out var t) ? t : ThemePreference.System;

    // -------- Fix 1: manual "check now & update" trigger --------
    private async void OnCheckUpdateClicked(object sender, RoutedEventArgs e)
    {
        _checkUpdateCts?.Cancel();
        _checkUpdateCts = new CancellationTokenSource();
        var ct = _checkUpdateCts.Token;

        CheckUpdateNowButton.IsEnabled = false;
        UpdateStatusText.Visibility = Visibility.Visible;
        UpdateProgressBar.Visibility = Visibility.Visible;
        UpdateProgressBar.IsIndeterminate = true;
        UpdateStatusText.Text = "檢查中…";
        OpenDiagnosticsLogLink.Visibility = Visibility.Collapsed;

        try
        {
            var installed = await _host.GetInstalledVersionsAsync().ConfigureAwait(true);
            if (ct.IsCancellationRequested) return;
            var availability = await _host.UpdateChecker.CheckAsync(installed, ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested) return;

            if (!availability.HasUpdate || availability.NewerFiles.Count == 0)
            {
                UpdateProgressBar.IsIndeterminate = false;
                UpdateProgressBar.Visibility = Visibility.Collapsed;
                UpdateStatusText.Text = availability.FailureReason is null
                    ? "已是最新版本"
                    : $"檢查失敗：{availability.FailureReason}";
                // Fix 2 (v1.1.6): surface the 顯示診斷詳情 link only when the check
                // actually failed, so the user can inspect today's log entries.
                OpenDiagnosticsLogLink.Visibility = availability.FailureReason is null
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                await Task.Delay(2000, ct).ConfigureAwait(true);
                UpdateStatusText.Visibility = Visibility.Collapsed;
                OpenDiagnosticsLogLink.Visibility = Visibility.Collapsed;
                return;
            }
            // Successful availability path: any previously-shown diagnostic link hides.
            OpenDiagnosticsLogLink.Visibility = Visibility.Collapsed;

            await ApplyUpdateAsync(availability.NewerFiles, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { /* dialog closed */ }
        catch (Exception ex)
        {
            UpdateProgressBar.IsIndeterminate = false;
            UpdateProgressBar.Visibility = Visibility.Collapsed;
            UpdateStatusText.Text = $"更新失敗：{ex.Message}";
            OpenDiagnosticsLogLink.Visibility = Visibility.Visible;
        }
        finally
        {
            CheckUpdateNowButton.IsEnabled = true;
        }
    }

    private async Task ApplyUpdateAsync(IReadOnlyList<ManifestFileEntry> entries, CancellationToken ct)
    {
        UpdateProgressBar.IsIndeterminate = false;
        UpdateProgressBar.Value = 0;
        var progress = new Progress<UpdateApplyProgress>(p => ApplyUpdateStageToUi(p));
        var result = await _host.UpdateApplier.ApplyAsync(entries, progress, ct).ConfigureAwait(true);
        if (ct.IsCancellationRequested) return;
        if (result.IsSuccess)
        {
            UpdateStatusText.Text = "✓ 已更新到最新版本";
            UpdateProgressBar.Value = 100;
            try { await Task.Delay(2000, ct).ConfigureAwait(true); }
            catch (OperationCanceledException) { return; }
            UpdateStatusText.Visibility = Visibility.Collapsed;
            UpdateProgressBar.Visibility = Visibility.Collapsed;
        }
        else
        {
            UpdateStatusText.Text = $"更新失敗：{result.FailureReason}";
        }
    }

    private void ApplyUpdateStageToUi(UpdateApplyProgress p)
    {
        // Marshals to the dispatcher; Progress<T> already captures SynchronizationContext on the
        // UI thread where the handler was registered, so this lambda is already main-thread safe.
        UpdateProgressBar.Value = p.FilePercent;
        UpdateStatusText.Text = p.Stage switch
        {
            UpdateApplyStage.Downloading        => $"下載中 · {p.FileName} · {p.FilePercent:0}%",
            UpdateApplyStage.VerifyingHash      => "驗證雜湊…",
            UpdateApplyStage.VerifyingSignature => "驗證簽章…",
            UpdateApplyStage.Applying           => "套用中…",
            UpdateApplyStage.Done               => "✓ 已更新到最新版本",
            UpdateApplyStage.RolledBack         => "更新失敗，已自動還原",
            UpdateApplyStage.Failed             => "更新失敗",
            _                                   => UpdateStatusText.Text
        };
    }

    // -------- Fix 2: "重新下載元件" — force re-fetch yt-dlp + ffmpeg from manifest --------
    private async void OnRedownloadComponentsClicked(object sender, RoutedEventArgs e)
    {
        _redownloadCts?.Cancel();
        _redownloadCts = new CancellationTokenSource();
        var ct = _redownloadCts.Token;

        RedownloadComponentsButton.IsEnabled = false;
        RedownloadStatusText.Visibility = Visibility.Visible;
        RedownloadProgressBar.Visibility = Visibility.Visible;
        RedownloadProgressBar.IsIndeterminate = true;
        RedownloadStatusText.Text = "取得元件清單…";
        OpenDiagnosticsLogLinkAdvanced.Visibility = Visibility.Collapsed;

        try
        {
            // Use an empty "installed" probe so UpdateChecker reports every manifest entry as newer.
            // We discard the newer-list and pull yt-dlp + ffmpeg entries from the manifest directly so
            // this also works when versions match (the user explicitly asked to redownload).
            var availability = await _host.UpdateChecker.CheckAsync(
                new InstalledVersions(App: "0.0.0", YtDlp: "", Ffmpeg: ""),
                ct).ConfigureAwait(true);

            if (ct.IsCancellationRequested) return;
            if (availability.Manifest is null)
            {
                RedownloadProgressBar.IsIndeterminate = false;
                RedownloadProgressBar.Visibility = Visibility.Collapsed;
                RedownloadStatusText.Text = $"取得清單失敗：{availability.FailureReason ?? "manifest missing"}";
                OpenDiagnosticsLogLinkAdvanced.Visibility = Visibility.Visible;
                return;
            }

            var targets = availability.Manifest.Files
                .Where(f => f.Component is UpdateComponent.YtDlp or UpdateComponent.Ffmpeg)
                .ToList();
            if (targets.Count == 0)
            {
                RedownloadProgressBar.IsIndeterminate = false;
                RedownloadProgressBar.Visibility = Visibility.Collapsed;
                RedownloadStatusText.Text = "清單中找不到 yt-dlp/ffmpeg 元件";
                OpenDiagnosticsLogLinkAdvanced.Visibility = Visibility.Visible;
                return;
            }
            OpenDiagnosticsLogLinkAdvanced.Visibility = Visibility.Collapsed;

            RedownloadProgressBar.IsIndeterminate = false;
            RedownloadProgressBar.Value = 0;
            var progress = new Progress<UpdateApplyProgress>(ApplyRedownloadStageToUi);
            var result = await _host.UpdateApplier.ApplyAsync(targets, progress, ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested) return;

            if (result.IsSuccess)
            {
                RedownloadStatusText.Text = "✓ 元件已重新下載完成";
                RedownloadProgressBar.Value = 100;
                try { await Task.Delay(2000, ct).ConfigureAwait(true); }
                catch (OperationCanceledException) { return; }
                RedownloadStatusText.Visibility = Visibility.Collapsed;
                RedownloadProgressBar.Visibility = Visibility.Collapsed;
            }
            else
            {
                RedownloadStatusText.Text = $"重新下載失敗：{result.FailureReason}";
                OpenDiagnosticsLogLinkAdvanced.Visibility = Visibility.Visible;
            }
        }
        catch (OperationCanceledException) { /* dialog closed */ }
        catch (Exception ex)
        {
            RedownloadProgressBar.IsIndeterminate = false;
            RedownloadProgressBar.Visibility = Visibility.Collapsed;
            RedownloadStatusText.Text = $"重新下載失敗：{ex.Message}";
            OpenDiagnosticsLogLinkAdvanced.Visibility = Visibility.Visible;
        }
        finally
        {
            RedownloadComponentsButton.IsEnabled = true;
        }
    }

    private void ApplyRedownloadStageToUi(UpdateApplyProgress p)
    {
        RedownloadProgressBar.Value = p.FilePercent;
        RedownloadStatusText.Text = p.Stage switch
        {
            UpdateApplyStage.Downloading        => $"下載中 · {p.FileName} · {p.FilePercent:0}%",
            UpdateApplyStage.VerifyingHash      => "驗證雜湊…",
            UpdateApplyStage.VerifyingSignature => "驗證簽章…",
            UpdateApplyStage.Applying           => "套用中…",
            UpdateApplyStage.Done               => "✓ 元件已重新下載完成",
            UpdateApplyStage.RolledBack         => "重新下載失敗，已自動還原",
            UpdateApplyStage.Failed             => "重新下載失敗",
            _                                   => RedownloadStatusText.Text
        };
    }

    /// <summary>
    /// Fix 2 (v1.1.6): opens the newest log file in Notepad so the user can capture the
    /// update.check.* trace for an IT bug report. Shared by the 更新 and 進階 sections;
    /// both surface this link only when the corresponding action failed. The newest .log
    /// is preferred over today's date because dispose/flush can race on log rollover.
    /// </summary>
    private void OnOpenLatestLogClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            // Flush before reading: AppLogger buffers entries until the day-file rolls
            // over or Dispose() runs. Without this, the user might open a log that's
            // missing the entries that triggered them to click the link.
            _host.Logger.Flush();

            var logDir = _host.Paths.LogsDirectory;
            if (!Directory.Exists(logDir)) return;
            var newest = new DirectoryInfo(logDir).GetFiles("*.log")
                .OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
            if (newest is null) return;
            System.Diagnostics.Process.Start(new ProcessStartInfo("notepad.exe", newest.FullName)
            {
                UseShellExecute = true
            });
        }
        catch { /* best-effort: nothing actionable for the user if notepad can't launch */ }
    }
}
