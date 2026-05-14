using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace YtDlpTool.Views;

public partial class AuroraBackground : UserControl
{
    public AuroraBackground()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var sb = (Storyboard)Resources["AuroraStoryboard"];
        sb.Begin(this, isControllable: true);
    }
}
