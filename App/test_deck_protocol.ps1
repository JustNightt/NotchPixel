$ErrorActionPreference = "Stop"

$AppRoot = $PSScriptRoot
$RepoRoot = Split-Path -Parent $AppRoot
$JavaBin = "C:\Program Files\Android\Android Studio\jbr\bin"
$Javac = Join-Path $JavaBin "javac.exe"
$Java = Join-Path $JavaBin "java.exe"

if (-not (Test-Path $Javac)) { throw "JBR javac not found at $Javac" }

$TempDir = Join-Path $env:TEMP ("NotchPixel_M6_Test_" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $TempDir | Out-Null

try {
    # ========================================================================
    # 1. Pure-Java & Android Client Tests (App/test_jvm)
    # ========================================================================
    Write-Host "=== [1/2] Compiling and running App/test_jvm suite ==="
    $JavaOut = Join-Path $TempDir "jvm_classes"
    New-Item -ItemType Directory -Force -Path $JavaOut | Out-Null

    $StubFiles = @(Get-ChildItem -Path (Join-Path $AppRoot "test_jvm\stubs") -Recurse -Filter "*.java" | ForEach-Object { $_.FullName })
    $QrFiles = @(Get-ChildItem -Path (Join-Path $AppRoot "app\src\main\java\com\openpixel\deck\qr") -Recurse -Filter "*.java" | ForEach-Object { $_.FullName })
    $NetFiles = @(
        (Join-Path $AppRoot "app\src\main\java\com\openpixel\deck\network\WakeOnLanSender.java"),
        (Join-Path $AppRoot "app\src\main\java\com\openpixel\deck\security\SecurePairingStore.java"),
        (Join-Path $AppRoot "app\src\main\java\com\openpixel\deck\security\PinnedDeckClient.java")
    )
    $TestFiles = @(Get-ChildItem -Path (Join-Path $AppRoot "test_jvm\src") -Recurse -Filter "*.java" | ForEach-Object { $_.FullName })

    $AllJava = $StubFiles + $QrFiles + $NetFiles + $TestFiles
    & $Javac --release 11 -d $JavaOut @AllJava
    if ($LASTEXITCODE -ne 0) { throw "Java test compilation failed" }

    & $Java -cp $JavaOut com.openpixel.deck.test.DeckJvmTestRunner $TempDir
    if ($LASTEXITCODE -ne 0) { throw "DeckJvmTestRunner failed" }

    # ========================================================================
    # 2. .NET 10 Live LocalNetworkServers Protocol Harness
    # ========================================================================
    Write-Host "=== [2/2] Compiling and running .NET 10 PC Deck Protocol Harness ==="
    $OpenPixelOutDir = Join-Path $RepoRoot "bin\Release\net10.0-windows10.0.19041.0"
    $OpenPixelDll = Join-Path $OpenPixelOutDir "OpenPixel.dll"
    if (-not (Test-Path $OpenPixelDll)) {
        $OpenPixelDll = Join-Path $RepoRoot "OpenPixel.dll"
    }
    if (-not (Test-Path $OpenPixelDll)) {
        throw "OpenPixel.dll not found. Run dotnet build OpenPixel.csproj -c Release first."
    }

    $HarnessDir = Join-Path $TempDir "PcDeckHarness"
    New-Item -ItemType Directory -Force -Path $HarnessDir | Out-Null

    $CsprojContent = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>disable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="OpenPixel">
      <HintPath>$OpenPixelDll</HintPath>
    </Reference>
  </ItemGroup>
</Project>
"@
    Set-Content -Path (Join-Path $HarnessDir "PcDeckHarness.csproj") -Value $CsprojContent -Encoding UTF8

    $ProgramCs = @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using OpenPixel.Core;
using OpenPixel.Rendering;
using OpenPixel.Services;

internal static class Program
{
    private static int _passed;
    private static int _failed;

    private static void Check(string name, bool cond)
    {
        if (cond) { Console.WriteLine($"[PASS] {name}"); _passed++; }
        else      { Console.WriteLine($"[FAIL] {name}"); _failed++; }
    }

    private static int Main()
    {
        string pairedPath = Path.Combine(ConfigManager.GetAppDataDirectory(), "paired_deck_devices.json");
        string backupPaired = File.Exists(pairedPath) ? File.ReadAllText(pairedPath, Encoding.UTF8) : null;

        try
        {
            var state = GlobalAppState.Instance;
            ConfigManager.InitializeDefaults(state);
            state.Settings.PcDeckTlsPort = 47895;
            state.Settings.PcDeckPort = 47894;
            state.Settings.LoopbackApiEnabled = false;
            LocalNetworkServers.SkipInteractiveAllowPromptForTest = false;

            // 1. Live Windows NetworkListManager profile check
            bool nlmExpectedPrivate = false;
            try
            {
                Type nlmType = Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"));
                dynamic nlm = Activator.CreateInstance(nlmType);
                bool hasPriv = false, hasPub = false;
                foreach (dynamic net in nlm.GetNetworks(1))
                {
                    int cat = Convert.ToInt32(net.GetCategory());
                    if (cat == 0) hasPub = true;
                    else if (cat == 1 || cat == 2) hasPriv = true;
                }
                nlmExpectedPrivate = hasPriv && !hasPub;
            }
            catch { nlmExpectedPrivate = false; }

            bool detected = LocalNetworkServers.DetectPrivateNetworkAndLocalIp(state);
            Check($"DetectPrivateNetworkAndLocalIp matches live Windows NLM profile ({nlmExpectedPrivate})", detected == nlmExpectedPrivate);

            // Force Private for local protocol/socket tests
            state.IsNetworkPrivate = false;
            state.Settings.PcDeckEnabled = true;
            var (pubSt, _, _, _) = LocalNetworkServers.ProcessDeckOrShelfRequest(state, "GET /deck/state HTTP/1.1\r\n\r\n", "127.0.0.1");
            Check("Public network blocks /deck/state with 403 Forbidden", pubSt.StartsWith("403"));

            state.IsNetworkPrivate = true;
            state.Settings.PcDeckEnabled = true;

            // 2. Pairing QR URI & length check
            var cert = LocalNetworkServers.EnsurePcTlsCertificate(state);
            string code = LocalNetworkServers.GenerateTwoMinutePairingCode(state);
            string qrUri = LocalNetworkServers.BuildDeckPairingQrPayload(state);
            var qrMat = QrCodeEncoder.Encode(qrUri);
            Check("BuildDeckPairingQrPayload uses notchpixel-deck:// and encodes <= Version 10",
                qrUri.StartsWith("notchpixel-deck://pair?") && Encoding.UTF8.GetByteCount(qrUri) <= 213 && qrMat.Version <= 10);

            // 3. UDP Discovery JSON contract
            bool udpBuilt = LocalNetworkServers.TryBuildUdpDiscoveryReply(state, "NOTCHPIXEL_DECK_DISCOVER", out string udpJson);
            bool udpLegacyBuilt = LocalNetworkServers.TryBuildUdpDiscoveryReply(state, "OPENPIXEL_DECK_DISCOVER", out string udpLegacyJson);
            using var udpDoc = JsonDocument.Parse(udpJson);
            var root = udpDoc.RootElement;
            Check("TryBuildUdpDiscoveryReply uses exact keys certFingerprint & pcName (not cert/pc)",
                udpBuilt && udpLegacyBuilt
                && root.TryGetProperty("ip", out _)
                && root.TryGetProperty("tlsPort", out _)
                && root.TryGetProperty("certFingerprint", out _)
                && root.TryGetProperty("mac", out _)
                && root.TryGetProperty("pcName", out _)
                && !root.TryGetProperty("cert", out _)
                && !root.TryGetProperty("pc", out _));

            // 4. Auth bypass removal & empty token rejection
            var (uiSt, _, _, _) = LocalNetworkServers.ProcessDeckOrShelfRequest(state, "GET /deck/ui HTTP/1.1\r\n\r\n", "127.0.0.1");
            var (emptyTokSt, _, _, _) = LocalNetworkServers.ProcessDeckOrShelfRequest(state, "GET /deck/state?token= HTTP/1.1\r\n\r\n", "127.0.0.1");
            Check("/deck/ui auth bypass removed & empty token rejected with 401", uiSt.StartsWith("401") && emptyTokSt.StartsWith("401"));

            // 5. ApprovalHandlerForTest Deny vs Allow & 5-attempt lockout
            LocalNetworkServers.ApprovalHandlerForTest = (_, _, _, _) => false;
            var (denySt, _, _, _) = LocalNetworkServers.ProcessDeckOrShelfRequest(
                state, $"POST /deck/pair?code={code}&device=Pixel9&model=Pixel+9&os=Android+15 HTTP/1.1\r\n\r\n", "127.0.0.1");
            Check("ApprovalHandlerForTest Deny returns 403 and adds 0 paired devices",
                denySt.StartsWith("403") && state.PairedDeckDevices.Count == 0);

            // 5 bad codes invalidate ActivePairingCode
            for (int i = 0; i < 5; i++)
            {
                LocalNetworkServers.ProcessDeckOrShelfRequest(state, "POST /deck/pair?code=000000 HTTP/1.1\r\n\r\n", "127.0.0.1");
            }
            var (afterLockSt, _, _, _) = LocalNetworkServers.ProcessDeckOrShelfRequest(
                state, $"POST /deck/pair?code={code} HTTP/1.1\r\n\r\n", "127.0.0.1");
            Check("5 consecutive bad pairing codes invalidate ActivePairingCode",
                afterLockSt.StartsWith("401") && string.IsNullOrEmpty(state.ActivePairingCode));

            // Allow flow
            code = LocalNetworkServers.GenerateTwoMinutePairingCode(state);
            LocalNetworkServers.ApprovalHandlerForTest = (dev, mod, os, ip) => dev == "Pixel9";
            var (allowSt, _, allowBody, _) = LocalNetworkServers.ProcessDeckOrShelfRequest(
                state, $"POST /deck/pair?code={code}&device=Pixel9&model=Pixel+9&os=Android+15 HTTP/1.1\r\n\r\n", "127.0.0.1");
            using var pairDoc = JsonDocument.Parse(allowBody);
            string token = pairDoc.RootElement.GetProperty("token").GetString();
            Check("ApprovalHandlerForTest Allow returns 200 with token & persists paired_deck_devices.json",
                allowSt.StartsWith("200") && !string.IsNullOrEmpty(token) && File.Exists(pairedPath));

            // Persistence reload test
            state.PairedDeckDevices.Clear();
            LocalNetworkServers.LoadPairedDevices(state);
            Check("LoadPairedDevices restores paired device across restart",
                state.PairedDeckDevices.Count == 1 && state.PairedDeckDevices[0].AuthToken == token);

            // Bearer header auth + /deck/devices
            var (devSt, _, devBody, _) = LocalNetworkServers.ProcessDeckOrShelfRequest(
                state, $"GET /deck/devices HTTP/1.1\r\nAuthorization: Bearer {token}\r\n\r\n", "127.0.0.1");
            string devStr = Encoding.UTF8.GetString(devBody);
            Check("/deck/devices returns metadata without AuthToken using Bearer auth",
                devSt.StartsWith("200") && devStr.Contains("Pixel9") && !devStr.Contains(token));

            // Commands: Pad, Media, Apps, Mode, Power
            var (padTypeSt, _, _, _) = LocalNetworkServers.ProcessDeckOrShelfRequest(
                state, $"POST /deck/pad?token={Uri.EscapeDataString(token)}&a=type&text=Hi+(100%25)! HTTP/1.1\r\n\r\n", "127.0.0.1");
            state.Media.DurationSec = 100.0;
            var (seekSt, _, _, _) = LocalNetworkServers.ProcessDeckOrShelfRequest(
                state, $"POST /deck/media?token={Uri.EscapeDataString(token)}&a=seek&v=42 HTTP/1.1\r\n\r\n", "127.0.0.1");
            var (appBadSt, _, _, _) = LocalNetworkServers.ProcessDeckOrShelfRequest(
                state, $"POST /deck/app?token={Uri.EscapeDataString(token)}&a=add&path= HTTP/1.1\r\n\r\n", "127.0.0.1");
            for (int i = 0; i < 6; i++)
            {
                state.LauncherSlots[i].TargetPath = $"app{i}.exe";
                state.LauncherSlots[i].Label = $"App {i}";
            }
            var (appFullSt, _, _, _) = LocalNetworkServers.ProcessDeckOrShelfRequest(
                state, $"POST /deck/app?token={Uri.EscapeDataString(token)}&a=add&path=extra.exe HTTP/1.1\r\n\r\n", "127.0.0.1");

            string capturedPower = "";
            LocalNetworkServers.PowerActionHandlerForTest = act => { capturedPower = act; return true; };
            var (pwrSt, _, _, _) = LocalNetworkServers.ProcessDeckOrShelfRequest(
                state, $"POST /deck/power?token={Uri.EscapeDataString(token)}&a=abort HTTP/1.1\r\n\r\n", "127.0.0.1");
            Check("Pad/Media/Apps(400+409)/Power(abort) endpoints behave cleanly",
                padTypeSt.StartsWith("200")
                && seekSt.StartsWith("200") && Math.Abs(state.Media.PositionSec - 42.0) < 0.1
                && appBadSt.StartsWith("400") && appFullSt.StartsWith("409")
                && pwrSt.StartsWith("200") && capturedPower == "abort");

            // Unpair + immediate 401
            var (unpairSt, _, _, _) = LocalNetworkServers.ProcessDeckOrShelfRequest(
                state, $"POST /deck/unpair?token={Uri.EscapeDataString(token)} HTTP/1.1\r\n\r\n", "127.0.0.1");
            var (afterRevokeSt, _, _, _) = LocalNetworkServers.ProcessDeckOrShelfRequest(
                state, $"GET /deck/state?token={Uri.EscapeDataString(token)} HTTP/1.1\r\n\r\n", "127.0.0.1");
            Check("/deck/unpair revokes token and immediately returns 401 on subsequent call",
                unpairSt.StartsWith("200") && afterRevokeSt.StartsWith("401"));

            // 6. Live TLS SslStream + Live UDP 47826 socket verification
            LocalNetworkServers.Start(state);
            state.IsNetworkPrivate = true;
            state.Settings.PcDeckEnabled = true;
            Thread.Sleep(250);

            string observedServerFp = "";
            using (var tcp = new TcpClient("127.0.0.1", state.Settings.PcDeckTlsPort))
            using (var ssl = new SslStream(tcp.GetStream(), false, (_, srvCert, _, _) =>
            {
                if (srvCert != null)
                {
                    observedServerFp = Convert.ToHexString(SHA256.HashData(srvCert.GetRawCertData())).ToLowerInvariant();
                }
                return true;
            }))
            {
                ssl.AuthenticateAsClient("127.0.0.1");
                byte[] reqBytes = Encoding.UTF8.GetBytes("GET /deck/probe HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");
                ssl.Write(reqBytes);
                ssl.Flush();
                using var respMs = new MemoryStream();
                byte[] rBuf = new byte[4096];
                int n;
                while ((n = ssl.Read(rBuf, 0, rBuf.Length)) > 0)
                {
                    respMs.Write(rBuf, 0, n);
                }
                string respText = Encoding.UTF8.GetString(respMs.ToArray());
                bool tlsOk = observedServerFp == state.PcCertFingerprint && respText.Contains("200 OK") && respText.Contains(state.PcCertFingerprint);
                if (!tlsOk) Console.WriteLine($"  [DBG] obsFp={observedServerFp} expected={state.PcCertFingerprint} resp={respText}");
                Check("Live SslStream :47895 authenticates with matching SHA-256 cert fingerprint", tlsOk);
            }

            using (var udp = new UdpClient())
            {
                udp.Client.ReceiveTimeout = 1500;
                byte[] disc = Encoding.UTF8.GetBytes("NOTCHPIXEL_DECK_DISCOVER");
                udp.Send(disc, disc.Length, new IPEndPoint(IPAddress.Loopback, 47826));
                IPEndPoint remote = new(IPAddress.Any, 0);
                byte[] reply = udp.Receive(ref remote);
                string replyStr = Encoding.UTF8.GetString(reply);
                Check("Live UDP :47826 responds with certFingerprint & pcName",
                    replyStr.Contains("certFingerprint") && replyStr.Contains("pcName"));
            }

            LocalNetworkServers.Stop();
        }
        finally
        {
            LocalNetworkServers.ApprovalHandlerForTest = null;
            LocalNetworkServers.PowerActionHandlerForTest = null;
            LocalNetworkServers.Stop();
            try
            {
                if (backupPaired != null) File.WriteAllText(pairedPath, backupPaired, Encoding.UTF8);
                else if (File.Exists(pairedPath)) File.Delete(pairedPath);
            }
            catch { }
        }

        Console.WriteLine($"PC DECK HARNESS SUMMARY: {_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }
}
'@
    Set-Content -Path (Join-Path $HarnessDir "Program.cs") -Value $ProgramCs -Encoding UTF8

    $HarnessBin = Join-Path $TempDir "harness_bin"
    New-Item -ItemType Directory -Force -Path $HarnessBin | Out-Null
    & dotnet build (Join-Path $HarnessDir "PcDeckHarness.csproj") -c Release -o $HarnessBin --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "PcDeckHarness build failed" }

    Copy-Item -Force (Join-Path $OpenPixelOutDir "*.dll") $HarnessBin
    if (Test-Path (Join-Path $OpenPixelOutDir "runtimes")) {
        Copy-Item -Recurse -Force (Join-Path $OpenPixelOutDir "runtimes") (Join-Path $HarnessBin "runtimes")
    }

    & (Join-Path $HarnessBin "PcDeckHarness.exe")
    if ($LASTEXITCODE -ne 0) { throw "PcDeckHarness execution failed" }

    Write-Host "ALL M6 DECK PROTOCOL TESTS PASSED!"
}
finally {
    Remove-Item -LiteralPath $TempDir -Recurse -Force -ErrorAction SilentlyContinue
}
