using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using OpenPixel.Core;
using OpenPixel.Rendering;

namespace OpenPixel.Services
{
    // ========================================================================
    // Local Network Servers (Windows PC Side)
    // 1. Loopback Scripting API (http://127.0.0.1:47823/notify, /tile, /agent)
    // 2. PC Deck Encrypted TLS/HTTPS Server (Port 47825) + HTTP/Probe (47824)
    //    - Off by default until turned on in Deck tab / Settings
    //    - Restricted strictly to Private Wi-Fi networks (disabled on Public)
    //    - Self-signed X.509 RSA-2048 cert pinned by SHA-256 fingerprint on Android
    //    - 2-minute expiring 6-digit pairing code + explicit PC "Allow" prompt
    //    - Device metadata & key fingerprint list + instant revocation
    //    - Full Pad, Now Playing, Apps, Modes (Game/Present/Movie/Work), PC Power
    // 3. Send-to-Phone File Shelf Wi-Fi QR Transfer Server (/shelf & /download)
    // ========================================================================
    public static class LocalNetworkServers
    {
        private static volatile bool _running;
        public static bool SkipInteractiveAllowPromptForTest { get; set; } = false;

        private static TcpListener _loopbackListener;
        private static TcpListener _deckHttpListener;
        private static TcpListener _deckTlsListener;
        private static UdpClient _udpDiscovery;
        private static X509Certificate2 _pcCert;

        // The app used to ship notchpixel_deck.pfx; adopt it so the pinned TLS
        // identity survives the rename and already-paired phones keep trusting us.
        private static void TryAdoptLegacyCertificate(string certPath)
        {
            try
            {
                if (File.Exists(certPath)) return;
                string legacy = Path.Combine(ConfigManager.GetAppDataDirectory(), "notchpixel_deck.pfx");
                if (File.Exists(legacy)) File.Copy(legacy, certPath, false);
            }
            catch { }
        }

        public static X509Certificate2 EnsurePcTlsCertificate(GlobalAppState state)
        {
            if (_pcCert != null) return _pcCert;
            try
            {
                string certPath = Path.Combine(ConfigManager.GetAppDataDirectory(), "openpixel_deck.pfx");
                TryAdoptLegacyCertificate(certPath);
                if (File.Exists(certPath))
                {
                    _pcCert = X509CertificateLoader.LoadPkcs12FromFile(certPath, "openpixel-local");
                }
                else
                {
                    using RSA rsa = RSA.Create(2048);
                    var req = new CertificateRequest($"CN={Environment.MachineName}.openpixel.local", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
                    _pcCert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
                    byte[] pfxBytes = _pcCert.Export(X509ContentType.Pfx, "openpixel-local");
                    File.WriteAllBytes(certPath, pfxBytes);
                }

                byte[] hash = SHA256.HashData(_pcCert.RawData);
                string fp = Convert.ToHexString(hash).ToLowerInvariant();
                lock (state.SyncRoot)
                {
                    state.PcCertFingerprint = fp;
                }
            }
            catch { }
            return _pcCert;
        }

        public static bool DetectPrivateNetworkAndLocalIp(GlobalAppState state)
        {
            string bestIp = "127.0.0.1";
            string bestMac = "00:00:00:00:00:00";
            bool isRfc1918Private = false;

            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up ||
                        ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        continue;

                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            byte[] b = ua.Address.GetAddressBytes();
                            if (b[0] == 10 ||
                                (b[0] == 172 && (b[1] & 0xF0) == 16) ||
                                (b[0] == 192 && b[1] == 168))
                            {
                                bestIp = ua.Address.ToString();
                                byte[] macBytes = ni.GetPhysicalAddress().GetAddressBytes();
                                if (macBytes.Length == 6)
                                {
                                    bestMac = string.Join(":", Array.ConvertAll(macBytes, x => x.ToString("X2")));
                                }
                                isRfc1918Private = true;
                                break;
                            }
                        }
                    }
                    if (isRfc1918Private) break;
                }
            }
            catch { }

            bool registrySaysPublic = false;
            try
            {
                using RegistryKey profiles = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\NetworkList\Profiles");
                if (profiles != null)
                {
                    foreach (string subName in profiles.GetSubKeyNames())
                    {
                        using RegistryKey sub = profiles.OpenSubKey(subName);
                        if (sub?.GetValue("Category") is int cat)
                        {
                            if (cat == 0) registrySaysPublic = true;
                            else if (cat == 1 || cat == 2) { registrySaysPublic = false; break; }
                        }
                    }
                }
            }
            catch { }

            lock (state.SyncRoot)
            {
                state.LocalPrivateIp = bestIp;
                state.LocalMacAddress = bestMac;
                state.IsNetworkPrivate = isRfc1918Private && !registrySaysPublic;
                if (!state.IsNetworkPrivate)
                {
                    state.Settings.PcDeckEnabled = false;
                }
                return state.IsNetworkPrivate;
            }
        }

        public static string GenerateTwoMinutePairingCode(GlobalAppState state)
        {
            int code = RandomNumberGenerator.GetInt32(100000, 1000000);
            string codeStr = code.ToString();
            lock (state.SyncRoot)
            {
                state.ActivePairingCode = codeStr;
                state.PairingCodeExpireMs = ConfigManager.GetTickMs() + 2 * 60 * 1000;
            }
            return codeStr;
        }

        public static string BuildDeckPairingQrPayload(GlobalAppState state)
        {
            EnsurePcTlsCertificate(state);
            lock (state.SyncRoot)
            {
                // Only the fields the phone scanner consumes; keeping the payload short
                // lets the code stay at a large, scannable module size.
                return $"openpixel-deck://pair?ip={state.LocalPrivateIp}&port={state.Settings.PcDeckTlsPort}&code={state.ActivePairingCode}&cert={state.PcCertFingerprint}";
            }
        }

        public static bool HandleLoopbackRequest(GlobalAppState state, string rawRequestLineAndQuery, out string outJson)
        {
            string q = rawRequestLineAndQuery ?? "";
            string Extract(string key)
            {
                string pat = key + "=";
                int idx = q.IndexOf(pat, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return "";
                idx += pat.Length;
                int end = idx;
                while (end < q.Length && q[end] != '&' && q[end] != ' ' && q[end] != '\r' && q[end] != '\n') ++end;
                return Uri.UnescapeDataString(q.Substring(idx, end - idx).Replace('+', ' '));
            }

            if (q.Contains("/notify", StringComparison.OrdinalIgnoreCase))
            {
                string title = Extract("title");
                string text = Extract("text");
                string app = Extract("app");
                if (string.IsNullOrEmpty(title)) title = "Custom Script Notice";
                if (string.IsNullOrEmpty(app)) app = "Script";

                lock (state.SyncRoot)
                {
                    state.Notifications.Insert(0, new NotificationItem
                    {
                        Id = ConfigManager.GetTickMs(),
                        AppName = app,
                        Title = title,
                        Body = text,
                        TimeStr = DateTime.Now.ToString("HH:mm"),
                        RelativeStr = "Just now"
                    });
                    state.ToastPopupType = TopPopupType.Notice;
                    state.ToastTitle = $"{app}: {title}";
                    state.ToastSubtitle = text;
                    state.ToastAccent = System.Drawing.Color.FromArgb(255, 78, 179, 255);
                    state.ToastIsReminder = false;
                    state.ToastActive = true;
                    state.ToastExpireMs = ConfigManager.GetTickMs() + (long)(state.Settings.NoticeDurationSec * 1000.0f);
                }
                outJson = "{\"ok\":true,\"endpoint\":\"notify\"}";
                return true;
            }

            if (q.Contains("/tile", StringComparison.OrdinalIgnoreCase))
            {
                string id = Extract("id");
                string title = Extract("title");
                string val = Extract("value");
                string sub = Extract("sub");
                if (string.IsNullOrEmpty(id)) id = "default";
                if (string.IsNullOrEmpty(title)) title = "Live Tile";

                lock (state.SyncRoot)
                {
                    var existing = state.CustomTiles.Find(t => t.Id == id);
                    if (existing != null) { existing.Title = title; existing.Value = val; existing.Subtitle = sub; }
                    else state.CustomTiles.Add(new CustomTileData { Id = id, Title = title, Value = val, Subtitle = sub });
                }
                outJson = "{\"ok\":true,\"endpoint\":\"tile\"}";
                return true;
            }

            if (q.Contains("/agent", StringComparison.OrdinalIgnoreCase))
            {
                string name = Extract("name");
                string status = Extract("status");
                string step = Extract("step");
                string progStr = Extract("progress");
                int progress = int.TryParse(progStr, out int p) ? Math.Clamp(p, 0, 100) : -1;
                bool almost = Extract("almost_done") == "1" || (progress >= 80 && progress < 100) ||
                              status.Equals("Almost done", StringComparison.OrdinalIgnoreCase);
                bool needsInput = Extract("needs_input") == "1" ||
                                  status.Equals("Needs input", StringComparison.OrdinalIgnoreCase);
                if (string.IsNullOrEmpty(name)) name = "Free Buff";
                if (string.IsNullOrEmpty(status)) status = needsInput ? "Needs input" : (almost ? "Almost done" : "Working");

                WindowsSystemHooks.UpsertAgentSession(state, name, step, status, progress, almost, needsInput);
                outJson = "{\"ok\":true,\"endpoint\":\"agent\"}";
                return true;
            }

            outJson = "{\"app\":\"OpenPixel\",\"version\":\"0.5.126\",\"loopback\":true}";
            return true;
        }

        // ====================================================================
        // Unified PC Deck Protocol Dispatcher (HTTPS TLS + HTTP/Probe + Unit Testable)
        // ====================================================================
        public static (string Status, string ContentType, byte[] Body, string ExtraHeaders) ProcessDeckOrShelfRequest(
            GlobalAppState state, string rawRequest, string remoteIp)
        {
            int lineEnd = rawRequest.IndexOf("\r\n", StringComparison.Ordinal);
            string firstLine = lineEnd > 0 ? rawRequest.Substring(0, lineEnd) : rawRequest;

            string ExtractParam(string key)
            {
                string pat = key + "=";
                int idx = rawRequest.IndexOf(pat, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return "";
                idx += pat.Length;
                int end = idx;
                while (end < rawRequest.Length && rawRequest[end] != '&' && rawRequest[end] != ' ' &&
                       rawRequest[end] != '\r' && rawRequest[end] != '\n') ++end;
                return Uri.UnescapeDataString(rawRequest.Substring(idx, end - idx).Replace('+', ' '));
            }

            // 1. Send-to-Phone Wi-Fi File Shelf (No phone app or account required)
            if (firstLine.Contains("/shelf", StringComparison.OrdinalIgnoreCase))
            {
                var sb = new StringBuilder();
                sb.Append("<!DOCTYPE html><html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>OpenPixel Shelf</title>");
                sb.Append("<style>body{background:#0b0b0c;color:#efede8;font-family:system-ui,sans-serif;padding:20px}a{display:block;background:#18181b;color:#efede8;padding:16px;border-radius:14px;margin-bottom:10px;text-decoration:none}</style></head><body><h2>OpenPixel Shelf &middot; Files to your phone</h2>");
                lock (state.SyncRoot)
                {
                    for (int i = 0; i < state.ShelfFiles.Count; ++i)
                    {
                        string fn = WebUtility.HtmlEncode(state.ShelfFiles[i].DisplayName);
                        sb.Append($"<a href='/download/{i}' download='{fn}'><strong>{fn}</strong></a>");
                    }
                }
                sb.Append("</body></html>");
                return ("200 OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(sb.ToString()), "");
            }
            if (firstLine.Contains("/download/", StringComparison.OrdinalIgnoreCase))
            {
                int p = firstLine.IndexOf("/download/", StringComparison.OrdinalIgnoreCase) + 10;
                string numStr = firstLine.Substring(p).Split(' ')[0];
                string fPath = "", fName = "";
                if (int.TryParse(numStr, out int idx))
                {
                    lock (state.SyncRoot)
                    {
                        if (idx >= 0 && idx < state.ShelfFiles.Count)
                        {
                            fPath = state.ShelfFiles[idx].FullPath;
                            fName = state.ShelfFiles[idx].DisplayName;
                        }
                    }
                }
                if (!string.IsNullOrEmpty(fPath) && File.Exists(fPath))
                {
                    return ("200 OK", "application/octet-stream", File.ReadAllBytes(fPath), $"Content-Disposition: attachment; filename=\"{fName}\"\r\n");
                }
                return ("404 Not Found", "text/plain", Encoding.UTF8.GetBytes("File not found"), "");
            }

            // 2. PC Deck Enforcement: Off by default and disabled on Public networks
            if (!state.Settings.PcDeckEnabled || !state.IsNetworkPrivate)
            {
                return ("403 Forbidden", "application/json",
                        Encoding.UTF8.GetBytes("{\"error\":\"PC Deck is off or network is Public\"}"), "");
            }

            // 3. IP Auto-Rediscovery Probe (/deck/probe)
            if (firstLine.Contains("/deck/probe", StringComparison.OrdinalIgnoreCase))
            {
                EnsurePcTlsCertificate(state);
                string probeJson = JsonSerializer.Serialize(new
                {
                    pcName = Environment.MachineName,
                    ip = state.LocalPrivateIp,
                    tlsPort = state.Settings.PcDeckTlsPort,
                    certFingerprint = state.PcCertFingerprint,
                    mac = state.LocalMacAddress
                });
                return ("200 OK", "application/json", Encoding.UTF8.GetBytes(probeJson), "");
            }

            // 4. Pairing Endpoint (/deck/pair) with 2-minute code + Explicit PC "Allow" Prompt + Key Fingerprint
            if (firstLine.Contains("/deck/pair", StringComparison.OrdinalIgnoreCase))
            {
                string code = ExtractParam("code");
                string deviceName = ExtractParam("device");
                string model = ExtractParam("model");
                string osVer = ExtractParam("os");
                string appVer = ExtractParam("app");
                if (string.IsNullOrEmpty(deviceName)) deviceName = "Android Phone";
                if (string.IsNullOrEmpty(model)) model = deviceName;
                if (string.IsNullOrEmpty(osVer)) osVer = "Android";
                if (string.IsNullOrEmpty(appVer)) appVer = "1.0.5";

                bool validCode;
                lock (state.SyncRoot)
                {
                    validCode = !string.IsNullOrEmpty(state.ActivePairingCode) &&
                                ConfigManager.GetTickMs() <= state.PairingCodeExpireMs &&
                                state.ActivePairingCode == code;
                }
                if (!validCode)
                {
                    return ("401 Unauthorized", "application/json",
                            Encoding.UTF8.GetBytes("{\"paired\":false,\"error\":\"Invalid or expired 2-minute code\"}"), "");
                }

                bool allowed = SkipInteractiveAllowPromptForTest;
                if (!allowed)
                {
                    DialogResult res = MessageBox.Show(
                        $"Allow \"{deviceName}\" ({model}, {osVer}) to control this PC via OpenPixel Deck over Private Wi-Fi?",
                        "OpenPixel PC Deck Pairing Request",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question,
                        MessageBoxDefaultButton.Button2,
                        MessageBoxOptions.ServiceNotification);
                    allowed = (res == DialogResult.Yes);
                }

                if (!allowed)
                {
                    return ("403 Forbidden", "application/json",
                            Encoding.UTF8.GetBytes("{\"paired\":false,\"error\":\"Denied on PC\"}"), "");
                }

                byte[] keyBytes = RandomNumberGenerator.GetBytes(32);
                string token = Convert.ToBase64String(keyBytes);
                string keyFp = Convert.ToHexString(SHA256.HashData(keyBytes)).Substring(0, 16).ToLowerInvariant();
                EnsurePcTlsCertificate(state);

                lock (state.SyncRoot)
                {
                    state.PairedDeckDevices.Add(new PairedDeckDevice
                    {
                        DeviceId = Guid.NewGuid().ToString("N"),
                        DeviceName = deviceName,
                        Model = model,
                        OsVersion = osVer,
                        AppVersion = appVer,
                        IpAddress = remoteIp,
                        KeyFingerprint = keyFp,
                        AuthToken = token,
                        PairedAtMs = ConfigManager.GetTickMs()
                    });
                    // Single-use pairing code consumed
                    state.ActivePairingCode = "";
                }

                string resp = JsonSerializer.Serialize(new
                {
                    paired = true,
                    token,
                    keyFingerprint = keyFp,
                    certFingerprint = state.PcCertFingerprint,
                    mac = state.LocalMacAddress,
                    pcName = Environment.MachineName
                });
                return ("200 OK", "application/json", Encoding.UTF8.GetBytes(resp), "");
            }

            // 5. Authenticate subsequent commands using paired device token (when paired devices exist)
            string authToken = ExtractParam("token");
            bool tokenAccepted = false;
            lock (state.SyncRoot)
            {
                if (state.PairedDeckDevices.Exists(d => d.AuthToken == authToken))
                {
                    tokenAccepted = true;
                }
            }
            if (!tokenAccepted && !firstLine.Contains("/deck/ui", StringComparison.OrdinalIgnoreCase))
            {
                return ("401 Unauthorized", "application/json",
                        Encoding.UTF8.GetBytes("{\"error\":\"Device not paired or revoked\"}"), "");
            }

            // 6. Live State Endpoint (/deck/state)
            if (firstLine.Contains("/deck/state", StringComparison.OrdinalIgnoreCase))
            {
                string json;
                lock (state.SyncRoot)
                {
                    var apps = new List<object>();
                    for (int i = 0; i < 6; ++i)
                    {
                        var slot = state.LauncherSlots[i];
                        apps.Add(new
                        {
                            index = i,
                            label = slot.Label,
                            target = slot.TargetPath,
                            configured = !string.IsNullOrEmpty(slot.TargetPath)
                        });
                    }
                    json = JsonSerializer.Serialize(new
                    {
                        pcName = Environment.MachineName,
                        cpu = (int)state.Telemetry.CpuPercent,
                        gpu = (int)state.Telemetry.GpuPercent,
                        ram = (int)state.Telemetry.RamPercent,
                        cpuTempAvailable = state.Telemetry.CpuTempAvailable,
                        cpuTempC = (int)state.Telemetry.CpuTempC,
                        gpuTempAvailable = state.Telemetry.GpuTempAvailable,
                        gpuTempC = (int)state.Telemetry.GpuTempC,
                        recording = state.IsRecordingScreen,
                        recordingSec = state.IsRecordingScreen ? (int)((ConfigManager.GetTickMs() - state.RecordingStartMs) / 1000) : 0,
                        micMuted = state.GlobalMicMuted,
                        volume = (int)state.MasterVolume,
                        muted = state.MasterOutputMuted,
                        media = new
                        {
                            hasSession = state.Media.HasSession,
                            isPlaying = state.Media.IsPlaying,
                            title = state.Media.Title,
                            artist = state.Media.Artist,
                            app = state.Media.SourceAppName,
                            posSec = (int)state.Media.PositionSec,
                            durSec = (int)state.Media.DurationSec,
                            accentHex = $"#{state.Media.DominantColor.R:X2}{state.Media.DominantColor.G:X2}{state.Media.DominantColor.B:X2}"
                        },
                        apps
                    });
                }
                return ("200 OK", "application/json", Encoding.UTF8.GetBytes(json), "");
            }

            // 7. Pad Endpoint (/deck/pad)
            if (firstLine.Contains("/deck/pad", StringComparison.OrdinalIgnoreCase))
            {
                string act = ExtractParam("a");
                if (act == "move")
                {
                    int dx = int.TryParse(ExtractParam("dx"), out int x) ? x : 0;
                    int dy = int.TryParse(ExtractParam("dy"), out int y) ? y : 0;
                    var pt = Cursor.Position;
                    Cursor.Position = new System.Drawing.Point(pt.X + dx, pt.Y + dy);
                }
                else if (act == "lclick") SendMouseFlag(0x0002 | 0x0004);
                else if (act == "rclick") SendMouseFlag(0x0008 | 0x0010);
                else if (act == "drag_start") SendMouseFlag(0x0002);
                else if (act == "drag_end") SendMouseFlag(0x0004);
                else if (act == "scroll")
                {
                    int dy = int.TryParse(ExtractParam("dy"), out int s) ? s : 0;
                    mouse_event(0x0800, 0, 0, dy * 120, UIntPtr.Zero);
                }
                else if (act == "type")
                {
                    string txt = ExtractParam("text");
                    if (!string.IsNullOrEmpty(txt))
                    {
                        try { SendKeys.SendWait(txt); } catch { }
                    }
                }
                else if (act == "copy") SendCtrlKey(0x43);
                else if (act == "paste") SendCtrlKey(0x56);
                else if (act == "undo") SendCtrlKey(0x5A);

                return ("200 OK", "application/json", Encoding.UTF8.GetBytes("{\"ok\":true}"), "");
            }

            // 8. Media Endpoint (/deck/media)
            if (firstLine.Contains("/deck/media", StringComparison.OrdinalIgnoreCase))
            {
                string act = ExtractParam("a");
                if (act == "play") WindowsSystemHooks.MediaTogglePlayPause(state);
                else if (act == "next") WindowsSystemHooks.MediaNextTrack(state);
                else if (act == "prev") WindowsSystemHooks.MediaPreviousTrack(state);
                else if (act == "vol" && float.TryParse(ExtractParam("v"), out float v)) WindowsSystemHooks.SetMasterVolume(state, v, showPopup: true);
                else if (act == "mute") WindowsSystemHooks.SetMasterVolume(state, state.MasterOutputMuted ? 50.0f : 0.0f, showPopup: true);
                else if (act == "seek" && double.TryParse(ExtractParam("v"), out double pos))
                {
                    lock (state.SyncRoot) state.Media.PositionSec = Math.Clamp(pos, 0.0, Math.Max(1.0, state.Media.DurationSec));
                }
                return ("200 OK", "application/json", Encoding.UTF8.GetBytes("{\"ok\":true}"), "");
            }

            // 9. Apps Endpoint (/deck/app)
            if (firstLine.Contains("/deck/app", StringComparison.OrdinalIgnoreCase))
            {
                string act = ExtractParam("a");
                if (act == "add")
                {
                    string label = ExtractParam("label");
                    string path = ExtractParam("path");
                    lock (state.SyncRoot)
                    {
                        for (int i = 0; i < 6; ++i)
                        {
                            if (string.IsNullOrEmpty(state.LauncherSlots[i].TargetPath))
                            {
                                state.LauncherSlots[i].Label = string.IsNullOrEmpty(label) ? Path.GetFileNameWithoutExtension(path) : label;
                                state.LauncherSlots[i].TargetPath = path;
                                break;
                            }
                        }
                    }
                    ConfigManager.SaveAtomic(state);
                }
                else if (int.TryParse(ExtractParam("idx"), out int slotIdx) && slotIdx >= 0 && slotIdx < 6)
                {
                    string target;
                    lock (state.SyncRoot) target = state.LauncherSlots[slotIdx].TargetPath;
                    if (!string.IsNullOrEmpty(target))
                    {
                        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
                    }
                }
                return ("200 OK", "application/json", Encoding.UTF8.GetBytes("{\"ok\":true}"), "");
            }

            // 10. Modes Endpoint (/deck/mode — Game, Present/Slides, Movie, Work)
            if (firstLine.Contains("/deck/mode", StringComparison.OrdinalIgnoreCase))
            {
                string act = ExtractParam("a");
                switch (act)
                {
                    case "gamebar":
                        Win32Layered.keybd_event(0x5B, 0, 0, UIntPtr.Zero);
                        Win32Layered.keybd_event((byte)'G', 0, 0, UIntPtr.Zero);
                        Win32Layered.keybd_event((byte)'G', 0, 2, UIntPtr.Zero);
                        Win32Layered.keybd_event(0x5B, 0, 2, UIntPtr.Zero);
                        break;
                    case "screenshot":
                        WindowsSystemHooks.CaptureFullScreenshot(state);
                        break;
                    case "clip":
                        // Xbox Game Bar: save the last 30 seconds.
                        Win32Layered.keybd_event(0x5B, 0, 0, UIntPtr.Zero);
                        Win32Layered.keybd_event(0x12, 0, 0, UIntPtr.Zero);
                        Win32Layered.keybd_event((byte)'G', 0, 0, UIntPtr.Zero);
                        Win32Layered.keybd_event((byte)'G', 0, 2, UIntPtr.Zero);
                        Win32Layered.keybd_event(0x12, 0, 2, UIntPtr.Zero);
                        Win32Layered.keybd_event(0x5B, 0, 2, UIntPtr.Zero);
                        break;
                    case "record":
                        lock (state.SyncRoot)
                        {
                            state.IsRecordingScreen = !state.IsRecordingScreen;
                            state.RecordingStartMs = ConfigManager.GetTickMs();
                            state.RecSpring.SetTarget(state.IsRecordingScreen ? 100.0f : 0.0f, state.Settings.ReduceMotion);
                        }
                        break;
                    case "switch":
                        // Alt+Tab window switch.
                        Win32Layered.keybd_event(0x12, 0, 0, UIntPtr.Zero);
                        Win32Layered.keybd_event(0x09, 0, 0, UIntPtr.Zero);
                        Win32Layered.keybd_event(0x09, 0, 2, UIntPtr.Zero);
                        Win32Layered.keybd_event(0x12, 0, 2, UIntPtr.Zero);
                        break;
                    case "mic":
                        WindowsSystemHooks.ToggleGlobalMicMute(state);
                        break;
                    case "slide_start": TapKey(0x74); break; // F5
                    case "slide_next":  TapKey(0x27); break; // Right
                    case "slide_prev":  TapKey(0x25); break; // Left
                    case "slide_black": TapKey((byte)'B'); break;
                    case "slide_end":   TapKey(0x1B); break; // Esc
                    case "movie_play":  TapKey(0x20); break; // Space
                    case "movie_back":  TapKey((byte)'J'); break;
                    case "movie_fwd":   TapKey((byte)'L'); break;
                    case "movie_full":  TapKey((byte)'F'); break;
                    case "movie_mute":  TapKey((byte)'M'); break;
                    case "work_copy":   SendCtrlKey(0x43); break;
                    case "work_paste":  SendCtrlKey(0x56); break;
                    case "work_undo":   SendCtrlKey(0x5A); break;
                }
                return ("200 OK", "application/json", Encoding.UTF8.GetBytes("{\"ok\":true}"), "");
            }

            // 11. PC Power Endpoint (/deck/power — Lock, Sleep, Restart/Shutdown 30s grace)
            if (firstLine.Contains("/deck/power", StringComparison.OrdinalIgnoreCase))
            {
                string act = ExtractParam("a");
                if (act == "lock") Win32Layered.LockWorkStation();
                else if (act == "sleep") Application.SetSuspendState(PowerState.Suspend, false, false);
                else if (act == "restart") Process.Start(new ProcessStartInfo("shutdown", "/r /t 30") { CreateNoWindow = true, UseShellExecute = false });
                else if (act == "shutdown") Process.Start(new ProcessStartInfo("shutdown", "/s /t 30") { CreateNoWindow = true, UseShellExecute = false });
                return ("200 OK", "application/json", Encoding.UTF8.GetBytes("{\"ok\":true}"), "");
            }

            return ("200 OK", "application/json", Encoding.UTF8.GetBytes("{\"ok\":true,\"service\":\"OpenPixelDeck\"}"), "");
        }

        public static void Start(GlobalAppState state)
        {
            if (_running) return;
            _running = true;
            DetectPrivateNetworkAndLocalIp(state);
            EnsurePcTlsCertificate(state);
            GenerateTwoMinutePairingCode(state);

            if (state.Settings.LoopbackApiEnabled)
            {
                new Thread(() => RunLoopbackServer(state)) { IsBackground = true }.Start();
            }
            new Thread(() => RunHttpDeckAndShelfServer(state)) { IsBackground = true }.Start();
            new Thread(() => RunTlsDeckServer(state)) { IsBackground = true }.Start();
            new Thread(() => RunUdpDiscoveryResponder(state)) { IsBackground = true }.Start();
        }

        public static void Stop()
        {
            _running = false;
            try { _loopbackListener?.Stop(); } catch { }
            try { _deckHttpListener?.Stop(); } catch { }
            try { _deckTlsListener?.Stop(); } catch { }
            try { _udpDiscovery?.Close(); } catch { }
        }

        private static void RunLoopbackServer(GlobalAppState state)
        {
            try
            {
                _loopbackListener = new TcpListener(IPAddress.Loopback, state.Settings.LoopbackApiPort);
                _loopbackListener.Start();
                while (_running)
                {
                    using TcpClient client = _loopbackListener.AcceptTcpClient();
                    using NetworkStream ns = client.GetStream();
                    byte[] buf = new byte[4096];
                    int n = ns.Read(buf, 0, buf.Length);
                    if (n > 0)
                    {
                        HandleLoopbackRequest(state, Encoding.UTF8.GetString(buf, 0, n), out string json);
                        WriteHttpResponse(ns, "200 OK", "application/json", Encoding.UTF8.GetBytes(json));
                    }
                }
            }
            catch { }
        }

        private static void RunHttpDeckAndShelfServer(GlobalAppState state)
        {
            try
            {
                _deckHttpListener = new TcpListener(IPAddress.Any, state.Settings.PcDeckPort);
                _deckHttpListener.Start();
                while (_running)
                {
                    using TcpClient client = _deckHttpListener.AcceptTcpClient();
                    using NetworkStream ns = client.GetStream();
                    byte[] buf = new byte[4096];
                    int n = ns.Read(buf, 0, buf.Length);
                    if (n <= 0) continue;
                    string ip = ((IPEndPoint)client.Client.RemoteEndPoint)?.Address.ToString() ?? "";
                    var (st, ct, body, extra) = ProcessDeckOrShelfRequest(state, Encoding.UTF8.GetString(buf, 0, n), ip);
                    WriteHttpResponse(ns, st, ct, body, extra);
                }
            }
            catch { }
        }

        private static void RunTlsDeckServer(GlobalAppState state)
        {
            try
            {
                var cert = EnsurePcTlsCertificate(state);
                if (cert == null) return;
                _deckTlsListener = new TcpListener(IPAddress.Any, state.Settings.PcDeckTlsPort);
                _deckTlsListener.Start();
                while (_running)
                {
                    TcpClient client = _deckTlsListener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try
                        {
                            using (client)
                            using (var ssl = new SslStream(client.GetStream(), false))
                            {
                                ssl.AuthenticateAsServer(cert, clientCertificateRequired: false, checkCertificateRevocation: false);
                                byte[] buf = new byte[8192];
                                int n = ssl.Read(buf, 0, buf.Length);
                                if (n > 0)
                                {
                                    string ip = ((IPEndPoint)client.Client.RemoteEndPoint)?.Address.ToString() ?? "";
                                    var (st, ct, body, extra) = ProcessDeckOrShelfRequest(state, Encoding.UTF8.GetString(buf, 0, n), ip);
                                    WriteHttpResponse(ssl, st, ct, body, extra);
                                }
                            }
                        }
                        catch { }
                    });
                }
            }
            catch { }
        }

        private static void RunUdpDiscoveryResponder(GlobalAppState state)
        {
            try
            {
                _udpDiscovery = new UdpClient(47826);
                while (_running)
                {
                    IPEndPoint ep = new(IPAddress.Any, 0);
                    byte[] req = _udpDiscovery.Receive(ref ep);
                    string msg = Encoding.UTF8.GetString(req);
                    if (msg.StartsWith("OPENPIXEL_DECK_DISCOVER") && state.Settings.PcDeckEnabled && state.IsNetworkPrivate)
                    {
                        EnsurePcTlsCertificate(state);
                        string reply = $"{{\"ip\":\"{state.LocalPrivateIp}\",\"tlsPort\":{state.Settings.PcDeckTlsPort},\"cert\":\"{state.PcCertFingerprint}\",\"mac\":\"{state.LocalMacAddress}\",\"pc\":\"{Environment.MachineName}\"}}";
                        byte[] resp = Encoding.UTF8.GetBytes(reply);
                        _udpDiscovery.Send(resp, resp.Length, ep);
                    }
                }
            }
            catch { }
        }

        private static void WriteHttpResponse(Stream s, string status, string contentType, byte[] body, string extraHeaders = "")
        {
            string hdr = $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n{extraHeaders}\r\n";
            byte[] hBytes = Encoding.UTF8.GetBytes(hdr);
            s.Write(hBytes, 0, hBytes.Length);
            s.Write(body, 0, body.Length);
            s.Flush();
        }

        private static void SendMouseFlag(uint flags) => mouse_event(flags, 0, 0, 0, UIntPtr.Zero);
        private static void TapKey(byte vk) { Win32Layered.keybd_event(vk, 0, 0, UIntPtr.Zero); Win32Layered.keybd_event(vk, 0, 2, UIntPtr.Zero); }
        private static void SendCtrlKey(byte vk)
        {
            Win32Layered.keybd_event(0x11, 0, 0, UIntPtr.Zero);
            TapKey(vk);
            Win32Layered.keybd_event(0x11, 0, 2, UIntPtr.Zero);
        }

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, int dx, int dy, int dwData, UIntPtr dwExtraInfo);
    }
}
