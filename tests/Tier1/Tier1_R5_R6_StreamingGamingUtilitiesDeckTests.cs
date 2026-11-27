using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using NotchPixel.E2E.Tests.Infrastructure;
using Xunit;

namespace NotchPixel.E2E.Tests.Tier1;

[Collection("NotchPixelE2E")]
public sealed class Tier1_R5_R6_StreamingGamingUtilitiesDeckTests
{
    private const uint WM_CLOSE = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    private readonly E2ETestSessionFixture _fixture;

    public Tier1_R5_R6_StreamingGamingUtilitiesDeckTests(E2ETestSessionFixture fixture)
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
                ["DeviceId"] = "e2e-device-f21",
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

    private static async Task<string> ReceiveWsTextAsync(ClientWebSocket ws, CancellationToken ct)
    {
        byte[] buf = new byte[8192];
        var ms = new MemoryStream();
        while (true)
        {
            var res = await ws.ReceiveAsync(buf, ct);
            ms.Write(buf, 0, res.Count);
            if (res.EndOfMessage) break;
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static async Task SendWsTextAsync(ClientWebSocket ws, string text, CancellationToken ct)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    // =========================================================================
    // F21: Streaming: Twitch/YouTube Chat & OBS REC Pill — 5 Tests
    // =========================================================================

    [Fact]
    public async Task F21_01_MockObsWebSocketServer_V5HandshakeAndGetRecordStatus()
    {
        using var obs = new MediaAndFixtureBuilder.MockObsWebSocketServer(initialRecording: true, initialSeconds: 125);
        using var ws = new ClientWebSocket();
        using var cts = new CancellationTokenSource(5000);

        await ws.ConnectAsync(obs.WebSocketUri, cts.Token);
        string hello = await ReceiveWsTextAsync(ws, cts.Token);
        using (var helloDoc = JsonDocument.Parse(hello))
        {
            Assert.Equal(0, helloDoc.RootElement.GetProperty("op").GetInt32());
        }

        await SendWsTextAsync(ws, "{\"op\":1,\"d\":{\"rpcVersion\":1}}", cts.Token);
        string identified = await ReceiveWsTextAsync(ws, cts.Token);
        using (var idDoc = JsonDocument.Parse(identified))
        {
            Assert.Equal(2, idDoc.RootElement.GetProperty("op").GetInt32());
        }

        await SendWsTextAsync(ws, "{\"op\":6,\"d\":{\"requestType\":\"GetRecordStatus\",\"requestId\":\"e2e-1\"}}", cts.Token);
        string statusResp = await ReceiveWsTextAsync(ws, cts.Token);
        using var statusDoc = JsonDocument.Parse(statusResp);
        Assert.Equal(7, statusDoc.RootElement.GetProperty("op").GetInt32());
        JsonElement respData = statusDoc.RootElement.GetProperty("d").GetProperty("responseData");
        Assert.True(respData.GetProperty("outputActive").GetBoolean());
        Assert.Equal(125000L, respData.GetProperty("outputDuration").GetInt64());
    }

    [Fact]
    public async Task F21_02_MockChatStreamServer_DeliversLiveMessages()
    {
        using var chat = new MediaAndFixtureBuilder.MockChatStreamServer();
        chat.PushMessage("StreamerMod", "Welcome to the stream!", "MOD");
        chat.PushMessage("Viewer42", "Nice liquid glass UI!", "SUB");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        string json = await http.GetStringAsync(chat.BaseHttpUrl + "/chat");

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("live").GetBoolean());
        var messages = doc.RootElement.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("StreamerMod", messages[0].GetProperty("user").GetString());
        Assert.Equal("Nice liquid glass UI!", messages[1].GetProperty("text").GetString());
    }

    [Fact]
    public async Task F21_03_StreamingSettings_TwitchYoutubeObsPill_PersistAcrossRestart()
    {
        SettingsJsonHelper.ResetToCleanSettings(o =>
        {
            o["BottomTaskbarEnabled"] = false;
            o["TwitchChannel"] = "notchpixel_live";
            o["YoutubeLiveId"] = "yt_live_abc123";
            o["StreamChatEnabled"] = true;
            o["ShowIslandChatPopups"] = true;
            o["ShowObsRecPill"] = true;
        });

        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        Assert.False(session.Process.HasExited);
        Assert.True(await CloseGracefullyAsync(session));

        Assert.Equal("notchpixel_live", SettingsJsonHelper.GetValue<string>("TwitchChannel"));
        Assert.Equal("yt_live_abc123", SettingsJsonHelper.GetValue<string>("YoutubeLiveId"));
        Assert.True(SettingsJsonHelper.GetValue<bool>("StreamChatEnabled"));
        Assert.True(SettingsJsonHelper.GetValue<bool>("ShowIslandChatPopups"));
        Assert.True(SettingsJsonHelper.GetValue<bool>("ShowObsRecPill"));
    }

    [Fact]
    public async Task F21_04_DeckStateRecordingFields_ReflectIdleAndRecordToggle()
    {
        const string token = "e2e-f21-rec-tok";
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
            Assert.Equal(200, st0.StatusCode);
            using (var d0 = JsonDocument.Parse(st0.Body))
            {
                Assert.False(d0.RootElement.GetProperty("recording").GetBoolean());
                Assert.Equal(0, d0.RootElement.GetProperty("recordingSec").GetInt32());
            }

            try
            {
                Assert.Equal(200, (await client.SendModeAsync(token, "record", probe.ServerCertSha256Hex)).StatusCode);
                var st1 = await client.GetStateAsync(token, probe.ServerCertSha256Hex);
                using var d1 = JsonDocument.Parse(st1.Body);
                Assert.True(d1.RootElement.GetProperty("recording").GetBoolean());
            }
            finally
            {
                await client.SendModeAsync(token, "record", probe.ServerCertSha256Hex);
            }
        }
        else
        {
            Assert.Equal(403, probe.StatusCode);
            using var mock = new MediaAndFixtureBuilder.MockDeckTlsServer(enableUdpDiscovery: false);
            using var mockClient = new LoopbackAndDeckClient(deckTlsPort: mock.TlsPort);
            var st = await mockClient.GetStateAsync(mock.IssuedToken, mock.CertFingerprintSha256);
            using var doc = JsonDocument.Parse(st.Body);
            Assert.True(doc.RootElement.TryGetProperty("recording", out _));
            Assert.True(doc.RootElement.TryGetProperty("recordingSec", out _));
        }
    }

    [Fact]
    public async Task F21_05_DisconnectedObsAndChat_CleanFallbackWithoutCrash()
    {
        SettingsJsonHelper.ResetToCleanSettings(o =>
        {
            o["BottomTaskbarEnabled"] = false;
            o["StreamChatEnabled"] = true;
            o["ShowObsRecPill"] = true;
            o["TwitchChannel"] = "offline_nonexistent_e2e_channel";
        });

        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2000);
        Assert.False(session.Process.HasExited);

        using var client = new LoopbackAndDeckClient();
        Assert.Equal(200, (await client.GetLoopbackRootAsync()).StatusCode);
    }

    // =========================================================================
    // F22: Fullscreen Game Strip & Rules — 5 Tests
    // =========================================================================

    [Fact]
    public async Task F22_01_GameOverlaySettings_OffSlimNormal_PersistAcrossRestart()
    {
        foreach (int mode in new[] { 0 /* Off */, 1 /* SlimStrip */, 2 /* Normal */ })
        {
            SettingsJsonHelper.ResetToCleanSettings(o =>
            {
                o["BottomTaskbarEnabled"] = false;
                o["GameOverlayMasterEnabled"] = mode != 0;
                o["GameOverlayMode"] = mode;
            });

            await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1300);
            Assert.True(await CloseGracefullyAsync(session));
            Assert.Equal(mode != 0, SettingsJsonHelper.GetValue<bool>("GameOverlayMasterEnabled"));
            Assert.Equal(mode, SettingsJsonHelper.GetValue<int>("GameOverlayMode"));
        }
    }

    [Fact]
    public async Task F22_02_GameRulesAllowList_And_MetricFlags_Persist()
    {
        SettingsJsonHelper.ResetToCleanSettings(o =>
        {
            o["BottomTaskbarEnabled"] = false;
            o["GameOverlayMasterEnabled"] = true;
            o["GameOverlayMode"] = 1;
            o["GameStripShowFps"] = true;
            o["GameStripShowCpu"] = true;
            o["GameStripShowGpu"] = true;
            o["GameStripShowRam"] = true;
            o["GameStripShowTemps"] = false;
            o["GameStripShowNet"] = true;
            o["GameRulesAllowList"] = new JsonArray("cs2.exe", "NotchPixelE2E_Game.exe");
        });

        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1500);
        Assert.True(await CloseGracefullyAsync(session));

        var rules = SettingsJsonHelper.GetValue<List<string>>("GameRulesAllowList");
        Assert.NotNull(rules);
        Assert.Contains("NotchPixelE2E_Game.exe", rules);
        Assert.False(SettingsJsonHelper.GetValue<bool>("GameStripShowTemps"));
    }

    [Fact]
    public async Task F22_03_FullscreenDummyWindow_ForegroundFocusNeverStolen()
    {
        SettingsJsonHelper.ResetToCleanSettings(o =>
        {
            o["BottomTaskbarEnabled"] = false;
            o["GameOverlayMasterEnabled"] = true;
            o["GameOverlayMode"] = 1;
            o["GameRulesAllowList"] = new JsonArray("testhost.exe", "dotnet.exe", "NotchPixel.E2E.Tests.exe");
        });

        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1600);
        using var fs = ProcessAndWindowDriver.CreateDummyFullscreenWindow(title: "NotchPixelE2E_F22_Game");
        fs.BringToFront();
        await Task.Delay(700);

        Assert.Equal(fs.Hwnd, ProcessAndWindowDriver.GetForegroundWindowHandle());
        Assert.False(session.Process.HasExited);
    }

    [Fact]
    public async Task F22_04_SelfTest_VerifiesGameStripGeometryAllThreeModes()
    {
        var res = await ProcessAndWindowDriver.RunCliAsync("--self-test", timeoutMs: 20000);
        Assert.Equal(0, res.ExitCode);
        Assert.Contains("Disableable Game Overlay supports Off/Hide completely, Slim FPS/CPU/GPU strip, and Normal modes", res.StdOut);
    }

    [Fact]
    public async Task F22_05_DeckStateTelemetryFlags_ZeroFakeDesktopTemps()
    {
        const string token = "e2e-f22-telem-tok";
        SeedPairedDeviceToken(token);
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
            var st = await client.GetStateAsync(token, probe.ServerCertSha256Hex);
            Assert.Equal(200, st.StatusCode);
            using var doc = JsonDocument.Parse(st.Body);
            int cpu = doc.RootElement.GetProperty("cpu").GetInt32();
            int ram = doc.RootElement.GetProperty("ram").GetInt32();
            Assert.InRange(cpu, 0, 100);
            Assert.InRange(ram, 0, 100);
            Assert.False(doc.RootElement.GetProperty("cpuTempAvailable").GetBoolean());
        }
        else
        {
            var selfTest = await ProcessAndWindowDriver.RunCliAsync("--self-test", timeoutMs: 20000);
            Assert.Equal(0, selfTest.ExitCode);
            Assert.Contains("Desktop CPU temperature does not invent fake motherboard temps", selfTest.StdOut);
        }
    }

    // =========================================================================
    // F23: Reminders, Notifications, Controls, Captures, Launcher & Web Drawer — 6 Tests
    // =========================================================================

    [Fact]
    public async Task F23_01_LoopbackNotifyEndpoint_GetAndPost_Accepted()
    {
        SettingsJsonHelper.ResetToCleanSettings(o => o["BottomTaskbarEnabled"] = false);
        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1600);
        using var client = new LoopbackAndDeckClient();
        Assert.True(await client.WaitForLoopbackReadyAsync(5000));

        var getRes = await client.SendNotifyAsync("Build Complete", "0 warnings", "CI", usePost: false);
        Assert.Equal(200, getRes.StatusCode);
        Assert.Contains("\"endpoint\":\"notify\"", getRes.Body);

        var postRes = await client.SendNotifyAsync("Deploy Ready", "Artifact verified", "CI", usePost: true);
        Assert.Equal(200, postRes.StatusCode);
        Assert.Contains("\"ok\":true", postRes.Body);
    }

    [Fact]
    public async Task F23_02_CliNotifyFlag_ExitsZeroCleanly()
    {
        var res = await ProcessAndWindowDriver.RunCliAsync("--notify \"E2E CLI Notification\"", timeoutMs: 15000);
        Assert.Equal(0, res.ExitCode);
        Assert.False(res.TimedOut);
    }

    [Fact]
    public async Task F23_03_RemindersPersistence_OnceDailyWeekdaysWeekly_SurviveRestart()
    {
        SettingsJsonHelper.ResetToCleanSettings(o => o["BottomTaskbarEnabled"] = false);
        Directory.CreateDirectory(TestWorkspace.LocalAppDataOpenPixelDir);
        string remPath = Path.Combine(TestWorkspace.LocalAppDataOpenPixelDir, "reminders.json");
        long futureUnix = DateTimeOffset.UtcNow.AddHours(6).ToUnixTimeMilliseconds();

        var reminders = new JsonArray
        {
            new JsonObject { ["Id"] = 11, ["Title"] = "Once Check", ["DueUnixMs"] = futureUnix, ["RepeatRule"] = "Once", ["Triggered"] = false, ["Completed"] = false },
            new JsonObject { ["Id"] = 12, ["Title"] = "Daily Standup", ["DueUnixMs"] = futureUnix, ["RepeatRule"] = "Daily", ["Triggered"] = false, ["Completed"] = false },
            new JsonObject { ["Id"] = 13, ["Title"] = "Weekday Review", ["DueUnixMs"] = futureUnix, ["RepeatRule"] = "Weekdays", ["Triggered"] = false, ["Completed"] = false },
            new JsonObject { ["Id"] = 14, ["Title"] = "Weekly Retro", ["DueUnixMs"] = futureUnix, ["RepeatRule"] = "Weekly", ["Triggered"] = false, ["Completed"] = false }
        };
        File.WriteAllText(remPath, reminders.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1600);
        Assert.True(await CloseGracefullyAsync(session));

        Assert.True(File.Exists(remPath));
        string saved = File.ReadAllText(remPath);
        Assert.Contains("Once Check", saved);
        Assert.Contains("Daily Standup", saved);
        Assert.Contains("Weekday Review", saved);
        Assert.Contains("Weekly Retro", saved);
    }

    [Fact]
    public async Task F23_04_LauncherSlots_SixSlotsConfig_PersistAcrossRestart()
    {
        var slots = new JsonArray();
        for (int i = 0; i < 6; i++)
        {
            slots.Add(new JsonObject
            {
                ["Label"] = $"Slot{i + 1}",
                ["TargetPath"] = $@"C:\Windows\System32\notepad.exe",
                ["BadgeColorArgb"] = -12541697
            });
        }

        SettingsJsonHelper.ResetToCleanSettings(o =>
        {
            o["BottomTaskbarEnabled"] = false;
            o["LauncherSlots"] = slots;
        });

        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1600);
        Assert.True(await CloseGracefullyAsync(session));

        var loaded = SettingsJsonHelper.LoadOrCreate()["LauncherSlots"]?.AsArray();
        Assert.NotNull(loaded);
        Assert.Equal(6, loaded.Count);
        Assert.Equal("Slot6", loaded[5]?["Label"]?.GetValue<string>());
    }

    [Fact]
    public async Task F23_05_WebTestCli_WebView2RuntimeResolvesCleanly()
    {
        var res = await ProcessAndWindowDriver.RunCliAsync("--web-test", timeoutMs: 15000);
        Assert.Equal(0, res.ExitCode);
        Assert.False(res.TimedOut);
        Assert.Contains("WEBVIEW_OK", res.StdOut);
    }

    [Fact]
    public async Task F23_06_DormantWebDrawer_NoWebView2SpawnedWhenClosed()
    {
        int[] beforeWebViewPids = Process.GetProcessesByName("msedgewebview2").Select(p =>
        {
            try { return p.Id; }
            catch { return -1; }
            finally { p.Dispose(); }
        }).ToArray();

        SettingsJsonHelper.ResetToCleanSettings(o => o["BottomTaskbarEnabled"] = false);
        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2000);
        await Task.Delay(500);

        int[] afterWebViewPids = Process.GetProcessesByName("msedgewebview2").Select(p =>
        {
            try { return p.Id; }
            catch { return -1; }
            finally { p.Dispose(); }
        }).ToArray();

        // No Web Drawer window should exist when WebDrawerVisible == false
        var wins = session.GetOwnedWindows(visibleOnly: true);
        Assert.DoesNotContain(wins, w => w.Title.Contains("Web", StringComparison.OrdinalIgnoreCase));

        int[] newlySpawned = afterWebViewPids.Except(beforeWebViewPids).Where(id => id > 0).ToArray();
        Assert.Empty(newlySpawned);
    }

    // =========================================================================
    // F24: Android Companion (NotchPixel Deck -> main.apk) — 7 Tests
    // =========================================================================

    [Fact]
    public void F24_01_AppDirectory_ZeroIosArtifacts()
    {
        IReadOnlyList<string> iosArtifacts = AndroidHarnessRunner.FindIosArtifactsInApp();
        Assert.Empty(iosArtifacts);
    }

    [Fact]
    public async Task F24_02_AppApk_ExistsAndPassesAapt2AndApkSignerVerification()
    {
        string? apkPath = AndroidHarnessRunner.FindBuiltApkPath();
        if (string.IsNullOrEmpty(apkPath))
        {
            var buildRes = await AndroidHarnessRunner.RunBuildApkScriptAsync(90000);
            Assert.Equal(0, buildRes.ExitCode);
            apkPath = AndroidHarnessRunner.FindBuiltApkPath();
        }

        Assert.NotNull(apkPath);
        Assert.NotEmpty(apkPath);
        ApkInspectionResult info = await AndroidHarnessRunner.InspectApkAsync(apkPath);

        Assert.True(info.Exists, $"APK missing at {apkPath}");
        Assert.True(info.HasAndroidManifest, "APK missing AndroidManifest.xml");
        Assert.True(info.HasClassesDex, "APK missing classes.dex");
        Assert.True(info.HasResourcesArsc, "APK missing resources.arsc");
        Assert.True(info.HasSignatureEntries, "APK missing META-INF signature entries");
        Assert.True(info.SignatureVerified, $"apksigner verify failed: {info.VerifyOutput}");
        Assert.Equal("com.openpixel.deck", info.PackageName);
        Assert.False(string.IsNullOrWhiteSpace(info.LaunchableActivity));
        Assert.Contains("android.permission.INTERNET", info.Permissions);
        Assert.Contains("android.permission.ACCESS_WIFI_STATE", info.Permissions);
        Assert.Contains("android.permission.CAMERA", info.Permissions);

        string manifestXml = File.ReadAllText(Path.Combine(TestWorkspace.AppDirectory, "app", "src", "main", "AndroidManifest.xml"));
        Assert.Contains("usesCleartextTraffic=\"false\"", manifestXml);
    }

    [Fact]
    public async Task F24_03_AndroidJvmProtocolTests_AllPass()
    {
        AndroidHarnessResult res = await AndroidHarnessRunner.RunJvmProtocolTestsAsync(30000);
        Assert.True(res.Compiled, $"Android JVM harness failed to compile: {res.StdErr}");
        Assert.False(res.TimedOut, "Android JVM harness timed out.");
        Assert.Equal(0, res.ExitCode);
        Assert.Equal(0, res.FailedCount);
        Assert.True(res.PassedCount >= 8, $"Expected >= 8 passing JVM protocol tests, got {res.PassedCount}.");
    }

    [Fact]
    public async Task F24_04_LivePcDeck_ProbeAndUnauthPowerGating_Enforced()
    {
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
            using var doc = JsonDocument.Parse(probe.Body);
            Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("pcName").GetString()));
            Assert.Equal(47825, doc.RootElement.GetProperty("tlsPort").GetInt32());
            string fp = doc.RootElement.GetProperty("certFingerprint").GetString() ?? "";
            Assert.Equal(64, fp.Length);
            Assert.Equal(fp, probe.ServerCertSha256Hex);
            Assert.True(doc.RootElement.TryGetProperty("mac", out _));

            // SAFETY: Never send valid token to live /deck/power; verify 401 gating without token
            var unauthState = await client.GetStateAsync(token: "", expectedFp: fp);
            Assert.Equal(401, unauthState.StatusCode);

            var unauthPower = await client.SendPowerAsync(token: "", action: "lock", expectedFp: fp);
            Assert.Equal(401, unauthPower.StatusCode);
        }
        else
        {
            // Public Wi-Fi enforces 403 on all /deck/* endpoints
            Assert.Equal(403, probe.StatusCode);
            var unauthPower = await client.SendPowerAsync(token: "", action: "lock");
            Assert.Equal(403, unauthPower.StatusCode);
        }
    }

    [Fact]
    public async Task F24_05_LivePcDeck_InvalidPairingCodeReturns401_AndDisabledDeckReturns403()
    {
        SettingsJsonHelper.ResetToCleanSettings(o =>
        {
            o["BottomTaskbarEnabled"] = false;
            o["PcDeckEnabled"] = true;
        });

        await using (var s1 = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1700))
        {
            using var client = new LoopbackAndDeckClient();
            var probe = await client.ProbeAsync();
            var badPair = await client.PairAsync("000000");
            if (probe.StatusCode == 200) Assert.Equal(401, badPair.StatusCode);
            else Assert.Equal(403, badPair.StatusCode);
        }

        // Now verify PcDeckEnabled = false strictly returns 403 Forbidden
        SettingsJsonHelper.ResetToCleanSettings(o =>
        {
            o["BottomTaskbarEnabled"] = false;
            o["PcDeckEnabled"] = false;
        });

        await using (var s2 = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1700))
        {
            using var client = new LoopbackAndDeckClient();
            Assert.Equal(403, (await client.ProbeAsync()).StatusCode);
            Assert.Equal(403, (await client.PairAsync("123456")).StatusCode);
        }
    }

    [Fact]
    public async Task F24_06_LivePcDeck_UdpDiscovery47826_ReturnsRequiredJsonKeys()
    {
        SettingsJsonHelper.ResetToCleanSettings(o =>
        {
            o["BottomTaskbarEnabled"] = false;
            o["PcDeckEnabled"] = true;
        });

        await using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 1800);
        using var client = new LoopbackAndDeckClient();
        var probe = await client.ProbeAsync();
        var udp = await client.SendDiscoveryProbeAsync("OPENPIXEL_DECK_DISCOVER", timeoutMs: 1500);

        if (probe.StatusCode == 200)
        {
            Assert.NotNull(udp);
            Assert.Equal(47825, udp.TlsPort);
            Assert.False(string.IsNullOrWhiteSpace(udp.CertFingerprint));
            Assert.False(string.IsNullOrWhiteSpace(udp.PcName));
            Assert.False(string.IsNullOrWhiteSpace(udp.Mac));
            Assert.True(udp.Parsed.ContainsKey("ip"));
            Assert.True(udp.Parsed.ContainsKey("certFingerprint"));
            Assert.True(udp.Parsed.ContainsKey("pcName"));
        }
        else
        {
            Assert.Equal(403, probe.StatusCode);
            Assert.Null(udp);
        }
    }

    [Fact]
    public async Task F24_07_MockDeckTlsServer_FullPairingPadMediaAppsModesAndPowerProtocol()
    {
        using var mock = new MediaAndFixtureBuilder.MockDeckTlsServer(enableUdpDiscovery: true);
        using var client = new LoopbackAndDeckClient(
            host: "127.0.0.1",
            deckTlsPort: mock.TlsPort,
            discoveryUdpPort: mock.UdpPort);

        var udp = await client.SendDiscoveryProbeAsync("OPENPIXEL_DECK_DISCOVER", timeoutMs: 1500);
        Assert.NotNull(udp);
        Assert.Equal(mock.TlsPort, udp.TlsPort);
        Assert.Equal(mock.CertFingerprintSha256, udp.CertFingerprint);
        Assert.Equal(mock.PcName, udp.PcName);

        var pair = await client.PairAsync(mock.PairingCode, expectedFp: mock.CertFingerprintSha256);
        Assert.Equal(200, pair.StatusCode);
        string token = JsonDocument.Parse(pair.Body).RootElement.GetProperty("token").GetString()!;

        Assert.Equal(200, (await client.GetStateAsync(token, mock.CertFingerprintSha256)).StatusCode);
        Assert.Equal(200, (await client.SendPadAsync(token, "a=move&dx=10&dy=-5", mock.CertFingerprintSha256)).StatusCode);
        Assert.Equal(200, (await client.SendMediaAsync(token, "a=vol&v=50", mock.CertFingerprintSha256)).StatusCode);
        Assert.Equal(200, (await client.SendAppAsync(token, "idx=0", mock.CertFingerprintSha256)).StatusCode);
        Assert.Equal(200, (await client.SendModeAsync(token, "slide_next", mock.CertFingerprintSha256)).StatusCode);

        // SAFETY: Power actions tested ONLY against MockDeckTlsServer
        foreach (string pwr in new[] { "lock", "sleep", "restart", "shutdown", "abort" })
        {
            Assert.Equal(200, (await client.SendPowerAsync(token, pwr, mock.CertFingerprintSha256)).StatusCode);
        }

        Assert.Equal(200, (await client.UnpairAsync(token, mock.CertFingerprintSha256)).StatusCode);
        Assert.Equal(401, (await client.GetStateAsync(token, mock.CertFingerprintSha256)).StatusCode);
    }
}
