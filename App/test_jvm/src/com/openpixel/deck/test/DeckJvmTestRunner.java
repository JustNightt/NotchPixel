package com.openpixel.deck.test;

import android.content.Context;
import android.net.wifi.WifiManager;
import android.security.keystore.KeyGenParameterSpec;

import com.openpixel.deck.network.WakeOnLanSender;
import com.openpixel.deck.qr.PairingUriParser;
import com.openpixel.deck.qr.QrDecoder;
import com.openpixel.deck.qr.QrReedSolomon;
import com.openpixel.deck.qr.QrVersionTables;
import com.openpixel.deck.security.PinnedDeckClient;
import com.openpixel.deck.security.SecurePairingStore;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.net.URLDecoder;
import java.nio.charset.StandardCharsets;
import java.security.Key;
import java.security.KeyStore;
import java.security.KeyStoreSpi;
import java.security.MessageDigest;
import java.security.Provider;
import java.security.SecureRandom;
import java.security.Security;
import java.security.cert.Certificate;
import java.security.spec.AlgorithmParameterSpec;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Date;
import java.util.Enumeration;
import java.util.HashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.CopyOnWriteArrayList;

import javax.crypto.KeyGenerator;
import javax.crypto.KeyGeneratorSpi;
import javax.crypto.SecretKey;
import javax.net.ssl.KeyManagerFactory;
import javax.net.ssl.SSLContext;
import javax.net.ssl.SSLServerSocket;
import javax.net.ssl.SSLSocket;

public final class DeckJvmTestRunner {
    private static int passed = 0;
    private static int failed = 0;

    private static void check(String name, boolean cond) {
        if (cond) {
            System.out.println("[PASS] " + name);
            passed++;
        } else {
            System.out.println("[FAIL] " + name);
            failed++;
        }
    }

    // In-JVM JCA Provider simulating AndroidKeyStore so the real SecurePairingStore
    // exercises its genuine AES-256-GCM encryption and KeyStore lookup unmodified.
    private static final Map<String, Key> KEYSTORE_KEYS = new ConcurrentHashMap<>();

    public static final class AndroidKeyStoreTestProvider extends Provider {
        public AndroidKeyStoreTestProvider() {
            super("AndroidKeyStore", "1.0", "Desktop JVM Test AndroidKeyStore");
            put("KeyStore.AndroidKeyStore", TestKeyStoreSpi.class.getName());
            put("KeyGenerator.AES", TestKeyGeneratorSpi.class.getName());
        }
    }

    public static final class TestKeyStoreSpi extends KeyStoreSpi {
        @Override public Key engineGetKey(String alias, char[] password) { return KEYSTORE_KEYS.get(alias); }
        @Override public Certificate[] engineGetCertificateChain(String alias) { return null; }
        @Override public Certificate engineGetCertificate(String alias) { return null; }
        @Override public Date engineGetCreationDate(String alias) { return new Date(); }
        @Override public void engineSetKeyEntry(String alias, Key key, char[] password, Certificate[] chain) { KEYSTORE_KEYS.put(alias, key); }
        @Override public void engineSetKeyEntry(String alias, byte[] key, Certificate[] chain) {}
        @Override public void engineSetCertificateEntry(String alias, Certificate cert) {}
        @Override public void engineDeleteEntry(String alias) { KEYSTORE_KEYS.remove(alias); }
        @Override public Enumeration<String> engineAliases() { return Collections.enumeration(KEYSTORE_KEYS.keySet()); }
        @Override public boolean engineContainsAlias(String alias) { return KEYSTORE_KEYS.containsKey(alias); }
        @Override public int engineSize() { return KEYSTORE_KEYS.size(); }
        @Override public boolean engineIsKeyEntry(String alias) { return KEYSTORE_KEYS.containsKey(alias); }
        @Override public boolean engineIsCertificateEntry(String alias) { return false; }
        @Override public String engineGetCertificateAlias(Certificate cert) { return null; }
        @Override public KeyStore.Entry engineGetEntry(String alias, KeyStore.ProtectionParameter protParam) {
            Key k = KEYSTORE_KEYS.get(alias);
            if (k instanceof SecretKey) return new KeyStore.SecretKeyEntry((SecretKey) k);
            return null;
        }
        @Override public boolean engineEntryInstanceOf(String alias, Class<? extends KeyStore.Entry> entryClass) {
            return KEYSTORE_KEYS.containsKey(alias) && entryClass == KeyStore.SecretKeyEntry.class;
        }
        @Override public void engineStore(OutputStream stream, char[] password) {}
        @Override public void engineLoad(InputStream stream, char[] password) {}
    }

    public static final class TestKeyGeneratorSpi extends KeyGeneratorSpi {
        private String alias = "default";
        private int keySize = 256;

        @Override protected void engineInit(SecureRandom random) {}
        @Override protected void engineInit(AlgorithmParameterSpec params, SecureRandom random) {
            if (params instanceof KeyGenParameterSpec) {
                KeyGenParameterSpec spec = (KeyGenParameterSpec) params;
                this.alias = spec.getKeystoreAlias();
                this.keySize = spec.getKeySize();
            }
        }
        @Override protected void engineInit(int keysize, SecureRandom random) { this.keySize = keysize; }
        @Override protected SecretKey engineGenerateKey() {
            try {
                KeyGenerator kg = KeyGenerator.getInstance("AES");
                kg.init(keySize);
                SecretKey sk = kg.generateKey();
                KEYSTORE_KEYS.put(alias, sk);
                return sk;
            } catch (Exception e) {
                throw new RuntimeException(e);
            }
        }
    }

    public static void main(String[] args) throws Exception {
        if (Security.getProvider("AndroidKeyStore") == null) {
            Security.addProvider(new AndroidKeyStoreTestProvider());
        }

        String workDir = args.length > 0 ? args[0] : System.getProperty("java.io.tmpdir");
        testReedSolomon();
        testQrDecoder();
        testPairingUriParser();
        testWakeOnLan();
        testProtocolAndTlsPinning(workDir);

        System.out.println("JVM TESTS SUMMARY: " + passed + " passed, " + failed + " failed");
        if (failed > 0) System.exit(1);
    }

    private static void testReedSolomon() {
        int[] data = new int[28];
        for (int i = 0; i < data.length; i++) data[i] = (i * 17 + 5) & 0xFF;
        int ecCount = 16; // t = 8
        int[] ec = QrReedSolomon.computeEc(data, ecCount);
        int[] cw = new int[data.length + ecCount];
        System.arraycopy(data, 0, cw, 0, data.length);
        System.arraycopy(ec, 0, cw, data.length, ecCount);

        // Corrupt 8 bytes (maximum correctable)
        int[] corrupted = cw.clone();
        for (int i = 0; i < 8; i++) corrupted[i * 4] ^= (0x31 + i);
        boolean ok = QrReedSolomon.decode(corrupted, ecCount);
        boolean matches = ok;
        for (int i = 0; i < cw.length && matches; i++) {
            if (corrupted[i] != cw[i]) matches = false;
        }
        check("Reed-Solomon corrects up to floor(ecCount/2) = 8 symbol errors", matches);

        // Corrupt 9 bytes (uncorrectable)
        int[] over = cw.clone();
        for (int i = 0; i < 9; i++) over[i * 3] ^= 0x77;
        check("Reed-Solomon rejects > t errors cleanly", !QrReedSolomon.decode(over, ecCount));
    }

    private static void testQrDecoder() {
        String notchUri = "notchpixel-deck://pair?ip=192.168.1.42&port=47825&code=123456&cert=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        String openUri = "openpixel-deck://pair?ip=10.0.0.8&port=47825&code=654321&cert=fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

        int[] ecLevels = {QrVersionTables.EC_M, QrVersionTables.EC_L, QrVersionTables.EC_Q, QrVersionTables.EC_H};
        String[] ecNames = {"M", "L", "Q", "H"};
        for (int i = 0; i < ecLevels.length; i++) {
            boolean[][] m = QrTestEncoder.encode(notchUri, ecLevels[i], QrTestEncoder.MODE_BYTE, false);
            QrTestEncoder.LumaFrame frame = QrTestEncoder.renderToLuma(m, 6, 24, 15, 240, 0.0, 0.0, false);
            String dec = QrDecoder.decodeLuma(frame.yPlane, frame.width, frame.height);
            check("QR decode NotchPixel pairing URI with ECC " + ecNames[i], notchUri.equals(dec));
        }

        // Rotations 0, 90, 180, 270
        boolean[][] base = QrTestEncoder.encode(openUri, QrVersionTables.EC_M, QrTestEncoder.MODE_BYTE, false);
        for (int deg : new int[]{0, 90, 180, 270}) {
            boolean[][] rot = QrTestEncoder.rotateMatrix(base, deg);
            QrTestEncoder.LumaFrame f = QrTestEncoder.renderToLuma(rot, 6, 20, 20, 235, 0.0, 0.0, false);
            check("QR decode at " + deg + " deg rotation", openUri.equals(QrDecoder.decodeLuma(f.yPlane, f.width, f.height)));
        }

        // Perspective skew + low contrast + 3x3 box blur
        QrTestEncoder.LumaFrame skewed = QrTestEncoder.renderToLuma(base, 8, 28, 70, 190, 0.08, 0.04, true);
        check("QR decode with perspective skew, low contrast, and box blur",
                openUri.equals(QrDecoder.decodeLuma(skewed.yPlane, skewed.width, skewed.height)));

        // Inverted polarity
        QrTestEncoder.LumaFrame inv = QrTestEncoder.renderToLuma(base, 6, 24, 230, 25, 0.0, 0.0, false);
        check("QR decode inverted polarity (light on dark)",
                openUri.equals(QrDecoder.decodeLuma(inv.yPlane, inv.width, inv.height)));

        // Injected Reed-Solomon byte errors in QR image
        boolean[][] errMat = QrTestEncoder.encodeWithInjectedByteErrors(notchUri, QrVersionTables.EC_M, 4);
        QrTestEncoder.LumaFrame errFrame = QrTestEncoder.renderToLuma(errMat, 6, 24, 10, 245, 0.0, 0.0, false);
        check("QR decode recovers from 4 injected byte errors via Reed-Solomon",
                notchUri.equals(QrDecoder.decodeLuma(errFrame.yPlane, errFrame.width, errFrame.height)));

        // Numeric, Alphanumeric, ECI UTF-8, and Version 12 large payload
        boolean[][] numMat = QrTestEncoder.encode("01234567899876543210", QrVersionTables.EC_M, QrTestEncoder.MODE_NUMERIC, false);
        check("QR decode Numeric mode (v1)", "01234567899876543210".equals(QrDecoder.decodeMatrix(numMat)));

        boolean[][] alphaMat = QrTestEncoder.encode("NOTCHPIXEL DECK 192.168.1.42", QrVersionTables.EC_Q, QrTestEncoder.MODE_ALPHANUMERIC, false);
        check("QR decode Alphanumeric mode", "NOTCHPIXEL DECK 192.168.1.42".equals(QrDecoder.decodeMatrix(alphaMat)));

        String utf8Sample = "NotchPixel Deck \u2014 caf\u00E9";
        boolean[][] eciMat = QrTestEncoder.encode(utf8Sample, QrVersionTables.EC_M, QrTestEncoder.MODE_BYTE, true);
        check("QR decode ECI + UTF-8 mode", utf8Sample.equals(QrDecoder.decodeMatrix(eciMat)));

        StringBuilder longPay = new StringBuilder(notchUri);
        while (longPay.length() < 245) longPay.append("&pad=0123456789");
        boolean[][] v12Mat = QrTestEncoder.encode(longPay.toString(), QrVersionTables.EC_M, QrTestEncoder.MODE_BYTE, false);
        QrTestEncoder.LumaFrame v12Frame = QrTestEncoder.renderToLuma(v12Mat, 5, 20, 15, 240, 0.0, 0.0, false);
        check("QR decode Version >= 11 (" + v12Mat.length + "x" + v12Mat.length + ")",
                longPay.toString().equals(QrDecoder.decodeLuma(v12Frame.yPlane, v12Frame.width, v12Frame.height)));
    }

    private static void testPairingUriParser() {
        PairingUriParser.PairingPayload p1 = PairingUriParser.parse(
                "notchpixel-deck://pair?ip=192.168.1.42&port=47825&code=123456&cert=AA:BB:CC:DD");
        check("PairingUriParser parses notchpixel-deck:// & normalizes cert fp",
                p1 != null && p1.isValidForPairing()
                        && "192.168.1.42".equals(p1.ip)
                        && p1.port == 47825
                        && "123456".equals(p1.code)
                        && "aabbccdd".equals(p1.certFingerprint));

        PairingUriParser.PairingPayload p2 = PairingUriParser.parse(
                "OPENPIXEL-DECK://pair?host=10.0.0.9&fp=0123abcd&code=654321&mac=AA:BB:CC:DD:EE:FF&name=StudioPC");
        check("PairingUriParser parses openpixel-deck:// with host/fp/mac/name aliases",
                p2 != null && p2.isValidForPairing()
                        && "10.0.0.9".equals(p2.ip)
                        && "0123abcd".equals(p2.certFingerprint)
                        && "AA:BB:CC:DD:EE:FF".equals(p2.mac)
                        && "StudioPC".equals(p2.pcName));

        check("PairingUriParser rejects http:// scheme", PairingUriParser.parse("https://example.com?ip=1.2.3.4&code=123456") == null);
        PairingUriParser.PairingPayload shortCode = PairingUriParser.parse("notchpixel-deck://pair?ip=1.2.3.4&code=123");
        check("PairingUriParser rejects short pairing code", shortCode != null && !shortCode.isValidForPairing());
    }

    private static void testWakeOnLan() {
        byte[] pkt1 = WakeOnLanSender.buildMagicPacket("AA:BB:CC:DD:EE:FF");
        byte[] pkt2 = WakeOnLanSender.buildMagicPacket("aa-bb-cc-dd-ee-ff");
        boolean valid = pkt1.length == 102 && pkt2.length == 102;
        for (int i = 0; i < 6 && valid; i++) {
            if ((pkt1[i] & 0xFF) != 0xFF) valid = false;
        }
        byte[] expectedMac = {(byte) 0xAA, (byte) 0xBB, (byte) 0xCC, (byte) 0xDD, (byte) 0xEE, (byte) 0xFF};
        for (int rep = 0; rep < 16 && valid; rep++) {
            for (int j = 0; j < 6; j++) {
                if (pkt1[6 + rep * 6 + j] != expectedMac[j] || pkt2[6 + rep * 6 + j] != expectedMac[j]) {
                    valid = false;
                    break;
                }
            }
        }
        check("WakeOnLanSender builds exact 102-byte magic packet for colon/dash MACs", valid);

        boolean rejected = false;
        try {
            WakeOnLanSender.buildMagicPacket("ZZ:BB:CC:DD:EE:FF");
        } catch (IllegalArgumentException ex) {
            rejected = true;
        }
        boolean rejectedEmpty = false;
        try {
            WakeOnLanSender.buildMagicPacket("11:22");
        } catch (IllegalArgumentException ex) {
            rejectedEmpty = true;
        }
        check("WakeOnLanSender rejects malformed MAC addresses", rejected && rejectedEmpty);
    }

    private static void testProtocolAndTlsPinning(String workDir) throws Exception {
        File p12File = new File(workDir, "test_pc_deck.p12");
        if (p12File.exists()) p12File.delete();

        String javaBin = new File(System.getProperty("java.home"), "bin/keytool.exe").getAbsolutePath();
        if (!new File(javaBin).exists()) {
            javaBin = new File(System.getProperty("java.home"), "bin/keytool").getAbsolutePath();
        }
        ProcessBuilder pb = new ProcessBuilder(
                javaBin, "-genkeypair", "-alias", "deck", "-keyalg", "RSA", "-keysize", "2048",
                "-storetype", "PKCS12", "-keystore", p12File.getAbsolutePath(),
                "-storepass", "testpass", "-keypass", "testpass",
                "-dname", "CN=TestPC.notchpixel.local", "-validity", "30");
        pb.redirectErrorStream(true);
        Process proc = pb.start();
        proc.getInputStream().readAllBytes();
        int ktCode = proc.waitFor();
        check("Generated RSA-2048 PKCS12 test certificate (exit=" + ktCode + ")", ktCode == 0 && p12File.exists());

        KeyStore ks = KeyStore.getInstance("PKCS12");
        try (FileInputStream fis = new FileInputStream(p12File)) {
            ks.load(fis, "testpass".toCharArray());
        }
        byte[] certDer = ks.getCertificate("deck").getEncoded();
        byte[] sha = MessageDigest.getInstance("SHA-256").digest(certDer);
        StringBuilder fpSb = new StringBuilder();
        for (byte b : sha) fpSb.append(String.format(Locale.US, "%02x", b & 0xFF));
        final String realFp = fpSb.toString();

        KeyManagerFactory kmf = KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm());
        kmf.init(ks, "testpass".toCharArray());
        SSLContext sslCtx = SSLContext.getInstance("TLSv1.2");
        sslCtx.init(kmf.getKeyManagers(), null, null);

        final List<String> recordedRequests = new CopyOnWriteArrayList<>();
        final boolean[] allowPair = {false};
        final String activeToken = "tok-test-secret-xyz";
        final boolean[] pairedActive = {false};

        SSLServerSocket srv = (SSLServerSocket) sslCtx.getServerSocketFactory().createServerSocket(
                0, 50, InetAddress.getByName("127.0.0.1"));
        final int tlsPort = srv.getLocalPort();

        Thread srvThread = new Thread(() -> {
            while (!srv.isClosed()) {
                try (SSLSocket s = (SSLSocket) srv.accept()) {
                    InputStream is = s.getInputStream();
                    ByteArrayOutputStream reqBuf = new ByteArrayOutputStream();
                    byte[] b = new byte[4096];
                    int n;
                    while ((n = is.read(b)) > 0) {
                        reqBuf.write(b, 0, n);
                        String cur = reqBuf.toString(StandardCharsets.UTF_8);
                        if (cur.contains("\r\n\r\n")) break;
                    }
                    String raw = reqBuf.toString(StandardCharsets.UTF_8);
                    String firstLine = raw.split("\r\n")[0];
                    String[] parts = firstLine.split(" ");
                    String target = parts.length >= 2 ? parts[1] : "/";
                    recordedRequests.add(parts[0] + " " + target);

                    String status = "200 OK";
                    String body = "{\"ok\":true}";

                    if (target.startsWith("/deck/probe")) {
                        body = new JSONObject()
                                .put("pcName", "TestWorkstation")
                                .put("ip", "127.0.0.1")
                                .put("tlsPort", tlsPort)
                                .put("certFingerprint", realFp)
                                .put("mac", "AA:BB:CC:DD:EE:FF")
                                .toString();
                    } else if (target.startsWith("/deck/pair")) {
                        if (!allowPair[0]) {
                            status = "403 Forbidden";
                            body = "{\"paired\":false,\"error\":\"Denied on PC\"}";
                        } else {
                            pairedActive[0] = true;
                            body = new JSONObject()
                                    .put("paired", true)
                                    .put("token", activeToken)
                                    .put("keyFingerprint", "1122334455667788")
                                    .put("certFingerprint", realFp)
                                    .put("mac", "AA:BB:CC:DD:EE:FF")
                                    .put("pcName", "TestWorkstation")
                                    .toString();
                        }
                    } else {
                        String decodedTarget = URLDecoder.decode(target, StandardCharsets.UTF_8.name());
                        if (!pairedActive[0] || !decodedTarget.contains("token=" + activeToken)) {
                            status = "401 Unauthorized";
                            body = "{\"error\":\"Device not paired or revoked\"}";
                        } else if (target.startsWith("/deck/state")) {
                            JSONArray apps = new JSONArray();
                            for (int i = 0; i < 6; i++) {
                                apps.put(new JSONObject()
                                        .put("index", i)
                                        .put("label", i == 0 ? "VS Code" : "")
                                        .put("target", i == 0 ? "code" : "")
                                        .put("configured", i == 0));
                            }
                            body = new JSONObject()
                                    .put("pcName", "TestWorkstation")
                                    .put("cpu", 32).put("gpu", 14).put("ram", 58)
                                    .put("cpuTempAvailable", true).put("cpuTempC", 54)
                                    .put("gpuTempAvailable", true).put("gpuTempC", 49)
                                    .put("recording", false).put("recordingSec", 0)
                                    .put("micMuted", false).put("volume", 65).put("muted", false)
                                    .put("media", new JSONObject()
                                            .put("hasSession", true).put("isPlaying", true)
                                            .put("title", "Focus Track").put("artist", "Artist")
                                            .put("app", "Spotify").put("posSec", 30).put("durSec", 180)
                                            .put("accentHex", "#2BE4EA"))
                                    .put("apps", apps)
                                    .toString();
                        } else if (target.startsWith("/deck/devices")) {
                            JSONArray arr = new JSONArray().put(new JSONObject()
                                    .put("deviceId", "dev1")
                                    .put("deviceName", "NotchPixelTestPhone")
                                    .put("model", "NotchPixelTestPhone")
                                    .put("osVersion", "Android 15")
                                    .put("appVersion", "1.0.5")
                                    .put("ipAddress", "127.0.0.1")
                                    .put("keyFingerprint", "1122334455667788")
                                    .put("pairedAtMs", 123456L)
                                    .put("isCurrent", true));
                            body = new JSONObject().put("devices", arr).toString();
                        } else if (target.startsWith("/deck/unpair")) {
                            pairedActive[0] = false;
                            body = "{\"ok\":true,\"unpaired\":true,\"remaining\":0}";
                        }
                    }

                    byte[] bBytes = body.getBytes(StandardCharsets.UTF_8);
                    String hdr = "HTTP/1.1 " + status + "\r\nContent-Type: application/json\r\nContent-Length: " + bBytes.length + "\r\nConnection: close\r\n\r\n";
                    OutputStream os = s.getOutputStream();
                    os.write(hdr.getBytes(StandardCharsets.UTF_8));
                    os.write(bBytes);
                    os.flush();
                } catch (Exception ignored) { }
            }
        });
        srvThread.setDaemon(true);
        srvThread.start();

        // Optional UDP 47826 responder if port is free on host
        DatagramSocket udpSrv = null;
        try {
            udpSrv = new DatagramSocket(PinnedDeckClient.DISCOVERY_UDP_PORT);
            final DatagramSocket udpRef = udpSrv;
            Thread udpThread = new Thread(() -> {
                byte[] buf = new byte[512];
                while (!udpRef.isClosed()) {
                    try {
                        DatagramPacket pkt = new DatagramPacket(buf, buf.length);
                        udpRef.receive(pkt);
                        String msg = new String(pkt.getData(), 0, pkt.getLength(), StandardCharsets.UTF_8);
                        if (msg.startsWith("NOTCHPIXEL_DECK_DISCOVER") || msg.startsWith("OPENPIXEL_DECK_DISCOVER")) {
                            String reply = new JSONObject()
                                    .put("ip", "127.0.0.1")
                                    .put("tlsPort", tlsPort)
                                    .put("certFingerprint", realFp)
                                    .put("mac", "AA:BB:CC:DD:EE:FF")
                                    .put("pcName", "TestWorkstation")
                                    .toString();
                            byte[] rBytes = reply.getBytes(StandardCharsets.UTF_8);
                            udpRef.send(new DatagramPacket(rBytes, rBytes.length, pkt.getAddress(), pkt.getPort()));
                        }
                    } catch (Exception ignored) { }
                }
            });
            udpThread.setDaemon(true);
            udpThread.start();
        } catch (Exception ignored) { }

        Context ctx = new Context();
        // 127.0.0.1 little-endian = (1<<24) | 127 = 0x0100007F
        ctx.registerSystemService(Context.WIFI_SERVICE, new WifiManager(0x0100007F));
        SecurePairingStore store = new SecurePairingStore(ctx);
        PinnedDeckClient client = new PinnedDeckClient(ctx, store);

        if (udpSrv != null) {
            PinnedDeckClient.ProbeResult pr = client.discoverViaUdpBroadcast();
            if (pr != null) {
                check("UDP discovery parses certFingerprint & pcName",
                        "TestWorkstation".equals(pr.pcName) && realFp.equals(pr.certFingerprint));
            } else {
                System.out.println("[INFO] Broadcast 255.255.255.255 did not loop back on this NIC/firewall; skipping broadcast loop check (covered in C# loopback UDP test).");
            }
        }

        // TLS pinning mismatch rejection
        PinnedDeckClient.PairOutcome badPin = client.pairWithPc(
                "127.0.0.1", tlsPort, "123456",
                "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff");
        check("TLS pinning rejects mismatched cert fingerprint", !badPin.ok && badPin.error.contains("mismatch"));

        // PC Deny (403)
        allowPair[0] = false;
        PinnedDeckClient.PairOutcome denied = client.pairWithPc("127.0.0.1", tlsPort, "123456", realFp);
        check("Pair fails cleanly when PC clicks Deny (403)", !denied.ok && !store.isPaired());

        // PC Allow (200)
        allowPair[0] = true;
        PinnedDeckClient.PairOutcome okPair = client.pairWithPc("127.0.0.1", tlsPort, "123456", realFp);
        check("Pair succeeds when PC clicks Allow with matching TLS pin", okPair.ok && store.isPaired());

        // Verify SecurePairingStore AES-GCM round-trip with fresh instance
        SecurePairingStore reloaded = new SecurePairingStore(ctx);
        check("SecurePairingStore AES-GCM persists & decrypts token and pinned cert",
                reloaded.isPaired() && activeToken.equals(reloaded.getToken()) && realFp.equals(reloaded.getCertFingerprint()));

        // Fetch state & commands
        PinnedDeckClient.PcState st = client.fetchState();
        check("fetchState parses PC stats, media session, and app slots",
                "TestWorkstation".equals(st.pcName) && st.cpu == 32 && st.hasMedia
                        && "Focus Track".equals(st.mediaTitle) && st.apps.size() == 6 && st.apps.get(0).configured);

        client.sendPadAction("a=move&dx=5&dy=-3");
        client.sendPadAction("a=scroll&dy=2");
        client.sendPadAction("a=type&text=Hello");
        client.sendMediaAction("a=seek&v=45");
        client.sendMediaAction("a=mute");
        client.launchAppSlot(0);
        client.addAppSlot("Calc", "calc.exe");
        client.sendModeAction("slide_start");
        client.sendPowerAction("lock");
        check("Pad, Media, App, Mode, Power commands dispatch over pinned TLS",
                hasReq(recordedRequests, "/deck/pad?a=move&dx=5&dy=-3")
                        && hasReq(recordedRequests, "/deck/pad?a=type&text=Hello")
                        && hasReq(recordedRequests, "/deck/media?a=seek&v=45")
                        && hasReq(recordedRequests, "/deck/app?idx=0")
                        && hasReq(recordedRequests, "/deck/mode?a=slide_start")
                        && hasReq(recordedRequests, "/deck/power?a=lock"));

        List<PinnedDeckClient.DeviceInfo> devs = client.fetchDevices();
        check("fetchDevices returns paired device metadata", devs.size() == 1 && "dev1".equals(devs.get(0).deviceId));

        // IP-change rediscoverPcIp() via /24 probe
        store.updatePcIp("127.0.0.99");
        boolean redir = client.rediscoverPcIp();
        check("rediscoverPcIp finds PC at 127.0.0.1 when IP changes", redir && "127.0.0.1".equals(store.getPcIp()));

        // Unpair
        boolean unpaired = client.unpairWithPc();
        check("unpairWithPc sends POST /deck/unpair and PC revokes token", unpaired && !pairedActive[0]);

        srv.close();
        if (udpSrv != null) udpSrv.close();
        p12File.delete();
    }

    private static boolean hasReq(List<String> list, String sub) {
        for (String s : list) {
            if (s.contains(sub)) return true;
        }
        return false;
    }
}
