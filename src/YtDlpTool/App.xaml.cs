using System.Windows;
using System.Windows.Threading;

namespace YtDlpTool;

public partial class App : Application
{
    public AppHost? Host { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainException;
        TaskScheduler.UnobservedTaskException += OnTaskException;

        Host = new AppHost();
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Host?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Host?.Logger.Error("unhandled.dispatcher", new Dictionary<string, string>
        {
            ["type"] = e.Exception.GetType().Name,
            ["msg"]  = e.Exception.Message
        });
        MessageBox.Show("程式遇到了未預期的問題，已保存錯誤記錄。按確定關閉。",
            "YtDlpTool", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
        Shutdown(1);
    }

    private void OnDomainException(object sender, UnhandledExceptionEventArgs e) =>
        Host?.Logger.Error("unhandled.domain",
            e.ExceptionObject is Exception ex
                ? new Dictionary<string, string> { ["type"] = ex.GetType().Name, ["msg"] = ex.Message }
                : new Dictionary<string, string> { ["msg"] = "non-exception object" });

    private void OnTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Host?.Logger.Warn("unhandled.task", new Dictionary<string, string>
        {
            ["msg"] = e.Exception.Message
        });
        e.SetObserved();
    }
}
