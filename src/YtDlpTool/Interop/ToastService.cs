using CommunityToolkit.WinUI.Notifications;

namespace YtDlpTool.Interop;

public static class ToastService
{
    public static void NotifyDownloadCompleted(string title, string outputPath)
    {
        try
        {
            new ToastContentBuilder()
                .AddText("下載完成")
                .AddText(title)
                .AddText(outputPath)
                .AddButton(new ToastButton().SetContent("開啟資料夾").SetProtocolActivation(new Uri("file:///" + System.IO.Path.GetDirectoryName(outputPath)!.Replace('\\', '/'))))
                .Show();
        }
        catch { /* best effort */ }
    }

    public static void NotifyDownloadFailed(string title, string reason)
    {
        try
        {
            new ToastContentBuilder()
                .AddText("下載失敗")
                .AddText(title)
                .AddText(reason)
                .Show();
        }
        catch { }
    }
}
