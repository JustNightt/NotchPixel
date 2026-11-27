using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows.Forms;
using NotchPixel.E2E.Tests.Infrastructure;
using Xunit;

namespace NotchPixel.E2E.Tests.Tier1;

[Collection("NotchPixelE2E")]
public sealed class Tier1_R3_R4_WallpaperTaskbarAgentsTests
{
    private const uint WM_CLOSE = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    private readonly E2ETestSessionFixture _fixture;

    public Tier1_R3_R4_WallpaperTaskbarAgentsTests(E2ETestSessionFixture fixture)
    {
        _fixture = fixture;
    }

    private static async Task<bool> CloseGracefullyAsync(ProcessAndWindowDriver.GuiSession session, int timeoutMs = 4000)
    {
        var wins = session.GetOwnedWindows(visibleOnly: false);
        var main = wins.FirstOrDefault(w => w.Title.Equals("OpenPixel", StringComparison.OrdinalIgnoreCase))
                   ?? wins.FirstOrDefault();
        if (main != null)
        {
            PostMessage(main.Hwnd, WM_CLOSE, 0, 0);
        }

        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (session.Process.HasExited) return true;
            await Task.Delay(100);
        }
        return session.Process.HasExited;
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> predicate, int timeoutMs = 5000, int pollMs = 100)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return true;
            await Task.Delay(pollMs);
        }
        return predicate();
    }

    // =========================================================================
    // F13: Live Video Wallpaper (.mp4 / .mkv) — 6 Tests
    // =========================================================================

    [Fact]
    public async Task F13_01_Mp4WallpaperFixture_EnablesAndRunsCleanly()
    {
        string dir = TestWorkspace.CreateIsolatedTempDir("f13_mp4");
        try
        {
            string mp4Path = MediaAndFixtureBuilder.CreateValidMp4(Path.Combine(dir, "sample_wall.mp4"));
            SettingsJsonHelper.ResetToCleanSettings(o =>
            {
                o["BottomTaskbarEnabled"] = false;
                o["LiveVideoWallpaperEnabled"] = true;
                o["LiveVideoWallpaperPath"] = mp4Path;
            });

            await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2000);
            Assert.False(session.Process.HasExited, "OpenPixel.exe exited prematurely with MP4 live wallpaper enabled.");

            var windows = await session.WaitForVisibleWindowsAsync(minCount: 2, timeoutMs: 6000);
            Assert.True(windows.Count >= 2, $"Expected Island + LiveVideoWallpaperForm windows, found {windows.Count}.");

            Rectangle primaryBounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
            bool hasDesktopSizedWallpaper = windows.Any(w =>
                w.Bounds.Width >= primaryBounds.Width - 20 &&
                w.Bounds.Height >= primaryBounds.Height - 20);
            Assert.True(hasDesktopSizedWallpaper, "Expected a desktop-sized LiveVideoWallpaperForm window.");

            using var client = new LoopbackAndDeckClient();
            Assert.True(await client.WaitForLoopbackReadyAsync(5000));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task F13_02_MkvWallpaperFixture_EnablesAndRunsCleanly()
    {
        string dir = TestWorkspace.CreateIsolatedTempDir("f13_mkv");
        try
        {
            string mkvPath = MediaAndFixtureBuilder.CreateValidMkv(Path.Combine(dir, "sample_wall.mkv"));
            SettingsJsonHelper.ResetToCleanSettings(o =>
            {
                o["BottomTaskbarEnabled"] = false;
                o["LiveVideoWallpaperEnabled"] = true;
                o["LiveVideoWallpaperPath"] = mkvPath;
            });

            await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2000);
            Assert.False(session.Process.HasExited, "OpenPixel.exe exited prematurely with MKV live wallpaper enabled.");

            var windows = await session.WaitForVisibleWindowsAsync(minCount: 2, timeoutMs: 6000);
            Assert.True(windows.Count >= 2, "Expected LiveVideoWallpaperForm to be visible for MKV fixture.");

            using var client = new LoopbackAndDeckClient();
            var root = await client.GetLoopbackRootAsync();
            Assert.Equal(200, root.StatusCode);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task F13_03_UnicodeMp4AndMkvPaths_PersistedAndHandledWithoutCrash()
    {
        string dir = TestWorkspace.CreateIsolatedTempDir("f13_unicode");
        try
        {
            string unicodeMp4 = MediaAndFixtureBuilder.CreateUnicodeMp4Fixture(dir, "测试_видео_α.mp4");
            string unicodeMkv = MediaAndFixtureBuilder.CreateUnicodeMkvFixture(dir, "日本語_тест.mkv");

            foreach (string path in new[] { unicodeMp4, unicodeMkv })
            {
                Assert.True(File.Exists(path));
                SettingsJsonHelper.ResetToCleanSettings(o =>
                {
                    o["BottomTaskbarEnabled"] = false;
                    o["LiveVideoWallpaperEnabled"] = true;
                    o["LiveVideoWallpaperPath"] = path;
                });

                await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1800);
                Assert.False(session.Process.HasExited, $"OpenPixel.exe crashed on Unicode wallpaper path '{path}'.");
                Assert.True(await CloseGracefullyAsync(session), "Expected clean exit for Unicode wallpaper path.");
                Assert.Equal(path, SettingsJsonHelper.GetValue<string>("LiveVideoWallpaperPath"));
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task F13_04_WallpaperColorAdjustments_HueSatBrightContrast_PersistAndRun()
    {
        string dir = TestWorkspace.CreateIsolatedTempDir("f13_color");
        try
        {
            string mp4Path = MediaAndFixtureBuilder.CreateValidMp4(Path.Combine(dir, "color_adj.mp4"));
            SettingsJsonHelper.ResetToCleanSettings(o =>
            {
                o["BottomTaskbarEnabled"] = false;
                o["LiveVideoWallpaperEnabled"] = true;
                o["LiveVideoWallpaperPath"] = mp4Path;
                o["WallpaperHue"] = 45.0f;
                o["WallpaperSaturation"] = 125.0f;
                o["WallpaperBrightness"] = 90.0f;
                o["WallpaperContrast"] = 115.0f;
            });

            await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1800);
            Assert.False(session.Process.HasExited);
            Assert.True(await CloseGracefullyAsync(session));

            Assert.Equal(45.0f, SettingsJsonHelper.GetValue<float>("WallpaperHue"), 1);
            Assert.Equal(125.0f, SettingsJsonHelper.GetValue<float>("WallpaperSaturation"), 1);
            Assert.Equal(90.0f, SettingsJsonHelper.GetValue<float>("WallpaperBrightness"), 1);
            Assert.Equal(115.0f, SettingsJsonHelper.GetValue<float>("WallpaperContrast"), 1);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task F13_05_WallpaperStaysBelowNormalAndFullscreenWindows_NeverStealsFocus()
    {
        string dir = TestWorkspace.CreateIsolatedTempDir("f13_zorder");
        try
        {
            string mp4Path = MediaAndFixtureBuilder.CreateValidMp4(Path.Combine(dir, "zorder.mp4"));
            SettingsJsonHelper.ResetToCleanSettings(o =>
            {
                o["BottomTaskbarEnabled"] = false;
                o["LiveVideoWallpaperEnabled"] = true;
                o["LiveVideoWallpaperPath"] = mp4Path;
            });

            await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1800);
            var owned = await session.WaitForVisibleWindowsAsync(minCount: 2, timeoutMs: 6000);
            Rectangle primaryBounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
            var wallpaperWin = owned.FirstOrDefault(w =>
                w.Bounds.Width >= primaryBounds.Width - 20 &&
                w.Bounds.Height >= primaryBounds.Height - 20);
            Assert.NotNull(wallpaperWin);

            using var normalWin = ProcessAndWindowDriver.CreateDummyNormalWindow(title: "NotchPixelE2E_F13_Normal");
            normalWin.BringToFront();
            await Task.Delay(300);

            int normalZ = ProcessAndWindowDriver.GetWindowZOrderIndex(normalWin.Hwnd);
            int wallZ = ProcessAndWindowDriver.GetWindowZOrderIndex(wallpaperWin.Hwnd);
            Assert.True(normalZ >= 0 && wallZ >= 0, $"Invalid Z-order indices normal={normalZ}, wallpaper={wallZ}.");
            Assert.True(normalZ < wallZ, $"Wallpaper (z={wallZ}) must remain below normal window (z={normalZ}).");

            using var fsWin = ProcessAndWindowDriver.CreateDummyFullscreenWindow(title: "NotchPixelE2E_F13_FS");
            fsWin.BringToFront();
            await Task.Delay(400);
            Assert.Equal(fsWin.Hwnd, ProcessAndWindowDriver.GetForegroundWindowHandle());
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task F13_06_MissingOrCorruptVideoPath_ResilientWithoutCrash()
    {
        string dir = TestWorkspace.CreateIsolatedTempDir("f13_corrupt");
        try
        {
            string corruptPath = MediaAndFixtureBuilder.CreateCorruptVideoFixture(Path.Combine(dir, "broken.mp4"));
            SettingsJsonHelper.ResetToCleanSettings(o =>
            {
                o["BottomTaskbarEnabled"] = false;
                o["LiveVideoWallpaperEnabled"] = true;
                o["LiveVideoWallpaperPath"] = corruptPath;
            });

            await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2000);
            Assert.False(session.Process.HasExited, "Corrupt video file must not crash OpenPixel.exe.");

            using var client = new LoopbackAndDeckClient();
            Assert.True(await client.WaitForLoopbackReadyAsync(5000));
            var notify = await client.SendNotifyAsync("Wallpaper Resilience", "Corrupt file skipped", "Test");
            Assert.Equal(200, notify.StatusCode);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    // =========================================================================
    // F14: Custom Bottom Notch Taskbar & Resilience — 6 Tests
    // =========================================================================

    [Fact]
    public async Task F14_01_BottomTaskbarEnabled_CreatesBottomWindowAndHidesNativeTray()
    {
        try
        {
            SettingsJsonHelper.ResetToCleanSettings(o => o["BottomTaskbarEnabled"] = true);

            await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2000);
            var visible = await session.WaitForVisibleWindowsAsync(minCount: 2, timeoutMs: 6000);

            Rectangle scr = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
            bool hasBottomBar = visible.Any(w =>
                w.Bounds.Bottom >= scr.Bottom - 20 &&
                w.Bounds.Height is > 20 and <= 90 &&
                w.Bounds.Width >= 300);
            Assert.True(hasBottomBar, "Expected a custom bottom notch taskbar window near the bottom edge.");

            bool nativeHidden = await WaitUntilAsync(() => !ProcessAndWindowDriver.IsNativeTaskbarVisible(), 4000);
            Assert.True(nativeHidden, "Expected native Shell_TrayWnd to be hidden while BottomTaskbarEnabled is true.");
        }
        finally
        {
            ProcessAndWindowDriver.RestoreWindowsNativeTaskbar();
        }
    }

    [Fact]
    public async Task F14_02_BottomTaskbar_CleanExitRestoresNativeTaskbar()
    {
        try
        {
            SettingsJsonHelper.ResetToCleanSettings(o => o["BottomTaskbarEnabled"] = true);

            await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2000);
            Assert.True(await WaitUntilAsync(() => !ProcessAndWindowDriver.IsNativeTaskbarVisible(), 4000));

            bool exited = await CloseGracefullyAsync(session, timeoutMs: 5000);
            Assert.True(exited, "OpenPixel.exe should exit cleanly when IslandMainForm receives WM_CLOSE.");

            bool restored = await WaitUntilAsync(() => ProcessAndWindowDriver.IsNativeTaskbarVisible(), 4000);
            Assert.True(restored, "Native Windows taskbar must be restored immediately on clean exit.");
        }
        finally
        {
            ProcessAndWindowDriver.RestoreWindowsNativeTaskbar();
        }
    }

    [Fact]
    public async Task F14_03_BottomTaskbar_CrashSimulation_WatchdogRestoresNativeTaskbar()
    {
        try
        {
            SettingsJsonHelper.ResetToCleanSettings(o => o["BottomTaskbarEnabled"] = true);

            await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2000);
            Assert.True(await WaitUntilAsync(() => !ProcessAndWindowDriver.IsNativeTaskbarVisible(), 4000),
                "Precondition failed: native taskbar was not hidden by BottomTaskbarForm.");

            // Kill only the main process so --watchdog restores Shell_TrayWnd automatically.
            ProcessAndWindowDriver.SimulateCrash(session.Pid);

            bool restoredByWatchdog = await WaitUntilAsync(() => ProcessAndWindowDriver.IsNativeTaskbarVisible(), 6000);
            Assert.True(restoredByWatchdog, "Watchdog process must restore Windows native taskbar after OpenPixel.exe crash.");
        }
        finally
        {
            ProcessAndWindowDriver.RestoreWindowsNativeTaskbar();
        }
    }

    [Fact]
    public async Task F14_04_BottomTaskbar_SuspendHangSimulation_AndRecoveryRestoresNativeTaskbar()
    {
        ProcessAndWindowDriver.GuiSession? session = null;
        try
        {
            SettingsJsonHelper.ResetToCleanSettings(o => o["BottomTaskbarEnabled"] = true);
            session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2000);
            Assert.True(await WaitUntilAsync(() => !ProcessAndWindowDriver.IsNativeTaskbarVisible(), 4000));

            // Simulate a frozen UI thread / hung process
            ProcessAndWindowDriver.SuspendProcess(session.Pid);
            await Task.Delay(500);

            // Terminate the hung main process so watchdog triggers recovery
            ProcessAndWindowDriver.ResumeProcess(session.Pid);
            ProcessAndWindowDriver.SimulateCrash(session.Pid);

            bool restored = await WaitUntilAsync(() => ProcessAndWindowDriver.IsNativeTaskbarVisible(), 6000);
            Assert.True(restored, "Native taskbar must be restored after hung process termination.");
        }
        finally
        {
            if (session != null)
            {
                try { ProcessAndWindowDriver.ResumeProcess(session.Pid); } catch { }
                await session.DisposeAsync();
            }
            ProcessAndWindowDriver.RestoreWindowsNativeTaskbar();
        }
    }

    [Fact]
    public async Task F14_05_BottomTaskbar_FullscreenWindow_DoesNotStealFocus()
    {
        try
        {
            SettingsJsonHelper.ResetToCleanSettings(o =>
            {
                o["BottomTaskbarEnabled"] = true;
                o["GameRulesAllowList"] = new JsonArray("testhost.exe", "dotnet.exe", "NotchPixel.E2E.Tests.exe");
            });

            await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2000);
            await session.WaitForVisibleWindowsAsync(minCount: 2, timeoutMs: 6000);

            using var fs = ProcessAndWindowDriver.CreateDummyFullscreenWindow(title: "NotchPixelE2E_F14_Game");
            fs.BringToFront();
            await Task.Delay(600);

            Assert.Equal(fs.Hwnd, ProcessAndWindowDriver.GetForegroundWindowHandle());
            Assert.False(session.Process.HasExited);
        }
        finally
        {
            ProcessAndWindowDriver.RestoreWindowsNativeTaskbar();
        }
    }

    [Fact]
    public async Task F14_06_BottomTaskbar_SelfTestAndSourceContract_NoFakeAppIcons()
    {
        var selfTest = await ProcessAndWindowDriver.RunCliAsync("--self-test", timeoutMs: 20000);
        Assert.Equal(0, selfTest.ExitCode);
        Assert.Contains("Custom bottom taskbar renders the synced pinned + running app list without crashing", selfTest.StdOut);

        var taskbarFiles = SourceInspector.FindMatchesInCSharp(@"TaskbarApps\.Refresh|User Pinned\\TaskBar");
        Assert.NotEmpty(taskbarFiles);
        Assert.False(SourceInspector.ContainsForbiddenPlaceholder(out var placeholders),
            $"Forbidden placeholders found: {string.Join("; ", placeholders.Select(p => p.LineText))}");
    }

    // =========================================================================
    // F15: Free Buff & Coding Agent Monitor — 6 Tests
    // =========================================================================

    [Fact]
    public async Task F15_01_AgentEndpoint_GetAndPost_AcceptsAllSupportedCodingAgents()
    {
        SettingsJsonHelper.ResetToCleanSettings(o => o["BottomTaskbarEnabled"] = false);
        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1800);
        using var client = new LoopbackAndDeckClient();
        Assert.True(await client.WaitForLoopbackReadyAsync(5000));

        string[] agents = { "Free Buff", "Claude Code", "Codex", "Antigravity", "Hermes", "CustomRunner" };
        for (int i = 0; i < agents.Length; i++)
        {
            bool usePost = i % 2 == 1;
            var res = await client.SendAgentAsync(
                name: agents[i],
                status: "Working",
                step: $"Running step {i + 1}",
                progress: (i + 1) * 10,
                usePost: usePost);
            Assert.Equal(200, res.StatusCode);
            using var doc = JsonDocument.Parse(res.Body);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("agent", doc.RootElement.GetProperty("endpoint").GetString());
        }
    }

    [Fact]
    public async Task F15_02_AgentEndpoint_AlmostDoneHighlight_AcceptedAt80PercentPlus()
    {
        SettingsJsonHelper.ResetToCleanSettings(o => o["BottomTaskbarEnabled"] = false);
        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1800);
        using var client = new LoopbackAndDeckClient();
        Assert.True(await client.WaitForLoopbackReadyAsync(5000));

        var res = await client.SendAgentAsync(
            name: "Free Buff",
            status: "Almost done",
            step: "Finalizing build verification",
            progress: 88,
            almostDone: true);
        Assert.Equal(200, res.StatusCode);
        Assert.Contains("\"ok\":true", res.Body);
        Assert.False(session.Process.HasExited);
    }

    [Fact]
    public async Task F15_03_AgentEndpoint_NeedsInputAlert_TriggersCleanly()
    {
        SettingsJsonHelper.ResetToCleanSettings(o => o["BottomTaskbarEnabled"] = false);
        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1800);
        using var client = new LoopbackAndDeckClient();
        Assert.True(await client.WaitForLoopbackReadyAsync(5000));

        var res = await client.SendAgentAsync(
            name: "Free Buff",
            status: "Needs input",
            step: "Approve production deployment?",
            progress: 92,
            needsInput: true,
            usePost: true);
        Assert.Equal(200, res.StatusCode);
        Assert.Contains("\"endpoint\":\"agent\"", res.Body);

        await Task.Delay(300);
        Assert.False(session.Process.HasExited);
        Assert.Equal(200, (await client.GetLoopbackRootAsync()).StatusCode);
    }

    [Fact]
    public async Task F15_04_AgentMonitor_FreeBuffStatusFile_PickedUpAndCleanedUp()
    {
        string freeBuffDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".freebuff");
        string statusPath = Path.Combine(freeBuffDir, "status.json");
        bool dirExisted = Directory.Exists(freeBuffDir);
        string? origContent = File.Exists(statusPath) ? File.ReadAllText(statusPath) : null;

        try
        {
            Directory.CreateDirectory(freeBuffDir);
            string payload = JsonSerializer.Serialize(new
            {
                status = "Needs input",
                step = "E2E Free Buff file watcher check",
                workspace = "NotchPixel New",
                progress = 85,
                almost_done = true,
                needs_input = true
            });
            File.WriteAllText(statusPath, payload);

            SettingsJsonHelper.ResetToCleanSettings(o => o["BottomTaskbarEnabled"] = false);
            await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2200);
            Assert.False(session.Process.HasExited);

            // Delete status file while running to verify stale/deleted file cleanup doesn't crash
            File.Delete(statusPath);
            await Task.Delay(1400);
            Assert.False(session.Process.HasExited);
        }
        finally
        {
            if (origContent != null)
            {
                Directory.CreateDirectory(freeBuffDir);
                File.WriteAllText(statusPath, origContent);
            }
            else
            {
                if (File.Exists(statusPath)) File.Delete(statusPath);
                if (!dirExisted && Directory.Exists(freeBuffDir) && !Directory.EnumerateFileSystemEntries(freeBuffDir).Any())
                {
                    Directory.Delete(freeBuffDir);
                }
            }
        }
    }

    [Fact]
    public async Task F15_05_AgentEndpoint_DoneAndSubsequentUpdates_HandledGracefully()
    {
        SettingsJsonHelper.ResetToCleanSettings(o => o["BottomTaskbarEnabled"] = false);
        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1800);
        using var client = new LoopbackAndDeckClient();
        Assert.True(await client.WaitForLoopbackReadyAsync(5000));

        Assert.Equal(200, (await client.SendAgentAsync("Antigravity", "Working", "Indexing", 30)).StatusCode);
        Assert.Equal(200, (await client.SendAgentAsync("Antigravity", "Done", "Completed", 100)).StatusCode);
        Assert.Equal(200, (await client.SendAgentAsync("Claude Code", "Working", "Testing", 55)).StatusCode);
        Assert.False(session.Process.HasExited);
    }

    [Fact]
    public async Task F15_06_CustomCodingAgentsSetting_PersistsAcrossLaunch()
    {
        SettingsJsonHelper.ResetToCleanSettings(o =>
        {
            o["BottomTaskbarEnabled"] = false;
            o["CustomCodingAgents"] = new JsonArray("aider", "goose-cli");
        });

        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1800);
        using var client = new LoopbackAndDeckClient();
        Assert.True(await client.WaitForLoopbackReadyAsync(5000));
        Assert.Equal(200, (await client.SendAgentAsync("aider", "Working", "Refactoring", 40)).StatusCode);

        Assert.True(await CloseGracefullyAsync(session));
        var persisted = SettingsJsonHelper.GetValue<List<string>>("CustomCodingAgents");
        Assert.NotNull(persisted);
        Assert.Contains("aider", persisted);
        Assert.Contains("goose-cli", persisted);
    }
}
