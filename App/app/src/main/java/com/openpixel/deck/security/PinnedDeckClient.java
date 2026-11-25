package com.openpixel.deck.security;

import android.content.Context;
import android.net.wifi.WifiInfo;
import android.net.wifi.WifiManager;
import android.os.Build;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.net.URL;
import java.net.URLEncoder;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.cert.CertificateException;
import java.security.cert.X509Certificate;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;
import java.util.concurrent.Callable;
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
        public final List<AppSlot> apps = new ArrayList<>();
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
        final String expectedLower = expectedFp == null ? "" : expectedFp.trim().toLowerCase(Locale.US);
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

    private String requestJson(String method, String ip, int port, String pathAndQuery,
                               String expectedFp, int timeoutMs, String[] observedFpOut) throws Exception {
        URL url = new URL("https://" + ip + ":" + port + pathAndQuery);
        HttpsURLConnection conn = (HttpsURLConnection) url.openConnection();
        conn.setSSLSocketFactory(buildPinnedSocketFactory(expectedFp, observedFpOut));
        conn.setHostnameVerifier(LAN_HOST_VERIFIER);
        conn.setConnectTimeout(timeoutMs);
        conn.setReadTimeout(timeoutMs);
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
            String body = requestJson("POST", ip.trim(), port <= 0 ? 47825 : port, q, expectedCertFp, 15000, observedFp);
            JSONObject obj = new JSONObject(body);
            if (!obj.optBoolean("paired", false)) {
                out.ok = false;
                out.error = obj.optString("error", "Pairing rejected");
                return out;
            }
            String certFp = obj.optString("certFingerprint", observedFp[0]);
            if (certFp.isEmpty()) certFp = observedFp[0];
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
            byte[] req = "OPENPIXEL_DECK_DISCOVER".getBytes(StandardCharsets.UTF_8);
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
            pr.certFingerprint = o.optString("certFingerprint", "").toLowerCase(Locale.US);
            pr.mac = o.optString("mac", "");
            return pr;
        } catch (Exception ignored) {
            return null;
        }
    }

    public boolean rediscoverPcIp() {
        String pinnedFp = store.getCertFingerprint();
        if (pinnedFp.isEmpty()) return false;

        ProbeResult udp = discoverViaUdpBroadcast();
        if (udp != null && !udp.ip.isEmpty() && pinnedFp.equals(udp.certFingerprint)) {
            store.updatePcIp(udp.ip);
            return true;
        }

        // Fallback: scan local Wi-Fi /24 subnet for pinned /deck/probe cert match
        try {
            WifiManager wm = (WifiManager) appContext.getSystemService(Context.WIFI_SERVICE);
            if (wm == null) return false;
            WifiInfo wi = wm.getConnectionInfo();
            int ipInt = wi.getIpAddress();
            if (ipInt == 0) return false;
            final String prefix = String.format(Locale.US, "%d.%d.%d.",
                    (ipInt & 0xff), ((ipInt >> 8) & 0xff), ((ipInt >> 16) & 0xff));
            final int port = store.getTlsPort();
            ExecutorService pool = Executors.newFixedThreadPool(16);
            List<Callable<String>> tasks = new ArrayList<>();
            for (int i = 1; i <= 254; i++) {
                final String candidate = prefix + i;
                tasks.add(() -> {
                    try {
                        String json = requestJson("GET", candidate, port, "/deck/probe", pinnedFp, 180, null);
                        JSONObject o = new JSONObject(json);
                        if (pinnedFp.equalsIgnoreCase(o.optString("certFingerprint", ""))) {
                            return candidate;
                        }
                    } catch (Exception ignored) { }
                    return null;
                });
            }
            List<Future<String>> results = pool.invokeAll(tasks);
            pool.shutdownNow();
            for (Future<String> f : results) {
                String found = f.get();
                if (found != null && !found.isEmpty()) {
                    store.updatePcIp(found);
                    return true;
                }
            }
        } catch (Exception ignored) { }
        return false;
    }

    private String authedCall(String method, String pathPrefix) throws Exception {
        if (!store.isPaired()) throw new IllegalStateException("Not paired");
        String sep = pathPrefix.contains("?") ? "&" : "?";
        String tokEnc = URLEncoder.encode(store.getToken(), "UTF-8");
        String query = pathPrefix + sep + "token=" + tokEnc;
        try {
            return requestJson(method, store.getPcIp(), store.getTlsPort(), query,
                    store.getCertFingerprint(), 1600, null);
        } catch (SecurityException se) {
            throw se;
        } catch (Exception netEx) {
            if (rediscoverPcIp()) {
                return requestJson(method, store.getPcIp(), store.getTlsPort(), query,
                        store.getCertFingerprint(), 1600, null);
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
                st.apps.add(slot);
            }
        }
        return st;
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

    public void sendPowerAction(String action) throws Exception {
        authedCall("POST", "/deck/power?a=" + URLEncoder.encode(action, "UTF-8"));
    }
}
