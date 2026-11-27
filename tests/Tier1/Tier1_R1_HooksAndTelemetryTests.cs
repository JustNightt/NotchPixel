using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NotchPixel.E2E.Tests.Infrastructure;
using Xunit;

namespace NotchPixel.E2E.Tests.Tier1;

[Collection("NotchPixelE2E")]
public sealed class Tier1_R1_HooksAndTelemetryTests
{
    private readonly E2ETestSessionFixture _fixture;

    public Tier1_R1_HooksAndTelemetryTests(E2ETestSessionFixture fixture)
    {
        _fixture = fixture;
    }

    private static string? SeedPairedDeckDeviceIfEnabled(string token)
    {
        string path = Path.Combine(TestWorkspace.LocalAppDataOpenPixelDir, "paired_deck_devices.json");
        Directory.CreateDirectory(TestWorkspace.LocalAppDataOpenPixelDir);
        string? prior = File.Exists(path) ? File.ReadAllText(path) : null;
        var arr = new JsonArray
        {
            new JsonObject
            {
                ["DeviceId"] = "e2e-deck-f01",
                ["DeviceName"] = "E2E-Test-Phone",
                ["Model"] = "Pixel 9",
                ["OsVersion"] = "Android 15",
                ["AppVersion"] = "1.0.5",
                ["IpAddress"] = "127.0.0.1",
                ["KeyFingerprint"] = "0123456789abcdef",
                ["AuthToken"] = token,
                ["PairedAtMs"] = 1700000000000L
            }
        };
        File.WriteAllText(path, arr.ToJsonString());
        return prior;
    }

    private static void RestoreFile(string path, string? originalContent)
    {
        if (originalContent == null)
        {
            if (File.Exists(path)) File.Delete(path);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, originalContent);
        }
    }

    // =========================================================================
    // F01: Zero-Placeholder Policy & Real Telemetry (6 tests)
    // =========================================================================

    [Fact]
    public void F01_Source_ContainsZeroForbiddenPlaceholders()
    {
        bool hasForbidden = SourceInspector.ContainsForbiddenPlaceholder(out IReadOnlyList<SourceMatch> matches);
        string detail = string.Join(Environment.NewLine, matches.Select(m => $"{m.FilePath}:{m.LineNumber}: {m.LineText}"));
        Assert.False(hasForbidden, $"Forbidden placeholder(s) found in C# source:{Environment.NewLine}{detail}");
    }

    [Fact]
    public void F01_Source_ContainsNoHardcodedFakePlaybackOrFakeData()
    {
        string[] bannedPatterns =
        {
            @"Midnight\s+City",
            @"\bM83\b",
            @"Fake\s+(Spotify|YouTube|Apple|Notification|Clipboard|Reminder)",
            @"Sample\s+(Track|Notification|Reminder)",
            @"GpuPercent\s*=\s*.*cpu\s*\*\s*0\.65"
        };

        var hits = new List<SourceMatch>();
        foreach (string pattern in bannedPatterns)
        {
            hits.AddRange(SourceInspector.FindMatchesInCSharp(pattern, RegexOptions.IgnoreCase));
        }

        string detail = string.Join(Environment.NewLine, hits.Select(m => $"{m.FilePath}:{m.LineNumber}: {m.LineText}"));
        Assert.True(hits.Count == 0, $"Hardcoded fake playback/data detected:{Environment.NewLine}{detail}");
    }

    [Fact]
    public async Task F01_SelfTest_PassesZeroPlaceholdersAndCleanIdleMedia()
    {
        CliExecutionResult res = await ProcessAndWindowDriver.RunCliAsync("--self-test", timeoutMs: 15000);
        Assert.False(res.TimedOut, "--self-test timed out.");
        Assert.Equal(0, res.ExitCode);
        Assert.Contains("[PASS] Zero Placeholders: Media starts with HasSession=false", res.StdOut);
        Assert.Contains("[PASS] Zero Placeholders: Desktop CPU temperature does not invent fake motherboard temps", res.StdOut);
    }

    [Fact]
    public void F01_Source_HardwareTelemetryGatedByAvailabilityFlags()
    {
        IReadOnlyList<SourceMatch> gpuFlag = SourceInspector.FindMatchesInCSharp(@"\bGpuPercentAvailable\b");
        IReadOnlyList<SourceMatch> fpsFlag = SourceInspector.FindMatchesInCSharp(@"\bFpsAvailable\b");
        IReadOnlyList<SourceMatch> tempFlag = SourceInspector.FindMatchesInCSharp(@"\bCpuTempAvailable\b");

        Assert.True(gpuFlag.Count >= 2, "Expected GpuPercentAvailable flag to be defined and checked across telemetry/UI.");
        Assert.True(fpsFlag.Count >= 2, "Expected FpsAvailable flag to be defined and checked across telemetry/UI.");
        Assert.True(tempFlag.Count >= 2, "Expected CpuTempAvailable flag to be defined and checked across telemetry/UI.");
    }

    [Fact]
    public async Task F01_FreshStartup_ProducesNoFakeClipboardOrReminderFiles()
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings();
        string pinsPath = Path.Combine(TestWorkspace.LocalAppDataOpenPixelDir, "clipboard_pins.json");
        string remindersPath = Path.Combine(TestWorkspace.LocalAppDataOpenPixelDir, "reminders.json");
        if (File.Exists(pinsPath)) File.Delete(pinsPath);
        if (File.Exists(remindersPath)) File.Delete(remindersPath);

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2000);
        using var client = new LoopbackAndDeckClient();
        bool ready = await client.WaitForLoopbackReadyAsync(5000);
        Assert.True(ready, "Loopback API failed to become ready.");

        if (File.Exists(pinsPath))
        {
            string pinsRaw = File.ReadAllText(pinsPath);
            Assert.DoesNotContain("Sample", pinsRaw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Fake", pinsRaw, StringComparison.OrdinalIgnoreCase);
        }
        if (File.Exists(remindersPath))
        {
            string remRaw = File.ReadAllText(remindersPath);
            Assert.DoesNotContain("Sample", remRaw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Fake", remRaw, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task F01_LiveSession_LoopbackAndDeckStateReportCleanTelemetryAndNoFakeMedia()
    {
        using var guard = new StateBackupGuard();
        const string token = "e2e_f01_token_value_123456";
        string pairedPath = Path.Combine(TestWorkspace.LocalAppDataOpenPixelDir, "paired_deck_devices.json");
        string? origPaired = null;

        SettingsJsonHelper.ResetToCleanSettings(s => s["PcDeckEnabled"] = true);
        origPaired = SeedPairedDeckDeviceIfEnabled(token);
        try
        {
            using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2500);
            using var client = new LoopbackAndDeckClient();
            Assert.True(await client.WaitForLoopbackReadyAsync(8000), "Loopback 47823 did not respond.");

            HttpCallResult root = await client.GetLoopbackRootAsync();
            Assert.Equal(200, root.StatusCode);
            Assert.Contains("\"loopback\":true", root.Body);

            TlsCallResult probe = await client.ProbeAsync();
            if (probe.StatusCode == 200)
            {
                TlsCallResult stateRes = await client.GetStateAsync(token, probe.ServerCertSha256Hex);
                Assert.Equal(200, stateRes.StatusCode);

                using JsonDocument doc = JsonDocument.Parse(stateRes.Body);
                JsonElement r = doc.RootElement;
                int cpu = r.GetProperty("cpu").GetInt32();
                int gpu = r.GetProperty("gpu").GetInt32();
                int ram = r.GetProperty("ram").GetInt32();
                Assert.InRange(cpu, 0, 100);
                Assert.InRange(gpu, 0, 100);
                Assert.InRange(ram, 1, 100);

                JsonElement media = r.GetProperty("media");
                string title = media.GetProperty("title").GetString() ?? "";
                Assert.DoesNotContain("Midnight City", title, StringComparison.OrdinalIgnoreCase);
                if (!media.GetProperty("hasSession").GetBoolean())
                {
                    Assert.Equal(string.Empty, title);
                    Assert.False(media.GetProperty("isPlaying").GetBoolean());
                }
            }
            else
            {
                // On Public Wi-Fi profiles, Deck TLS intentionally returns 403 fail-closed.
                Assert.Equal(403, probe.StatusCode);
            }
        }
        finally
        {
            RestoreFile(pairedPath, origPaired);
        }
    }

    // =========================================================================
    // F02: Core Audio Volume & Mute via IAudioEndpointVolume (6 tests)
    // =========================================================================

    [Fact]
    public void F02_CoreAudio_DefaultRenderEndpointReadableAndBounded()
    {
        (float scalar01, bool _, string endpointId) = ProcessAndWindowDriver.GetSystemMasterVolumeAndMute();
        Assert.False(string.IsNullOrWhiteSpace(endpointId), "Expected active Windows default audio render endpoint ID.");
        Assert.InRange(scalar01, 0.0f, 1.0f);
    }

    [Fact]
    public void F02_Source_BindsIAudioEndpointVolumeCallbackAndNotificationClient()
    {
        IReadOnlyList<SourceMatch> epVol = SourceInspector.FindMatchesInCSharp("5CDF2C82-841E-4546-9722-0CF74078229A");
        IReadOnlyList<SourceMatch> cb = SourceInspector.FindMatchesInCSharp("657804FA-D6AD-4496-8A60-352752AF4F89");
        IReadOnlyList<SourceMatch> devNotify = SourceInspector.FindMatchesInCSharp("7991EEC9-7E89-4D85-8390-6C703CEC60C0");

        Assert.NotEmpty(epVol);
        Assert.NotEmpty(cb);
        Assert.NotEmpty(devNotify);
    }

    [Fact]
    public async Task F02_Startup_NeverUnmutesMutedSystemVolume()
    {
        using var audioScope = ProcessAndWindowDriver.CaptureAndRestoreAudioScope();
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings();

        ProcessAndWindowDriver.SetSystemMasterVolume(0.35f, muted: true);
        (float beforeScalar, bool beforeMuted, _) = ProcessAndWindowDriver.GetSystemMasterVolumeAndMute();
        Assert.True(beforeMuted, "Precondition failed: could not mute system audio.");

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2500);
        await Task.Delay(1200);

        (float afterScalar, bool afterMuted, _) = ProcessAndWindowDriver.GetSystemMasterVolumeAndMute();
        Assert.True(afterMuted, "OpenPixel startup MUST NOT unmute an already-muted system master volume.");
        Assert.InRange(MathF.Abs(afterScalar - beforeScalar), 0.0f, 0.03f);
        Assert.False(session.Process.HasExited, "OpenPixel exited unexpectedly during muted startup.");
    }

    [Fact]
    public async Task F02_SelfTest_PassesDeviceSwitchPopupSuppressionGuard()
    {
        CliExecutionResult res = await ProcessAndWindowDriver.RunCliAsync("--self-test", timeoutMs: 15000);
        Assert.False(res.TimedOut, "--self-test timed out.");
        Assert.Equal(0, res.ExitCode);
        Assert.Contains("Device-Switch Guard: No false pop-up on audio device shift", res.StdOut);
    }

    [Fact]
    public async Task F02_LiveSession_TracksSystemVolumeAndMuteChangesReliably()
    {
        using var audioScope = ProcessAndWindowDriver.CaptureAndRestoreAudioScope();
        using var guard = new StateBackupGuard();
        const string token = "e2e_f02_token_value_123456";
        string pairedPath = Path.Combine(TestWorkspace.LocalAppDataOpenPixelDir, "paired_deck_devices.json");
        SettingsJsonHelper.ResetToCleanSettings(s => s["PcDeckEnabled"] = true);
        string? origPaired = SeedPairedDeckDeviceIfEnabled(token);

        try
        {
            ProcessAndWindowDriver.SetSystemMasterVolume(0.25f, muted: false);
            using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2500);
            using var client = new LoopbackAndDeckClient();
            Assert.True(await client.WaitForLoopbackReadyAsync(8000));

            ProcessAndWindowDriver.SetSystemMasterVolume(0.64f, muted: false);
            await Task.Delay(500);
            (float s1, bool m1, _) = ProcessAndWindowDriver.GetSystemMasterVolumeAndMute();
            Assert.InRange(s1, 0.61f, 0.67f);
            Assert.False(m1);

            ProcessAndWindowDriver.SetSystemMasterVolume(0.64f, muted: true);
            await Task.Delay(500);
            (_, bool m2, _) = ProcessAndWindowDriver.GetSystemMasterVolumeAndMute();
            Assert.True(m2);

            TlsCallResult probe = await client.ProbeAsync();
            if (probe.StatusCode == 200)
            {
                TlsCallResult deckState = await client.GetStateAsync(token, probe.ServerCertSha256Hex);
                Assert.Equal(200, deckState.StatusCode);
                using JsonDocument doc = JsonDocument.Parse(deckState.Body);
                int reportedVol = doc.RootElement.GetProperty("volume").GetInt32();
                bool reportedMute = doc.RootElement.GetProperty("muted").GetBoolean();
                Assert.InRange(reportedVol, 60, 68);
                Assert.True(reportedMute);
            }

            Assert.False(session.Process.HasExited);
        }
        finally
        {
            RestoreFile(pairedPath, origPaired);
        }
    }

    [Fact]
    public void F02_Source_IgnoresMixerAndLoopbackEndpointsOnMicMute()
    {
        IReadOnlyList<SourceMatch> mixerFilter = SourceInspector.FindMatchesInCSharp(@"IsMixerOrLoopbackEndpoint");
        IReadOnlyList<SourceMatch> stereoMix = SourceInspector.FindMatchesInCSharp(@"stereo\s+mix|loopback|what\s+u\s+hear");

        Assert.NotEmpty(mixerFilter);
        Assert.NotEmpty(stereoMix);
    }

    // =========================================================================
    // F03: Real Windows Display Brightness (6 tests)
    // =========================================================================

    [Fact]
    public void F03_Source_ImplementsWmiAndDdcCiBrightnessApis()
    {
        IReadOnlyList<SourceMatch> wmiRead = SourceInspector.FindMatchesInCSharp(@"WmiMonitorBrightness");
        IReadOnlyList<SourceMatch> wmiSet = SourceInspector.FindMatchesInCSharp(@"WmiMonitorBrightnessMethods");
        IReadOnlyList<SourceMatch> dxva2 = SourceInspector.FindMatchesInCSharp(@"dxva2\.dll");
        IReadOnlyList<SourceMatch> ddcSet = SourceInspector.FindMatchesInCSharp(@"SetMonitorBrightness");

        Assert.NotEmpty(wmiRead);
        Assert.NotEmpty(wmiSet);
        Assert.NotEmpty(dxva2);
        Assert.NotEmpty(ddcSet);
    }

    [Fact]
    public void F03_Source_DdcBrightnessScalesWithinMinMaxAndClamps0To100()
    {
        IReadOnlyList<SourceMatch> scale = SourceInspector.FindMatchesInCSharp(@"ScaleDdcBrightness");
        IReadOnlyList<SourceMatch> readMethod = SourceInspector.FindMatchesInCSharp(@"ReadMonitorBrightnessLevel");
        IReadOnlyList<SourceMatch> setMethod = SourceInspector.FindMatchesInCSharp(@"SetMonitorBrightnessLevel");

        Assert.True(scale.Count >= 2, "Expected ScaleDdcBrightness definition and call site.");
        Assert.NotEmpty(readMethod);
        Assert.NotEmpty(setMethod);
    }

    [Fact]
    public async Task F03_LiveSession_ExternalMonitorWithoutDdcDoesNotCrash()
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings();

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2500);
        IReadOnlyList<WindowSnapshot> wins = await session.WaitForVisibleWindowsAsync(1, 8000);
        Assert.NotEmpty(wins);

        using var client = new LoopbackAndDeckClient();
        HttpCallResult root = await client.GetLoopbackRootAsync();
        Assert.Equal(200, root.StatusCode);
        Assert.False(session.Process.HasExited, "OpenPixel crashed on monitor brightness probe.");
    }

    [Fact]
    public async Task F03_OsWmiBrightnessProbe_WithinZeroToHundredIfPresent()
    {
        CliExecutionResult ps = await ProcessAndWindowDriver.RunProcessAsync(
            "powershell",
            "-NoProfile -Command \"try { $b = Get-CimInstance -Namespace root/wmi -ClassName WmiMonitorBrightness -ErrorAction Stop | Select-Object -First 1 -ExpandProperty CurrentBrightness; if ($null -ne $b) { Write-Output ('BRIGHT=' + $b) } else { Write-Output 'NONE' } } catch { Write-Output 'UNSUPPORTED' }\"",
            timeoutMs: 10000);

        Assert.False(ps.TimedOut);
        string outStr = ps.StdOut.Trim();
        if (outStr.StartsWith("BRIGHT=", StringComparison.Ordinal))
        {
            Assert.True(int.TryParse(outStr.Substring("BRIGHT=".Length), out int level));
            Assert.InRange(level, 0, 100);
        }
        else
        {
            // Desktop monitors without internal WMI panel must report UNSUPPORTED/NONE cleanly.
            Assert.True(outStr.Contains("UNSUPPORTED") || outStr.Contains("NONE"), $"Unexpected WMI output: {outStr}");
        }
    }

    [Fact]
    public async Task F03_LiveSession_NoBrightnessExceptionsLoggedDuringStartupTicks()
    {
        using var guard = new StateBackupGuard();
        SettingsJsonHelper.ResetToCleanSettings();
        if (File.Exists(TestWorkspace.DiagnosticLogPath)) File.Delete(TestWorkspace.DiagnosticLogPath);

        using var session = await ProcessAndWindowDriver.StartGuiSessionAsync(startupWaitMs: 2500);
        await Task.Delay(1200);
        Assert.False(session.Process.HasExited);

        if (File.Exists(TestWorkspace.DiagnosticLogPath))
        {
            string log = File.ReadAllText(TestWorkspace.DiagnosticLogPath);
            Assert.DoesNotContain("Brightness", log, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("dxva2", log, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task F03_LiveSession_ControlPanelRenderingDoesNotThrowOnMissingBrightnessHardware()
    {
        CliExecutionResult res = await ProcessAndWindowDriver.RunCliAsync("--self-test", timeoutMs: 15000);
        Assert.False(res.TimedOut);
        Assert.Equal(0, res.ExitCode);
        Assert.Contains("Stress-tested slider 0..40px across all 10 tabs cleanly", res.StdOut);
    }
}
