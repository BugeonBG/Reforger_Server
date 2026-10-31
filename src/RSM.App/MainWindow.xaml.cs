using System.Collections.Specialized;
using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RSM.Core;

namespace RSM.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _scrollQueued;
    private bool _followLog = true;     // keep the newest line in view (off while the user reads older lines)
    private ScrollViewer? _logScroll;
    private ICollectionView? _logView;
    private bool _consoleExpanded;
    private double _consoleHeightBeforeExpand;

    public MainWindow()
    {
        InitializeComponent();

        _vm = new MainViewModel(new WpfUiServices(this), new DispatcherSynchronizationContext(Dispatcher), AppSettingsStore.Default());
        DataContext = _vm;

        // first run: start on the install page instead of settings
        if (!_vm.ServerInstalled)
            NavInstall.IsChecked = true;

        ApplySavedLayout();

        _logView = CollectionViewSource.GetDefaultView(_vm.LogLines);
        ApplyLogFilter();
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.LogLevel) or nameof(MainViewModel.LogSearch))
                ApplyLogFilter();
        };
        LogList.Loaded += (_, _) =>
        {
            _logScroll = FindChild<ScrollViewer>(LogList);
            if (_logScroll != null)
                _logScroll.ScrollChanged += OnLogScrolled;
        };

        _vm.LogLines.CollectionChanged += OnLogChanged;
        // the "서버 현황" numbers refresh every 2 s; start once the window (and its dispatcher) is up
        Loaded += async (_, _) =>
        {
            _vm.Monitor.Start();
            // find the server if SteamCMD put it elsewhere, then check Steam for a newer build
            await _vm.OnStartupAsync();
        };
        Closing += OnClosing;
    }

    // Keep the newest log line in view. This must not touch the ListBox inside CollectionChanged:
    // our handler can run before the ListBox has seen the new line, and asking it to scroll then
    // throws "ItemsControl is inconsistent with its items source". So scroll once things settle,
    // and only once per burst of lines (SteamCMD prints many lines at a time).
    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_scrollQueued)
            return;

        _scrollQueued = true;
        Dispatcher.InvokeAsync(() =>
        {
            _scrollQueued = false;
            UpdateLogCount();
            if (_followLog)
                ScrollLogToEnd();
        }, DispatcherPriority.Background);
    }

    private void ScrollLogToEnd()
    {
        if (LogList.Items.Count > 0)
            LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
    }

    // the user scrolled: follow new lines only while the view is at the bottom
    private void OnLogScrolled(object sender, ScrollChangedEventArgs e)
    {
        if (_logScroll == null)
            return;
        // ignore scrolls caused by lines arriving/being trimmed; only the position matters
        var atEnd = _logScroll.ScrollableHeight <= 0 || _logScroll.VerticalOffset >= _logScroll.ScrollableHeight - 1;
        if (e.ExtentHeightChange == 0 || atEnd)
            _followLog = atEnd;
        ToBottomButton.Visibility = _followLog ? Visibility.Collapsed : Visibility.Visible;
    }

    // the explanation panel follows the last line the user clicked
    private void OnLogSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LogList.SelectedItems.Count == 0)
            _vm.SelectedLogLine = null;
        else if (e.AddedItems.Count > 0 && e.AddedItems[e.AddedItems.Count - 1] is string added)
            _vm.SelectedLogLine = added;
        else if (LogList.SelectedItem is string first)
            _vm.SelectedLogLine = first;
    }

    private void OnLogHelpBack(object sender, RoutedEventArgs e) => LogList.UnselectAll();

    //------------------------------------------------------------------ 서버 현황 → 접속 문제
    private void OnCopyPlayerMessage(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string text || text.Length == 0)
            return;
        try
        {
            Clipboard.SetText(text);
            _vm.SetStatus("안내문을 복사했습니다. 디스코드나 카톡에 붙여 넣어 보내세요.");
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // clipboard busy; try again
        }
    }

    private void OnClearConnectionIssues(object sender, RoutedEventArgs e) => _vm.Monitor.ClearConnectionIssues();

    private void OnScrollToBottom(object sender, RoutedEventArgs e)
    {
        _followLog = true;
        ToBottomButton.Visibility = Visibility.Collapsed;
        LogList.UnselectAll();
        ScrollLogToEnd();
    }

    //------------------------------------------------------------------ console filter
    private void ApplyLogFilter()
    {
        if (_logView == null)
            return;
        var everything = _vm.LogLevel == LogLevelFilter.All && string.IsNullOrWhiteSpace(_vm.LogSearch);
        if (everything)
            _logView.Filter = null;
        else
            _logView.Filter = o => o is string line && _vm.ShowLogLine(line);
        UpdateLogCount();
        _followLog = true;
        ToBottomButton.Visibility = Visibility.Collapsed;
        Dispatcher.InvokeAsync(ScrollLogToEnd, DispatcherPriority.Background);
    }

    private void UpdateLogCount()
    {
        var total = _vm.LogLines.Count;
        var shown = LogList.Items.Count;
        LogCountText.Text = shown == total ? $"{total:N0}줄" : $"{total:N0}줄 중 {shown:N0}줄";
    }

    //------------------------------------------------------------------ console tools
    private void OnConsoleSmaller(object sender, RoutedEventArgs e) => _vm.ConsoleFontSize -= 1;
    private void OnConsoleBigger(object sender, RoutedEventArgs e) => _vm.ConsoleFontSize += 1;

    // Ctrl + wheel on the console = text size
    private void OnLogWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
            return;
        _vm.ConsoleFontSize += e.Delta > 0 ? 1 : -1;
        e.Handled = true;
    }

    private void OnLogKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            CopyLog();
            e.Handled = true;
        }
        else if (e.Key == Key.A && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            LogList.SelectAll();
            e.Handled = true;
        }
    }

    private void OnCopyLog(object sender, RoutedEventArgs e) => CopyLog();

    // selected lines in screen order; nothing selected = everything currently shown
    private void CopyLog()
    {
        string text;
        if (LogList.SelectedItems.Count > 0)
        {
            var sb = new StringBuilder();
            foreach (var item in LogList.Items)
                if (LogList.SelectedItems.Contains(item))
                    sb.AppendLine(item as string);
            text = sb.ToString();
        }
        else
        {
            text = _vm.VisibleLogText();
        }
        if (text.Length == 0)
            return;
        try
        {
            Clipboard.SetText(text);
            _vm.SetStatus(LogList.SelectedItems.Count > 0 ? $"고른 {LogList.SelectedItems.Count}줄을 복사했습니다" : "보이는 로그를 복사했습니다");
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // clipboard busy (another program holds it); nothing to do
        }
    }

    // console fills the window / back to normal
    private void OnToggleConsoleSize(object sender, RoutedEventArgs e)
    {
        _consoleExpanded = !_consoleExpanded;
        if (_consoleExpanded)
        {
            _consoleHeightBeforeExpand = ConsoleRow.ActualHeight;
            PageRow.MinHeight = 0;
            PageRow.Height = new GridLength(0);
            ConsoleRow.Height = new GridLength(1, GridUnitType.Star);
            ConsoleSplitter.Visibility = Visibility.Collapsed;
            ExpandButton.Content = "원래대로";
        }
        else
        {
            PageRow.MinHeight = 200;
            PageRow.Height = new GridLength(1, GridUnitType.Star);
            ConsoleRow.Height = new GridLength(_consoleHeightBeforeExpand);
            ConsoleSplitter.Visibility = Visibility.Visible;
            ExpandButton.Content = "크게 보기";
        }
        if (_followLog)
            Dispatcher.InvokeAsync(ScrollLogToEnd, DispatcherPriority.Background);
    }

    //------------------------------------------------------------------ window size, remembered between runs
    private void ApplySavedLayout()
    {
        var area = SystemParameters.WorkArea;
        // small screens (e.g. 1366x768 laptops): never open bigger than the screen
        MinWidth = Math.Min(MinWidth, area.Width);
        MinHeight = Math.Min(MinHeight, area.Height);
        Width = Math.Min(_vm.WindowWidth > 0 ? _vm.WindowWidth : 1440, area.Width - 24);
        Height = Math.Min(_vm.WindowHeight > 0 ? _vm.WindowHeight : 900, area.Height - 24);
        if (_vm.WindowMaximized)
            WindowState = WindowState.Maximized;
        ConsoleRow.Height = new GridLength(Math.Clamp(_vm.ConsoleHeight, 120, Math.Max(120, Height - 300)));
    }

    private void RememberLayout()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (bounds.Width > 0 && bounds.Height > 0)
        {
            _vm.WindowWidth = Math.Round(bounds.Width);
            _vm.WindowHeight = Math.Round(bounds.Height);
        }
        _vm.WindowMaximized = WindowState == WindowState.Maximized;
        _vm.ConsoleHeight = Math.Round(_consoleExpanded ? _consoleHeightBeforeExpand : ConsoleRow.ActualHeight);
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found)
                return found;
            if (FindChild<T>(child) is { } deeper)
                return deeper;
        }
        return null;
    }

    // the workshop page loads the first time it is opened
    private void OnBrowseOpened(object sender, RoutedEventArgs e) => _ = _vm.Browser.EnsureLoadedAsync();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_vm.ConfirmClose())
        {
            e.Cancel = true;
            return;
        }
        RememberLayout();
        _vm.Shutdown();
    }

    // title bar buttons (the window draws its own title bar)
    private void OnMinimize(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void OnMaximize(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(this);
        else
            SystemCommands.MaximizeWindow(this);
    }

    private void OnClose(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);
}
