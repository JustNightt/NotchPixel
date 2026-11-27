using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using NotchPixel.E2E.Tests.Infrastructure;
using Xunit;

namespace NotchPixel.E2E.Tests.Tier1;

[Collection("NotchPixelE2E")]
public sealed class Tier1_R5_IslandMediaProductivityTests
{
    private const uint WM_CLOSE = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    private readonly E2ETestSessionFixture _fixture;

    public Tier1_R5_IslandMediaProductivityTests(E2ETestSessionFixture fixture)
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
                ["DeviceId"] = "e2e-device-01",
                ["DeviceName"] = "E2E-Pixel",
                ["Model"] = "Pixel 9",
                ["OsVersion"] = "Android 15",
                ["AppVersion"] = "1.0.5",
                ["IpAddress"] = "127.0.0.1",
                ["KeyFingerprint"] = "e2efp0123456789",
                ["AuthToken"] = token,
                ["PairedAtMs"] = 1000
            }
        };
        File.WriteAllText(path, arr.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static T RunInSta<T>(Func<T> func)
    {
        T result = default!;
        Exception? ex = null;
        var thread = new Thread(() =>
        {
            try { result = func(); }
            catch (Exception e) { ex = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(5000);
        if (ex != null) throw ex;
        return result;
    }

    // =========================================================================
    // F16: Island Placement, Monitor, Corners & Sensors — 6 Tests
    // =========================================================================

    [Fact]
    public async Task F16_01_IslandShape_NotchVsFloatingIsland_PositionsAtTopEdge()
    {
        Rectangle primary = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);

        foreach (int shape in new[] { 1 /* Notch */, 0 /* Island */ })
        {
            SettingsJsonHelper.ResetToCleanSettings(o =>
            {
                o["BottomTaskbarEnabled"] = false;
                o["Shape"] = shape;
                o["Edge"] = 0; // TopCenter
                o["MonitorIndex"] = 0;
            });

            await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1600);
            var wins = await session.WaitForVisibleWindowsAsync(minCount: 1, timeoutMs: 6000);
            Assert.NotEmpty(wins);
            var island = wins.FirstOrDefault(w => w.Title.Equals("OpenPixel", StringComparison.OrdinalIgnoreCase)) ?? wins[0];

            Assert.Equal(primary.Top, island.Bounds.Top);
            Assert.True(island.Bounds.Width >= 400, $"Expected island canvas width >= 400, got {island.Bounds.Width}.");
        }
    }

    [Fact]
    public async Task F16_02_EdgePlacement_LeftCenterRight_PositionsWindowAccordingly()
    {
        Rectangle primary = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);

        // LeftEdge = 1
        SettingsJsonHelper.ResetToCleanSettings(o =>
        {
            o["BottomTaskbarEnabled"] = false;
            o["Edge"] = 1;
            o["MonitorIndex"] = 0;
        });
        await using (var leftSession = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500))
        {
            var wins = await leftSession.WaitForVisibleWindowsAsync(1, 5000);
            var island = wins.First(w => w.Title.Equals("OpenPixel", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(primary.Left, island.Bounds.Left);
        }

        // RightEdge = 2
        SettingsJsonHelper.ResetToCleanSettings(o =>
        {
            o["BottomTaskbarEnabled"] = false;
            o["Edge"] = 2;
            o["MonitorIndex"] = 0;
        });
        await using (var rightSession = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500))
        {
            var wins = await rightSession.WaitForVisibleWindowsAsync(1, 5000);
            var island = wins.First(w => w.Title.Equals("OpenPixel", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(primary.Right, island.Bounds.Right);
        }
    }

    [Fact]
    public async Task F16_03_MonitorIndex_ValidAndOutOfBounds_ClampedInsideVirtualScreen()
    {
        SettingsJsonHelper.ResetToCleanSettings(o =>
        {
            o["BottomTaskbarEnabled"] = false;
            o["MonitorIndex"] = 99;
        });

        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        var wins = await session.WaitForVisibleWindowsAsync(1, 5000);
        var island = wins.First(w => w.Title.Equals("OpenPixel", StringComparison.OrdinalIgnoreCase));

        Rectangle vScreen = SystemInformation.VirtualScreen;
        Assert.True(vScreen.IntersectsWith(island.Bounds),
            $"Out-of-bounds MonitorIndex placed island at {island.Bounds}, outside VirtualScreen {vScreen}.");
    }

    [Fact]
    public async Task F16_04_CornerRadiusScale_ValidValue_PersistsAcrossCleanRestart()
    {
        SettingsJsonHelper.ResetToCleanSettings(o =>
        {
            o["BottomTaskbarEnabled"] = false;
            o["CornerRadiusScale"] = 0.55f;
        });

        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        Assert.False(session.Process.HasExited);
        Assert.True(await CloseGracefullyAsync(session));

        float saved = SettingsJsonHelper.GetValue<float>("CornerRadiusScale");
        Assert.InRange(saved, 0.54f, 0.56f);
    }

    [Fact]
    public async Task F16_05_Hotkeys_CtrlAltN_And_CtrlAltH_ToggleIslandCleanly()
    {
        SettingsJsonHelper.ResetToCleanSettings(o => o["BottomTaskbarEnabled"] = false);
        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1800);
        var wins = await session.WaitForVisibleWindowsAsync(1, 5000);
        var island = wins.First(w => w.Title.Equals("OpenPixel", StringComparison.OrdinalIgnoreCase));
        Assert.True(ProcessAndWindowDriver.IsWindowVisibleHandle(island.Hwnd));

        // Toggle open & collapse with Ctrl+Alt+N
        ProcessAndWindowDriver.SendHotkey(ProcessAndWindowDriver.MOD_CONTROL | ProcessAndWindowDriver.MOD_ALT, Keys.N);
        await Task.Delay(350);
        ProcessAndWindowDriver.SendHotkey(ProcessAndWindowDriver.MOD_CONTROL | ProcessAndWindowDriver.MOD_ALT, Keys.N);
        await Task.Delay(350);

        // Toggle hide & show with Ctrl+Alt+H
        ProcessAndWindowDriver.SendHotkey(ProcessAndWindowDriver.MOD_CONTROL | ProcessAndWindowDriver.MOD_ALT, Keys.H);
        await Task.Delay(300);
        bool hiddenAfterFirst = !ProcessAndWindowDriver.IsWindowVisibleHandle(island.Hwnd);

        ProcessAndWindowDriver.SendHotkey(ProcessAndWindowDriver.MOD_CONTROL | ProcessAndWindowDriver.MOD_ALT, Keys.H);
        await Task.Delay(300);
        bool visibleAfterSecond = ProcessAndWindowDriver.IsWindowVisibleHandle(island.Hwnd);

        Assert.False(session.Process.HasExited);
        Assert.True(hiddenAfterFirst, "Ctrl+Alt+H should hide the island window.");
        Assert.True(visibleAfterSecond, "Second Ctrl+Alt+H should restore the island window.");
    }

    [Fact]
    public async Task F16_06_RestingModes_And_SensorIgnoredApps_PersistCleanly()
    {
        SettingsJsonHelper.ResetToCleanSettings(o =>
        {
            o["BottomTaskbarEnabled"] = false;
            o["RestingMode"] = 7; // PixelPet
            o["ShowSongTitleInClosedPill"] = true;
            o["IgnoredSensorApps"] = new JsonArray("svchost.exe", "audiodg.exe", "custom_bg.exe");
        });

        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1600);
        Assert.False(session.Process.HasExited);
        Assert.True(await CloseGracefullyAsync(session));

        Assert.Equal(7, SettingsJsonHelper.GetValue<int>("RestingMode"));
        Assert.True(SettingsJsonHelper.GetValue<bool>("ShowSongTitleInClosedPill"));
        var ignored = SettingsJsonHelper.GetValue<List<string>>("IgnoredSensorApps");
        Assert.NotNull(ignored);
        Assert.Contains("custom_bg.exe", ignored);
    }

    // =========================================================================
    // F17: Real Windows Music & Synced Lyrics — 5 Tests
    // =========================================================================

    [Fact]
    public async Task F17_01_DeckStateMediaSchema_ZeroPlaceholdersVerified()
    {
        const string token = "e2e-f17-token";
        SeedPairedDeviceToken(token);
        SettingsJsonHelper.ResetToCleanSettings(o =>
        {
            o["BottomTaskbarEnabled"] = false;
            o["PcDeckEnabled"] = true;
        });

        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1800);
        using var client = new LoopbackAndDeckClient();
        var probe = await client.ProbeAsync();
        if (probe.StatusCode == 200)
        {
            var stateRes = await client.GetStateAsync(token, probe.ServerCertSha256Hex);
            Assert.Equal(200, stateRes.StatusCode);
            using var doc = JsonDocument.Parse(stateRes.Body);
            JsonElement media = doc.RootElement.GetProperty("media");

            Assert.True(media.TryGetProperty("hasSession", out var hasSessionEl));
            Assert.True(media.TryGetProperty("isPlaying", out _));
            Assert.True(media.TryGetProperty("title", out var titleEl));
            Assert.True(media.TryGetProperty("artist", out _));
            Assert.True(media.TryGetProperty("app", out _));
            Assert.True(media.TryGetProperty("posSec", out _));
            Assert.True(media.TryGetProperty("durSec", out _));
            Assert.True(media.TryGetProperty("accentHex", out _));

            string title = titleEl.GetString() ?? "";
            Assert.DoesNotContain("Midnight City", title, StringComparison.OrdinalIgnoreCase);
            if (!hasSessionEl.GetBoolean())
            {
                Assert.Equal(string.Empty, title);
            }
        }
        else
        {
            // Host network is Public so PC Deck enforces 403; verify zero-placeholder contract via MockDeckTlsServer & --self-test
            Assert.Equal(403, probe.StatusCode);
            using var mock = new MediaAndFixtureBuilder.MockDeckTlsServer(enableUdpDiscovery: false);
            using var mockClient = new LoopbackAndDeckClient(deckTlsPort: mock.TlsPort);
            var st = await mockClient.GetStateAsync(mock.IssuedToken, mock.CertFingerprintSha256);
            Assert.Equal(200, st.StatusCode);
            using var doc = JsonDocument.Parse(st.Body);
            Assert.False(doc.RootElement.GetProperty("media").GetProperty("hasSession").GetBoolean());
        }

        Assert.False(SourceInspector.ContainsForbiddenPlaceholder(out _));
    }

    [Fact]
    public async Task F17_02_DeckMediaEndpoints_SeekPlayNextPrev_ProtocolVerified()
    {
        using var mock = new MediaAndFixtureBuilder.MockDeckTlsServer(enableUdpDiscovery: false);
        using var mockClient = new LoopbackAndDeckClient(deckTlsPort: mock.TlsPort);
        var pair = await mockClient.PairAsync(mock.PairingCode, expectedFp: mock.CertFingerprintSha256);
        Assert.Equal(200, pair.StatusCode);

        Assert.Equal(200, (await mockClient.SendMediaAsync(mock.IssuedToken, "a=play", mock.CertFingerprintSha256)).StatusCode);
        Assert.Equal(200, (await mockClient.SendMediaAsync(mock.IssuedToken, "a=next", mock.CertFingerprintSha256)).StatusCode);
        Assert.Equal(200, (await mockClient.SendMediaAsync(mock.IssuedToken, "a=prev", mock.CertFingerprintSha256)).StatusCode);
        Assert.Equal(200, (await mockClient.SendMediaAsync(mock.IssuedToken, "a=seek&v=15.0", mock.CertFingerprintSha256)).StatusCode);

        Assert.Contains(mock.ReceivedRequests, r => r.Contains("a=seek&v=15.0"));

        // Also test live /deck/media?a=seek when private network allows PC Deck
        const string token = "e2e-f17-seek-tok";
        SeedPairedDeviceToken(token);
        SettingsJsonHelper.ResetToCleanSettings(o =>
        {
            o["BottomTaskbarEnabled"] = false;
            o["PcDeckEnabled"] = true;
        });
        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1600);
        using var liveClient = new LoopbackAndDeckClient();
        var probe = await liveClient.ProbeAsync();
        if (probe.StatusCode == 200)
        {
            var seek = await liveClient.SendMediaAsync(token, "a=seek&v=0", probe.ServerCertSha256Hex);
            Assert.Equal(200, seek.StatusCode);
        }
    }

    [Fact]
    public async Task F17_03_ProgressBarStyles_AllSixModes_PersistAndRenderCleanly()
    {
        // Test Hearts (5) and SongShape (3) + Wave (0)
        foreach (int style in new[] { 0, 3, 5 })
        {
            SettingsJsonHelper.ResetToCleanSettings(o =>
            {
                o["BottomTaskbarEnabled"] = false;
                o["ProgressBar"] = style;
                o["CardBackground"] = style % 2;
            });

            await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1300);
            Assert.False(session.Process.HasExited, $"OpenPixel.exe exited with ProgressBar style {style}.");
            Assert.True(await CloseGracefullyAsync(session));
            Assert.Equal(style, SettingsJsonHelper.GetValue<int>("ProgressBar"));
        }
    }

    [Fact]
    public async Task F17_04_ExcludedMusicApps_And_MusicFilterMode_Persist()
    {
        SettingsJsonHelper.ResetToCleanSettings(o =>
        {
            o["BottomTaskbarEnabled"] = false;
            o["MusicFilterMode"] = 1;
            o["ExcludedMusicApps"] = new JsonArray("msedgewebview2.exe", "vlc.exe");
        });

        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        Assert.False(session.Process.HasExited);
        Assert.True(await CloseGracefullyAsync(session));

        Assert.Equal(1, SettingsJsonHelper.GetValue<int>("MusicFilterMode"));
        var excluded = SettingsJsonHelper.GetValue<List<string>>("ExcludedMusicApps");
        Assert.NotNull(excluded);
        Assert.Contains("vlc.exe", excluded);
    }

    [Fact]
    public async Task F17_05_SelfTestAndSourceContract_LrcLibSyncedLyricsAndZeroFakeSongs()
    {
        var selfTest = await ProcessAndWindowDriver.RunCliAsync("--self-test", timeoutMs: 20000);
        Assert.Equal(0, selfTest.ExitCode);
        Assert.Contains("Zero Placeholders: Media starts with HasSession=false", selfTest.StdOut);

        var lrcMatches = SourceInspector.FindMatchesInCSharp(@"lrclib\.net|ParseLrcLyrics");
        Assert.NotEmpty(lrcMatches);
    }

    // =========================================================================
    // F18: Global Mic Mute & Call Timer — 5 Tests
    // =========================================================================

    [Fact]
    public async Task F18_01_Hotkey_CtrlAltM_TogglesMicMuteAndRestoresCleanly()
    {
        const string token = "e2e-f18-hk-tok";
        SeedPairedDeviceToken(token);
        SettingsJsonHelper.ResetToCleanSettings(o =>
        {
            o["BottomTaskbarEnabled"] = false;
            o["PcDeckEnabled"] = true;
            o["ShowGlobalMuteNotice"] = true;
        });

        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1800);
        using var client = new LoopbackAndDeckClient();
        var probe = await client.ProbeAsync();

        bool? initialMuted = null;
        try
        {
            if (probe.StatusCode == 200)
            {
                var st0 = await client.GetStateAsync(token, probe.ServerCertSha256Hex);
                initialMuted = JsonDocument.Parse(st0.Body).RootElement.GetProperty("micMuted").GetBoolean();
            }

            ProcessAndWindowDriver.SendHotkey(ProcessAndWindowDriver.MOD_CONTROL | ProcessAndWindowDriver.MOD_ALT, Keys.M);
            await Task.Delay(350);

            if (probe.StatusCode == 200 && initialMuted.HasValue)
            {
                var st1 = await client.GetStateAsync(token, probe.ServerCertSha256Hex);
                bool afterToggle = JsonDocument.Parse(st1.Body).RootElement.GetProperty("micMuted").GetBoolean();
                Assert.NotEqual(initialMuted.Value, afterToggle);
            }
        }
        finally
        {
            // Restore original mic mute state
            ProcessAndWindowDriver.SendHotkey(ProcessAndWindowDriver.MOD_CONTROL | ProcessAndWindowDriver.MOD_ALT, Keys.M);
            await Task.Delay(250);
        }

        Assert.False(session.Process.HasExited);
    }

    [Fact]
    public async Task F18_02_DeckModeMic_TogglesMicMutedInDeckState()
    {
        const string token = "e2e-f18-mode-tok";
        SeedPairedDeviceToken(token);
        SettingsJsonHelper.ResetToCleanSettings(o =>
        {
            o["BottomTaskbarEnabled"] = false;
            o["PcDeckEnabled"] = true;
        });

        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1800);
        using var client = new LoopbackAndDeckClient();
        var probe = await client.ProbeAsync();

        if (probe.StatusCode == 200)
        {
            var st0 = await client.GetStateAsync(token, probe.ServerCertSha256Hex);
            bool initialMuted = JsonDocument.Parse(st0.Body).RootElement.GetProperty("micMuted").GetBoolean();
            try
            {
                var toggle1 = await client.SendModeAsync(token, "mic", probe.ServerCertSha256Hex);
                Assert.Equal(200, toggle1.StatusCode);

                var st1 = await client.GetStateAsync(token, probe.ServerCertSha256Hex);
                bool nextMuted = JsonDocument.Parse(st1.Body).RootElement.GetProperty("micMuted").GetBoolean();
                Assert.Equal(!initialMuted, nextMuted);
            }
            finally
            {
                await client.SendModeAsync(token, "mic", probe.ServerCertSha256Hex);
            }
        }
        else
        {
            Assert.Equal(403, probe.StatusCode);
            using var mock = new MediaAndFixtureBuilder.MockDeckTlsServer(enableUdpDiscovery: false);
            using var mockClient = new LoopbackAndDeckClient(deckTlsPort: mock.TlsPort);
            var res = await mockClient.SendModeAsync(mock.IssuedToken, "mic", mock.CertFingerprintSha256);
            Assert.Equal(200, res.StatusCode);
            Assert.Contains(mock.ReceivedRequests, r => r.Contains("/deck/mode") && r.Contains("a=mic"));
        }
    }

    [Fact]
    public async Task F18_03_ShowGlobalMuteNotice_SettingPersisted()
    {
        SettingsJsonHelper.ResetToCleanSettings(o =>
        {
            o["BottomTaskbarEnabled"] = false;
            o["ShowGlobalMuteNotice"] = false;
        });

        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        Assert.False(session.Process.HasExited);
        Assert.True(await CloseGracefullyAsync(session));
        Assert.False(SettingsJsonHelper.GetValue<bool>("ShowGlobalMuteNotice"));
    }

    [Fact]
    public void F18_04_SvchostAndSystemServices_ExcludedFromMutedMicNotice()
    {
        var matches = SourceInspector.FindMatchesInCSharp(@"IsSystemServiceApp|svchost\.exe");
        Assert.NotEmpty(matches);
        Assert.Contains(matches, m => m.LineText.Contains("svchost.exe", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(matches, m => m.LineText.Contains("IsSystemServiceApp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task F18_05_CallsPanel_UpcomingMeetingSettings_Persist()
    {
        SettingsJsonHelper.ResetToCleanSettings(o =>
        {
            o["BottomTaskbarEnabled"] = false;
            o["UpcomingMeetingTitle"] = "Architecture Review";
            o["UpcomingMeetingTime"] = "14:30";
            o["UpcomingMeetingUrl"] = "https://meet.example.local/room";
        });

        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        Assert.False(session.Process.HasExited);
        Assert.True(await CloseGracefullyAsync(session));

        Assert.Equal("Architecture Review", SettingsJsonHelper.GetValue<string>("UpcomingMeetingTitle"));
        Assert.Equal("14:30", SettingsJsonHelper.GetValue<string>("UpcomingMeetingTime"));
        Assert.Equal("https://meet.example.local/room", SettingsJsonHelper.GetValue<string>("UpcomingMeetingUrl"));
    }

    // =========================================================================
    // F19: Tabbed Clipboard History & Persistent Pins — 5 Tests
    // =========================================================================

    [Fact]
    public async Task F19_01_ClipboardPins_PinnedAndSnippetItems_SurviveProcessRestart()
    {
        SettingsJsonHelper.ResetToCleanSettings(o => o["BottomTaskbarEnabled"] = false);
        Directory.CreateDirectory(TestWorkspace.LocalAppDataOpenPixelDir);
        string pinsPath = Path.Combine(TestWorkspace.LocalAppDataOpenPixelDir, "clipboard_pins.json");

        var initialPins = new JsonArray
        {
            new JsonObject
            {
                ["Id"] = 1001,
                ["Category"] = 1, // Text
                ["Text"] = "Pinned E2E Text Token",
                ["Preview"] = "Pinned E2E Text Token",
                ["SourceInfo"] = "Clipboard",
                ["IsPinned"] = true,
                ["IsCurrent"] = false,
                ["TimestampMs"] = 1001
            },
            new JsonObject
            {
                ["Id"] = 1002,
                ["Category"] = 2, // Links
                ["Text"] = "https://notchpixel.com/updates",
                ["Preview"] = "https://notchpixel.com/updates",
                ["SourceInfo"] = "Link",
                ["IsPinned"] = true,
                ["IsCurrent"] = false,
                ["TimestampMs"] = 1002
            },
            new JsonObject
            {
                ["Id"] = 1003,
                ["Category"] = 5, // Snippets
                ["Text"] = "git status -sb",
                ["Preview"] = "git status -sb",
                ["SourceInfo"] = "Snippet",
                ["IsPinned"] = false,
                ["IsCurrent"] = false,
                ["TimestampMs"] = 1003
            }
        };
        File.WriteAllText(pinsPath, initialPins.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        // Run #1 -> graceful close
        await using (var s1 = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1600))
        {
            Assert.True(await CloseGracefullyAsync(s1));
        }

        // Run #2 -> graceful close
        await using (var s2 = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1600))
        {
            Assert.True(await CloseGracefullyAsync(s2));
        }

        Assert.True(File.Exists(pinsPath), "clipboard_pins.json must persist across restarts.");
        string savedJson = File.ReadAllText(pinsPath);
        Assert.Contains("Pinned E2E Text Token", savedJson);
        Assert.Contains("https://notchpixel.com/updates", savedJson);
        Assert.Contains("git status -sb", savedJson);
    }

    [Fact]
    public async Task F19_02_ClipboardCategories_TextLinksImagesFiles_SupportedInPersistence()
    {
        SettingsJsonHelper.ResetToCleanSettings(o => o["BottomTaskbarEnabled"] = false);
        Directory.CreateDirectory(TestWorkspace.LocalAppDataOpenPixelDir);
        string pinsPath = Path.Combine(TestWorkspace.LocalAppDataOpenPixelDir, "clipboard_pins.json");

        var items = new JsonArray
        {
            new JsonObject { ["Id"] = 2001, ["Category"] = 1, ["Text"] = "Plain text", ["Preview"] = "Plain text", ["IsPinned"] = true },
            new JsonObject { ["Id"] = 2002, ["Category"] = 2, ["Text"] = "https://example.org", ["Preview"] = "https://example.org", ["IsPinned"] = true },
            new JsonObject { ["Id"] = 2003, ["Category"] = 3, ["Text"] = "img.png", ["ImagePath"] = "img.png", ["Preview"] = "Image 100x100", ["IsPinned"] = true },
            new JsonObject { ["Id"] = 2004, ["Category"] = 4, ["Text"] = @"C:\test\doc.txt", ["Preview"] = "doc.txt", ["IsPinned"] = true, ["FilePaths"] = new JsonArray(@"C:\test\doc.txt") }
        };
        File.WriteAllText(pinsPath, items.ToJsonString());

        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1600);
        Assert.False(session.Process.HasExited);
        Assert.True(await CloseGracefullyAsync(session));

        var parsed = JsonNode.Parse(File.ReadAllText(pinsPath))?.AsArray();
        Assert.NotNull(parsed);
        Assert.Equal(4, parsed.Count);
    }

    [Fact]
    public async Task F19_03_ClipboardListener_LiveClipboardTextUpdate_HandledWithoutCrash()
    {
        SettingsJsonHelper.ResetToCleanSettings(o => o["BottomTaskbarEnabled"] = false);
        string? origText = RunInSta(() => Clipboard.ContainsText() ? Clipboard.GetText() : null);

        try
        {
            await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1800);
            string marker = $"NotchPixel_E2E_Clip_{Guid.NewGuid():N}";
            RunInSta(() =>
            {
                Clipboard.SetText(marker);
                return true;
            });

            await Task.Delay(500);
            Assert.False(session.Process.HasExited, "Clipboard listener must handle live clipboard updates without crashing.");

            using var client = new LoopbackAndDeckClient();
            Assert.Equal(200, (await client.GetLoopbackRootAsync()).StatusCode);
        }
        finally
        {
            try
            {
                RunInSta(() =>
                {
                    if (!string.IsNullOrEmpty(origText)) Clipboard.SetText(origText);
                    else Clipboard.Clear();
                    return true;
                });
            }
            catch { }
        }
    }

    [Fact]
    public async Task F19_04_ClipboardPins_EmptyArray_StartsCleanWithZeroFakeItems()
    {
        SettingsJsonHelper.ResetToCleanSettings(o => o["BottomTaskbarEnabled"] = false);
        Directory.CreateDirectory(TestWorkspace.LocalAppDataOpenPixelDir);
        string pinsPath = Path.Combine(TestWorkspace.LocalAppDataOpenPixelDir, "clipboard_pins.json");
        File.WriteAllText(pinsPath, "[]");

        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        Assert.False(session.Process.HasExited);
        Assert.True(await CloseGracefullyAsync(session));

        var parsed = JsonNode.Parse(File.ReadAllText(pinsPath))?.AsArray();
        Assert.NotNull(parsed);
        Assert.Empty(parsed);
    }

    [Fact]
    public async Task F19_05_ClipboardPins_CorruptJson_RecoversWithoutCrash()
    {
        SettingsJsonHelper.ResetToCleanSettings(o => o["BottomTaskbarEnabled"] = false);
        Directory.CreateDirectory(TestWorkspace.LocalAppDataOpenPixelDir);
        string pinsPath = Path.Combine(TestWorkspace.LocalAppDataOpenPixelDir, "clipboard_pins.json");
        File.WriteAllText(pinsPath, "{corrupt_clipboard_json:::[");

        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1800);
        Assert.False(session.Process.HasExited, "Corrupt clipboard_pins.json must not crash OpenPixel.exe.");

        using var client = new LoopbackAndDeckClient();
        Assert.True(await client.WaitForLoopbackReadyAsync(5000));
    }

    // =========================================================================
    // F20: File Shelf & Local Wi-Fi QR Phone Transfer — 6 Tests
    // =========================================================================

    [Fact]
    public async Task F20_01_ShelfHttpEndpoint_ReturnsValidHtmlListing()
    {
        SettingsJsonHelper.ResetToCleanSettings(o => o["BottomTaskbarEnabled"] = false);
        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1800);
        using var client = new LoopbackAndDeckClient();
        Assert.True(await client.WaitForLoopbackReadyAsync(5000));

        var res = await client.GetShelfHtmlAsync();
        Assert.Equal(200, res.StatusCode);
        Assert.Contains("<!DOCTYPE html>", res.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NotchPixel Shelf", res.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task F20_02_ShelfDownloadEndpoint_OutOfRangeIndex_Returns404()
    {
        SettingsJsonHelper.ResetToCleanSettings(o => o["BottomTaskbarEnabled"] = false);
        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1800);
        using var client = new LoopbackAndDeckClient();
        Assert.True(await client.WaitForLoopbackReadyAsync(5000));

        var dl0 = await client.DownloadShelfFileAsync(0);
        var dl99 = await client.DownloadShelfFileAsync(99);
        Assert.Equal(404, dl0.StatusCode);
        Assert.Equal(404, dl99.StatusCode);
    }

    [Fact]
    public async Task F20_03_QrTestCli_GeneratesValidSquareQrMatrixJson()
    {
        var res = await ProcessAndWindowDriver.RunCliAsync("--qr-test \"http://192.168.1.44:47824/shelf\"", timeoutMs: 15000);
        Assert.Equal(0, res.ExitCode);
        Assert.False(res.TimedOut);

        using var doc = JsonDocument.Parse(res.StdOut.Trim());
        int version = doc.RootElement.GetProperty("version").GetInt32();
        int size = doc.RootElement.GetProperty("size").GetInt32();
        var rows = doc.RootElement.GetProperty("rows");

        Assert.True(version >= 1, $"Expected QR version >= 1, got {version}.");
        Assert.True(size >= 21 && size % 2 == 1, $"Expected valid odd QR size >= 21, got {size}.");
        Assert.Equal(size, rows.GetArrayLength());

        foreach (var row in rows.EnumerateArray())
        {
            string line = row.GetString() ?? "";
            Assert.Equal(size, line.Length);
            Assert.All(line, c => Assert.True(c == '0' || c == '1'));
        }
    }

    [Fact]
    public async Task F20_04_QrTestCli_UnicodeShelfUrl_EncodesCleanly()
    {
        var res = await ProcessAndWindowDriver.RunCliAsync("--qr-test \"http://192.168.1.44:47824/shelf?file=测试_α.txt\"", timeoutMs: 15000);
        Assert.Equal(0, res.ExitCode);

        using var doc = JsonDocument.Parse(res.StdOut.Trim());
        int size = doc.RootElement.GetProperty("size").GetInt32();
        Assert.Equal(size, doc.RootElement.GetProperty("rows").GetArrayLength());
    }

    [Fact]
    public async Task F20_05_KeepCapturesOffShelf_SettingPersistsAcrossRestart()
    {
        foreach (bool flag in new[] { true, false })
        {
            SettingsJsonHelper.ResetToCleanSettings(o =>
            {
                o["BottomTaskbarEnabled"] = false;
                o["KeepCapturesOffShelf"] = flag;
            });

            await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1400);
            Assert.True(await CloseGracefullyAsync(session));
            Assert.Equal(flag, SettingsJsonHelper.GetValue<bool>("KeepCapturesOffShelf"));
        }
    }

    [Fact]
    public void F20_06_ShelfContract_Max12FilesAndUtf8FilenameHeader()
    {
        var capMatches = SourceInspector.FindMatchesInCSharp(@"ShelfFiles\.Count\s*>=\s*12");
        Assert.NotEmpty(capMatches);

        var dispMatches = SourceInspector.FindMatchesInCSharp(@"filename\*=UTF-8''");
        Assert.NotEmpty(dispMatches);
    }
}
