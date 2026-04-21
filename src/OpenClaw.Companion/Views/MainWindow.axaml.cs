using Avalonia.Controls;
using Avalonia.VisualTree;
using OpenClaw.Companion.ViewModels;

namespace OpenClaw.Companion.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        Activated += (_, _) => PushWindowActive(true);
        Deactivated += (_, _) => PushWindowActive(false);

        PropertyChanged += (_, args) =>
        {
            if (args.Property == WindowStateProperty)
                PushWindowMinimized(WindowState == WindowState.Minimized);
        };

        DataContextChanged += (_, _) =>
        {
            PushWindowActive(IsActive);
            PushWindowMinimized(WindowState == WindowState.Minimized);
            AttachTabSelectionListener();
        };
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
        // The MainWindow layout contains exactly one TabControl.
        var tabControl = this.FindDescendantOfType<TabControl>();
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
