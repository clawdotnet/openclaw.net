using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OpenClaw.Companion.ViewModels;

public sealed partial class A2UiFrameItem : ObservableObject
{
    private readonly Func<A2UiFrameItem, string, string, Task> _onEvent;
    private bool _suppressEvents;

    public A2UiFrameItem(
        string surfaceId,
        string id,
        string type,
        Func<A2UiFrameItem, string, string, Task> onEvent)
    {
        SurfaceId = surfaceId;
        Id = id;
        Type = type;
        _onEvent = onEvent;
        ActivateCommand = new AsyncRelayCommand(() => FireEventAsync("click", "true"));
        CommitValueCommand = new AsyncRelayCommand(() => FireEventAsync("change", JsonSerializer.Serialize(ValueText ?? "")));
    }

    public string SurfaceId { get; }
    public string Id { get; }
    public string Type { get; }
    public string? Title { get; init; }
    public string? Body { get; init; }
    public string? Text { get; init; }
    public string? Label { get; init; }
    public string? Url { get; init; }
    public string? DisplayText { get; init; }
    public double ProgressValue { get; init; }
    public IReadOnlyList<A2UiOptionItem> Options { get; init; } = [];
    public IReadOnlyList<string> Columns { get; init; } = [];
    public IReadOnlyList<A2UiTableRowItem> Rows { get; init; } = [];
    public IAsyncRelayCommand ActivateCommand { get; }
    public IAsyncRelayCommand CommitValueCommand { get; }

    [ObservableProperty]
    private string? _valueText;

    [ObservableProperty]
    private string? _selectedValue;

    public bool HasTitle => !string.IsNullOrWhiteSpace(Title);
    public bool HasBody => !string.IsNullOrWhiteSpace(Body);
    public bool HasText => !string.IsNullOrWhiteSpace(Text);
    public bool HasLabel => !string.IsNullOrWhiteSpace(Label);
    public bool HasUrl => !string.IsNullOrWhiteSpace(Url);
    public bool HasDisplayText => !string.IsNullOrWhiteSpace(DisplayText);
    public bool HasOptions => Options.Count > 0;
    public bool HasTable => Columns.Count > 0 || Rows.Count > 0;
    public bool IsButton => TypeEquals("button");
    public bool IsInput => TypeEquals("input");
    public bool IsSelect => TypeEquals("select");
    public bool IsChecklist => TypeEquals("checklist");
    public bool IsProgress => TypeEquals("progress");
    public bool IsImage => TypeEquals("image");
    public bool IsChart => TypeEquals("chart");

    public static A2UiFrameItem FromJson(string surfaceId, JsonElement root, Func<A2UiFrameItem, string, string, Task> onEvent)
    {
        var type = GetString(root, "type") ?? "text";
        var item = new A2UiFrameItem(surfaceId, GetString(root, "id") ?? Guid.NewGuid().ToString("N"), type, onEvent)
        {
            Title = GetString(root, "title"),
            Body = GetString(root, "body"),
            Text = GetString(root, "text"),
            Label = GetString(root, "label"),
            Url = GetString(root, "url") ?? GetString(root, "src"),
            DisplayText = BuildDisplayText(type, root),
            ProgressValue = BuildProgressValue(root),
            Columns = BuildColumns(root),
            Rows = BuildRows(root),
            Options = BuildOptions(root)
        };

        item._suppressEvents = true;
        item.ValueText = GetString(root, "value") ?? "";
        item.SelectedValue = item.Options.FirstOrDefault(static option => option.IsSelected)?.Value;
        item._suppressEvents = false;
        foreach (var option in item.Options)
            option.AttachParent(item);

        return item;
    }

    partial void OnValueTextChanged(string? value)
    {
        if (!_suppressEvents && IsInput)
            _ = FireEventAsync("change", JsonSerializer.Serialize(value ?? ""));
    }

    partial void OnSelectedValueChanged(string? value)
    {
        if (!_suppressEvents && IsSelect)
            _ = FireEventAsync("change", JsonSerializer.Serialize(value ?? ""));
    }

    internal Task FireEventAsync(string eventName, string valueJson)
        => _onEvent(this, eventName, valueJson);

    private bool TypeEquals(string expected)
        => string.Equals(Type, expected, StringComparison.OrdinalIgnoreCase);

    private static string? GetString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var prop))
            return null;
        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => prop.GetRawText(),
            _ => null
        };
    }

    private static double BuildProgressValue(JsonElement root)
    {
        if (!root.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Number)
            return 0;
        var number = value.GetDouble();
        return number <= 1 ? number * 100 : Math.Clamp(number, 0, 100);
    }

    private static string BuildDisplayText(string type, JsonElement root)
    {
        if (string.Equals(type, "chart", StringComparison.OrdinalIgnoreCase) &&
            root.TryGetProperty("data", out var data))
        {
            return data.GetRawText();
        }

        return string.Empty;
    }

    private static IReadOnlyList<string> BuildColumns(JsonElement root)
    {
        if (!root.TryGetProperty("columns", out var columns) || columns.ValueKind != JsonValueKind.Array)
            return [];

        return columns.EnumerateArray()
            .Select(static item => item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : item.GetRawText())
            .Where(static item => item.Length > 0)
            .ToArray();
    }

    private static IReadOnlyList<A2UiTableRowItem> BuildRows(JsonElement root)
    {
        if (!root.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
            return [];

        return rows.EnumerateArray()
            .Select(static row => new A2UiTableRowItem(row.ValueKind == JsonValueKind.Array
                ? string.Join(" | ", row.EnumerateArray().Select(static cell => cell.ValueKind == JsonValueKind.String ? cell.GetString() : cell.GetRawText()))
                : row.GetRawText()))
            .ToArray();
    }

    private static IReadOnlyList<A2UiOptionItem> BuildOptions(JsonElement root)
    {
        if (!root.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array)
            return [];

        return options.EnumerateArray()
            .Select(static option =>
            {
                if (option.ValueKind == JsonValueKind.String)
                {
                    var value = option.GetString() ?? "";
                    return new A2UiOptionItem(value, value, false);
                }

                var label = GetString(option, "label") ?? GetString(option, "text") ?? GetString(option, "value") ?? "option";
                var valueText = GetString(option, "value") ?? label;
                var selected = option.TryGetProperty("selected", out var selectedProp) &&
                    selectedProp.ValueKind == JsonValueKind.True;
                return new A2UiOptionItem(label, valueText, selected);
            })
            .ToArray();
    }
}

public sealed partial class A2UiOptionItem : ObservableObject
{
    private A2UiFrameItem? _parent;

    public A2UiOptionItem(string label, string value, bool isSelected)
    {
        Label = label;
        Value = value;
        _isSelected = isSelected;
    }

    public string Label { get; }
    public string Value { get; }

    [ObservableProperty]
    private bool _isSelected;

    internal void AttachParent(A2UiFrameItem parent) => _parent = parent;

    partial void OnIsSelectedChanged(bool value)
    {
        if (_parent is null)
            return;

        if (_parent.IsSelect && value)
            _parent.SelectedValue = Value;
        if (_parent.IsChecklist)
            _ = _parent.FireEventAsync("change", JsonSerializer.Serialize(new { value = Value, selected = value }));
    }
}

public sealed record A2UiTableRowItem(string DisplayText);
