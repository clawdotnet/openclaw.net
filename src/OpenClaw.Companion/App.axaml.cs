using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
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
            // Avoid duplicate validations from both Avalonia and the CommunityToolkit. 
            // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
            DisableAvaloniaDataAnnotationValidation();

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

    private void DisableAvaloniaDataAnnotationValidation()
    {
        // Get an array of plugins to remove
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        // remove each entry found
        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}
