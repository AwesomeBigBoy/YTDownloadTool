using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using YtDlpTool.Interop;

namespace YtDlpTool.Dialogs;

/// <summary>
/// Dark message dialog with MessageBox.Show's call shape (v1.5.0). Supports OK,
/// OKCancel, YesNo and YesNoCancel. Enter = the affirmative button; Esc / the title-bar ✕
/// = the negative one (Cancel, else No, else OK).
///
/// The fatal-startup and unhandled-exception reports in App.xaml.cs deliberately keep the
/// native MessageBox: they must still work when the theme itself failed to load.
/// </summary>
public partial class AppDialog : Window
{
    private MessageBoxResult _result;
    private readonly MessageBoxResult _dismissResult;

    private AppDialog(string message, string title, MessageBoxButton buttons, MessageBoxImage icon)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);

        // The native MessageBox this replaces copies its text on Ctrl+C, which is how users
        // pass the 詳細說明 text on to IT. Keep that.
        PreviewKeyDown += (_, e) =>
        {
            // v1.5.0: a message longer than the box scrolls from the keyboard; focus stays on
            // the buttons (the scroller takes no Tab stop).
            if (MessageScroll.ScrollableHeight > 0 && Keyboard.Modifiers == ModifierKeys.None)
            {
                switch (e.Key)
                {
                    case Key.Down: MessageScroll.LineDown(); e.Handled = true; return;
                    case Key.Up: MessageScroll.LineUp(); e.Handled = true; return;
                    case Key.PageDown: MessageScroll.PageDown(); e.Handled = true; return;
                    case Key.PageUp: MessageScroll.PageUp(); e.Handled = true; return;
                    case Key.Home: MessageScroll.ScrollToTop(); e.Handled = true; return;
                    case Key.End: MessageScroll.ScrollToBottom(); e.Handled = true; return;
                }
            }
            if (e.Key != Key.C || Keyboard.Modifiers != ModifierKeys.Control) return;
            try { Clipboard.SetText(title + Environment.NewLine + Environment.NewLine + message); }
            catch (System.Runtime.InteropServices.ExternalException) { /* clipboard held by another app */ }
            e.Handled = true;
        };

        var (glyph, brushKey) = icon switch
        {
            MessageBoxImage.Error       => ("", "Brush.Danger"),   // ErrorBadge
            MessageBoxImage.Warning     => ("", "Brush.Warning"),  // Warning
            MessageBoxImage.Question    => ("", "Brush.Focus"),    // StatusCircleQuestionMark
            MessageBoxImage.Information => ("", "Brush.Focus"),    // Info
            _                           => ((string?)null, (string?)null),
        };
        if (glyph is null)
        {
            IconGlyph.Visibility = Visibility.Collapsed;
        }
        else
        {
            IconGlyph.Text = glyph;
            IconGlyph.Foreground = (Brush)FindResource(brushKey!);
        }

        var spec = buttons switch
        {
            MessageBoxButton.OKCancel    => new[] { ("取消", MessageBoxResult.Cancel), ("確定", MessageBoxResult.OK) },
            MessageBoxButton.YesNo       => new[] { ("否", MessageBoxResult.No), ("是", MessageBoxResult.Yes) },
            MessageBoxButton.YesNoCancel => new[] { ("取消", MessageBoxResult.Cancel), ("否", MessageBoxResult.No), ("是", MessageBoxResult.Yes) },
            _                            => new[] { ("確定", MessageBoxResult.OK) },
        };
        _dismissResult = spec[0].Item2;
        _result = _dismissResult;

        var affirmative = spec[^1].Item2;
        foreach (var (label, result) in spec)
        {
            var isPrimary = result == affirmative;
            var b = new Button
            {
                Content = label,
                MinWidth = 88,
                Margin = new Thickness(8, 0, 0, 0),
                Style = (Style)FindResource(isPrimary ? "Button.Primary" : "Button.Secondary"),
                IsDefault = isPrimary,
                // For OK-only the one button is both: Esc closes it, as with the native box.
                IsCancel = result == _dismissResult,
            };
            if (isPrimary) b.Height = 36;
            b.Click += (_, _) => { _result = result; Close(); };
            ButtonRow.Children.Add(b);
            if (isPrimary) Loaded += (_, _) => b.Focus();
        }
    }

    public static MessageBoxResult Show(string message, string title,
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None) =>
        Show(null, message, title, buttons, icon);

    public static MessageBoxResult Show(Window? owner, string message, string title,
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None)
    {
        var app = Application.Current;
        if (app is null) return MessageBox.Show(message, title, buttons, icon);
        if (!app.Dispatcher.CheckAccess())
            return app.Dispatcher.Invoke(() => Show(owner, message, title, buttons, icon));

        owner ??= app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? app.MainWindow;
        var dlg = new AppDialog(message, title, buttons, icon);
        if (owner is not null && owner.IsVisible && !ReferenceEquals(owner, dlg))
            dlg.Owner = owner;
        else
            dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dlg.ShowDialog();
        return dlg._result;
    }
}
