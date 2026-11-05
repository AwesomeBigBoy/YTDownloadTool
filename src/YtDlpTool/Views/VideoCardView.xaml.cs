using System.Windows;
using System.Windows.Controls;
using YtDlpTool.ViewModels;

namespace YtDlpTool.Views;

public partial class VideoCardView : UserControl
{
    public VideoCardView() => InitializeComponent();

    // v1.5.0: offer the rest of the playlist the pasted link came from.
    private void OnPlaylistClicked(object sender, RoutedEventArgs e)
    {
        // The watch page with list= (PlaylistLinks): it also reads Mix lists, which the
        // /playlist page refuses.
        if (DataContext is MainViewModel { CurrentPlaylistUrl: { } url } && Window.GetWindow(this) is MainWindow main)
            _ = main.OpenPlaylistAsync(url);
    }
}
