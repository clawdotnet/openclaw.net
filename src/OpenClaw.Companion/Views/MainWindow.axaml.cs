using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using System.ComponentModel;
using OpenClaw.Companion.ViewModels;

namespace OpenClaw.Companion.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _observedViewModel;
    private bool _followChat = true;
    public MainWindow()
    {
        InitializeComponent();

        Activated += (_, _) => PushWindowActive(true);
        Deactivated += (_, _) => PushWindowActive(false);

        PropertyChanged += (_, args) =>
        {
            if (args.Property == BoundsProperty)
                Classes.Set("compact", Bounds.Width < 1200 || Bounds.Height < 850);
            if (args.Property == WindowStateProperty)
                PushWindowMinimized(WindowState == WindowState.Minimized);
        };

        DataContextChanged += (_, _) =>
        {
            if (_observedViewModel is not null)
                _observedViewModel.PropertyChanged -= OnViewModelChanged;
            _observedViewModel = DataContext as MainWindowViewModel;
            if (_observedViewModel is not null)
            {
                _observedViewModel.PropertyChanged += OnViewModelChanged;
                ApplyTheme();
            }
            PushWindowActive(IsActive);
            PushWindowMinimized(WindowState == WindowState.Minimized);
            AttachTabSelectionListener();
        };
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        // Intercept Enter before the TextBox editing/default-button routing.
        ChatComposer.AddHandler(KeyDownEvent, OnComposerKeyDown, RoutingStrategies.Tunnel);
        NavigationSearchBox.AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel);
        SearchResults.AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel);
        Closed += (_, _) =>
        {
            if (_observedViewModel is not null)
                _observedViewModel.PropertyChanged -= OnViewModelChanged;
        };
    }

    private void ApplyTheme() => RequestedThemeVariant = _observedViewModel?.IsDarkTheme == true
        ? ThemeVariant.Dark : ThemeVariant.Light;

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsDarkTheme)) ApplyTheme();
        if (e.PropertyName == nameof(MainWindowViewModel.IsCommandPaletteOpen))
            Dispatcher.UIThread.Post(() =>
            {
                if (_observedViewModel?.IsCommandPaletteOpen == true) NavigationSearchBox.Focus();
                else SearchNavigationButton.Focus();
            });
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (e.Key == Key.K && (e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control)))
        {
            vm.OpenCommandPaletteCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && vm.IsCommandPaletteOpen)
        {
            vm.CloseCommandPaletteCommand.Execute(null);
            e.Handled = true;
        }

    }

    private void OnComposerKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None) return;
        e.Handled = true;
        if (DataContext is MainWindowViewModel vm && vm.SendCommand.CanExecute(null))
        {
            _followChat = true;
            vm.SendCommand.Execute(null);
        }
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (e.Key == Key.Enter) { vm.OpenSearchResultCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Down && sender == NavigationSearchBox) { SearchResults.Focus(); e.Handled = true; }
    }

    private void OnSearchResultActivated(object? sender, TappedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) vm.OpenSearchResultCommand.Execute(null);
    }

    private void OnChatScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer scroll) return;
        if (e.ExtentDelta.Y != 0 && _followChat)
            scroll.ScrollToEnd();
        else if (e.OffsetDelta.Y != 0)
            _followChat = scroll.Extent.Height - scroll.Viewport.Height - scroll.Offset.Y < 48;
    }

    private void PushWindowActive(bool active)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.IsWindowActive = active;
    }

    private void PushWindowMinimized(bool minimized)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.IsWindowMinimized = minimized;
    }

    private void AttachTabSelectionListener()
    {
        var tabControl = this.FindControl<TabControl>("SectionHost");
        if (tabControl is null || DataContext is not MainWindowViewModel vm)
            return;

        UpdateApprovalsTabActive(tabControl, vm);
        tabControl.SelectionChanged -= OnTabSelectionChanged;
        tabControl.SelectionChanged += OnTabSelectionChanged;
    }

    private void OnTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is TabControl tabControl && DataContext is MainWindowViewModel vm)
            UpdateApprovalsTabActive(tabControl, vm);
    }

    private void UpdateApprovalsTabActive(TabControl tabControl, MainWindowViewModel vm)
    {
        var approvalsTab = this.FindControl<TabItem>("ApprovalsTab");
        vm.IsApprovalsTabActive = approvalsTab is not null && tabControl.SelectedItem == approvalsTab;
    }
}
