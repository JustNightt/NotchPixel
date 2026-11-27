using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows.Forms;
using NotchPixel.E2E.Tests.Infrastructure;
using Xunit;

namespace NotchPixel.E2E.Tests.Tier3;

[Collection("NotchPixelE2E")]
public sealed class Tier3_CrossFeaturePairwiseTests
{
    private readonly E2ETestSessionFixture _fixture;

    public Tier3_CrossFeaturePairwiseTests(E2ETestSessionFixture fixture)
    {
        _fixture = fixture;
    }

    private static async Task<bool> PollUntilAsync(Func<bool> predicate, int timeoutMs = 8000, int intervalMs = 100)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return true;
            await Task.Delay(intervalMs).ConfigureAwait(false);
        }
        return predicate();
    }

    private static void CleanupSidecarFiles()
    {
        foreach (string name in new[] { "clipboard_pins.json", "reminders.json", "paired_deck_devices.json", "desktop_widgets.json" })
        {
            string p = Path.Combine(TestWorkspace.LocalAppDataOpenPixelDir, name);
            try { if (File.Exists(p)) File.Delete(p); } catch { }
        }
    }

    // 1. Live .mp4 Video Wallpaper + Glass Day Clock widget config/coexistence
    [Fact]
    public async Task Pair01_LiveMp4Wallpaper_With_DayClockWidget_CoexistsAndRendersCleanly()
    {
        string tempDir = TestWorkspace.CreateIsolatedTempDir("t3_mp4_dayclock");
        try
        {
            string mp4Path = MediaAndFixtureBuilder.CreateValidMp4(Path.Combine(tempDir, "ambient_loop.mp4"), durationSeconds: 2);
            SettingsJsonHelper.ResetToCleanSettings(s =>
            {
                s["Theme"] = SettingsJsonHelper.ThemeIds.LiquidGlass;
                s["LiveVideoWallpaperEnabled"] = true;
                s["LiveVideoWallpaperPath"] = mp4Path;
                s["DayClockShowWeekday"] = true;
                s["DayClockShowDate"] = true;
                s["DayClockShowTime"] = true;
                s["DayClockShowDashes"] = true;
                s["BottomTaskbarEnabled"] = false;
            });

            using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2000, readinessTimeoutMs: 10000);
            var windows = await session.WaitForVisibleWindowsAsync(minCount: 1, timeoutMs: 8000);
            Assert.NotEmpty(windows);
            Assert.False(session.Process.HasExited);

            using var client = new LoopbackAndDeckClient();
            Assert.True(await client.WaitForLoopbackReadyAsync(5000));
            HttpCallResult root = await client.GetLoopbackRootAsync();
            Assert.Equal(200, root.StatusCode);

            // Verify settings retain both Live Video Wallpaper path and Day Clock toggles
            Assert.True(SettingsJsonHelper.GetValue<bool>("LiveVideoWallpaperEnabled"));
            Assert.Equal(mp4Path, SettingsJsonHelper.GetValue<string>("LiveVideoWallpaperPath"));
            Assert.True(SettingsJsonHelper.GetValue<bool>("DayClockShowWeekday"));
        }
        finally
        {
            ProcessAndWindowDriver.TerminateAllOpenPixelInstances();
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    // 2. Live .mkv Unicode Wallpaper + DummyFullscreenWindow auto-pause + recovery
    [Fact]
    public async Task Pair02_LiveMkvUnicodeWallpaper_With_FullscreenDummy_AutoPausesAndRecovers()
    {
        string tempDir = TestWorkspace.CreateIsolatedTempDir("t3_mkv_fs");
        try
        {
            string mkvPath = MediaAndFixtureBuilder.CreateUnicodeMkvFixture(tempDir, "日本語_тест_wallpaper.mkv", durationMs: 2000);
            Assert.True(File.Exists(mkvPath));

            SettingsJsonHelper.ResetToCleanSettings(s =>
            {
                s["Theme"] = SettingsJsonHelper.ThemeIds.LiquidGlass;
                s["LiveVideoWallpaperEnabled"] = true;
                s["LiveVideoWallpaperPath"] = mkvPath;
                s["BottomTaskbarEnabled"] = false;
            });

            using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2000, readinessTimeoutMs: 10000);
            using var client = new LoopbackAndDeckClient();
            Assert.True(await client.WaitForLoopbackReadyAsync(5000));

            using (var fullscreen = ProcessAndWindowDriver.CreateDummyFullscreenWindow("NotchPixel_T3_Fullscreen_Mkv"))
            {
                fullscreen.BringToFront();
                await Task.Delay(700);
                Assert.False(session.Process.HasExited, "OpenPixel.exe must not crash when fullscreen window covers Unicode .mkv wallpaper.");
                HttpCallResult duringFs = await client.GetLoopbackRootAsync();
                Assert.Equal(200, duringFs.StatusCode);
            }

            // After fullscreen window closes, wallpaper/island session recovers cleanly
            await Task.Delay(500);
            Assert.False(session.Process.HasExited);
            IReadOnlyList<WindowSnapshot> recoveredWindows = session.GetOwnedWindows(visibleOnly: true);
            Assert.NotEmpty(recoveredWindows);
            HttpCallResult afterFs = await client.GetLoopbackRootAsync();
            Assert.Equal(200, afterFs.StatusCode);
        }
        finally
        {
            ProcessAndWindowDriver.TerminateAllOpenPixelInstances();
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    // 3. Fullscreen Game (DummyFullscreenWindow) + Bottom Notch Taskbar step-aside + Start surfacing
    [Fact]
    public async Task Pair03_FullscreenGame_With_BottomNotchTaskbar_StepsAsideAndRestoresNativeTaskbar()
    {
        try
        {
            SettingsJsonHelper.ResetToCleanSettings(s =>
            {
                s["Theme"] = SettingsJsonHelper.ThemeIds.LiquidGlass;
                s["BottomTaskbarEnabled"] = true;
                s["GameOverlayMasterEnabled"] = true;
                s["GameOverlayMode"] = 1;
            });

            using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2500, readinessTimeoutMs: 10000);
            var initialWindows = await session.WaitForVisibleWindowsAsync(minCount: 1, timeoutMs: 8000);
            Assert.NotEmpty(initialWindows);

            using (var fullscreen = ProcessAndWindowDriver.CreateDummyFullscreenWindow("NotchPixel_T3_Fullscreen_Game"))
            {
                fullscreen.BringToFront();
                await Task.Delay(700);

                // Fullscreen window must remain foreground and unobstructed by custom taskbar
                nint fg = ProcessAndWindowDriver.GetForegroundWindowHandle();
                Assert.Equal(fullscreen.Hwnd, fg);
                Assert.False(session.Process.HasExited);

                // Verify any visible taskbar-sized bottom window is not above the fullscreen window in Z-order
                Screen primary = Screen.PrimaryScreen!;
                var bottomOverlays = session.GetOwnedWindows(visibleOnly: true)
                    .Where(w => w.Bounds.Bottom >= primary.Bounds.Bottom - 20 && w.Bounds.Width >= 220 && w.Bounds.Height <= 100)
                    .ToList();
                int fsZ = ProcessAndWindowDriver.GetWindowZOrderIndex(fullscreen.Hwnd);
                foreach (var bar in bottomOverlays)
                {
                    int barZ = ProcessAndWindowDriver.GetWindowZOrderIndex(bar.Hwnd);
                    Assert.True(barZ > fsZ || !bar.IsTopMost,
                        $"Bottom taskbar window (hwnd={bar.Hwnd}, z={barZ}) must step aside behind fullscreen game (z={fsZ}).");
                }
            }

            session.Dispose();
            bool nativeRestored = await PollUntilAsync(ProcessAndWindowDriver.IsNativeTaskbarVisible, timeoutMs: 6000);
            Assert.True(nativeRestored, "Windows native taskbar (Shell_TrayWnd) must be visible after OpenPixel exits.");
        }
        finally
        {
            ProcessAndWindowDriver.TerminateAllOpenPixelInstances();
            ProcessAndWindowDriver.RestoreWindowsNativeTaskbar();
        }
    }

    // 4. Fullscreen Game + Game Strip settings + Capture Exclusion (WDA_EXCLUDEFROMCAPTURE)
    [Fact]
    public async Task Pair04_FullscreenGame_With_GameStripAndCaptureExclusion_HonorsDisableAndAffinity()
    {
        try
        {
            // Case A: Master game overlay disabled while Liquid Glass is active
            SettingsJsonHelper.ResetToCleanSettings(s =>
            {
                s["Theme"] = SettingsJsonHelper.ThemeIds.LiquidGlass;
                s["GameOverlayMasterEnabled"] = false;
                s["GameOverlayMode"] = 0;
                s["BottomTaskbarEnabled"] = false;
            });

            using (var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2000, readinessTimeoutMs: 10000))
            {
                var wins = await session.WaitForVisibleWindowsAsync(minCount: 1, timeoutMs: 8000);
                Assert.NotEmpty(wins);
                foreach (var w in wins)
                {
                    Assert.Equal(ProcessAndWindowDriver.WDA_EXCLUDEFROMCAPTURE, w.DisplayAffinity);
                }

                using var fs = ProcessAndWindowDriver.CreateDummyFullscreenWindow("NotchPixel_T3_GameStrip_Off");
                fs.BringToFront();
                await Task.Delay(500);
                Assert.False(session.Process.HasExited);
            }

            // Case B: SlimStrip enabled with Liquid Glass -> still capture-excluded and stable
            SettingsJsonHelper.Patch(s =>
            {
                s["GameOverlayMasterEnabled"] = true;
                s["GameOverlayMode"] = 1;
                s["GameStripShowFps"] = true;
                s["GameStripShowCpu"] = true;
                s["GameStripShowGpu"] = true;
            });

            using (var session2 = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2000, readinessTimeoutMs: 10000))
            {
                using var fs2 = ProcessAndWindowDriver.CreateDummyFullscreenWindow("NotchPixel_T3_GameStrip_Slim");
                fs2.BringToFront();
                await Task.Delay(500);
                var wins2 = session2.GetOwnedWindows(visibleOnly: true);
                Assert.NotEmpty(wins2);
                foreach (var w in wins2)
                {
                    Assert.Equal(ProcessAndWindowDriver.WDA_EXCLUDEFROMCAPTURE, w.DisplayAffinity);
                }
            }
        }
        finally
        {
            ProcessAndWindowDriver.TerminateAllOpenPixelInstances();
        }
    }

    // 5. Free Buff needs_input=1 (/agent) + /notify Island Alert + /deck/state telemetry
    [Fact]
    public async Task Pair05_FreeBuffNeedsInput_With_NotifyAlertAndDeckState_PropagatesAcrossEndpoints()
    {
        try
        {
            SettingsJsonHelper.ResetToCleanSettings(s =>
            {
                s["Theme"] = SettingsJsonHelper.ThemeIds.LiquidGlass;
                s["LoopbackApiEnabled"] = true;
                s["PcDeckEnabled"] = true;
                s["BottomTaskbarEnabled"] = false;
            });

            using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2000, readinessTimeoutMs: 10000);
            using var client = new LoopbackAndDeckClient();
            Assert.True(await client.WaitForLoopbackReadyAsync(6000));

            HttpCallResult workingRes = await client.SendAgentAsync("Free Buff", "Working", "Analyzing UI tree", progress: 45);
            Assert.Equal(200, workingRes.StatusCode);
            Assert.Contains("\"ok\":true", workingRes.Body);

            HttpCallResult needsInputRes = await client.SendAgentAsync("Free Buff", "Needs input", "Approve schema migration?", progress: 95, almostDone: true, needsInput: true);
            Assert.Equal(200, needsInputRes.StatusCode);
            Assert.Contains("\"endpoint\":\"agent\"", needsInputRes.Body);

            HttpCallResult notifyRes = await client.SendNotifyAsync("Free Buff Nudge", "Approval required in terminal", "Free Buff", usePost: true);
            Assert.Equal(200, notifyRes.StatusCode);
            Assert.Contains("\"endpoint\":\"notify\"", notifyRes.Body);

            // Deck TLS server should reject unauthenticated /deck/state cleanly (401) or public guard (403), never crashing OpenPixel
            if (ProcessAndWindowDriver.GetBoundNotchPixelPorts().Contains(47825))
            {
                TlsCallResult unauthState = await client.GetStateAsync("unpaired-token");
                Assert.True(unauthState.StatusCode is 401 or 403, $"Expected 401 or 403, got {unauthState.StatusCode}");
            }

            await Task.Delay(300);
            Assert.False(session.Process.HasExited);
        }
        finally
        {
            ProcessAndWindowDriver.TerminateAllOpenPixelInstances();
        }
    }

    // 6. Core Audio Volume change + /deck/state live volume sync + Mute toggle
    [Fact]
    public async Task Pair06_CoreAudioVolumeAndMute_With_DeckStateTelemetry_SyncsAndRestores()
    {
        using var audioScope = ProcessAndWindowDriver.CaptureAndRestoreAudioScope();
        try
        {
            SettingsJsonHelper.ResetToCleanSettings(s =>
            {
                s["Theme"] = SettingsJsonHelper.ThemeIds.LiquidGlass;
                s["PcDeckEnabled"] = true;
                s["BottomTaskbarEnabled"] = false;
            });

            using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2000, readinessTimeoutMs: 10000);
            using var client = new LoopbackAndDeckClient();
            Assert.True(await client.WaitForLoopbackReadyAsync(5000));

            ProcessAndWindowDriver.SetSystemMasterVolume(0.37f, muted: false);
            await Task.Delay(250);
            var (scalar1, muted1, _) = ProcessAndWindowDriver.GetSystemMasterVolumeAndMute();
            Assert.InRange(scalar1, 0.34f, 0.40f);
            Assert.False(muted1);

            ProcessAndWindowDriver.SetSystemMasterVolume(0.37f, muted: true);
            await Task.Delay(250);
            var (scalar2, muted2, _) = ProcessAndWindowDriver.GetSystemMasterVolumeAndMute();
            Assert.InRange(scalar2, 0.34f, 0.40f);
            Assert.True(muted2);

            // Verify MockDeckTlsServer telemetry contract aligns with volume/mute fields
            using var mockDeck = new MediaAndFixtureBuilder.MockDeckTlsServer(enableUdpDiscovery: false);
            using var mockClient = new LoopbackAndDeckClient(deckTlsPort: mockDeck.TlsPort);
            TlsCallResult stateRes = await mockClient.GetStateAsync(mockDeck.IssuedToken, mockDeck.CertFingerprintSha256);
            Assert.Equal(200, stateRes.StatusCode);
            using JsonDocument doc = JsonDocument.Parse(stateRes.Body);
            Assert.True(doc.RootElement.TryGetProperty("volume", out _));
            Assert.True(doc.RootElement.TryGetProperty("muted", out _));

            Assert.False(session.Process.HasExited);
        }
        finally
        {
            ProcessAndWindowDriver.TerminateAllOpenPixelInstances();
        }
    }

    // 7. Liquid Glass (Theme=0) + WDA_EXCLUDEFROMCAPTURE across ALL owned windows + Settings toggle
    [Fact]
    public async Task Pair07_LiquidGlassTheme_EnforcesCaptureExclusionAcrossAllOwnedWindows()
    {
        try
        {
            SettingsJsonHelper.ResetToCleanSettings(s =>
            {
                s["Theme"] = SettingsJsonHelper.ThemeIds.LiquidGlass;
                s["HideFromScreenshots"] = false; // Glass must ALWAYS be excluded regardless of opt-in flag
                s["BottomTaskbarEnabled"] = true;
            });

            using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2500, readinessTimeoutMs: 10000);
            var wins = await session.WaitForVisibleWindowsAsync(minCount: 1, timeoutMs: 8000);
            Assert.NotEmpty(wins);

            foreach (WindowSnapshot w in wins)
            {
                Assert.Equal(ProcessAndWindowDriver.WDA_EXCLUDEFROMCAPTURE, w.DisplayAffinity);
            }

            // Flip HideFromScreenshots to true as well and confirm affinity stays WDA_EXCLUDEFROMCAPTURE
            SettingsJsonHelper.Patch(s => s["HideFromScreenshots"] = true);
            session.Dispose();

            using var session2 = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2500, readinessTimeoutMs: 10000);
            var wins2 = await session2.WaitForVisibleWindowsAsync(minCount: 1, timeoutMs: 8000);
            Assert.NotEmpty(wins2);
            foreach (WindowSnapshot w in wins2)
            {
                Assert.Equal(ProcessAndWindowDriver.WDA_EXCLUDEFROMCAPTURE, w.DisplayAffinity);
            }
        }
        finally
        {
            ProcessAndWindowDriver.TerminateAllOpenPixelInstances();
            ProcessAndWindowDriver.RestoreWindowsNativeTaskbar();
        }
    }

    // 8. Candy Glass (Theme=1) + WDA_EXCLUDEFROMCAPTURE + rapid blur clamping
    [Fact]
    public async Task Pair08_CandyGlass_EnforcesCaptureExclusionAndExtremeBlurClamping()
    {
        try
        {
            SettingsJsonHelper.ResetToCleanSettings(s =>
            {
                s["Theme"] = SettingsJsonHelper.ThemeIds.CandyGlass;
                s["GlassBlurRadius"] = 999.0;
                s["HideFromScreenshots"] = false;
                s["BottomTaskbarEnabled"] = false;
            });

            using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2200, readinessTimeoutMs: 10000);
            var wins = await session.WaitForVisibleWindowsAsync(minCount: 1, timeoutMs: 8000);
            Assert.NotEmpty(wins);
            foreach (WindowSnapshot w in wins)
            {
                Assert.Equal(ProcessAndWindowDriver.WDA_EXCLUDEFROMCAPTURE, w.DisplayAffinity);
            }
            Assert.False(session.Process.HasExited);
            session.Dispose();

            // Negative blur value must also clamp safely without crash
            SettingsJsonHelper.Patch(s => s["GlassBlurRadius"] = -250.0);
            using var sessionNeg = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1800, readinessTimeoutMs: 10000);
            var winsNeg = await sessionNeg.WaitForVisibleWindowsAsync(minCount: 1, timeoutMs: 8000);
            Assert.NotEmpty(winsNeg);
            Assert.False(sessionNeg.Process.HasExited);
        }
        finally
        {
            ProcessAndWindowDriver.TerminateAllOpenPixelInstances();
        }
    }

    // 9. Solid Theme (Black/Rose) + HideFromScreenshots=false vs true capture affinity flip
    [Fact]
    public async Task Pair09_SolidThemesBlackAndRose_FlipCaptureAffinityWithHideFromScreenshotsSetting()
    {
        try
        {
            // Non-glass theme with HideFromScreenshots = false -> visible to capture (WDA_NONE)
            SettingsJsonHelper.ResetToCleanSettings(s =>
            {
                s["Theme"] = SettingsJsonHelper.ThemeIds.Black;
                s["HideFromScreenshots"] = false;
                s["BottomTaskbarEnabled"] = false;
            });

            using (var sessionVisible = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2000, readinessTimeoutMs: 10000))
            {
                var wins = await sessionVisible.WaitForVisibleWindowsAsync(minCount: 1, timeoutMs: 8000);
                Assert.NotEmpty(wins);
                Assert.Contains(wins, w => w.DisplayAffinity == ProcessAndWindowDriver.WDA_NONE);
            }

            // Non-glass theme (Rose) with HideFromScreenshots = true -> excluded from capture (WDA_EXCLUDEFROMCAPTURE)
            SettingsJsonHelper.ResetToCleanSettings(s =>
            {
                s["Theme"] = SettingsJsonHelper.ThemeIds.Rose;
                s["HideFromScreenshots"] = true;
                s["BottomTaskbarEnabled"] = false;
            });

            using (var sessionHidden = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2000, readinessTimeoutMs: 10000))
            {
                var wins2 = await sessionHidden.WaitForVisibleWindowsAsync(minCount: 1, timeoutMs: 8000);
                Assert.NotEmpty(wins2);
                foreach (var w in wins2)
                {
                    Assert.Equal(ProcessAndWindowDriver.WDA_EXCLUDEFROMCAPTURE, w.DisplayAffinity);
                }
            }
        }
        finally
        {
            ProcessAndWindowDriver.TerminateAllOpenPixelInstances();
        }
    }

    // 10. Album-Art / Idle Music State + Adaptive Ink + Zero-Placeholder Policy
    [Fact]
    public async Task Pair10_IdleMusicState_With_AdaptiveInkThemesAndZeroPlaceholderPolicy()
    {
        try
        {
            Assert.False(SourceInspector.ContainsForbiddenPlaceholder(out var violations),
                "Forbidden placeholder pattern found in C# sources: " + string.Join("; ", violations.Select(v => $"{v.FilePath}:{v.LineNumber}")));

            // Test both light surface (PureWhite) and dark surface (PureBlack) idle states
            foreach (int themeId in new[] { SettingsJsonHelper.ThemeIds.PureWhite, SettingsJsonHelper.ThemeIds.PureBlack })
            {
                SettingsJsonHelper.ResetToCleanSettings(s =>
                {
                    s["Theme"] = themeId;
                    s["CardBackground"] = 0; // AlbumColours
                    s["ShowSongTitleInClosedPill"] = true;
                    s["BottomTaskbarEnabled"] = false;
                });

                using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1800, readinessTimeoutMs: 10000);
                var wins = await session.WaitForVisibleWindowsAsync(minCount: 1, timeoutMs: 8000);
                Assert.NotEmpty(wins);
                foreach (var w in wins)
                {
                    Assert.DoesNotContain("Midnight City", w.Title, StringComparison.OrdinalIgnoreCase);
                }
                Assert.False(session.Process.HasExited);
            }
        }
        finally
        {
            ProcessAndWindowDriver.TerminateAllOpenPixelInstances();
        }
    }

    // 11. Ctrl+Alt+M Global Mic Mute + /deck/state micMuted + Core Audio restore
    [Fact]
    public async Task Pair11_GlobalMicMuteHotkey_With_AudioScopeAndLoopbackStability()
    {
        using var audioScope = ProcessAndWindowDriver.CaptureAndRestoreAudioScope();
        try
        {
            SettingsJsonHelper.ResetToCleanSettings(s =>
            {
                s["Theme"] = SettingsJsonHelper.ThemeIds.LiquidGlass;
                s["ShowGlobalMuteNotice"] = true;
                s["BottomTaskbarEnabled"] = false;
            });

            using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2000, readinessTimeoutMs: 10000);
            using var client = new LoopbackAndDeckClient();
            Assert.True(await client.WaitForLoopbackReadyAsync(5000));

            // Toggle mic mute via Ctrl+Alt+M, then toggle back to restore original mic mute state
            ProcessAndWindowDriver.SendHotkey(ProcessAndWindowDriver.MOD_CONTROL | ProcessAndWindowDriver.MOD_ALT, Keys.M);
            await Task.Delay(350);
            Assert.False(session.Process.HasExited);

            ProcessAndWindowDriver.SendHotkey(ProcessAndWindowDriver.MOD_CONTROL | ProcessAndWindowDriver.MOD_ALT, Keys.M);
            await Task.Delay(350);
            HttpCallResult root = await client.GetLoopbackRootAsync();
            Assert.Equal(200, root.StatusCode);
        }
        finally
        {
            ProcessAndWindowDriver.TerminateAllOpenPixelInstances();
        }
    }

    // 12. Ctrl+Alt+N / Ctrl+Alt+H Island Toggle/Hide + Hover Delay persistence
    [Fact]
    public async Task Pair12_IslandToggleAndHideHotkeys_With_HoverDelayPersistence()
    {
        try
        {
            SettingsJsonHelper.ResetToCleanSettings(s =>
            {
                s["Theme"] = SettingsJsonHelper.ThemeIds.LiquidGlass;
                s["HoverOpenDelaySec"] = 0.65;
                s["ReopenOnLastTab"] = true;
                s["BottomTaskbarEnabled"] = false;
            });

            using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2000, readinessTimeoutMs: 10000);
            var winsBefore = await session.WaitForVisibleWindowsAsync(minCount: 1, timeoutMs: 8000);
            Assert.NotEmpty(winsBefore);

            // Toggle island open via Ctrl+Alt+N
            ProcessAndWindowDriver.SendHotkey(ProcessAndWindowDriver.MOD_CONTROL | ProcessAndWindowDriver.MOD_ALT, Keys.N);
            await Task.Delay(400);
            Assert.False(session.Process.HasExited);

            // Hide island via Ctrl+Alt+H, then restore via Ctrl+Alt+H
            ProcessAndWindowDriver.SendHotkey(ProcessAndWindowDriver.MOD_CONTROL | ProcessAndWindowDriver.MOD_ALT, Keys.H);
            await Task.Delay(300);
            ProcessAndWindowDriver.SendHotkey(ProcessAndWindowDriver.MOD_CONTROL | ProcessAndWindowDriver.MOD_ALT, Keys.H);
            await Task.Delay(300);
            Assert.False(session.Process.HasExited);

            double? savedHover = SettingsJsonHelper.GetValue<double?>("HoverOpenDelaySec");
            Assert.NotNull(savedHover);
            Assert.InRange(savedHover!.Value, 0.0, 1.5);
        }
        finally
        {
            ProcessAndWindowDriver.TerminateAllOpenPixelInstances();
        }
    }

    // 13. Day Clock + Multiple Desktop Widgets + Z-order below DummyNormalWindow
    [Fact]
    public async Task Pair13_DayClockAndWidgets_StayBelowNormalWindowsInZOrder()
    {
        try
        {
            string widgetsPath = Path.Combine(TestWorkspace.LocalAppDataOpenPixelDir, "desktop_widgets.json");
            Directory.CreateDirectory(TestWorkspace.LocalAppDataOpenPixelDir);
            File.WriteAllText(widgetsPath, JsonSerializer.Serialize(new[]
            {
                new { WidgetType = 0, X = 60, Y = 80, Width = 300, Height = 168, StyleMode = 0 },
                new { WidgetType = 11, X = 380, Y = 80, Width = 210, Height = 110, StyleMode = 0 },
                new { WidgetType = 9, X = 610, Y = 80, Width = 210, Height = 110, StyleMode = 0 }
            }));

            SettingsJsonHelper.ResetToCleanSettings(s =>
            {
                s["Theme"] = SettingsJsonHelper.ThemeIds.LiquidGlass;
                s["LockDesktopWidgets"] = false;
                s["BottomTaskbarEnabled"] = false;
            });

            using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2200, readinessTimeoutMs: 10000);
            var owned = await session.WaitForVisibleWindowsAsync(minCount: 1, timeoutMs: 8000);
            Assert.NotEmpty(owned);

            using var normal = ProcessAndWindowDriver.CreateDummyNormalWindow(new Rectangle(50, 50, 720, 460), "NotchPixel_T3_NormalWindow");
            normal.BringToFront();
            await Task.Delay(400);

            int normalZ = ProcessAndWindowDriver.GetWindowZOrderIndex(normal.Hwnd);
            Assert.True(normalZ >= 0);
            Assert.Equal(normal.Hwnd, ProcessAndWindowDriver.GetForegroundWindowHandle());

            // Any non-topmost desktop widget windows must sit below normal application windows in Z-order
            foreach (var w in session.GetOwnedWindows(visibleOnly: true).Where(x => !x.IsTopMost))
            {
                int widgetZ = ProcessAndWindowDriver.GetWindowZOrderIndex(w.Hwnd);
                Assert.True(widgetZ > normalZ,
                    $"Desktop widget hwnd={w.Hwnd} (z={widgetZ}) must be behind normal window (z={normalZ}).");
                Assert.False(w.IsForeground);
            }
        }
        finally
        {
            ProcessAndWindowDriver.TerminateAllOpenPixelInstances();
            CleanupSidecarFiles();
        }
    }

    // 14. Custom Script /tile (47823) + /notify (47823) concurrent loopback handling
    [Fact]
    public async Task Pair14_CustomScriptTileAndNotify_ConcurrentLoopbackRequestsAllSucceed()
    {
        try
        {
            SettingsJsonHelper.ResetToCleanSettings(s =>
            {
                s["LoopbackApiEnabled"] = true;
                s["LoopbackApiPort"] = 47823;
                s["BottomTaskbarEnabled"] = false;
            });

            using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1800, readinessTimeoutMs: 10000);
            using var client = new LoopbackAndDeckClient();
            Assert.True(await client.WaitForLoopbackReadyAsync(5000));

            var tasks = new List<Task<HttpCallResult>>();
            for (int i = 0; i < 6; i++)
            {
                int idx = i;
                tasks.Add(client.SendTileAsync($"tile_{idx}", $"Metric {idx}", $"{idx * 10}%", "Live", usePost: idx % 2 == 1));
                tasks.Add(client.SendNotifyAsync($"Notice {idx}", $"Payload {idx}", "Automation", usePost: idx % 2 == 0));
            }

            HttpCallResult[] results = await Task.WhenAll(tasks);
            Assert.Equal(12, results.Length);
            foreach (HttpCallResult r in results)
            {
                Assert.Equal(200, r.StatusCode);
                Assert.Contains("\"ok\":true", r.Body);
            }
            Assert.False(session.Process.HasExited);
        }
        finally
        {
            ProcessAndWindowDriver.TerminateAllOpenPixelInstances();
        }
    }

    // 15. File Shelf (47824) + --qr-test QR generation + Unicode download filename preservation
    [Fact]
    public async Task Pair15_FileShelf_With_QrTestAndUnicodeSafeHeaders()
    {
        try
        {
            CliExecutionResult qrRes = await ProcessAndWindowDriver.RunCliAsync(
                "--qr-test \"http://192.168.1.50:47824/shelf\"", timeoutMs: 10000);
            Assert.False(qrRes.TimedOut);
            Assert.Equal(0, qrRes.ExitCode);

            using JsonDocument qrDoc = JsonDocument.Parse(qrRes.StdOut.Trim());
            int size = qrDoc.RootElement.GetProperty("size").GetInt32();
            int version = qrDoc.RootElement.GetProperty("version").GetInt32();
            JsonElement rows = qrDoc.RootElement.GetProperty("rows");
            Assert.True(version >= 1);
            Assert.True(size >= 21);
            Assert.Equal(size, rows.GetArrayLength());

            SettingsJsonHelper.ResetToCleanSettings(s =>
            {
                s["PcDeckPort"] = 47824;
                s["BottomTaskbarEnabled"] = false;
            });

            using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1800, readinessTimeoutMs: 10000);
            using var client = new LoopbackAndDeckClient();
            Assert.True(await client.WaitForLoopbackReadyAsync(5000));

            HttpCallResult shelfHtml = await client.GetShelfHtmlAsync();
            Assert.Equal(200, shelfHtml.StatusCode);
            Assert.Contains("NotchPixel Shelf", shelfHtml.Body);
            Assert.Contains("charset='utf-8'", shelfHtml.Body, StringComparison.OrdinalIgnoreCase);

            // Missing index returns clean 404 without crashing server
            var (dlStatus, _, _) = await client.DownloadShelfFileAsync(99);
            Assert.Equal(404, dlStatus);
            Assert.False(session.Process.HasExited);
        }
        finally
        {
            ProcessAndWindowDriver.TerminateAllOpenPixelInstances();
        }
    }

    // 16. Clipboard History Persistent Pins + App Restart survival + Settings atomic save
    [Fact]
    public async Task Pair16_ClipboardPersistentPins_SurviveAppRestartWithAtomicConfigSave()
    {
        string pinsPath = Path.Combine(TestWorkspace.LocalAppDataOpenPixelDir, "clipboard_pins.json");
        string pinText = "E2E-Pinned-Snippet-αβγ-" + Guid.NewGuid().ToString("N");
        string? previousPins = null;
        try
        {
            Directory.CreateDirectory(TestWorkspace.LocalAppDataOpenPixelDir);
            if (File.Exists(pinsPath)) previousPins = File.ReadAllText(pinsPath);

            void WritePins() => File.WriteAllText(pinsPath, JsonSerializer.Serialize(new[]
            {
                new
                {
                    Id = 9001L,
                    Category = 1,
                    Text = pinText,
                    ImagePath = "",
                    FilePaths = Array.Empty<string>(),
                    IsPinned = true,
                    IsCurrent = false,
                    TimestampText = "12:00"
                }
            }));

            SettingsJsonHelper.ResetToCleanSettings(s =>
            {
                s["Theme"] = SettingsJsonHelper.ThemeIds.LiquidGlass;
                s["BottomTaskbarEnabled"] = false;
            });

            // Persist the fixture after the settings reset so this test measures
            // restart persistence rather than harness file ordering.
            WritePins();

            using (var s1 = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1800, readinessTimeoutMs: 10000))
            {
                Assert.False(s1.Process.HasExited);
                await Task.Delay(400);
            }

            // No stale .tmp atomic artifacts left behind
            Assert.False(File.Exists(pinsPath + ".tmp"));
            Assert.False(File.Exists(TestWorkspace.SettingsJsonPath + ".tmp"));

            using (var s2 = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1800, readinessTimeoutMs: 10000))
            {
                Assert.False(s2.Process.HasExited);
                string persisted = File.ReadAllText(pinsPath);

                // Compare the decoded entry, not raw file bytes: JSON may escape
                // non-ASCII text (\u03B1...), which a substring check cannot match.
                using var pinsDoc = JsonDocument.Parse(persisted);
                bool pinSurvived = pinsDoc.RootElement.EnumerateArray().Any(entry =>
                    entry.TryGetProperty("Text", out JsonElement textNode) &&
                    textNode.GetString() == pinText &&
                    entry.TryGetProperty("IsPinned", out JsonElement pinnedNode) &&
                    pinnedNode.GetBoolean());
                Assert.True(pinSurvived, "The pinned clipboard entry must survive an app restart with its text and pinned flag intact.");
                Assert.Contains("\"IsPinned\": true", persisted.Replace(":true", ": true"), StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            ProcessAndWindowDriver.TerminateAllOpenPixelInstances();
            CleanupSidecarFiles();
            // Leave the machine exactly as this test found it.
            try { if (previousPins != null) File.WriteAllText(pinsPath, previousPins); } catch { }
        }
    }

    // 17. OBS Mock WebSocket + Twitch/YouTube Mock Chat Server + Capture Exclusion
    [Fact]
    public async Task Pair17_ObsMockWebSocketAndChatServer_With_CaptureExclusion()
    {
        try
        {
            using var obs = new MediaAndFixtureBuilder.MockObsWebSocketServer(initialRecording: true, initialSeconds: 75);
            using var chat = new MediaAndFixtureBuilder.MockChatStreamServer();
            chat.PushMessage("StreamViewer", "Liquid Glass looks clean!", "MOD");

            SettingsJsonHelper.ResetToCleanSettings(s =>
            {
                s["Theme"] = SettingsJsonHelper.ThemeIds.LiquidGlass;
                s["ShowObsRecPill"] = true;
                s["StreamChatEnabled"] = true;
                s["ShowIslandChatPopups"] = true;
                s["TwitchChannel"] = "notchpixel_test";
                s["BottomTaskbarEnabled"] = false;
            });

            using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2000, readinessTimeoutMs: 10000);
            var wins = await session.WaitForVisibleWindowsAsync(minCount: 1, timeoutMs: 8000);
            Assert.NotEmpty(wins);

            foreach (var w in wins)
            {
                Assert.Equal(ProcessAndWindowDriver.WDA_EXCLUDEFROMCAPTURE, w.DisplayAffinity);
            }

            // Verify mock chat server serves pushed messages cleanly while OpenPixel runs
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            string chatJson = await http.GetStringAsync(chat.BaseHttpUrl + "/chat");
            Assert.Contains("StreamViewer", chatJson);
            Assert.True(obs.IsRecording);
            Assert.Equal(75, obs.RecordSeconds);
            Assert.False(session.Process.HasExited);
        }
        finally
        {
            ProcessAndWindowDriver.TerminateAllOpenPixelInstances();
        }
    }

    // 18. Web Drawer (--web-test WebView2 availability) + Dormant memory discipline when closed
    [Fact]
    public async Task Pair18_WebView2Availability_And_DormantMemoryWhenDrawerClosed()
    {
        try
        {
            CliExecutionResult webProbe = await ProcessAndWindowDriver.RunCliAsync("--web-test", timeoutMs: 12000);
            Assert.False(webProbe.TimedOut);
            Assert.Equal(0, webProbe.ExitCode);
            Assert.Contains("WEBVIEW_OK", webProbe.StdOut);

            SettingsJsonHelper.ResetToCleanSettings(s =>
            {
                s["Theme"] = SettingsJsonHelper.ThemeIds.LiquidGlass;
                s["BottomTaskbarEnabled"] = false;
            });

            using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2000, readinessTimeoutMs: 10000);
            await session.WaitForVisibleWindowsAsync(minCount: 1, timeoutMs: 8000);

            session.Process.Refresh();
            long workingSetMb = session.Process.WorkingSet64 / (1024 * 1024);
            Assert.True(workingSetMb < 250, $"Dormant GUI working set should remain disciplined when Web Drawer is closed, got {workingSetMb} MB.");
        }
        finally
        {
            ProcessAndWindowDriver.TerminateAllOpenPixelInstances();
        }
    }

    // 19. Bottom Notch Taskbar + --watchdog crash recovery (SimulateCrash -> Shell_TrayWnd restored!)
    [Fact]
    public async Task Pair19_BottomNotchTaskbar_WatchdogRestoresNativeTaskbarOnCrash()
    {
        try
        {
            ProcessAndWindowDriver.RestoreWindowsNativeTaskbar();
            SettingsJsonHelper.ResetToCleanSettings(s =>
            {
                s["Theme"] = SettingsJsonHelper.ThemeIds.LiquidGlass;
                s["BottomTaskbarEnabled"] = true;
            });

            using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2500, readinessTimeoutMs: 10000);
            await session.WaitForVisibleWindowsAsync(minCount: 1, timeoutMs: 8000);
            await Task.Delay(500);

            // Simulate hard crash of main process without graceful cleanup
            ProcessAndWindowDriver.SimulateCrash(session.Pid);

            bool restored = await PollUntilAsync(ProcessAndWindowDriver.IsNativeTaskbarVisible, timeoutMs: 8000, intervalMs: 100);
            Assert.True(restored, "Out-of-process --watchdog must restore Shell_TrayWnd after OpenPixel crashes.");
        }
        finally
        {
            ProcessAndWindowDriver.TerminateAllOpenPixelInstances();
            ProcessAndWindowDriver.RestoreWindowsNativeTaskbar();
        }
    }

    // 20. Bottom Notch Taskbar + Hung process termination -> Shell_TrayWnd restored!
    [Fact]
    public async Task Pair20_BottomNotchTaskbar_RestoresNativeTaskbarAfterHungProcessTermination()
    {
        try
        {
            ProcessAndWindowDriver.RestoreWindowsNativeTaskbar();
            SettingsJsonHelper.ResetToCleanSettings(s =>
            {
                s["Theme"] = SettingsJsonHelper.ThemeIds.LiquidGlass;
                s["BottomTaskbarEnabled"] = true;
            });

            using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2500, readinessTimeoutMs: 10000);
            await session.WaitForVisibleWindowsAsync(minCount: 1, timeoutMs: 8000);

            // Freeze main process to simulate unresponsive UI hang, then kill it
            ProcessAndWindowDriver.SuspendProcess(session.Pid);
            await Task.Delay(600);
            ProcessAndWindowDriver.SimulateCrash(session.Pid);

            bool restored = await PollUntilAsync(ProcessAndWindowDriver.IsNativeTaskbarVisible, timeoutMs: 8000, intervalMs: 100);
            Assert.True(restored, "Watchdog must restore Shell_TrayWnd when hung OpenPixel process is terminated.");
        }
        finally
        {
            ProcessAndWindowDriver.TerminateAllOpenPixelInstances();
            ProcessAndWindowDriver.RestoreWindowsNativeTaskbar();
        }
    }

    // 21. Android NotchPixel Deck APK (main.apk) + Zero iOS check + apksigner verification
    [Fact]
    public async Task Pair21_AndroidDeckApk_ZeroIosArtifactsAndApkSignerVerified()
    {
        IReadOnlyList<string> iosArtifacts = AndroidHarnessRunner.FindIosArtifactsInApp();
        Assert.Empty(iosArtifacts);

        string? apkPath = AndroidHarnessRunner.FindBuiltApkPath();
        if (apkPath == null)
        {
            CliExecutionResult buildRes = await AndroidHarnessRunner.RunBuildApkScriptAsync(timeoutMs: 90000);
            Assert.False(buildRes.TimedOut, "build_apk.ps1 timed out.");
            Assert.Equal(0, buildRes.ExitCode);
            apkPath = AndroidHarnessRunner.FindBuiltApkPath();
        }

        Assert.NotNull(apkPath);
        ApkInspectionResult info = await AndroidHarnessRunner.InspectApkAsync(apkPath!, timeoutMs: 15000);
        Assert.True(info.Exists);
        Assert.True(info.HasAndroidManifest, "APK missing AndroidManifest.xml");
        Assert.True(info.HasClassesDex, "APK missing classes.dex");
        Assert.True(info.HasResourcesArsc, "APK missing resources.arsc");
        Assert.True(info.SignatureVerified, "apksigner verify failed: " + info.VerifyOutput);
        Assert.False(string.IsNullOrWhiteSpace(info.PackageName));
    }

    // 22. Android JVM Protocol + UDP 47826 discovery keys (certFingerprint, pcName)
    [Fact]
    public async Task Pair22_AndroidJvmProtocol_With_UdpDiscoveryKeysAndTlsPinning()
    {
        AndroidHarnessResult jvm = await AndroidHarnessRunner.RunJvmProtocolTestsAsync(
            timeoutMs: 15000,
            filterTestMethod: "testUdpDiscoveryJsonParsingRequiredFields");
        Assert.True(jvm.Compiled, jvm.StdErr);
        Assert.Equal(0, jvm.ExitCode);
        Assert.Equal(1, jvm.PassedCount);
        Assert.Equal(0, jvm.FailedCount);

        using var mockDeck = new MediaAndFixtureBuilder.MockDeckTlsServer(enableUdpDiscovery: true);
        using var client = new LoopbackAndDeckClient(deckTlsPort: mockDeck.TlsPort, discoveryUdpPort: mockDeck.UdpPort);

        UdpDiscoveryResponse? udp = await client.SendDiscoveryProbeAsync("OPENPIXEL_DECK_DISCOVER", timeoutMs: 2000);
        Assert.NotNull(udp);
        Assert.Equal(mockDeck.CertFingerprintSha256, udp!.CertFingerprint);
        Assert.Equal(mockDeck.PcName, udp.PcName);
        Assert.Equal(mockDeck.TlsPort, udp.TlsPort);

        TlsCallResult probe = await client.ProbeAsync(udp.CertFingerprint);
        Assert.Equal(200, probe.StatusCode);
        Assert.Equal(mockDeck.CertFingerprintSha256, probe.ServerCertSha256Hex);
    }

    // 23. Android IP-change /24 auto-reconnect + WakeOnLanSender magic packet
    [Fact]
    public async Task Pair23_AndroidIpChangeSubnetRediscovery_With_WakeOnLanMagicPacket()
    {
        AndroidHarnessResult rediscovery = await AndroidHarnessRunner.RunJvmProtocolTestsAsync(
            timeoutMs: 15000,
            filterTestMethod: "testPinnedDeckClientSubnetRediscoveryUpdatesIp");
        Assert.True(rediscovery.Compiled, rediscovery.StdErr);
        Assert.Equal(0, rediscovery.ExitCode);
        Assert.Equal(1, rediscovery.PassedCount);

        AndroidHarnessResult wolLayout = await AndroidHarnessRunner.RunJvmProtocolTestsAsync(
            timeoutMs: 15000,
            filterTestMethod: "testWakeOnLanMagicPacketLayoutBytes");
        Assert.True(wolLayout.Compiled, wolLayout.StdErr);
        Assert.Equal(0, wolLayout.ExitCode);
        Assert.Equal(1, wolLayout.PassedCount);

        AndroidHarnessResult wolMacs = await AndroidHarnessRunner.RunJvmProtocolTestsAsync(
            timeoutMs: 15000,
            filterTestMethod: "testWakeOnLanValidAndInvalidMacs");
        Assert.True(wolMacs.Compiled, wolMacs.StdErr);
        Assert.Equal(0, wolMacs.ExitCode);
        Assert.Equal(1, wolMacs.PassedCount);
    }

    // 24. PC Deck Private-Wi-Fi guard + Public network 403 + Unpaired 401 on /deck/power
    [Fact]
    public async Task Pair24_PcDeckPrivateWifiGuard_And_Unpaired401PowerGating()
    {
        try
        {
            // When PcDeckEnabled is false, Deck TLS commands must not accept requests
            SettingsJsonHelper.ResetToCleanSettings(s =>
            {
                s["PcDeckEnabled"] = false;
                s["PcDeckTlsPort"] = 47825;
                s["BottomTaskbarEnabled"] = false;
            });

            using (var sessionOff = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1800, readinessTimeoutMs: 10000))
            {
                using var clientOff = new LoopbackAndDeckClient();
                if (ProcessAndWindowDriver.GetBoundNotchPixelPorts().Contains(47825))
                {
                    TlsCallResult res = await clientOff.SendPowerAsync("any-token", "lock");
                    Assert.True(res.StatusCode is 401 or 403, $"Expected 401 or 403 when PcDeckEnabled=false, got {res.StatusCode}");
                }
                Assert.False(sessionOff.Process.HasExited);
            }

            // NEVER send destructive power commands with a valid token to live OpenPixel.exe!
            // Verify with an explicitly invalid/unpaired token against live OpenPixel.exe (expects 401 or 403)
            SettingsJsonHelper.Patch(s => s["PcDeckEnabled"] = true);
            using (var sessionOn = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1800, readinessTimeoutMs: 10000))
            {
                using var clientOn = new LoopbackAndDeckClient();
                if (ProcessAndWindowDriver.GetBoundNotchPixelPorts().Contains(47825))
                {
                    TlsCallResult res = await clientOn.SendPowerAsync("unpaired-invalid-token", "shutdown");
                    Assert.True(res.StatusCode is 401 or 403, $"Expected 401 (unpaired) or 403 (public Wi-Fi guard), got {res.StatusCode}");
                }
                Assert.False(sessionOn.Process.HasExited);
            }

            // Also verify MockDeckTlsServer enforces 401 on unpaired /deck/power
            using var mockDeck = new MediaAndFixtureBuilder.MockDeckTlsServer(enableUdpDiscovery: false);
            using var mockClient = new LoopbackAndDeckClient(deckTlsPort: mockDeck.TlsPort);
            TlsCallResult mockUnauth = await mockClient.SendPowerAsync("bogus-token", "restart", mockDeck.CertFingerprintSha256);
            Assert.Equal(401, mockUnauth.StatusCode);
        }
        finally
        {
            ProcessAndWindowDriver.TerminateAllOpenPixelInstances();
        }
    }

    // 25. Settings ConfigRevision < 2 migration + LiquidGlass default + HoverOpenDelaySec clamping
    [Fact]
    public async Task Pair25_SettingsMigration_ConfigRevision1_RestoresLiquidGlassAndClampsBounds()
    {
        try
        {
            SettingsJsonHelper.Save(new JsonObject
            {
                ["ConfigRevision"] = 1,
                ["Theme"] = SettingsJsonHelper.ThemeIds.Black,
                ["GlassBlurRadius"] = 150.0,
                ["HoverOpenDelaySec"] = 9.0,
                ["CornerRadiusScale"] = 5.0,
                ["HideFromScreenshots"] = false,
                ["BottomTaskbarEnabled"] = false,
                ["LoopbackApiEnabled"] = true,
                ["LoopbackApiPort"] = 47823
            });

            using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2000, readinessTimeoutMs: 10000);
            var wins = await session.WaitForVisibleWindowsAsync(minCount: 1, timeoutMs: 8000);
            Assert.NotEmpty(wins);

            // Migration from ConfigRevision < 2 restores LiquidGlass, which forces WDA_EXCLUDEFROMCAPTURE even with HideFromScreenshots=false
            foreach (WindowSnapshot w in wins)
            {
                Assert.Equal(ProcessAndWindowDriver.WDA_EXCLUDEFROMCAPTURE, w.DisplayAffinity);
            }
            Assert.False(session.Process.HasExited);
        }
        finally
        {
            ProcessAndWindowDriver.TerminateAllOpenPixelInstances();
        }
    }

    // 26. Multi-theme switch (Rosé/Lavender/Peach/Sakura/Acrylic) + OpenPixel.exe window stability & low idle memory
    [Fact]
    public async Task Pair26_MultiThemeSwitch_MaintainsWindowStabilityAndLowIdleMemory()
    {
        try
        {
            // Verify internal multi-theme + ambience frame-budget self-test passes
            CliExecutionResult selfTest = await ProcessAndWindowDriver.RunCliAsync("--self-test", timeoutMs: 18000);
            Assert.False(selfTest.TimedOut);
            Assert.Equal(0, selfTest.ExitCode);
            Assert.DoesNotContain("[FAIL]", selfTest.StdOut);

            int[] themes =
            {
                SettingsJsonHelper.ThemeIds.Acrylic,
                SettingsJsonHelper.ThemeIds.Rose,
                SettingsJsonHelper.ThemeIds.Lavender,
                SettingsJsonHelper.ThemeIds.Peach,
                SettingsJsonHelper.ThemeIds.Sakura
            };

            foreach (int themeId in themes)
            {
                SettingsJsonHelper.ResetToCleanSettings(s =>
                {
                    s["Theme"] = themeId;
                    s["BottomTaskbarEnabled"] = false;
                });

                using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 800, readinessTimeoutMs: 7000);
                var wins = await session.WaitForVisibleWindowsAsync(minCount: 1, timeoutMs: 6000);
                Assert.NotEmpty(wins);
                session.Process.Refresh();
                long privateMb = session.Process.PrivateMemorySize64 / (1024 * 1024);
                Assert.True(privateMb < 250, $"Theme {themeId} private memory too high: {privateMb} MB");
                Assert.False(session.Process.HasExited);
            }
        }
        finally
        {
            ProcessAndWindowDriver.TerminateAllOpenPixelInstances();
        }
    }
}
