package com.openpixel.deck.security;

import android.content.Context;
import android.net.wifi.WifiInfo;
import android.net.wifi.WifiManager;
import android.os.Build;

import com.openpixel.deck.qr.PairingUriParser;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.Inet4Address;
import java.net.InetAddress;
import java.net.NetworkInterface;
import java.net.URL;
import java.net.URLEncoder;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.cert.CertificateException;
import java.security.cert.X509Certificate;
import java.util.ArrayList;
import java.util.Enumeration;
import java.util.List;
import java.util.Locale;
import java.util.concurrent.CompletionService;
import java.util.concurrent.ExecutorCompletionService;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;

import javax.net.ssl.HostnameVerifier;
import javax.net.ssl.HttpsURLConnection;
import javax.net.ssl.SSLContext;
import javax.net.ssl.SSLSocketFactory;
import javax.net.ssl.TrustManager;
import javax.net.ssl.X509TrustManager;

/**
 * HTTPS client with SHA-256 X.509 certificate pinning and private Wi-Fi IP rediscovery.
 */
public final class PinnedDeckClient {
    public static final int DISCOVERY_UDP_PORT = 47826;

    public static final class AppSlot {
        public int index;
        public String label = "";
        public String target = "";
        public boolean configured;
        public String iconB64 = "";
    }

    public static final class ShelfItem {
        public int index;
        public String name = "";
        public String ext = "";
        public long sizeBytes;
        public boolean isImage;
        public String thumbB64 = "";
    }

    public static final class ClipItem {
        public int index;
        public String text = "";
        public boolean pinned;
    }

    public static final class TodoItem {
        public int index;
        public String text = "";
        public boolean done;
    }

    public static final class DeviceInfo {
        public String deviceId = "";
        public String deviceName = "";
        public String model = "";
        public String osVersion = "";
        public String appVersion = "";
        public String ipAddress = "";
        public String keyFingerprint = "";
        public long pairedAtMs;
        public boolean isCurrent;
    }

    public static final class PcState {
        public String pcName = "Desk PC";
        public int cpu;
        public int gpu;
        public int ram;
        public boolean cpuTempAvailable;
        public int cpuTempC;
        public boolean gpuTempAvailable;
        public int gpuTempC;
        public boolean recording;
        public int recordingSec;
        public boolean micMuted;
        public int volume;
        public boolean muted;
        public boolean hasMedia;
        public boolean isPlaying;
        public String mediaTitle = "";
        public String mediaArtist = "";
        public String mediaApp = "";
        public int posSec;
        public int durSec;
        public String accentHex = "#2BE4EA";
        public String albumArtB64 = "";
        public int audioLevel = 0;
        // Synced from Windows NotchPixel theme
        public String themeName = "LiquidGlass";
        public boolean themeIsLight = false;
        public boolean themeIsGlass = true;
        public int themeBlurRadius = 20;
        public String themeAccentHex = "#2BE4EA";
        public String themeSurfaceHex = "#101216";
        public final List<AppSlot> apps = new ArrayList<>();
        public final List<ShelfItem> shelf = new ArrayList<>();
        public final List<ClipItem> clipboard = new ArrayList<>();
        public final List<TodoItem> todos = new ArrayList<>();
    }

    public static final class ProbeResult {
        public String pcName = "";
        public String ip = "";
        public int tlsPort = 47825;
        public String certFingerprint = "";
        public String mac = "";
    }

    public static final class PairOutcome {
        public boolean ok;
        public String error = "";
    }

    private final Context appContext;
    private final SecurePairingStore store;
    private volatile String cachedFp = null;
    private volatile SSLSocketFactory cachedFactory = null;
    private DatagramSocket padUdpSocket = null;
    private final Object udpLock = new Object();

    public PinnedDeckClient(Context ctx, SecurePairingStore store) {
        this.appContext = ctx.getApplicationContext();
        this.store = store;
    }

    public SecurePairingStore getStore() {
        return store;
    }

    private static String sha256Hex(byte[] data) throws Exception {
        byte[] digest = MessageDigest.getInstance("SHA-256").digest(data);
        StringBuilder sb = new StringBuilder(digest.length * 2);
        for (byte b : digest) {
            sb.append(String.format(Locale.US, "%02x", b & 0xFF));
        }
        return sb.toString();
    }

    private SSLSocketFactory buildPinnedSocketFactory(final String expectedFp, final String[] observedFpOut) throws Exception {
        final String expectedLower = PairingUriParser.normalizeFingerprint(expectedFp);
        TrustManager[] tm = new TrustManager[]{
                new X509TrustManager() {
                    @Override public void checkClientTrusted(X509Certificate[] chain, String authType) {}
                    @Override public X509Certificate[] getAcceptedIssuers() { return new X509Certificate[0]; }
                    @Override public void checkServerTrusted(X509Certificate[] chain, String authType) throws CertificateException {
                        if (chain == null || chain.length == 0) {
                            throw new CertificateException("Empty PC certificate chain");
                        }
                        try {
                            String fp = sha256Hex(chain[0].getEncoded());
                            if (observedFpOut != null && observedFpOut.length > 0) {
                                observedFpOut[0] = fp;
                            }
                            if (!expectedLower.isEmpty() && !expectedLower.equals(fp)) {
                                throw new CertificateException("Pinned PC cert fingerprint mismatch: " + fp);
                            }
                        } catch (CertificateException ce) {
                            throw ce;
                        } catch (Exception ex) {
                            throw new CertificateException(ex);
                        }
                    }
                }
        };
        SSLContext sc = SSLContext.getInstance("TLSv1.2");
        sc.init(null, tm, null);
        return sc.getSocketFactory();
    }

    private static final HostnameVerifier LAN_HOST_VERIFIER = (hostname, session) -> true;

    private SSLSocketFactory getOrBuildPinnedSocketFactory(final String expectedFp, final String[] observedFpOut) throws Exception {
        final String expectedLower = PairingUriParser.normalizeFingerprint(expectedFp);
        if (observedFpOut == null && !expectedLower.isEmpty() && expectedLower.equals(cachedFp) && cachedFactory != null) {
            return cachedFactory;
        }
        SSLSocketFactory f = buildPinnedSocketFactory(expectedLower, observedFpOut);
        if (observedFpOut == null && !expectedLower.isEmpty()) {
            cachedFp = expectedLower;
            cachedFactory = f;
        }
        return f;
    }

    private String requestJson(String method, String ip, int port, String pathAndQuery,
                               String expectedFp, int connectTimeoutMs, int readTimeoutMs,
                               String[] observedFpOut) throws Exception {
        URL url = new URL("https://" + ip + ":" + port + pathAndQuery);
        HttpsURLConnection conn = (HttpsURLConnection) url.openConnection();
        conn.setSSLSocketFactory(getOrBuildPinnedSocketFactory(expectedFp, observedFpOut));
        conn.setHostnameVerifier(LAN_HOST_VERIFIER);
        conn.setConnectTimeout(connectTimeoutMs);
        conn.setReadTimeout(readTimeoutMs);
        conn.setRequestMethod(method);
        conn.setDoInput(true);
        int code = conn.getResponseCode();
        InputStream is = (code >= 200 && code < 400) ? conn.getInputStream() : conn.getErrorStream();
        if (is == null) throw new IllegalStateException("HTTP " + code);
        ByteArrayOutputStream bos = new ByteArrayOutputStream();
        byte[] buf = new byte[2048];
        int r;
        while ((r = is.read(buf)) > 0) bos.write(buf, 0, r);
        conn.disconnect();
        String body = new String(bos.toByteArray(), StandardCharsets.UTF_8);
        if (code == 401 || code == 403) {
            throw new SecurityException("HTTP " + code + ": " + body);
        }
        if (code >= 400) {
            throw new IllegalStateException("HTTP " + code + ": " + body);
        }
        return body;
    }

    public PairOutcome pairWithPc(String ip, int port, String sixDigitCode, String expectedCertFp) {
        PairOutcome out = new PairOutcome();
        try {
            String[] observedFp = new String[]{""};
            String devName = URLEncoder.encode(Build.MODEL != null ? Build.MODEL : "Android Phone", "UTF-8");
            String osVer = URLEncoder.encode("Android " + Build.VERSION.RELEASE, "UTF-8");
            String q = "/deck/pair?code=" + URLEncoder.encode(sixDigitCode.trim(), "UTF-8")
                    + "&device=" + devName
                    + "&model=" + devName
                    + "&os=" + osVer
                    + "&app=1.0.5";
            String normalizedExpected = PairingUriParser.normalizeFingerprint(expectedCertFp);
            String body = requestJson("POST", ip.trim(), port <= 0 ? 47825 : port, q,
                    normalizedExpected, 5000, 45000, observedFp);
            JSONObject obj = new JSONObject(body);
            if (!obj.optBoolean("paired", false)) {
                out.ok = false;
                out.error = obj.optString("error", "Pairing rejected");
                return out;
            }
            String certFp = PairingUriParser.normalizeFingerprint(obj.optString("certFingerprint", observedFp[0]));
            if (certFp.isEmpty()) certFp = PairingUriParser.normalizeFingerprint(observedFp[0]);
            store.savePairing(
                    obj.optString("token", ""),
                    ip.trim(),
                    port <= 0 ? 47825 : port,
                    certFp,
                    obj.optString("mac", ""),
                    obj.optString("pcName", "Desk PC"),
                    obj.optString("keyFingerprint", "")
            );
            out.ok = store.isPaired();
            if (!out.ok) out.error = "Missing pairing credentials";
        } catch (Exception ex) {
            out.ok = false;
            out.error = ex.getMessage() != null ? ex.getMessage() : "Connection failed";
        }
        return out;
    }

    public ProbeResult discoverViaUdpBroadcast() {
        try (DatagramSocket sock = new DatagramSocket()) {
            sock.setBroadcast(true);
            sock.setSoTimeout(900);
            byte[] req = "NOTCHPIXEL_DECK_DISCOVER".getBytes(StandardCharsets.UTF_8);
            sock.send(new DatagramPacket(req, req.length, InetAddress.getByName("255.255.255.255"), DISCOVERY_UDP_PORT));
            byte[] buf = new byte[1024];
            DatagramPacket resp = new DatagramPacket(buf, buf.length);
            sock.receive(resp);
            String json = new String(resp.getData(), 0, resp.getLength(), StandardCharsets.UTF_8);
            JSONObject o = new JSONObject(json);
            ProbeResult pr = new ProbeResult();
            pr.pcName = o.optString("pcName", "Desk PC");
            pr.ip = o.optString("ip", resp.getAddress().getHostAddress());
            pr.tlsPort = o.optInt("tlsPort", 47825);
            pr.certFingerprint = PairingUriParser.normalizeFingerprint(o.optString("certFingerprint", ""));
            pr.mac = o.optString("mac", "");
            return pr;
        } catch (Exception ignored) {
            return null;
        }
    }

    private String resolveLocalIpv4Prefix() {
        try {
            WifiManager wm = (WifiManager) appContext.getSystemService(Context.WIFI_SERVICE);
            if (wm != null) {
                WifiInfo wi = wm.getConnectionInfo();
                if (wi != null) {
                    int ipInt = wi.getIpAddress();
                    if (ipInt != 0) {
                        return String.format(Locale.US, "%d.%d.%d.",
                                (ipInt & 0xff), ((ipInt >> 8) & 0xff), ((ipInt >> 16) & 0xff));
                    }
                }
            }
        } catch (Exception ignored) { }

        try {
            Enumeration<NetworkInterface> ifaces = NetworkInterface.getNetworkInterfaces();
            if (ifaces != null) {
                while (ifaces.hasMoreElements()) {
                    NetworkInterface ni = ifaces.nextElement();
                    if (!ni.isUp() || ni.isLoopback()) continue;
                    Enumeration<InetAddress> addrs = ni.getInetAddresses();
                    while (addrs.hasMoreElements()) {
                        InetAddress addr = addrs.nextElement();
                        if (addr instanceof Inet4Address && addr.isSiteLocalAddress() && !addr.isLoopbackAddress()) {
                            byte[] b = addr.getAddress();
                            return String.format(Locale.US, "%d.%d.%d.",
                                    (b[0] & 0xFF), (b[1] & 0xFF), (b[2] & 0xFF));
                        }
                    }
                }
            }
        } catch (Exception ignored) { }
        return null;
    }

    public boolean rediscoverPcIp() {
        String pinnedFp = PairingUriParser.normalizeFingerprint(store.getCertFingerprint());
        if (pinnedFp.isEmpty()) return false;

        ProbeResult udp = discoverViaUdpBroadcast();
        if (udp != null && !udp.ip.isEmpty() && pinnedFp.equals(udp.certFingerprint)) {
            store.updatePcIp(udp.ip);
            return true;
        }

        // Fallback: scan local Wi-Fi /24 subnet for pinned /deck/probe cert match, returning on first match
        String prefix = resolveLocalIpv4Prefix();
        if (prefix == null || prefix.isEmpty()) return false;

        final int port = store.getTlsPort();
        ExecutorService pool = Executors.newFixedThreadPool(24);
        try {
            CompletionService<String> ecs = new ExecutorCompletionService<>(pool);
            List<Future<String>> futures = new ArrayList<>(254);
            for (int i = 1; i <= 254; i++) {
                final String candidate = prefix + i;
                futures.add(ecs.submit(() -> {
                    try {
                        String json = requestJson("GET", candidate, port, "/deck/probe", pinnedFp, 180, 220, null);
                        JSONObject o = new JSONObject(json);
                        String respFp = PairingUriParser.normalizeFingerprint(o.optString("certFingerprint", ""));
                        if (pinnedFp.equals(respFp)) {
                            return candidate;
                        }
                    } catch (Exception ignored) { }
                    return null;
                }));
            }
            for (int i = 0; i < 254; i++) {
                Future<String> completed = ecs.take();
                String found = completed.get();
                if (found != null && !found.isEmpty()) {
                    store.updatePcIp(found);
                    return true;
                }
            }
        } catch (Exception ignored) {
        } finally {
            pool.shutdownNow();
        }
        return false;
    }

    private String authedCall(String method, String pathPrefix) throws Exception {
        if (!store.isPaired()) throw new IllegalStateException("Not paired");
        String sep = pathPrefix.contains("?") ? "&" : "?";
        String tokEnc = URLEncoder.encode(store.getToken(), "UTF-8");
        String query = pathPrefix + sep + "token=" + tokEnc;
        String pinnedFp = PairingUriParser.normalizeFingerprint(store.getCertFingerprint());
        try {
            return requestJson(method, store.getPcIp(), store.getTlsPort(), query,
                    pinnedFp, 1600, 2000, null);
        } catch (SecurityException se) {
            throw se;
        } catch (Exception netEx) {
            if (rediscoverPcIp()) {
                return requestJson(method, store.getPcIp(), store.getTlsPort(), query,
                        pinnedFp, 1600, 2000, null);
            }
            throw netEx;
        }
    }

    public PcState fetchState() throws Exception {
        String json = authedCall("GET", "/deck/state");
        JSONObject o = new JSONObject(json);
        PcState st = new PcState();
        st.pcName = o.optString("pcName", store.getPcName());
        st.cpu = o.optInt("cpu", 0);
        st.gpu = o.optInt("gpu", 0);
        st.ram = o.optInt("ram", 0);
        st.cpuTempAvailable = o.optBoolean("cpuTempAvailable", false);
        st.cpuTempC = o.optInt("cpuTempC", 0);
        st.gpuTempAvailable = o.optBoolean("gpuTempAvailable", false);
        st.gpuTempC = o.optInt("gpuTempC", 0);
        st.recording = o.optBoolean("recording", false);
        st.recordingSec = o.optInt("recordingSec", 0);
        st.micMuted = o.optBoolean("micMuted", false);
        st.volume = o.optInt("volume", 50);
        st.muted = o.optBoolean("muted", false);

        JSONObject th = o.optJSONObject("theme");
        if (th != null) {
            st.themeName = th.optString("name", "LiquidGlass");
            st.themeIsLight = th.optBoolean("isLight", false);
            st.themeIsGlass = th.optBoolean("isGlass", true);
            st.themeBlurRadius = th.optInt("blurRadius", 20);
            st.themeAccentHex = th.optString("accentHex", "#2BE4EA");
            st.themeSurfaceHex = th.optString("surfaceHex", "#101216");
        }

        JSONObject m = o.optJSONObject("media");
        if (m != null) {
            st.hasMedia = m.optBoolean("hasSession", false);
            st.isPlaying = m.optBoolean("isPlaying", false);
            st.mediaTitle = m.optString("title", "");
            st.mediaArtist = m.optString("artist", "");
            st.mediaApp = m.optString("app", "");
            st.posSec = m.optInt("posSec", 0);
            st.durSec = m.optInt("durSec", 0);
            st.accentHex = m.optString("accentHex", "#2BE4EA");
            st.albumArtB64 = m.optString("albumArtB64", "");
            st.audioLevel = m.optInt("audioLevel", 0);
        }

        JSONArray arr = o.optJSONArray("apps");
        if (arr != null) {
            for (int i = 0; i < arr.length(); i++) {
                JSONObject item = arr.getJSONObject(i);
                AppSlot slot = new AppSlot();
                slot.index = item.optInt("index", i);
                slot.label = item.optString("label", "Slot " + (i + 1));
                slot.target = item.optString("target", "");
                slot.configured = item.optBoolean("configured", false);
                slot.iconB64 = item.optString("iconB64", "");
                st.apps.add(slot);
            }
        }

        JSONArray shelfArr = o.optJSONArray("shelf");
        if (shelfArr != null) {
            for (int i = 0; i < shelfArr.length(); i++) {
                JSONObject item = shelfArr.getJSONObject(i);
                ShelfItem sf = new ShelfItem();
                sf.index = item.optInt("index", i);
                sf.name = item.optString("name", "file");
                sf.ext = item.optString("ext", "");
                sf.sizeBytes = item.optLong("sizeBytes", 0L);
                sf.isImage = item.optBoolean("isImage", false);
                sf.thumbB64 = item.optString("thumbB64", "");
                st.shelf.add(sf);
            }
        }

        JSONArray clipArr = o.optJSONArray("clipboard");
        if (clipArr != null) {
            for (int i = 0; i < clipArr.length(); i++) {
                JSONObject item = clipArr.getJSONObject(i);
                ClipItem ci = new ClipItem();
                ci.index = item.optInt("index", i);
                ci.text = item.optString("text", "");
                ci.pinned = item.optBoolean("pinned", false);
                st.clipboard.add(ci);
            }
        }

        JSONArray todoArr = o.optJSONArray("todos");
        if (todoArr != null) {
            for (int i = 0; i < todoArr.length(); i++) {
                JSONObject item = todoArr.getJSONObject(i);
                TodoItem ti = new TodoItem();
                ti.index = item.optInt("index", i);
                ti.text = item.optString("text", "");
                ti.done = item.optBoolean("done", false);
                st.todos.add(ti);
            }
        }
        return st;
    }

    public List<DeviceInfo> fetchDevices() throws Exception {
        String json = authedCall("GET", "/deck/devices");
        JSONObject o = new JSONObject(json);
        List<DeviceInfo> list = new ArrayList<>();
        JSONArray arr = o.optJSONArray("devices");
        if (arr != null) {
            for (int i = 0; i < arr.length(); i++) {
                JSONObject item = arr.getJSONObject(i);
                DeviceInfo d = new DeviceInfo();
                d.deviceId = item.optString("deviceId", "");
                d.deviceName = item.optString("deviceName", "");
                d.model = item.optString("model", "");
                d.osVersion = item.optString("osVersion", "");
                d.appVersion = item.optString("appVersion", "");
                d.ipAddress = item.optString("ipAddress", "");
                d.keyFingerprint = item.optString("keyFingerprint", "");
                d.pairedAtMs = item.optLong("pairedAtMs", 0L);
                d.isCurrent = item.optBoolean("isCurrent", false);
                list.add(d);
            }
        }
        return list;
    }

    public boolean unpairWithPc() {
        if (!store.isPaired()) return false;
        try {
            String tokEnc = URLEncoder.encode(store.getToken(), "UTF-8");
            String query = "/deck/unpair?token=" + tokEnc;
            String fp = PairingUriParser.normalizeFingerprint(store.getCertFingerprint());
            String resp = requestJson("POST", store.getPcIp(), store.getTlsPort(), query, fp, 1200, 1500, null);
            JSONObject obj = new JSONObject(resp);
            return obj.optBoolean("unpaired", false);
        } catch (Exception ignored) {
            return false;
        }
    }

    public void sendPadUdp(String pipePayload) {
        if (!store.isPaired()) return;
        String ip = store.getPcIp();
        String tok = store.getToken();
        if (ip == null || ip.isEmpty() || tok == null || tok.isEmpty()) return;
        try {
            byte[] data = ("PAD|" + tok + "|" + pipePayload).getBytes(StandardCharsets.UTF_8);
            InetAddress addr = InetAddress.getByName(ip);
            synchronized (udpLock) {
                if (padUdpSocket == null || padUdpSocket.isClosed()) {
                    padUdpSocket = new DatagramSocket();
                }
                padUdpSocket.send(new DatagramPacket(data, data.length, addr, 47826));
            }
        } catch (Exception ignored) { }
    }

    public void sendPadAction(String actionQuery) throws Exception {
        authedCall("POST", "/deck/pad?" + actionQuery);
    }

    public void sendMediaAction(String actionQuery) throws Exception {
        authedCall("POST", "/deck/media?" + actionQuery);
    }

    public void launchAppSlot(int index) throws Exception {
        authedCall("POST", "/deck/app?idx=" + index);
    }

    public void addAppSlot(String label, String path) throws Exception {
        String q = "a=add&label=" + URLEncoder.encode(label, "UTF-8")
                + "&path=" + URLEncoder.encode(path, "UTF-8");
        authedCall("POST", "/deck/app?" + q);
    }

    public void sendModeAction(String action) throws Exception {
        authedCall("POST", "/deck/mode?a=" + URLEncoder.encode(action, "UTF-8"));
    }

    public boolean sendWakeUdp() {
        if (!store.isPaired()) return false;
        String ip = store.getPcIp();
        String tok = store.getToken();
        if (ip == null || ip.isEmpty() || tok == null || tok.isEmpty()) return false;
        try {
            byte[] data = ("WAKE|" + tok).getBytes(StandardCharsets.UTF_8);
            InetAddress addr = InetAddress.getByName(ip);
            synchronized (udpLock) {
                if (padUdpSocket == null || padUdpSocket.isClosed()) {
                    padUdpSocket = new DatagramSocket();
                }
                for (int i = 0; i < 3; i++) {
                    padUdpSocket.send(new DatagramPacket(data, data.length, addr, 47826));
                }
            }
            return true;
        } catch (Exception ignored) { return false; }
    }

    public void sendClipboardToPc(String text) throws Exception {
        authedCall("POST", "/deck/clipboard?text=" + URLEncoder.encode(text == null ? "" : text, "UTF-8"));
    }

    public void toggleTodo(int index) throws Exception {
        authedCall("POST", "/deck/todo?a=toggle&idx=" + index);
    }

    public void addTodo(String text) throws Exception {
        authedCall("POST", "/deck/todo?a=add&text=" + URLEncoder.encode(text == null ? "" : text, "UTF-8"));
    }

    public void testAudio() throws Exception {
        authedCall("POST", "/deck/media?a=test_audio");
    }

    public byte[] downloadShelfFile(int index) throws Exception {
        if (!store.isPaired()) throw new IllegalStateException("Not paired");
        String ip = store.getPcIp();
        int port = store.getTlsPort();
        String fp = PairingUriParser.normalizeFingerprint(store.getCertFingerprint());
        String tok = URLEncoder.encode(store.getToken(), "UTF-8");
        URL url = new URL("https://" + ip + ":" + port + "/deck/shelf/file?idx=" + index + "&token=" + tok);
        HttpsURLConnection conn = (HttpsURLConnection) url.openConnection();
        conn.setSSLSocketFactory(getOrBuildPinnedSocketFactory(fp, null));
        conn.setHostnameVerifier(LAN_HOST_VERIFIER);
        conn.setConnectTimeout(3500);
        conn.setReadTimeout(12000);
        conn.setRequestMethod("GET");
        conn.setDoInput(true);
        int code = conn.getResponseCode();
        if (code < 200 || code >= 300) {
            conn.disconnect();
            throw new IllegalStateException("HTTP " + code);
        }
        InputStream is = conn.getInputStream();
        ByteArrayOutputStream bos = new ByteArrayOutputStream();
        byte[] buf = new byte[8192];
        int r;
        while ((r = is.read(buf)) > 0) bos.write(buf, 0, r);
        conn.disconnect();
        return bos.toByteArray();
    }

    public void sendPowerAction(String action) throws Exception {
        authedCall("POST", "/deck/power?a=" + URLEncoder.encode(action, "UTF-8"));
    }
}
