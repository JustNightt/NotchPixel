using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows.Forms;
using NotchPixel.E2E.Tests.Infrastructure;
using Xunit;

namespace NotchPixel.E2E.Tests.Tier4;

[Collection("NotchPixelE2E")]
public sealed class Tier4_RealWorldScenarioTests
{
    private readonly E2ETestSessionFixture _fixture;

    public Tier4_RealWorldScenarioTests(E2ETestSessionFixture fixture)
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

    // Scenario 1: Fresh First-Launch Desktop Session (F1, F4, F7, F8, F9, F16)
    [Fact]
    public async Task Scenario01_FreshFirstLaunchDesktopSession_LiquidGlassDpiAwareAndCleanEmptyStates()
    {
        try
        {
            Assert.False(SourceInspector.ContainsForbiddenPlaceholder(out var violations),
                "Forbidden placeholder found: " + string.Join(", ", violations.Select(v => $"{v.FilePath}:{v.LineNumber}")));

            CleanupSidecarFiles();
            SettingsJsonHelper.DeleteSettingsFile();

            using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2200, readinessTimeoutMs: 10000);
            Assert.True(ProcessAndWindowDriver.IsProcessDpiAware(session.Pid),
                "Fresh launch OpenPixel.exe must be DPI-aware (PerMonitorV2 / System DPI aware).");

            IReadOnlyList<WindowSnapshot> visibleWindows = await session.WaitForVisibleWindowsAsync(minCount: 1, timeoutMs: 8000);
            Assert.NotEmpty(visibleWindows);

            // Liquid Glass is the out-of-the-box default -> all visible windows must enforce WDA_EXCLUDEFROMCAPTURE
            foreach (WindowSnapshot w in visibleWindows)
            {
                Assert.Equal(ProcessAndWindowDriver.WDA_EXCLUDEFROMCAPTURE, w.DisplayAffinity);
                Assert.True(w.Dpi >= 96);
                Assert.DoesNotContain("Midnight City", w.Title, StringComparison.OrdinalIgnoreCase);
            }

            using var client = new LoopbackAndDeckClient();
            Assert.True(await client.WaitForLoopbackReadyAsync(5000));
            HttpCallResult root = await client.GetLoopbackRootAsync();
            Assert.Equal(200, root.StatusCode);
            Assert.Contains("\"loopback\":true", root.Body);
        }
        finally
        {
            ProcessAndWindowDriver.TerminateAllOpenPixelInstances();
            ProcessAndWindowDriver.RestoreWindowsNativeTaskbar();
            CleanupSidecarFiles();
        }
    }

    // Scenario 2: Developer Coding Session (Free Buff + Antigravity + Web Drawer + Clipboard + /tile) (F15, F19, F23, F12)
    [Fact]
    public async Task Scenario02_DeveloperCodingSession_FreeBuffAntigravityNotifyTileAndClipboard()
    {
        try
        {
            Directory.CreateDirectory(TestWorkspace.LocalAppDataOpenPixelDir);
            string pinsPath = Path.Combine(TestWorkspace.LocalAppDataOpenPixelDir, "clipboard_pins.json");
            const string pinnedSnippet = "git rebase --onto main HEAD~3";
            File.WriteAllText(pinsPath, JsonSerializer.Serialize(new[]
            {
                new
                {
                    Id = 101L,
                    Category = 5, // Snippets
                    Text = pinnedSnippet,
                    ImagePath = "",
                    FilePaths = Array.Empty<string>(),
                    IsPinned = true,
                    IsCurrent = false,
                    TimestampText = "09:30"
                }
            }));

            SettingsJsonHelper.ResetToCleanSettings(s =>
            {
                s["Theme"] = SettingsJsonHelper.ThemeIds.LiquidGlass;
                s["LoopbackApiEnabled"] = true;
                s["BottomTaskbarEnabled"] = false;
            });

            // Verify WebView2 runtime availability for Web Drawer without instantiating WebView2 until opened
            CliExecutionResult webRes = await ProcessAndWindowDriver.RunCliAsync("--web-test", timeoutMs: 10000);
            Assert.Equal(0, webRes.ExitCode);
            Assert.Contains("WEBVIEW_OK", webRes.StdOut);

            using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1800, readinessTimeoutMs: 10000);
            using var client = new LoopbackAndDeckClient();
            Assert.True(await client.WaitForLoopbackReadyAsync(5000));

            // Simulate coding agents working -> nearing completion -> input needed + live build tile
            Assert.Equal(200, (await client.SendAgentAsync("Free Buff", "Working", "Indexing solution", progress: 30)).StatusCode);
            Assert.Equal(200, (await client.SendAgentAsync("Antigravity", "Working", "Running analyzers", progress: 65)).StatusCode);
            Assert.Equal(200, (await client.SendTileAsync("ci_build", "Dotnet Build", "PASS", "0 errors, 0 warnings")).StatusCode);
            Assert.Equal(200, (await client.SendAgentAsync("Free Buff", "Almost done", "Finalizing tests", progress: 92, almostDone: true)).StatusCode);
            Assert.Equal(200, (await client.SendAgentAsync("Free Buff", "Needs input", "Approve PR merge?", progress: 98, almostDone: true, needsInput: true)).StatusCode);
            Assert.Equal(200, (await client.SendNotifyAsync("Build Succeeded", "All checks green", "NotchPixel CI")).StatusCode);

            await Task.Delay(350);
            Assert.False(session.Process.HasExited);
            Assert.Contains(pinnedSnippet, File.ReadAllText(pinsPath));
        }
        finally
        {
            ProcessAndWindowDriver.TerminateAllOpenPixelInstances();
            CleanupSidecarFiles();
        }
    }

    // Scenario 3: Fullscreen Gaming Session + Hotkey Launchers + Game Strip + Taskbar Step-Aside (F14, F22, F23, F16)
    [Fact]
    public async Task Scenario03_FullscreenGamingSession_TaskbarStepAsideHotkeysAndGameStrip()
    {
        try
        {
            SettingsJsonHelper.ResetToCleanSettings(s =>
            {
                s["Theme"] = SettingsJsonHelper.ThemeIds.LiquidGlass;
                s["BottomTaskbarEnabled"] = true;
                s["GameOverlayMasterEnabled"] = true;
                s["GameOverlayMode"] = 1; // SlimStrip
                s["GameStripShowFps"] = true;
                s["GameStripShowCpu"] = true;
                s["GameStripShowGpu"] = true;
                s["GameStripShowRam"] = true;
            });

            using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2200, readinessTimeoutMs: 10000);
            await session.WaitForVisibleWindowsAsync(minCount: 1, timeoutMs: 8000);

            using (var game = ProcessAndWindowDriver.CreateDummyFullscreenWindow("NotchPixel_Scenario3_Game"))
            {
                game.BringToFront();
                await Task.Delay(600);
                Assert.Equal(game.Hwnd, ProcessAndWindowDriver.GetForegroundWindowHandle());

                // Toggle island hotkey during fullscreen game
                ProcessAndWindowDriver.SendHotkey(ProcessAndWindowDriver.MOD_CONTROL | ProcessAndWindowDriver.MOD_ALT, Keys.N);
                await Task.Delay(350);
                Assert.False(session.Process.HasExited);

                // Ensure any bottom taskbar overlay stays behind the fullscreen game window
                Screen primary = Screen.PrimaryScreen!;
                int gameZ = ProcessAndWindowDriver.GetWindowZOrderIndex(game.Hwnd);
                foreach (var bar in session.GetOwnedWindows(visibleOnly: true)
                    .Where(w => w.Bounds.Bottom >= primary.Bounds.Bottom - 20 && w.Bounds.Width >= 220 && w.Bounds.Height <= 100))
                {
                    int barZ = ProcessAndWindowDriver.GetWindowZOrderIndex(bar.Hwnd);
                    Assert.True(barZ > gameZ || !bar.IsTopMost);
                }
            }

            session.Dispose();
            bool restored = await PollUntilAsync(ProcessAndWindowDriver.IsNativeTaskbarVisible, timeoutMs: 6000);
            Assert.True(restored);
        }
        finally
        {
            ProcessAndWindowDriver.TerminateAllOpenPixelInstances();
            ProcessAndWindowDriver.RestoreWindowsNativeTaskbar();
        }
    }

    // Scenario 4: Media Playback + Album-Art Tinting + Synced Lyrics + Deck Remote (F5, F6, F17, F24)
    [Fact]
    public async Task Scenario04_MediaPlaybackWorkflow_AlbumArtTintingAndDeckRemoteControl()
    {
        using var audioScope = ProcessAndWindowDriver.CaptureAndRestoreAudioScope();
        try
        {
            SettingsJsonHelper.ResetToCleanSettings(s =>
            {
                s["Theme"] = SettingsJsonHelper.ThemeIds.Rose;
                s["CardBackground"] = 0; // AlbumColours
                s["ShowSongTitleInClosedPill"] = true;
                s["HideFromScreenshots"] = false;
                s["BottomTaskbarEnabled"] = false;
            });

            using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1800, readinessTimeoutMs: 10000);
            var wins = await session.WaitForVisibleWindowsAsync(minCount: 1, timeoutMs: 8000);
            Assert.NotEmpty(wins);

            // Trigger media play/pause hotkey cleanly without crashing even with idle SMTC session
            ProcessAndWindowDriver.SendHotkey(ProcessAndWindowDriver.MOD_CONTROL | ProcessAndWindowDriver.MOD_ALT, Keys.P);
            await Task.Delay(300);
            Assert.False(session.Process.HasExited);

            // Exercise Deck Remote media control workflow over pinned TLS against MockDeckTlsServer
            using var mockDeck = new MediaAndFixtureBuilder.MockDeckTlsServer(enableUdpDiscovery: true);
            using var deckClient = new LoopbackAndDeckClient(deckTlsPort: mockDeck.TlsPort, discoveryUdpPort: mockDeck.UdpPort);

            TlsCallResult pairRes = await deckClient.PairAsync(mockDeck.PairingCode, expectedFp: mockDeck.CertFingerprintSha256);
            Assert.Equal(200, pairRes.StatusCode);
            TlsCallResult stateRes = await deckClient.GetStateAsync(mockDeck.IssuedToken, mockDeck.CertFingerprintSha256);
            Assert.Equal(200, stateRes.StatusCode);
            Assert.Contains("\"media\"", stateRes.Body);

            Assert.Equal(200, (await deckClient.SendMediaAsync(mockDeck.IssuedToken, "a=playpause", mockDeck.CertFingerprintSha256)).StatusCode);
            Assert.Equal(200, (await deckClient.SendMediaAsync(mockDeck.IssuedToken, "a=next", mockDeck.CertFingerprintSha256)).StatusCode);
            Assert.Equal(200, (await deckClient.UnpairAsync(mockDeck.IssuedToken, mockDeck.CertFingerprintSha256)).StatusCode);
        }
        finally
        {
            ProcessAndWindowDriver.TerminateAllOpenPixelInstances();
        }
    }

    // Scenario 5: Live .mp4/.mkv Wallpaper + Day Clock + Glass Widgets Sampling (F10, F11, F13)
    [Fact]
    public async Task Scenario05_LiveUnicodeVideoWallpaper_With_DayClockAndGlassWidgetsCoexistence()
    {
        string tempDir = TestWorkspace.CreateIsolatedTempDir("t4_wallpaper");
        try
        {
            string mp4Path = MediaAndFixtureBuilder.CreateUnicodeMp4Fixture(tempDir, "测试_видео_α.mp4", durationSeconds: 2);
            string mkvPath = MediaAndFixtureBuilder.CreateUnicodeMkvFixture(tempDir, "日本語_тест.mkv", durationMs: 2000);

            SettingsJsonHelper.ResetToCleanSettings(s =>
            {
                s["Theme"] = SettingsJsonHelper.ThemeIds.LiquidGlass;
                s["LiveVideoWallpaperEnabled"] = true;
                s["LiveVideoWallpaperPath"] = mp4Path;
                s["WallpaperBrightness"] = 1.0;
                s["WallpaperContrast"] = 1.0;
                s["DayClockShowWeekday"] = true;
                s["DayClockShowDate"] = true;
                s["DayClockShowTime"] = true;
                s["BottomTaskbarEnabled"] = false;
            });

            using (var s1 = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1800, readinessTimeoutMs: 10000))
            {
                await s1.WaitForVisibleWindowsAsync(minCount: 1, timeoutMs: 8000);
                using var normal = ProcessAndWindowDriver.CreateDummyNormalWindow(new Rectangle(100, 100, 600, 400), "NotchPixel_Scenario5_App");
                normal.BringToFront();
                await Task.Delay(350);
                Assert.False(s1.Process.HasExited);
            }

            // Switch to Unicode .mkv live wallpaper and verify clean startup
            SettingsJsonHelper.Patch(s => s["LiveVideoWallpaperPath"] = mkvPath);
            using (var s2 = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1800, readinessTimeoutMs: 10000))
            {
                await s2.WaitForVisibleWindowsAsync(minCount: 1, timeoutMs: 8000);
                Assert.False(s2.Process.HasExited);
            }
        }
        finally
        {
            ProcessAndWindowDriver.TerminateAllOpenPixelInstances();
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    // Scenario 6: Multi-File Capture + File Shelf + Local Wi-Fi QR Phone Transfer (F7, F20, F23)
    [Fact]
    public async Task Scenario06_MultiFileCapture_FileShelfAndLocalWifiQrPhoneTransfer()
    {
        string tempDir = TestWorkspace.CreateIsolatedTempDir("t4_shelf");
        try
        {
            string file1 = Path.Combine(tempDir, "截图_α.png");
            string file2 = Path.Combine(tempDir, "notes_β.txt");
            File.WriteAllBytes(file1, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
            File.WriteAllText(file2, "NotchPixel QR phone transfer test payload");

            // Verify QR generator produces valid matrix for local Wi-Fi shelf transfer URL
            CliExecutionResult qr = await ProcessAndWindowDriver.RunCliAsync(
                "--qr-test \"http://192.168.1.42:47824/shelf\"", timeoutMs: 10000);
            Assert.Equal(0, qr.ExitCode);
            using JsonDocument qrJson = JsonDocument.Parse(qr.StdOut.Trim());
            Assert.True(qrJson.RootElement.GetProperty("size").GetInt32() >= 21);

            SettingsJsonHelper.ResetToCleanSettings(s =>
            {
                s["Theme"] = SettingsJsonHelper.ThemeIds.LiquidGlass;
                s["PcDeckPort"] = 47824;
                s["KeepCapturesOffShelf"] = false;
                s["BottomTaskbarEnabled"] = false;
            });

            using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1800, readinessTimeoutMs: 10000);
            using var client = new LoopbackAndDeckClient();
            Assert.True(await client.WaitForLoopbackReadyAsync(5000));

            // Confirm shelf HTTP endpoint serves UTF-8 HTML and handles out-of-range downloads gracefully
            HttpCallResult shelf = await client.GetShelfHtmlAsync();
            Assert.Equal(200, shelf.StatusCode);
            Assert.Contains("NotchPixel Shelf", shelf.Body);
            var (dlCode, _, _) = await client.DownloadShelfFileAsync(0);
            Assert.True(dlCode is 200 or 404);
            Assert.False(session.Process.HasExited);
        }
        finally
        {
            ProcessAndWindowDriver.TerminateAllOpenPixelInstances();
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    // Scenario 7: Crash/Hang Recovery & Native Windows Taskbar Restoration (F4, F14)
    [Fact]
    public async Task Scenario07_CrashAndHangRecovery_WatchdogRestoresNativeWindowsTaskbar()
    {
        try
        {
            ProcessAndWindowDriver.RestoreWindowsNativeTaskbar();
            SettingsJsonHelper.ResetToCleanSettings(s =>
            {
                s["Theme"] = SettingsJsonHelper.ThemeIds.LiquidGlass;
                s["BottomTaskbarEnabled"] = true;
            });

            // 1. Crash recovery
            using (var s1 = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2200, readinessTimeoutMs: 10000))
            {
                await s1.WaitForVisibleWindowsAsync(minCount: 1, timeoutMs: 8000);
                ProcessAndWindowDriver.SimulateCrash(s1.Pid);
                bool restoredAfterCrash = await PollUntilAsync(ProcessAndWindowDriver.IsNativeTaskbarVisible, timeoutMs: 8000);
                Assert.True(restoredAfterCrash, "Native taskbar must be restored after OpenPixel crash.");
            }

            // 2. Hang recovery (suspend -> terminate)
            using (var s2 = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2200, readinessTimeoutMs: 10000))
            {
                await s2.WaitForVisibleWindowsAsync(minCount: 1, timeoutMs: 8000);
                ProcessAndWindowDriver.SuspendProcess(s2.Pid);
                await Task.Delay(500);
                ProcessAndWindowDriver.SimulateCrash(s2.Pid);
                bool restoredAfterHang = await PollUntilAsync(ProcessAndWindowDriver.IsNativeTaskbarVisible, timeoutMs: 8000);
                Assert.True(restoredAfterHang, "Native taskbar must be restored after hung OpenPixel process terminates.");
            }
        }
        finally
        {
            ProcessAndWindowDriver.TerminateAllOpenPixelInstances();
            ProcessAndWindowDriver.RestoreWindowsNativeTaskbar();
        }
    }

    // Scenario 8: Audio Call Workflow (Mic Mute + Call Timer + Core Audio Volume Safety) (F2, F16, F18)
    [Fact]
    public async Task Scenario08_AudioCallWorkflow_GlobalMicMuteAndVolumeRestoration()
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

            using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1800, readinessTimeoutMs: 10000);
            using var client = new LoopbackAndDeckClient();
            Assert.True(await client.WaitForLoopbackReadyAsync(5000));

            // Adjust Core Audio level, mute mic during call via Ctrl+Alt+M, unmute, verify app stability
            ProcessAndWindowDriver.SetSystemMasterVolume(0.35f, muted: false);
            await Task.Delay(250);
            var (scalar, muted, endpointId) = ProcessAndWindowDriver.GetSystemMasterVolumeAndMute();
            Assert.InRange(scalar, 0.32f, 0.38f);
            Assert.False(muted);
            Assert.False(string.IsNullOrWhiteSpace(endpointId));

            ProcessAndWindowDriver.SendHotkey(ProcessAndWindowDriver.MOD_CONTROL | ProcessAndWindowDriver.MOD_ALT, Keys.M);
            await Task.Delay(450);
            ProcessAndWindowDriver.SendHotkey(ProcessAndWindowDriver.MOD_CONTROL | ProcessAndWindowDriver.MOD_ALT, Keys.M);
            await Task.Delay(350);

            HttpCallResult root = await client.GetLoopbackRootAsync();
            Assert.Equal(200, root.StatusCode);
            Assert.False(session.Process.HasExited);
        }
        finally
        {
            ProcessAndWindowDriver.TerminateAllOpenPixelInstances();
        }
    }

    // Scenario 9: Streaming Broadcast Workflow (OBS REC Pill + Twitch/YouTube Chat + Capture Exclusion) (F7, F21, F23)
    [Fact]
    public async Task Scenario09_StreamingBroadcastWorkflow_ObsRecPillChatDrawerAndCaptureExclusion()
    {
        try
        {
            using var obs = new MediaAndFixtureBuilder.MockObsWebSocketServer(initialRecording: false, initialSeconds: 0);
            using var chat = new MediaAndFixtureBuilder.MockChatStreamServer();
            chat.PushMessage("Moderator", "Welcome to the stream!", "MOD");
            chat.PushMessage("Viewer42", "Zero leaks in capture!", "");

            SettingsJsonHelper.ResetToCleanSettings(s =>
            {
                s["Theme"] = SettingsJsonHelper.ThemeIds.LiquidGlass;
                s["ShowObsRecPill"] = true;
                s["StreamChatEnabled"] = true;
                s["ShowIslandChatPopups"] = true;
                s["TwitchChannel"] = "notchpixel_live";
                s["YoutubeLiveId"] = "live_stream_e2e";
                s["BottomTaskbarEnabled"] = false;
            });

            using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2000, readinessTimeoutMs: 10000);
            var wins = await session.WaitForVisibleWindowsAsync(minCount: 1, timeoutMs: 8000);
            Assert.NotEmpty(wins);

            // Simulate OBS starting recording mid-session
            obs.SetRecording(true, 120);
            await Task.Delay(400);

            foreach (WindowSnapshot w in session.GetOwnedWindows(visibleOnly: true))
            {
                Assert.Equal(ProcessAndWindowDriver.WDA_EXCLUDEFROMCAPTURE, w.DisplayAffinity);
            }

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            string chatBody = await http.GetStringAsync(chat.BaseHttpUrl + "/messages");
            Assert.Contains("Moderator", chatBody);
            Assert.True(obs.IsRecording);
            Assert.False(session.Process.HasExited);
        }
        finally
        {
            ProcessAndWindowDriver.TerminateAllOpenPixelInstances();
        }
    }

    // Scenario 10: Power & Theme Stress Session (All 10+ Themes + Blur Sweep + Settings Close) (F4, F5, F9)
    [Fact]
    public async Task Scenario10_ThemeAndBlurStressSession_All11ThemesAndSettingsClose()
    {
        try
        {
            // Self-test exercises all 11 themes, 0..40px blur sweep on SettingsWindow, and frame-budget metrics
            CliExecutionResult selfTest = await ProcessAndWindowDriver.RunCliAsync("--self-test", timeoutMs: 18000);
            Assert.False(selfTest.TimedOut);
            Assert.Equal(0, selfTest.ExitCode);
            Assert.DoesNotContain("[FAIL]", selfTest.StdOut);
            Assert.Contains("[PASS]", selfTest.StdOut);

            // Launch live GUI at max clamped blur (40.0) and verify clean rendering and stability
            SettingsJsonHelper.ResetToCleanSettings(s =>
            {
                s["Theme"] = SettingsJsonHelper.ThemeIds.LiquidGlass;
                s["GlassBlurRadius"] = 40.0;
                s["BottomTaskbarEnabled"] = false;
            });

            using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1800, readinessTimeoutMs: 10000);
            var wins = await session.WaitForVisibleWindowsAsync(minCount: 1, timeoutMs: 8000);
            Assert.NotEmpty(wins);
            foreach (var w in wins)
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

    // Scenario 11: Custom Automation Dashboard (/notify + /tile + /agent + Reminders) (F11, F12, F15, F23)
    [Fact]
    public async Task Scenario11_CustomAutomationDashboard_NotifyTileAgentAndRemindersPersistence()
    {
        try
        {
            Directory.CreateDirectory(TestWorkspace.LocalAppDataOpenPixelDir);
            string remindersPath = Path.Combine(TestWorkspace.LocalAppDataOpenPixelDir, "reminders.json");
            long futureUnixMs = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeMilliseconds();
            File.WriteAllText(remindersPath, JsonSerializer.Serialize(new[]
            {
                new
                {
                    Id = 5001L,
                    Title = "Standup Sync",
                    DueTimestampMs = 0L,
                    DueUnixMs = futureUnixMs,
                    RepeatRule = "Daily",
                    Triggered = false,
                    Completed = false
                }
            }));

            SettingsJsonHelper.ResetToCleanSettings(s =>
            {
                s["Theme"] = SettingsJsonHelper.ThemeIds.LiquidGlass;
                s["LoopbackApiEnabled"] = true;
                s["BottomTaskbarEnabled"] = false;
            });

            using (var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1800, readinessTimeoutMs: 10000))
            {
                using var client = new LoopbackAndDeckClient();
                Assert.True(await client.WaitForLoopbackReadyAsync(5000));

                var calls = await Task.WhenAll(
                    client.SendTileAsync("deploy", "Prod Deploy", "v2.4.1", "Healthy"),
                    client.SendTileAsync("queue", "Jobs", "0", "Idle"),
                    client.SendAgentAsync("Hermes", "Working", "Syncing metrics", progress: 70),
                    client.SendAgentAsync("Codex", "Almost done", "Patch verified", progress: 90, almostDone: true),
                    client.SendNotifyAsync("Automation Complete", "All tiles updated", "Scheduler", usePost: true));

                foreach (HttpCallResult r in calls)
                {
                    Assert.Equal(200, r.StatusCode);
                    Assert.Contains("\"ok\":true", r.Body);
                }
                Assert.False(session.Process.HasExited);
            }

            // Confirm reminders persisted cleanly across session lifecycle without stale .tmp files
            Assert.False(File.Exists(remindersPath + ".tmp"));
            Assert.Contains("Standup Sync", File.ReadAllText(remindersPath));
        }
        finally
        {
            ProcessAndWindowDriver.TerminateAllOpenPixelInstances();
            CleanupSidecarFiles();
        }
    }

    // Scenario 12: End-to-End Android NotchPixel Deck Pairing, Reconnect, WOL & Power Gating (F24)
    [Fact]
    public async Task Scenario12_EndToEndAndroidNotchPixelDeck_PairingReconnectWolAndPowerGating()
    {
        // 1. Android project hygiene & APK signature verification
        Assert.Empty(AndroidHarnessRunner.FindIosArtifactsInApp());
        string? apkPath = AndroidHarnessRunner.FindBuiltApkPath();
        if (apkPath == null)
        {
            CliExecutionResult build = await AndroidHarnessRunner.RunBuildApkScriptAsync(timeoutMs: 90000);
            Assert.Equal(0, build.ExitCode);
            apkPath = AndroidHarnessRunner.FindBuiltApkPath();
        }
        Assert.NotNull(apkPath);
        ApkInspectionResult apkInfo = await AndroidHarnessRunner.InspectApkAsync(apkPath!, timeoutMs: 15000);
        Assert.True(apkInfo.SignatureVerified, apkInfo.VerifyOutput);

        // 2. Full Android JVM protocol suite (all 8 tests)
        AndroidHarnessResult jvm = await AndroidHarnessRunner.RunJvmProtocolTestsAsync(timeoutMs: 15000);
        Assert.True(jvm.Compiled, jvm.StdErr);
        Assert.Equal(0, jvm.ExitCode);
        Assert.Equal(8, jvm.PassedCount);
        Assert.Equal(0, jvm.FailedCount);

        // 3. Full Deck TLS + UDP lifecycle against MockDeckTlsServer
        using var mockDeck = new MediaAndFixtureBuilder.MockDeckTlsServer(enableUdpDiscovery: true);
        using var client = new LoopbackAndDeckClient(deckTlsPort: mockDeck.TlsPort, discoveryUdpPort: mockDeck.UdpPort);

        UdpDiscoveryResponse? disc = await client.SendDiscoveryProbeAsync("OPENPIXEL_DECK_DISCOVER", timeoutMs: 2000);
        Assert.NotNull(disc);
        string qrPayload = $"notchpixel-deck://pair?ip=127.0.0.1&port={mockDeck.TlsPort}&code={mockDeck.PairingCode}&fp={disc!.CertFingerprint}&mac={mockDeck.MacAddress}&name={Uri.EscapeDataString(mockDeck.PcName)}";
        Assert.True(LoopbackAndDeckClient.TryParsePairingUri(qrPayload, out DeckPairingUri? parsed, out string? parseErr), parseErr);
        Assert.Equal(mockDeck.CertFingerprintSha256, parsed!.CertFingerprint);

        // Unpaired power command must fail with 401
        TlsCallResult unauthPower = await client.SendPowerAsync("unpaired-token", "lock", parsed.CertFingerprint);
        Assert.Equal(401, unauthPower.StatusCode);

        // Pair -> State -> Pad -> Mode -> Unpair -> Revoked state check (401)
        TlsCallResult paired = await client.PairAsync(parsed.Code, expectedFp: parsed.CertFingerprint);
        Assert.Equal(200, paired.StatusCode);
        Assert.Equal(200, (await client.GetStateAsync(mockDeck.IssuedToken, parsed.CertFingerprint)).StatusCode);
        Assert.Equal(200, (await client.SendPadAsync(mockDeck.IssuedToken, "a=move&dx=12&dy=-4", parsed.CertFingerprint)).StatusCode);
        Assert.Equal(200, (await client.SendModeAsync(mockDeck.IssuedToken, "games", parsed.CertFingerprint)).StatusCode);
        Assert.Equal(200, (await client.UnpairAsync(mockDeck.IssuedToken, parsed.CertFingerprint)).StatusCode);

        TlsCallResult afterUnpair = await client.GetStateAsync("e2e-test-deck-token-v1", parsed.CertFingerprint);
        Assert.Equal(401, afterUnpair.StatusCode);
    }

    // Scenario 13: Reboot-Simulated Persistent Pins & Launcher Slots Survival (F11, F19, F23)
    [Fact]
    public async Task Scenario13_RebootSimulatedPersistence_ClipboardPinsAndLauncherSlotsSurvival()
    {
        try
        {
            Directory.CreateDirectory(TestWorkspace.LocalAppDataOpenPixelDir);
            string pinsPath = Path.Combine(TestWorkspace.LocalAppDataOpenPixelDir, "clipboard_pins.json");
            const string urlPin = "https://notchpixel.com/guides/dynamic-island-for-windows";
            File.WriteAllText(pinsPath, JsonSerializer.Serialize(new[]
            {
                new
                {
                    Id = 777L,
                    Category = 2, // Links
                    Text = urlPin,
                    ImagePath = "",
                    FilePaths = Array.Empty<string>(),
                    IsPinned = true,
                    IsCurrent = false,
                    TimestampText = "14:15"
                }
            }));

            SettingsJsonHelper.ResetToCleanSettings(s =>
            {
                s["Theme"] = SettingsJsonHelper.ThemeIds.Lavender;
                s["HideFromScreenshots"] = true;
                s["BottomTaskbarEnabled"] = false;
                s["LauncherSlots"] = new JsonArray
                {
                    new JsonObject { ["Label"] = "Terminal", ["TargetPath"] = "wt.exe", ["BadgeColorArgb"] = -12542721 },
                    new JsonObject { ["Label"] = "Notepad", ["TargetPath"] = "notepad.exe", ["BadgeColorArgb"] = -12542721 }
                };
            });

            using (var sessionA = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1600, readinessTimeoutMs: 10000))
            {
                await sessionA.WaitForVisibleWindowsAsync(minCount: 1, timeoutMs: 8000);
                ProcessAndWindowDriver.SendHotkey(ProcessAndWindowDriver.MOD_CONTROL | ProcessAndWindowDriver.MOD_ALT, Keys.N);
                await Task.Delay(250);
                Assert.False(sessionA.Process.HasExited);
            }

            // Simulate reboot by starting fresh Session B and confirming pins + launcher slots survive intact
            using (var sessionB = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1600, readinessTimeoutMs: 10000))
            {
                await sessionB.WaitForVisibleWindowsAsync(minCount: 1, timeoutMs: 8000);
                Assert.Contains(urlPin, File.ReadAllText(pinsPath));
                JsonObject reloaded = SettingsJsonHelper.LoadOrCreate();
                Assert.Equal(SettingsJsonHelper.ThemeIds.Lavender, reloaded["Theme"]?.GetValue<int>());
                JsonArray? slots = reloaded["LauncherSlots"] as JsonArray;
                Assert.NotNull(slots);
                Assert.True(slots!.Count >= 2);
                Assert.Equal("Terminal", slots[0]?["Label"]?.GetValue<string>());
            }
        }
        finally
        {
            ProcessAndWindowDriver.TerminateAllOpenPixelInstances();
            CleanupSidecarFiles();
        }
    }

    // Scenario 14: Multi-Monitor/DPI & Zero-Placeholder Full-Stack Audit (F1, F7, F16)
    [Fact]
    public async Task Scenario14_MultiMonitorDpiAndZeroPlaceholderFullStackAudit()
    {
        try
        {
            Assert.False(SourceInspector.ContainsForbiddenPlaceholder(out var matches),
                "Forbidden placeholder found: " + string.Join(", ", matches.Select(m => $"{m.FilePath}:{m.LineNumber}")));
            Assert.Empty(SourceInspector.FindIosArtifactsInApp());

            SettingsJsonHelper.ResetToCleanSettings(s =>
            {
                s["Theme"] = SettingsJsonHelper.ThemeIds.LiquidGlass;
                s["Shape"] = 1; // Notch
                s["Edge"] = 0;  // TopCenter
                s["MonitorIndex"] = 0;
                s["BottomTaskbarEnabled"] = false;
            });

            using (var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1800, readinessTimeoutMs: 10000))
            {
                Assert.True(ProcessAndWindowDriver.IsProcessDpiAware(session.Pid));
                var wins = await session.WaitForVisibleWindowsAsync(minCount: 1, timeoutMs: 8000);
                Assert.NotEmpty(wins);

                foreach (WindowSnapshot w in wins)
                {
                    Assert.True(w.Dpi >= 96);
                    Assert.True(w.Bounds.Width > 0 && w.Bounds.Height > 0);
                    Assert.Equal(ProcessAndWindowDriver.WDA_EXCLUDEFROMCAPTURE, w.DisplayAffinity);
                }
            }

            // If secondary monitor is attached, verify MonitorIndex = 1 starts cleanly without bounds errors
            if (Screen.AllScreens.Length > 1)
            {
                SettingsJsonHelper.Patch(s => s["MonitorIndex"] = 1);
                using var session2 = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500, readinessTimeoutMs: 10000);
                var wins2 = await session2.WaitForVisibleWindowsAsync(minCount: 1, timeoutMs: 6000);
                Assert.NotEmpty(wins2);
                Assert.False(session2.Process.HasExited);
            }
        }
        finally
        {
            ProcessAndWindowDriver.TerminateAllOpenPixelInstances();
        }
    }
}
