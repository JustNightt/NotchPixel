package com.openpixel.deck.harness;

import java.net.URI;
import java.net.URLDecoder;
import java.nio.charset.StandardCharsets;
import java.security.AlgorithmParameters;
import java.security.InvalidAlgorithmParameterException;
import java.security.InvalidKeyException;
import java.security.Key;
import java.security.KeyStore;
import java.security.KeyStoreException;
import java.security.KeyStoreSpi;
import java.security.NoSuchAlgorithmException;
import java.security.Provider;
import java.security.SecureRandom;
import java.security.Security;
import java.security.UnrecoverableEntryException;
import java.security.UnrecoverableKeyException;
import java.security.cert.Certificate;
import java.security.spec.AlgorithmParameterSpec;
import java.util.ArrayList;
import java.util.Base64;
import java.util.Collections;
import java.util.Date;
import java.util.Enumeration;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;
import javax.crypto.KeyGenerator;
import javax.crypto.KeyGeneratorSpi;
import javax.crypto.SecretKey;

/**
 * Pure-JVM Android runtime shim backing SharedPreferences, AndroidKeyStore AES-GCM,
 * Base64, WifiManager, and RFC-8259 JSONObject/JSONArray for opaque-box JVM tests.
 */
public final class AndroidTestShim {
    private static final ConcurrentHashMap<String, ConcurrentHashMap<String, String>> PREFS_STORE =
            new ConcurrentHashMap<>();
    private static final ConcurrentHashMap<String, KeyStore.Entry> KEYSTORE_ENTRIES =
            new ConcurrentHashMap<>();
    private static volatile int localIpv4LittleEndian = 0x0100007F; // 127.0.0.1
    private static volatile boolean providerInstalled = false;

    private AndroidTestShim() {}

    public static synchronized void ensureInitialized() {
        if (!providerInstalled) {
            if (Security.getProvider("AndroidKeyStore") == null) {
                Security.insertProviderAt(new AndroidKeyStoreProvider(), 1);
            }
            providerInstalled = true;
        }
    }

    public static void resetAllState() {
        ensureInitialized();
        PREFS_STORE.clear();
        KEYSTORE_ENTRIES.clear();
        setLocalIpv4Dotted("127.0.0.1");
    }

    public static void setLocalIpv4Dotted(String ipv4) {
        String[] parts = ipv4.trim().split("\\.");
        if (parts.length != 4) {
            throw new IllegalArgumentException("Invalid IPv4: " + ipv4);
        }
        int a = Integer.parseInt(parts[0]) & 0xFF;
        int b = Integer.parseInt(parts[1]) & 0xFF;
        int c = Integer.parseInt(parts[2]) & 0xFF;
        int d = Integer.parseInt(parts[3]) & 0xFF;
        localIpv4LittleEndian = a | (b << 8) | (c << 16) | (d << 24);
    }

    public static int getLocalIpv4LittleEndian() {
        return localIpv4LittleEndian;
    }

    public static Map<String, String> getPrefsBucket(String name) {
        return PREFS_STORE.computeIfAbsent(name, k -> new ConcurrentHashMap<>());
    }

    public static String base64Encode(byte[] data) {
        return Base64.getEncoder().encodeToString(data);
    }

    public static byte[] base64Decode(String b64) {
        return Base64.getDecoder().decode(b64.replaceAll("\\s+", ""));
    }

    // -------------------------------------------------------------------------
    // QR URI Parser supporting notchpixel-deck:// and openpixel-deck://
    // -------------------------------------------------------------------------

    public static final class ParsedPairingUri {
        public final String scheme;
        public final String ip;
        public final int port;
        public final String code;
        public final String certFingerprint;
        public final String mac;
        public final String pcName;

        public ParsedPairingUri(String scheme, String ip, int port, String code,
                                String certFingerprint, String mac, String pcName) {
            this.scheme = scheme;
            this.ip = ip;
            this.port = port;
            this.code = code;
            this.certFingerprint = certFingerprint;
            this.mac = mac;
            this.pcName = pcName;
        }
    }

    public static ParsedPairingUri parsePairingUri(String rawUri) {
        if (rawUri == null || rawUri.trim().isEmpty()) {
            throw new IllegalArgumentException("Empty URI");
        }
        URI uri = URI.create(rawUri.trim());
        String scheme = uri.getScheme() == null ? "" : uri.getScheme().toLowerCase();
        if (!"notchpixel-deck".equals(scheme) && !"openpixel-deck".equals(scheme)) {
            throw new IllegalArgumentException("Unsupported scheme: " + scheme);
        }
        if (!"pair".equalsIgnoreCase(uri.getHost())) {
            throw new IllegalArgumentException("Unexpected host: " + uri.getHost());
        }
        Map<String, String> q = new LinkedHashMap<>();
        String rawQuery = uri.getRawQuery();
        if (rawQuery != null && !rawQuery.isEmpty()) {
            for (String part : rawQuery.split("&")) {
                if (part.isEmpty()) continue;
                int eq = part.indexOf('=');
                if (eq < 0) {
                    q.put(URLDecoder.decode(part, StandardCharsets.UTF_8), "");
                } else {
                    String k = URLDecoder.decode(part.substring(0, eq), StandardCharsets.UTF_8);
                    String v = URLDecoder.decode(part.substring(eq + 1), StandardCharsets.UTF_8);
                    q.put(k, v);
                }
            }
        }
        String ip = q.containsKey("ip") ? q.get("ip") : q.getOrDefault("host", "");
        int port = Integer.parseInt(q.getOrDefault("port", "47825"));
        String code = q.getOrDefault("code", "");
        String cert = q.containsKey("cert") ? q.get("cert") : q.getOrDefault("fp", "");
        String mac = q.getOrDefault("mac", "");
        String name = q.getOrDefault("name", "");
        return new ParsedPairingUri(scheme, ip, port, code, cert, mac, name);
    }

    // -------------------------------------------------------------------------
    // RFC-8259 JSON Parser & Serializer for org.json.* shims
    // -------------------------------------------------------------------------

    public static Map<String, Object> parseJsonObject(String json) {
        if (json == null) throw new IllegalArgumentException("null json");
        JsonParser parser = new JsonParser(json);
        Object val = parser.parseValue();
        parser.skipWs();
        if (!parser.eof()) {
            throw new IllegalArgumentException("Trailing JSON content at pos " + parser.pos);
        }
        if (!(val instanceof Map)) {
            throw new IllegalArgumentException("Expected JSON object");
        }
        @SuppressWarnings("unchecked")
        Map<String, Object> map = (Map<String, Object>) val;
        return map;
    }

    public static List<Object> parseJsonArray(String json) {
        if (json == null) throw new IllegalArgumentException("null json");
        JsonParser parser = new JsonParser(json);
        Object val = parser.parseValue();
        parser.skipWs();
        if (!parser.eof()) {
            throw new IllegalArgumentException("Trailing JSON content at pos " + parser.pos);
        }
        if (!(val instanceof List)) {
            throw new IllegalArgumentException("Expected JSON array");
        }
        @SuppressWarnings("unchecked")
        List<Object> list = (List<Object>) val;
        return list;
    }

    public static String serializeJsonValue(Object value) {
        if (value == null) return "null";
        if (value instanceof Boolean || value instanceof Number) return value.toString();
        if (value instanceof String) return quoteString((String) value);
        if (value instanceof Map) {
            @SuppressWarnings("unchecked")
            Map<String, Object> map = (Map<String, Object>) value;
            StringBuilder sb = new StringBuilder("{");
            boolean first = true;
            for (Map.Entry<String, Object> e : map.entrySet()) {
                if (!first) sb.append(',');
                first = false;
                sb.append(quoteString(e.getKey())).append(':').append(serializeJsonValue(e.getValue()));
            }
            sb.append('}');
            return sb.toString();
        }
        if (value instanceof List) {
            @SuppressWarnings("unchecked")
            List<Object> list = (List<Object>) value;
            StringBuilder sb = new StringBuilder("[");
            for (int i = 0; i < list.size(); i++) {
                if (i > 0) sb.append(',');
                sb.append(serializeJsonValue(list.get(i)));
            }
            sb.append(']');
            return sb.toString();
        }
        return quoteString(String.valueOf(value));
    }

    private static String quoteString(String s) {
        StringBuilder sb = new StringBuilder("\"");
        for (int i = 0; i < s.length(); i++) {
            char ch = s.charAt(i);
            switch (ch) {
                case '"': sb.append("\\\""); break;
                case '\\': sb.append("\\\\"); break;
                case '\b': sb.append("\\b"); break;
                case '\f': sb.append("\\f"); break;
                case '\n': sb.append("\\n"); break;
                case '\r': sb.append("\\r"); break;
                case '\t': sb.append("\\t"); break;
                default:
                    if (ch < 0x20) sb.append(String.format("\\u%04x", (int) ch));
                    else sb.append(ch);
            }
        }
        sb.append('"');
        return sb.toString();
    }

    private static final class JsonParser {
        final String s;
        int pos;

        JsonParser(String s) {
            this.s = s;
        }

        boolean eof() {
            return pos >= s.length();
        }

        void skipWs() {
            while (pos < s.length() && Character.isWhitespace(s.charAt(pos))) pos++;
        }

        Object parseValue() {
            skipWs();
            if (eof()) throw new IllegalArgumentException("Unexpected end of JSON");
            char c = s.charAt(pos);
            if (c == '{') return parseObject();
            if (c == '[') return parseArray();
            if (c == '"') return parseString();
            if (c == 't') { expect("true"); return Boolean.TRUE; }
            if (c == 'f') { expect("false"); return Boolean.FALSE; }
            if (c == 'n') { expect("null"); return null; }
            if (c == '-' || (c >= '0' && c <= '9')) return parseNumber();
            throw new IllegalArgumentException("Unexpected JSON token at " + pos + ": " + c);
        }

        Map<String, Object> parseObject() {
            pos++; // '{'
            Map<String, Object> map = new LinkedHashMap<>();
            skipWs();
            if (!eof() && s.charAt(pos) == '}') {
                pos++;
                return map;
            }
            while (true) {
                skipWs();
                if (eof() || s.charAt(pos) != '"') {
                    throw new IllegalArgumentException("Expected object key at " + pos);
                }
                String key = parseString();
                skipWs();
                if (eof() || s.charAt(pos) != ':') {
                    throw new IllegalArgumentException("Expected ':' at " + pos);
                }
                pos++;
                Object val = parseValue();
                map.put(key, val);
                skipWs();
                if (eof()) throw new IllegalArgumentException("Unterminated object");
                char next = s.charAt(pos++);
                if (next == '}') break;
                if (next != ',') throw new IllegalArgumentException("Expected ',' or '}' at " + (pos - 1));
            }
            return map;
        }

        List<Object> parseArray() {
            pos++; // '['
            List<Object> list = new ArrayList<>();
            skipWs();
            if (!eof() && s.charAt(pos) == ']') {
                pos++;
                return list;
            }
            while (true) {
                list.add(parseValue());
                skipWs();
                if (eof()) throw new IllegalArgumentException("Unterminated array");
                char next = s.charAt(pos++);
                if (next == ']') break;
                if (next != ',') throw new IllegalArgumentException("Expected ',' or ']' at " + (pos - 1));
            }
            return list;
        }

        String parseString() {
            pos++; // '"'
            StringBuilder sb = new StringBuilder();
            while (!eof()) {
                char c = s.charAt(pos++);
                if (c == '"') return sb.toString();
                if (c == '\\') {
                    if (eof()) throw new IllegalArgumentException("Unterminated escape");
                    char e = s.charAt(pos++);
                    switch (e) {
                        case '"': sb.append('"'); break;
                        case '\\': sb.append('\\'); break;
                        case '/': sb.append('/'); break;
                        case 'b': sb.append('\b'); break;
                        case 'f': sb.append('\f'); break;
                        case 'n': sb.append('\n'); break;
                        case 'r': sb.append('\r'); break;
                        case 't': sb.append('\t'); break;
                        case 'u':
                            if (pos + 4 > s.length()) throw new IllegalArgumentException("Bad unicode escape");
                            sb.append((char) Integer.parseInt(s.substring(pos, pos + 4), 16));
                            pos += 4;
                            break;
                        default:
                            throw new IllegalArgumentException("Invalid escape: \\" + e);
                    }
                } else {
                    sb.append(c);
                }
            }
            throw new IllegalArgumentException("Unterminated string");
        }

        Number parseNumber() {
            int start = pos;
            if (s.charAt(pos) == '-') pos++;
            while (!eof() && Character.isDigit(s.charAt(pos))) pos++;
            boolean isFloat = false;
            if (!eof() && s.charAt(pos) == '.') {
                isFloat = true;
                pos++;
                while (!eof() && Character.isDigit(s.charAt(pos))) pos++;
            }
            if (!eof() && (s.charAt(pos) == 'e' || s.charAt(pos) == 'E')) {
                isFloat = true;
                pos++;
                if (!eof() && (s.charAt(pos) == '+' || s.charAt(pos) == '-')) pos++;
                while (!eof() && Character.isDigit(s.charAt(pos))) pos++;
            }
            String num = s.substring(start, pos);
            if (isFloat) return Double.parseDouble(num);
            long l = Long.parseLong(num);
            if (l >= Integer.MIN_VALUE && l <= Integer.MAX_VALUE) return (int) l;
            return l;
        }

        void expect(String literal) {
            if (!s.startsWith(literal, pos)) {
                throw new IllegalArgumentException("Expected " + literal + " at " + pos);
            }
            pos += literal.length();
        }
    }

    // -------------------------------------------------------------------------
    // AndroidKeyStore JCA Provider (Real AES-256 KeyStore + KeyGenerator)
    // -------------------------------------------------------------------------

    public static final class AndroidKeyStoreProvider extends Provider {
        public AndroidKeyStoreProvider() {
            super("AndroidKeyStore", "1.0", "In-memory AndroidKeyStore test shim");
            put("KeyStore.AndroidKeyStore", InMemoryKeyStoreSpi.class.getName());
            put("KeyGenerator.AES", AesKeyGeneratorSpi.class.getName());
        }
    }

    public static final class InMemoryKeyStoreSpi extends KeyStoreSpi {
        @Override public Key engineGetKey(String alias, char[] password) throws NoSuchAlgorithmException, UnrecoverableKeyException {
            KeyStore.Entry e = KEYSTORE_ENTRIES.get(alias);
            if (e instanceof KeyStore.SecretKeyEntry) return ((KeyStore.SecretKeyEntry) e).getSecretKey();
            return null;
        }
        @Override public Certificate[] engineGetCertificateChain(String alias) { return null; }
        @Override public Certificate engineGetCertificate(String alias) { return null; }
        @Override public Date engineGetCreationDate(String alias) { return new Date(); }
        @Override public void engineSetKeyEntry(String alias, Key key, char[] password, Certificate[] chain) throws KeyStoreException {
            if (key instanceof SecretKey) KEYSTORE_ENTRIES.put(alias, new KeyStore.SecretKeyEntry((SecretKey) key));
        }
        @Override public void engineSetKeyEntry(String alias, byte[] key, Certificate[] chain) throws KeyStoreException {}
        @Override public void engineSetCertificateEntry(String alias, Certificate cert) throws KeyStoreException {}
        @Override public void engineDeleteEntry(String alias) throws KeyStoreException { KEYSTORE_ENTRIES.remove(alias); }
        @Override public Enumeration<String> engineAliases() { return Collections.enumeration(KEYSTORE_ENTRIES.keySet()); }
        @Override public boolean engineContainsAlias(String alias) { return KEYSTORE_ENTRIES.containsKey(alias); }
        @Override public int engineSize() { return KEYSTORE_ENTRIES.size(); }
        @Override public boolean engineIsKeyEntry(String alias) { return KEYSTORE_ENTRIES.containsKey(alias); }
        @Override public boolean engineIsCertificateEntry(String alias) { return false; }
        @Override public String engineGetCertificateAlias(Certificate cert) { return null; }
        @Override public void engineStore(java.io.OutputStream stream, char[] password) {}
        @Override public void engineLoad(java.io.InputStream stream, char[] password) {}
        @Override public KeyStore.Entry engineGetEntry(String alias, KeyStore.ProtectionParameter protParam) throws NoSuchAlgorithmException, UnrecoverableEntryException, KeyStoreException {
            return KEYSTORE_ENTRIES.get(alias);
        }
        @Override public void engineSetEntry(String alias, KeyStore.Entry entry, KeyStore.ProtectionParameter protParam) throws KeyStoreException {
            KEYSTORE_ENTRIES.put(alias, entry);
        }
        @Override public boolean engineEntryInstanceOf(String alias, Class<? extends KeyStore.Entry> entryClass) {
            KeyStore.Entry e = KEYSTORE_ENTRIES.get(alias);
            return e != null && entryClass.isInstance(e);
        }
    }

    public static final class AesKeyGeneratorSpi extends KeyGeneratorSpi {
        private String alias = "default_alias";
        private int keySize = 256;

        @Override protected void engineInit(SecureRandom random) {
            this.keySize = 256;
        }

        @Override protected void engineInit(AlgorithmParameterSpec params, SecureRandom random) throws InvalidAlgorithmParameterException {
            try {
                java.lang.reflect.Method mAlias = params.getClass().getMethod("getKeystoreAlias");
                Object a = mAlias.invoke(params);
                if (a instanceof String) this.alias = (String) a;
                java.lang.reflect.Method mSize = params.getClass().getMethod("getKeySize");
                Object sz = mSize.invoke(params);
                if (sz instanceof Integer) this.keySize = (Integer) sz;
            } catch (Exception ex) {
                throw new InvalidAlgorithmParameterException(ex);
            }
        }

        @Override protected void engineInit(int keysize, SecureRandom random) {
            this.keySize = keysize;
        }

        @Override protected SecretKey engineGenerateKey() {
            try {
                KeyGenerator realKg = KeyGenerator.getInstance("AES", "SunJCE");
                realKg.init(keySize);
                SecretKey key = realKg.generateKey();
                KEYSTORE_ENTRIES.put(alias, new KeyStore.SecretKeyEntry(key));
                return key;
            } catch (Exception ex) {
                throw new RuntimeException(ex);
            }
        }
    }
}
