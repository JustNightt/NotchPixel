package com.openpixel.deck.ui;

import android.Manifest;
import android.app.Activity;
import android.content.ClipboardManager;
import android.content.Context;
import android.content.pm.PackageManager;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.ImageFormat;
import android.graphics.Matrix;
import android.graphics.Outline;
import android.graphics.Paint;
import android.graphics.RectF;
import android.graphics.SurfaceTexture;
import android.graphics.drawable.GradientDrawable;
import android.hardware.camera2.CameraCaptureSession;
import android.hardware.camera2.CameraCharacteristics;
import android.hardware.camera2.CameraDevice;
import android.hardware.camera2.CameraManager;
import android.hardware.camera2.CaptureRequest;
import android.hardware.camera2.params.StreamConfigurationMap;
import android.media.Image;
import android.media.ImageReader;
import android.os.Bundle;
import android.os.Handler;
import android.os.HandlerThread;
import android.text.InputType;
import android.util.Size;
import android.view.Gravity;
import android.view.Surface;
import android.view.TextureView;
import android.view.View;
import android.view.ViewOutlineProvider;
import android.widget.Button;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

import com.openpixel.deck.qr.PairingUriParser;
import com.openpixel.deck.qr.QrDecoder;
import com.openpixel.deck.security.PinnedDeckClient;
import com.openpixel.deck.security.SecurePairingStore;

import java.nio.ByteBuffer;
import java.util.Arrays;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.atomic.AtomicBoolean;

/**
 * Sleek Liquid-Glass QR & Private Wi-Fi Pairing screen.
 * Fixes aspect-ratio squishing using exact Camera2 sensor-to-view center-crop Matrix.
 */
public final class QrPairingActivity extends Activity {
    private static final int REQ_CAMERA = 204;

    private TextureView viewfinder;
    private EditText etQrUri, etIp, etPort, etCode, etCertFp;
    private TextView tvStatus;
    private LinearLayout manualBox;
    private Button btnToggleManual;

    private CameraDevice cameraDevice;
    private CameraCaptureSession captureSession;
    private ImageReader imageReader;
    private HandlerThread scanThread;
    private Handler scanHandler;
    private Size previewSize = new Size(1280, 720);
    private int sensorOrientation = 90;

    private final AtomicBoolean decoding = new AtomicBoolean(false);
    private final AtomicBoolean qrLocked = new AtomicBoolean(false);
    private volatile long lastScanMs = 0L;

    private PinnedDeckClient client;
    private ExecutorService netExecutor;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        netExecutor = Executors.newSingleThreadExecutor();
        client = new PinnedDeckClient(this, new SecurePairingStore(this));

        final float dp = getResources().getDisplayMetrics().density;

        FrameLayout outer = new FrameLayout(this);
        GradientDrawable bgGrad = new GradientDrawable(
                GradientDrawable.Orientation.TL_BR,
                new int[]{Color.rgb(9, 14, 18), Color.rgb(12, 22, 26), Color.rgb(8, 10, 14)});
        outer.setBackground(bgGrad);

        ScrollView scroll = new ScrollView(this);
        scroll.setFillViewport(true);
        scroll.setVerticalScrollBarEnabled(false);

        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setPadding(Math.round(18 * dp), Math.round(28 * dp), Math.round(18 * dp), Math.round(28 * dp));
        scroll.addView(root);
        outer.addView(scroll);

        // Header bar with Back pill + Title
        LinearLayout headerRow = new LinearLayout(this);
        headerRow.setOrientation(LinearLayout.HORIZONTAL);
        headerRow.setGravity(Gravity.CENTER_VERTICAL);
        LinearLayout.LayoutParams hLp = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        hLp.bottomMargin = Math.round(14 * dp);
        headerRow.setLayoutParams(hLp);

        Button btnBack = makePillButton("← Back", Color.argb(36, 255, 255, 255), Color.WHITE, 14);
        btnBack.setOnClickListener(v -> finish());
        headerRow.addView(btnBack);

        TextView title = new TextView(this);
        title.setText("Pair with Windows PC");
        title.setTextColor(Color.WHITE);
        title.setTextSize(19f);
        title.getPaint().setFakeBoldText(true);
        title.setPadding(Math.round(12 * dp), 0, 0, 0);
        headerRow.addView(title);
        root.addView(headerRow);

        TextView sub = new TextView(this);
        sub.setText("Open NotchPixel Settings → PC Deck on Windows, then scan the QR code below.");
        sub.setTextColor(Color.argb(185, 225, 232, 240));
        sub.setTextSize(13f);
        sub.setPadding(Math.round(2 * dp), 0, Math.round(2 * dp), Math.round(14 * dp));
        root.addView(sub);

        // Tall, true-aspect-ratio rounded Camera2 viewfinder card (NEVER squished)
        final float camRadius = 24 * dp;
        FrameLayout camCard = new FrameLayout(this);
        LinearLayout.LayoutParams camLp = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, Math.round(290 * dp));
        camLp.bottomMargin = Math.round(14 * dp);
        camCard.setLayoutParams(camLp);
        camCard.setBackgroundColor(Color.rgb(12, 16, 22));
        camCard.setClipToOutline(true);
        camCard.setOutlineProvider(new ViewOutlineProvider() {
            @Override
            public void getOutline(View view, Outline outline) {
                outline.setRoundRect(0, 0, view.getWidth(), view.getHeight(), camRadius);
            }
        });

        viewfinder = new TextureView(this);
        viewfinder.setLayoutParams(new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT));
        viewfinder.setSurfaceTextureListener(new TextureView.SurfaceTextureListener() {
            @Override public void onSurfaceTextureAvailable(SurfaceTexture st, int w, int h) {
                configurePreviewTransform(w, h);
                startCameraIfPermitted();
            }
            @Override public void onSurfaceTextureSizeChanged(SurfaceTexture st, int w, int h) {
                configurePreviewTransform(w, h);
            }
            @Override public boolean onSurfaceTextureDestroyed(SurfaceTexture st) { closeCamera(); return true; }
            @Override public void onSurfaceTextureUpdated(SurfaceTexture st) {}
        });
        camCard.addView(viewfinder);

        // Crisp QR scanning corner reticle + glass border overlay
        View reticle = new QrReticleOverlay(this, camRadius);
        camCard.addView(reticle, new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT));

        TextView camHint = new TextView(this);
        camHint.setText("Align NotchPixel QR inside frame");
        camHint.setGravity(Gravity.CENTER);
        camHint.setTextSize(12f);
        camHint.setTextColor(Color.WHITE);
        GradientDrawable hintBg = new GradientDrawable();
        hintBg.setColor(Color.argb(150, 10, 12, 16));
        hintBg.setCornerRadius(14 * dp);
        camHint.setBackground(hintBg);
        camHint.setPadding(Math.round(14 * dp), Math.round(6 * dp), Math.round(14 * dp), Math.round(6 * dp));
        FrameLayout.LayoutParams hintLp = new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.WRAP_CONTENT, FrameLayout.LayoutParams.WRAP_CONTENT,
                Gravity.BOTTOM | Gravity.CENTER_HORIZONTAL);
        hintLp.bottomMargin = Math.round(12 * dp);
        camCard.addView(camHint, hintLp);
        root.addView(camCard);

        // Status banner
        tvStatus = new TextView(this);
        tvStatus.setText("Camera ready — scanning automatically");
        tvStatus.setTextColor(Color.rgb(43, 228, 234));
        tvStatus.setTextSize(13f);
        tvStatus.setGravity(Gravity.CENTER);
        tvStatus.setPadding(Math.round(8 * dp), Math.round(4 * dp), Math.round(8 * dp), Math.round(12 * dp));
        root.addView(tvStatus);

        // Primary quick actions row: Auto-Find PC + Paste QR
        LinearLayout quickRow = new LinearLayout(this);
        quickRow.setOrientation(LinearLayout.HORIZONTAL);
        LinearLayout.LayoutParams qLp = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        qLp.bottomMargin = Math.round(12 * dp);
        quickRow.setLayoutParams(qLp);

        Button btnDiscover = makePillButton("Auto-Find PC", Color.rgb(43, 228, 234), Color.rgb(8, 12, 16), 16);
        btnDiscover.setOnClickListener(v -> discoverPc());
        LinearLayout.LayoutParams dLp = new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f);
        dLp.rightMargin = Math.round(8 * dp);
        quickRow.addView(btnDiscover, dLp);

        Button btnPaste = makePillButton("Paste QR Link", Color.argb(38, 255, 255, 255), Color.WHITE, 16);
        btnPaste.setOnClickListener(v -> pasteAndParseQr());
        quickRow.addView(btnPaste, new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f));
        root.addView(quickRow);

        // Collapsible manual entry Glass card (keeps screen super clean!)
        LiquidGlassHelper.GlassContainer manualCard = new LiquidGlassHelper.GlassContainer(this, 20);
        manualCard.setOrientation(LinearLayout.VERTICAL);
        manualCard.setGlassStyle(true, false, Color.rgb(43, 228, 234));
        manualCard.setPadding(Math.round(14 * dp), Math.round(12 * dp), Math.round(14 * dp), Math.round(12 * dp));

        btnToggleManual = makePillButton("Enter IP & 6-Digit Code Manually ▾", Color.TRANSPARENT, Color.argb(220, 235, 240, 248), 12);
        btnToggleManual.setOnClickListener(v -> {
            boolean show = manualBox.getVisibility() != View.VISIBLE;
            manualBox.setVisibility(show ? View.VISIBLE : View.GONE);
            btnToggleManual.setText(show ? "Hide Manual Setup ▴" : "Enter IP & 6-Digit Code Manually ▾");
        });
        manualCard.addView(btnToggleManual);

        manualBox = new LinearLayout(this);
        manualBox.setOrientation(LinearLayout.VERTICAL);
        manualBox.setVisibility(View.GONE);
        manualBox.setPadding(0, Math.round(10 * dp), 0, 0);

        etQrUri = makeField("notchpixel-deck://pair?...");
        etIp = makeField("PC Private Wi-Fi IP (e.g. 192.168.1.42)");
        etPort = makeField("TLS Port (default 47825)");
        etPort.setText("47825");
        etPort.setInputType(InputType.TYPE_CLASS_NUMBER);
        etCode = makeField("6-digit pairing code");
        etCode.setInputType(InputType.TYPE_CLASS_NUMBER);
        etCertFp = makeField("PC Cert SHA-256 (auto from QR/Auto-Find)");

        manualBox.addView(etQrUri);
        manualBox.addView(etIp);
        manualBox.addView(etPort);
        manualBox.addView(etCode);
        manualBox.addView(etCertFp);

        Button btnPair = makePillButton("Pair & Verify Certificate", Color.rgb(43, 228, 234), Color.rgb(8, 12, 16), 16);
        LinearLayout.LayoutParams pLp = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        pLp.topMargin = Math.round(6 * dp);
        btnPair.setOnClickListener(v -> submitPair());
        manualBox.addView(btnPair, pLp);

        manualCard.addView(manualBox);
        root.addView(manualCard);

        setContentView(outer);

        if (checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(new String[]{Manifest.permission.CAMERA}, REQ_CAMERA);
        }
    }

    /**
     * Center-crop Matrix transform so the Camera2 preview NEVER stretches or squishes
     * inside the viewfinder card regardless of sensor aspect ratio or orientation.
     */
    private void configurePreviewTransform(int viewW, int viewH) {
        if (viewfinder == null || previewSize == null || viewW <= 0 || viewH <= 0) return;
        int rotation = Surface.ROTATION_0;
        try {
            if (getWindowManager() != null && getWindowManager().getDefaultDisplay() != null) {
                rotation = getWindowManager().getDefaultDisplay().getRotation();
            }
        } catch (Exception ignored) {}

        // In portrait (ROTATION_0/180), Camera2 sensor buffer is typically landscape (e.g. 1280x720)
        // and rotated 90 degrees by TextureView automatically; its effective portrait dimensions are swapped.
        boolean swapDims = (rotation == Surface.ROTATION_0 || rotation == Surface.ROTATION_180)
                ? (sensorOrientation == 90 || sensorOrientation == 270)
                : (sensorOrientation == 0 || sensorOrientation == 180);

        float bufW = swapDims ? previewSize.getHeight() : previewSize.getWidth();
        float bufH = swapDims ? previewSize.getWidth() : previewSize.getHeight();

        float viewRatio = (float) viewW / (float) viewH;
        float bufRatio = bufW / bufH;

        float scaleX = 1f;
        float scaleY = 1f;
        if (bufRatio > viewRatio) {
            scaleX = bufRatio / viewRatio;
        } else {
            scaleY = viewRatio / bufRatio;
        }

        Matrix m = new Matrix();
        m.setScale(scaleX, scaleY, viewW * 0.5f, viewH * 0.5f);
        if (rotation == Surface.ROTATION_90 || rotation == Surface.ROTATION_270) {
            m.postRotate(90 * (rotation - 2), viewW * 0.5f, viewH * 0.5f);
        } else if (rotation == Surface.ROTATION_180) {
            m.postRotate(180, viewW * 0.5f, viewH * 0.5f);
        }
        viewfinder.setTransform(m);
    }

    private static final class QrReticleOverlay extends View {
        private final Paint borderPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
        private final Paint cornerPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
        private final Paint maskPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
        private final RectF box = new RectF();
        private final float outerRadius;
        private final float dp;

        QrReticleOverlay(Context ctx, float outerRadius) {
            super(ctx);
            this.outerRadius = outerRadius;
            this.dp = ctx.getResources().getDisplayMetrics().density;
            borderPaint.setStyle(Paint.Style.STROKE);
            borderPaint.setStrokeWidth(1.5f * dp);
            borderPaint.setColor(Color.argb(95, 255, 255, 255));
            cornerPaint.setStyle(Paint.Style.STROKE);
            cornerPaint.setStrokeWidth(3.2f * dp);
            cornerPaint.setStrokeCap(Paint.Cap.ROUND);
            cornerPaint.setColor(Color.rgb(43, 228, 234));
            maskPaint.setColor(Color.argb(55, 0, 0, 0));
        }

        @Override
        protected void onDraw(Canvas c) {
            int w = getWidth(), h = getHeight();
            if (w <= 0 || h <= 0) return;
            float side = Math.min(w, h) * 0.62f;
            float l = (w - side) * 0.5f;
            float t = (h - side) * 0.44f;
            float r = l + side;
            float b = t + side;
            float len = 24f * dp;

            // Corner brackets
            c.drawLine(l, t, l + len, t, cornerPaint);
            c.drawLine(l, t, l, t + len, cornerPaint);
            c.drawLine(r - len, t, r, t, cornerPaint);
            c.drawLine(r, t, r, t + len, cornerPaint);
            c.drawLine(l, b, l + len, b, cornerPaint);
            c.drawLine(l, b - len, l, b, cornerPaint);
            c.drawLine(r - len, b, r, b, cornerPaint);
            c.drawLine(r, b - len, r, b, cornerPaint);

            box.set(dp, dp, w - dp, h - dp);
            c.drawRoundRect(box, outerRadius, outerRadius, borderPaint);
        }
    }

    @Override
    protected void onResume() {
        super.onResume();
        if (viewfinder != null && viewfinder.isAvailable()) {
            startCameraIfPermitted();
        }
    }

    @Override
    protected void onPause() {
        closeCamera();
        super.onPause();
    }

    private EditText makeField(String hint) {
        float dp = getResources().getDisplayMetrics().density;
        EditText et = new EditText(this);
        et.setHint(hint);
        et.setHintTextColor(Color.argb(130, 210, 220, 235));
        et.setTextColor(Color.WHITE);
        et.setTextSize(14f);
        et.setSingleLine(true);
        GradientDrawable bg = new GradientDrawable();
        bg.setColor(Color.argb(38, 255, 255, 255));
        bg.setCornerRadius(14 * dp);
        bg.setStroke(Math.round(1 * dp), Color.argb(55, 255, 255, 255));
        et.setBackground(bg);
        et.setPadding(Math.round(14 * dp), Math.round(11 * dp), Math.round(14 * dp), Math.round(11 * dp));
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        lp.bottomMargin = Math.round(8 * dp);
        et.setLayoutParams(lp);
        return et;
    }

    private Button makePillButton(String text, int bgColor, int fgColor, float radDp) {
        float dp = getResources().getDisplayMetrics().density;
        Button b = new Button(this);
        b.setText(text);
        b.setAllCaps(false);
        b.setTextColor(fgColor);
        b.setTextSize(13.5f);
        b.getPaint().setFakeBoldText(true);
        GradientDrawable bg = new GradientDrawable();
        bg.setColor(bgColor);
        bg.setCornerRadius(radDp * dp);
        b.setBackground(bg);
        b.setMinHeight(0);
        b.setMinimumHeight(0);
        b.setPadding(Math.round(16 * dp), Math.round(11 * dp), Math.round(16 * dp), Math.round(11 * dp));
        return b;
    }

    private void applyParsedPayload(String rawUri, PairingUriParser.PairingPayload p) {
        etQrUri.setText(rawUri);
        if (!p.ip.isEmpty()) etIp.setText(p.ip);
        etPort.setText(String.valueOf(p.port));
        if (!p.code.isEmpty()) etCode.setText(p.code);
        if (!p.certFingerprint.isEmpty()) etCertFp.setText(p.certFingerprint);
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
        PairingUriParser.PairingPayload p = PairingUriParser.parse(txt);
        if (p != null) {
            applyParsedPayload(txt, p);
            manualBox.setVisibility(View.VISIBLE);
            tvStatus.setText("Loaded QR payload — pairing...");
            if (p.isValidForPairing()) submitPair();
        } else {
            tvStatus.setText("No NotchPixel QR link found in clipboard.");
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
                    manualBox.setVisibility(View.VISIBLE);
                    btnToggleManual.setText("Hide Manual Setup ▴");
                    tvStatus.setText("Found " + pr.pcName + " (" + pr.ip + "). Enter the 6-digit code shown on PC.");
                } else {
                    tvStatus.setText("PC not found. Make sure PC Deck is ON and both devices share Private Wi-Fi.");
                }
            });
        });
    }

    private void submitPair() {
        final String ip = etIp.getText().toString().trim();
        final String code = etCode.getText().toString().trim();
        final String cert = PairingUriParser.normalizeFingerprint(etCertFp.getText().toString());
        int port = 47825;
        try { port = Integer.parseInt(etPort.getText().toString().trim()); } catch (Exception ignored) {}
        final int finalPort = port;

        if (ip.isEmpty() || code.length() < 6) {
            tvStatus.setText("Enter valid PC IP and 6-digit pairing code.");
            qrLocked.set(false);
            return;
        }
        tvStatus.setText("Connecting over TLS... Click Allow on your Windows PC.");
        netExecutor.execute(() -> {
            PinnedDeckClient.PairOutcome res = client.pairWithPc(ip, finalPort, code, cert);
            runOnUiThread(() -> {
                if (res.ok) {
                    setResult(RESULT_OK);
                    finish();
                } else {
                    qrLocked.set(false);
                    tvStatus.setText("Pairing failed: " + res.error);
                }
            });
        });
    }

    private static Size chooseOptimalPreviewSize(CameraCharacteristics chars) {
        try {
            StreamConfigurationMap map = chars.get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP);
            if (map != null) {
                Size[] sizes = map.getOutputSizes(SurfaceTexture.class);
                if (sizes != null && sizes.length > 0) {
                    Size best = sizes[0];
                    int bestDiff = Integer.MAX_VALUE;
                    for (Size s : sizes) {
                        int w = s.getWidth(), h = s.getHeight();
                        if (w >= 720 && w <= 1920 && h >= 480 && h <= 1440) {
                            int diff = Math.abs(w * h - 1280 * 720);
                            if (diff < bestDiff) {
                                bestDiff = diff;
                                best = s;
                            }
                        }
                    }
                    return best;
                }
            }
        } catch (Exception ignored) {}
        return new Size(1280, 720);
    }

    private static Size chooseScanSize(CameraCharacteristics chars) {
        try {
            StreamConfigurationMap map = chars.get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP);
            if (map != null) {
                Size[] sizes = map.getOutputSizes(ImageFormat.YUV_420_888);
                if (sizes != null && sizes.length > 0) {
                    Size best = null;
                    int bestDiff = Integer.MAX_VALUE;
                    for (Size s : sizes) {
                        int w = s.getWidth(), h = s.getHeight();
                        if (w >= 640 && w <= 960 && h >= 480 && h <= 720) {
                            int diff = Math.abs(w * h - 640 * 480);
                            if (diff < bestDiff) {
                                bestDiff = diff;
                                best = s;
                            }
                        }
                    }
                    if (best != null) return best;
                }
            }
        } catch (Exception ignored) { }
        return new Size(640, 480);
    }

    private static byte[] extractLumaPlane(Image img) {
        Image.Plane plane = img.getPlanes()[0];
        ByteBuffer buf = plane.getBuffer();
        int w = img.getWidth();
        int h = img.getHeight();
        int rowStride = plane.getRowStride();
        int pixelStride = plane.getPixelStride();
        byte[] out = new byte[w * h];
        if (pixelStride == 1 && rowStride == w) {
            buf.get(out, 0, out.length);
            return out;
        }
        byte[] rowBuf = new byte[rowStride];
        for (int y = 0; y < h; y++) {
            buf.position(y * rowStride);
            int avail = Math.min(rowStride, buf.remaining());
            buf.get(rowBuf, 0, avail);
            int dstRow = y * w;
            if (pixelStride == 1) {
                System.arraycopy(rowBuf, 0, out, dstRow, w);
            } else {
                for (int x = 0; x < w; x++) out[dstRow + x] = rowBuf[x * pixelStride];
            }
        }
        return out;
    }

    private void startCameraIfPermitted() {
        if (checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) return;
        if (!viewfinder.isAvailable() || cameraDevice != null) return;
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

            CameraCharacteristics chars = cm.getCameraCharacteristics(backId);
            Integer orient = chars.get(CameraCharacteristics.SENSOR_ORIENTATION);
            sensorOrientation = orient != null ? orient : 90;
            previewSize = chooseOptimalPreviewSize(chars);
            runOnUiThread(() -> configurePreviewTransform(viewfinder.getWidth(), viewfinder.getHeight()));

            scanThread = new HandlerThread("NotchPixelQrScan");
            scanThread.start();
            scanHandler = new Handler(scanThread.getLooper());

            Size scanSize = chooseScanSize(chars);
            imageReader = ImageReader.newInstance(scanSize.getWidth(), scanSize.getHeight(), ImageFormat.YUV_420_888, 2);
            imageReader.setOnImageAvailableListener(reader -> {
                Image img = null;
                try {
                    img = reader.acquireLatestImage();
                    if (img == null) return;
                    long now = System.currentTimeMillis();
                    if (qrLocked.get() || (now - lastScanMs) < 140L || !decoding.compareAndSet(false, true)) return;
                    lastScanMs = now;
                    int w = img.getWidth();
                    int h = img.getHeight();
                    byte[] luma = extractLumaPlane(img);
                    img.close();
                    img = null;
                    try {
                        String decoded = QrDecoder.decodeLuma(luma, w, h);
                        if (decoded != null && !decoded.isEmpty()) {
                            PairingUriParser.PairingPayload payload = PairingUriParser.parse(decoded);
                            if (payload != null && payload.isValidForPairing() && qrLocked.compareAndSet(false, true)) {
                                runOnUiThread(() -> {
                                    applyParsedPayload(decoded, payload);
                                    tvStatus.setText("QR scanned! Pairing with " + payload.ip + "...");
                                    submitPair();
                                });
                            }
                        }
                    } finally {
                        decoding.set(false);
                    }
                } catch (Exception ignored) {
                    decoding.set(false);
                } finally {
                    if (img != null) {
                        try { img.close(); } catch (Exception ignored) {}
                    }
                }
            }, scanHandler);

            cm.openCamera(backId, new CameraDevice.StateCallback() {
                @Override
                public void onOpened(CameraDevice cam) {
                    cameraDevice = cam;
                    try {
                        SurfaceTexture st = viewfinder.getSurfaceTexture();
                        if (st == null || imageReader == null) return;
                        st.setDefaultBufferSize(previewSize.getWidth(), previewSize.getHeight());
                        Surface previewSurf = new Surface(st);
                        Surface readerSurf = imageReader.getSurface();
                        final CaptureRequest.Builder req = cam.createCaptureRequest(CameraDevice.TEMPLATE_PREVIEW);
                        req.addTarget(previewSurf);
                        req.addTarget(readerSurf);
                        cam.createCaptureSession(Arrays.asList(previewSurf, readerSurf), new CameraCaptureSession.StateCallback() {
                            @Override
                            public void onConfigured(CameraCaptureSession session) {
                                captureSession = session;
                                try {
                                    req.set(CaptureRequest.CONTROL_AF_MODE, CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_PICTURE);
                                    session.setRepeatingRequest(req.build(), null, scanHandler);
                                } catch (Exception ignored) { }
                            }
                            @Override public void onConfigureFailed(CameraCaptureSession session) {}
                        }, scanHandler);
                    } catch (Exception ignored) { }
                }
                @Override public void onDisconnected(CameraDevice cam) { closeCamera(); }
                @Override public void onError(CameraDevice cam, int err) { closeCamera(); }
            }, scanHandler);
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
        if (imageReader != null) {
            try { imageReader.close(); } catch (Exception ignored) {}
            imageReader = null;
        }
        if (scanThread != null) {
            try { scanThread.quitSafely(); } catch (Exception ignored) {}
            scanThread = null;
            scanHandler = null;
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
