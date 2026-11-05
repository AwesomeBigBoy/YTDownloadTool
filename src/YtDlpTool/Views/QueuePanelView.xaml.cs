using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using YtDlpTool.ViewModels;

namespace YtDlpTool.Views;

public partial class QueuePanelView : UserControl
{
    public QueuePanelView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel oldVm) oldVm.Queue.CollectionChanged -= OnCollectionChanged;
        if (e.NewValue is MainViewModel vm)
        {
            vm.Queue.CollectionChanged += OnCollectionChanged;
            UpdateCount(vm.Queue.Count);
        }
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (DataContext is MainViewModel vm) UpdateCount(vm.Queue.Count);
    }

    // v1.5.0: Delete on a row does what its ✕ does — 移除, or 取消下載 while it runs.
    private void OnQueueKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || Keyboard.Modifiers != ModifierKeys.None) return;
        if (QueueItems.SelectedItem is not QueueItemViewModel item || DataContext is not MainViewModel vm) return;
        vm.CancelJobCommand.Execute(item.Id);
        e.Handled = true;
    }

    private void UpdateCount(int n)
    {
        CountText.Text = n.ToString();
        EmptyState.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearDoneButton.Visibility = n == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
}
