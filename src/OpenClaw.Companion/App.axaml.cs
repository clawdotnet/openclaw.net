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

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _client = new GatewayWebSocketClient();
            var settings = new SettingsStore();
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(settings, _client),
            };

            desktop.Exit += async (_, _) =>
            {
                if (_client is not null)
                    await _client.DisposeAsync();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}