using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using YtDlpTool.Interop;
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
        // v1.1.26: allocate a hidden console for the parent process BEFORE
        // anything else can spawn a child. Child processes (yt-dlp, ffmpeg)
        // launched without CREATE_NO_WINDOW inherit this console, so they
        // see real TTY stdio (their isatty probe returns true and
        // GetConsoleWindow returns a non-zero hwnd) without a window
        // flashing up for the user. v1.1.23-v1.1.25 had a visible 1-2s
        // console flash per yt-dlp spawn; this removes it without sacrificing
        // the TTY mode that satisfies endpoint web filtering in AD envs.
        HiddenConsole.Allocate();

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

            // White-UI on a single machine workaround: if Config.ForceSoftwareRendering
            // is true (set by user editing config.json), bypass GPU rendering entirely.
            // Some managed / RDP-session / older-Intel-iGPU machines render the WPF
            // visual tree as blank in hardware mode despite WPF reporting Tier > 0.
            if (Host.Config.ForceSoftwareRendering)
            {
                RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
                WriteEarlyDiag("render.mode", new Dictionary<string,string> { ["mode"] = "software-forced" });
                Host.Logger.Info("render.mode", new Dictionary<string,string> { ["mode"] = "software-forced" });
            }
            else if ((RenderCapability.Tier >> 16) == 0)
            {
                // WPF itself reports no hardware acceleration — auto-fall-back.
                RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
                WriteEarlyDiag("render.mode", new Dictionary<string,string> { ["mode"] = "software-auto" });
                Host.Logger.Info("render.mode", new Dictionary<string,string> { ["mode"] = "software-auto" });
            }
            else
            {
                Host.Logger.Info("render.mode", new Dictionary<string,string>
                {
                    ["mode"] = "hardware",
                    ["tier"] = (RenderCapability.Tier >> 16).ToString()
                });
            }

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
        HiddenConsole.Free();
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
