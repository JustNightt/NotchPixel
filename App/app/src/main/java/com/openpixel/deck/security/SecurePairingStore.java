package com.openpixel.deck.security;

import android.content.Context;
import android.content.SharedPreferences;
import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import android.util.Base64;

import org.json.JSONObject;

import java.nio.charset.StandardCharsets;
import java.security.KeyStore;

import javax.crypto.Cipher;
import javax.crypto.KeyGenerator;
import javax.crypto.SecretKey;
import javax.crypto.spec.GCMParameterSpec;

/**
 * Stores OpenPixel PC Deck pairing secrets encrypted with Android KeyStore AES-GCM.
 */
public final class SecurePairingStore {
    private static final String PREF_NAME = "np_deck_secure_v1";
    private static final String KEY_BLOB = "enc_pairing_blob";
    private static final String KEY_SETTINGS = "deck_ui_prefs";
    private static final String ANDROID_KEYSTORE = "AndroidKeyStore";
    private static final String KEY_ALIAS = "openpixel_deck_aes_v1";
    private static final String AES_GCM = "AES/GCM/NoPadding";

    private final SharedPreferences prefs;

    private String token = "";
    private String pcIp = "";
    private int tlsPort = 47825;
    private String certFingerprint = "";
    private String macAddress = "";
    private String pcName = "Desk PC";
    private String keyFingerprint = "";

    private int padSpeed = 1;       // 0=Slow, 1=Normal, 2=Fast, 3=Faster
    private int scrollSpeed = 1;    // 0=Slow, 1=Normal, 2=Fast, 3=Faster
    private boolean hapticsEnabled = true;
    private boolean trailEnabled = true;
    private String lookMode = "Frosted"; // Frosted, Auto, Dark, Light
    private boolean mediaNotifEnabled = true;

    public SecurePairingStore(Context ctx) {
        this.prefs = ctx.getApplicationContext().getSharedPreferences(PREF_NAME, Context.MODE_PRIVATE);
        load();
    }

    private SecretKey getOrCreateKey() throws Exception {
        KeyStore ks = KeyStore.getInstance(ANDROID_KEYSTORE);
        ks.load(null);
        if (ks.containsAlias(KEY_ALIAS)) {
            KeyStore.Entry entry = ks.getEntry(KEY_ALIAS, null);
            if (entry instanceof KeyStore.SecretKeyEntry) {
                return ((KeyStore.SecretKeyEntry) entry).getSecretKey();
            }
        }
        KeyGenerator kg = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, ANDROID_KEYSTORE);
        kg.init(new KeyGenParameterSpec.Builder(
                KEY_ALIAS,
                KeyProperties.PURPOSE_ENCRYPT | KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .build());
        return kg.generateKey();
    }

    private String encryptString(String plain) throws Exception {
        Cipher cipher = Cipher.getInstance(AES_GCM);
        cipher.init(Cipher.ENCRYPT_MODE, getOrCreateKey());
        byte[] iv = cipher.getIV();
        byte[] ct = cipher.doFinal(plain.getBytes(StandardCharsets.UTF_8));
        byte[] combined = new byte[1 + iv.length + ct.length];
        combined[0] = (byte) iv.length;
        System.arraycopy(iv, 0, combined, 1, iv.length);
        System.arraycopy(ct, 0, combined, 1 + iv.length, ct.length);
        return Base64.encodeToString(combined, Base64.NO_WRAP);
    }

    private String decryptString(String b64) throws Exception {
        byte[] combined = Base64.decode(b64, Base64.NO_WRAP);
        if (combined.length < 14) return "{}";
        int ivLen = combined[0] & 0xFF;
        if (ivLen <= 0 || 1 + ivLen >= combined.length) return "{}";
        byte[] iv = new byte[ivLen];
        byte[] ct = new byte[combined.length - 1 - ivLen];
        System.arraycopy(combined, 1, iv, 0, ivLen);
        System.arraycopy(combined, 1 + ivLen, ct, 0, ct.length);
        Cipher cipher = Cipher.getInstance(AES_GCM);
        cipher.init(Cipher.DECRYPT_MODE, getOrCreateKey(), new GCMParameterSpec(128, iv));
        return new String(cipher.doFinal(ct), StandardCharsets.UTF_8);
    }

    private synchronized void load() {
        String blob = prefs.getString(KEY_BLOB, "");
        if (!blob.isEmpty()) {
            try {
                JSONObject o = new JSONObject(decryptString(blob));
                token = o.optString("token", "");
                pcIp = o.optString("pcIp", "");
                tlsPort = o.optInt("tlsPort", 47825);
                certFingerprint = o.optString("certFingerprint", "");
                macAddress = o.optString("macAddress", "");
                pcName = o.optString("pcName", "Desk PC");
                keyFingerprint = o.optString("keyFingerprint", "");
            } catch (Exception ignored) {
                token = "";
            }
        }
        String ui = prefs.getString(KEY_SETTINGS, "");
        if (!ui.isEmpty()) {
            try {
                JSONObject s = new JSONObject(ui);
                padSpeed = Math.max(0, Math.min(3, s.optInt("padSpeed", 1)));
                scrollSpeed = Math.max(0, Math.min(3, s.optInt("scrollSpeed", 1)));
                hapticsEnabled = s.optBoolean("haptics", true);
                trailEnabled = s.optBoolean("trail", true);
                lookMode = s.optString("look", "Frosted");
                mediaNotifEnabled = s.optBoolean("mediaNotif", true);
            } catch (Exception ignored) { }
        }
    }

    private synchronized void persistPairing() {
        try {
            JSONObject o = new JSONObject();
            o.put("token", token);
            o.put("pcIp", pcIp);
            o.put("tlsPort", tlsPort);
            o.put("certFingerprint", certFingerprint);
            o.put("macAddress", macAddress);
            o.put("pcName", pcName);
            o.put("keyFingerprint", keyFingerprint);
            prefs.edit().putString(KEY_BLOB, encryptString(o.toString())).apply();
        } catch (Exception ignored) { }
    }

    private synchronized void persistUiPrefs() {
        try {
            JSONObject s = new JSONObject();
            s.put("padSpeed", padSpeed);
            s.put("scrollSpeed", scrollSpeed);
            s.put("haptics", hapticsEnabled);
            s.put("trail", trailEnabled);
            s.put("look", lookMode);
            s.put("mediaNotif", mediaNotifEnabled);
            prefs.edit().putString(KEY_SETTINGS, s.toString()).apply();
        } catch (Exception ignored) { }
    }

    public synchronized void savePairing(String token, String pcIp, int tlsPort,
                                         String certFingerprint, String macAddress,
                                         String pcName, String keyFingerprint) {
        this.token = token == null ? "" : token;
        this.pcIp = pcIp == null ? "" : pcIp;
        this.tlsPort = tlsPort <= 0 ? 47825 : tlsPort;
        this.certFingerprint = certFingerprint == null ? "" : certFingerprint.toLowerCase();
        this.macAddress = macAddress == null ? "" : macAddress;
        this.pcName = (pcName == null || pcName.isEmpty()) ? "Desk PC" : pcName;
        this.keyFingerprint = keyFingerprint == null ? "" : keyFingerprint;
        persistPairing();
    }

    public synchronized void updatePcIp(String ip) {
        if (ip != null && !ip.isEmpty() && !ip.equals(this.pcIp)) {
            this.pcIp = ip;
            persistPairing();
        }
    }

    public synchronized void forgetPc() {
        this.token = "";
        this.pcIp = "";
        this.certFingerprint = "";
        this.keyFingerprint = "";
        prefs.edit().remove(KEY_BLOB).apply();
    }

    public synchronized boolean isPaired() {
        return !token.isEmpty() && !pcIp.isEmpty() && !certFingerprint.isEmpty();
    }

    public synchronized String getToken() { return token; }
    public synchronized String getPcIp() { return pcIp; }
    public synchronized int getTlsPort() { return tlsPort; }
    public synchronized String getCertFingerprint() { return certFingerprint; }
    public synchronized String getMacAddress() { return macAddress; }
    public synchronized String getPcName() { return pcName; }
    public synchronized String getKeyFingerprint() { return keyFingerprint; }

    public synchronized int getPadSpeed() { return padSpeed; }
    public synchronized void setPadSpeed(int v) { padSpeed = Math.max(0, Math.min(3, v)); persistUiPrefs(); }
    public synchronized int getScrollSpeed() { return scrollSpeed; }
    public synchronized void setScrollSpeed(int v) { scrollSpeed = Math.max(0, Math.min(3, v)); persistUiPrefs(); }
    public synchronized boolean isHapticsEnabled() { return hapticsEnabled; }
    public synchronized void setHapticsEnabled(boolean v) { hapticsEnabled = v; persistUiPrefs(); }
    public synchronized boolean isTrailEnabled() { return trailEnabled; }
    public synchronized void setTrailEnabled(boolean v) { trailEnabled = v; persistUiPrefs(); }
    public synchronized String getLookMode() { return lookMode; }
    public synchronized void setLookMode(String v) { lookMode = v == null ? "Frosted" : v; persistUiPrefs(); }
    public synchronized boolean isMediaNotifEnabled() { return mediaNotifEnabled; }
    public synchronized void setMediaNotifEnabled(boolean v) { mediaNotifEnabled = v; persistUiPrefs(); }
}
