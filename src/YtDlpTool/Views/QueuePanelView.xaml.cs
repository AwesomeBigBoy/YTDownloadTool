using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
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

    private void UpdateCount(int n) => CountRun.Text = $" ({n})";
}
