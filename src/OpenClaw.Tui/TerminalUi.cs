using OpenClaw.Client;
using OpenClaw.Core.Models;
using Spectre.Console;

namespace OpenClaw.Tui;

public static class TerminalUi
{
    public static async Task<int> RunAsync(string baseUrl, string? authToken, string? presetId, CancellationToken ct)
    {
        using var client = new OpenClawHttpClient(baseUrl, authToken);

        while (!ct.IsCancellationRequested)
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(new Rule("[yellow]OpenClaw Terminal UI[/]").RuleStyle("grey"));

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Select an action")
                    .PageSize(12)
                    .AddChoices(
                    [
                        "Status",
                        "Insights",
                        "Approvals",
                        "Sessions",
                        "Session Search",
                        "Automations",
                        "Learning Proposals",
                        "Profiles",
                        "Tool Presets",
                        "Live Session",
                        "Chat",
                        "Exit"
                    ]));

            switch (choice)
            {
                case "Status":
                    await ShowStatusAsync(client, ct);
                    break;
                case "Insights":
                    await ShowInsightsAsync(client, ct);
                    break;
                case "Approvals":
                    await ShowApprovalsAsync(client, ct);
                    break;
                case "Sessions":
                    await ShowSessionsAsync(client, ct);
                    break;
                case "Session Search":
                    await ShowSessionSearchAsync(client, ct);
                    break;
                case "Automations":
                    await ShowAutomationsAsync(client, ct);
                    break;
                case "Learning Proposals":
                    await ShowLearningProposalsAsync(client, ct);
                    break;
                case "Profiles":
                    await ShowProfilesAsync(client, ct);
                    break;
                case "Tool Presets":
                    await ShowToolPresetsAsync(client, ct);
                    break;
                case "Live Session":
                    await ShowLiveSessionAsync(client, authToken, ct);
                    break;
                case "Chat":
                    await ShowChatAsync(client, presetId, ct);
                    break;
                default:
                    return 0;
            }
        }

        return 0;
    }

    private static async Task ShowStatusAsync(OpenClawHttpClient client, CancellationToken ct)
    {
        var dashboard = await client.GetIntegrationDashboardAsync(ct);

        var table = new Table().RoundedBorder();
        table.AddColumn("Metric");
        table.AddColumn("Value");
        table.AddRow("Health", dashboard.Status.Health.Status);
        table.AddRow("Requested mode", dashboard.Status.Runtime.RequestedMode);
        table.AddRow("Effective mode", dashboard.Status.Runtime.EffectiveModeName);
        table.AddRow("Active sessions", dashboard.Status.ActiveSessions.ToString());
        table.AddRow("Pending approvals", dashboard.Status.PendingApprovals.ToString());
        table.AddRow("Approval grants", dashboard.Status.ActiveApprovalGrants.ToString());
        table.AddRow("Plugin health items", dashboard.Plugins.Items.Count.ToString());
        table.AddRow("Recent runtime events", dashboard.Events.Items.Count.ToString());

        AnsiConsole.Write(table);
        Pause();
    }

    private static async Task ShowInsightsAsync(OpenClawHttpClient client, CancellationToken ct)
    {
        var insights = await client.GetOperatorInsightsAsync(null, null, ct);

        var summary = new Table().RoundedBorder();
        summary.AddColumn("Metric");
        summary.AddColumn("Value");
        summary.AddRow("Window", $"{insights.StartUtc:yyyy-MM-dd HH:mm} UTC - {insights.EndUtc:yyyy-MM-dd HH:mm} UTC");
        summary.AddRow("Sessions", $"active {insights.Sessions.Active}, persisted {insights.Sessions.Persisted}, range {insights.Sessions.InRange}");
        summary.AddRow("Provider requests", insights.Totals.ProviderRequests.ToString());
        summary.AddRow("Tokens", $"{insights.Totals.TotalTokens} ({insights.Totals.InputTokens} in / {insights.Totals.OutputTokens} out)");
        summary.AddRow("Estimated spend", $"${insights.Totals.EstimatedCostUsd:0.######}");
        summary.AddRow("Tool calls", insights.Totals.ToolCalls.ToString());
        AnsiConsole.Write(summary);

        var providers = new Table().RoundedBorder().Title("Providers");
        providers.AddColumn("Provider");
        providers.AddColumn("Requests");
        providers.AddColumn("Tokens");
        providers.AddColumn("Cost");
        providers.AddColumn("Errors");
        foreach (var item in insights.Providers.Take(8))
        {
            providers.AddRow(
                $"{Markup.Escape(item.ProviderId)}/{Markup.Escape(item.ModelId)}",
                item.Requests.ToString(),
                item.TotalTokens.ToString(),
                $"${item.EstimatedCostUsd:0.######}",
                item.Errors.ToString());
        }
        AnsiConsole.Write(providers);

        var tools = new Table().RoundedBorder().Title("Tools");
        tools.AddColumn("Tool");
        tools.AddColumn("Calls");
        tools.AddColumn("Failures");
        tools.AddColumn("Avg ms");
        foreach (var item in insights.Tools.Take(10))
        {
            tools.AddRow(
                Markup.Escape(item.ToolName),
                item.Calls.ToString(),
                item.Failures.ToString(),
                item.AverageDurationMs.ToString("0.0"));
        }
        AnsiConsole.Write(tools);

        foreach (var warning in insights.Warnings)
            AnsiConsole.MarkupLine($"[grey]{Markup.Escape(warning)}[/]");

        Pause();
    }

    private static async Task ShowApprovalsAsync(OpenClawHttpClient client, CancellationToken ct)
    {
        var approvals = await client.GetIntegrationApprovalsAsync(channelId: null, senderId: null, ct);
        var table = new Table().RoundedBorder();
        table.AddColumn("Approval ID");
        table.AddColumn("Tool");
        table.AddColumn("Channel");
        table.AddColumn("Sender");

        foreach (var item in approvals.Items)
        {
            table.AddRow(
                item.ApprovalId,
                item.ToolName,
                item.ChannelId ?? "-",
                item.SenderId ?? "-");
        }

        if (approvals.Items.Count == 0)
            AnsiConsole.MarkupLine("[grey]No pending approvals.[/]");
        else
            AnsiConsole.Write(table);

        Pause();
    }

    private static async Task ShowSessionsAsync(OpenClawHttpClient client, CancellationToken ct)
    {
        var search = AnsiConsole.Ask<string>("Session filter ([grey]blank for all[/])");
        var sessions = await client.ListSessionsAsync(1, 25, new SessionListQuery
        {
            Search = string.IsNullOrWhiteSpace(search) ? null : search
        }, ct);

        var table = new Table().RoundedBorder();
        table.AddColumn("Session ID");
        table.AddColumn("Channel");
        table.AddColumn("Sender");
        table.AddColumn("State");
        table.AddColumn("Turns");

        foreach (var item in sessions.Active.Concat(sessions.Persisted.Items).Take(25))
        {
            table.AddRow(
                item.Id,
                item.ChannelId,
                item.SenderId,
                item.State.ToString(),
                item.HistoryTurns.ToString());
        }

        if (sessions.Active.Count == 0 && sessions.Persisted.Items.Count == 0)
            AnsiConsole.MarkupLine("[grey]No sessions found.[/]");
        else
            AnsiConsole.Write(table);

        var sessionId = AnsiConsole.Ask<string>("Inspect session ID ([grey]blank to return[/])");
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        var detail = await client.GetSessionAsync(sessionId, ct);
        if (detail.Session is null)
        {
            AnsiConsole.MarkupLine("[red]Session not found.[/]");
            Pause();
            return;
        }

        var panelText = string.Join(
            "\n\n",
            detail.Session.History.TakeLast(12).Select(turn => $"{turn.Role}: {turn.Content}"));
        AnsiConsole.Write(new Panel(panelText.Length == 0 ? "(empty session)" : panelText).Header(detail.Session.Id));

        if (detail.Metadata is not null)
            AnsiConsole.MarkupLine($"[grey]Active preset:[/] {Markup.Escape(detail.Metadata.ActivePresetId ?? "(default)")}");

        if (AnsiConsole.Confirm("Update this session's preset?", false))
        {
            var presets = await client.ListToolPresetsAsync(ct);
            var choices = presets.Items.Select(static item => item.PresetId).ToList();
            if (choices.Count > 0)
            {
                var selected = AnsiConsole.Prompt(new SelectionPrompt<string>().Title("Preset").AddChoices(choices));
                await client.UpdateSessionMetadataAsync(detail.Session.Id, new SessionMetadataUpdateRequest
                {
                    ActivePresetId = selected,
                    Starred = detail.Metadata?.Starred,
                    Tags = detail.Metadata?.Tags,
                    TodoItems = detail.Metadata?.TodoItems
                }, ct);
                AnsiConsole.MarkupLine($"[green]Preset updated to {Markup.Escape(selected)}.[/]");
            }
        }

        Pause();
    }

    private static async Task ShowSessionSearchAsync(OpenClawHttpClient client, CancellationToken ct)
    {
        var text = AnsiConsole.Ask<string>("Search text");
        if (string.IsNullOrWhiteSpace(text))
            return;

        var results = await client.SearchSessionsAsync(new SessionSearchQuery
        {
            Text = text,
            Limit = 25
        }, ct);

        var table = new Table().RoundedBorder();
        table.AddColumn("Session");
        table.AddColumn("Role");
        table.AddColumn("Score");
        table.AddColumn("Snippet");

        foreach (var hit in results.Result.Items)
            table.AddRow(hit.SessionId, hit.Role, hit.Score.ToString("0.00"), hit.Snippet);

        if (results.Result.Items.Count == 0)
            AnsiConsole.MarkupLine("[grey]No session hits found.[/]");
        else
            AnsiConsole.Write(table);

        Pause();
    }

    private static async Task ShowAutomationsAsync(OpenClawHttpClient client, CancellationToken ct)
    {
        var automations = await client.GetAdminAutomationsAsync(ct);
        var table = new Table().RoundedBorder();
        table.AddColumn("ID");
        table.AddColumn("Name");
        table.AddColumn("Schedule");
        table.AddColumn("Enabled");
        table.AddColumn("Source");

        foreach (var item in automations.Items)
            table.AddRow(item.Id, item.Name, item.Schedule, item.Enabled ? "yes" : "no", item.Source);

        if (automations.Items.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No automations found.[/]");
            Pause();
            return;
        }

        AnsiConsole.Write(table);
        var selectedId = AnsiConsole.Ask<string>("Automation ID to inspect/run ([grey]blank to return[/])");
        if (string.IsNullOrWhiteSpace(selectedId))
            return;

        var detail = await client.GetAdminAutomationAsync(selectedId, ct);
        if (detail.Automation is null)
        {
            AnsiConsole.MarkupLine("[red]Automation not found.[/]");
            Pause();
            return;
        }

        var panel = new Panel(detail.Automation.Prompt.Length == 0 ? "(empty prompt)" : detail.Automation.Prompt)
            .Header($"{detail.Automation.Name} [{detail.Automation.Id}]");
        AnsiConsole.Write(panel);

        if (AnsiConsole.Confirm("Queue this automation now?", false))
        {
            var result = await client.RunAdminAutomationAsync(selectedId, dryRun: false, ct);
            AnsiConsole.MarkupLine(result.Success ? $"[green]{Markup.Escape(result.Message)}[/]" : $"[red]{Markup.Escape(result.Error ?? "Run failed.")}[/]");
        }

        Pause();
    }

    private static async Task ShowLearningProposalsAsync(OpenClawHttpClient client, CancellationToken ct)
    {
        var proposals = await client.ListLearningProposalsAsync("pending", kind: null, ct);
        var table = new Table().RoundedBorder();
        table.AddColumn("ID");
        table.AddColumn("Kind");
        table.AddColumn("Title");
        table.AddColumn("Confidence");

        foreach (var item in proposals.Items)
            table.AddRow(item.Id, item.Kind, item.Title, item.Confidence.ToString("0.00"));

        if (proposals.Items.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No pending learning proposals.[/]");
            Pause();
            return;
        }

        AnsiConsole.Write(table);
        var proposalId = AnsiConsole.Ask<string>("Proposal ID to review ([grey]blank to return[/])");
        if (string.IsNullOrWhiteSpace(proposalId))
            return;

        var proposal = proposals.Items.FirstOrDefault(item => string.Equals(item.Id, proposalId, StringComparison.OrdinalIgnoreCase));
        if (proposal is null)
        {
            AnsiConsole.MarkupLine("[red]Proposal not found in current listing.[/]");
            Pause();
            return;
        }

        AnsiConsole.Write(new Panel(proposal.DraftContent ?? proposal.Summary).Header(proposal.Title));
        var action = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Review action")
                .AddChoices(["Approve", "Reject", "Return"]));

        switch (action)
        {
            case "Approve":
                await client.ApproveLearningProposalAsync(proposalId, ct);
                AnsiConsole.MarkupLine("[green]Proposal approved.[/]");
                break;
            case "Reject":
                var reason = AnsiConsole.Ask<string>("Reason ([grey]optional[/])");
                await client.RejectLearningProposalAsync(proposalId, string.IsNullOrWhiteSpace(reason) ? null : reason, ct);
                AnsiConsole.MarkupLine("[yellow]Proposal rejected.[/]");
                break;
        }

        Pause();
    }

    private static async Task ShowProfilesAsync(OpenClawHttpClient client, CancellationToken ct)
    {
        var profiles = await client.ListProfilesAsync(ct);
        var table = new Table().RoundedBorder();
        table.AddColumn("Actor");
        table.AddColumn("Tone");
        table.AddColumn("Summary");

        foreach (var item in profiles.Items.Take(25))
            table.AddRow(item.ActorId, item.Tone, item.Summary);

        if (profiles.Items.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No profiles found.[/]");
            Pause();
            return;
        }

        AnsiConsole.Write(table);
        var actorId = AnsiConsole.Ask<string>("Actor ID to inspect/edit ([grey]blank to return[/])");
        if (string.IsNullOrWhiteSpace(actorId))
            return;

        var response = await client.GetProfileAsync(actorId, ct);
        if (response.Profile is null)
        {
            AnsiConsole.MarkupLine("[red]Profile not found.[/]");
            Pause();
            return;
        }

        var profile = response.Profile;
        AnsiConsole.Write(new Panel(profile.Summary.Length == 0 ? "(no summary)" : profile.Summary).Header(profile.ActorId));

        if (!AnsiConsole.Confirm("Edit this profile?", false))
            return;

        var updatedSummary = AnsiConsole.Ask<string>("Summary", profile.Summary);
        var updatedTone = AnsiConsole.Ask<string>("Tone", profile.Tone);
        await client.SaveProfileAsync(actorId, new UserProfile
        {
            ActorId = profile.ActorId,
            ChannelId = profile.ChannelId,
            SenderId = profile.SenderId,
            Summary = updatedSummary,
            Tone = updatedTone,
            Facts = profile.Facts,
            Preferences = profile.Preferences,
            ActiveProjects = profile.ActiveProjects,
            RecentIntents = profile.RecentIntents,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        }, ct);

        AnsiConsole.MarkupLine("[green]Profile saved.[/]");
        Pause();
    }

    private static async Task ShowToolPresetsAsync(OpenClawHttpClient client, CancellationToken ct)
    {
        var presets = await client.ListToolPresetsAsync(ct);
        if (presets.Items.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No presets available.[/]");
            Pause();
            return;
        }

        var table = new Table().RoundedBorder();
        table.AddColumn("Preset");
        table.AddColumn("Autonomy");
        table.AddColumn("Approval");
        table.AddColumn("Description");

        foreach (var item in presets.Items)
            table.AddRow(item.PresetId, item.EffectiveAutonomyMode, item.RequireToolApproval ? "required" : "not required", item.Description);

        AnsiConsole.Write(table);
        Pause();
    }

    private static async Task ShowChatAsync(OpenClawHttpClient client, string? defaultPresetId, CancellationToken ct)
    {
        var system = AnsiConsole.Ask<string>("System prompt ([grey]blank for none[/])");
        var model = AnsiConsole.Ask<string>("Model override ([grey]blank for default[/])");
        var presetId = AnsiConsole.Ask<string>("Preset ([grey]blank for default[/])", defaultPresetId ?? "");

        while (!ct.IsCancellationRequested)
        {
            var prompt = AnsiConsole.Ask<string>("Prompt ([grey]blank to return[/])");
            if (string.IsNullOrWhiteSpace(prompt))
                return;

            var messages = string.IsNullOrWhiteSpace(system)
                ? new List<OpenAiMessage> { new() { Role = "user", Content = prompt } }
                : [new OpenAiMessage { Role = "system", Content = system }, new OpenAiMessage { Role = "user", Content = prompt }];

            var request = new OpenAiChatCompletionRequest
            {
                Model = string.IsNullOrWhiteSpace(model) ? null : model,
                Stream = true,
                Messages = messages
            };

            AnsiConsole.MarkupLine("[grey]Streaming response...[/]");
            await client.StreamChatCompletionAsync(request, chunk => Console.Write(chunk), ct, string.IsNullOrWhiteSpace(presetId) ? null : presetId);
            Console.WriteLine();
        }
    }

    private static async Task ShowLiveSessionAsync(OpenClawHttpClient client, string? authToken, CancellationToken ct)
    {
        var provider = AnsiConsole.Ask<string>("Live provider ([grey]blank for default[/])", "gemini");
        var model = AnsiConsole.Ask<string>("Live model ([grey]blank for default[/])");
        var system = AnsiConsole.Ask<string>("System instruction ([grey]blank for none[/])");
        var modalitiesRaw = AnsiConsole.Ask<string>("Response modalities ([grey]TEXT or TEXT,AUDIO[/])", "TEXT");
        var voice = AnsiConsole.Ask<string>("Voice ([grey]blank for default[/])");
        var modalities = modalitiesRaw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item.ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (modalities.Length == 0)
            modalities = ["TEXT"];

        await using var live = new OpenClawLiveClient();
        live.OnEnvelopeReceived += envelope =>
        {
            switch (envelope.Type)
            {
                case "opened":
                    AnsiConsole.MarkupLine($"[green]Live session opened:[/] {Markup.Escape(envelope.Text ?? "(default model)")}");
                    break;
                case "text":
                    Console.Write(envelope.Text);
                    break;
                case "turn_complete":
                    Console.WriteLine();
                    break;
                case "audio":
                    AnsiConsole.MarkupLine($"[yellow]Audio chunk received[/] ({Markup.Escape(envelope.MimeType ?? "audio/pcm")})");
                    break;
                case "input_transcription":
                    AnsiConsole.MarkupLine($"[grey]You:[/] {Markup.Escape(envelope.Text ?? string.Empty)}");
                    break;
                case "output_transcription":
                    AnsiConsole.MarkupLine($"[grey]Model:[/] {Markup.Escape(envelope.Text ?? string.Empty)}");
                    break;
                case "interrupted":
                    AnsiConsole.MarkupLine("[yellow]Live generation interrupted.[/]");
                    break;
                case "error":
                    AnsiConsole.MarkupLine($"[red]{Markup.Escape(envelope.Error ?? "Live session error.")}[/]");
                    break;
            }
        };
        live.OnError += message => AnsiConsole.MarkupLine($"[red]{Markup.Escape(message)}[/]");

        await live.ConnectAsync(
            client.GetLiveWebSocketUri(),
            authToken,
            new LiveSessionOpenRequest
            {
                Provider = string.IsNullOrWhiteSpace(provider) ? null : provider,
                Model = string.IsNullOrWhiteSpace(model) ? null : model,
                SystemInstruction = string.IsNullOrWhiteSpace(system) ? null : system,
                VoiceName = string.IsNullOrWhiteSpace(voice) ? null : voice,
                ResponseModalities = modalities
            },
            ct);

        AnsiConsole.MarkupLine("[grey]Live session ready. Commands: /interrupt, /audio-file <path> [mime], /exit[/]");
        while (!ct.IsCancellationRequested)
        {
            var line = AnsiConsole.Ask<string>("Live input ([grey]blank to return[/])");
            if (string.IsNullOrWhiteSpace(line) || string.Equals(line, "/exit", StringComparison.OrdinalIgnoreCase))
                break;

            if (string.Equals(line, "/interrupt", StringComparison.OrdinalIgnoreCase))
            {
                await live.InterruptAsync(ct);
                continue;
            }

            if (line.StartsWith("/audio-file ", StringComparison.OrdinalIgnoreCase))
            {
                var tail = line["/audio-file ".Length..].Trim();
                if (tail.Length == 0)
                {
                    AnsiConsole.MarkupLine("[red]Usage: /audio-file <path> [mime][/]");
                    continue;
                }

                var parts = tail.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var path = Path.GetFullPath(parts[0]);
                if (!File.Exists(path))
                {
                    AnsiConsole.MarkupLine($"[red]File not found:[/] {Markup.Escape(path)}");
                    continue;
                }

                var mime = parts.Length > 1 ? parts[1] : "audio/pcm";
                var base64 = Convert.ToBase64String(await File.ReadAllBytesAsync(path, ct));
                await live.SendAudioAsync(base64, mime, turnComplete: true, ct);
                continue;
            }

            await live.SendTextAsync(line, turnComplete: true, ct);
        }

        await live.CloseSessionAsync(CancellationToken.None);
        Pause();
    }

    private static void Pause()
    {
        AnsiConsole.MarkupLine("[grey]Press enter to continue...[/]");
        Console.ReadLine();
    }
}
