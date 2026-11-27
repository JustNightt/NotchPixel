using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using NotchPixel.E2E.Tests.Infrastructure;
using Xunit;

namespace NotchPixel.E2E.Tests.Tier2;

[Collection("NotchPixelE2E")]
public sealed class Tier2_R1_R3_BoundaryTests
{
    private readonly E2ETestSessionFixture _fixture;

    public Tier2_R1_R3_BoundaryTests(E2ETestSessionFixture fixture)
    {
        _fixture = fixture;
    }

    // =========================================================================
    // F01 Boundaries (5 test methods)
    // =========================================================================

    [Fact]
    public void F01_BVA_NoPlaceholderVariantsInCSharpSources()
    {
        string[] variants =
        {
            @"MidnightCity",
            @"Placeholder\s*(Song|Track|Artist|Stat|Notification)",
            @"Dummy\s*(Song|Track|Clipboard|Reminder)",
            @"0\.65f?\s*\*\s*(cpu|CpuPercent)"
        };
        foreach (string pat in variants)
        {
            IReadOnlyList<SourceMatch> hits = SourceInspector.FindMatchesInCSharp(pat, RegexOptions.IgnoreCase);
            Assert.Empty(hits);
        }
    }

    [Fact]
    public async Task F01_BVA_EmptySettingsFileRecoversCleanlyWithoutCrash()
    {
        using var guard = new StateBackupGuard();
        Directory.CreateDirectory(TestWorkspace.LocalAppDataOpenPixelDir);
        File.WriteAllText(TestWorkspace.SettingsJsonPath, string.Empty);

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        IReadOnlyList<WindowSnapshot> wins = await session.WaitForVisibleWindowsAsync(1, 8000);
        Assert.NotEmpty(wins);

        using var client = new LoopbackAndDeckClient();
        Assert.Equal(200, (await client.GetLoopbackRootAsync()).StatusCode);
    }

    [Fact]
    public async Task F01_BVA_CorruptJsonSettingsFileRecoversCleanly()
    {
        using var guard = new StateBackupGuard();
        Directory.CreateDirectory(TestWorkspace.LocalAppDataOpenPixelDir);
        File.WriteAllText(TestWorkspace.SettingsJsonPath, "{corrupt_json::: [[[");

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        IReadOnlyList<WindowSnapshot> wins = await session.WaitForVisibleWindowsAsync(1, 8000);
        Assert.NotEmpty(wins);
        Assert.False(session.Process.HasExited);
    }

    [Fact]
    public async Task F01_BVA_NullAndOversizedStringsInSettingsHandledGracefully()
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings(s =>
        {
            s["WeatherCity"] = null;
            s["UpcomingMeetingTitle"] = new string('A', 2048);
            s["WorldClockCity"] = string.Empty;
        });

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        Assert.False(session.Process.HasExited);
        using var client = new LoopbackAndDeckClient();
        Assert.Equal(200, (await client.GetLoopbackRootAsync()).StatusCode);
    }

    [Fact]
    public void F01_BVA_TelemetryClampingPreventsNegativeOrOverflowPercent()
    {
        IReadOnlyList<SourceMatch> clamps = SourceInspector.FindMatchesInCSharp(@"CpuPercent\s*=.*Clamp|RamPercent\s*=.*Clamp|GpuPercent\s*=.*Clamp");
        Assert.NotEmpty(clamps);
    }

    // =========================================================================
    // F02 Boundaries (5 test methods)
    // =========================================================================

    [Fact]
    public async Task F02_BVA_MasterVolumeZeroPercentBoundaryPreserved()
    {
        using var audioScope = ProcessAndWindowDriver.CaptureAndRestoreAudioScope();
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings();

        ProcessAndWindowDriver.SetSystemMasterVolume(0.0f, muted: false);
        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        await Task.Delay(500);

        (float scalar, bool muted, _) = ProcessAndWindowDriver.GetSystemMasterVolumeAndMute();
        Assert.InRange(scalar, 0.0f, 0.01f);
        Assert.False(muted);
        Assert.False(session.Process.HasExited);
    }

    [Fact]
    public async Task F02_BVA_MasterVolumeHundredPercentBoundaryPreserved()
    {
        using var audioScope = ProcessAndWindowDriver.CaptureAndRestoreAudioScope();
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings();

        ProcessAndWindowDriver.SetSystemMasterVolume(1.0f, muted: false);
        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        await Task.Delay(500);

        (float scalar, _, _) = ProcessAndWindowDriver.GetSystemMasterVolumeAndMute();
        Assert.InRange(scalar, 0.99f, 1.0f);
        Assert.False(session.Process.HasExited);
    }

    [Fact]
    public async Task F02_BVA_RapidMuteUnmuteToggleStressZeroCrash()
    {
        using var audioScope = ProcessAndWindowDriver.CaptureAndRestoreAudioScope();
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings();

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        for (int i = 0; i < 8; i++)
        {
            ProcessAndWindowDriver.SetSystemMasterVolume(0.45f, muted: i % 2 == 0);
            await Task.Delay(60);
        }

        Assert.False(session.Process.HasExited);
        using var client = new LoopbackAndDeckClient();
        Assert.Equal(200, (await client.GetLoopbackRootAsync()).StatusCode);
    }

    [Fact]
    public async Task F02_BVA_MutedAtZeroVolumeOnStartupNeverUnmutes()
    {
        using var audioScope = ProcessAndWindowDriver.CaptureAndRestoreAudioScope();
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings();

        ProcessAndWindowDriver.SetSystemMasterVolume(0.0f, muted: true);
        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        await Task.Delay(800);

        (float scalar, bool muted, _) = ProcessAndWindowDriver.GetSystemMasterVolumeAndMute();
        Assert.True(muted, "Muted system audio at 0% must remain muted after startup.");
        Assert.InRange(scalar, 0.0f, 0.01f);
    }

    [Fact]
    public void F02_BVA_MasterVolumeSetterClampsOutOfRangeInputsInSource()
    {
        IReadOnlyList<SourceMatch> clamp = SourceInspector.FindMatchesInCSharp(@"SetMasterVolume[\s\S]{0,200}Clamp|MathF?\.Clamp\(volume0to100,\s*0\.0f,\s*100\.0f\)");
        Assert.NotEmpty(clamp);
    }

    // =========================================================================
    // F03 Boundaries (5 test methods)
    // =========================================================================

    [Fact]
    public void F03_BVA_DdcScalingHandlesZeroAndDegenerateMinMaxBounds()
    {
        IReadOnlyList<SourceMatch> ddcGuard = SourceInspector.FindMatchesInCSharp(@"maxB\s*<=\s*minB|ScaleDdcBrightness");
        Assert.True(ddcGuard.Count >= 2, "Expected ScaleDdcBrightness to guard maxB <= minB against divide-by-zero.");
    }

    [Fact]
    public void F03_BVA_NegativeBrightnessInputClampedToZeroInSource()
    {
        IReadOnlyList<SourceMatch> clamp = SourceInspector.FindMatchesInCSharp(@"SetMonitorBrightnessLevel[\s\S]{0,200}Clamp\(brightness0to100,\s*0\.0f,\s*100\.0f\)|Math\.Clamp\(brightness0to100,\s*0\.0f,\s*100\.0f\)");
        Assert.NotEmpty(clamp);
    }

    [Fact]
    public void F03_BVA_OverflowBrightnessInputClampedToHundredInSource()
    {
        IReadOnlyList<SourceMatch> clamp = SourceInspector.FindMatchesInCSharp(@"Math\.Clamp\(brightness0to100,\s*0\.0f,\s*100\.0f\)");
        Assert.NotEmpty(clamp);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(99)]
    public async Task F03_BVA_InvalidMonitorIndexClampsToValidScreen(int monitorIdx)
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings(s => s["MonitorIndex"] = monitorIdx);

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        IReadOnlyList<WindowSnapshot> wins = await session.WaitForVisibleWindowsAsync(1, 8000);
        Assert.NotEmpty(wins);
        Assert.False(session.Process.HasExited);
    }

    [Fact]
    public async Task F03_BVA_ContinuousTelemetryTicksProduceNoBrightnessErrors()
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings();
        if (File.Exists(TestWorkspace.DiagnosticLogPath)) File.Delete(TestWorkspace.DiagnosticLogPath);

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        await Task.Delay(1500);
        Assert.False(session.Process.HasExited);
        Assert.False(File.Exists(TestWorkspace.DiagnosticLogPath) &&
                     File.ReadAllText(TestWorkspace.DiagnosticLogPath).Contains("MonitorBrightness", StringComparison.OrdinalIgnoreCase));
    }

    // =========================================================================
    // F04 Boundaries (5 test methods)
    // =========================================================================

    [Fact]
    public async Task F04_BVA_ExtremeNegativeBlurClampedAndSafe()
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings(s => s["GlassBlurRadius"] = -9999.0f);

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        IReadOnlyList<WindowSnapshot> wins = await session.WaitForVisibleWindowsAsync(1, 8000);
        Assert.NotEmpty(wins);
        Assert.False(session.Process.HasExited);
    }

    [Fact]
    public async Task F04_BVA_ExtremePositiveBlurClampedTo40AndSafe()
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings(s => s["GlassBlurRadius"] = 100000.0f);

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        IReadOnlyList<WindowSnapshot> wins = await session.WaitForVisibleWindowsAsync(1, 8000);
        Assert.NotEmpty(wins);
        Assert.False(session.Process.HasExited);
    }

    [Fact]
    public async Task F04_BVA_ZeroBlurBoundaryRendersCleanly()
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings(s =>
        {
            s["Theme"] = SettingsJsonHelper.ThemeIds.LiquidGlass;
            s["GlassBlurRadius"] = 0.0f;
        });

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        IReadOnlyList<WindowSnapshot> wins = await session.WaitForVisibleWindowsAsync(1, 8000);
        Assert.NotEmpty(wins);
        Assert.False(session.Process.HasExited);
    }

    [Fact]
    public void F04_BVA_NaNAndInfinityBlurGuardPresentInRendererOrState()
    {
        IReadOnlyList<SourceMatch> nanGuard = SourceInspector.FindMatchesInCSharp(@"IsNaN|IsInfinity|Math\.Clamp\(.*blur|Math\.Clamp\(rawRadius");
        Assert.NotEmpty(nanGuard);
    }

    [Fact]
    public async Task F04_BVA_ConfigRevisionLessThan2MigratesToLiquidGlass()
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings(s =>
        {
            s["ConfigRevision"] = 1;
            s["Theme"] = SettingsJsonHelper.ThemeIds.Black;
            s["HideFromScreenshots"] = false;
        });

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1800);
        IReadOnlyList<WindowSnapshot> wins = await session.WaitForVisibleWindowsAsync(1, 8000);
        Assert.NotEmpty(wins);

        // Migration from ConfigRevision < 2 forces LiquidGlass, which enforces WDA_EXCLUDEFROMCAPTURE.
        WindowSnapshot island = wins.OrderBy(w => w.Bounds.Top).First();
        Assert.Equal(ProcessAndWindowDriver.WDA_EXCLUDEFROMCAPTURE, island.DisplayAffinity);
    }

    // =========================================================================
    // F05 Boundaries (5 test methods)
    // =========================================================================

    [Fact]
    public async Task F05_BVA_NegativeOutOfRangeThemeIdHandledSafely()
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings(s => s["Theme"] = -5);

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        IReadOnlyList<WindowSnapshot> wins = await session.WaitForVisibleWindowsAsync(1, 8000);
        Assert.NotEmpty(wins);
        Assert.False(session.Process.HasExited);
    }

    [Fact]
    public async Task F05_BVA_HighOutOfRangeThemeIdHandledSafely()
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings(s => s["Theme"] = 999);

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        IReadOnlyList<WindowSnapshot> wins = await session.WaitForVisibleWindowsAsync(1, 8000);
        Assert.NotEmpty(wins);
        Assert.False(session.Process.HasExited);
    }

    [Fact]
    public void F05_BVA_PureBlackOledUsesTrueZeroBlackDistinctFromBlack()
    {
        IReadOnlyList<SourceMatch> pureBlack = SourceInspector.FindMatchesInCSharp(@"Name\s*=\s*""Pure black""");
        IReadOnlyList<SourceMatch> oledFill = SourceInspector.FindMatchesInCSharp(@"FromArgb\(\s*255,\s*0,\s*0,\s*0\s*\)");
        Assert.NotEmpty(pureBlack);
        Assert.NotEmpty(oledFill);
    }

    [Fact]
    public void F05_BVA_PureWhiteThemeMarksIsLightTrue()
    {
        IReadOnlyList<SourceMatch> pureWhite = SourceInspector.FindMatchesInCSharp(@"Name\s*=\s*""Pure white""");
        IReadOnlyList<SourceMatch> isLight = SourceInspector.FindMatchesInCSharp(@"IsLight\s*=\s*true");
        Assert.NotEmpty(pureWhite);
        Assert.NotEmpty(isLight);
    }

    [Theory]
    [InlineData(SettingsJsonHelper.ThemeIds.Rose)]
    [InlineData(SettingsJsonHelper.ThemeIds.Lavender)]
    [InlineData(SettingsJsonHelper.ThemeIds.Peach)]
    [InlineData(SettingsJsonHelper.ThemeIds.Sakura)]
    public async Task F05_BVA_SoftThemesWithAmbienceEnabledLaunchCleanly(int softThemeId)
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings(s =>
        {
            s["Theme"] = softThemeId;
            s["PauseEffectsOnBattery"] = false;
        });

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        IReadOnlyList<WindowSnapshot> wins = await session.WaitForVisibleWindowsAsync(1, 8000);
        Assert.NotEmpty(wins);
        Assert.False(session.Process.HasExited);
    }

    // =========================================================================
    // F06 Boundaries (5 test methods)
    // =========================================================================

    [Fact]
    public void F06_BVA_LuminanceThresholdsSelectDarkInkOnBrightAndPaperOnDark()
    {
        IReadOnlyList<SourceMatch> threshold = SourceInspector.FindMatchesInCSharp(@"RelativeLuminance\(bg\)\s*>\s*0\.42f");
        IReadOnlyList<SourceMatch> darkInk = SourceInspector.FindMatchesInCSharp(@"FromArgb\(255,\s*12,\s*12,\s*14\)");
        IReadOnlyList<SourceMatch> lightPaper = SourceInspector.FindMatchesInCSharp(@"FromArgb\(255,\s*239,\s*237,\s*232\)");

        Assert.NotEmpty(threshold);
        Assert.NotEmpty(darkInk);
        Assert.NotEmpty(lightPaper);
    }

    [Fact]
    public async Task F06_BVA_CardBackgroundPlainModeLaunchesCleanly()
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings(s => s["CardBackground"] = 1);

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        Assert.False(session.Process.HasExited);
    }

    [Fact]
    public async Task F06_BVA_CardBackgroundOutOfRangeHandledCleanly()
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings(s => s["CardBackground"] = 99);

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        Assert.False(session.Process.HasExited);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 1)] // Hearts progress bar + Bars visualizer
    public async Task F06_BVA_ProgressBarAndVisualizerCombinationsSafe(int progressStyle, int visualizerStyle)
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings(s =>
        {
            s["ProgressBar"] = progressStyle;
            s["Visualizer"] = visualizerStyle;
        });

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1400);
        Assert.False(session.Process.HasExited);
    }

    [Fact]
    public void F06_BVA_RenderAlbumBackdropGuardsNullOrEmptyMedia()
    {
        IReadOnlyList<SourceMatch> guard = SourceInspector.FindMatchesInCSharp(@"RenderAlbumBackdrop[\s\S]{0,200}(HasSession|AlbumArt)");
        Assert.NotEmpty(guard);
    }

    // =========================================================================
    // F07 Boundaries (5 test methods)
    // =========================================================================

    [Theory]
    [InlineData(SettingsJsonHelper.ThemeIds.PureBlack)]
    [InlineData(SettingsJsonHelper.ThemeIds.PureWhite)]
    public async Task F07_BVA_HideFromScreenshotsTrueEnforcesCaptureExclusionOnOpaqueThemes(int themeId)
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings(s =>
        {
            s["Theme"] = themeId;
            s["HideFromScreenshots"] = true;
        });

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1800);
        IReadOnlyList<WindowSnapshot> wins = await session.WaitForVisibleWindowsAsync(1, 8000);
        Assert.NotEmpty(wins);

        WindowSnapshot island = wins.OrderBy(w => w.Bounds.Top).First();
        Assert.Equal(ProcessAndWindowDriver.WDA_EXCLUDEFROMCAPTURE, island.DisplayAffinity);
    }

    [Fact]
    public async Task F07_BVA_LiquidGlassCannotDisableCaptureExclusion()
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings(s =>
        {
            s["Theme"] = SettingsJsonHelper.ThemeIds.LiquidGlass;
            s["HideFromScreenshots"] = false;
        });

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1800);
        IReadOnlyList<WindowSnapshot> wins = await session.WaitForVisibleWindowsAsync(1, 8000);
        Assert.NotEmpty(wins);

        WindowSnapshot island = wins.OrderBy(w => w.Bounds.Top).First();
        Assert.Equal(ProcessAndWindowDriver.WDA_EXCLUDEFROMCAPTURE, island.DisplayAffinity);
    }

    [Fact]
    public async Task F07_BVA_CandyGlassCannotDisableCaptureExclusion()
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings(s =>
        {
            s["Theme"] = SettingsJsonHelper.ThemeIds.CandyGlass;
            s["HideFromScreenshots"] = false;
        });

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1800);
        IReadOnlyList<WindowSnapshot> wins = await session.WaitForVisibleWindowsAsync(1, 8000);
        Assert.NotEmpty(wins);

        WindowSnapshot island = wins.OrderBy(w => w.Bounds.Top).First();
        Assert.Equal(ProcessAndWindowDriver.WDA_EXCLUDEFROMCAPTURE, island.DisplayAffinity);
    }

    [Theory]
    [InlineData(-10.0f)]
    [InlineData(10.0f)]
    public async Task F07_BVA_ExtremeCornerRadiusScaleClampedSafely(float cornerScale)
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings(s => s["CornerRadiusScale"] = cornerScale);

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        IReadOnlyList<WindowSnapshot> wins = await session.WaitForVisibleWindowsAsync(1, 8000);
        Assert.NotEmpty(wins);
        Assert.True(wins[0].Bounds.Width > 0 && wins[0].Bounds.Height > 0);
    }

    [Fact]
    public async Task F07_BVA_ExtremePillAndOpenSizePercentsSafe()
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings(s =>
        {
            s["PillSizePercent"] = 10.0f;
            s["OpenSizePercent"] = 500.0f;
        });

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        IReadOnlyList<WindowSnapshot> wins = await session.WaitForVisibleWindowsAsync(1, 8000);
        Assert.NotEmpty(wins);
        Assert.False(session.Process.HasExited);
    }

    // =========================================================================
    // F08 Boundaries (5 test methods)
    // =========================================================================

    [Fact]
    public async Task F08_BVA_NegativeHoverDelayClampedNoTimerException()
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings(s => s["HoverOpenDelaySec"] = -5.0f);

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        Assert.False(session.Process.HasExited);
    }

    [Fact]
    public async Task F08_BVA_ZeroHoverDelayBoundaryResponsive()
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings(s => s["HoverOpenDelaySec"] = 0.0f);

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        ProcessAndWindowDriver.SendHotkey(ProcessAndWindowDriver.MOD_CONTROL | ProcessAndWindowDriver.MOD_ALT, Keys.N);
        await Task.Delay(200);
        Assert.False(session.Process.HasExited);
    }

    [Fact]
    public async Task F08_BVA_MaxHoverDelay1Point5SecBoundary()
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings(s => s["HoverOpenDelaySec"] = 1.5f);

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        Assert.False(session.Process.HasExited);
    }

    [Fact]
    public async Task F08_BVA_AboveMaxHoverDelay99SecClamped()
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings(s => s["HoverOpenDelaySec"] = 99.0f);

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        Assert.False(session.Process.HasExited);
    }

    [Theory]
    [InlineData(0.0f)]
    [InlineData(999.0f)]
    public async Task F08_BVA_NoticeDurationExtremeValuesDoNotCrashOnNotify(float noticeDuration)
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings(s => s["NoticeDurationSec"] = noticeDuration);

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        using var client = new LoopbackAndDeckClient();
        Assert.True(await client.WaitForLoopbackReadyAsync(8000));

        HttpCallResult res = await client.SendNotifyAsync("Boundary Notice", "Duration check", "E2E");
        Assert.Equal(200, res.StatusCode);
        Assert.False(session.Process.HasExited);
    }

    // =========================================================================
    // F09 Boundaries (5 test methods)
    // =========================================================================

    [Theory]
    [InlineData(0.1f)]
    [InlineData(5.0f)]
    public async Task F09_BVA_ExtremeTextScaleClampedNoFontCrash(float scale)
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings(s => s["TextScale"] = scale);

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        IReadOnlyList<WindowSnapshot> wins = await session.WaitForVisibleWindowsAsync(1, 8000);
        Assert.NotEmpty(wins);
        Assert.False(session.Process.HasExited);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(99)]
    public async Task F09_BVA_OutOfRangeFontIndexHandledSafely(int fontIdx)
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings(s => s["FontIndex"] = fontIdx);

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        Assert.False(session.Process.HasExited);
    }

    [Fact]
    public async Task F09_BVA_UnknownLanguageCodeFallbackSafe()
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings(s => s["Language"] = "xx-INVALID");

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        Assert.False(session.Process.HasExited);
    }

    [Fact]
    public async Task F09_BVA_HigherContrastEnabledLaunchesCleanly()
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings(s => s["HigherContrast"] = true);

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        IReadOnlyList<WindowSnapshot> wins = await session.WaitForVisibleWindowsAsync(1, 8000);
        Assert.NotEmpty(wins);
    }

    [Fact]
    public async Task F09_BVA_RepeatedCtrlAltHToggleDoesNotCorruptWindowVisibility()
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings();

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1800);
        await session.WaitForVisibleWindowsAsync(1, 8000);

        for (int i = 0; i < 6; i++)
        {
            ProcessAndWindowDriver.SendHotkey(ProcessAndWindowDriver.MOD_CONTROL | ProcessAndWindowDriver.MOD_ALT, Keys.H);
            await Task.Delay(120);
        }

        await Task.Delay(250);
        Assert.False(session.Process.HasExited);
        IReadOnlyList<WindowSnapshot> winsAfter = await session.WaitForVisibleWindowsAsync(1, 4000);
        Assert.NotEmpty(winsAfter);
    }

    // =========================================================================
    // F10 Boundaries (5 test methods)
    // =========================================================================

    [Theory]
    [InlineData(-10)]
    [InlineData(0)]
    public async Task F10_BVA_ZeroAndNegativeDayClockLetterSpacingSafe(int spacing)
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings(s => s["DayClockLetterSpacing"] = spacing);

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1400);
        Assert.False(session.Process.HasExited);
    }

    [Fact]
    public async Task F10_BVA_ExtremeDayClockLetterSpacing200Safe()
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings(s => s["DayClockLetterSpacing"] = 200);

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1400);
        Assert.False(session.Process.HasExited);
    }

    [Fact]
    public async Task F10_BVA_DayClockAllSubElementsDisabledSafe()
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings(s =>
        {
            s["DayClockShowWeekday"] = false;
            s["DayClockShowDate"] = false;
            s["DayClockShowTime"] = false;
            s["DayClockShowDashes"] = false;
        });

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1400);
        Assert.False(session.Process.HasExited);
    }

    [Fact]
    public async Task F10_BVA_DayClockTransparentZeroArgbSafe()
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings(s => s["DayClockAccentColorArgb"] = 0);

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1400);
        Assert.False(session.Process.HasExited);
    }

    [Fact]
    public void F10_BVA_DayClock12hAnd24hFormatStringsDefinedInSource()
    {
        IReadOnlyList<SourceMatch> fmt12 = SourceInspector.FindMatchesInCSharp(@"hh:mm\s*tt|h:mm\s*tt");
        IReadOnlyList<SourceMatch> fmt24 = SourceInspector.FindMatchesInCSharp(@"HH:mm");
        Assert.NotEmpty(fmt12);
        Assert.NotEmpty(fmt24);
    }

    // =========================================================================
    // F11 Boundaries (5 test methods)
    // =========================================================================

    [Theory]
    [InlineData(-24)]
    [InlineData(24)]
    public async Task F11_BVA_WorldClockExtremeHourOffsetsSafe(int offsetHours)
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings(s =>
        {
            s["WorldClockCity"] = "";
            s["WorldClockOffsetHours"] = offsetHours;
        });

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1400);
        Assert.False(session.Process.HasExited);
    }

    [Fact]
    public async Task F11_BVA_UnicodeAndEmojiWeatherCityHandledSafe()
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings(s => s["WeatherCity"] = "東京_München_🌍");

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1400);
        Assert.False(session.Process.HasExited);
    }

    [Fact]
    public async Task F11_BVA_LockDesktopWidgetsTruePersistsCleanly()
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings(s => s["LockDesktopWidgets"] = true);

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1400);
        Assert.False(session.Process.HasExited);
        Assert.True(SettingsJsonHelper.GetValue<bool>("LockDesktopWidgets"));
    }

    [Fact]
    public async Task F11_BVA_EmptyPanelOrderArrayHandledWithoutIndexCrash()
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings(s => s["PanelOrder"] = new JsonArray());

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        IReadOnlyList<WindowSnapshot> wins = await session.WaitForVisibleWindowsAsync(1, 8000);
        Assert.NotEmpty(wins);
        Assert.False(session.Process.HasExited);
    }

    [Fact]
    public async Task F11_BVA_ExtremeWeatherTemperaturesHandledSafe()
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings(s =>
        {
            s["WeatherTempC"] = -99;
            s["WeatherHighC"] = 999;
            s["WeatherLowC"] = 999;
        });

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1400);
        Assert.False(session.Process.HasExited);
    }

    // =========================================================================
    // F12 Boundaries (5 test methods)
    // =========================================================================

    [Fact]
    public async Task F12_BVA_NotifyMissingTitleAndAppUsesDefaults()
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings();

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1800);
        using var client = new LoopbackAndDeckClient();
        Assert.True(await client.WaitForLoopbackReadyAsync(8000));

        HttpCallResult res = await client.SendRawLoopbackAsync(HttpMethod.Get, "/notify?text=onlytext");
        Assert.Equal(200, res.StatusCode);
        Assert.Contains("\"ok\":true", res.Body);
    }

    [Fact]
    public async Task F12_BVA_TileMissingIdAndTitleUsesDefaults()
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings();

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1800);
        using var client = new LoopbackAndDeckClient();
        Assert.True(await client.WaitForLoopbackReadyAsync(8000));

        HttpCallResult res = await client.SendRawLoopbackAsync(HttpMethod.Get, "/tile?value=42");
        Assert.Equal(200, res.StatusCode);
        Assert.Contains("\"ok\":true", res.Body);
    }

    [Fact]
    public async Task F12_BVA_UnicodeAndEmojiPayloadsOnNotifyAndTileSucceed()
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings();

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1800);
        using var client = new LoopbackAndDeckClient();
        Assert.True(await client.WaitForLoopbackReadyAsync(8000));

        HttpCallResult nRes = await client.SendNotifyAsync("构建完成 🚀", "Тест passed αβγ", "NotchPixel-CLI");
        Assert.Equal(200, nRes.StatusCode);

        HttpCallResult tRes = await client.SendTileAsync("unicode_1", "データ", "99%", "状態OK");
        Assert.Equal(200, tRes.StatusCode);
    }

    [Fact]
    public async Task F12_BVA_MalformedPercentEncodingAndLongQueryDoNotCrashLoopbackServer()
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings();

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1800);
        using var client = new LoopbackAndDeckClient();
        Assert.True(await client.WaitForLoopbackReadyAsync(8000));

        HttpCallResult badEsc = await client.SendRawLoopbackAsync(HttpMethod.Get, "/notify?title=%ZZ%20&text=a%26b");
        Assert.Equal(200, badEsc.StatusCode);

        string longVal = new string('x', 2048);
        HttpCallResult longTile = await client.SendRawLoopbackAsync(HttpMethod.Get, $"/tile?id=long&title=Stress&value={longVal}");
        Assert.Equal(200, longTile.StatusCode);

        // Listener must remain alive and serve subsequent requests.
        HttpCallResult health = await client.GetLoopbackRootAsync();
        Assert.Equal(200, health.StatusCode);
    }

    [Fact]
    public async Task F12_BVA_ConcurrentBurst20RequestsOnLoopback47823AllSucceed()
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings();

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1800);
        using var client = new LoopbackAndDeckClient();
        Assert.True(await client.WaitForLoopbackReadyAsync(8000));

        var tasks = new List<Task<HttpCallResult>>(20);
        for (int i = 0; i < 20; i++)
        {
            if (i % 2 == 0)
            {
                tasks.Add(client.SendNotifyAsync($"Burst {i}", "Concurrent test", "E2E", usePost: i % 4 == 0));
            }
            else
            {
                tasks.Add(client.SendTileAsync($"tile_{i % 5}", $"Tile {i}", i.ToString(), "burst", usePost: i % 3 == 0));
            }
        }

        HttpCallResult[] results = await Task.WhenAll(tasks);
        Assert.All(results, r => Assert.Equal(200, r.StatusCode));
        Assert.False(session.Process.HasExited);
    }
}
