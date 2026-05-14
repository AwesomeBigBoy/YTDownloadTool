using System.Windows;
using YtDlpTool.Interop;

namespace YtDlpTool;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowChromeHelper.ApplyAuroraBackdrop(this);
    }

    private void OnSettingsClicked(object sender, RoutedEventArgs e)
    {
        // Phase 9 wires this up to SettingsDialog.
        MessageBox.Show("設定對話框將在 Phase 9 完成", "YtDlpTool");
    }
}
