using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using OpenClaw.Companion.ViewModels;
using OpenClaw.Companion.Views;
using OpenClaw.Companion.Services;

namespace OpenClaw.Companion;

public partial class App : Application
{
    private GatewayWebSocketClient? _client;
    private ManagedGatewayService? _managedGateway;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _client = new GatewayWebSocketClient();
            _managedGateway = new ManagedGatewayService();
            var settings = new SettingsStore();
            var viewModel = new MainWindowViewModel(settings, _client, managedGateway: _managedGateway);
            viewModel.AttachDesktopNotifier(new DesktopNotifier());

            var mainWindow = new MainWindow
            {
                DataContext = viewModel,
            };
            viewModel.AttachConfirmationDialogService(new WindowConfirmationDialogService(mainWindow));
            desktop.MainWindow = mainWindow;

            viewModel.StartApprovalsPolling();
            var initializeLocalGatewayTask = viewModel.InitializeLocalGatewayAsync();
            _ = initializeLocalGatewayTask.ContinueWith(
                task => viewModel.ReportLocalGatewayInitializationFailure(task.Exception),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            desktop.Exit += async (_, _) =>
            {
                viewModel.StopApprovalsPolling();
                if (_client is not null)
                    await _client.DisposeAsync();
                if (_managedGateway is not null)
                    await _managedGateway.DisposeAsync();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
