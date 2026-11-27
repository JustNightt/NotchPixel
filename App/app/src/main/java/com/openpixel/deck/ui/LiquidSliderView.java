package com.openpixel.deck.ui;

import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.LinearGradient;
import android.graphics.Paint;
import android.graphics.Path;
import android.graphics.RectF;
import android.graphics.Shader;
import android.os.SystemClock;
import android.view.MotionEvent;
import android.view.VelocityTracker;
import android.view.View;

/**
 * Liquid Glass Slider ported from Kyant0/AndroidLiquidGlass (LiquidSlider.kt & DampedDragAnimation.kt):
 * - 6dp capsule track with active accent fill
 * - 40dp x 24dp capsule thumb overhanging ends by 10dp
 * - Spring-driven 1.5x press magnification + velocity squash/stretch (velocity / 10f)
 * - Opaque white thumb at rest that dissolves into clear refractive Liquid Glass
 *   with chromatic RGB rim dispersion, specular sheen, and track-refraction magnification on press.
 */
public final class LiquidSliderView extends View {
    public interface OnChangeListener {
        void onChanged(int value, boolean fromUser);
        void onStartTracking();
        void onStopTracking(int value);
    }

    private final Paint trackBgPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint trackFillPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint thumbShadowPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint thumbFillPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint thumbGlassRimPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint thumbChromaPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint thumbSheenPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final RectF trackRect = new RectF();
    private final RectF fillRect = new RectF();
    private final RectF thumbRect = new RectF();
    private final Path thumbClip = new Path();

    private int max = 100;
    private float value = 0f;          // 0..1
    private float displayValue = 0f;   // spring-smoothed 0..1
    private float pressProgress = 0f;  // 0..1 (critically damped, stiffness 1000)
    private float scaleX = 1f, velScaleX = 0f;
    private float scaleY = 1f, velScaleY = 0f;
    private float dragVelNorm = 0f;    // velocity / 10f for squash/stretch
    private boolean pressed = false;
    private long lastFrameMs = 0L;

    private int accentColor = Color.rgb(0, 145, 255);
    private boolean lightSurface = false;
    private OnChangeListener listener;
    private VelocityTracker velocityTracker;

    public LiquidSliderView(Context ctx) {
        super(ctx);
        setLayerType(LAYER_TYPE_HARDWARE, null);
        thumbGlassRimPaint.setStyle(Paint.Style.STROKE);
        thumbChromaPaint.setStyle(Paint.Style.STROKE);
    }

    public void setMax(int max) {
        this.max = Math.max(1, max);
    }

    public int getProgress() {
        return Math.round(value * max);
    }

    public void setProgress(int progress) {
        if (pressed) return;
        float clamped = Math.max(0f, Math.min(1f, progress / (float) max));
        this.value = clamped;
        if (Math.abs(displayValue - value) > 0.001f) {
            lastFrameMs = SystemClock.uptimeMillis();
            postInvalidateOnAnimation();
        }
    }

    public void setStyle(int accent, boolean light) {
        this.accentColor = accent;
        this.lightSurface = light;
        invalidate();
    }

    public void setOnChangeListener(OnChangeListener l) {
        this.listener = l;
    }

    @Override
    protected void onMeasure(int widthMeasureSpec, int heightMeasureSpec) {
        float dp = getResources().getDisplayMetrics().density;
        int w = MeasureSpec.getSize(widthMeasureSpec);
        setMeasuredDimension(w, Math.round(44 * dp));
    }

    @Override
    public boolean onTouchEvent(MotionEvent ev) {
        float dp = getResources().getDisplayMetrics().density;
        float horizPad = 20 * dp;
        float trackW = Math.max(1f, getWidth() - horizPad * 2f);

        switch (ev.getActionMasked()) {
            case MotionEvent.ACTION_DOWN:
                getParent().requestDisallowInterceptTouchEvent(true);
                pressed = true;
                velocityTracker = VelocityTracker.obtain();
                velocityTracker.addMovement(ev);
                if (listener != null) listener.onStartTracking();
                updateFromTouchX(ev.getX(), horizPad, trackW);
                lastFrameMs = SystemClock.uptimeMillis();
                postInvalidateOnAnimation();
                return true;

            case MotionEvent.ACTION_MOVE:
                if (velocityTracker != null) {
                    velocityTracker.addMovement(ev);
                    velocityTracker.computeCurrentVelocity(1000);
                    float vx = velocityTracker.getXVelocity() / Math.max(1f, trackW);
                    dragVelNorm = dragVelNorm * 0.65f + (vx / 10f) * 0.35f;
                }
                updateFromTouchX(ev.getX(), horizPad, trackW);
                postInvalidateOnAnimation();
                return true;

            case MotionEvent.ACTION_UP:
            case MotionEvent.ACTION_CANCEL:
                pressed = false;
                dragVelNorm = 0f;
                if (velocityTracker != null) {
                    velocityTracker.recycle();
                    velocityTracker = null;
                }
                if (listener != null) listener.onStopTracking(getProgress());
                lastFrameMs = SystemClock.uptimeMillis();
                postInvalidateOnAnimation();
                return true;
        }
        return super.onTouchEvent(ev);
    }

    private void updateFromTouchX(float x, float horizPad, float trackW) {
        float next = Math.max(0f, Math.min(1f, (x - horizPad) / trackW));
        int prevInt = getProgress();
        this.value = next;
        int nextInt = getProgress();
        if (nextInt != prevInt && listener != null) {
            listener.onChanged(nextInt, true);
        }
    }

    private boolean stepSprings() {
        long now = SystemClock.uptimeMillis();
        float dt = lastFrameMs == 0L ? 0.016f : Math.min(0.033f, Math.max(0.004f, (now - lastFrameMs) / 1000f));
        lastFrameMs = now;

        // Value smoothing (stiffness 1000 critically damped)
        displayValue += (value - displayValue) * Math.min(1f, dt * 28f);

        // Press progress (critically damped, waits for value within 2.5% on release like DampedDragAnimation.kt)
        float targetPress = (pressed || Math.abs(displayValue - value) > 0.025f) ? 1f : 0f;
        pressProgress += (targetPress - pressProgress) * Math.min(1f, dt * 22f);

        // Underdamped spring scale (Kyant0: initialScale 1.0 -> pressedScale 1.5, damping 0.6 / 0.7, stiffness 250)
        float targetScale = 1f + 0.42f * targetPress;
        float k = 250f;
        float cX = 2f * 0.60f * (float) Math.sqrt(k);
        float cY = 2f * 0.70f * (float) Math.sqrt(k);

        velScaleX += ((targetScale - scaleX) * k - velScaleX * cX) * dt;
        scaleX += velScaleX * dt;
        velScaleY += ((targetScale - scaleY) * k - velScaleY * cY) * dt;
        scaleY += velScaleY * dt;

        dragVelNorm *= (1f - Math.min(1f, dt * 14f));

        return Math.abs(displayValue - value) > 0.001f
                || Math.abs(pressProgress - targetPress) > 0.002f
                || Math.abs(scaleX - targetScale) > 0.002f
                || Math.abs(velScaleX) > 0.005f
                || Math.abs(scaleY - targetScale) > 0.002f;
    }

    @Override
    protected void onDraw(Canvas canvas) {
        boolean animating = stepSprings();
        float dp = getResources().getDisplayMetrics().density;
        int w = getWidth();
        int h = getHeight();
        if (w <= 0 || h <= 0) return;

        float cy = h * 0.5f;
        float horizPad = 20 * dp;
        float trackH = 6 * dp;
        float trackL = horizPad;
        float trackR = w - horizPad;
        float trackW = Math.max(1f, trackR - trackL);

        // 1. Background capsule track
        trackRect.set(trackL, cy - trackH * 0.5f, trackR, cy + trackH * 0.5f);
        trackBgPaint.setColor(lightSurface ? Color.argb(52, 120, 120, 120) : Color.argb(92, 120, 120, 128));
        canvas.drawRoundRect(trackRect, trackH * 0.5f, trackH * 0.5f, trackBgPaint);

        // 2. Active accent track
        float fillW = trackW * displayValue;
        if (fillW > 1f) {
            fillRect.set(trackL, cy - trackH * 0.5f, trackL + fillW, cy + trackH * 0.5f);
            trackFillPaint.setColor(accentColor);
            canvas.drawRoundRect(fillRect, trackH * 0.5f, trackH * 0.5f, trackFillPaint);
        }

        // 3. Capsule thumb (40dp x 24dp base, Kyant0 1.5x press magnification + velocity squash/stretch)
        float baseThumbW = 40 * dp;
        float baseThumbH = 24 * dp;
        float vClampX = Math.max(-0.2f, Math.min(0.2f, dragVelNorm * 0.75f));
        float vClampY = Math.max(-0.2f, Math.min(0.2f, dragVelNorm * 0.25f));
        float sx = scaleX / (1f - vClampX);
        float sy = scaleY * (1f - vClampY);
        float tw = baseThumbW * sx;
        float th = baseThumbH * sy;
        float thumbCx = trackL + fillW;
        thumbRect.set(thumbCx - tw * 0.5f, cy - th * 0.5f, thumbCx + tw * 0.5f, cy + th * 0.5f);
        float rad = th * 0.5f;

        // Soft drop shadow (Kyant0 Shadow 4dp @ 5-14% alpha)
        thumbShadowPaint.setColor(Color.argb((int) (26 + 22 * pressProgress), 0, 0, 0));
        canvas.drawRoundRect(thumbRect.left, thumbRect.top + 2 * dp, thumbRect.right, thumbRect.bottom + 3 * dp, rad, rad, thumbShadowPaint);

        // Refractive Liquid Glass body when pressed: frosted optical lens + magnified track + specular crescent
        if (pressProgress > 0.02f) {
            canvas.save();
            thumbClip.reset();
            thumbClip.addRoundRect(thumbRect, rad, rad, Path.Direction.CW);
            canvas.clipPath(thumbClip);

            // Soft frosted glass base tint so it looks like thick curved glass, NOT empty clear space
            int glassBaseAlpha = Math.round(pressProgress * (lightSurface ? 115f : 72f));
            thumbFillPaint.setColor(lightSurface
                    ? Color.argb(glassBaseAlpha, 245, 248, 255)
                    : Color.argb(glassBaseAlpha, 225, 236, 252));
            canvas.drawRoundRect(thumbRect, rad, rad, thumbFillPaint);

            // Magnified & vertically-expanded refracted track visible through the glass droplet
            float lensTrackH = trackH * (1f + 1.15f * pressProgress);
            trackBgPaint.setColor(lightSurface ? Color.argb(90, 110, 115, 125) : Color.argb(135, 140, 148, 165));
            canvas.drawRect(thumbRect.left, cy - lensTrackH * 0.5f, thumbRect.right, cy + lensTrackH * 0.5f, trackBgPaint);
            trackFillPaint.setColor(Color.argb(230, Color.red(accentColor), Color.green(accentColor), Color.blue(accentColor)));
            canvas.drawRect(thumbRect.left, cy - lensTrackH * 0.5f, thumbCx, cy + lensTrackH * 0.5f, trackFillPaint);

            // Curved optical dome sheen (bright top-left specular crescent + subtle bottom caustic)
            thumbSheenPaint.setShader(new LinearGradient(
                    thumbRect.left, thumbRect.top, thumbRect.right, thumbRect.bottom,
                    new int[]{
                            Color.argb((int) (185 * pressProgress), 255, 255, 255),
                            Color.argb((int) (40 * pressProgress), 255, 255, 255),
                            Color.argb((int) (25 * pressProgress), 10, 14, 24),
                            Color.argb((int) (115 * pressProgress), 210, 240, 255)
                    },
                    new float[]{0f, 0.36f, 0.72f, 1f}, Shader.TileMode.CLAMP));
            canvas.drawRoundRect(thumbRect, rad, rad, thumbSheenPaint);
            canvas.restore();
        }

        // Opaque white surface at rest -> blends smoothly into the frosted glass lens on press
        int surfaceAlpha = Math.round((1f - pressProgress * 0.78f) * 255f);
        thumbFillPaint.setColor(Color.argb(surfaceAlpha, 255, 255, 255));
        canvas.drawRoundRect(thumbRect, rad, rad, thumbFillPaint);

        // Spectral chromatic aberration rim + 45-degree specular highlight
        if (pressProgress > 0.02f) {
            thumbChromaPaint.setStrokeWidth(1.8f * dp);
            thumbChromaPaint.setShader(new LinearGradient(
                    thumbRect.left, thumbRect.top, thumbRect.right, thumbRect.bottom,
                    new int[]{
                            Color.argb((int) (220 * pressProgress), 125, 232, 255),
                            Color.argb((int) (235 * pressProgress), 255, 255, 255),
                            Color.argb((int) (195 * pressProgress), 255, 145, 215)
                    }, null, Shader.TileMode.CLAMP));
            canvas.drawRoundRect(thumbRect, rad, rad, thumbChromaPaint);
        } else {
            thumbGlassRimPaint.setStrokeWidth(0.8f * dp);
            thumbGlassRimPaint.setColor(Color.argb(40, 0, 0, 0));
            canvas.drawRoundRect(thumbRect, rad, rad, thumbGlassRimPaint);
        }

        if (animating) postInvalidateOnAnimation();
    }
}
