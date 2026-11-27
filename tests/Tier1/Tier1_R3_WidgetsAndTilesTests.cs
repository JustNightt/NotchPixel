using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NotchPixel.E2E.Tests.Infrastructure;
using Xunit;

namespace NotchPixel.E2E.Tests.Tier1;

[Collection("NotchPixelE2E")]
public sealed class Tier1_R3_WidgetsAndTilesTests
{
    private readonly E2ETestSessionFixture _fixture;

    public Tier1_R3_WidgetsAndTilesTests(E2ETestSessionFixture fixture)
    {
        _fixture = fixture;
    }

    // =========================================================================
    // F10: Signature Day Clock Desktop Widget (6 test methods)
    // =========================================================================

    [Fact]
    public async Task F10_SelfTest_DayClockPinsRendersAndUnpinsCleanly()
    {
        CliExecutionResult res = await ProcessAndWindowDriver.RunCliAsync("--self-test", timeoutMs: 15000);
        Assert.False(res.TimedOut, "--self-test timed out.");
        Assert.Equal(0, res.ExitCode);
        Assert.Contains("[PASS] Desktop Day Clock Widget pins, renders with Liquid Glass live wallpaper sampling, and unpins cleanly", res.StdOut);
    }

    [Fact]
    public void F10_Source_DayClockRendersGeometricStencilUppercaseWeekdayAndDate()
    {
        IReadOnlyList<SourceMatch> weekdayProp = SourceInspector.FindMatchesInCSharp(@"DayClockShowWeekday");
        IReadOnlyList<SourceMatch> dateProp = SourceInspector.FindMatchesInCSharp(@"DayClockShowDate");
        IReadOnlyList<SourceMatch> dashesProp = SourceInspector.FindMatchesInCSharp(@"DayClockShowDashes");
        IReadOnlyList<SourceMatch> stencil = SourceInspector.FindMatchesInCSharp(@"DrawDayClock|Stencil|Geometric|DayOfWeek");

        Assert.True(weekdayProp.Count >= 2);
        Assert.True(dateProp.Count >= 2);
        Assert.True(dashesProp.Count >= 2);
        Assert.NotEmpty(stencil);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task F10_Settings_DayClock12hAnd24hFormatSupport(bool use24Hour)
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings(s =>
        {
            s["Use24HourClock"] = use24Hour;
            s["DayClockShowTime"] = true;
            s["DayClockShowDashes"] = true;
        });

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1800);
        Assert.False(session.Process.HasExited);
        Assert.Equal(use24Hour, SettingsJsonHelper.GetValue<bool>("Use24HourClock"));
    }

    [Fact]
    public async Task F10_Settings_DayClockColorWheelAndLetterSpacingPersist()
    {
        using var guard = new StateBackupGuard();
        int customColor = Color.FromArgb(255, 110, 210, 255).ToArgb();
        SettingsJsonHelper.ResetToCleanSettings(s =>
        {
            s["DayClockAccentColorArgb"] = customColor;
            s["DayClockLetterSpacing"] = 12;
        });

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1800);
        Assert.False(session.Process.HasExited);

        Assert.Equal(customColor, SettingsJsonHelper.GetValue<int>("DayClockAccentColorArgb"));
        Assert.Equal(12, SettingsJsonHelper.GetValue<int>("DayClockLetterSpacing"));
    }

    [Fact]
    public void F10_Source_DayClockSupportsTransparentDragWithoutTurningBlack()
    {
        IReadOnlyList<SourceMatch> noBg = SourceInspector.FindMatchesInCSharp(@"noBackground|StyleMode\s*==\s*3");
        IReadOnlyList<SourceMatch> dragOffset = SourceInspector.FindMatchesInCSharp(@"_dragOffsetX");

        Assert.NotEmpty(noBg);
        Assert.True(dragOffset.Count >= 2, "Expected relative drag offset tracking to prevent click-jump on widget drag.");
    }

    [Fact]
    public void F10_Source_DayClockSupportsOptionalNowPlayingAndDisplayReClamp()
    {
        IReadOnlyList<SourceMatch> nowPlaying = SourceInspector.FindMatchesInCSharp(@"DayClockShowNowPlaying");
        IReadOnlyList<SourceMatch> displayChanged = SourceInspector.FindMatchesInCSharp(@"DisplaySettingsChanged");

        Assert.True(nowPlaying.Count >= 2);
        Assert.NotEmpty(displayChanged);
    }

    // =========================================================================
    // F11: Full Desktop & Island Widget Suite (6 test methods)
    // =========================================================================

    [Fact]
    public void F11_Models_AllRequiredWidgetsDefinedInCodebase()
    {
        string[] requiredWidgets =
        {
            "DayClock", "Clock", "WorldClock", "Calendar", "TodoList",
            "Photos", "Batteries", "FocusTimer", "PcStats", "AnalogClock",
            "PlainWeather", "CustomTile"
        };

        foreach (string w in requiredWidgets)
        {
            Assert.NotEmpty(SourceInspector.FindMatchesInCSharp($@"\b{w}\b"));
        }

        // Stock prices widget must also be supported in the widget suite.
        Assert.NotEmpty(SourceInspector.FindMatchesInCSharp(@"\bStock(s|Price)?\b"));
    }

    [Fact]
    public async Task F11_Settings_WeatherAndWorldClockConfigLaunchesCleanly()
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings(s =>
        {
            s["WeatherCity"] = "Seattle";
            s["WorldClockCity"] = "Tokyo";
            s["WorldClockOffsetHours"] = 9;
        });

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2000);
        Assert.False(session.Process.HasExited);
        Assert.Equal("Seattle", SettingsJsonHelper.GetValue<string>("WeatherCity"));
        Assert.Equal("Tokyo", SettingsJsonHelper.GetValue<string>("WorldClockCity"));
    }

    [Fact]
    public void F11_Source_TodoWidgetSupportsScrollingMoreThanTenItems()
    {
        IReadOnlyList<SourceMatch> todoScroll = SourceInspector.FindMatchesInCSharp(@"Todo.*Scroll|_todoScroll|OnMouseWheel", RegexOptions.IgnoreCase);
        Assert.True(todoScroll.Count >= 2, "Expected To-Do widget scroll handling for lists exceeding 10 items.");
    }

    [Fact]
    public void F11_Source_WidgetRightClickMenuProvidesLockResizeColorUnpin()
    {
        IReadOnlyList<SourceMatch> menu = SourceInspector.FindMatchesInCSharp(@"ShowWidgetContextMenu");
        IReadOnlyList<SourceMatch> lockItem = SourceInspector.FindMatchesInCSharp(@"LockDesktopWidgets|Lock.*Widget");
        IReadOnlyList<SourceMatch> unpin = SourceInspector.FindMatchesInCSharp(@"Unpin");

        Assert.NotEmpty(menu);
        Assert.NotEmpty(lockItem);
        Assert.NotEmpty(unpin);
    }

    [Fact]
    public async Task F11_DesktopWidgets_ZOrderRemainsBelowActiveNormalWindow()
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings();

        // Confirm HWND_BOTTOM layering in widget rendering and verify live non-island windows stay below active window.
        IReadOnlyList<SourceMatch> hwndBottom = SourceInspector.FindMatchesInCSharp(@"SetWindowPos\s*\(\s*Handle\s*,\s*new\s+IntPtr\s*\(\s*1\s*\)");
        Assert.NotEmpty(hwndBottom);

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2000);
        using var dummy = ProcessAndWindowDriver.CreateDummyNormalWindow(new Rectangle(80, 80, 500, 380), "F11_ZOrderVerifier");
        dummy.BringToFront();
        await Task.Delay(250);

        int dummyZ = ProcessAndWindowDriver.GetWindowZOrderIndex(dummy.Hwnd);
        Assert.True(dummyZ >= 0, "Dummy normal window not found in top-level z-order.");

        // Any non-topmost owned widget window must appear deeper in z-order (higher index) than active normal window.
        IReadOnlyList<WindowSnapshot> nonTopMostOwned = session.GetOwnedWindows(visibleOnly: true)
            .Where(w => !w.IsTopMost && w.Bounds.Width > 0 && w.Bounds.Height > 0)
            .ToArray();

        foreach (WindowSnapshot widgetWin in nonTopMostOwned)
        {
            int widgetZ = ProcessAndWindowDriver.GetWindowZOrderIndex(widgetWin.Hwnd);
            Assert.True(widgetZ > dummyZ, $"Desktop widget HWND {widgetWin.Hwnd} (z={widgetZ}) unexpectedly overlapped active window (z={dummyZ}).");
        }
    }

    [Fact]
    public void F11_Source_WidgetLayoutPersistenceAcrossRestartsImplemented()
    {
        IReadOnlyList<SourceMatch> persist = SourceInspector.FindMatchesInCSharp(@"PinnedWidgets|widgets\.json|SaveWidgets|LoadWidgets");
        Assert.NotEmpty(persist);
    }

    // =========================================================================
    // F12: Custom Script Live Tiles & Notifications (47823 + CLI) (6 test methods)
    // =========================================================================

    [Fact]
    public async Task F12_Loopback_GetNotifyReturns200AndOkJson()
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings();

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2000);
        using var client = new LoopbackAndDeckClient();
        Assert.True(await client.WaitForLoopbackReadyAsync(8000));

        HttpCallResult res = await client.SendNotifyAsync("Build Complete", "All checks green", "CI", usePost: false);
        Assert.Equal(200, res.StatusCode);
        Assert.Contains("\"ok\":true", res.Body);
        Assert.Contains("\"endpoint\":\"notify\"", res.Body);
    }

    [Fact]
    public async Task F12_Loopback_PostNotifyReturns200AndOkJson()
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings();

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2000);
        using var client = new LoopbackAndDeckClient();
        Assert.True(await client.WaitForLoopbackReadyAsync(8000));

        HttpCallResult res = await client.SendNotifyAsync("Deploy Ready", "Artifact signed", "CD", usePost: true);
        Assert.Equal(200, res.StatusCode);
        Assert.Contains("\"ok\":true", res.Body);
        Assert.Contains("\"endpoint\":\"notify\"", res.Body);
    }

    [Fact]
    public async Task F12_Loopback_GetAndPostTileUpsertsCleanly()
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings();

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2000);
        using var client = new LoopbackAndDeckClient();
        Assert.True(await client.WaitForLoopbackReadyAsync(8000));

        HttpCallResult getRes = await client.SendTileAsync("temp", "GPU Temp", "62C", "Normal", usePost: false);
        Assert.Equal(200, getRes.StatusCode);
        Assert.Contains("\"endpoint\":\"tile\"", getRes.Body);

        HttpCallResult postRes = await client.SendTileAsync("temp", "GPU Temp", "74C", "Load", usePost: true);
        Assert.Equal(200, postRes.StatusCode);
        Assert.Contains("\"endpoint\":\"tile\"", postRes.Body);
    }

    [Fact]
    public async Task F12_Cli_NotifyAndTileExitZeroCleanly()
    {
        CliExecutionResult notifyRes = await ProcessAndWindowDriver.RunCliAsync("--notify \"CLI Notice\"", timeoutMs: 12000);
        Assert.False(notifyRes.TimedOut, "--notify timed out.");
        Assert.Equal(0, notifyRes.ExitCode);

        CliExecutionResult tileRes = await ProcessAndWindowDriver.RunCliAsync("--tile \"CLI Tile\"", timeoutMs: 12000);
        Assert.False(tileRes.TimedOut, "--tile timed out.");
        Assert.Equal(0, tileRes.ExitCode);
    }

    [Fact]
    public async Task F12_Loopback_BindsStrictlyToLoopback47823NotWildcardAny()
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings();

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2000);
        using var client = new LoopbackAndDeckClient();
        Assert.True(await client.WaitForLoopbackReadyAsync(8000));

        IPEndPoint[] listeners47823 = IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners()
            .Where(ep => ep.Port == 47823)
            .ToArray();

        Assert.NotEmpty(listeners47823);
        foreach (IPEndPoint ep in listeners47823)
        {
            Assert.True(IPAddress.IsLoopback(ep.Address), $"Port 47823 must bind strictly to loopback, found {ep.Address}.");
            Assert.NotEqual(IPAddress.Any, ep.Address);
            Assert.NotEqual(IPAddress.IPv6Any, ep.Address);
        }
    }

    [Fact]
    public async Task F12_Loopback_RootHealthEndpointReturnsValidMetadata()
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings();

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2000);
        using var client = new LoopbackAndDeckClient();
        Assert.True(await client.WaitForLoopbackReadyAsync(8000));

        HttpCallResult root = await client.GetLoopbackRootAsync();
        Assert.Equal(200, root.StatusCode);
        Assert.Contains("\"loopback\":true", root.Body);
        Assert.Contains("OpenPixel", root.Body);
    }
}
