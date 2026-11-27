using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;
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
#nullable enable
        public static Func<string, string, string, string, bool>? ApprovalHandlerForTest { get; set; }
        public static Func<string, bool>? PowerActionHandlerForTest { get; set; }
#nullable restore

        private static TcpListener _loopbackListener;
        private static TcpListener _deckHttpListener;
        private static TcpListener _deckTlsListener;
        private static UdpClient _udpDiscovery;

        // Deck servers are independent of the loopback server: they must start and
        // stop with the PC Deck toggle instead of listening forever once created.
        private static readonly object _deckLock = new();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<TcpClient, byte> _deckClients = new();
        private static volatile bool _deckEnabled;
        private static int _deckGeneration;
        private static X509Certificate2 _pcCert;
        private static NetworkAddressChangedEventHandler _networkAddressChangedHandler;
        private static long _lastNetRefreshTickMs;
        private static int _failedPairAttempts;
        private static volatile bool _persistenceActive;
        private static int _lastPersistedDeviceCount;
        private static readonly object _persistFileLock = new();
        private static readonly MethodInfo _seekMediaMethod2 = typeof(WindowsSystemHooks).GetMethod(
            "SeekMediaPosition",
            BindingFlags.Public | BindingFlags.Static,
            null,
            new[] { typeof(GlobalAppState), typeof(double) },
            null);
        private static readonly MethodInfo _seekMediaMethod1 = typeof(WindowsSystemHooks).GetMethod(
            "SeekMediaPosition",
            BindingFlags.Public | BindingFlags.Static,
            null,
            new[] { typeof(double) },
            null);

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

        private static readonly object _certLock = new();

        public static X509Certificate2 EnsurePcTlsCertificate(GlobalAppState state)
        {
            lock (_certLock)
            {
                if (_pcCert != null) return _pcCert;
                try
                {
                string certPath = Path.Combine(ConfigManager.GetAppDataDirectory(), "openpixel_deck.pfx");
                TryAdoptLegacyCertificate(certPath);
                if (File.Exists(certPath))
                {
                    try
                    {
                        _pcCert = X509CertificateLoader.LoadPkcs12FromFile(
                            certPath,
                            "openpixel-local",
                            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
                    }
                    catch
                    {
                        // A truncated/corrupt PFX used to disable the deck forever.
                        // Drop it and mint a fresh identity below.
                        try { File.Delete(certPath); } catch { }
                        _pcCert = null;
                    }
                }

                if (_pcCert == null)
                {
                    using RSA rsa = RSA.Create(2048);
                    var req = new CertificateRequest($"CN={Environment.MachineName}.openpixel.local", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
                    using X509Certificate2 ephemeral = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
                    byte[] pfxBytes = ephemeral.Export(X509ContentType.Pfx, "openpixel-local");
                    File.WriteAllBytes(certPath, pfxBytes);
                    _pcCert = X509CertificateLoader.LoadPkcs12(
                        pfxBytes,
                        "openpixel-local",
                        X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
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
        }

        private static bool IsRfc1918Ipv4(IPAddress addr, out string ipStr)
        {
            ipStr = "";
            if (addr == null || addr.AddressFamily != AddressFamily.InterNetwork) return false;
            byte[] b = addr.GetAddressBytes();
            bool rfc = b[0] == 10 ||
                       (b[0] == 172 && (b[1] & 0xF0) == 16) ||
                       (b[0] == 192 && b[1] == 168);
            if (!rfc) return false;
            ipStr = addr.ToString();
            return true;
        }

        private static bool IsVirtualOrTunnelAdapter(NetworkInterface ni)
        {
            string desc = ((ni.Description ?? "") + " " + (ni.Name ?? "")).ToLowerInvariant();
            return desc.Contains("hyper-v") || desc.Contains("wsl") || desc.Contains("vethernet") ||
                   desc.Contains("virtualbox") || desc.Contains("vmware") || desc.Contains("tap-") ||
                   desc.Contains("wintun") || desc.Contains("wireguard") || desc.Contains("zerotier") ||
                   desc.Contains("tailscale") || desc.Contains("tunnel") || desc.Contains("loopback") ||
                   desc.Contains("npcap") || desc.Contains("pseudo");
        }

        private static bool HasValidIpv4Gateway(IPInterfaceProperties props)
        {
            foreach (var gw in props.GatewayAddresses)
            {
                if (gw?.Address != null &&
                    gw.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.Any.Equals(gw.Address) &&
                    !IPAddress.Loopback.Equals(gw.Address))
                {
                    return true;
                }
            }
            return false;
        }

        public static bool DetectPrivateNetworkAndLocalIp(GlobalAppState state)
        {
            string bestIp = "127.0.0.1";
            string bestMac = "00:00:00:00:00:00";
            bool isRfc1918Private = false;

            try
            {
                var nics = NetworkInterface.GetAllNetworkInterfaces();
                int bestPriority = -1;
                foreach (var ni in nics)
                {
                    if (ni.OperationalStatus != OperationalStatus.Up ||
                        ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        continue;

                    IPInterfaceProperties props;
                    try { props = ni.GetIPProperties(); }
                    catch { continue; }

                    bool isPhysicalType = ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ||
                                          ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet;
                    bool isVirtual = IsVirtualOrTunnelAdapter(ni);
                    bool hasGw = HasValidIpv4Gateway(props);

                    foreach (var ua in props.UnicastAddresses)
                    {
                        if (!IsRfc1918Ipv4(ua.Address, out string candidateIp)) continue;

                        int priority = 0;
                        if (isPhysicalType && !isVirtual && hasGw)
                        {
                            priority = ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 4 : 3;
                        }
                        else if (!isVirtual && hasGw)
                        {
                            priority = 2;
                        }
                        else if (isPhysicalType && !isVirtual)
                        {
                            priority = 1;
                        }

                        if (priority > bestPriority)
                        {
                            bestPriority = priority;
                            bestIp = candidateIp;
                            byte[] macBytes = ni.GetPhysicalAddress().GetAddressBytes();
                            if (macBytes.Length == 6)
                            {
                                bestMac = string.Join(":", Array.ConvertAll(macBytes, x => x.ToString("X2")));
                            }
                            isRfc1918Private = true;
                        }
                        break;
                    }
                }
            }
            catch { }

            // Check ONLY currently connected networks via COM CLSID_NetworkListManager.
            // Require at least one connected Private (1) or DomainAuthenticated (2) network
            // AND zero connected Public (0) networks. Fail closed on COM error.
            bool nlmPrivateOk = false;
            try
            {
                Type nlmType = Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"));
                if (nlmType != null)
                {
                    object nlmObj = Activator.CreateInstance(nlmType);
                    if (nlmObj != null)
                    {
                        bool hasPrivateOrDomain = false;
                        bool hasPublic = false;
                        dynamic nlm = nlmObj;
                        dynamic networks = nlm.GetNetworks(1); // NLM_ENUM_NETWORK_CONNECTED
                        foreach (dynamic net in networks)
                        {
                            int cat = Convert.ToInt32(net.GetCategory());
                            if (cat == 0) hasPublic = true;
                            else if (cat == 1 || cat == 2) hasPrivateOrDomain = true;
                        }
                        nlmPrivateOk = hasPrivateOrDomain && !hasPublic;
                    }
                }
            }
            catch
            {
                nlmPrivateOk = false;
            }

            Volatile.Write(ref _lastNetRefreshTickMs, Environment.TickCount64);
            bool isPrivate = isRfc1918Private && nlmPrivateOk;
            lock (state.SyncRoot)
            {
                state.LocalPrivateIp = bestIp;
                state.LocalMacAddress = bestMac;
                state.IsNetworkPrivate = isPrivate;
            }

            // A public profile does not rewrite the user's toggle; every deck
            // request is already gated on IsNetworkPrivate and answers 403.
            return isPrivate;
        }

        private static void RefreshNetworkStateIfStale(GlobalAppState state)
        {
            long now = Environment.TickCount64;
            long prev = Volatile.Read(ref _lastNetRefreshTickMs);
            if (now - prev >= 2000)
            {
                DetectPrivateNetworkAndLocalIp(state);
            }
        }

        public static string GenerateTwoMinutePairingCode(GlobalAppState state)
        {
            int code = RandomNumberGenerator.GetInt32(100000, 1000000);
            string codeStr = code.ToString();
            Interlocked.Exchange(ref _failedPairAttempts, 0);
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
                // lets the code stay at a large, scannable module size (<= 213 bytes).
                return $"notchpixel-deck://pair?ip={state.LocalPrivateIp}&port={state.Settings.PcDeckTlsPort}&code={state.ActivePairingCode}&cert={state.PcCertFingerprint}";
            }
        }

        public static bool TryBuildUdpDiscoveryReply(GlobalAppState state, string msg, out string replyJson)
        {
            replyJson = "";
            if (state == null || string.IsNullOrEmpty(msg)) return false;
            if (!msg.StartsWith("OPENPIXEL_DECK_DISCOVER", StringComparison.Ordinal) &&
                !msg.StartsWith("NOTCHPIXEL_DECK_DISCOVER", StringComparison.Ordinal))
            {
                return false;
            }

            bool allowed;
            lock (state.SyncRoot)
            {
                allowed = state.Settings.PcDeckEnabled && state.IsNetworkPrivate;
            }
            if (!allowed) return false;

            EnsurePcTlsCertificate(state);
            lock (state.SyncRoot)
            {
                replyJson = JsonSerializer.Serialize(new
                {
                    ip = state.LocalPrivateIp,
                    tlsPort = state.Settings.PcDeckTlsPort,
                    certFingerprint = state.PcCertFingerprint,
                    mac = state.LocalMacAddress,
                    pcName = Environment.MachineName
                });
            }
            return true;
        }

        private static string GetPairedDevicesFilePath()
        {
            return Path.Combine(ConfigManager.GetAppDataDirectory(), "paired_deck_devices.json");
        }

        public static void LoadPairedDevices(GlobalAppState state)
        {
            if (state == null) return;
            try
            {
                string path = GetPairedDevicesFilePath();
                lock (_persistFileLock)
                {
                    if (File.Exists(path))
                    {
                        string json = File.ReadAllText(path, Encoding.UTF8);
                        var list = JsonSerializer.Deserialize<List<PairedDeckDevice>>(json);
                        lock (state.SyncRoot)
                        {
                            state.PairedDeckDevices.Clear();
                            if (list != null)
                            {
                                foreach (var d in list)
                                {
                                    if (d != null && !string.IsNullOrEmpty(d.AuthToken))
                                    {
                                        state.PairedDeckDevices.Add(d);
                                    }
                                }
                            }
                            _lastPersistedDeviceCount = state.PairedDeckDevices.Count;
                        }
                    }
                    else
                    {
                        lock (state.SyncRoot)
                        {
                            _lastPersistedDeviceCount = state.PairedDeckDevices.Count;
                        }
                    }
                    _persistenceActive = true;
                }
            }
            catch { }
        }

        public static void SavePairedDevices(GlobalAppState state)
        {
            if (state == null || SkipInteractiveAllowPromptForTest) return;
            try
            {
                string json;
                int count;
                lock (state.SyncRoot)
                {
                    count = state.PairedDeckDevices.Count;
                    json = JsonSerializer.Serialize(state.PairedDeckDevices, new JsonSerializerOptions { WriteIndented = true });
                }

                string finalPath = GetPairedDevicesFilePath();
                string dir = Path.GetDirectoryName(finalPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                string tmpPath = finalPath + ".tmp";

                lock (_persistFileLock)
                {
                    File.WriteAllText(tmpPath, json, Encoding.UTF8);
                    File.Move(tmpPath, finalPath, overwrite: true);
                    _lastPersistedDeviceCount = count;
                    _persistenceActive = true;
                }
            }
            catch { }
        }

        private static void FlushRevokedDevicesIfCleared(GlobalAppState state)
        {
            if (!_persistenceActive || SkipInteractiveAllowPromptForTest) return;
            bool needFlush;
            lock (state.SyncRoot)
            {
                needFlush = _lastPersistedDeviceCount > 0 && state.PairedDeckDevices.Count == 0;
            }
            if (needFlush)
            {
                SavePairedDevices(state);
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

        private sealed class ParsedHttpRequest
        {
            public string Method = "GET";
            public string Path = "/";
            public Dictionary<string, string> Params = new(StringComparer.OrdinalIgnoreCase);
            public string BearerToken = "";
        }

        private static ParsedHttpRequest ParseHttpRequest(string rawRequest)
        {
            var req = new ParsedHttpRequest();
            if (string.IsNullOrEmpty(rawRequest)) return req;

            int headEnd = rawRequest.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            int sepLen = 4;
            if (headEnd < 0)
            {
                headEnd = rawRequest.IndexOf("\n\n", StringComparison.Ordinal);
                sepLen = 2;
            }
            string head = headEnd >= 0 ? rawRequest.Substring(0, headEnd) : rawRequest;
            string body = headEnd >= 0 ? rawRequest.Substring(headEnd + sepLen) : "";

            string[] lines = head.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            string firstLine = lines.Length > 0 ? lines[0].Trim() : "";
            string[] parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string target = parts.Length >= 2 ? parts[1] : (parts.Length == 1 ? parts[0] : "/");
            if (parts.Length >= 2) req.Method = parts[0].ToUpperInvariant();

            int qIdx = target.IndexOf('?');
            string queryStr = "";
            if (qIdx >= 0)
            {
                req.Path = target.Substring(0, qIdx);
                queryStr = target.Substring(qIdx + 1);
            }
            else
            {
                req.Path = target;
            }

            static void ParseFormPairs(string s, Dictionary<string, string> dest)
            {
                if (string.IsNullOrEmpty(s)) return;
                foreach (string pair in s.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = pair.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = Uri.UnescapeDataString(pair.Substring(0, eq).Replace('+', ' '));
                    string v = Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' '));
                    if (!string.IsNullOrEmpty(k)) dest[k] = v;
                }
            }

            string contentType = "";
            for (int i = 1; i < lines.Length; ++i)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0) continue;
                string hName = lines[i].Substring(0, colon).Trim();
                string hVal = lines[i].Substring(colon + 1).Trim();
                if (hName.Equals("Authorization", StringComparison.OrdinalIgnoreCase) &&
                    hVal.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    req.BearerToken = hVal.Substring(7).Trim();
                }
                else if (hName.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    contentType = hVal;
                }
            }

            ParseFormPairs(queryStr, req.Params);
            if (!string.IsNullOrWhiteSpace(body) &&
                (contentType.Contains("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) ||
                 (string.IsNullOrEmpty(contentType) && body.Contains('='))))
            {
                ParseFormPairs(body.Trim(), req.Params);
            }

            return req;
        }

        private static string SanitizeAsciiFilename(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "file";
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (c >= 32 && c < 127 && c != '"' && c != '\\' && c != '/' && c != ';')
                    sb.Append(c);
                else if (c >= 127)
                    sb.Append('_');
            }
            string s = sb.ToString().Trim();
            return string.IsNullOrEmpty(s) ? "file" : s;
        }

        private static bool ShowInteractiveApprovalDialog(string deviceName, string model, string osVer, string remoteIp)
        {
            bool approved = false;
            var thread = new Thread(() =>
            {
                try
                {
                    using var form = new Form
                    {
                        Text = "NotchPixel Deck — Pairing Request",
                        Width = 420,
                        Height = 230,
                        FormBorderStyle = FormBorderStyle.FixedDialog,
                        StartPosition = FormStartPosition.CenterScreen,
                        TopMost = true,
                        MaximizeBox = false,
                        MinimizeBox = false,
                        ControlBox = false,
                        BackColor = Color.FromArgb(18, 20, 26),
                        ForeColor = Color.FromArgb(242, 244, 248)
                    };

                    var lblTitle = new Label
                    {
                        Text = "Allow new NotchPixel Deck device?",
                        Left = 20,
                        Top = 18,
                        Width = 370,
                        Height = 26,
                        Font = new Font("Segoe UI", 11.5f, FontStyle.Bold),
                        ForeColor = Color.FromArgb(43, 228, 234)
                    };

                    var lblBody = new Label
                    {
                        Text = $"Device: {deviceName}\nModel: {model}\nOS: {osVer}\nRemote IP: {remoteIp}",
                        Left = 20,
                        Top = 52,
                        Width = 370,
                        Height = 82,
                        Font = new Font("Segoe UI", 9.5f),
                        ForeColor = Color.FromArgb(220, 226, 235)
                    };

                    int secondsLeft = 28;
                    var btnAllow = new Button
                    {
                        Text = "Allow",
                        Left = 200,
                        Top = 144,
                        Width = 90,
                        Height = 34,
                        FlatStyle = FlatStyle.Flat,
                        BackColor = Color.FromArgb(43, 228, 234),
                        ForeColor = Color.FromArgb(10, 14, 18),
                        Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                        DialogResult = DialogResult.Yes
                    };
                    btnAllow.FlatAppearance.BorderSize = 0;

                    var btnDeny = new Button
                    {
                        Text = $"Deny ({secondsLeft}s)",
                        Left = 300,
                        Top = 144,
                        Width = 90,
                        Height = 34,
                        FlatStyle = FlatStyle.Flat,
                        BackColor = Color.FromArgb(44, 48, 58),
                        ForeColor = Color.White,
                        Font = new Font("Segoe UI", 9.5f),
                        DialogResult = DialogResult.No
                    };
                    btnDeny.FlatAppearance.BorderSize = 0;

                    using var countdown = new System.Windows.Forms.Timer { Interval = 1000 };
                    countdown.Tick += (_, _) =>
                    {
                        secondsLeft--;
                        if (secondsLeft <= 0)
                        {
                            countdown.Stop();
                            form.DialogResult = DialogResult.No;
                            form.Close();
                        }
                        else
                        {
                            btnDeny.Text = $"Deny ({secondsLeft}s)";
                        }
                    };

                    form.AcceptButton = null;
                    form.CancelButton = btnDeny;
                    form.Controls.Add(lblTitle);
                    form.Controls.Add(lblBody);
                    form.Controls.Add(btnAllow);
                    form.Controls.Add(btnDeny);
                    form.Shown += (_, _) =>
                    {
                        form.TopMost = true;
                        form.Activate();
                        Win32Layered.SetForegroundWindow(form.Handle);
                        countdown.Start();
                    };

                    approved = form.ShowDialog() == DialogResult.Yes;
                }
                catch
                {
                    approved = false;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            thread.Join();
            return approved;
        }

        // ====================================================================
        // Unified PC Deck Protocol Dispatcher (HTTPS TLS + HTTP/Probe + Unit Testable)
        // ====================================================================
        public static (string Status, string ContentType, byte[] Body, string ExtraHeaders) ProcessDeckOrShelfRequest(
            GlobalAppState state, string rawRequest, string remoteIp)
        {
            ParsedHttpRequest req = ParseHttpRequest(rawRequest);
            string path = req.Path;
            string ExtractParam(string key) => req.Params.TryGetValue(key, out string val) ? val : "";

            // 1. Send-to-Phone Wi-Fi File Shelf (No phone app or account required)
            if (path.Equals("/shelf", StringComparison.OrdinalIgnoreCase))
            {
                var sb = new StringBuilder();
                sb.Append("<!DOCTYPE html><html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>NotchPixel Shelf</title>");
                sb.Append("<style>body{background:#0b0b0c;color:#efede8;font-family:system-ui,sans-serif;padding:20px}a{display:block;background:#18181b;color:#efede8;padding:16px;border-radius:14px;margin-bottom:10px;text-decoration:none}</style></head><body><h2>NotchPixel Shelf &middot; Files to your phone</h2>");
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
            if (path.StartsWith("/download/", StringComparison.OrdinalIgnoreCase))
            {
                string numStr = path.Substring(10);
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
                    string safeAscii = SanitizeAsciiFilename(fName);
                    string utf8Encoded = Uri.EscapeDataString(string.IsNullOrEmpty(fName) ? safeAscii : fName);
                    string disp = $"Content-Disposition: attachment; filename=\"{safeAscii}\"; filename*=UTF-8''{utf8Encoded}\r\n";
                    return ("200 OK", "application/octet-stream", File.ReadAllBytes(fPath), disp);
                }
                return ("404 Not Found", "text/plain", Encoding.UTF8.GetBytes("File not found"), "");
            }

            // 2. PC Deck Enforcement: Off by default and disabled on Public networks
            bool deckActive;
            lock (state.SyncRoot)
            {
                deckActive = state.Settings.PcDeckEnabled && state.IsNetworkPrivate;
            }
            if (!deckActive)
            {
                return ("403 Forbidden", "application/json",
                        Encoding.UTF8.GetBytes("{\"error\":\"PC Deck is off or network is Public\"}"), "");
            }

            // 3. IP Auto-Rediscovery Probe (/deck/probe)
            if (path.Equals("/deck/probe", StringComparison.OrdinalIgnoreCase))
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
            if (path.Equals("/deck/pair", StringComparison.OrdinalIgnoreCase))
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
                bool lockedOut = false;
                lock (state.SyncRoot)
                {
                    validCode = !string.IsNullOrEmpty(state.ActivePairingCode) &&
                                ConfigManager.GetTickMs() <= state.PairingCodeExpireMs &&
                                state.ActivePairingCode == code;
                    if (!validCode)
                    {
                        int fails = Interlocked.Increment(ref _failedPairAttempts);
                        if (fails >= 5)
                        {
                            state.ActivePairingCode = "";
                            state.PairingCodeExpireMs = 0;
                            lockedOut = true;
                        }
                    }
                    else
                    {
                        Interlocked.Exchange(ref _failedPairAttempts, 0);
                    }
                }
                if (!validCode)
                {
                    string errMsg = lockedOut
                        ? "{\"paired\":false,\"error\":\"Too many invalid attempts; generate a new code on PC\"}"
                        : "{\"paired\":false,\"error\":\"Invalid or expired 2-minute code\"}";
                    return ("401 Unauthorized", "application/json", Encoding.UTF8.GetBytes(errMsg), "");
                }

                bool allowed;
                if (SkipInteractiveAllowPromptForTest)
                {
                    allowed = true;
                }
                else if (ApprovalHandlerForTest != null)
                {
                    allowed = ApprovalHandlerForTest(deviceName, model, osVer, remoteIp);
                }
                else
                {
                    lock (state.SyncRoot)
                    {
                        state.Notifications.Insert(0, new NotificationItem
                        {
                            Id = ConfigManager.GetTickMs(),
                            AppName = "NotchPixel Deck",
                            Title = $"Pair request: {deviceName}",
                            Body = $"{model} ({osVer}) from {remoteIp}",
                            TimeStr = DateTime.Now.ToString("HH:mm"),
                            RelativeStr = "Just now"
                        });
                        state.ToastPopupType = TopPopupType.Notice;
                        state.ToastTitle = "NotchPixel Deck Pairing";
                        state.ToastSubtitle = $"Approve {deviceName} ({model})?";
                        state.ToastAccent = Color.FromArgb(255, 43, 228, 234);
                        state.ToastIsReminder = false;
                        state.ToastActive = true;
                        state.ToastExpireMs = ConfigManager.GetTickMs() + 10000;
                    }
                    allowed = ShowInteractiveApprovalDialog(deviceName, model, osVer, remoteIp);
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
                SavePairedDevices(state);

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

            // 5. Authenticate subsequent commands using paired device token
            FlushRevokedDevicesIfCleared(state);
            string authToken = ExtractParam("token");
            if (string.IsNullOrEmpty(authToken)) authToken = req.BearerToken;
            bool tokenAccepted = false;
            if (!string.IsNullOrEmpty(authToken))
            {
                lock (state.SyncRoot)
                {
                    if (state.PairedDeckDevices.Exists(d => !string.IsNullOrEmpty(d.AuthToken) && d.AuthToken == authToken))
                    {
                        tokenAccepted = true;
                    }
                }
            }
            if (!tokenAccepted)
            {
                return ("401 Unauthorized", "application/json",
                        Encoding.UTF8.GetBytes("{\"error\":\"Device not paired or revoked\"}"), "");
            }

            // 5b. Paired Devices Metadata (/deck/devices) & Unpair (/deck/unpair)
            if (path.Equals("/deck/devices", StringComparison.OrdinalIgnoreCase))
            {
                string json;
                lock (state.SyncRoot)
                {
                    var list = new List<object>(state.PairedDeckDevices.Count);
                    foreach (var d in state.PairedDeckDevices)
                    {
                        list.Add(new
                        {
                            deviceId = d.DeviceId,
                            deviceName = d.DeviceName,
                            model = d.Model,
                            osVersion = d.OsVersion,
                            appVersion = d.AppVersion,
                            ipAddress = d.IpAddress,
                            keyFingerprint = d.KeyFingerprint,
                            pairedAtMs = d.PairedAtMs,
                            isCurrent = d.AuthToken == authToken
                        });
                    }
                    json = JsonSerializer.Serialize(new { devices = list });
                }
                return ("200 OK", "application/json", Encoding.UTF8.GetBytes(json), "");
            }

            if (path.Equals("/deck/unpair", StringComparison.OrdinalIgnoreCase))
            {
                string targetDeviceId = ExtractParam("deviceId");
                string targetKeyFp = ExtractParam("keyFingerprint");
                int removed = 0;
                int remaining = 0;
                lock (state.SyncRoot)
                {
                    if (!string.IsNullOrEmpty(targetDeviceId))
                    {
                        removed = state.PairedDeckDevices.RemoveAll(d => string.Equals(d.DeviceId, targetDeviceId, StringComparison.OrdinalIgnoreCase));
                    }
                    else if (!string.IsNullOrEmpty(targetKeyFp))
                    {
                        removed = state.PairedDeckDevices.RemoveAll(d => string.Equals(d.KeyFingerprint, targetKeyFp, StringComparison.OrdinalIgnoreCase));
                    }
                    else
                    {
                        removed = state.PairedDeckDevices.RemoveAll(d => d.AuthToken == authToken);
                    }
                    remaining = state.PairedDeckDevices.Count;
                }
                if (removed > 0)
                {
                    SavePairedDevices(state);
                }
                string resp = JsonSerializer.Serialize(new
                {
                    ok = true,
                    unpaired = removed > 0,
                    remaining
                });
                return ("200 OK", "application/json", Encoding.UTF8.GetBytes(resp), "");
            }

            // 6. Live State Endpoint (/deck/state)
            if (path.Equals("/deck/state", StringComparison.OrdinalIgnoreCase))
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
                            configured = !string.IsNullOrEmpty(slot.TargetPath),
                            iconB64 = EncodeSlotIconPngBase64(slot.IconPath)
                        });
                    }
                    var shelfList = new List<object>();
                    for (int i = 0; i < Math.Min(12, state.ShelfFiles.Count); ++i)
                    {
                        var sf = state.ShelfFiles[i];
                        string extUp = (sf.Extension ?? "").TrimStart('.').ToUpperInvariant();
                        bool isImg = sf.IsImage || extUp == "PNG" || extUp == "JPG" || extUp == "JPEG" || extUp == "BMP" || extUp == "GIF" || extUp == "WEBP";
                        shelfList.Add(new
                        {
                            index = i,
                            name = sf.DisplayName ?? "",
                            ext = extUp,
                            sizeBytes = sf.FileSize,
                            isImage = isImg,
                            thumbB64 = isImg ? EncodeShelfThumbJpegBase64(sf.FullPath) : ""
                        });
                    }
                    var clipList = new List<object>();
                    for (int i = 0; i < Math.Min(12, state.ClipboardHistory.Count); ++i)
                    {
                        var ci = state.ClipboardHistory[i];
                        string txt = ci.Text ?? "";
                        if (txt.Length > 240) txt = txt.Substring(0, 240);
                        clipList.Add(new
                        {
                            index = i,
                            text = txt,
                            pinned = ci.IsPinned
                        });
                    }
                    var todoList = new List<object>();
                    for (int i = 0; i < Math.Min(20, state.TodoList.Count); ++i)
                    {
                        var ti = state.TodoList[i];
                        todoList.Add(new
                        {
                            index = i,
                            text = ti.Text ?? "",
                            done = ti.Done
                        });
                    }
                    string artB64 = EncodeAlbumArtJpegBase64(state.Media.AlbumArt);
                    int audioLvl = Math.Clamp((int)MathF.Round(MathF.Pow(Math.Clamp(state.AudioPeakSmoothed * 3.8f, 0f, 1f), 0.72f) * 100f), 0, 100);
                    ThemeSpec activeTheme = ThemeSpec.Get(state.Settings.Theme);
                    Color activeAccent = state.Settings.AccentColorArgb != 0
                        ? Color.FromArgb(255, Color.FromArgb(state.Settings.AccentColorArgb))
                        : activeTheme.Accent;
                    bool themeIsLight = state.Settings.Theme == SurfaceTheme.PureWhite
                        || state.Settings.Theme == SurfaceTheme.Rose
                        || state.Settings.Theme == SurfaceTheme.Lavender
                        || state.Settings.Theme == SurfaceTheme.Peach
                        || state.Settings.Theme == SurfaceTheme.Sakura;
                    bool themeIsGlass = state.Settings.Theme == SurfaceTheme.LiquidGlass
                        || state.Settings.Theme == SurfaceTheme.CandyGlass
                        || state.Settings.Theme == SurfaceTheme.Acrylic;
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
                        theme = new
                        {
                            name = state.Settings.Theme.ToString(),
                            isLight = themeIsLight,
                            isGlass = themeIsGlass,
                            blurRadius = (int)state.Settings.GlassBlurRadius,
                            accentHex = $"#{activeAccent.R:X2}{activeAccent.G:X2}{activeAccent.B:X2}",
                            surfaceHex = $"#{activeTheme.Fill.R:X2}{activeTheme.Fill.G:X2}{activeTheme.Fill.B:X2}"
                        },
                        media = new
                        {
                            hasSession = state.Media.HasSession,
                            isPlaying = state.Media.IsPlaying,
                            title = state.Media.Title,
                            artist = state.Media.Artist,
                            app = state.Media.SourceAppName,
                            posSec = (int)state.Media.PositionSec,
                            durSec = (int)state.Media.DurationSec,
                            accentHex = $"#{state.Media.DominantColor.R:X2}{state.Media.DominantColor.G:X2}{state.Media.DominantColor.B:X2}",
                            albumArtB64 = artB64,
                            audioLevel = audioLvl
                        },
                        apps,
                        shelf = shelfList,
                        clipboard = clipList,
                        todos = todoList
                    });
                }
                return ("200 OK", "application/json", Encoding.UTF8.GetBytes(json), "");
            }

            // 7. Pad Endpoint (/deck/pad)
            if (path.Equals("/deck/pad", StringComparison.OrdinalIgnoreCase))
            {
                string act = ExtractParam("a");
                if (act == "move")
                {
                    int dx = int.TryParse(ExtractParam("dx"), out int x) ? x : 0;
                    int dy = int.TryParse(ExtractParam("dy"), out int y) ? y : 0;
                    var pt = Cursor.Position;
                    Cursor.Position = new Point(pt.X + dx, pt.Y + dy);
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
                        SendUnicodeTextInput(txt);
                    }
                }
                else if (act == "copy") SendCtrlKey(0x43);
                else if (act == "paste") SendCtrlKey(0x56);
                else if (act == "undo") SendCtrlKey(0x5A);

                return ("200 OK", "application/json", Encoding.UTF8.GetBytes("{\"ok\":true}"), "");
            }

            // 8. Media Endpoint (/deck/media)
            if (path.Equals("/deck/media", StringComparison.OrdinalIgnoreCase))
            {
                string act = ExtractParam("a");
                if (act == "play") WindowsSystemHooks.MediaTogglePlayPause(state);
                else if (act == "next") WindowsSystemHooks.MediaNextTrack(state);
                else if (act == "prev") WindowsSystemHooks.MediaPreviousTrack(state);
                else if (act == "vol" && float.TryParse(ExtractParam("v"), out float v)) WindowsSystemHooks.SetMasterVolume(state, v, showPopup: true);
                else if (act == "mute")
                {
                    WindowsSystemHooks.ToggleMasterOutputMute(state, showPopup: true);
                }
                else if (act == "test_audio")
                {
                    try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
                }
                else if (act == "seek" && double.TryParse(ExtractParam("v"), out double pos))
                {
                    double clamped;
                    lock (state.SyncRoot)
                    {
                        clamped = Math.Clamp(pos, 0.0, Math.Max(1.0, state.Media.DurationSec));
                        state.Media.PositionSec = clamped;
                    }
                    try
                    {
                        if (_seekMediaMethod2 != null) _seekMediaMethod2.Invoke(null, new object[] { state, clamped });
                        else if (_seekMediaMethod1 != null) _seekMediaMethod1.Invoke(null, new object[] { clamped });
                    }
                    catch { }
                }
                return ("200 OK", "application/json", Encoding.UTF8.GetBytes("{\"ok\":true}"), "");
            }

            // 8b. To-Do Sync Endpoint (/deck/todo)
            if (path.Equals("/deck/todo", StringComparison.OrdinalIgnoreCase))
            {
                string act = ExtractParam("a");
                bool changed = false;
                if (act == "toggle" && int.TryParse(ExtractParam("idx"), out int tidx))
                {
                    lock (state.SyncRoot)
                    {
                        if (tidx >= 0 && tidx < state.TodoList.Count)
                        {
                            state.TodoList[tidx].Done = !state.TodoList[tidx].Done;
                            changed = true;
                        }
                    }
                }
                else if (act == "add")
                {
                    string txt = (ExtractParam("text") ?? "").Trim();
                    if (!string.IsNullOrEmpty(txt))
                    {
                        if (txt.Length > 120) txt = txt.Substring(0, 120);
                        lock (state.SyncRoot)
                        {
                            state.TodoList.Add(new TodoItem { Text = txt, Done = false });
                            changed = true;
                        }
                    }
                }
                if (changed) ConfigManager.SaveAtomic(state);
                return ("200 OK", "application/json", Encoding.UTF8.GetBytes("{\"ok\":true}"), "");
            }

            // 8c. Shelf File Download Endpoint (/deck/shelf/file)
            if (path.Equals("/deck/shelf/file", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(ExtractParam("idx"), out int sidx))
                    return ("400 Bad Request", "application/json", Encoding.UTF8.GetBytes("{\"error\":\"Bad idx\"}"), "");
                string filePath = null;
                string fileName = "file";
                lock (state.SyncRoot)
                {
                    if (sidx >= 0 && sidx < state.ShelfFiles.Count)
                    {
                        filePath = state.ShelfFiles[sidx].FullPath;
                        fileName = state.ShelfFiles[sidx].DisplayName ?? Path.GetFileName(filePath ?? "file");
                    }
                }
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                    return ("404 Not Found", "application/json", Encoding.UTF8.GetBytes("{\"error\":\"File not found\"}"), "");
                try
                {
                    var fi = new FileInfo(filePath);
                    if (fi.Length > 25L * 1024L * 1024L)
                        return ("413 Payload Too Large", "application/json", Encoding.UTF8.GetBytes("{\"error\":\"File over 25MB\"}"), "");
                    byte[] raw = File.ReadAllBytes(filePath);
                    string safeName = SanitizeAsciiFilename(fileName);
                    return ("200 OK", "application/octet-stream", raw, $"Content-Disposition: attachment; filename=\"{safeName}\"\r\n");
                }
                catch
                {
                    return ("500 Internal Server Error", "application/json", Encoding.UTF8.GetBytes("{\"error\":\"Read failed\"}"), "");
                }
            }

            // 9. Apps Endpoint (/deck/app)
            if (path.Equals("/deck/app", StringComparison.OrdinalIgnoreCase))
            {
                string act = ExtractParam("a");
                if (act == "add")
                {
                    string label = ExtractParam("label");
                    string appPath = ExtractParam("path");
                    if (string.IsNullOrWhiteSpace(appPath))
                    {
                        return ("400 Bad Request", "application/json",
                                Encoding.UTF8.GetBytes("{\"ok\":false,\"error\":\"Missing app path\"}"), "");
                    }
                    string safeLabel = label;
                    if (string.IsNullOrWhiteSpace(safeLabel))
                    {
                        try { safeLabel = Path.GetFileNameWithoutExtension(appPath); }
                        catch { safeLabel = ""; }
                        if (string.IsNullOrWhiteSpace(safeLabel)) safeLabel = appPath;
                    }

                    bool added = false;
                    lock (state.SyncRoot)
                    {
                        for (int i = 0; i < 6; ++i)
                        {
                            if (string.IsNullOrEmpty(state.LauncherSlots[i].TargetPath))
                            {
                                state.LauncherSlots[i].Label = safeLabel;
                                state.LauncherSlots[i].TargetPath = appPath;
                                added = true;
                                break;
                            }
                        }
                    }
                    if (!added)
                    {
                        return ("409 Conflict", "application/json",
                                Encoding.UTF8.GetBytes("{\"ok\":false,\"error\":\"All 6 launcher slots are full\"}"), "");
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
            if (path.Equals("/deck/mode", StringComparison.OrdinalIgnoreCase))
            {
                string act = ExtractParam("a");
                switch (act)
                {
                    case "gamebar":
                    case "game_bar":
                        SendKeyChord(new byte[] { 0x5B, (byte)'G' }); // Win+G
                        break;
                    case "screenshot":
                    case "game_shot":
                        WindowsSystemHooks.CaptureFullScreenshot(state);
                        break;
                    case "clip":
                    case "game_clip":
                        SendKeyChord(new byte[] { 0x5B, 0x12, (byte)'G' }); // Win+Alt+G
                        break;
                    case "record":
                    case "game_rec":
                        SendKeyChord(new byte[] { 0x5B, 0x12, (byte)'R' }); // Win+Alt+R (Windows Game Bar Record)
                        lock (state.SyncRoot)
                        {
                            state.IsRecordingScreen = !state.IsRecordingScreen;
                            state.RecordingStartMs = ConfigManager.GetTickMs();
                            state.RecSpring.SetTarget(state.IsRecordingScreen ? 100.0f : 0.0f, state.Settings.ReduceMotion);
                        }
                        break;
                    case "switch":
                        SendKeyChord(new byte[] { 0x12, 0x09 }); // Alt+Tab
                        break;
                    case "mic":
                        WindowsSystemHooks.ToggleGlobalMicMute(state);
                        break;
                    case "slide_start": TapKey(0x74); break; // F5
                    case "slide_next":  TapExtendedKey(0x27); break; // Right
                    case "slide_prev":  TapExtendedKey(0x25); break; // Left
                    case "slide_black": TapKey((byte)'B'); break;
                    case "slide_end":   TapKey(0x1B); break; // Esc
                    case "movie_play":  WindowsSystemHooks.MediaTogglePlayPause(state); break;
                    case "movie_back":  TapKey((byte)'J'); break;
                    case "movie_fwd":   TapKey((byte)'L'); break;
                    case "movie_full":  TapKey((byte)'F'); break;
                    case "movie_mute":
                    {
                        bool nextM;
                        lock (state.SyncRoot) nextM = !state.MasterOutputMuted;
                        if (CoreAudioInterop.TrySetDefaultEndpointMute(false, nextM))
                            WindowsSystemHooks.SyncSystemAudioState(state, allowPopup: true);
                        else
                            lock (state.SyncRoot) state.MasterOutputMuted = nextM;
                        break;
                    }
                    case "work_copy":   SendCtrlKey(0x43); break;
                    case "work_paste":  SendCtrlKey(0x56); break;
                    case "work_undo":   SendCtrlKey(0x5A); break;
                }
                return ("200 OK", "application/json", Encoding.UTF8.GetBytes("{\"ok\":true}"), "");
            }

            // 10b. Deck Clipboard & Shelf endpoints (for live Shelf & Clipboard access on phone)
            if (path.Equals("/deck/clipboard", StringComparison.OrdinalIgnoreCase))
            {
                string txt = ExtractParam("text");
                if (!string.IsNullOrEmpty(txt))
                {
                    try
                    {
                        var th = new Thread(() => { try { Clipboard.SetText(txt); } catch { } });
                        th.SetApartmentState(ApartmentState.STA);
                        th.Start();
                        th.Join(400);
                    }
                    catch { }
                }
                return ("200 OK", "application/json", Encoding.UTF8.GetBytes("{\"ok\":true}"), "");
            }

            if (path.Equals("/deck/shelf/file", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(ExtractParam("idx"), out int idx))
                {
                    string filePath = "";
                    string fileName = "file";
                    lock (state.SyncRoot)
                    {
                        if (idx >= 0 && idx < state.ShelfFiles.Count)
                        {
                            filePath = state.ShelfFiles[idx].FullPath ?? "";
                            fileName = state.ShelfFiles[idx].DisplayName ?? "file";
                        }
                    }
                    if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                    {
                        byte[] data = File.ReadAllBytes(filePath);
                        string safeName = SanitizeAsciiFilename(fileName);
                        return ("200 OK", "application/octet-stream", data,
                            $"Content-Disposition: attachment; filename=\"{safeName}\"\r\n");
                    }
                }
                return ("404 Not Found", "application/json", Encoding.UTF8.GetBytes("{\"error\":\"Not found\"}"), "");
            }

            // 11. PC Power Endpoint (/deck/power — Lock, Lid-Close Sleep, Restart/Shutdown 30s grace, Abort)
            if (path.Equals("/deck/power", StringComparison.OrdinalIgnoreCase))
            {
                string act = ExtractParam("a");
                if (PowerActionHandlerForTest != null)
                {
                    PowerActionHandlerForTest(act);
                }
                else
                {
                    if (act == "lock") Win32Layered.LockWorkStation();
                    else if (act == "sleep")
                    {
                        // True Lid-Close / Modern Standby sleep: lock workstation + turn off display
                        // without triggering a full ACPI shutdown/hibernate, keeping Wi-Fi wake reachable!
                        Win32Layered.LockWorkStation();
                        SendMessageW((IntPtr)0xFFFF, 0x0112, (IntPtr)0xF170, (IntPtr)2);
                    }
                    else if (act == "restart") Process.Start(new ProcessStartInfo("shutdown", "/r /t 30") { CreateNoWindow = true, UseShellExecute = false });
                    else if (act == "shutdown") Process.Start(new ProcessStartInfo("shutdown", "/s /t 30") { CreateNoWindow = true, UseShellExecute = false });
                    else if (act == "abort") Process.Start(new ProcessStartInfo("shutdown", "/a") { CreateNoWindow = true, UseShellExecute = false });
                }
                return ("200 OK", "application/json", Encoding.UTF8.GetBytes("{\"ok\":true}"), "");
            }

            return ("200 OK", "application/json", Encoding.UTF8.GetBytes("{\"ok\":true,\"service\":\"NotchPixelDeck\"}"), "");
        }

        private static GlobalAppState _activeState;

        public static void Start(GlobalAppState state)
        {
            if (_running) return;
            _running = true;
            _activeState = state;
            LoadPairedDevices(state);
            DetectPrivateNetworkAndLocalIp(state);
            // Certificate generation is slow on a fresh profile; never block the
            // listeners on it. The TLS thread (or this warm-up) mints it once.
            new Thread(() => EnsurePcTlsCertificate(state)) { IsBackground = true }.Start();
            GenerateTwoMinutePairingCode(state);

            _networkAddressChangedHandler = (_, _) =>
            {
                if (_running && _activeState != null)
                {
                    DetectPrivateNetworkAndLocalIp(_activeState);
                }
            };
            try { NetworkChange.NetworkAddressChanged += _networkAddressChangedHandler; } catch { }

            if (state.Settings.LoopbackApiEnabled)
            {
                new Thread(() => RunLoopbackServer(state)) { IsBackground = true }.Start();
            }
            int httpGeneration = _deckGeneration;
            new Thread(() => RunHttpDeckAndShelfServer(state, httpGeneration)) { IsBackground = true }.Start();

            // Listeners start with the app. They are closed only by an explicit
            // toggle OFF (ApplyDeckEnabled), which also drops live sockets and the
            // pairing code, so the toggle can never stick ON with dead servers.
            lock (_deckLock)
            {
                if (!_deckEnabled)
                {
                    _deckEnabled = true;
                    StartDeckThreads(state, ++_deckGeneration);
                }
            }
        }

        private static void StartDeckThreads(GlobalAppState state, int generation)
        {
            // Bind the TLS socket before returning: the app's loopback port is what
            // clients use as a readiness signal, so it must not come up first.
            TcpListener tls = null;
            try
            {
                tls = new TcpListener(IPAddress.Any, state.Settings.PcDeckTlsPort);
                tls.Start();
                _deckTlsListener = tls;
            }
            catch (Exception ex)
            {
                ConfigManager.LogDiagnostic("deck tls bind: " + ex.Message);
                try { tls?.Stop(); } catch { }
                tls = null;
            }

            new Thread(() => RunTlsDeckServer(state, generation, tls)) { IsBackground = true }.Start();
            new Thread(() => RunUdpDiscoveryResponder(state, generation)) { IsBackground = true }.Start();
        }

        public static bool IsDeckRunning => _deckEnabled;

        // Idempotent: starts the HTTP/TLS/UDP deck servers when the deck is enabled
        // on a private network, and fully tears them down (listeners, live sockets,
        // pairing code) when it is switched off. Never leaves the toggle stuck ON.
        public static void ApplyDeckEnabled(GlobalAppState state)
        {
            if (state == null) return;
            // The toggle alone decides whether sockets exist. On a public network
            // the request handlers still answer 403, and DetectPrivateNetworkAndLocalIp
            // forces the toggle off (which closes these sockets).
            bool enable = state.Settings.PcDeckEnabled;
            lock (_deckLock)
            {
                if (enable == _deckEnabled) return;
                _deckEnabled = enable;
                ConfigManager.LogDiagnostic($"deck sockets {(enable ? "on" : "off")}");
                int generation = ++_deckGeneration;
                if (enable)
                {
                    _activeState ??= state;
                    // The plain-HTTP listener also serves the phone file shelf, which
                    // is a separate feature and stays up; deck endpoints on it answer
                    // 403 while PcDeckEnabled is false.
                    StartDeckThreads(state, generation);
                }
                else
                {
                    StopDeckSockets(state);
                }
            }
        }

        private static void StopDeckSockets(GlobalAppState state)
        {
            // The HTTP listener is shared with the phone file shelf, which is a
            // separate feature, so only the deck-only sockets are torn down here.
            try { _deckTlsListener?.Stop(); } catch { }
            try { _udpDiscovery?.Close(); } catch { }
            _deckTlsListener = null;
            _udpDiscovery = null;

            // In-flight requests run on the thread pool; do not yank the socket that
            // the caller itself is serving, but do close every other live socket.
            if (!Thread.CurrentThread.IsThreadPoolThread)
            {
                foreach (TcpClient live in _deckClients.Keys)
                {
                    try { live.Close(); } catch { }
                }
                _deckClients.Clear();
            }

            GlobalAppState active = state ?? _activeState;
            if (active != null)
            {
                lock (active.SyncRoot)
                {
                    active.ActivePairingCode = "";
                }
            }
        }

        public static void Stop()
        {
            _running = false;
            if (_networkAddressChangedHandler != null)
            {
                try { NetworkChange.NetworkAddressChanged -= _networkAddressChangedHandler; } catch { }
                _networkAddressChangedHandler = null;
            }
            if (_activeState != null)
            {
                FlushRevokedDevicesIfCleared(_activeState);
                _activeState = null;
            }
            try { _loopbackListener?.Stop(); } catch { }
            _loopbackListener = null;
            try { _deckHttpListener?.Stop(); } catch { }
            _deckHttpListener = null;
            lock (_deckLock)
            {
                _deckEnabled = false;
                _deckGeneration++;
                StopDeckSockets(null);
            }
        }

        private static string ReadHttpRequestFromStream(Stream s, int maxBytes = 65536)
        {
            using var ms = new MemoryStream();
            byte[] buf = new byte[4096];
            int headerEnd = -1;
            int contentLength = 0;

            while (ms.Length < maxBytes)
            {
                int n = s.Read(buf, 0, Math.Min(buf.Length, maxBytes - (int)ms.Length));
                if (n <= 0) break;
                ms.Write(buf, 0, n);

                byte[] cur = ms.GetBuffer();
                int len = (int)ms.Length;
                if (headerEnd < 0)
                {
                    string partial = Encoding.ASCII.GetString(cur, 0, len);
                    int idx = partial.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    int sep = 4;
                    if (idx < 0)
                    {
                        idx = partial.IndexOf("\n\n", StringComparison.Ordinal);
                        sep = 2;
                    }
                    if (idx >= 0)
                    {
                        headerEnd = idx + sep;
                        foreach (string line in partial.Substring(0, idx).Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
                        {
                            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                            {
                                if (int.TryParse(line.Substring(15).Trim(), out int cl) && cl > 0)
                                {
                                    contentLength = Math.Min(cl, maxBytes - headerEnd);
                                }
                            }
                        }
                    }
                }

                if (headerEnd >= 0 && len >= headerEnd + contentLength)
                {
                    break;
                }
            }
            return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
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
                    client.ReceiveTimeout = 5000;
                    client.SendTimeout = 5000;
                    using NetworkStream ns = client.GetStream();
                    string raw = ReadHttpRequestFromStream(ns, 16384);
                    if (!string.IsNullOrEmpty(raw))
                    {
                        HandleLoopbackRequest(state, raw, out string json);
                        WriteHttpResponse(ns, "200 OK", "application/json", Encoding.UTF8.GetBytes(json));
                    }
                }
            }
            catch { }
        }

        private static void RunHttpDeckAndShelfServer(GlobalAppState state, int generation)
        {
            TcpListener listener = null;
            try
            {
                listener = new TcpListener(IPAddress.Any, state.Settings.PcDeckPort);
                _deckHttpListener = listener;
                listener.Start();
                // HTTP + shelf live as long as the app does; the deck flag only
                // gates the deck endpoints inside ProcessDeckOrShelfRequest.
                while (_running)
                {
                    TcpClient client = listener.AcceptTcpClient();
                    _deckClients.TryAdd(client, 0);
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try
                        {
                            using (client)
                            {
                                client.ReceiveTimeout = 5000;
                                client.SendTimeout = 5000;
                                RefreshNetworkStateIfStale(state);
                                using NetworkStream ns = client.GetStream();
                                string raw = ReadHttpRequestFromStream(ns, 65536);
                                if (string.IsNullOrEmpty(raw)) return;
                                string ip = ((IPEndPoint)client.Client.RemoteEndPoint)?.Address.ToString() ?? "";
                                var (st, ct, body, extra) = ProcessDeckOrShelfRequest(state, raw, ip);
                                WriteHttpResponse(ns, st, ct, body, extra);
                            }
                        }
                        catch { }
                        finally { _deckClients.TryRemove(client, out byte _drained); }
                    });
                }
            }
            catch { }
            finally
            {
                try { listener?.Stop(); } catch { }
                if (ReferenceEquals(_deckHttpListener, listener)) _deckHttpListener = null;
            }
        }

        private static void RunTlsDeckServer(GlobalAppState state, int generation, TcpListener bound = null)
        {
            TcpListener listener = bound;
            try
            {
                if (listener == null)
                {
                    listener = new TcpListener(IPAddress.Any, state.Settings.PcDeckTlsPort);
                    _deckTlsListener = listener;
                    listener.Start();
                }

                // The certificate is minted after the bind so a slow RSA keygen on a
                // fresh profile never shows clients a closed port.
                var cert = EnsurePcTlsCertificate(state);
                ConfigManager.LogDiagnostic($"deck tls start cert={(cert != null)} port={state.Settings.PcDeckTlsPort}");
                if (cert == null) return;
                while (_deckEnabled && generation == _deckGeneration)
                {
                    TcpClient client = listener.AcceptTcpClient();
                    _deckClients.TryAdd(client, 0);
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try
                        {
                            using (client)
                            {
                                client.ReceiveTimeout = 35000;
                                client.SendTimeout = 35000;
                                RefreshNetworkStateIfStale(state);
                                using var ssl = new SslStream(client.GetStream(), false);
                                ssl.ReadTimeout = 35000;
                                ssl.WriteTimeout = 35000;
                                ssl.AuthenticateAsServer(cert, clientCertificateRequired: false, checkCertificateRevocation: false);
                                string raw = ReadHttpRequestFromStream(ssl, 65536);
                                if (!string.IsNullOrEmpty(raw))
                                {
                                    string ip = ((IPEndPoint)client.Client.RemoteEndPoint)?.Address.ToString() ?? "";
                                    var (st, ct, body, extra) = ProcessDeckOrShelfRequest(state, raw, ip);
                                    WriteHttpResponse(ssl, st, ct, body, extra);
                                }
                            }
                        }
                        catch { }
                        finally { _deckClients.TryRemove(client, out byte _drained); }
                    });
                }
            }
            catch (Exception ex)
            {
                if (_deckEnabled) ConfigManager.LogDiagnostic("deck tls listener: " + ex.Message);
            }
            finally
            {
                try { listener?.Stop(); } catch { }
                if (ReferenceEquals(_deckTlsListener, listener)) _deckTlsListener = null;
            }
        }

        private static void RunUdpDiscoveryResponder(GlobalAppState state, int generation)
        {
            UdpClient udp = null;
            try
            {
                udp = new UdpClient(47826);
                _udpDiscovery = udp;
                while (_deckEnabled && generation == _deckGeneration)
                {
                    IPEndPoint ep = new(IPAddress.Any, 0);
                    byte[] req = udp.Receive(ref ep);
                    string msg = Encoding.UTF8.GetString(req);
                    if (msg.StartsWith("WAKE|", StringComparison.Ordinal))
                    {
                        // Instant Wi-Fi wake from Modern Standby / Lid-Close Sleep
                        string[] parts = msg.Split('|');
                        if (parts.Length >= 2)
                        {
                            string tok = parts[1];
                            bool authed;
                            lock (state.SyncRoot)
                            {
                                authed = state.PairedDeckDevices.Exists(d => !string.IsNullOrEmpty(d.AuthToken) && d.AuthToken == tok);
                            }
                            if (authed)
                            {
                                try
                                {
                                    SetThreadExecutionState(0x00000001u | 0x00000002u); // ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED
                                    SendMessageW((IntPtr)0xFFFF, 0x0112, (IntPtr)0xF170, (IntPtr)(-1)); // SC_MONITORPOWER ON
                                    mouse_event(0x0001, 1, 0, 0, UIntPtr.Zero);
                                    mouse_event(0x0001, -1, 0, 0, UIntPtr.Zero);
                                }
                                catch { }
                            }
                        }
                        continue;
                    }
                    if (msg.StartsWith("PAD|", StringComparison.Ordinal))
                    {
                        // Ultra-low-latency (<0.5ms) authenticated UDP trackpad fast-path:
                        // PAD|<token>|move|<dx>|<dy>  or  PAD|<token>|scroll|<dy>  or  PAD|<token>|lclick|rclick|drag_start|drag_end
                        string[] parts = msg.Split('|');
                        if (parts.Length >= 3)
                        {
                            string tok = parts[1];
                            bool authed;
                            lock (state.SyncRoot)
                            {
                                authed = state.PairedDeckDevices.Exists(d => !string.IsNullOrEmpty(d.AuthToken) && d.AuthToken == tok);
                            }
                            if (authed)
                            {
                                string act = parts[2];
                                if (act == "move" && parts.Length >= 5
                                    && int.TryParse(parts[3], out int dx)
                                    && int.TryParse(parts[4], out int dy))
                                {
                                    var pt = Cursor.Position;
                                    Cursor.Position = new Point(pt.X + dx, pt.Y + dy);
                                }
                                else if (act == "scroll" && parts.Length >= 4 && int.TryParse(parts[3], out int sdy))
                                {
                                    mouse_event(0x0800, 0, 0, sdy * 120, UIntPtr.Zero);
                                }
                                else if (act == "lclick") SendMouseFlag(0x0002 | 0x0004);
                                else if (act == "rclick") SendMouseFlag(0x0008 | 0x0010);
                                else if (act == "drag_start") SendMouseFlag(0x0002);
                                else if (act == "drag_end") SendMouseFlag(0x0004);
                            }
                        }
                        continue;
                    }
                    RefreshNetworkStateIfStale(state);
                    if (TryBuildUdpDiscoveryReply(state, msg, out string replyJson))
                    {
                        byte[] resp = Encoding.UTF8.GetBytes(replyJson);
                        udp.Send(resp, resp.Length, ep);
                    }
                }
            }
            catch { }
            finally
            {
                try { udp?.Close(); } catch { }
                if (ReferenceEquals(_udpDiscovery, udp)) _udpDiscovery = null;
            }
        }

        private static void WriteHttpResponse(Stream s, string status, string contentType, byte[] body, string extraHeaders = "")
        {
            string hdr = $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n{extraHeaders}\r\n";
            byte[] hBytes = Encoding.UTF8.GetBytes(hdr);
            s.Write(hBytes, 0, hBytes.Length);
            s.Write(body, 0, body.Length);
            s.Flush();
        }

        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_UNICODE = 0x0004;

        [StructLayout(LayoutKind.Explicit)]
        private struct INPUT
        {
            [FieldOffset(0)] public uint type;
            [FieldOffset(8)] public MOUSEINPUT mi;
            [FieldOffset(8)] public KEYBDINPUT ki;
            [FieldOffset(8)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        private static void SendUnicodeTextInput(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var inputs = new List<INPUT>(text.Length * 2);
            for (int i = 0; i < text.Length; ++i)
            {
                char ch = text[i];
                if (ch == '\r')
                {
                    if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                    inputs.Add(new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = 0x0D } });
                    inputs.Add(new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = 0x0D, dwFlags = KEYEVENTF_KEYUP } });
                }
                else if (ch == '\n')
                {
                    inputs.Add(new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = 0x0D } });
                    inputs.Add(new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = 0x0D, dwFlags = KEYEVENTF_KEYUP } });
                }
                else if (ch == '\t')
                {
                    inputs.Add(new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = 0x09 } });
                    inputs.Add(new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = 0x09, dwFlags = KEYEVENTF_KEYUP } });
                }
                else
                {
                    inputs.Add(new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = KEYEVENTF_UNICODE } });
                    inputs.Add(new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } });
                }
            }
            if (inputs.Count > 0)
            {
                try { SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>()); } catch { }
            }
        }

        private static Image _cachedArtRef;
        private static string _cachedArtB64 = "";
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _slotIconCache = new();

        private static string EncodeAlbumArtJpegBase64(Image img)
        {
            if (img == null) { _cachedArtRef = null; _cachedArtB64 = ""; return ""; }
            if (ReferenceEquals(_cachedArtRef, img)) return _cachedArtB64;
            try
            {
                using var thumb = new Bitmap(160, 160);
                using (var g = Graphics.FromImage(thumb))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(img, 0, 0, 160, 160);
                }
                using var ms = new MemoryStream();
                thumb.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
                _cachedArtB64 = Convert.ToBase64String(ms.ToArray());
                _cachedArtRef = img;
                return _cachedArtB64;
            }
            catch { return ""; }
        }

        private static readonly Dictionary<string, string> _shelfThumbCache = new(StringComparer.OrdinalIgnoreCase);

        private static string EncodeShelfThumbJpegBase64(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath)) return "";
            lock (_shelfThumbCache)
            {
                if (_shelfThumbCache.TryGetValue(fullPath, out string cached)) return cached;
            }
            if (!File.Exists(fullPath)) return "";
            try
            {
                using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var src = Image.FromStream(fs, useEmbeddedColorManagement: false, validateImageData: false);
                using var thumb = new Bitmap(96, 96);
                using (var g = Graphics.FromImage(thumb))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    float scale = Math.Max(96f / Math.Max(1, src.Width), 96f / Math.Max(1, src.Height));
                    float dw = src.Width * scale;
                    float dh = src.Height * scale;
                    g.DrawImage(src, (96f - dw) * 0.5f, (96f - dh) * 0.5f, dw, dh);
                }
                using var ms = new MemoryStream();
                thumb.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
                string b64 = Convert.ToBase64String(ms.ToArray());
                lock (_shelfThumbCache)
                {
                    if (_shelfThumbCache.Count > 32) _shelfThumbCache.Clear();
                    _shelfThumbCache[fullPath] = b64;
                }
                return b64;
            }
            catch { return ""; }
        }

        private static string EncodeSlotIconPngBase64(string iconPath)
        {
            if (string.IsNullOrWhiteSpace(iconPath) || !File.Exists(iconPath)) return "";
            if (_slotIconCache.TryGetValue(iconPath, out string cached)) return cached;
            try
            {
                using var src = Image.FromFile(iconPath);
                using var thumb = new Bitmap(96, 96);
                using (var g = Graphics.FromImage(thumb))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.Clear(Color.Transparent);
                    g.DrawImage(src, 0, 0, 96, 96);
                }
                using var ms = new MemoryStream();
                thumb.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                string b64 = Convert.ToBase64String(ms.ToArray());
                _slotIconCache[iconPath] = b64;
                return b64;
            }
            catch { return ""; }
        }

        private static void SendKeyChord(byte[] vks)
        {
            if (vks == null || vks.Length == 0) return;
            try
            {
                var list = new List<INPUT>(vks.Length * 2);
                for (int i = 0; i < vks.Length; ++i)
                {
                    ushort sc = (ushort)MapVirtualKeyW(vks[i], 0);
                    uint ext = (vks[i] == 0x5B || vks[i] == 0x5C || vks[i] == 0x25 || vks[i] == 0x27) ? KEYEVENTF_EXTENDEDKEY : 0u;
                    list.Add(new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = vks[i], wScan = sc, dwFlags = ext } });
                }
                for (int i = vks.Length - 1; i >= 0; --i)
                {
                    ushort sc = (ushort)MapVirtualKeyW(vks[i], 0);
                    uint ext = (vks[i] == 0x5B || vks[i] == 0x5C || vks[i] == 0x25 || vks[i] == 0x27) ? KEYEVENTF_EXTENDEDKEY : 0u;
                    list.Add(new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = vks[i], wScan = sc, dwFlags = ext | KEYEVENTF_KEYUP } });
                }
                SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf<INPUT>());
            }
            catch { }
        }

        private static void SendMouseFlag(uint flags) => mouse_event(flags, 0, 0, 0, UIntPtr.Zero);
        private static void TapKey(byte vk) => SendKeyChord(new[] { vk });
        private static void TapExtendedKey(byte vk) => SendKeyChord(new[] { vk });
        private static void SendCtrlKey(byte vk) => SendKeyChord(new byte[] { 0x11, vk });

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, int dx, int dy, int dwData, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKeyW(uint uCode, uint uMapType);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern uint SetThreadExecutionState(uint esFlags);
    }
}
