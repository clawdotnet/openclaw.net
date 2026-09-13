using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenClaw.Companion;
using OpenClaw.Companion.Services;
using OpenClaw.Companion.ViewModels;
using OpenClaw.Companion.Views;
using OpenClaw.Core.Canvas;
using OpenClaw.Core.Models;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(OpenClaw.Tests.CompanionAvaloniaTestApp))]

namespace OpenClaw.Tests;

public sealed class CompanionAvaloniaTestApp
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public sealed class CompanionCanvasUiTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { }
        }
    }

    [AvaloniaFact]
    public async Task MainWindow_RendersSurfaceSelectorAndActiveSurfaceComponents()
    {
        var viewModel = CreateViewModel();
        await ApplyCanvasEnvelopeAsync(viewModel, CreateSurfaceEnvelope("alpha", "Alpha", [TextComponent("alpha-text", "Alpha ready")]));
        await ApplyCanvasEnvelopeAsync(viewModel, CreateSurfaceEnvelope("beta", "Beta", [ButtonComponent("beta-save", "Save beta")]));

        var window = new MainWindow
        {
            Width = 900,
            Height = 600,
            DataContext = viewModel
        };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var tabControl = window.GetVisualDescendants().OfType<TabControl>().Single();
            var canvasTab = GetTabByHeader(tabControl, "Canvas");
            tabControl.SelectedItem = canvasTab;
            Dispatcher.UIThread.RunJobs();
            Assert.Same(canvasTab, tabControl.SelectedItem);

            var selector = window.FindControl<ComboBox>("CanvasSurfaceSelector");
            Assert.NotNull(selector);
            Assert.True(selector!.IsVisible);
            Assert.Equal(viewModel.CanvasSurfaces, selector.ItemsSource);
            Assert.Equal(viewModel.ActiveCanvasSurface, selector.SelectedItem);
            Assert.Contains(viewModel.CanvasSurfaces, surface => surface.Title == "Alpha");
            Assert.Contains(viewModel.CanvasSurfaces, surface => surface.Title == "Beta");

            var componentHost = window.FindControl<ItemsControl>("CanvasComponentHost");
            Assert.NotNull(componentHost);
            Assert.Equal(viewModel.ActiveCanvasSurface!.Components, componentHost!.ItemsSource);
            Assert.Contains(window.GetVisualDescendants().OfType<Button>(), button => string.Equals(button.Content?.ToString(), "Save beta", StringComparison.Ordinal));

            selector.SelectedItem = viewModel.CanvasSurfaces.Single(surface => surface.SurfaceId == "alpha");
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("alpha", viewModel.ActiveCanvasSurface?.SurfaceId);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => string.Equals(text.Text, "Alpha ready", StringComparison.Ordinal));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void MainWindow_RendersRuntimeConsoleShellAndNavigationSections()
    {
        var viewModel = CreateViewModel();
        var window = new MainWindow
        {
            Width = 1100,
            Height = 760,
            DataContext = viewModel
        };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var tabControl = window.GetVisualDescendants().OfType<TabControl>().Single();
            Assert.Equal(Dock.Left, tabControl.TabStripPlacement);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => string.Equals(text.Text, "AgentQi Companion", StringComparison.Ordinal));

            var headers = tabControl.Items.OfType<TabItem>().Select(static item => item.Header?.ToString()).ToArray();
            Assert.Contains("Home", headers);
            Assert.Contains("Sessions", headers);
            Assert.Contains("Runtime Events", headers);
            Assert.Contains("Plugins & Channels", headers);
            Assert.Contains("Payment Lab", headers);

            var sessionsTab = GetTabByHeader(tabControl, "Sessions");
            tabControl.SelectedItem = sessionsTab;
            Dispatcher.UIThread.RunJobs();

            Assert.Same(sessionsTab, tabControl.SelectedItem);
            Assert.Equal(Array.IndexOf(headers, "Sessions"), viewModel.SelectedSectionIndex);
            Assert.Same(sessionsTab.Content, tabControl.SelectedContent);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Navigation_AllFeaturePagesRemainReachable_AndApprovalsTrackSelection()
    {
        var vm = CreateViewModel();
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, vm.SelectedSectionIndex);
            var host = window.FindControl<TabControl>("SectionHost")!;
            var navigation = window.FindControl<ListBox>("PrimaryNavigation")!;
            var sections = window.FindControl<ListBox>("SectionNavigation")!;
            Assert.Equal(15, vm.NavigationGroups.SelectMany(g => g.Sections).Select(s => s.Index).Distinct().Count());
            foreach (var group in vm.NavigationGroups)
            {
                navigation.SelectedItem = group;
                Dispatcher.UIThread.RunJobs();
                foreach (var section in group.Sections)
                {
                    sections.SelectedItem = section;
                    Dispatcher.UIThread.RunJobs();
                    Assert.Equal(section.Index, host.SelectedIndex);
                    Assert.Equal(section.Index == 5, vm.IsApprovalsTabActive);
                    Assert.NotNull(host.SelectedContent);
                }
            }
            vm.NavigateToSectionCommand.Execute("whatsapp");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Connections", vm.SelectedNavigationGroup!.Title);
            Assert.Equal("WhatsApp", vm.SelectedNavigationSection!.Title);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Palette_FiltersAndNavigates_WithoutLosingChatDraft()
    {
        var vm = CreateViewModel();
        vm.InputText = "Keep this draft";
        vm.OpenCommandPaletteCommand.Execute(null);
        vm.NavigationSearch = "WhatsApp";
        Assert.Single(vm.NavigationSearchResults);
        vm.OpenSearchResultCommand.Execute(null);
        Assert.Equal(13, vm.SelectedSectionIndex);
        Assert.False(vm.IsCommandPaletteOpen);
        Assert.Equal("Keep this draft", vm.InputText);
        vm.NavigationSearch = "no-matching-page";
        Assert.True(vm.HasNoNavigationResults);
        vm.OpenSearchResultCommand.Execute(null);
        Assert.Equal(13, vm.SelectedSectionIndex);
    }

    [AvaloniaFact]
    public void Theme_UpdatesWindow_AndPersistsInSettings()
    {
        var dir = Path.Combine(Path.GetTempPath(), "companion-theme-tests", Guid.NewGuid().ToString("N"));
        _tempDirs.Add(dir);
        var store = new SettingsStore(dir);
        var vm = new MainWindowViewModel(store, new GatewayWebSocketClient());
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show();
            vm.ToggleThemeCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(Avalonia.Styling.ThemeVariant.Dark, window.ActualThemeVariant);
            Assert.True(store.Load().IsDarkTheme);
            vm.ToggleThemeCommand.Execute(null);
            Assert.Equal(Avalonia.Styling.ThemeVariant.Light, window.ActualThemeVariant);
            Assert.False(store.Load().IsDarkTheme);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Keyboard_PaletteAndComposer_PreserveDraftUntilSend()
    {
        var vm = CreateViewModel();
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show();
            var composer = window.FindControl<TextBox>("ChatComposer")!;
            composer.Focus();
            Assert.True(composer.IsFocused);
            window.KeyTextInput("A draft");
            window.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.Shift, Avalonia.Input.PhysicalKey.None, null);
            window.KeyRelease(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.Shift, Avalonia.Input.PhysicalKey.None, null);
            Assert.Contains('\n', vm.InputText);
            window.KeyPress(Avalonia.Input.Key.K, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.None, null);
            window.KeyRelease(Avalonia.Input.Key.K, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.None, null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.IsCommandPaletteOpen);
            Assert.True(window.FindControl<TextBox>("NavigationSearchBox")!.IsFocused);
            window.KeyTextInput("WhatsApp");
            window.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.None, null);
            window.KeyRelease(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.None, null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(13, vm.SelectedSectionIndex);
            Assert.False(vm.IsCommandPaletteOpen);
            vm.NavigateToSectionCommand.Execute("chat");
            vm.OpenCommandPaletteCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            window.KeyPress(Avalonia.Input.Key.Escape, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.None, null);
            window.KeyRelease(Avalonia.Input.Key.Escape, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.None, null);
            Assert.False(vm.IsCommandPaletteOpen);
            Dispatcher.UIThread.RunJobs();
            composer.Focus();
            Assert.True(composer.IsFocused);
            window.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.None, null);
            window.KeyRelease(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.None, null);
            Assert.Equal("A draft\n", vm.InputText); // Disconnected: do not submit or discard it.
            vm.IsConnected = true;
            window.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.None, null);
            window.KeyRelease(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.None, null);
            Assert.Equal("", vm.InputText);
            Assert.Contains(vm.Messages, message => message.IsUser && message.Text == "A draft");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task CompanionConfiguration_OfflineSetupAndTheme_DoNotNeedAModel()
    {
        var vm = CreateViewModel();
        vm.InputText = "/setup";
        await vm.SendCommand.ExecuteAsync(null);
        Assert.True(vm.IsLocalSetupOpen);
        Assert.Empty(vm.InputText);
        Assert.Empty(vm.Messages);
        vm.CancelConfigurationCommand.Execute(null);
        Assert.False(vm.IsLocalSetupOpen);
        vm.InputText = "use dark mode";
        await vm.SendCommand.ExecuteAsync(null);
        Assert.True(vm.IsDarkTheme);
        Dispatcher.UIThread.RunJobs();
        Assert.Single(vm.Messages, message => message.Text == "Switched to dark mode.");
        Assert.False(vm.IsConfigurationBusy);
    }

    [Fact]
    public void CompanionConfiguration_EditorPreservesTypesAndLiteralText()
    {
        Assert.Equal("a \"model\"", new ConfigurationEdit { Key = "modelName", Kind = System.Text.Json.JsonValueKind.String, Value = "a \"model\"" }.ToJson().GetString());
        Assert.True(new ConfigurationEdit { Key = "readOnlyMode", Kind = System.Text.Json.JsonValueKind.False, Value = "true" }.ToJson().GetBoolean());
        Assert.Equal(45, new ConfigurationEdit { Key = "sessionTimeoutMinutes", Kind = System.Text.Json.JsonValueKind.Number, Value = "45" }.ToJson().GetInt32());
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => new ConfigurationEdit { Key = "sessionTimeoutMinutes", Kind = System.Text.Json.JsonValueKind.Number, Value = "forty" }.ToJson());
    }

    [AvaloniaFact]
    public void CompanionTheme_MissingPreferenceFollowsSystemUntilExplicitToggle()
    {
        var app = Application.Current!;
        var previous = app.RequestedThemeVariant;
        app.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        var vm = CreateViewModel();
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.FollowSystemTheme);
            Assert.True(vm.IsDarkTheme);
            Assert.Equal(Avalonia.Styling.ThemeVariant.Dark, window.ActualThemeVariant);
            app.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
            Dispatcher.UIThread.RunJobs();
            Assert.False(vm.IsDarkTheme);
            vm.ToggleThemeCommand.Execute(null);
            Assert.False(vm.FollowSystemTheme);
            Assert.True(vm.IsDarkTheme);
        }
        finally { window.Close(); app.RequestedThemeVariant = previous; }
    }

    [Fact]
    public void CompanionBranding_PreservesAssemblyAndManifestIdentities()
    {
        Assert.Equal("OpenClaw.Companion", typeof(App).Assembly.GetName().Name);
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/OpenClaw.Companion/app.manifest"));
        var manifest = System.Xml.Linq.XDocument.Load(path);
        Assert.Equal("OpenClaw.Companion.Desktop", manifest.Root!.Elements().Single(e => e.Name.LocalName == "assemblyIdentity").Attribute("name")!.Value);
    }

    private MainWindowViewModel CreateViewModel()
    {
        var dir = Path.Combine(Path.GetTempPath(), "openclaw-companion-canvas-ui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);

        var client = new GatewayWebSocketClient();
        client.SetConnectedSocketForTest(new TestWebSocket());
        return new MainWindowViewModel(new SettingsStore(dir), client);
    }

    private static TabItem GetTabByHeader(TabControl tabControl, string header)
        => tabControl.Items
            .OfType<TabItem>()
            .Single(item => string.Equals(item.Header?.ToString(), header, StringComparison.Ordinal));

    private static WsServerEnvelope CreateSurfaceEnvelope(string surfaceId, string title, string[] components)
        => new()
        {
            Type = "a2ui_create_surface",
            Operation = "createSurface",
            RequestId = "create-" + surfaceId,
            SessionId = "sess",
            SurfaceId = surfaceId,
            CatalogId = A2UiCatalogRegistry.AGenUiCatalogId,
            SurfaceTitle = title,
            Components = components
        };

    private static string TextComponent(string id, string text)
        => $$"""{"type":"Text","id":"{{id}}","text":"{{text}}"}""";

    private static string ButtonComponent(string id, string label)
        => $$"""{"type":"Button","id":"{{id}}","label":"{{label}}"}""";

    private static async Task ApplyCanvasEnvelopeAsync(MainWindowViewModel viewModel, WsServerEnvelope envelope)
    {
        var method = typeof(MainWindowViewModel).GetMethod(
            "ApplyCanvasEnvelopeAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = (Task?)method!.Invoke(viewModel, [envelope]);
        Assert.NotNull(task);
        await task!;
    }
}
