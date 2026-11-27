using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace NotchPixel.E2E.Tests.Infrastructure;

public sealed record HttpCallResult(
    int StatusCode,
    string Body,
    IReadOnlyDictionary<string, string> Headers);

public sealed record TlsCallResult(
    int StatusCode,
    string Body,
    string ServerCertSha256Hex);

public sealed record UdpDiscoveryResponse(
    string RawJson,
    string RemoteIp,
    int TlsPort,
    string? CertFingerprint,
    string? PcName,
    string? Mac,
    JsonObject Parsed);

public sealed record DeckPairingUri(
    string Scheme,
    string HostIp,
    int Port,
    string Code,
    string CertFingerprint,
    string? Mac,
    string? PcName);

public sealed class LoopbackAndDeckClient : IDisposable
{
    private readonly string _host;
    private readonly int _loopbackPort;
    private readonly int _shelfPort;
    private readonly int _deckTlsPort;
    private readonly int _discoveryUdpPort;
    private readonly HttpClient _plainHttp;
    private bool _disposed;

    public string Host => _host;
    public int LoopbackPort => _loopbackPort;
    public int ShelfPort => _shelfPort;
    public int DeckTlsPort => _deckTlsPort;
    public int DiscoveryUdpPort => _discoveryUdpPort;

    public LoopbackAndDeckClient(
        string host = "127.0.0.1",
        int loopbackPort = 47823,
        int shelfPort = 47824,
        int deckTlsPort = 47825,
        int discoveryUdpPort = 47826)
    {
        _host = host;
        _loopbackPort = loopbackPort;
        _shelfPort = shelfPort;
        _deckTlsPort = deckTlsPort;
        _discoveryUdpPort = discoveryUdpPort;
        _plainHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    }

    // -------------------------------------------------------------------------
    // Loopback HTTP (127.0.0.1:47823)
    // -------------------------------------------------------------------------

    public async Task<bool> WaitForLoopbackReadyAsync(int timeoutMs = 10000, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                HttpCallResult res = await GetLoopbackRootAsync(ct).ConfigureAwait(false);
                if (res.StatusCode == 200) return true;
            }
            catch { }
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
        return false;
    }

    public Task<HttpCallResult> GetLoopbackRootAsync(CancellationToken ct = default)
        => SendPlainHttpAsync(HttpMethod.Get, $"http://{_host}:{_loopbackPort}/", null, ct);

    public Task<HttpCallResult> SendNotifyAsync(
        string title,
        string text = "",
        string app = "",
        bool usePost = false,
        CancellationToken ct = default)
    {
        string path = $"/notify?title={Uri.EscapeDataString(title)}"
                    + $"&text={Uri.EscapeDataString(text)}"
                    + $"&app={Uri.EscapeDataString(app)}";
        return SendPlainHttpAsync(
            usePost ? HttpMethod.Post : HttpMethod.Get,
            $"http://{_host}:{_loopbackPort}{path}",
            usePost ? string.Empty : null,
            ct);
    }

    public Task<HttpCallResult> SendTileAsync(
        string id,
        string title,
        string value = "",
        string sub = "",
        bool usePost = false,
        CancellationToken ct = default)
    {
        string path = $"/tile?id={Uri.EscapeDataString(id)}"
                    + $"&title={Uri.EscapeDataString(title)}"
                    + $"&value={Uri.EscapeDataString(value)}"
                    + $"&sub={Uri.EscapeDataString(sub)}";
        return SendPlainHttpAsync(
            usePost ? HttpMethod.Post : HttpMethod.Get,
            $"http://{_host}:{_loopbackPort}{path}",
            usePost ? string.Empty : null,
            ct);
    }

    public Task<HttpCallResult> SendAgentAsync(
        string name,
        string status,
        string step = "",
        int? progress = null,
        bool almostDone = false,
        bool needsInput = false,
        bool usePost = false,
        CancellationToken ct = default)
    {
        var sb = new StringBuilder("/agent?");
        sb.Append("name=").Append(Uri.EscapeDataString(name));
        sb.Append("&status=").Append(Uri.EscapeDataString(status));
        if (!string.IsNullOrEmpty(step))
            sb.Append("&step=").Append(Uri.EscapeDataString(step));
        if (progress.HasValue)
            sb.Append("&progress=").Append(progress.Value);
        if (almostDone)
            sb.Append("&almost_done=1");
        if (needsInput)
            sb.Append("&needs_input=1");

        return SendPlainHttpAsync(
            usePost ? HttpMethod.Post : HttpMethod.Get,
            $"http://{_host}:{_loopbackPort}{sb}",
            usePost ? string.Empty : null,
            ct);
    }

    public Task<HttpCallResult> SendRawLoopbackAsync(
        HttpMethod method,
        string pathAndQuery,
        string? body = null,
        CancellationToken ct = default)
    {
        string normalized = pathAndQuery.StartsWith('/') ? pathAndQuery : "/" + pathAndQuery;
        return SendPlainHttpAsync(method, $"http://{_host}:{_loopbackPort}{normalized}", body, ct);
    }

    // -------------------------------------------------------------------------
    // File Shelf HTTP (127.0.0.1:47824)
    // -------------------------------------------------------------------------

    public Task<HttpCallResult> GetShelfHtmlAsync(CancellationToken ct = default)
        => SendPlainHttpAsync(HttpMethod.Get, $"http://{_host}:{_shelfPort}/shelf", null, ct);

    public async Task<(int StatusCode, byte[] Bytes, string? ContentDisposition)> DownloadShelfFileAsync(
        int index,
        CancellationToken ct = default)
    {
        using var resp = await _plainHttp.GetAsync($"http://{_host}:{_shelfPort}/download/{index}", ct).ConfigureAwait(false);
        byte[] bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        string? disp = resp.Content.Headers.ContentDisposition?.ToString();
        return ((int)resp.StatusCode, bytes, disp);
    }

    // -------------------------------------------------------------------------
    // Pinned-TLS PC Deck HTTPS (127.0.0.1:47825)
    // -------------------------------------------------------------------------

    public async Task<TlsCallResult> RequestDeckTlsAsync(
        HttpMethod method,
        string pathAndQuery,
        string? expectedCertSha256Hex = null,
        string? token = null,
        bool sendBearerHeader = false,
        int timeoutMs = 5000,
        CancellationToken ct = default)
    {
        string normalizedPath = pathAndQuery.StartsWith('/') ? pathAndQuery : "/" + pathAndQuery;
        if (!string.IsNullOrEmpty(token) && !sendBearerHeader)
        {
            string sep = normalizedPath.Contains('?') ? "&" : "?";
            normalizedPath = normalizedPath + sep + "token=" + Uri.EscapeDataString(token);
        }

        string capturedFp = string.Empty;
        using var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromMilliseconds(timeoutMs),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                {
                    if (cert == null) return false;
                    byte[] hash = SHA256.HashData(cert.GetRawCertData());
                    capturedFp = Convert.ToHexString(hash).ToLowerInvariant();
                    if (!string.IsNullOrWhiteSpace(expectedCertSha256Hex))
                    {
                        return string.Equals(
                            capturedFp,
                            expectedCertSha256Hex.Trim().ToLowerInvariant(),
                            StringComparison.Ordinal);
                    }
                    return true;
                }
            }
        };

        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
        using var req = new HttpRequestMessage(method, $"https://{_host}:{_deckTlsPort}{normalizedPath}");
        if (method == HttpMethod.Post)
        {
            req.Content = new StringContent(string.Empty, Encoding.UTF8, "application/x-www-form-urlencoded");
        }
        if (!string.IsNullOrEmpty(token) && sendBearerHeader)
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using HttpResponseMessage resp = await client.SendAsync(req, ct).ConfigureAwait(false);
        string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return new TlsCallResult((int)resp.StatusCode, body, capturedFp);
    }

    public Task<TlsCallResult> ProbeAsync(string? expectedFp = null, CancellationToken ct = default)
        => RequestDeckTlsAsync(HttpMethod.Get, "/deck/probe", expectedFp, ct: ct);

    public Task<TlsCallResult> PairAsync(
        string code,
        string device = "E2E-Pixel",
        string model = "Pixel 9",
        string os = "Android 15",
        string app = "1.0.5",
        string? expectedFp = null,
        CancellationToken ct = default)
    {
        string path = "/deck/pair?code=" + Uri.EscapeDataString(code)
                    + "&device=" + Uri.EscapeDataString(device)
                    + "&model=" + Uri.EscapeDataString(model)
                    + "&os=" + Uri.EscapeDataString(os)
                    + "&app=" + Uri.EscapeDataString(app);
        return RequestDeckTlsAsync(HttpMethod.Post, path, expectedFp, ct: ct);
    }

    public Task<TlsCallResult> UnpairAsync(string token, string? expectedFp = null, CancellationToken ct = default)
        => RequestDeckTlsAsync(HttpMethod.Post, "/deck/unpair", expectedFp, token, ct: ct);

    public Task<TlsCallResult> GetStateAsync(string token, string? expectedFp = null, CancellationToken ct = default)
        => RequestDeckTlsAsync(HttpMethod.Get, "/deck/state", expectedFp, token, ct: ct);

    public Task<TlsCallResult> SendPadAsync(string token, string actionQuery, string? expectedFp = null, CancellationToken ct = default)
        => RequestDeckTlsAsync(HttpMethod.Post, $"/deck/pad?{actionQuery}", expectedFp, token, ct: ct);

    public Task<TlsCallResult> SendMediaAsync(string token, string actionQuery, string? expectedFp = null, CancellationToken ct = default)
        => RequestDeckTlsAsync(HttpMethod.Post, $"/deck/media?{actionQuery}", expectedFp, token, ct: ct);

    public Task<TlsCallResult> SendAppAsync(string token, string actionQuery, string? expectedFp = null, CancellationToken ct = default)
        => RequestDeckTlsAsync(HttpMethod.Post, $"/deck/app?{actionQuery}", expectedFp, token, ct: ct);

    public Task<TlsCallResult> SendModeAsync(string token, string action, string? expectedFp = null, CancellationToken ct = default)
        => RequestDeckTlsAsync(HttpMethod.Post, $"/deck/mode?a={Uri.EscapeDataString(action)}", expectedFp, token, ct: ct);

    public Task<TlsCallResult> SendPowerAsync(string token, string action, string? expectedFp = null, CancellationToken ct = default)
        => RequestDeckTlsAsync(HttpMethod.Post, $"/deck/power?a={Uri.EscapeDataString(action)}", expectedFp, token, ct: ct);

    // -------------------------------------------------------------------------
    // UDP Discovery (47826)
    // -------------------------------------------------------------------------

    public async Task<UdpDiscoveryResponse?> SendDiscoveryProbeAsync(
        string payload = "OPENPIXEL_DECK_DISCOVER",
        int timeoutMs = 1500,
        string? targetIp = null,
        CancellationToken ct = default)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.EnableBroadcast = true;
        byte[] bytes = Encoding.UTF8.GetBytes(payload);
        string dest = string.IsNullOrWhiteSpace(targetIp) ? _host : targetIp;
        await udp.SendAsync(bytes, bytes.Length, dest, _discoveryUdpPort).ConfigureAwait(false);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            UdpReceiveResult rx = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
            string json = Encoding.UTF8.GetString(rx.Buffer);
            JsonObject parsed = JsonNode.Parse(json) as JsonObject ?? new JsonObject();

            int tlsPort = parsed["tlsPort"]?.GetValue<int>() ?? _deckTlsPort;
            string? fp = parsed["certFingerprint"]?.GetValue<string>()
                      ?? parsed["cert"]?.GetValue<string>();
            string? pcName = parsed["pcName"]?.GetValue<string>()
                          ?? parsed["pc"]?.GetValue<string>();
            string? mac = parsed["mac"]?.GetValue<string>();
            string ip = parsed["ip"]?.GetValue<string>() ?? rx.RemoteEndPoint.Address.ToString();

            return new UdpDiscoveryResponse(json, ip, tlsPort, fp, pcName, mac, parsed);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    // -------------------------------------------------------------------------
    // QR Pairing URI Parser (notchpixel-deck:// & openpixel-deck://)
    // -------------------------------------------------------------------------

    public static bool TryParsePairingUri(string uriText, out DeckPairingUri? parsed, out string? error)
    {
        parsed = null;
        error = null;
        if (string.IsNullOrWhiteSpace(uriText))
        {
            error = "URI is empty.";
            return false;
        }

        if (!Uri.TryCreate(uriText.Trim(), UriKind.Absolute, out Uri? uri))
        {
            error = "Invalid URI syntax.";
            return false;
        }

        string scheme = uri.Scheme.ToLowerInvariant();
        if (scheme != "notchpixel-deck" && scheme != "openpixel-deck")
        {
            error = $"Unsupported URI scheme '{uri.Scheme}'.";
            return false;
        }

        if (!uri.Host.Equals("pair", StringComparison.OrdinalIgnoreCase))
        {
            error = $"Unexpected URI host/action '{uri.Host}'. Expected 'pair'.";
            return false;
        }

        Dictionary<string, string> query = ParseQueryString(uri.Query);
        string hostIp = query.TryGetValue("ip", out string? qIp) ? qIp
            : query.TryGetValue("host", out string? qHost) ? qHost : string.Empty;
        string portStr = query.TryGetValue("port", out string? qPort) ? qPort : "47825";
        string code = query.TryGetValue("code", out string? qCode) ? qCode : string.Empty;
        string cert = query.TryGetValue("cert", out string? qCert) ? qCert
            : query.TryGetValue("fp", out string? qFp) ? qFp : string.Empty;
        query.TryGetValue("mac", out string? mac);
        query.TryGetValue("name", out string? name);

        if (!int.TryParse(portStr, out int port) || port <= 0 || port > 65535)
        {
            error = $"Invalid port '{portStr}'.";
            return false;
        }

        parsed = new DeckPairingUri(scheme, hostIp, port, code, cert, mac, name);
        return true;
    }

    private static Dictionary<string, string> ParseQueryString(string rawQuery)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(rawQuery)) return map;
        string trimmed = rawQuery.StartsWith('?') ? rawQuery[1..] : rawQuery;
        foreach (string part in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq < 0)
            {
                map[Uri.UnescapeDataString(part.Replace('+', ' '))] = string.Empty;
            }
            else
            {
                string k = Uri.UnescapeDataString(part[..eq].Replace('+', ' '));
                string v = Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
                map[k] = v;
            }
        }
        return map;
    }

    private async Task<HttpCallResult> SendPlainHttpAsync(
        HttpMethod method,
        string url,
        string? body,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url);
        if (body != null)
        {
            req.Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");
        }
        using HttpResponseMessage resp = await _plainHttp.SendAsync(req, ct).ConfigureAwait(false);
        string respBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in resp.Headers)
            headers[h.Key] = string.Join(", ", h.Value);
        foreach (var h in resp.Content.Headers)
            headers[h.Key] = string.Join(", ", h.Value);
        return new HttpCallResult((int)resp.StatusCode, respBody, headers);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _plainHttp.Dispose();
    }
}
