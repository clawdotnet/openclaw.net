using System.Threading;
using System.Threading.Tasks;
using Xunit;
using OpenClaw.Agent.Tools;
using OpenClaw.Core.Models;

namespace OpenClaw.Tests;

public class BrowserToolTests
{
    [Fact]
    public async Task BrowserTool_CanNavigateAndGetText()
    {
        var config = new ToolingConfig 
        { 
            EnableBrowserTool = true, 
            BrowserHeadless = true,
            BrowserTimeoutSeconds = 30
        };
        await using var browser = new BrowserTool(config);

        // goto example.com
        var gotoArgs = "{\"action\": \"goto\", \"url\": \"https://example.com\"}";
        var gotoRes = await browser.ExecuteAsync(gotoArgs, CancellationToken.None);
        Assert.Contains("Navigated to", gotoRes);

        // get_text
        var getTextArgs = "{\"action\": \"get_text\", \"selector\": \"h1\"}";
        var textRes = await browser.ExecuteAsync(getTextArgs, CancellationToken.None);
        Assert.Contains("Example Domain", textRes);
        
        // evaluate JS
        var evalArgs = "{\"action\": \"evaluate\", \"script\": \"Math.max(1, 5)\"}";
        var evalRes = await browser.ExecuteAsync(evalArgs, CancellationToken.None);
        Assert.Equal("5", evalRes);
    }
}
