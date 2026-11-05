using System.IO;
using System.Windows;
using YtDlpTool.Domain.Models;

namespace YtDlpTool.Dialogs;

public partial class FilenameConflictDialog : Window
{
    public FilenameConflictResolution Resolution { get; private set; } = FilenameConflictResolution.Cancel;

    /// <summary>
    /// v1.5.0: the answer is for the other downloads of the same batch too (ConflictDecisions).
    /// Only for a button: closing the window with ✕ skips this one and asks again next time.
    /// </summary>
    public bool ForTheRest => ForTheRestBox.IsChecked == true && _answered;

    private bool _answered;

    /// <param name="offerForTheRest">The download came from a playlist or multi-link batch.</param>
    public FilenameConflictDialog(string conflictingFilePath, bool offerForTheRest = false)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Interop.DarkTitleBar.Apply(this);
        var name = Path.GetFileName(conflictingFilePath);
        HeadlineText.Text = $"「{name}」已經存在";
        PathRun.Text = conflictingFilePath;
        if (offerForTheRest)
        {
            ForTheRestBox.Visibility = Visibility.Visible;
            // In a batch, not downloading this one means moving on to the next.
            CancelButton.Content = "略過";
        }
        Loaded += (_, _) => RenameButton.Focus();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Resolution = FilenameConflictResolution.Cancel;
        _answered = true;
        DialogResult = false;
        Close();
    }

    private void OnOverwrite(object sender, RoutedEventArgs e)
    {
        Resolution = FilenameConflictResolution.Overwrite;
        _answered = true;
        DialogResult = true;
        Close();
    }

    private void OnRename(object sender, RoutedEventArgs e)
    {
        Resolution = FilenameConflictResolution.AutoRename;
        _answered = true;
        DialogResult = true;
        Close();
    }
}
