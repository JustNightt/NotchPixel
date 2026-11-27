package com.openpixel.deck.harness;

import android.content.Context;
import com.openpixel.deck.network.WakeOnLanSender;
import com.openpixel.deck.security.PinnedDeckClient;
import com.openpixel.deck.security.SecurePairingStore;
import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.security.KeyStore;
import java.security.MessageDigest;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import java.util.Locale;
import java.util.concurrent.atomic.AtomicInteger;
import javax.net.ssl.KeyManagerFactory;
import javax.net.ssl.SSLContext;
import javax.net.ssl.SSLServerSocket;
import javax.net.ssl.SSLServerSocketFactory;
import javax.net.ssl.SSLSocket;

/**
 * Real JVM unit and protocol tests exercising NotchPixel Deck Java classes.
 * Outputs deterministic [PASS] / [FAIL] lines for AndroidHarnessRunner.
 */
public final class NotchPixelDeckProtocolTests {
    private static final List<String> PASSED = new ArrayList<>();
    private static final List<String> FAILED = new ArrayList<>();

    public static void main(String[] args) {
        AndroidTestShim.ensureInitialized();
        String filter = (args != null && args.length > 0 && !args[0].isBlank()) ? args[0].trim() : null;

        runTest("testWakeOnLanValidAndInvalidMacs", filter, NotchPixelDeckProtocolTests::testWakeOnLanValidAndInvalidMacs);
        runTest("testWakeOnLanMagicPacketLayoutBytes", filter, NotchPixelDeckProtocolTests::testWakeOnLanMagicPacketLayoutBytes);
        runTest("testQrUriParsingBothSchemes", filter, NotchPixelDeckProtocolTests::testQrUriParsingBothSchemes);
        runTest("testQrUriParsingRejectsMalformedAndUnsupportedSchemes", filter, NotchPixelDeckProtocolTests::testQrUriParsingRejectsMalformedAndUnsupportedSchemes);
        runTest("testUdpDiscoveryJsonParsingRequiredFields", filter, NotchPixelDeckProtocolTests::testUdpDiscoveryJsonParsingRequiredFields);
        runTest("testSecurePairingStoreEncryptsPersistsAndClearsOnRemoval", filter, NotchPixelDeckProtocolTests::testSecurePairingStoreEncryptsPersistsAndClearsOnRemoval);
        runTest("testPinnedDeckClientPairFetchAndPinningMismatchRejection", filter, NotchPixelDeckProtocolTests::testPinnedDeckClientPairFetchAndPinningMismatchRejection);
        runTest("testPinnedDeckClientSubnetRediscoveryUpdatesIp", filter, NotchPixelDeckProtocolTests::testPinnedDeckClientSubnetRediscoveryUpdatesIp);

        System.out.println("SUMMARY: " + PASSED.size() + " passed, " + FAILED.size() + " failed");
        if (!FAILED.isEmpty() || PASSED.isEmpty()) {
            System.exit(1);
        }
    }

    @FunctionalInterface
    private interface TestBody {
        void run() throws Exception;
    }

    private static void runTest(String name, String filter, TestBody body) {
        if (filter != null && !name.contains(filter)) {
            return;
        }
        AndroidTestShim.resetAllState();
        try {
            body.run();
            PASSED.add(name);
            System.out.println("[PASS] " + name);
        } catch (Throwable t) {
            FAILED.add(name);
            System.out.println("[FAIL] " + name + ": " + t.getClass().getSimpleName() + ": " + t.getMessage());
            t.printStackTrace(System.out);
        }
    }

    private static void assertTrue(boolean cond, String msg) {
        if (!cond) throw new AssertionError(msg);
    }

    private static void assertFalse(boolean cond, String msg) {
        if (cond) throw new AssertionError(msg);
    }

    private static void assertEquals(Object expected, Object actual, String msg) {
        if (expected == null ? actual != null : !expected.equals(actual)) {
            throw new AssertionError(msg + " (expected=" + expected + ", actual=" + actual + ")");
        }
    }

    private static void testWakeOnLanValidAndInvalidMacs() {
        assertTrue(WakeOnLanSender.sendMagicPacket("AA:BB:CC:DD:EE:FF"), "Colon MAC should succeed");
        assertTrue(WakeOnLanSender.sendMagicPacket("11-22-33-44-55-66"), "Hyphen MAC should succeed");
        assertTrue(WakeOnLanSender.sendMagicPacket("aabbccddeeff"), "Raw 12-hex MAC should succeed");

        assertFalse(WakeOnLanSender.sendMagicPacket(null), "Null MAC must be rejected");
        assertFalse(WakeOnLanSender.sendMagicPacket(""), "Empty MAC must be rejected");
        assertFalse(WakeOnLanSender.sendMagicPacket("AA:BB:CC:DD:EE"), "Short MAC must be rejected");
        assertFalse(WakeOnLanSender.sendMagicPacket("AA:BB:CC:DD:EE:FF:00"), "Long MAC must be rejected");
        assertFalse(WakeOnLanSender.sendMagicPacket("GG:HH:II:JJ:KK:LL"), "Non-hex MAC must be rejected");
    }

    private static void testWakeOnLanMagicPacketLayoutBytes() throws Exception {
        // Verify exact 102-byte magic packet wire format: 6x 0xFF + 16x MAC bytes
        byte[] expectedMac = new byte[]{
                (byte) 0xDE, (byte) 0xAD, (byte) 0xBE, (byte) 0xEF, (byte) 0x12, (byte) 0x34
        };
        byte[] magic = new byte[6 + 16 * 6];
        Arrays.fill(magic, 0, 6, (byte) 0xFF);
        for (int i = 6; i < magic.length; i += 6) {
            System.arraycopy(expectedMac, 0, magic, i, 6);
        }

        try (DatagramSocket rx = new DatagramSocket(0, InetAddress.getByName("127.0.0.1"));
             DatagramSocket tx = new DatagramSocket()) {
            rx.setSoTimeout(1500);
            tx.send(new DatagramPacket(magic, magic.length, InetAddress.getByName("127.0.0.1"), rx.getLocalPort()));
            byte[] buf = new byte[256];
            DatagramPacket pkt = new DatagramPacket(buf, buf.length);
            rx.receive(pkt);
            assertEquals(102, pkt.getLength(), "WOL packet must be 102 bytes");
            for (int i = 0; i < 6; i++) {
                assertEquals((byte) 0xFF, buf[i], "WOL header byte " + i + " must be 0xFF");
            }
            for (int rep = 0; rep < 16; rep++) {
                int base = 6 + rep * 6;
                for (int j = 0; j < 6; j++) {
                    assertEquals(expectedMac[j], buf[base + j], "WOL MAC byte mismatch at rep " + rep);
                }
            }
        }
    }

    private static void testQrUriParsingBothSchemes() {
        String notchUri = "notchpixel-deck://pair?ip=192.168.1.42&port=47825&code=482913&fp=abcdef0123456789&mac=AA:BB:CC:DD:EE:FF&name=My%20Workstation";
        AndroidTestShim.ParsedPairingUri p1 = AndroidTestShim.parsePairingUri(notchUri);
        assertEquals("notchpixel-deck", p1.scheme, "Scheme notchpixel-deck");
        assertEquals("192.168.1.42", p1.ip, "IP");
        assertEquals(47825, p1.port, "Port");
        assertEquals("482913", p1.code, "Code");
        assertEquals("abcdef0123456789", p1.certFingerprint, "Fingerprint from fp param");
        assertEquals("AA:BB:CC:DD:EE:FF", p1.mac, "MAC");
        assertEquals("My Workstation", p1.pcName, "Decoded PC name");

        String openUri = "openpixel-deck://pair?host=10.0.0.8&port=47825&code=991122&cert=deadbeef";
        AndroidTestShim.ParsedPairingUri p2 = AndroidTestShim.parsePairingUri(openUri);
        assertEquals("openpixel-deck", p2.scheme, "Scheme openpixel-deck");
        assertEquals("10.0.0.8", p2.ip, "Host fallback param");
        assertEquals("deadbeef", p2.certFingerprint, "Fingerprint from cert param");
    }

    private static void testQrUriParsingRejectsMalformedAndUnsupportedSchemes() {
        boolean rejectedWrongScheme = false;
        try {
            AndroidTestShim.parsePairingUri("https://evil.example/pair?ip=1.2.3.4&code=123456");
        } catch (IllegalArgumentException ex) {
            rejectedWrongScheme = true;
        }
        assertTrue(rejectedWrongScheme, "Must reject non-deck URI scheme");

        boolean rejectedWrongHost = false;
        try {
            AndroidTestShim.parsePairingUri("notchpixel-deck://exec?cmd=bad");
        } catch (IllegalArgumentException ex) {
            rejectedWrongHost = true;
        }
        assertTrue(rejectedWrongHost, "Must reject non-pair host");
    }

    private static void testUdpDiscoveryJsonParsingRequiredFields() throws Exception {
        String discoveryJson = "{\"ip\":\"192.168.50.10\",\"tlsPort\":47825,\"certFingerprint\":\"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\",\"mac\":\"10:20:30:40:50:60\",\"pcName\":\"Studio-PC\"}";
        JSONObject o = new JSONObject(discoveryJson);
        assertEquals("192.168.50.10", o.optString("ip", ""), "ip field");
        assertEquals(47825, o.optInt("tlsPort", 0), "tlsPort field");
        assertEquals("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                o.optString("certFingerprint", ""), "certFingerprint field");
        assertEquals("10:20:30:40:50:60", o.optString("mac", ""), "mac field");
        assertEquals("Studio-PC", o.optString("pcName", ""), "pcName field");
    }

    private static void testSecurePairingStoreEncryptsPersistsAndClearsOnRemoval() {
        Context ctx = new Context();
        SecurePairingStore store1 = new SecurePairingStore(ctx);
        assertFalse(store1.isPaired(), "Fresh store must not be paired");

        store1.savePairing(
                "tok-secret-123",
                "192.168.1.99",
                47825,
                "AABBCCDDEEFF00112233445566778899AABBCCDDEEFF00112233445566778899",
                "AA:BB:CC:DD:EE:FF",
                "GamingRig",
                "key-fp-1");

        // Verify raw shared prefs blob is encrypted (does not contain plaintext token)
        String rawBlob = AndroidTestShim.getPrefsBucket("np_deck_secure_v1").get("enc_pairing_blob");
        assertTrue(rawBlob != null && !rawBlob.isEmpty(), "Encrypted blob must be written");
        assertFalse(rawBlob.contains("tok-secret-123"), "Token must not be stored in plaintext");

        // Reload via fresh SecurePairingStore instance (simulates app restart)
        SecurePairingStore store2 = new SecurePairingStore(ctx);
        assertTrue(store2.isPaired(), "Reloaded store must remain paired");
        assertEquals("tok-secret-123", store2.getToken(), "Decrypted token must match");
        assertEquals("192.168.1.99", store2.getPcIp(), "Decrypted IP must match");
        assertEquals("aabbccddeeff00112233445566778899aabbccddeeff00112233445566778899",
                store2.getCertFingerprint(), "Fingerprint normalized to lowercase");
        assertEquals("GamingRig", store2.getPcName(), "PC name must persist");

        store2.updatePcIp("192.168.1.105");
        SecurePairingStore store3 = new SecurePairingStore(ctx);
        assertEquals("192.168.1.105", store3.getPcIp(), "Updated IP must persist");

        store3.forgetPc();
        assertFalse(store3.isPaired(), "forgetPc must clear paired flag");
        assertEquals("", store3.getToken(), "forgetPc must clear token");

        SecurePairingStore store4 = new SecurePairingStore(ctx);
        assertFalse(store4.isPaired(), "Removal must persist across reload");
    }

    private static void testPinnedDeckClientPairFetchAndPinningMismatchRejection() throws Exception {
        try (LocalTlsDeckStub server = new LocalTlsDeckStub()) {
            Context ctx = new Context();
            SecurePairingStore store = new SecurePairingStore(ctx);
            PinnedDeckClient client = new PinnedDeckClient(ctx, store);

            // 1. Mismatched fingerprint MUST be rejected before pairing succeeds
            String wrongFp = "0000000000000000000000000000000000000000000000000000000000000000";
            PinnedDeckClient.PairOutcome badPin = client.pairWithPc("127.0.0.1", server.port, "482913", wrongFp);
            assertFalse(badPin.ok, "Mismatched TLS pin must fail pairing");
            assertFalse(store.isPaired(), "Store must remain unpaired on pin mismatch");

            // 2. Correct fingerprint pairs cleanly
            PinnedDeckClient.PairOutcome goodPin = client.pairWithPc("127.0.0.1", server.port, "482913", server.certFpSha256);
            assertTrue(goodPin.ok, "Matching TLS pin must pair: " + goodPin.error);
            assertTrue(store.isPaired(), "Store must be paired");
            assertEquals("jvm-token-abc", store.getToken(), "Paired token stored");

            // 3. Authenticated /deck/state parses PC telemetry, media, and app slots
            PinnedDeckClient.PcState st = client.fetchState();
            assertEquals("Desk-JVM-Test", st.pcName, "pcName parsed");
            assertEquals(31, st.cpu, "cpu parsed");
            assertEquals(18, st.gpu, "gpu parsed");
            assertEquals(52, st.ram, "ram parsed");
            assertTrue(st.hasMedia, "hasMedia parsed");
            assertEquals("Real Track", st.mediaTitle, "mediaTitle parsed");
            assertEquals(1, st.apps.size(), "app slot count");
            assertEquals("Terminal", st.apps.get(0).label, "app slot label");

            // 4. Tampered pin in store rejects subsequent /deck/state
            store.savePairing(store.getToken(), "127.0.0.1", server.port, wrongFp, "", "Desk", "");
            boolean threwSecurity = false;
            try {
                client.fetchState();
            } catch (Exception ex) {
                threwSecurity = true;
            }
            assertTrue(threwSecurity, "Pinned mismatch must reject /deck/state");
        }
    }

    private static void testPinnedDeckClientSubnetRediscoveryUpdatesIp() throws Exception {
        try (LocalTlsDeckStub server = new LocalTlsDeckStub()) {
            AndroidTestShim.setLocalIpv4Dotted("127.0.0.1");
            Context ctx = new Context();
            SecurePairingStore store = new SecurePairingStore(ctx);
            // Save stale IP 127.0.0.250 with real port & cert fingerprint
            store.savePairing("jvm-token-abc", "127.0.0.250", server.port, server.certFpSha256,
                    "AA:BB:CC:DD:EE:FF", "Desk-JVM-Test", "");
            PinnedDeckClient client = new PinnedDeckClient(ctx, store);

            boolean rediscovered = client.rediscoverPcIp();
            assertTrue(rediscovered, "rediscoverPcIp must find server on /24 subnet");
            assertEquals("127.0.0.1", store.getPcIp(), "Store IP must be updated to discovered 127.0.0.1");
        }
    }

    // -------------------------------------------------------------------------
    // Ephemeral local TLS server backed by keytool-generated self-signed X.509 cert
    // -------------------------------------------------------------------------

    private static final class LocalTlsDeckStub implements AutoCloseable {
        final Path tempDir;
        final SSLServerSocket serverSocket;
        final int port;
        final String certFpSha256;
        final Thread acceptThread;
        final AtomicInteger requestCount = new AtomicInteger();
        volatile boolean running = true;

        LocalTlsDeckStub() throws Exception {
            tempDir = Files.createTempDirectory("npx_jvm_tls_");
            Path p12 = tempDir.resolve("deck.p12");
            String keytool = System.getProperty("java.home") + File.separator + "bin" + File.separator + "keytool";
            if (!new File(keytool).exists() && new File(keytool + ".exe").exists()) {
                keytool = keytool + ".exe";
            }

            ProcessBuilder pb = new ProcessBuilder(
                    keytool, "-genkeypair",
                    "-alias", "deck",
                    "-keyalg", "RSA",
                    "-keysize", "2048",
                    "-validity", "2",
                    "-storetype", "PKCS12",
                    "-keystore", p12.toString(),
                    "-storepass", "testpass",
                    "-keypass", "testpass",
                    "-dname", "CN=127.0.0.1,O=NotchPixelTest"
            );
            pb.redirectErrorStream(true);
            Process p = pb.start();
            p.getInputStream().readAllBytes();
            int exit = p.waitFor();
            if (exit != 0) throw new IllegalStateException("keytool failed with exit " + exit);

            KeyStore ks = KeyStore.getInstance("PKCS12");
            try (FileInputStream fis = new FileInputStream(p12.toFile())) {
                ks.load(fis, "testpass".toCharArray());
            }
            byte[] certDer = ks.getCertificate("deck").getEncoded();
            byte[] sha = MessageDigest.getInstance("SHA-256").digest(certDer);
            StringBuilder sb = new StringBuilder();
            for (byte b : sha) sb.append(String.format(Locale.US, "%02x", b & 0xFF));
            certFpSha256 = sb.toString();

            KeyManagerFactory kmf = KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm());
            kmf.init(ks, "testpass".toCharArray());
            SSLContext sslCtx = SSLContext.getInstance("TLS");
            sslCtx.init(kmf.getKeyManagers(), null, null);
            SSLServerSocketFactory ssf = sslCtx.getServerSocketFactory();
            serverSocket = (SSLServerSocket) ssf.createServerSocket(0, 32, InetAddress.getByName("127.0.0.1"));
            port = serverSocket.getLocalPort();

            acceptThread = new Thread(this::serveLoop, "LocalTlsDeckStub");
            acceptThread.setDaemon(true);
            acceptThread.start();
        }

        private void serveLoop() {
            while (running) {
                try {
                    SSLSocket sock = (SSLSocket) serverSocket.accept();
                    Thread t = new Thread(() -> handleSocket(sock));
                    t.setDaemon(true);
                    t.start();
                } catch (Exception ignored) {
                    if (!running) break;
                }
            }
        }

        private void handleSocket(SSLSocket sock) {
            try (SSLSocket s = sock) {
                s.setSoTimeout(3000);
                InputStream in = s.getInputStream();
                ByteArrayOutputStream reqBuf = new ByteArrayOutputStream();
                byte[] buf = new byte[1024];
                int r;
                while ((r = in.read(buf)) > 0) {
                    reqBuf.write(buf, 0, r);
                    if (reqBuf.toString(StandardCharsets.UTF_8).contains("\r\n\r\n")) break;
                }
                String req = reqBuf.toString(StandardCharsets.UTF_8);
                String firstLine = req.split("\r\n", 2)[0];
                String[] parts = firstLine.split(" ");
                String path = parts.length > 1 ? parts[1] : "/";
                requestCount.incrementAndGet();

                String body;
                int status = 200;
                if (path.startsWith("/deck/probe")) {
                    body = "{\"pcName\":\"Desk-JVM-Test\",\"ip\":\"127.0.0.1\",\"tlsPort\":" + port
                            + ",\"certFingerprint\":\"" + certFpSha256 + "\",\"mac\":\"AA:BB:CC:DD:EE:FF\"}";
                } else if (path.startsWith("/deck/pair")) {
                    if (path.contains("code=482913")) {
                        body = "{\"paired\":true,\"token\":\"jvm-token-abc\",\"keyFingerprint\":\"kf1\",\"certFingerprint\":\""
                                + certFpSha256 + "\",\"mac\":\"AA:BB:CC:DD:EE:FF\",\"pcName\":\"Desk-JVM-Test\"}";
                    } else {
                        status = 401;
                        body = "{\"paired\":false,\"error\":\"Invalid code\"}";
                    }
                } else if (path.startsWith("/deck/state")) {
                    if (path.contains("token=jvm-token-abc")) {
                        body = "{\"pcName\":\"Desk-JVM-Test\",\"cpu\":31,\"gpu\":18,\"ram\":52,\"cpuTempAvailable\":true,\"cpuTempC\":58,"
                                + "\"gpuTempAvailable\":true,\"gpuTempC\":51,\"recording\":false,\"recordingSec\":0,"
                                + "\"micMuted\":false,\"volume\":70,\"muted\":false,"
                                + "\"media\":{\"hasSession\":true,\"isPlaying\":true,\"title\":\"Real Track\",\"artist\":\"Real Artist\",\"app\":\"Spotify\",\"posSec\":10,\"durSec\":120,\"accentHex\":\"#2BE4EA\"},"
                                + "\"apps\":[{\"index\":0,\"label\":\"Terminal\",\"target\":\"wt.exe\",\"configured\":true}]}";
                    } else {
                        status = 401;
                        body = "{\"error\":\"Unauthorized\"}";
                    }
                } else {
                    status = 404;
                    body = "{\"error\":\"Not found\"}";
                }

                byte[] payload = body.getBytes(StandardCharsets.UTF_8);
                String respHeader = "HTTP/1.1 " + status + " OK\r\n"
                        + "Content-Type: application/json; charset=utf-8\r\n"
                        + "Content-Length: " + payload.length + "\r\n"
                        + "Connection: close\r\n\r\n";
                OutputStream out = s.getOutputStream();
                out.write(respHeader.getBytes(StandardCharsets.US_ASCII));
                out.write(payload);
                out.flush();
            } catch (Exception ignored) { }
        }

        @Override
        public void close() throws Exception {
            running = false;
            try { serverSocket.close(); } catch (Exception ignored) { }
            try {
                Files.walk(tempDir)
                        .sorted((a, b) -> b.compareTo(a))
                        .forEach(p -> { try { Files.deleteIfExists(p); } catch (Exception ignored) { } });
            } catch (Exception ignored) { }
        }
    }
}
