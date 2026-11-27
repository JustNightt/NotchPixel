package com.openpixel.deck.qr;

import java.net.URLDecoder;
import java.nio.charset.StandardCharsets;
import java.util.HashMap;
import java.util.Locale;
import java.util.Map;

/**
 * Pure-Java parser for NotchPixel Deck & OpenPixel Deck pairing URIs:
 *   notchpixel-deck://pair?ip=...&port=...&code=...&cert=...
 *   openpixel-deck://pair?host=...&port=...&code=...&fp=...&mac=...&name=...
 */
public final class PairingUriParser {
    public static final class PairingPayload {
        public String scheme = "";
        public String ip = "";
        public int port = 47825;
        public String code = "";
        public String certFingerprint = "";
        public String mac = "";
        public String pcName = "";

        public boolean isValidForPairing() {
            return !ip.isEmpty() && code.length() >= 6;
        }
    }

    private PairingUriParser() {}

    public static String normalizeFingerprint(String fp) {
        if (fp == null) return "";
        return fp.replace(":", "").replace(" ", "").trim().toLowerCase(Locale.US);
    }

    public static PairingPayload parse(String raw) {
        if (raw == null) return null;
        String s = raw.trim();
        if (s.isEmpty()) return null;

        int schemeSep = s.indexOf("://");
        if (schemeSep <= 0) return null;
        String scheme = s.substring(0, schemeSep).toLowerCase(Locale.US);
        if (!"notchpixel-deck".equals(scheme) && !"openpixel-deck".equals(scheme)) {
            return null;
        }

        String rest = s.substring(schemeSep + 3);
        int qIdx = rest.indexOf('?');
        if (qIdx < 0) return null;
        String authority = rest.substring(0, qIdx).toLowerCase(Locale.US);
        if (!authority.isEmpty() && !"pair".equals(authority) && !"pair/".equals(authority)) {
            return null;
        }

        String query = rest.substring(qIdx + 1);
        int fragIdx = query.indexOf('#');
        if (fragIdx >= 0) query = query.substring(0, fragIdx);

        Map<String, String> params = new HashMap<>();
        for (String pair : query.split("&")) {
            if (pair.isEmpty()) continue;
            int eq = pair.indexOf('=');
            if (eq <= 0) continue;
            try {
                String k = URLDecoder.decode(pair.substring(0, eq), StandardCharsets.UTF_8.name())
                        .trim().toLowerCase(Locale.US);
                String v = URLDecoder.decode(pair.substring(eq + 1), StandardCharsets.UTF_8.name())
                        .trim();
                params.put(k, v);
            } catch (Exception ignored) { }
        }

        PairingPayload out = new PairingPayload();
        out.scheme = scheme;
        String ip = params.get("ip");
        if (ip == null || ip.isEmpty()) ip = params.get("host");
        out.ip = ip == null ? "" : ip.trim();

        String portStr = params.get("port");
        if (portStr != null && !portStr.isEmpty()) {
            try {
                int p = Integer.parseInt(portStr);
                if (p > 0 && p <= 65535) out.port = p;
            } catch (Exception ignored) {
                return null;
            }
        }

        String code = params.get("code");
        out.code = code == null ? "" : code.trim();

        String cert = params.get("cert");
        if (cert == null || cert.isEmpty()) cert = params.get("fp");
        out.certFingerprint = normalizeFingerprint(cert);

        String mac = params.get("mac");
        out.mac = mac == null ? "" : mac.trim();

        String name = params.get("name");
        if (name == null || name.isEmpty()) name = params.get("pc");
        out.pcName = name == null ? "" : name.trim();

        return out;
    }
}
