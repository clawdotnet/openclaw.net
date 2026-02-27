using OpenClaw.Agent.Integrations;
using OpenClaw.Core.Plugins;
using Xunit;

namespace OpenClaw.Tests;

public sealed class EventBridgeRuleTests
{
    [Fact]
    public void SelectRule_MatchesEntityAndState()
    {
        var cfg = new HomeAssistantEventsConfig
        {
            EmitAllMatchingEvents = false,
            Rules =
            [
                new HomeAssistantEventRule
                {
                    Name = "motion-night",
                    EntityIdGlobs = ["binary_sensor.hall_motion"],
                    FromState = "off",
                    ToState = "on",
                    PromptTemplate = "Motion!"
                }
            ]
        };

        var info = new HomeAssistantRuleEngine.EventInfo(
            "state_changed",
            "binary_sensor.hall_motion",
            "off",
            "on",
            "Hall Motion");

        var rule = HomeAssistantRuleEngine.SelectRule(cfg, info, new DateTime(2026, 2, 26, 21, 0, 0));
        Assert.NotNull(rule);
        Assert.Equal("motion-night", rule!.Name);
    }

    [Fact]
    public void SelectRule_OvernightWindow_Works()
    {
        var cfg = new HomeAssistantEventsConfig
        {
            Rules =
            [
                new HomeAssistantEventRule
                {
                    Name = "overnight",
                    EntityIdGlobs = ["*"],
                    BetweenLocalStart = "22:00",
                    BetweenLocalEnd = "06:00",
                    PromptTemplate = "overnight"
                }
            ]
        };

        var info = new HomeAssistantRuleEngine.EventInfo("x", "a", "", "", "");

        Assert.NotNull(HomeAssistantRuleEngine.SelectRule(cfg, info, new DateTime(2026, 2, 26, 23, 0, 0)));
        Assert.NotNull(HomeAssistantRuleEngine.SelectRule(cfg, info, new DateTime(2026, 2, 26, 5, 0, 0)));
        Assert.Null(HomeAssistantRuleEngine.SelectRule(cfg, info, new DateTime(2026, 2, 26, 12, 0, 0)));
    }

    [Fact]
    public void Render_ReplacesTokens()
    {
        var cfg = new HomeAssistantEventsConfig
        {
            PromptTemplate = "E={event_type} {entity_id} {from_state}->{to_state} ({friendly_name})"
        };

        var info = new HomeAssistantRuleEngine.EventInfo("state_changed", "light.kitchen", "off", "on", "Kitchen");
        var text = HomeAssistantRuleEngine.Render(cfg, rule: null, info);
        Assert.Contains("state_changed", text);
        Assert.Contains("light.kitchen", text);
        Assert.Contains("off->on", text);
        Assert.Contains("Kitchen", text);
    }
}

