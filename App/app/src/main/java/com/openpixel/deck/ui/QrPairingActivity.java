package com.openpixel.deck.ui;

import android.Manifest;
import android.app.Activity;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Context;
import android.content.pm.PackageManager;
import android.graphics.Color;
import android.graphics.SurfaceTexture;
import android.graphics.drawable.GradientDrawable;
import android.hardware.camera2.CameraCaptureSession;
import android.hardware.camera2.CameraCharacteristics;
import android.hardware.camera2.CameraDevice;
import android.hardware.camera2.CameraManager;
import android.hardware.camera2.CaptureRequest;
import android.net.Uri;
import android.os.Bundle;
import android.text.InputType;
import android.view.Gravity;
import android.view.Surface;
import android.view.TextureView;
import android.view.View;
import android.widget.Button;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

import com.openpixel.deck.security.PinnedDeckClient;
import com.openpixel.deck.security.SecurePairingStore;

import java.util.Collections;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/**
 * Private Wi-Fi QR scan & 6-digit pairing code activity for OpenPixel Deck.
 */
public final class QrPairingActivity extends Activity {
    private static final int REQ_CAMERA = 1091;

    private TextureView viewfinder;
    private CameraDevice cameraDevice;
    private CameraCaptureSession captureSession;

    private EditText etIp, etPort, etCode, etCertFp, etQrUri;
    private TextView tvStatus;
    private PinnedDeckClient client;
    private ExecutorService netExecutor;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        netExecutor = Executors.newSingleThreadExecutor();
        client = new PinnedDeckClient(this, new SecurePairingStore(this));

        float dp = getResources().getDisplayMetrics().density;
        ScrollView scroll = new ScrollView(this);
        scroll.setBackgroundColor(Color.rgb(8, 9, 12));

        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setPadding(Math.round(18 * dp), Math.round(24 * dp), Math.round(18 * dp), Math.round(24 * dp));
        scroll.addView(root);

        TextView title = new TextView(this);
        title.setText("Pair with Windows PC");
        title.setTextColor(Color.WHITE);
        title.setTextSize(22f);
        title.getPaint().setFakeBoldText(true);
        root.addView(title);

        TextView sub = new TextView(this);
        sub.setText("Open OpenPixel Settings > PC Deck on your Windows PC, generate a 2-minute QR/code, then scan or enter it below. Both devices must be on the same Private Wi-Fi.");
        sub.setTextColor(Color.rgb(160, 170, 185));
        sub.setTextSize(13f);
        sub.setPadding(0, Math.round(6 * dp), 0, Math.round(14 * dp));
        root.addView(sub);

        // Camera2 viewfinder card
        FrameLayout camCard = new FrameLayout(this);
        LinearLayout.LayoutParams camLp = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, Math.round(200 * dp));
        camLp.bottomMargin = Math.round(14 * dp);
        camCard.setLayoutParams(camLp);
        GradientDrawable camBg = new GradientDrawable();
        camBg.setColor(Color.rgb(18, 22, 29));
        camBg.setCornerRadius(20 * dp);
        camBg.setStroke(Math.round(1.5f * dp), Color.rgb(43, 228, 234));
        camCard.setBackground(camBg);

        viewfinder = new TextureView(this);
        viewfinder.setSurfaceTextureListener(new TextureView.SurfaceTextureListener() {
            @Override public void onSurfaceTextureAvailable(SurfaceTexture st, int w, int h) { startCameraIfPermitted(); }
            @Override public void onSurfaceTextureSizeChanged(SurfaceTexture st, int w, int h) {}
            @Override public boolean onSurfaceTextureDestroyed(SurfaceTexture st) { closeCamera(); return true; }
            @Override public void onSurfaceTextureUpdated(SurfaceTexture st) {}
        });
        camCard.addView(viewfinder);

        TextView camHint = new TextView(this);
        camHint.setText("Point camera at PC Deck QR code\n(or tap Paste QR Link / Auto-Find PC below)");
        camHint.setGravity(Gravity.CENTER);
        camHint.setTextColor(Color.argb(215, 245, 247, 250));
        camHint.setBackgroundColor(Color.argb(120, 8, 10, 14));
        camHint.setPadding(12, 8, 12, 8);
        FrameLayout.LayoutParams hintLp = new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.WRAP_CONTENT, FrameLayout.LayoutParams.WRAP_CONTENT, Gravity.BOTTOM | Gravity.CENTER_HORIZONTAL);
        hintLp.bottomMargin = Math.round(10 * dp);
        camCard.addView(camHint, hintLp);
        root.addView(camCard);

        // QR URI paste row
        LinearLayout qrRow = new LinearLayout(this);
        qrRow.setOrientation(LinearLayout.HORIZONTAL);
        etQrUri = makeField("openpixel-deck://pair?...");
        LinearLayout.LayoutParams fLp = new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f);
        qrRow.addView(etQrUri, fLp);

        Button btnPaste = makePillButton("Paste QR", Color.rgb(34, 42, 54), Color.WHITE);
        btnPaste.setOnClickListener(v -> pasteAndParseQr());
        qrRow.addView(btnPaste);
        root.addView(qrRow);

        Button btnDiscover = makePillButton("Auto-Find PC on Private Wi-Fi", Color.rgb(26, 60, 68), Color.rgb(43, 228, 234));
        LinearLayout.LayoutParams discLp = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        discLp.topMargin = Math.round(10 * dp);
        discLp.bottomMargin = Math.round(10 * dp);
        btnDiscover.setOnClickListener(v -> discoverPc());
        root.addView(btnDiscover, discLp);

        etIp = makeField("PC Private Wi-Fi IP (e.g. 192.168.1.42)");
        etPort = makeField("TLS Port (default 47825)");
        etPort.setText("47825");
        etPort.setInputType(InputType.TYPE_CLASS_NUMBER);
        etCode = makeField("6-digit 2-minute pairing code");
        etCode.setInputType(InputType.TYPE_CLASS_NUMBER);
        etCertFp = makeField("PC Certificate SHA-256 Fingerprint (auto from QR/scan)");

        root.addView(etIp);
        root.addView(etPort);
        root.addView(etCode);
        root.addView(etCertFp);

        tvStatus = new TextView(this);
        tvStatus.setTextColor(Color.rgb(43, 228, 234));
        tvStatus.setTextSize(13f);
        tvStatus.setPadding(0, Math.round(10 * dp), 0, Math.round(10 * dp));
        root.addView(tvStatus);

        Button btnPair = makePillButton("Pair & Verify Certificate", Color.rgb(43, 228, 234), Color.rgb(8, 12, 16));
        btnPair.setOnClickListener(v -> submitPair());
        root.addView(btnPair);

        Button btnCancel = makePillButton("Cancel", Color.rgb(28, 32, 40), Color.rgb(190, 200, 212));
        LinearLayout.LayoutParams cLp = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        cLp.topMargin = Math.round(10 * dp);
        btnCancel.setOnClickListener(v -> finish());
        root.addView(btnCancel, cLp);

        setContentView(scroll);

        if (checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(new String[]{Manifest.permission.CAMERA}, REQ_CAMERA);
        }
    }

    private EditText makeField(String hint) {
        float dp = getResources().getDisplayMetrics().density;
        EditText et = new EditText(this);
        et.setHint(hint);
        et.setHintTextColor(Color.rgb(120, 130, 145));
        et.setTextColor(Color.WHITE);
        et.setTextSize(14f);
        et.setSingleLine(true);
        GradientDrawable bg = new GradientDrawable();
        bg.setColor(Color.rgb(21, 25, 33));
        bg.setCornerRadius(14 * dp);
        bg.setStroke(Math.round(1 * dp), Color.argb(60, 255, 255, 255));
        et.setBackground(bg);
        et.setPadding(Math.round(14 * dp), Math.round(11 * dp), Math.round(14 * dp), Math.round(11 * dp));
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        lp.bottomMargin = Math.round(8 * dp);
        et.setLayoutParams(lp);
        return et;
    }

    private Button makePillButton(String text, int bgColor, int fgColor) {
        float dp = getResources().getDisplayMetrics().density;
        Button b = new Button(this);
        b.setText(text);
        b.setAllCaps(false);
        b.setTextColor(fgColor);
        b.setTextSize(14f);
        b.getPaint().setFakeBoldText(true);
        GradientDrawable bg = new GradientDrawable();
        bg.setColor(bgColor);
        bg.setCornerRadius(18 * dp);
        b.setBackground(bg);
        b.setPadding(Math.round(16 * dp), Math.round(10 * dp), Math.round(16 * dp), Math.round(10 * dp));
        return b;
    }

    private void pasteAndParseQr() {
        String txt = etQrUri.getText().toString().trim();
        if (txt.isEmpty()) {
            ClipboardManager cm = (ClipboardManager) getSystemService(Context.CLIPBOARD_SERVICE);
            if (cm != null && cm.hasPrimaryClip() && cm.getPrimaryClip() != null && cm.getPrimaryClip().getItemCount() > 0) {
                CharSequence cs = cm.getPrimaryClip().getItemAt(0).coerceToText(this);
                if (cs != null) txt = cs.toString().trim();
                etQrUri.setText(txt);
            }
        }
        if (txt.startsWith("openpixel-deck://")) {
            Uri uri = Uri.parse(txt);
            String ip = uri.getQueryParameter("ip");
            String port = uri.getQueryParameter("port");
            String code = uri.getQueryParameter("code");
            String cert = uri.getQueryParameter("cert");
            if (ip != null) etIp.setText(ip);
            if (port != null) etPort.setText(port);
            if (code != null) etCode.setText(code);
            if (cert != null) etCertFp.setText(cert);
            tvStatus.setText("Loaded pairing payload from QR link.");
        } else {
            tvStatus.setText("No openpixel-deck:// QR payload found.");
        }
    }

    private void discoverPc() {
        tvStatus.setText("Searching Private Wi-Fi (UDP 47826)...");
        netExecutor.execute(() -> {
            PinnedDeckClient.ProbeResult pr = client.discoverViaUdpBroadcast();
            runOnUiThread(() -> {
                if (pr != null && !pr.ip.isEmpty()) {
                    etIp.setText(pr.ip);
                    etPort.setText(String.valueOf(pr.tlsPort));
                    if (!pr.certFingerprint.isEmpty()) etCertFp.setText(pr.certFingerprint);
                    tvStatus.setText("Found " + pr.pcName + " at " + pr.ip + " (cert pinned). Enter 6-digit code shown on PC.");
                } else {
                    tvStatus.setText("PC not found via broadcast. Verify PC Deck is ON & Wi-Fi is Private, or enter IP manually.");
                }
            });
        });
    }

    private void submitPair() {
        final String ip = etIp.getText().toString().trim();
        final String code = etCode.getText().toString().trim();
        final String cert = etCertFp.getText().toString().trim();
        int port = 47825;
        try { port = Integer.parseInt(etPort.getText().toString().trim()); } catch (Exception ignored) {}
        final int finalPort = port;

        if (ip.isEmpty() || code.length() < 6) {
            tvStatus.setText("Enter valid PC IP and 6-digit pairing code.");
            return;
        }
        tvStatus.setText("Connecting over TLS... Approve the confirmation dialog on your Windows PC.");
        netExecutor.execute(() -> {
            PinnedDeckClient.PairOutcome res = client.pairWithPc(ip, finalPort, code, cert);
            runOnUiThread(() -> {
                if (res.ok) {
                    setResult(RESULT_OK);
                    finish();
                } else {
                    tvStatus.setText("Pairing failed: " + res.error);
                }
            });
        });
    }

    private void startCameraIfPermitted() {
        if (checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) return;
        if (!viewfinder.isAvailable()) return;
        try {
            CameraManager cm = (CameraManager) getSystemService(Context.CAMERA_SERVICE);
            if (cm == null) return;
            String backId = null;
            for (String id : cm.getCameraIdList()) {
                Integer facing = cm.getCameraCharacteristics(id).get(CameraCharacteristics.LENS_FACING);
                if (facing != null && facing == CameraCharacteristics.LENS_FACING_BACK) {
                    backId = id;
                    break;
                }
            }
            if (backId == null && cm.getCameraIdList().length > 0) backId = cm.getCameraIdList()[0];
            if (backId == null) return;

            cm.openCamera(backId, new CameraDevice.StateCallback() {
                @Override
                public void onOpened(CameraDevice cam) {
                    cameraDevice = cam;
                    try {
                        SurfaceTexture st = viewfinder.getSurfaceTexture();
                        if (st == null) return;
                        st.setDefaultBufferSize(1280, 720);
                        Surface surf = new Surface(st);
                        final CaptureRequest.Builder req = cam.createCaptureRequest(CameraDevice.TEMPLATE_PREVIEW);
                        req.addTarget(surf);
                        cam.createCaptureSession(Collections.singletonList(surf), new CameraCaptureSession.StateCallback() {
                            @Override
                            public void onConfigured(CameraCaptureSession session) {
                                captureSession = session;
                                try {
                                    req.set(CaptureRequest.CONTROL_AF_MODE, CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_PICTURE);
                                    session.setRepeatingRequest(req.build(), null, null);
                                } catch (Exception ignored) { }
                            }
                            @Override public void onConfigureFailed(CameraCaptureSession session) {}
                        }, null);
                    } catch (Exception ignored) { }
                }
                @Override public void onDisconnected(CameraDevice cam) { cam.close(); }
                @Override public void onError(CameraDevice cam, int err) { cam.close(); }
            }, null);
        } catch (Exception ignored) { }
    }

    private void closeCamera() {
        if (captureSession != null) {
            try { captureSession.close(); } catch (Exception ignored) {}
            captureSession = null;
        }
        if (cameraDevice != null) {
            try { cameraDevice.close(); } catch (Exception ignored) {}
            cameraDevice = null;
        }
    }

    @Override
    public void onRequestPermissionsResult(int requestCode, String[] permissions, int[] grantResults) {
        if (requestCode == REQ_CAMERA && grantResults.length > 0 && grantResults[0] == PackageManager.PERMISSION_GRANTED) {
            startCameraIfPermitted();
        }
    }

    @Override
    protected void onDestroy() {
        closeCamera();
        netExecutor.shutdownNow();
        super.onDestroy();
    }
}
