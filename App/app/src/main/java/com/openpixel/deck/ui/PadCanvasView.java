package com.openpixel.deck.ui;

import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Paint;
import android.graphics.RectF;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.os.VibrationEffect;
import android.os.Vibrator;
import android.view.MotionEvent;
import android.view.View;

import java.util.ArrayList;
import java.util.Iterator;
import java.util.List;

/**
 * OpenPixel Deck frosted dot-grid trackpad with glowing cyan square pointer trail.
 */
public final class PadCanvasView extends View {
    public interface PadActionSink {
        void onMove(int dx, int dy);
        void onScroll(int dy);
        void onLeftClick();
        void onRightClick();
        void onDragStart();
        void onDragEnd();
    }

    private static final class TrailDot {
        float x;
        float y;
        long birthMs;
    }

    private final PadActionSink sink;
    private final Paint surfacePaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint borderPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint gridDotPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint trailPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint hintPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final RectF bounds = new RectF();
    private final List<TrailDot> trail = new ArrayList<>();
    private final Handler handler = new Handler(Looper.getMainLooper());
    private final Vibrator vibrator;

    private int padSpeedIndex = 1;    // 0..3 -> 0.65, 1.0, 1.55, 2.25
    private int scrollSpeedIndex = 1; // 0..3
    private boolean trailEnabled = true;
    private boolean hapticsEnabled = true;
    private boolean lightSurface = false;

    private float lastX, lastY, downX, downY;
    private long downTimeMs;
    private float accumDx, accumDy, accumScroll;
    private boolean draggingLock = false;
    private boolean twoFingerMode = false;
    private long twoFingerDownMs;
    private float totalMoveDist;
    private float activeCursorX = -1f, activeCursorY = -1f;

    private final Runnable longPressDragTrigger = new Runnable() {
        @Override
        public void run() {
            if (!twoFingerMode && totalMoveDist < 18f) {
                draggingLock = true;
                vibrateLight();
                if (sink != null) sink.onDragStart();
                invalidate();
            }
        }
    };

    private final Runnable trailFadeTicker = new Runnable() {
        @Override
        public void run() {
            long now = System.currentTimeMillis();
            boolean changed = false;
            Iterator<TrailDot> it = trail.iterator();
            while (it.hasNext()) {
                if (now - it.next().birthMs > 380L) {
                    it.remove();
                    changed = true;
                }
            }
            if (changed || !trail.isEmpty()) {
                invalidate();
                if (!trail.isEmpty()) handler.postDelayed(this, 25);
            }
        }
    };

    public PadCanvasView(Context ctx, PadActionSink sink) {
        super(ctx);
        this.sink = sink;
        this.vibrator = (Vibrator) ctx.getSystemService(Context.VIBRATOR_SERVICE);
        borderPaint.setStyle(Paint.Style.STROKE);
        borderPaint.setStrokeWidth(ctx.getResources().getDisplayMetrics().density * 1.2f);
        hintPaint.setTextAlign(Paint.Align.CENTER);
    }

    public void configure(int padSpd, int scrollSpd, boolean trailOn, boolean hapticsOn, boolean light) {
        this.padSpeedIndex = Math.max(0, Math.min(3, padSpd));
        this.scrollSpeedIndex = Math.max(0, Math.min(3, scrollSpd));
        this.trailEnabled = trailOn;
        this.hapticsEnabled = hapticsOn;
        this.lightSurface = light;
        invalidate();
    }

    private float padMultiplier() {
        switch (padSpeedIndex) {
            case 0: return 0.65f;
            case 2: return 1.55f;
            case 3: return 2.25f;
            default: return 1.0f;
        }
    }

    private float scrollMultiplier() {
        switch (scrollSpeedIndex) {
            case 0: return 0.55f;
            case 2: return 1.45f;
            case 3: return 2.1f;
            default: return 1.0f;
        }
    }

    private void vibrateLight() {
        if (!hapticsEnabled || vibrator == null || !vibrator.hasVibrator()) return;
        try {
            if (Build.VERSION.SDK_INT >= 29) {
                vibrator.vibrate(VibrationEffect.createPredefined(VibrationEffect.EFFECT_TICK));
            } else {
                vibrator.vibrate(VibrationEffect.createOneShot(12, 80));
            }
        } catch (Exception ignored) { }
    }

    private void pushTrailDot(float x, float y) {
        if (!trailEnabled) return;
        TrailDot d = new TrailDot();
        d.x = x;
        d.y = y;
        d.birthMs = System.currentTimeMillis();
        trail.add(d);
        if (trail.size() > 26) trail.remove(0);
        handler.removeCallbacks(trailFadeTicker);
        handler.postDelayed(trailFadeTicker, 25);
    }

    @Override
    protected void onMeasure(int widthMeasureSpec, int heightMeasureSpec) {
        float dp = getResources().getDisplayMetrics().density;
        int w = MeasureSpec.getSize(widthMeasureSpec);
        setMeasuredDimension(w, Math.round(280 * dp));
    }

    @Override
    public boolean onTouchEvent(MotionEvent ev) {
        int action = ev.getActionMasked();
        switch (action) {
            case MotionEvent.ACTION_DOWN:
                getParent().requestDisallowInterceptTouchEvent(true);
                downX = lastX = ev.getX();
                downY = lastY = ev.getY();
                activeCursorX = downX;
                activeCursorY = downY;
                downTimeMs = System.currentTimeMillis();
                accumDx = accumDy = accumScroll = 0f;
                totalMoveDist = 0f;
                twoFingerMode = false;
                draggingLock = false;
                pushTrailDot(downX, downY);
                handler.postDelayed(longPressDragTrigger, 520L);
                invalidate();
                return true;

            case MotionEvent.ACTION_POINTER_DOWN:
                handler.removeCallbacks(longPressDragTrigger);
                twoFingerMode = true;
                twoFingerDownMs = System.currentTimeMillis();
                if (ev.getPointerCount() >= 2) {
                    lastY = (ev.getY(0) + ev.getY(1)) * 0.5f;
                }
                return true;

            case MotionEvent.ACTION_MOVE:
                if (ev.getPointerCount() >= 2) {
                    twoFingerMode = true;
                    float midY = (ev.getY(0) + ev.getY(1)) * 0.5f;
                    float dy = (midY - lastY) * scrollMultiplier();
                    lastY = midY;
                    accumScroll += dy;
                    int steps = (int) (accumScroll / 18f);
                    if (steps != 0) {
                        accumScroll -= steps * 18f;
                        if (sink != null) sink.onScroll(-steps);
                    }
                    pushTrailDot((ev.getX(0) + ev.getX(1)) * 0.5f, midY);
                    invalidate();
                    return true;
                }

                float x = ev.getX();
                float y = ev.getY();
                float dx = x - lastX;
                float dy = y - lastY;
                lastX = x;
                lastY = y;
                activeCursorX = x;
                activeCursorY = y;
                totalMoveDist += (float) Math.hypot(dx, dy);
                if (totalMoveDist > 18f) handler.removeCallbacks(longPressDragTrigger);

                float mult = padMultiplier() * 1.35f;
                accumDx += dx * mult;
                accumDy += dy * mult;
                int sendX = (int) accumDx;
                int sendY = (int) accumDy;
                if (sendX != 0 || sendY != 0) {
                    accumDx -= sendX;
                    accumDy -= sendY;
                    if (sink != null) sink.onMove(sendX, sendY);
                }
                pushTrailDot(x, y);
                invalidate();
                return true;

            case MotionEvent.ACTION_POINTER_UP:
                if (twoFingerMode && (System.currentTimeMillis() - twoFingerDownMs) < 260L && totalMoveDist < 24f) {
                    vibrateLight();
                    if (sink != null) sink.onRightClick();
                }
                return true;

            case MotionEvent.ACTION_UP:
            case MotionEvent.ACTION_CANCEL:
                handler.removeCallbacks(longPressDragTrigger);
                activeCursorX = -1f;
                activeCursorY = -1f;
                if (draggingLock) {
                    draggingLock = false;
                    vibrateLight();
                    if (sink != null) sink.onDragEnd();
                } else if (!twoFingerMode && (System.currentTimeMillis() - downTimeMs) < 250L && totalMoveDist < 16f) {
                    vibrateLight();
                    if (sink != null) sink.onLeftClick();
                }
                twoFingerMode = false;
                invalidate();
                return true;
        }
        return super.onTouchEvent(ev);
    }

    @Override
    protected void onDraw(Canvas canvas) {
        super.onDraw(canvas);
        float dp = getResources().getDisplayMetrics().density;
        int w = getWidth();
        int h = getHeight();
        float rad = 24 * dp;
        bounds.set(dp, dp, w - dp, h - dp);

        surfacePaint.setColor(lightSurface ? Color.argb(236, 234, 240, 248) : Color.argb(235, 19, 23, 30));
        canvas.drawRoundRect(bounds, rad, rad, surfacePaint);

        // Dot grid matrix
        float spacing = 18 * dp;
        gridDotPaint.setColor(lightSurface ? Color.argb(46, 30, 45, 70) : Color.argb(56, 175, 195, 220));
        for (float gx = spacing; gx < w - spacing * 0.5f; gx += spacing) {
            for (float gy = spacing; gy < h - spacing * 0.5f; gy += spacing) {
                canvas.drawCircle(gx, gy, 1.3f * dp, gridDotPaint);
            }
        }

        // Cyan square dot-matrix pointer trail
        long now = System.currentTimeMillis();
        if (trailEnabled) {
            for (int i = 0; i < trail.size(); i++) {
                TrailDot d = trail.get(i);
                float age = (now - d.birthMs) / 380f;
                if (age >= 1f) continue;
                float alpha = 1f - age;
                float sz = (6f + 6f * alpha) * dp;
                trailPaint.setColor(Color.argb((int) (alpha * 220), 43, 228, 234));
                canvas.drawRect(d.x - sz * 0.5f, d.y - sz * 0.5f, d.x + sz * 0.5f, d.y + sz * 0.5f, trailPaint);
            }
        }

        if (activeCursorX >= 0f && activeCursorY >= 0f) {
            float sz = (draggingLock ? 18f : 14f) * dp;
            trailPaint.setColor(draggingLock ? Color.argb(240, 255, 198, 60) : Color.argb(245, 43, 228, 234));
            canvas.drawRect(activeCursorX - sz * 0.5f, activeCursorY - sz * 0.5f,
                    activeCursorX + sz * 0.5f, activeCursorY + sz * 0.5f, trailPaint);
        }

        borderPaint.setColor(draggingLock
                ? Color.rgb(255, 198, 60)
                : (lightSurface ? Color.argb(75, 25, 35, 50) : Color.argb(75, 255, 255, 255)));
        canvas.drawRoundRect(bounds, rad, rad, borderPaint);

        hintPaint.setTextSize(12 * dp);
        hintPaint.setColor(lightSurface ? Color.argb(145, 55, 65, 80) : Color.argb(140, 175, 185, 200));
        String hint = draggingLock
                ? "DRAG LOCKED - release to drop"
                : "Tap = Click  |  2-Finger Tap = Right Click  |  2-Finger = Scroll";
        canvas.drawText(hint, w / 2f, h - 14 * dp, hintPaint);
    }
}
