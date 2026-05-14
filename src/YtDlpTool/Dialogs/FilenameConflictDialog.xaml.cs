using System.IO;
using System.Windows;

namespace YtDlpTool.Dialogs;

public partial class FilenameConflictDialog : Window
{
    public FilenameConflictResolution Resolution { get; private set; } = FilenameConflictResolution.Cancel;

    public FilenameConflictDialog(string conflictingFilePath)
    {
        InitializeComponent();
        var name = Path.GetFileName(conflictingFilePath);
        HeadlineText.Text = $"「{name}」已經存在";
        PathRun.Text = conflictingFilePath;
        Loaded += (_, _) => RenameButton.Focus();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Resolution = FilenameConflictResolution.Cancel;
        DialogResult = false;
        Close();
    }

    private void OnOverwrite(object sender, RoutedEventArgs e)
    {
        Resolution = FilenameConflictResolution.Overwrite;
        DialogResult = true;
        Close();
    }

    private void OnRename(object sender, RoutedEventArgs e)
    {
        Resolution = FilenameConflictResolution.AutoRename;
        DialogResult = true;
        Close();
    }
}
