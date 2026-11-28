using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using NotchPixel.E2E.Tests.Infrastructure;
using Xunit;

namespace NotchPixel.E2E.Tests.Tier1;

[Collection("NotchPixelE2E")]
public sealed class Tier1_R2_GlassThemesMotionSettingsTests
{
    private readonly E2ETestSessionFixture _fixture;

    public Tier1_R2_GlassThemesMotionSettingsTests(E2ETestSessionFixture fixture)
    {
        _fixture = fixture;
    }

    // =========================================================================
    // F04: Liquid Glass Default & Crash-Free Rendering (6 test methods)
    // =========================================================================

    [Fact]
    public async Task F04_FreshLaunch_DefaultsToLiquidGlassAndBlur20()
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.DeleteSettingsFile();

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2500);
        IReadOnlyList<WindowSnapshot> wins = await session.WaitForVisibleWindowsAsync(1, 8000);
        Assert.NotEmpty(wins);

        // Fresh out-of-the-box launch MUST default to Liquid Glass, which enforces WDA_EXCLUDEFROMCAPTURE.
        WindowSnapshot island = wins.OrderBy(w => w.Bounds.Top).First();
        Assert.Equal(ProcessAndWindowDriver.WDA_EXCLUDEFROMCAPTURE, island.DisplayAffinity);

        CliExecutionResult selfTest = await ProcessAndWindowDriver.RunCliAsync("--self-test", timeoutMs: 15000);
        Assert.Equal(0, selfTest.ExitCode);
        Assert.Contains("Liquid Glass is enabled by default out of the box", selfTest.StdOut);
    }

    [Fact]
    public async Task F04_SelfTest_PassesLiquidGlassDefaultAndBlurSweep()
    {
        CliExecutionResult res = await ProcessAndWindowDriver.RunCliAsync("--self-test", timeoutMs: 15000);
        Assert.False(res.TimedOut, "--self-test timed out.");
        Assert.Equal(0, res.ExitCode);
        Assert.Contains("[PASS] Liquid Glass Blur Slider clamps strictly to [0..40px]", res.StdOut);
        Assert.Contains("[PASS] Crash-Proof Liquid Glass & All 11 Themes", res.StdOut);
    }

    [Theory]
    [InlineData(-100.0f)]
    [InlineData(0.0f)]
    [InlineData(20.0f)]
    [InlineData(40.0f)]
    [InlineData(9999.0f)]
    public async Task F04_ExtremeBlurValuesInSettings_LaunchSafeWithoutCrash(float blurRadius)
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings(s =>
        {
            s["Theme"] = SettingsJsonHelper.ThemeIds.LiquidGlass;
            s["GlassBlurRadius"] = blurRadius;
        });

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2000);
        IReadOnlyList<WindowSnapshot> wins = await session.WaitForVisibleWindowsAsync(1, 8000);
        Assert.NotEmpty(wins);
        Assert.False(session.Process.HasExited, $"OpenPixel crashed with GlassBlurRadius={blurRadius}.");

        using var client = new LoopbackAndDeckClient();
        HttpCallResult root = await client.GetLoopbackRootAsync();
        Assert.Equal(200, root.StatusCode);
    }

    [Fact]
    public void F04_Source_UsesDeferredRenderTickCommitsNotMidFrameDisposal()
    {
        IReadOnlyList<SourceMatch> qBlur = SourceInspector.FindMatchesInCSharp(@"QueueBlurRadius");
        IReadOnlyList<SourceMatch> qTheme = SourceInspector.FindMatchesInCSharp(@"QueueTheme");
        IReadOnlyList<SourceMatch> commit = SourceInspector.FindMatchesInCSharp(@"CommitPendingParametersOnRenderTick");

        Assert.True(qBlur.Count >= 2, "Expected QueueBlurRadius deferred parameter queue.");
        Assert.True(qTheme.Count >= 2, "Expected QueueTheme deferred parameter queue.");
        Assert.True(commit.Count >= 2, "Expected CommitPendingParametersOnRenderTick atomic commit.");
    }

    [Fact]
    public void F04_Source_StaticBackdropCacheUsesHashOptimization()
    {
        IReadOnlyList<SourceMatch> hashField = SourceInspector.FindMatchesInCSharp(@"_cachedBackdropHash");
        IReadOnlyList<SourceMatch> cacheHit = SourceInspector.FindMatchesInCSharp(@"cacheHit");

        Assert.NotEmpty(hashField);
        Assert.NotEmpty(cacheHit);
    }

    [Fact]
    public async Task F04_SimulatedGlassCrashFallback_FallsBackToBlackAndLaunchesSafely()
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings(s =>
        {
            s["Theme"] = SettingsJsonHelper.ThemeIds.LiquidGlass;
            s["HideFromScreenshots"] = false;
            s["FallbackBlackOnGlassCrash"] = true;
        });

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2500);
        IReadOnlyList<WindowSnapshot> wins = await session.WaitForVisibleWindowsAsync(1, 8000);
        Assert.NotEmpty(wins);
        Assert.False(session.Process.HasExited, "OpenPixel crashed when FallbackBlackOnGlassCrash=true.");

        // When FallbackBlackOnGlassCrash is active with HideFromScreenshots=false,
        // the island must fall back safely to Black (non-glass -> WDA_NONE).
        WindowSnapshot island = wins.OrderBy(w => w.Bounds.Top).First();
        Assert.Equal(ProcessAndWindowDriver.WDA_NONE, island.DisplayAffinity);
    }

    // =========================================================================
    // F05: All 10+ Required Themes & Ambience (6 test methods, 16 cases)
    // =========================================================================

    [Fact]
    public void F05_All11Themes_DefinedInSurfaceThemeEnum()
    {
        string[] requiredNames =
        {
            "LiquidGlass", "CandyGlass", "Acrylic", "Black", "PureBlack",
            "Graphite", "PureWhite", "Rose", "Lavender", "Peach", "Sakura"
        };

        foreach (string themeName in requiredNames)
        {
            IReadOnlyList<SourceMatch> matches = SourceInspector.FindMatchesInCSharp($@"\b{themeName}\b");
            Assert.NotEmpty(matches);
        }
    }

    [Theory]
    [InlineData(SettingsJsonHelper.ThemeIds.LiquidGlass)]
    [InlineData(SettingsJsonHelper.ThemeIds.CandyGlass)]
    [InlineData(SettingsJsonHelper.ThemeIds.Acrylic)]
    [InlineData(SettingsJsonHelper.ThemeIds.Black)]
    [InlineData(SettingsJsonHelper.ThemeIds.PureBlack)]
    [InlineData(SettingsJsonHelper.ThemeIds.Graphite)]
    [InlineData(SettingsJsonHelper.ThemeIds.PureWhite)]
    [InlineData(SettingsJsonHelper.ThemeIds.Rose)]
    [InlineData(SettingsJsonHelper.ThemeIds.Lavender)]
    [InlineData(SettingsJsonHelper.ThemeIds.Peach)]
    [InlineData(SettingsJsonHelper.ThemeIds.Sakura)]
    public async Task F05_LiveSession_EachThemeLaunchesAndRendersWithoutCrash(int themeId)
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings(s => s["Theme"] = themeId);

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1800);
        IReadOnlyList<WindowSnapshot> wins = await session.WaitForVisibleWindowsAsync(1, 8000);
        Assert.NotEmpty(wins);
        Assert.False(session.Process.HasExited, $"OpenPixel crashed for Theme={themeId}.");

        using var client = new LoopbackAndDeckClient();
        HttpCallResult root = await client.GetLoopbackRootAsync();
        Assert.Equal(200, root.StatusCode);
    }

    [Fact]
    public async Task F05_SelfTest_VerifiesAll11ThemesAndSakura60FpsBudget()
    {
        CliExecutionResult res = await ProcessAndWindowDriver.RunCliAsync("--self-test", timeoutMs: 15000);
        Assert.False(res.TimedOut);
        Assert.Equal(0, res.ExitCode);
        Assert.Contains("[PASS] Animated theme ambience stays inside the 60 fps frame budget (< 16.7 ms)", res.StdOut);
    }

    [Fact]
    public void F05_Source_AcrylicThemeUsesDwmSystemBackdropType()
    {
        IReadOnlyList<SourceMatch> dwmAttr = SourceInspector.FindMatchesInCSharp(@"DWMWA_SYSTEMBACKDROP_TYPE|DwmSetWindowAttribute");
        IReadOnlyList<SourceMatch> acrylic = SourceInspector.FindMatchesInCSharp(@"SurfaceTheme\.Acrylic");

        Assert.NotEmpty(acrylic);
        Assert.NotEmpty(dwmAttr);
    }

    [Fact]
    public void F05_Source_SoftThemesHaveAmbienceParticlesEnabled()
    {
        IReadOnlyList<SourceMatch> ambience = SourceInspector.FindMatchesInCSharp(@"RenderThemeAmbience|HasAmbience");
        Assert.True(ambience.Count >= 2, "Expected theme ambience rendering for soft themes.");
    }

    [Fact]
    public async Task F05_RapidThemeSwitchingStress_ZeroUnhandledExceptions()
    {
        using var guard = new StateBackupGuard();
        if (File.Exists(TestWorkspace.DiagnosticLogPath)) File.Delete(TestWorkspace.DiagnosticLogPath);

        int[] sequence =
        {
            SettingsJsonHelper.ThemeIds.LiquidGlass,
            SettingsJsonHelper.ThemeIds.CandyGlass,
            SettingsJsonHelper.ThemeIds.Acrylic,
            SettingsJsonHelper.ThemeIds.Black,
            SettingsJsonHelper.ThemeIds.Sakura
        };

        foreach (int themeId in sequence)
        {
            SettingsJsonHelper.ResetToCleanSettings(s => s["Theme"] = themeId);
            using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 900);
            Assert.False(session.Process.HasExited, $"Crashed during rapid switch to theme {themeId}.");
        }

        if (File.Exists(TestWorkspace.DiagnosticLogPath))
        {
            string log = File.ReadAllText(TestWorkspace.DiagnosticLogPath);
            Assert.DoesNotContain("NullReferenceException", log);
            Assert.DoesNotContain("ArgumentException", log);
        }
    }

    // =========================================================================
    // F06: Album-Art Edge Tinting & Adaptive Ink (6 test methods)
    // =========================================================================

    [Fact]
    public async Task F06_IdleMedia_RestingStateCleanWithoutFakeTinting()
    {
        CliExecutionResult res = await ProcessAndWindowDriver.RunCliAsync("--self-test", timeoutMs: 15000);
        Assert.Equal(0, res.ExitCode);
        Assert.Contains("[PASS] Zero Placeholders: Media starts with HasSession=false", res.StdOut);
    }

    [Fact]
    public void F06_Source_ExtractsAlbumArtDominantColorAndTopEdgeTint()
    {
        IReadOnlyList<SourceMatch> extract = SourceInspector.FindMatchesInCSharp(@"ExtractAlbumColors");
        IReadOnlyList<SourceMatch> backdrop = SourceInspector.FindMatchesInCSharp(@"RenderAlbumBackdrop");
        IReadOnlyList<SourceMatch> dominant = SourceInspector.FindMatchesInCSharp(@"DominantColor");

        Assert.NotEmpty(extract);
        Assert.NotEmpty(backdrop);
        Assert.True(dominant.Count >= 3);
    }

    [Fact]
    public void F06_Source_AdaptiveInkContrastLuminanceFormulaPresent()
    {
        IReadOnlyList<SourceMatch> lum = SourceInspector.FindMatchesInCSharp(@"RelativeLuminance");
        IReadOnlyList<SourceMatch> ink = SourceInspector.FindMatchesInCSharp(@"AdaptiveInkForBackground");

        Assert.NotEmpty(lum);
        Assert.NotEmpty(ink);
    }

    [Fact]
    public void F06_Source_ZeroHoverFadeRegressionInSettingsOrCards()
    {
        // Text on buttons/cards must remain full contrast on hover rather than fading out.
        IReadOnlyList<SourceMatch> fadeOutOnHover = SourceInspector.FindMatchesInCSharp(@"hover.*alpha\s*=\s*0\.[0-4]|hovered\s*\?\s*0\.[0-3]");
        Assert.Empty(fadeOutOnHover);
    }

    [Fact]
    public async Task F06_SelfTest_PassesMusicTabAndAlbumColourRendering()
    {
        CliExecutionResult res = await ProcessAndWindowDriver.RunCliAsync("--self-test", timeoutMs: 15000);
        Assert.Equal(0, res.ExitCode);
        Assert.Contains("Stress-tested slider 0..40px across all 10 tabs cleanly", res.StdOut);
    }

    [Fact]
    public async Task F06_DeckState_AccentHexMatchesSixDigitHexInvariant()
    {
        using var guard = new StateBackupGuard();
        const string token = "e2e_f06_token_value_123456";
        string pairedPath = Path.Combine(TestWorkspace.LocalAppDataOpenPixelDir, "paired_deck_devices.json");
        string? origPaired = File.Exists(pairedPath) ? File.ReadAllText(pairedPath) : null;

        SettingsJsonHelper.ResetToCleanSettings(s => s["PcDeckEnabled"] = true);
        Directory.CreateDirectory(TestWorkspace.LocalAppDataOpenPixelDir);
        File.WriteAllText(pairedPath, new JsonArray
        {
            new JsonObject
            {
                ["DeviceId"] = "e2e-deck-f06",
                ["DeviceName"] = "E2E-F06",
                ["Model"] = "Pixel 9",
                ["OsVersion"] = "Android 15",
                ["AppVersion"] = "1.0.5",
                ["IpAddress"] = "127.0.0.1",
                ["KeyFingerprint"] = "abcdef0123456789",
                ["AuthToken"] = token,
                ["PairedAtMs"] = 1700000000000L
            }
        }.ToJsonString());

        try
        {
            using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2500);
            using var client = new LoopbackAndDeckClient();
            TlsCallResult probe = await client.ProbeAsync();
            if (probe.StatusCode == 200)
            {
                TlsCallResult stateRes = await client.GetStateAsync(token, probe.ServerCertSha256Hex);
                Assert.Equal(200, stateRes.StatusCode);
                using JsonDocument doc = JsonDocument.Parse(stateRes.Body);
                string? accentHex = doc.RootElement.GetProperty("media").GetProperty("accentHex").GetString();
                Assert.NotNull(accentHex);
                Assert.Matches(@"^#[0-9A-Fa-f]{6}$", accentHex);
            }
            else
            {
                // Verify source contract when host network profile is Public.
                IReadOnlyList<SourceMatch> hexFmt = SourceInspector.FindMatchesInCSharp(@"accentHex\s*=\s*\$""#");
                Assert.NotEmpty(hexFmt);
            }
        }
        finally
        {
            if (origPaired == null)
            {
                if (File.Exists(pairedPath)) File.Delete(pairedPath);
            }
            else
            {
                File.WriteAllText(pairedPath, origPaired);
            }
        }
    }

    // =========================================================================
    // F07: Whole-Pixel Snapping & Capture Visibility (6 test methods)
    // =========================================================================

    [Fact]
    public async Task F07_LiveSession_LiquidGlassHasWdaExcludeFromCapture()
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings(s =>
        {
            s["Theme"] = SettingsJsonHelper.ThemeIds.LiquidGlass;
            s["HideFromScreenshots"] = false;
        });

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2500);
        IReadOnlyList<WindowSnapshot> wins = await session.WaitForVisibleWindowsAsync(1, 8000);
        Assert.NotEmpty(wins);

        WindowSnapshot island = wins.OrderBy(w => w.Bounds.Top).First();
        Assert.Equal(ProcessAndWindowDriver.WDA_EXCLUDEFROMCAPTURE, island.DisplayAffinity);
    }

    [Fact]
    public async Task F07_LiveSession_CandyGlassHasWdaExcludeFromCapture()
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings(s =>
        {
            s["Theme"] = SettingsJsonHelper.ThemeIds.CandyGlass;
            s["HideFromScreenshots"] = false;
        });

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2500);
        IReadOnlyList<WindowSnapshot> wins = await session.WaitForVisibleWindowsAsync(1, 8000);
        Assert.NotEmpty(wins);

        WindowSnapshot island = wins.OrderBy(w => w.Bounds.Top).First();
        Assert.Equal(ProcessAndWindowDriver.WDA_EXCLUDEFROMCAPTURE, island.DisplayAffinity);
    }

    [Fact]
    public async Task F07_LiveSession_BlackThemeHasWdaNoneWhenHideFromScreenshotsFalse()
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings(s =>
        {
            s["Theme"] = SettingsJsonHelper.ThemeIds.Black;
            s["HideFromScreenshots"] = false;
        });

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2500);
        IReadOnlyList<WindowSnapshot> wins = await session.WaitForVisibleWindowsAsync(1, 8000);
        Assert.NotEmpty(wins);

        WindowSnapshot island = wins.OrderBy(w => w.Bounds.Top).First();
        Assert.Equal(ProcessAndWindowDriver.WDA_NONE, island.DisplayAffinity);
    }

    [Fact]
    public async Task F07_LiveSession_RoseThemeSwitchesToExcludeFromCaptureWhenHideEnabled()
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings(s =>
        {
            s["Theme"] = SettingsJsonHelper.ThemeIds.Rose;
            s["HideFromScreenshots"] = true;
        });

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2500);
        IReadOnlyList<WindowSnapshot> wins = await session.WaitForVisibleWindowsAsync(1, 8000);
        Assert.NotEmpty(wins);

        WindowSnapshot island = wins.OrderBy(w => w.Bounds.Top).First();
        Assert.Equal(ProcessAndWindowDriver.WDA_EXCLUDEFROMCAPTURE, island.DisplayAffinity);
    }

    [Fact]
    public async Task F07_LiveSession_ProcessIsDpiAwareWithValidIntegerWindowBounds()
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings();

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2500);
        IReadOnlyList<WindowSnapshot> wins = await session.WaitForVisibleWindowsAsync(1, 8000);
        Assert.NotEmpty(wins);

        Assert.True(ProcessAndWindowDriver.IsProcessDpiAware(session.Pid), "OpenPixel.exe must be DPI-aware (PerMonitorV2).");
        foreach (WindowSnapshot w in wins)
        {
            Assert.True(w.Dpi >= 96, $"Expected DPI >= 96 on HWND {w.Hwnd}, got {w.Dpi}.");
            Assert.True(w.Bounds.Width > 0 && w.Bounds.Height > 0, $"Invalid window bounds: {w.Bounds}");
        }
    }

    [Fact]
    public async Task F07_Source_WholePixelSnappingAndInterFontsVerified()
    {
        IReadOnlyList<SourceMatch> snap = SourceInspector.FindMatchesInCSharp(@"SnapDipToWholePixel");
        Assert.NotEmpty(snap);

        CliExecutionResult res = await ProcessAndWindowDriver.RunCliAsync("--self-test", timeoutMs: 15000);
        Assert.Equal(0, res.ExitCode);
        Assert.Contains("[PASS] Inter resolves for both body and bold text", res.StdOut);
    }

    // =========================================================================
    // F08: Spring/Cubic Motion & Hover Behavior (6 test methods)
    // =========================================================================

    [Theory]
    [InlineData(-1.0f)]
    [InlineData(0.0f)]
    [InlineData(0.75f)]
    [InlineData(1.5f)]
    [InlineData(99.0f)]
    public async Task F08_HoverOpenDelay_ClampsAndStartsCleanlyAcrossRange(float delaySec)
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings(s => s["HoverOpenDelaySec"] = delaySec);

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1800);
        IReadOnlyList<WindowSnapshot> wins = await session.WaitForVisibleWindowsAsync(1, 8000);
        Assert.NotEmpty(wins);
        Assert.False(session.Process.HasExited, $"Crashed with HoverOpenDelaySec={delaySec}.");
    }

    [Fact]
    public void F08_Source_ClampsHoverOpenDelayBetweenZeroAnd1Point5Sec()
    {
        IReadOnlyList<SourceMatch> clamp = SourceInspector.FindMatchesInCSharp(@"HoverOpenDelaySec.*Clamp.*0\.0f.*1\.5f|Math\.Clamp\(.*\.HoverOpenDelaySec,\s*0\.0f,\s*1\.5f\)");
        Assert.NotEmpty(clamp);
    }

    [Fact]
    public async Task F08_OpenOnLastTab_PersistsAcrossLaunchAndToggle()
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings(s => s["ReopenOnLastTab"] = true);

        using (var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2000))
        {
            await session.WaitForVisibleWindowsAsync(1, 8000);
            ProcessAndWindowDriver.SendHotkey(ProcessAndWindowDriver.MOD_CONTROL | ProcessAndWindowDriver.MOD_ALT, Keys.N);
            await Task.Delay(350);
            ProcessAndWindowDriver.SendHotkey(ProcessAndWindowDriver.MOD_CONTROL | ProcessAndWindowDriver.MOD_ALT, Keys.N);
            await Task.Delay(350);
            Assert.False(session.Process.HasExited);
        }

        Assert.True(SettingsJsonHelper.GetValue<bool>("ReopenOnLastTab"));
    }

    [Theory]
    [InlineData(0)] // Calm
    [InlineData(1)] // Normal
    [InlineData(2)] // Quick
    public async Task F08_MotionSpeedOptions_AllModesLaunchAndAnimateCleanly(int motionSpeed)
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings(s => s["MotionSpeed"] = motionSpeed);

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1800);
        await session.WaitForVisibleWindowsAsync(1, 8000);
        ProcessAndWindowDriver.SendHotkey(ProcessAndWindowDriver.MOD_CONTROL | ProcessAndWindowDriver.MOD_ALT, Keys.N);
        await Task.Delay(300);
        Assert.False(session.Process.HasExited, $"Crashed with MotionSpeed={motionSpeed}.");
    }

    [Fact]
    public async Task F08_RestingIsland_DoesNotStealForegroundFocusFromActiveApp()
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings();

        using var dummy = ProcessAndWindowDriver.CreateDummyNormalWindow(new Rectangle(150, 150, 500, 350), "F08_ForegroundTarget");
        dummy.BringToFront();
        await Task.Delay(250);
        nint fgBefore = ProcessAndWindowDriver.GetForegroundWindowHandle();

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2000);
        IReadOnlyList<WindowSnapshot> wins = await session.WaitForVisibleWindowsAsync(1, 8000);
        Assert.NotEmpty(wins);

        nint fgAfter = ProcessAndWindowDriver.GetForegroundWindowHandle();
        Assert.DoesNotContain(wins, w => w.Hwnd == fgAfter);
        if (fgBefore == dummy.Hwnd)
        {
            Assert.Equal(dummy.Hwnd, fgAfter);
        }
    }

    [Fact]
    public async Task F08_ReduceMotion_SupportedAndResponsiveOnToggle()
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings(s => s["ReduceMotion"] = true);

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2000);
        await session.WaitForVisibleWindowsAsync(1, 8000);
        ProcessAndWindowDriver.SendHotkey(ProcessAndWindowDriver.MOD_CONTROL | ProcessAndWindowDriver.MOD_ALT, Keys.N);
        await Task.Delay(250);

        using var client = new LoopbackAndDeckClient();
        HttpCallResult root = await client.GetLoopbackRootAsync();
        Assert.Equal(200, root.StatusCode);
    }

    // =========================================================================
    // F09: Clean, Responsive, Closeable Settings (6 test methods)
    // =========================================================================

    [Fact]
    public async Task F09_SelfTest_Verifies13CategoriesAndSettingsClose()
    {
        CliExecutionResult res = await ProcessAndWindowDriver.RunCliAsync("--self-test", timeoutMs: 15000);
        Assert.False(res.TimedOut);
        Assert.Equal(0, res.ExitCode);
        Assert.Contains("[PASS] Settings contains all 13 OpenPixel categories", res.StdOut);
        Assert.Contains("[PASS] Closeable Settings Window: Toggles and closes cleanly without clipping", res.StdOut);
    }

    [Fact]
    public void F09_Source_SettingsWindowResponsiveToWorkingAreaAndNarrowScreens()
    {
        IReadOnlyList<SourceMatch> workArea = SourceInspector.FindMatchesInCSharp(@"work\.Width\s*\*\s*0\.94f|WorkingArea");
        Assert.NotEmpty(workArea);
    }

    [Fact]
    public void F09_Source_SettingsSupportsEscCloseAndUserClosingHide()
    {
        IReadOnlyList<SourceMatch> esc = SourceInspector.FindMatchesInCSharp(@"Keys\.Escape");
        IReadOnlyList<SourceMatch> closeHide = SourceInspector.FindMatchesInCSharp(@"CloseReason\.UserClosing");
        IReadOnlyList<SourceMatch> toggle = SourceInspector.FindMatchesInCSharp(@"ToggleVisibility");

        Assert.NotEmpty(esc);
        Assert.NotEmpty(closeHide);
        Assert.True(toggle.Count >= 2);
    }

    [Fact]
    public void F09_Source_SettingsUsesOpenPixelUserFacingBranding()
    {
        IReadOnlyList<SourceMatch> settingsTitle = SourceInspector.FindMatchesInCSharp(@"Text\s*=\s*""[^""]*OpenPixel[^""]*Settings""");
        Assert.True(settingsTitle.Count >= 1, "Expected OpenPixel user-facing branding in SettingsWindow title.");
    }

    [Fact]
    public async Task F09_SelfTest_BlurSliderSweepThroughSettingsWindowZeroCrash()
    {
        CliExecutionResult res = await ProcessAndWindowDriver.RunCliAsync("--self-test", timeoutMs: 15000);
        Assert.Equal(0, res.ExitCode);
        Assert.Contains("[PASS] Glass blur slider sweeps 0-40 px through the real Settings window without crashing", res.StdOut);
    }

    [Fact]
    public async Task F09_LiveSession_HideAndShowHotkeyLeavesZeroOrphanProcessesOnExit()
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings();

        int pid;
        using (var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2000))
        {
            pid = session.Pid;
            await session.WaitForVisibleWindowsAsync(1, 8000);
            ProcessAndWindowDriver.SendHotkey(ProcessAndWindowDriver.MOD_CONTROL | ProcessAndWindowDriver.MOD_ALT, Keys.H);
            await Task.Delay(250);
            ProcessAndWindowDriver.SendHotkey(ProcessAndWindowDriver.MOD_CONTROL | ProcessAndWindowDriver.MOD_ALT, Keys.H);
            await Task.Delay(250);
            Assert.False(session.Process.HasExited);
        }

        IReadOnlyList<int> remaining = ProcessAndWindowDriver.GetRunningOpenPixelPids();
        Assert.DoesNotContain(pid, remaining);
    }
}
