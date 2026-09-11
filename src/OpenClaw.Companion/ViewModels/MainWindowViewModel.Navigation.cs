using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OpenClaw.Companion.ViewModels;

public sealed record CompanionSection(int Index, string Title, string Description);
public sealed record CompanionNavigationGroup(string Title, string Icon, CompanionSection[] Sections);

public sealed partial class MainWindowViewModel
{
    public IReadOnlyList<CompanionNavigationGroup> NavigationGroups { get; } =
    [
        new("Chat", "M3 3H21V17H8L3 21Z", [new(2, "Conversation", "Talk with your assistant"), new(3, "Canvas", "Interactive surfaces and outputs")]),
        new("Activity", "M2 12H6L9 3L15 21L18 12H22", [new(5, "Approvals", "Review requests and decision history"), new(8, "Diagnostics", "Runtime events and troubleshooting")]),
        new("Connections", "M8 3V7M16 3V7M6 7H18V11A6 6 0 0 1 6 11ZM12 17V22", [new(9, "Plugins & channels", "Health, readiness and compatibility"), new(13, "WhatsApp", "Set up and manage your connection")]),
        new("Library", "M3 3H9Q12 3 12 6Q12 3 15 3H21V20H15Q12 20 12 22Q12 20 9 20H3ZM12 6V22", [new(6, "Workflows", "Run a workflow and follow its progress"), new(7, "Automations", "Templates, runs and recovery"), new(10, "Memory & profiles", "Explore remembered context")]),
        new("History", "M3 4V10H9M3 10A9 9 0 1 1 4 17M12 7V12L15 14", [new(4, "Conversation history", "Search sessions and explore their timeline")]),
        new("Overview", "M3 13A9 9 0 0 1 21 13V20H3ZM12 13L16 8", [new(0, "Runtime overview", "Health, recent activity and connected services")]),
        new("Settings", "M12 3V6M12 18V21M3 12H6M18 12H21M5 5L7 7M17 17L19 19M5 19L7 17M17 7L19 5M16 12A4 4 0 1 1 8 12A4 4 0 1 1 16 12", [new(1, "Setup & runtime", "Gateway connection, local models and workspace setup"), new(11, "Models & providers", "Routes, health and tool presets"), new(12, "Access & notifications", "Operator tokens, access and desktop notifications"), new(14, "Payment Lab", "Experimental payments, funding sources and virtual cards")])
    ];

    [ObservableProperty] private CompanionNavigationGroup? _selectedNavigationGroup;
    [ObservableProperty] private CompanionSection? _selectedNavigationSection;
    [ObservableProperty] private bool _isCommandPaletteOpen;
    [ObservableProperty] private string _navigationSearch = "";
    [ObservableProperty] private CompanionSection? _selectedSearchResult;
    [ObservableProperty] private bool _isDarkTheme;

    public ObservableCollection<CompanionSection> NavigationSearchResults { get; } = [];
    public bool HasNoNavigationResults => NavigationSearchResults.Count == 0;
    public string ThemeToggleLabel => IsDarkTheme ? "Light theme" : "Dark theme";

    private void InitializeNavigation()
    {
        SynchronizeNavigation();
        UpdateNavigationSearch();
    }

    partial void OnSelectedSectionIndexChanged(int value) => SynchronizeNavigation();

    private void SynchronizeNavigation()
    {
        var group = NavigationGroups.FirstOrDefault(g => g.Sections.Any(s => s.Index == SelectedSectionIndex));
        if (group is null) return;
        SelectedNavigationGroup = group;
        SelectedNavigationSection = group.Sections.First(s => s.Index == SelectedSectionIndex);
    }

    partial void OnSelectedNavigationGroupChanged(CompanionNavigationGroup? value)
    {
        if (value is not null && !value.Sections.Any(s => s.Index == SelectedSectionIndex))
            SelectedSectionIndex = value.Sections[0].Index;
    }

    partial void OnSelectedNavigationSectionChanged(CompanionSection? value)
    {
        if (value is not null) SelectedSectionIndex = value.Index;
    }

    partial void OnNavigationSearchChanged(string value) => UpdateNavigationSearch();

    private void UpdateNavigationSearch()
    {
        var query = NavigationSearch.Trim();
        var matches = NavigationGroups.SelectMany(g => g.Sections.Select(s => (Group: g.Title, Section: s)))
            .Where(item => $"{item.Group} {item.Section.Title} {item.Section.Description}".Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Section).ToArray();
        ReplaceItems(NavigationSearchResults, matches);
        SelectedSearchResult = matches.FirstOrDefault();
        OnPropertyChanged(nameof(HasNoNavigationResults));
    }

    [RelayCommand]
    private void OpenCommandPalette()
    {
        NavigationSearch = "";
        UpdateNavigationSearch();
        IsCommandPaletteOpen = true;
    }

    [RelayCommand] private void CloseCommandPalette() => IsCommandPaletteOpen = false;

    [RelayCommand]
    private void OpenSearchResult()
    {
        if (SelectedSearchResult is null) return;
        SelectedSectionIndex = SelectedSearchResult.Index;
        IsCommandPaletteOpen = false;
    }

    [RelayCommand] private void ToggleTheme() => IsDarkTheme = !IsDarkTheme;

    partial void OnIsDarkThemeChanged(bool value)
    {
        OnPropertyChanged(nameof(ThemeToggleLabel));
        if (!_isLoadingSettings) SaveSettings();
    }
}
