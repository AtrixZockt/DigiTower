using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DcsAtc.Gui.ViewModels;

namespace DcsAtc.Gui.Views;

/// <summary>The log: follows new lines at the bottom; scrolling up pauses that, Ctrl+C copies the selected lines.</summary>
public partial class LogView : UserControl
{
    private LogViewModel? _vm;
    private ScrollViewer? _scroll;

    public LogView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
        Loaded += (_, _) =>
        {
            Attach();
            _scroll ??= FindScroll(Lines);
            if (_scroll is not null) _scroll.ScrollChanged += OnScrolled;
            ScrollToEnd();
        };
        Unloaded += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.LinesAppended -= OnAppended;
                _vm.PropertyChanged -= OnFollowing;
            }
            if (_scroll is not null) _scroll.ScrollChanged -= OnScrolled;
            _vm = null;
        };
        Lines.KeyDown += (_, e) =>
        {
            if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
            {
                LogViewModel.CopyLines(Lines.SelectedItems.OfType<LogLineViewModel>());
                e.Handled = true;
            }
        };
    }

    private void Attach()
    {
        if (DataContext is not LogViewModel vm || vm == _vm) return;
        if (_vm is not null)
        {
            _vm.LinesAppended -= OnAppended;
            _vm.PropertyChanged -= OnFollowing;
        }
        _vm = vm;
        vm.LinesAppended += OnAppended;
        vm.PropertyChanged += OnFollowing;
    }

    private void OnFollowing(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogViewModel.Following) && _vm?.Following == true) ScrollToEnd();
    }

    private void OnAppended()
    {
        if (_vm?.Following == true) Dispatcher.BeginInvoke(ScrollToEnd, System.Windows.Threading.DispatcherPriority.Background);
    }

    private bool _scrolling;

    private void ScrollToEnd()
    {
        if (Lines.Items.Count == 0) return;
        _scrolling = true;
        Lines.ScrollIntoView(Lines.Items[^1]);
        _scroll?.ScrollToEnd();
        _scrolling = false;
    }

    // The user scrolled up: stop following; back at the bottom: follow again.
    private void OnScrolled(object sender, ScrollChangedEventArgs e)
    {
        if (_vm is null || _scrolling || e.ExtentHeightChange != 0 || e.VerticalChange == 0) return;
        bool atEnd = _scroll!.VerticalOffset >= _scroll.ScrollableHeight - 2;
        if (_vm.Following != atEnd) _vm.Following = atEnd;
    }

    private static ScrollViewer? FindScroll(DependencyObject d)
    {
        if (d is ScrollViewer s) return s;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++)
            if (FindScroll(System.Windows.Media.VisualTreeHelper.GetChild(d, i)) is { } found) return found;
        return null;
    }
}
