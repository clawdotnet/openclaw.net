using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using OpenClaw.Companion.Services;
using OpenClaw.Companion.ViewModels;
using OpenClaw.Companion.Views;
using OpenClaw.Core.Setup;
using Xunit;

namespace OpenClaw.Tests;

public sealed class CompanionSetupUiTests
{
    [AvaloniaFact]
    public void SetupPresetBinding_RetainsToolCapableOllamaPreset()
    {
        var directory = Path.Combine(Path.GetTempPath(), "companion-setup-tests", Guid.NewGuid().ToString("N"));
        var store = new SettingsStore(directory);
        var viewModel = new MainWindowViewModel(store, new GatewayWebSocketClient());
        var window = new MainWindow { DataContext = viewModel };
        try
        {
            window.Show();
            viewModel.SetupProvider = "ollama";
            viewModel.SetupModelPreset = "ollama-agentic";
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("ollama-agentic", viewModel.SetupModelPreset);
            Assert.Equal("ollama-agentic", store.Load().SetupModelPreset);
            Assert.All(viewModel.SetupModelPresetOptions, id =>
                Assert.True(LocalModelPresetCatalog.TryGet(id, out _), $"Setup offers unknown preset '{id}'."));
        }
        finally
        {
            window.Close();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
