using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using YtDlpTool.Services;

namespace YtDlpTool;

public partial class App : Application
{
    public AppHost? Host { get; private set; }
    public ThemeService ThemeService { get; private set; } = null!;

    // Early-startup crash log path. Populated BEFORE AppHost is constructed so even if
    // AppHost ctor blows up (white UI on one machine, theme/host wiring issues, etc.)
    // we still have a written trail in %TEMP%. Path printed in any fatal MessageBox
    // so the user can grab the file and send it back.
    private string _earlyDiagPath = "";

    protected override void OnStartup(StartupEventArgs e)
    {
        // Set up the early diagnostic path BEFORE anything else so a crash during
        // AppHost / ThemeService / WPF visual tree construction still gets a written
        // record (the regular AppLogger isn't available yet at this point).
        _earlyDiagPath = Path.Combine(Path.GetTempPath(),
            $"YtDlpTool-startup-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");

        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainException;
        TaskScheduler.UnobservedTaskException += OnTaskException;

        WriteEarlyDiag("startup.begin", new Dictionary<string,string>
        {
            ["dotnet"]      = Environment.Version.ToString(),
            ["os"]          = Environment.OSVersion.ToString(),
            ["renderTier"]  = (RenderCapability.Tier >> 16).ToString(),
            ["pixelShader_v3_sw"] = RenderCapability.IsPixelShaderVersionSupportedInSoftware(3, 0).ToString(),
            ["pid"]         = Environment.ProcessId.ToString(),
            ["app_dir"]     = AppContext.BaseDirectory,
            ["session"]     = Environment.UserInteractive ? "interactive" : "non-interactive",
        });

        try
        {
            Host = new AppHost();
            WriteEarlyDiag("startup.apphost.created", null);

            ApplyRenderMode(Host);

            ThemeService = new ThemeService(this);
            ThemeService.Apply(Host.Config.Theme);
            WriteEarlyDiag("startup.theme.applied", new Dictionary<string,string>
            {
                ["theme"] = Host.Config.Theme.ToString()
            });
        }
        catch (Exception ex)
        {
            WriteEarlyDiag("startup.fatal", new Dictionary<string,string>
            {
                ["type"]  = ex.GetType().FullName ?? "",
                ["msg"]   = ex.Message,
                ["stack"] = ex.ToString()
            });
            MessageBox.Show(
                "程式啟動失敗：" + ex.Message +
                "\n\n崩潰報告已存至：\n" + _earlyDiagPath +
                "\n\n請把這個檔案傳給開發者協助分析。",
                "YtDlpTool 啟動失敗", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        base.OnStartup(e);

        WriteEarlyDiag("startup.complete", null);
        _ = Host!.StartBackgroundUpdateCheckAsync(_appShutdown.Token);
    }

    // Render-mode fallback chain — fixes the "white window of doom" on Intel HD/UHD
    // iGPUs (and other machines where WPF hardware rendering produces a blank visual
    // tree despite RenderCapability.Tier reporting > 0). Priority highest → lowest:
    //   1. config.json ForceSoftwareRendering=true (explicit user opt-in)
    //   2. env var YTDLPTOOL_FORCE_SOFTWARE=1 (GPO/batch-deployable)
    //   3. software-render.flag file next to exe (one-touch user workaround, no JSON)
    //   4. RenderCapability.Tier == 0 (WPF itself reports no acceleration)
    //   5. GPU name matches known-bad regex (Intel HD/UHD Graphics ≈ Skylake–Raptor)
    //   6. otherwise: leave hardware rendering on
    private void ApplyRenderMode(AppHost host)
    {
        var gpuNames = DetectGpuNames();
        var gpuFields = new Dictionary<string, string>
        {
            ["count"] = gpuNames.Length.ToString(),
            ["names"] = gpuNames.Length == 0 ? "(none)" : string.Join(" | ", gpuNames),
        };
        WriteEarlyDiag("gpu.detected", gpuFields);
        host.Logger.Info("gpu.detected", gpuFields);

        var flagFile = Path.Combine(AppContext.BaseDirectory, "software-render.flag");
        var envForce = Environment.GetEnvironmentVariable("YTDLPTOOL_FORCE_SOFTWARE");
        var envForcesSoftware = !string.IsNullOrEmpty(envForce)
            && (envForce == "1" || envForce.Equals("true", StringComparison.OrdinalIgnoreCase));

        string reason;
        string? matchedGpu = null;
        if (host.Config.ForceSoftwareRendering)
        {
            reason = "user-config";
        }
        else if (envForcesSoftware)
        {
            reason = "env-var";
        }
        else if (File.Exists(flagFile))
        {
            reason = "flag-file";
        }
        else if ((RenderCapability.Tier >> 16) == 0)
        {
            reason = "tier0";
        }
        else if (TryMatchKnownBadGpu(gpuNames, out matchedGpu))
        {
            reason = "known-bad-gpu";
        }
        else
        {
            reason = "hardware";
        }

        if (reason != "hardware")
        {
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        }

        var renderFields = new Dictionary<string, string>
        {
            ["mode"]   = reason == "hardware" ? "hardware" : "software",
            ["reason"] = reason,
            ["tier"]   = (RenderCapability.Tier >> 16).ToString(),
        };
        if (matchedGpu is not null) renderFields["matched_gpu"] = matchedGpu;
        WriteEarlyDiag("render.mode", renderFields);
        host.Logger.Info("render.mode", renderFields);
    }

    // GPU name regex: Intel HD/UHD Graphics 5XX/6XX/7XX/8XX (Skylake → Raptor Lake).
    // Deliberately excludes Intel Iris Xe / Arc (those don't have the WPF white-window
    // bug) and all NVIDIA/AMD parts. Matches "Intel(R) HD Graphics 630", "Intel(R) UHD
    // Graphics", "Intel(R) UHD Graphics 770" etc.
    private static readonly Regex KnownBadGpuRegex = new(
        @"^Intel\b.*\b(HD|UHD)\s*Graphics\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool TryMatchKnownBadGpu(IReadOnlyList<string> names, out string? matched)
    {
        foreach (var n in names)
        {
            if (KnownBadGpuRegex.IsMatch(n))
            {
                matched = n;
                return true;
            }
        }
        matched = null;
        return false;
    }

    // Read GPU adapter names from the Windows registry instead of WMI to avoid pulling
    // in System.Management (extra package, slow first-call). Each child key under the
    // display-adapter class GUID is a 4-digit index whose DriverDesc is the vendor name.
    private static string[] DetectGpuNames()
    {
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (root is null) return Array.Empty<string>();
            var names = new List<string>();
            foreach (var subName in root.GetSubKeyNames())
            {
                if (subName.Length != 4 || !subName.All(char.IsDigit)) continue;
                using var sub = root.OpenSubKey(subName);
                if (sub?.GetValue("DriverDesc") is string desc && !string.IsNullOrWhiteSpace(desc))
                    names.Add(desc);
            }
            return names.ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    // v1.3.0: shared session-scope flag so we only pop the "enable bypass and
    // restart?" prompt once per launch even if multiple downloads fail in a row.
    // User can still manually flip the Settings checkbox if they dismiss the
    // first prompt and change their mind.
    private static bool _bypassPromptShownThisSession;

    /// <summary>
    /// Offers the user the choice to enable AllowUntrustedCertificates and
    /// auto-restart, after a URL parse or download failed with an SSL-related
    /// error code. Returns true if the user accepted (restart in progress);
    /// returns false if the user dismissed the prompt or the flag was already
    /// enabled (no further action). Safe to call from any UI thread.
    /// </summary>
    public static bool OfferSslBypassPrompt(AppHost host, string contextLeadIn)
    {
        if (host.Config.AllowUntrustedCertificates) return false;
        if (_bypassPromptShownThisSession) return false;
        _bypassPromptShownThisSession = true;

        var msg = contextLeadIn +
                  "\n\n" +
                  "可能是目前的網路會檢查 HTTPS 流量。是否要啟用「允許不受信任憑證」並重新啟動程式？" +
                  "\n\n" +
                  "⚠️ 啟用後 YtDlpTool 會接受任何 HTTPS 憑證 — " +
                  "不檢查發行單位、不檢查金鑰長度、不檢查主機名是否相符。" +
                  "請只在你完全信任當前網路時啟用（例如自己的家用網路）。" +
                  "連到公共 wifi（咖啡廳、機場、飯店等）會有遭中間人攔截的風險。";

        var result = MessageBox.Show(msg,
            "下載失敗：HTTPS 驗證問題",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return false;

        host.Config.AllowUntrustedCertificates = true;
        host.ConfigStore.Save(host.Config);
        host.Logger.Info("config.changed", new Dictionary<string, string>
        {
            ["key"]              = "AllowUntrustedCertificates",
            ["old_value"]        = "False",
            ["new_value"]        = "True",
            ["trigger"]          = "ssl_failure_prompt",
            ["takes_effect_at"]  = "next_restart",
        });
        RestartApplication();
        return true;
    }

    /// <summary>
    /// Confirms the user wants to DISABLE AllowUntrustedCertificates. v1.3.2
    /// added this because disabling also takes effect only after restart —
    /// silently restarting without warning was startling, and users had no
    /// last-chance-to-cancel button. Returns true if user accepted.
    /// </summary>
    public static bool ConfirmDisableUntrustedFromSettings()
    {
        var msg = "你正在關閉「允許不受信任憑證」。\n\n" +
                  "關閉後 YtDlpTool 會恢復完整的 HTTPS 憑證驗證。在某些網路下" +
                  "（會檢查或攔截 HTTPS 流量的環境）可能因此無法下載 YouTube — " +
                  "如果你之後遇到下載失敗，可以再回到這個設定打開。" +
                  "\n\n" +
                  "確定要關閉嗎？確認後程式會自動重新啟動讓設定生效。";
        var result = MessageBox.Show(msg,
            "確認關閉降級驗證",
            MessageBoxButton.OKCancel, MessageBoxImage.Question);
        return result == MessageBoxResult.OK;
    }

    /// <summary>
    /// Confirms the user wants to enable AllowUntrustedCertificates from the
    /// Settings dialog (different message wording than the failure-driven
    /// prompt). Returns true if user accepted; caller should then save the
    /// config and trigger a restart.
    /// </summary>
    public static bool ConfirmEnableUntrustedFromSettings()
    {
        var msg = "你正在啟用「允許不受信任憑證」。" +
                  "\n\n" +
                  "⚠️ 啟用後 YtDlpTool 會接受任何 HTTPS 憑證 — " +
                  "不檢查發行單位、不檢查金鑰長度、不檢查主機名是否相符。" +
                  "\n\n" +
                  "請只在你完全信任當前網路時啟用。" +
                  "連到公共 wifi（咖啡廳、機場、飯店等）會有遭中間人攔截或竄改你下載內容的風險。" +
                  "\n\n" +
                  "確定要啟用嗎？確認後程式會自動重新啟動讓設定生效。";
        var result = MessageBox.Show(msg,
            "確認啟用降級驗證",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        return result == MessageBoxResult.OK;
    }

    /// <summary>
    /// Closes this YtDlpTool process and launches a new instance from the same
    /// path. Used to apply settings (specifically AllowUntrustedCertificates)
    /// that are captured at YtDlpRunner construction time, which happens once
    /// in AppHost ctor at startup.
    /// </summary>
    public static void RestartApplication()
    {
        // ProcessPath is the only reliable way to get the running .exe path under
        // a single-file self-contained WPF app on .NET 8. Assembly.Location returns
        // empty for embedded assemblies, so don't fall back to that.
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath)) return;
        try
        {
            // Fully-qualify against YtDlpTool.Process project namespace (which
            // also lives in this file's `using` scope).
            System.Diagnostics.Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
            });
        }
        catch
        {
            // best-effort — if we can't relaunch, at least don't crash the
            // current process while shutting down.
        }
        Current?.Shutdown();
    }

    private void WriteEarlyDiag(string category, Dictionary<string,string>? fields)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("HH:mm:ss.fff"));
            sb.Append(' ').Append(category);
            if (fields is not null)
            {
                foreach (var kv in fields)
                {
                    sb.Append(' ').Append(kv.Key).Append('=');
                    var v = kv.Value;
                    if (v.IndexOfAny(new[] { ' ', '\t', '"', '\n', '\r' }) >= 0)
                        v = "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " | ").Replace("\r", "") + "\"";
                    sb.Append(v);
                }
            }
            sb.AppendLine();
            File.AppendAllText(_earlyDiagPath, sb.ToString());
        }
        catch { /* best effort — early diag must never throw */ }
    }

    private readonly CancellationTokenSource _appShutdown = new();

    protected override void OnExit(ExitEventArgs e)
    {
        _appShutdown.Cancel();
        Host?.Dispose();
        _appShutdown.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteEarlyDiag("unhandled.dispatcher", new Dictionary<string,string>
        {
            ["type"]  = e.Exception.GetType().FullName ?? "",
            ["msg"]   = e.Exception.Message,
            ["stack"] = e.Exception.ToString()
        });
        Host?.Logger.Error("unhandled.dispatcher", new Dictionary<string, string>
        {
            ["type"] = e.Exception.GetType().Name,
            ["msg"]  = e.Exception.Message
        });
        Host?.Logger.Flush();
        MessageBox.Show(
            "程式遇到了未預期的問題：" + e.Exception.Message +
            "\n\n詳細錯誤記錄已存至：\n" + _earlyDiagPath,
            "YtDlpTool", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
        Shutdown(1);
    }

    private void OnDomainException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            WriteEarlyDiag("unhandled.domain", new Dictionary<string,string>
            {
                ["type"]  = ex.GetType().FullName ?? "",
                ["msg"]   = ex.Message,
                ["stack"] = ex.ToString()
            });
            Host?.Logger.Error("unhandled.domain", new Dictionary<string,string>
            {
                ["type"] = ex.GetType().Name, ["msg"] = ex.Message
            });
        }
        else
        {
            WriteEarlyDiag("unhandled.domain", new Dictionary<string,string> { ["msg"] = "non-exception object" });
        }
        Host?.Logger.Flush();
    }

    private void OnTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteEarlyDiag("unhandled.task", new Dictionary<string,string>
        {
            ["msg"]   = e.Exception.Message,
            ["stack"] = e.Exception.ToString()
        });
        Host?.Logger.Warn("unhandled.task", new Dictionary<string, string>
        {
            ["msg"] = e.Exception.Message
        });
        e.SetObserved();
    }
}
