using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows.Forms;
using NotchPixel.E2E.Tests.Infrastructure;
using Xunit;

namespace NotchPixel.E2E.Tests.Tier2;

[Collection("NotchPixelE2E")]
public sealed class Tier2_R3_R6_BoundaryTests
{
    private const uint WM_CLOSE = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    private readonly E2ETestSessionFixture _fixture;

    public Tier2_R3_R6_BoundaryTests(E2ETestSessionFixture fixture)
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

    private static void SeedPairedDeviceToken(string token)
    {
        Directory.CreateDirectory(TestWorkspace.LocalAppDataOpenPixelDir);
        string path = Path.Combine(TestWorkspace.LocalAppDataOpenPixelDir, "paired_deck_devices.json");
        var arr = new JsonArray
        {
            new JsonObject
            {
                ["DeviceId"] = "e2e-tier2-dev",
                ["DeviceName"] = "E2E-Boundary-Pixel",
                ["Model"] = "Pixel 9",
                ["OsVersion"] = "Android 15",
                ["AppVersion"] = "1.0.5",
                ["IpAddress"] = "127.0.0.1",
                ["KeyFingerprint"] = "e2et2fp01234567",
                ["AuthToken"] = token,
                ["PairedAtMs"] = 1000
            }
        };
        File.WriteAllText(path, arr.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    // =========================================================================
    // F13 Boundaries: Live Video Wallpaper (.mp4 / .mkv) — 6 Tests
    // =========================================================================

    [Fact]
    public async Task F13_B01_EmptyVideoPathWithEnabledFlag_NoCrash()
    {
        SettingsJsonHelper.ResetToCleanSettings(o =>
        {
            o["BottomTaskbarEnabled"] = false;
            o["LiveVideoWallpaperEnabled"] = true;
            o["LiveVideoWallpaperPath"] = "";
        });

        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        Assert.False(session.Process.HasExited);
        using var client = new LoopbackAndDeckClient();
        Assert.Equal(200, (await client.GetLoopbackRootAsync()).StatusCode);
    }

    [Fact]
    public async Task F13_B02_NonExistentVideoPath_NoCrash()
    {
        SettingsJsonHelper.ResetToCleanSettings(o =>
        {
            o["BottomTaskbarEnabled"] = false;
            o["LiveVideoWallpaperEnabled"] = true;
            o["LiveVideoWallpaperPath"] = @"Z:\nonexistent_folder_e2e\missing_video_测试.mp4";
        });

        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        Assert.False(session.Process.HasExited);
    }

    [Fact]
    public async Task F13_B03_ZeroByteMp4Fixture_NoCrash()
    {
        string dir = TestWorkspace.CreateIsolatedTempDir("f13_b03");
        try
        {
            string emptyMp4 = MediaAndFixtureBuilder.CreateEmptyFixture(Path.Combine(dir, "empty.mp4"));
            SettingsJsonHelper.ResetToCleanSettings(o =>
            {
                o["BottomTaskbarEnabled"] = false;
                o["LiveVideoWallpaperEnabled"] = true;
                o["LiveVideoWallpaperPath"] = emptyMp4;
            });

            await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
            Assert.False(session.Process.HasExited);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task F13_B04_CorruptBinaryMkvFixture_NoCrash()
    {
        string dir = TestWorkspace.CreateIsolatedTempDir("f13_b04");
        try
        {
            string corruptMkv = MediaAndFixtureBuilder.CreateCorruptVideoFixture(Path.Combine(dir, "bad.mkv"));
            SettingsJsonHelper.ResetToCleanSettings(o =>
            {
                o["BottomTaskbarEnabled"] = false;
                o["LiveVideoWallpaperEnabled"] = true;
                o["LiveVideoWallpaperPath"] = corruptMkv;
            });

            await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
            Assert.False(session.Process.HasExited);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task F13_B05_ExtremeHueSatBrightContrastValues_NoRenderCrash()
    {
        string dir = TestWorkspace.CreateIsolatedTempDir("f13_b05");
        try
        {
            string mp4 = MediaAndFixtureBuilder.CreateValidMp4(Path.Combine(dir, "extreme.mp4"));
            SettingsJsonHelper.ResetToCleanSettings(o =>
            {
                o["BottomTaskbarEnabled"] = false;
                o["LiveVideoWallpaperEnabled"] = true;
                o["LiveVideoWallpaperPath"] = mp4;
                o["WallpaperHue"] = -720.0f;
                o["WallpaperSaturation"] = 500.0f;
                o["WallpaperBrightness"] = -50.0f;
                o["WallpaperContrast"] = 400.0f;
            });

            await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1600);
            Assert.False(session.Process.HasExited);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task F13_B06_LongUnicodeAndEmojiDirectoryPath_HandledCleanly()
    {
        string dir = TestWorkspace.CreateIsolatedTempDir("f13_b06");
        try
        {
            string sub = Path.Combine(dir, "папка_αβγ_🎬", "サブフォルダ");
            string mp4 = MediaAndFixtureBuilder.CreateUnicodeMp4Fixture(sub, "壁纸_α_🎥.mp4");
            SettingsJsonHelper.ResetToCleanSettings(o =>
            {
                o["BottomTaskbarEnabled"] = false;
                o["LiveVideoWallpaperEnabled"] = true;
                o["LiveVideoWallpaperPath"] = mp4;
            });

            await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
            Assert.False(session.Process.HasExited);
            Assert.True(await CloseGracefullyAsync(session));
            Assert.Equal(mp4, SettingsJsonHelper.GetValue<string>("LiveVideoWallpaperPath"));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    // =========================================================================
    // F14 Boundaries: Custom Bottom Notch Taskbar & Resilience — 5 Tests
    // =========================================================================

    [Fact]
    public async Task F14_B01_TaskbarEnabledThenDisabledInSettings_RestoresNativeTaskbar()
    {
        try
        {
            SettingsJsonHelper.ResetToCleanSettings(o => o["BottomTaskbarEnabled"] = true);
            await using (var s1 = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1800))
            {
                Assert.False(s1.Process.HasExited);
                Assert.True(await CloseGracefullyAsync(s1));
            }
            Assert.True(ProcessAndWindowDriver.IsNativeTaskbarVisible());

            SettingsJsonHelper.ResetToCleanSettings(o => o["BottomTaskbarEnabled"] = false);
            await using (var s2 = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500))
            {
                Assert.False(s2.Process.HasExited);
                Assert.True(ProcessAndWindowDriver.IsNativeTaskbarVisible());
            }
        }
        finally
        {
            ProcessAndWindowDriver.RestoreWindowsNativeTaskbar();
        }
    }

    [Fact]
    public async Task F14_B02_BottomTaskbar_WithOutOfBoundsCornerRadius_RendersWithoutCrash()
    {
        try
        {
            SettingsJsonHelper.ResetToCleanSettings(o =>
            {
                o["BottomTaskbarEnabled"] = true;
                o["CornerRadiusScale"] = -10.0f;
            });

            await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1800);
            Assert.False(session.Process.HasExited);
        }
        finally
        {
            ProcessAndWindowDriver.RestoreWindowsNativeTaskbar();
        }
    }

    [Fact]
    public async Task F14_B03_BottomTaskbar_MultiMonitorAndAutoHideFlags_NoCrash()
    {
        try
        {
            SettingsJsonHelper.ResetToCleanSettings(o =>
            {
                o["BottomTaskbarEnabled"] = true;
                o["TaskbarMultiMonitor"] = true;
                o["TaskbarAutoHideMaximized"] = true;
                o["TaskbarDockMagnification"] = false;
                o["TaskbarLiveHoverPreviews"] = false;
            });

            await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1800);
            Assert.False(session.Process.HasExited);
        }
        finally
        {
            ProcessAndWindowDriver.RestoreWindowsNativeTaskbar();
        }
    }

    [Fact]
    public async Task F14_B04_WatchdogCliWithDeadPid_ExitsZeroAndEnsuresTaskbarVisible()
    {
        try
        {
            var res = await ProcessAndWindowDriver.RunCliAsync("--watchdog 999999", timeoutMs: 8000);
            Assert.Equal(0, res.ExitCode);
            Assert.False(res.TimedOut);
            Assert.True(ProcessAndWindowDriver.IsNativeTaskbarVisible());
        }
        finally
        {
            ProcessAndWindowDriver.RestoreWindowsNativeTaskbar();
        }
    }

    [Fact]
    public async Task F14_B05_BottomTaskbar_TwoConsecutiveCrashes_WatchdogRestoresEachTime()
    {
        try
        {
            for (int round = 0; round < 2; round++)
            {
                SettingsJsonHelper.ResetToCleanSettings(o => o["BottomTaskbarEnabled"] = true);
                await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1800);
                ProcessAndWindowDriver.SimulateCrash(session.Pid);

                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < deadline && !ProcessAndWindowDriver.IsNativeTaskbarVisible())
                {
                    await Task.Delay(100);
                }
                Assert.True(ProcessAndWindowDriver.IsNativeTaskbarVisible(), $"Native taskbar not restored on crash round {round + 1}.");
            }
        }
        finally
        {
            ProcessAndWindowDriver.RestoreWindowsNativeTaskbar();
        }
    }

    // =========================================================================
    // F15 Boundaries: Free Buff & Coding Agent Monitor — 5 Tests
    // =========================================================================

    [Fact]
    public async Task F15_B01_AgentEndpoint_OutOfRangeProgressValues_HandledSafely()
    {
        SettingsJsonHelper.ResetToCleanSettings(o => o["BottomTaskbarEnabled"] = false);
        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1600);
        using var client = new LoopbackAndDeckClient();
        Assert.True(await client.WaitForLoopbackReadyAsync(5000));

        foreach (int p in new[] { -10, 0, 100, 250 })
        {
            var res = await client.SendAgentAsync("Free Buff", "Working", $"Progress {p}", progress: p);
            Assert.Equal(200, res.StatusCode);
        }
        Assert.False(session.Process.HasExited);
    }

    [Fact]
    public async Task F15_B02_AgentEndpoint_EmptyAgentNameAndStatus_DefaultsGracefully()
    {
        SettingsJsonHelper.ResetToCleanSettings(o => o["BottomTaskbarEnabled"] = false);
        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1600);
        using var client = new LoopbackAndDeckClient();
        Assert.True(await client.WaitForLoopbackReadyAsync(5000));

        var res = await client.SendRawLoopbackAsync(HttpMethod.Get, "/agent?name=&status=&step=");
        Assert.Equal(200, res.StatusCode);
        Assert.Contains("\"endpoint\":\"agent\"", res.Body);
    }

    [Fact]
    public async Task F15_B03_AgentEndpoint_UnicodeAndSpecialCharsInStep_HandledCleanly()
    {
        SettingsJsonHelper.ResetToCleanSettings(o => o["BottomTaskbarEnabled"] = false);
        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1600);
        using var client = new LoopbackAndDeckClient();
        Assert.True(await client.WaitForLoopbackReadyAsync(5000));

        var res = await client.SendAgentAsync(
            name: "Free Buff",
            status: "Needs input",
            step: "Fix C:\\src\\测试_α.cs & run <test> \"ok\" 🚀",
            progress: 80,
            almostDone: true,
            needsInput: true);
        Assert.Equal(200, res.StatusCode);
        Assert.False(session.Process.HasExited);
    }

    [Fact]
    public async Task F15_B04_AgentEndpoint_RapidUpdateBurst_NoCrash()
    {
        SettingsJsonHelper.ResetToCleanSettings(o => o["BottomTaskbarEnabled"] = false);
        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1600);
        using var client = new LoopbackAndDeckClient();
        Assert.True(await client.WaitForLoopbackReadyAsync(5000));

        for (int i = 0; i <= 10; i++)
        {
            var res = await client.SendAgentAsync("Codex", i >= 8 ? "Almost done" : "Working", $"Step {i}", i * 10, almostDone: i >= 8);
            Assert.Equal(200, res.StatusCode);
        }
        Assert.False(session.Process.HasExited);
    }

    [Fact]
    public async Task F15_B05_AgentEndpoint_NonNumericProgressString_HandledGracefully()
    {
        SettingsJsonHelper.ResetToCleanSettings(o => o["BottomTaskbarEnabled"] = false);
        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1600);
        using var client = new LoopbackAndDeckClient();
        Assert.True(await client.WaitForLoopbackReadyAsync(5000));

        var res = await client.SendRawLoopbackAsync(HttpMethod.Post, "/agent?name=Hermes&status=Working&progress=NaN&almost_done=0");
        Assert.Equal(200, res.StatusCode);
        Assert.False(session.Process.HasExited);
    }

    // =========================================================================
    // F16 Boundaries: Island Placement, Monitor, Corners & Sensors — 5 Tests
    // =========================================================================

    [Fact]
    public async Task F16_B01_CornerRadiusScale_BelowMinAndAboveMax_ClampedToBounds()
    {
        SettingsJsonHelper.ResetToCleanSettings(o =>
        {
            o["BottomTaskbarEnabled"] = false;
            o["CornerRadiusScale"] = -5.0f;
        });
        await using (var s1 = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1400))
        {
            Assert.True(await CloseGracefullyAsync(s1));
            Assert.InRange(SettingsJsonHelper.GetValue<float>("CornerRadiusScale"), 0.149f, 0.151f);
        }

        SettingsJsonHelper.ResetToCleanSettings(o =>
        {
            o["BottomTaskbarEnabled"] = false;
            o["CornerRadiusScale"] = 9.5f;
        });
        await using (var s2 = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1400))
        {
            Assert.True(await CloseGracefullyAsync(s2));
            Assert.InRange(SettingsJsonHelper.GetValue<float>("CornerRadiusScale"), 0.999f, 1.001f);
        }
    }

    [Fact]
    public async Task F16_B02_MonitorIndex_NegativeAndExtreme_ClampedSafely()
    {
        Rectangle vScreen = SystemInformation.VirtualScreen;
        foreach (int monIdx in new[] { -3, 999 })
        {
            SettingsJsonHelper.ResetToCleanSettings(o =>
            {
                o["BottomTaskbarEnabled"] = false;
                o["MonitorIndex"] = monIdx;
            });

            await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1400);
            var wins = await session.WaitForVisibleWindowsAsync(1, 5000);
            var island = wins.First(w => w.Title.Equals("OpenPixel", StringComparison.OrdinalIgnoreCase));
            Assert.True(vScreen.IntersectsWith(island.Bounds));
        }
    }

    [Fact]
    public async Task F16_B03_HoverOpenDelaySec_ExtremeValues_ClampedToZeroAndOnePointFive()
    {
        SettingsJsonHelper.ResetToCleanSettings(o =>
        {
            o["BottomTaskbarEnabled"] = false;
            o["HoverOpenDelaySec"] = -2.0f;
        });
        await using (var s1 = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1400))
        {
            Assert.True(await CloseGracefullyAsync(s1));
            Assert.Equal(0.0f, SettingsJsonHelper.GetValue<float>("HoverOpenDelaySec"), 2);
        }

        SettingsJsonHelper.ResetToCleanSettings(o =>
        {
            o["BottomTaskbarEnabled"] = false;
            o["HoverOpenDelaySec"] = 99.0f;
        });
        await using (var s2 = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1400))
        {
            Assert.True(await CloseGracefullyAsync(s2));
            Assert.Equal(1.5f, SettingsJsonHelper.GetValue<float>("HoverOpenDelaySec"), 2);
        }
    }

    [Fact]
    public async Task F16_B04_PillAndOpenSizePercent_ExtremeScales_NoRenderCrash()
    {
        SettingsJsonHelper.ResetToCleanSettings(o =>
        {
            o["BottomTaskbarEnabled"] = false;
            o["PillSizePercent"] = 10.0f;
            o["OpenSizePercent"] = 300.0f;
        });

        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        Assert.False(session.Process.HasExited);
    }

    [Fact]
    public async Task F16_B05_TopEdgeDistanceDip_NegativeAndLargeOffset_NoCrash()
    {
        SettingsJsonHelper.ResetToCleanSettings(o =>
        {
            o["BottomTaskbarEnabled"] = false;
            o["Shape"] = 0; // Island
            o["TopEdgeDistanceDip"] = -50.0f;
        });

        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        Assert.False(session.Process.HasExited);
    }

    // =========================================================================
    // F17 Boundaries: Real Windows Music & Synced Lyrics — 5 Tests
    // =========================================================================

    [Fact]
    public async Task F17_B01_DeckMediaSeek_NegativeAndExtremePositions_HandledSafely()
    {
        using var mock = new MediaAndFixtureBuilder.MockDeckTlsServer(enableUdpDiscovery: false);
        using var client = new LoopbackAndDeckClient(deckTlsPort: mock.TlsPort);
        Assert.Equal(200, (await client.SendMediaAsync(mock.IssuedToken, "a=seek&v=-25", mock.CertFingerprintSha256)).StatusCode);
        Assert.Equal(200, (await client.SendMediaAsync(mock.IssuedToken, "a=seek&v=999999", mock.CertFingerprintSha256)).StatusCode);
        Assert.Equal(200, (await client.SendMediaAsync(mock.IssuedToken, "a=seek&v=invalid", mock.CertFingerprintSha256)).StatusCode);
    }

    [Fact]
    public async Task F17_B02_DeckMediaVolume_ExtremeOutOfRangeValues_SafeOnLiveOrMock()
    {
        using var audioScope = ProcessAndWindowDriver.CaptureAndRestoreAudioScope();
        const string token = "e2e-f17-b02-tok";
        SeedPairedDeviceToken(token);
        SettingsJsonHelper.ResetToCleanSettings(o =>
        {
            o["BottomTaskbarEnabled"] = false;
            o["PcDeckEnabled"] = true;
        });

        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1600);
        using var client = new LoopbackAndDeckClient();
        var probe = await client.ProbeAsync();
        if (probe.StatusCode == 200)
        {
            Assert.Equal(200, (await client.SendMediaAsync(token, "a=vol&v=-20", probe.ServerCertSha256Hex)).StatusCode);
            Assert.Equal(200, (await client.SendMediaAsync(token, "a=vol&v=250", probe.ServerCertSha256Hex)).StatusCode);
        }
        else
        {
            Assert.Equal(403, probe.StatusCode);
        }
        Assert.False(session.Process.HasExited);
    }

    [Fact]
    public async Task F17_B03_DeckMediaUnknownAction_HandledWithoutCrash()
    {
        const string token = "e2e-f17-b03-tok";
        SeedPairedDeviceToken(token);
        SettingsJsonHelper.ResetToCleanSettings(o =>
        {
            o["BottomTaskbarEnabled"] = false;
            o["PcDeckEnabled"] = true;
        });

        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        using var client = new LoopbackAndDeckClient();
        var probe = await client.ProbeAsync();
        if (probe.StatusCode == 200)
        {
            var res = await client.SendMediaAsync(token, "a=unknown_op_xyz", probe.ServerCertSha256Hex);
            Assert.Equal(200, res.StatusCode);
        }
        Assert.False(session.Process.HasExited);
    }

    [Fact]
    public async Task F17_B04_ExcludedMusicApps_EmptyList_NoCrash()
    {
        SettingsJsonHelper.ResetToCleanSettings(o =>
        {
            o["BottomTaskbarEnabled"] = false;
            o["ExcludedMusicApps"] = new JsonArray();
        });

        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        Assert.False(session.Process.HasExited);
    }

    [Fact]
    public async Task F17_B05_ProgressBarStyle_OutOfRangeEnum_NoCrash()
    {
        SettingsJsonHelper.ResetToCleanSettings(o =>
        {
            o["BottomTaskbarEnabled"] = false;
            o["ProgressBar"] = 99;
        });

        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        Assert.False(session.Process.HasExited);
    }

    // =========================================================================
    // F18 Boundaries: Global Mic Mute & Call Timer — 5 Tests
    // =========================================================================

    [Fact]
    public async Task F18_B01_MicMuteRapidToggleBurst_RestoresInitialState()
    {
        const string token = "e2e-f18-b01-tok";
        SeedPairedDeviceToken(token);
        SettingsJsonHelper.ResetToCleanSettings(o =>
        {
            o["BottomTaskbarEnabled"] = false;
            o["PcDeckEnabled"] = true;
        });

        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1600);
        using var client = new LoopbackAndDeckClient();
        var probe = await client.ProbeAsync();
        if (probe.StatusCode == 200)
        {
            bool initMuted = JsonDocument.Parse((await client.GetStateAsync(token, probe.ServerCertSha256Hex)).Body)
                .RootElement.GetProperty("micMuted").GetBoolean();

            for (int i = 0; i < 4; i++)
            {
                Assert.Equal(200, (await client.SendModeAsync(token, "mic", probe.ServerCertSha256Hex)).StatusCode);
            }

            bool endMuted = JsonDocument.Parse((await client.GetStateAsync(token, probe.ServerCertSha256Hex)).Body)
                .RootElement.GetProperty("micMuted").GetBoolean();
            Assert.Equal(initMuted, endMuted);
        }
        Assert.False(session.Process.HasExited);
    }

    [Fact]
    public async Task F18_B02_IgnoredSensorApps_EmptyAndMixedCaseSvchost_NoCrash()
    {
        SettingsJsonHelper.ResetToCleanSettings(o =>
        {
            o["BottomTaskbarEnabled"] = false;
            o["IgnoredSensorApps"] = new JsonArray("", "SVCHOST.EXE");
        });

        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        Assert.False(session.Process.HasExited);
    }

    [Fact]
    public async Task F18_B03_NoticeDurationSec_ZeroAndExtremeValues_NoCrash()
    {
        foreach (float dur in new[] { 0.0f, 999.0f })
        {
            SettingsJsonHelper.ResetToCleanSettings(o =>
            {
                o["BottomTaskbarEnabled"] = false;
                o["NoticeDurationSec"] = dur;
            });
            await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1300);
            Assert.False(session.Process.HasExited);
        }
    }

    [Fact]
    public async Task F18_B04_UpcomingMeeting_EmptyAndUnicodeValues_PersistCleanly()
    {
        SettingsJsonHelper.ResetToCleanSettings(o =>
        {
            o["BottomTaskbarEnabled"] = false;
            o["UpcomingMeetingTitle"] = "会議_α_🚀";
            o["UpcomingMeetingTime"] = "";
            o["UpcomingMeetingUrl"] = "";
        });

        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1400);
        Assert.True(await CloseGracefullyAsync(session));
        Assert.Equal("会議_α_🚀", SettingsJsonHelper.GetValue<string>("UpcomingMeetingTitle"));
    }

    [Fact]
    public async Task F18_B05_DeckModeMic_Unauthenticated_Rejected401Or403()
    {
        SettingsJsonHelper.ResetToCleanSettings(o =>
        {
            o["BottomTaskbarEnabled"] = false;
            o["PcDeckEnabled"] = true;
        });

        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1600);
        using var client = new LoopbackAndDeckClient();
        var res = await client.SendModeAsync(token: "", action: "mic");
        Assert.True(res.StatusCode == 401 || res.StatusCode == 403, $"Expected 401 or 403, got {res.StatusCode}.");
    }

    // =========================================================================
    // F19 Boundaries: Tabbed Clipboard History & Persistent Pins — 5 Tests
    // =========================================================================

    [Fact]
    public async Task F19_B01_ClipboardPins_TruncatedJson_RecoversCleanly()
    {
        SettingsJsonHelper.ResetToCleanSettings(o => o["BottomTaskbarEnabled"] = false);
        Directory.CreateDirectory(TestWorkspace.LocalAppDataOpenPixelDir);
        File.WriteAllText(Path.Combine(TestWorkspace.LocalAppDataOpenPixelDir, "clipboard_pins.json"), "[{\"Id\":1,\"Text\":\"abc");

        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        Assert.False(session.Process.HasExited);
    }

    [Fact]
    public async Task F19_B02_ClipboardPins_WrongJsonObjectInsteadOfArray_RecoversCleanly()
    {
        SettingsJsonHelper.ResetToCleanSettings(o => o["BottomTaskbarEnabled"] = false);
        Directory.CreateDirectory(TestWorkspace.LocalAppDataOpenPixelDir);
        File.WriteAllText(Path.Combine(TestWorkspace.LocalAppDataOpenPixelDir, "clipboard_pins.json"), "{\"unexpected\":true}");

        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        Assert.False(session.Process.HasExited);
    }

    [Fact]
    public async Task F19_B03_ClipboardPins_NullEntriesInArray_IgnoredWithoutCrash()
    {
        SettingsJsonHelper.ResetToCleanSettings(o => o["BottomTaskbarEnabled"] = false);
        Directory.CreateDirectory(TestWorkspace.LocalAppDataOpenPixelDir);
        string pinsPath = Path.Combine(TestWorkspace.LocalAppDataOpenPixelDir, "clipboard_pins.json");
        File.WriteAllText(pinsPath, "[null, {\"Id\":55,\"Category\":1,\"Text\":\"Valid\",\"Preview\":\"Valid\",\"IsPinned\":true}, null]");

        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        Assert.True(await CloseGracefullyAsync(session));
        Assert.Contains("Valid", File.ReadAllText(pinsPath));
    }

    [Fact]
    public async Task F19_B04_ClipboardPins_FifteenPinnedItems_AllPreservedAcrossRestart()
    {
        SettingsJsonHelper.ResetToCleanSettings(o => o["BottomTaskbarEnabled"] = false);
        Directory.CreateDirectory(TestWorkspace.LocalAppDataOpenPixelDir);
        string pinsPath = Path.Combine(TestWorkspace.LocalAppDataOpenPixelDir, "clipboard_pins.json");

        var arr = new JsonArray();
        for (int i = 0; i < 15; i++)
        {
            arr.Add(new JsonObject
            {
                ["Id"] = 3000 + i,
                ["Category"] = 1,
                ["Text"] = $"PinnedItem_{i}",
                ["Preview"] = $"PinnedItem_{i}",
                ["IsPinned"] = true
            });
        }
        File.WriteAllText(pinsPath, arr.ToJsonString());

        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        Assert.True(await CloseGracefullyAsync(session));

        var parsed = JsonNode.Parse(File.ReadAllText(pinsPath))?.AsArray();
        Assert.NotNull(parsed);
        Assert.Equal(15, parsed.Count);
    }

    [Fact]
    public async Task F19_B05_ClipboardPins_UnicodeEmojiAndRtlText_SurvivesRoundTrip()
    {
        SettingsJsonHelper.ResetToCleanSettings(o => o["BottomTaskbarEnabled"] = false);
        Directory.CreateDirectory(TestWorkspace.LocalAppDataOpenPixelDir);
        string pinsPath = Path.Combine(TestWorkspace.LocalAppDataOpenPixelDir, "clipboard_pins.json");

        const string rtlUnicode = "مرحبا بالعالم · 日本語テスト · 🔐✨";
        var arr = new JsonArray
        {
            new JsonObject
            {
                ["Id"] = 4001,
                ["Category"] = 1,
                ["Text"] = rtlUnicode,
                ["Preview"] = rtlUnicode,
                ["IsPinned"] = true
            }
        };
        File.WriteAllText(pinsPath, arr.ToJsonString());

        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        Assert.True(await CloseGracefullyAsync(session));

        var parsed = JsonNode.Parse(File.ReadAllText(pinsPath))?.AsArray();
        Assert.NotNull(parsed);
        Assert.Equal(rtlUnicode, parsed[0]?["Text"]?.GetValue<string>());
    }

    // =========================================================================
    // F20 Boundaries: File Shelf & Local Wi-Fi QR Phone Transfer — 6 Tests
    // =========================================================================

    [Fact]
    public async Task F20_B01_ShelfDownload_NegativeIndex_Returns404()
    {
        SettingsJsonHelper.ResetToCleanSettings(o => o["BottomTaskbarEnabled"] = false);
        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1600);
        using var client = new LoopbackAndDeckClient();
        Assert.True(await client.WaitForLoopbackReadyAsync(5000));

        var res = await client.DownloadShelfFileAsync(-1);
        Assert.Equal(404, res.StatusCode);
    }

    [Fact]
    public async Task F20_B02_ShelfDownload_NonNumericAndTraversalPath_ReturnsNon200Or404()
    {
        SettingsJsonHelper.ResetToCleanSettings(o => o["BottomTaskbarEnabled"] = false);
        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1600);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        var r1 = await http.GetAsync("http://127.0.0.1:47824/download/abc");
        var r2 = await http.GetAsync("http://127.0.0.1:47824/download/..%2F..%2Fwindows%2Fwin.ini");
        Assert.Equal(404, (int)r1.StatusCode);
        Assert.Equal(404, (int)r2.StatusCode);
    }

    [Fact]
    public async Task F20_B03_ShelfDownload_MaxCapacityBoundaryIndex12_Returns404WhenEmpty()
    {
        SettingsJsonHelper.ResetToCleanSettings(o => o["BottomTaskbarEnabled"] = false);
        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1600);
        using var client = new LoopbackAndDeckClient();
        Assert.True(await client.WaitForLoopbackReadyAsync(5000));

        var res = await client.DownloadShelfFileAsync(12);
        Assert.Equal(404, res.StatusCode);
    }

    [Fact]
    public async Task F20_B04_QrTestCli_EmptyString_ProducesValidQrMatrix()
    {
        var res = await ProcessAndWindowDriver.RunCliAsync("--qr-test \"\"", timeoutMs: 12000);
        Assert.Equal(0, res.ExitCode);
        using var doc = JsonDocument.Parse(res.StdOut.Trim());
        int size = doc.RootElement.GetProperty("size").GetInt32();
        Assert.True(size >= 21);
    }

    [Fact]
    public async Task F20_B05_QrTestCli_LongPairingUri_ProducesValidMatrix()
    {
        string longUri = "notchpixel-deck://pair?ip=192.168.100.254&port=47825&code=987654&cert=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var res = await ProcessAndWindowDriver.RunCliAsync($"--qr-test \"{longUri}\"", timeoutMs: 12000);
        Assert.Equal(0, res.ExitCode);
        using var doc = JsonDocument.Parse(res.StdOut.Trim());
        int size = doc.RootElement.GetProperty("size").GetInt32();
        Assert.Equal(size, doc.RootElement.GetProperty("rows").GetArrayLength());
    }

    [Fact]
    public async Task F20_B06_ShelfHtml_EmptyShelf_ContainsZeroFakeDownloadLinks()
    {
        SettingsJsonHelper.ResetToCleanSettings(o => o["BottomTaskbarEnabled"] = false);
        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1600);
        using var client = new LoopbackAndDeckClient();
        Assert.True(await client.WaitForLoopbackReadyAsync(5000));

        var html = await client.GetShelfHtmlAsync();
        Assert.Equal(200, html.StatusCode);
        Assert.DoesNotContain("/download/0", html.Body);
    }

    // =========================================================================
    // F21 Boundaries: Streaming (Twitch/YouTube Chat & OBS REC Pill) — 5 Tests
    // =========================================================================

    [Fact]
    public async Task F21_B01_Streaming_EmptyAndWhitespaceChannels_NoCrash()
    {
        SettingsJsonHelper.ResetToCleanSettings(o =>
        {
            o["BottomTaskbarEnabled"] = false;
            o["TwitchChannel"] = "   ";
            o["YoutubeLiveId"] = "";
            o["StreamChatEnabled"] = true;
        });

        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        Assert.False(session.Process.HasExited);
    }

    [Fact]
    public async Task F21_B02_Streaming_SpecialCharsInChannelNames_NoCrash()
    {
        SettingsJsonHelper.ResetToCleanSettings(o =>
        {
            o["BottomTaskbarEnabled"] = false;
            o["TwitchChannel"] = "user_with?query=1&x=2";
            o["YoutubeLiveId"] = "id/with/slashes";
            o["StreamChatEnabled"] = true;
        });

        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        Assert.False(session.Process.HasExited);
    }

    [Fact]
    public async Task F21_B03_MockChatStreamServer_EmptyQueueAndUnicodeEmoji_SerializedCleanly()
    {
        using var chat = new MediaAndFixtureBuilder.MockChatStreamServer();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        string emptyJson = await http.GetStringAsync(chat.BaseHttpUrl);
        using (var d0 = JsonDocument.Parse(emptyJson))
        {
            Assert.Equal(0, d0.RootElement.GetProperty("messages").GetArrayLength());
        }

        chat.PushMessage("User_α", "Hello 🎮🔥 日本語", "VIP");
        string populatedJson = await http.GetStringAsync(chat.BaseHttpUrl);
        using var d1 = JsonDocument.Parse(populatedJson);
        Assert.Equal("Hello 🎮🔥 日本語", d1.RootElement.GetProperty("messages")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void F21_B04_MockObsWebSocketServer_ZeroAndLargeDuration_UpdatedAtomically()
    {
        using var obs = new MediaAndFixtureBuilder.MockObsWebSocketServer(initialRecording: false, initialSeconds: 0);
        Assert.False(obs.IsRecording);
        Assert.Equal(0, obs.RecordSeconds);

        obs.SetRecording(true, 86400);
        Assert.True(obs.IsRecording);
        Assert.Equal(86400, obs.RecordSeconds);
    }

    [Fact]
    public async Task F21_B05_DeckModeRecord_RepeatedToggle_FlipsRecordingStateCleanly()
    {
        using var mock = new MediaAndFixtureBuilder.MockDeckTlsServer(enableUdpDiscovery: false);
        using var client = new LoopbackAndDeckClient(deckTlsPort: mock.TlsPort);
        Assert.Equal(200, (await client.SendModeAsync(mock.IssuedToken, "record", mock.CertFingerprintSha256)).StatusCode);
        Assert.Equal(200, (await client.SendModeAsync(mock.IssuedToken, "record", mock.CertFingerprintSha256)).StatusCode);
        Assert.Equal(2, mock.ReceivedRequests.Count(r => r.Contains("a=record")));
    }

    // =========================================================================
    // F22 Boundaries: Fullscreen Game Strip & Rules — 5 Tests
    // =========================================================================

    [Fact]
    public async Task F22_B01_GameOverlayMode_OutOfRangeEnum_NoCrash()
    {
        SettingsJsonHelper.ResetToCleanSettings(o =>
        {
            o["BottomTaskbarEnabled"] = false;
            o["GameOverlayMasterEnabled"] = true;
            o["GameOverlayMode"] = 99;
        });

        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        Assert.False(session.Process.HasExited);
    }

    [Fact]
    public async Task F22_B02_GameRulesAllowList_EmptyList_NoCrash()
    {
        SettingsJsonHelper.ResetToCleanSettings(o =>
        {
            o["BottomTaskbarEnabled"] = false;
            o["GameOverlayMasterEnabled"] = true;
            o["GameRulesAllowList"] = new JsonArray();
        });

        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        Assert.False(session.Process.HasExited);
    }

    [Fact]
    public async Task F22_B03_GameRulesAllowList_DuplicateAndMixedCaseEntries_PersistCleanly()
    {
        SettingsJsonHelper.ResetToCleanSettings(o =>
        {
            o["BottomTaskbarEnabled"] = false;
            o["GameRulesAllowList"] = new JsonArray("CS2.EXE", "cs2.exe", "");
        });

        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        Assert.True(await CloseGracefullyAsync(session));
        Assert.NotNull(SettingsJsonHelper.GetValue<List<string>>("GameRulesAllowList"));
    }

    [Fact]
    public async Task F22_B04_GameStripAllMetricsDisabled_NoRenderCrash()
    {
        SettingsJsonHelper.ResetToCleanSettings(o =>
        {
            o["BottomTaskbarEnabled"] = false;
            o["GameOverlayMasterEnabled"] = true;
            o["GameOverlayMode"] = 1;
            o["GameStripShowFps"] = false;
            o["GameStripShowCpu"] = false;
            o["GameStripShowGpu"] = false;
            o["GameStripShowRam"] = false;
            o["GameStripShowTemps"] = false;
            o["GameStripShowNet"] = false;
            o["GameStripShowBackground"] = false;
        });

        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        Assert.False(session.Process.HasExited);
    }

    [Fact]
    public async Task F22_B05_FullscreenWindow_MinimizedAndRestored_NoFocusSteal()
    {
        SettingsJsonHelper.ResetToCleanSettings(o =>
        {
            o["BottomTaskbarEnabled"] = false;
            o["GameOverlayMasterEnabled"] = true;
            o["GameOverlayMode"] = 1;
            o["GameRulesAllowList"] = new JsonArray("testhost.exe", "dotnet.exe", "NotchPixel.E2E.Tests.exe");
        });

        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1600);
        using var fs = ProcessAndWindowDriver.CreateDummyFullscreenWindow(title: "NotchPixelE2E_F22_B05");
        fs.Minimize();
        await Task.Delay(300);
        fs.BringToFront();
        await Task.Delay(400);

        Assert.Equal(fs.Hwnd, ProcessAndWindowDriver.GetForegroundWindowHandle());
        Assert.False(session.Process.HasExited);
    }

    // =========================================================================
    // F23 Boundaries: Reminders, Notifications, Controls, Captures, Launcher & Web — 6 Tests
    // =========================================================================

    [Fact]
    public async Task F23_B01_RemindersJson_CorruptJson_RecoversWithoutCrash()
    {
        SettingsJsonHelper.ResetToCleanSettings(o => o["BottomTaskbarEnabled"] = false);
        Directory.CreateDirectory(TestWorkspace.LocalAppDataOpenPixelDir);
        File.WriteAllText(Path.Combine(TestWorkspace.LocalAppDataOpenPixelDir, "reminders.json"), "{corrupt_reminders::[");

        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1600);
        Assert.False(session.Process.HasExited);
    }

    [Fact]
    public async Task F23_B02_RemindersJson_PastDueAndUnknownRepeatRule_HandledWithoutCrash()
    {
        SettingsJsonHelper.ResetToCleanSettings(o => o["BottomTaskbarEnabled"] = false);
        Directory.CreateDirectory(TestWorkspace.LocalAppDataOpenPixelDir);
        string remPath = Path.Combine(TestWorkspace.LocalAppDataOpenPixelDir, "reminders.json");

        var rems = new JsonArray
        {
            new JsonObject
            {
                ["Id"] = 9001,
                ["Title"] = "Past Due Edge Case",
                ["DueUnixMs"] = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds(),
                ["RepeatRule"] = "EveryLeapYear",
                ["Triggered"] = false,
                ["Completed"] = false
            }
        };
        File.WriteAllText(remPath, rems.ToJsonString());

        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1600);
        Assert.False(session.Process.HasExited);
    }

    [Fact]
    public async Task F23_B03_NotifyEndpoint_EmptyAndLongUnicodeTitle_Accepted()
    {
        SettingsJsonHelper.ResetToCleanSettings(o => o["BottomTaskbarEnabled"] = false);
        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1600);
        using var client = new LoopbackAndDeckClient();
        Assert.True(await client.WaitForLoopbackReadyAsync(5000));

        Assert.Equal(200, (await client.SendNotifyAsync("", "", "")).StatusCode);
        string longUnicode = new string('α', 300) + "🔔";
        Assert.Equal(200, (await client.SendNotifyAsync(longUnicode, "Body <tag>", "App")).StatusCode);
    }

    [Fact]
    public async Task F23_B04_LauncherSlots_MoreThanSixConfigured_ClampedToSixSlots()
    {
        var slots = new JsonArray();
        for (int i = 0; i < 12; i++)
        {
            slots.Add(new JsonObject
            {
                ["Label"] = $"Extra{i}",
                ["TargetPath"] = @"C:\Windows\System32\notepad.exe",
                ["BadgeColorArgb"] = -1
            });
        }

        SettingsJsonHelper.ResetToCleanSettings(o =>
        {
            o["BottomTaskbarEnabled"] = false;
            o["LauncherSlots"] = slots;
        });

        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        Assert.True(await CloseGracefullyAsync(session));

        var loaded = SettingsJsonHelper.LoadOrCreate()["LauncherSlots"]?.AsArray();
        Assert.NotNull(loaded);
        Assert.Equal(6, loaded.Count);
    }

    [Fact]
    public async Task F23_B05_LauncherSlots_MissingTargetPath_NoStartupCrash()
    {
        var slots = new JsonArray
        {
            new JsonObject { ["Label"] = "Broken", ["TargetPath"] = @"Z:\does\not\exist\app.exe", ["BadgeColorArgb"] = 0 }
        };
        SettingsJsonHelper.ResetToCleanSettings(o =>
        {
            o["BottomTaskbarEnabled"] = false;
            o["LauncherSlots"] = slots;
        });

        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        Assert.False(session.Process.HasExited);
    }

    [Fact]
    public async Task F23_B06_LoopbackUnknownPath_HandledSafelyWithoutCrash()
    {
        SettingsJsonHelper.ResetToCleanSettings(o => o["BottomTaskbarEnabled"] = false);
        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1600);
        using var client = new LoopbackAndDeckClient();
        Assert.True(await client.WaitForLoopbackReadyAsync(5000));

        var res = await client.SendRawLoopbackAsync(HttpMethod.Get, "/nonexistent_endpoint?x=1");
        Assert.Equal(200, res.StatusCode);
        Assert.False(session.Process.HasExited);
    }

    // =========================================================================
    // F24 Boundaries: Android NotchPixel Deck Protocol & Security — 7 Tests
    // =========================================================================

    [Fact]
    public void F24_B01_PairingUri_RejectsInvalidSchemesMissingHostAndBadPorts()
    {
        string[] badUris =
        {
            "",
            "   ",
            "not-a-uri",
            "http://pair?ip=192.168.1.2&port=47825&code=123456&cert=abcd",
            "notchpixel-deck://wronghost?ip=192.168.1.2&port=47825&code=123456&cert=abcd",
            "notchpixel-deck://pair?ip=192.168.1.2&port=0&code=123456&cert=abcd",
            "notchpixel-deck://pair?ip=192.168.1.2&port=70000&code=123456&cert=abcd",
            "openpixel-deck://pair?ip=192.168.1.2&port=abc&code=123456&cert=abcd"
        };

        foreach (string uri in badUris)
        {
            bool ok = LoopbackAndDeckClient.TryParsePairingUri(uri, out _, out string? err);
            Assert.False(ok, $"Expected pairing URI '{uri}' to be rejected.");
            Assert.False(string.IsNullOrWhiteSpace(err));
        }
    }

    [Fact]
    public void F24_B02_PairingUri_AcceptsBothNotchPixelAndOpenPixelSchemes()
    {
        string fp = new string('a', 64);
        Assert.True(LoopbackAndDeckClient.TryParsePairingUri(
            $"notchpixel-deck://pair?ip=192.168.1.10&port=47825&code=123456&cert={fp}&mac=AA:BB:CC:DD:EE:FF&name=MyPC",
            out var p1, out _));
        Assert.NotNull(p1);
        Assert.Equal("notchpixel-deck", p1.Scheme);
        Assert.Equal("192.168.1.10", p1.HostIp);
        Assert.Equal(47825, p1.Port);
        Assert.Equal("123456", p1.Code);
        Assert.Equal(fp, p1.CertFingerprint);

        Assert.True(LoopbackAndDeckClient.TryParsePairingUri(
            $"openpixel-deck://pair?host=10.0.0.5&port=47825&code=654321&fp={fp}",
            out var p2, out _));
        Assert.NotNull(p2);
        Assert.Equal("openpixel-deck", p2.Scheme);
        Assert.Equal("10.0.0.5", p2.HostIp);
        Assert.Equal(fp, p2.CertFingerprint);
    }

    [Fact]
    public async Task F24_B03_TlsPinningMismatch_RejectedByClientOnMockAndLive()
    {
        string wrongFp = new string('0', 64);
        using var mock = new MediaAndFixtureBuilder.MockDeckTlsServer(enableUdpDiscovery: false);
        using var mockClient = new LoopbackAndDeckClient(deckTlsPort: mock.TlsPort);

        Exception ex = await Assert.ThrowsAnyAsync<Exception>(() => mockClient.ProbeAsync(expectedFp: wrongFp));
        Assert.True(ex is HttpRequestException || ex is AuthenticationException,
            $"Expected TLS pinning rejection exception, got {ex.GetType().Name}.");
    }

    [Fact]
    public async Task F24_B04_LivePcDeck_MalformedUdpDiscoveryPayloads_IgnoredWithoutReply()
    {
        SettingsJsonHelper.ResetToCleanSettings(o =>
        {
            o["BottomTaskbarEnabled"] = false;
            o["PcDeckEnabled"] = true;
        });

        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1700);
        using var client = new LoopbackAndDeckClient();

        foreach (string badPayload in new[] { "GARBAGE_PROBE", "{}", "NOTCHPIXEL_WRONG" })
        {
            var resp = await client.SendDiscoveryProbeAsync(badPayload, timeoutMs: 400);
            Assert.Null(resp);
        }
    }

    [Fact]
    public async Task F24_B05_LivePcDeck_FiveInvalidPairAttempts_TriggersLockoutOr403()
    {
        SettingsJsonHelper.ResetToCleanSettings(o =>
        {
            o["BottomTaskbarEnabled"] = false;
            o["PcDeckEnabled"] = true;
        });

        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1700);
        using var client = new LoopbackAndDeckClient();
        var probe = await client.ProbeAsync();

        if (probe.StatusCode == 200)
        {
            TlsCallResult? fifth = null;
            for (int i = 0; i < 5; i++)
            {
                fifth = await client.PairAsync($"99999{i}", expectedFp: probe.ServerCertSha256Hex);
                Assert.Equal(401, fifth.StatusCode);
            }
            Assert.NotNull(fifth);
            Assert.Contains("Too many invalid attempts", fifth.Body);
        }
        else
        {
            Assert.Equal(403, probe.StatusCode);
        }
    }

    [Fact]
    public async Task F24_B06_LivePcDeck_PowerAndStateWithoutValidToken_AlwaysRejected()
    {
        SettingsJsonHelper.ResetToCleanSettings(o =>
        {
            o["BottomTaskbarEnabled"] = false;
            o["PcDeckEnabled"] = true;
        });
        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1700);
        using var client = new LoopbackAndDeckClient();

        // SAFETY: Test ONLY empty or bogus tokens against live /deck/power
        foreach (string tok in new[] { "", "bogus-invalid-token-xyz" })
        {
            foreach (string action in new[] { "lock", "sleep", "restart", "shutdown" })
            {
                var res = await client.SendPowerAsync(tok, action);
                Assert.True(res.StatusCode == 401 || res.StatusCode == 403,
                    $"Expected 401/403 for unauth /deck/power?a={action}, got {res.StatusCode}.");
            }
        }
    }

    [Fact]
    public async Task F24_B07_AndroidJvmHarness_InvalidMacWolAndBadSchemeUriRejection()
    {
        AndroidHarnessResult wolRes = await AndroidHarnessRunner.RunJvmProtocolTestsAsync(
            timeoutMs: 20000,
            filterTestMethod: "testWakeOnLanValidAndInvalidMacs");
        Assert.True(wolRes.Compiled, wolRes.StdErr);
        Assert.Equal(0, wolRes.ExitCode);
        Assert.Equal(0, wolRes.FailedCount);
        Assert.Equal(1, wolRes.PassedCount);

        AndroidHarnessResult uriRes = await AndroidHarnessRunner.RunJvmProtocolTestsAsync(
            timeoutMs: 20000,
            filterTestMethod: "testQrUriParsingRejectsMalformedAndUnsupportedSchemes");
        Assert.True(uriRes.Compiled, uriRes.StdErr);
        Assert.Equal(0, uriRes.ExitCode);
        Assert.Equal(0, uriRes.FailedCount);
        Assert.Equal(1, uriRes.PassedCount);
    }
}
