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
 * Liquid Glass Toggle ported from Kyant0/AndroidLiquidGlass (LiquidToggle.kt & DampedDragAnimation.kt):
 * - 64dp x 28dp capsule track (lerp between off track and #30D158 green / accent)
 * - 40dp x 24dp capsule thumb with 20dp horizontal travel (2dp padding)
 * - Tap-to-toggle + drag-to-scrub with >= 0.5f snap threshold
 * - 1.5x press magnification, velocity squash/stretch (velocity / 50f), and
 *   white-to-clear refractive Liquid Glass morph with chromatic RGB rim.
 */
public final class LiquidToggleView extends View {
    public interface OnCheckedChangeListener {
        void onCheckedChanged(boolean checked);
    }

    private final Paint trackPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint shadowPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint thumbPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint sheenPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint chromaRimPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final RectF trackRect = new RectF();
    private final RectF thumbRect = new RectF();
    private final Path thumbClip = new Path();

    private boolean checked = false;
    private float targetFraction = 0f;
    private float fraction = 0f;
    private float pressProgress = 0f;
    private float scaleX = 1f, velScaleX = 0f;
    private float scaleY = 1f, velScaleY = 0f;
    private float dragVelNorm = 0f;
    private boolean pressed = false;
    private boolean didDrag = false;
    private float downX = 0f;
    private float downFraction = 0f;
    private long lastFrameMs = 0L;

    private boolean lightSurface = false;
    private OnCheckedChangeListener listener;
    private VelocityTracker velocityTracker;

    public LiquidToggleView(Context ctx) {
        super(ctx);
        setLayerType(LAYER_TYPE_HARDWARE, null);
        chromaRimPaint.setStyle(Paint.Style.STROKE);
    }

    public boolean isChecked() {
        return checked;
    }

    public void setChecked(boolean isChecked) {
        this.checked = isChecked;
        this.targetFraction = isChecked ? 1f : 0f;
        lastFrameMs = SystemClock.uptimeMillis();
        postInvalidateOnAnimation();
    }

    public void setLightSurface(boolean light) {
        this.lightSurface = light;
        invalidate();
    }

    public void setOnCheckedChangeListener(OnCheckedChangeListener l) {
        this.listener = l;
    }

    @Override
    protected void onMeasure(int widthMeasureSpec, int heightMeasureSpec) {
        float dp = getResources().getDisplayMetrics().density;
        setMeasuredDimension(Math.round(80 * dp), Math.round(40 * dp));
    }

    @Override
    public boolean onTouchEvent(MotionEvent ev) {
        float dp = getResources().getDisplayMetrics().density;
        float dragWidth = 20 * dp;

        switch (ev.getActionMasked()) {
            case MotionEvent.ACTION_DOWN:
                getParent().requestDisallowInterceptTouchEvent(true);
                pressed = true;
                didDrag = false;
                downX = ev.getX();
                downFraction = fraction;
                velocityTracker = VelocityTracker.obtain();
                velocityTracker.addMovement(ev);
                lastFrameMs = SystemClock.uptimeMillis();
                postInvalidateOnAnimation();
                return true;

            case MotionEvent.ACTION_MOVE:
                float dx = ev.getX() - downX;
                if (Math.abs(dx) > 4 * dp) didDrag = true;
                if (velocityTracker != null) {
                    velocityTracker.addMovement(ev);
                    velocityTracker.computeCurrentVelocity(1000);
                    float vx = velocityTracker.getXVelocity() / Math.max(1f, dragWidth);
                    dragVelNorm = dragVelNorm * 0.65f + (vx / 50f) * 0.35f;
                }
                targetFraction = Math.max(0f, Math.min(1f, downFraction + dx / dragWidth));
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
                boolean next = didDrag ? (targetFraction >= 0.5f) : !checked;
                boolean changed = (next != checked);
                checked = next;
                targetFraction = checked ? 1f : 0f;
                if (changed && listener != null) {
                    listener.onCheckedChanged(checked);
                }
                lastFrameMs = SystemClock.uptimeMillis();
                postInvalidateOnAnimation();
                return true;
        }
        return super.onTouchEvent(ev);
    }

    private boolean stepSprings() {
        long now = SystemClock.uptimeMillis();
        float dt = lastFrameMs == 0L ? 0.016f : Math.min(0.033f, Math.max(0.004f, (now - lastFrameMs) / 1000f));
        lastFrameMs = now;

        fraction += (targetFraction - fraction) * Math.min(1f, dt * 26f);
        float targetPress = (pressed || Math.abs(fraction - targetFraction) > 0.025f) ? 1f : 0f;
        pressProgress += (targetPress - pressProgress) * Math.min(1f, dt * 22f);

        float targetScale = 1f + 0.44f * targetPress;
        float k = 250f;
        float cX = 2f * 0.60f * (float) Math.sqrt(k);
        float cY = 2f * 0.70f * (float) Math.sqrt(k);

        velScaleX += ((targetScale - scaleX) * k - velScaleX * cX) * dt;
        scaleX += velScaleX * dt;
        velScaleY += ((targetScale - scaleY) * k - velScaleY * cY) * dt;
        scaleY += velScaleY * dt;

        dragVelNorm *= (1f - Math.min(1f, dt * 14f));

        return Math.abs(fraction - targetFraction) > 0.001f
                || Math.abs(pressProgress - targetPress) > 0.002f
                || Math.abs(scaleX - targetScale) > 0.002f
                || Math.abs(velScaleX) > 0.005f;
    }

    private static int lerpColor(int a, int b, float t) {
        int aa = Color.alpha(a), ar = Color.red(a), ag = Color.green(a), ab = Color.blue(a);
        int ba = Color.alpha(b), br = Color.red(b), bg = Color.green(b), bb = Color.blue(b);
        return Color.argb(
                Math.round(aa + (ba - aa) * t),
                Math.round(ar + (br - ar) * t),
                Math.round(ag + (bg - ag) * t),
                Math.round(ab + (bb - ab) * t));
    }

    @Override
    protected void onDraw(Canvas canvas) {
        boolean animating = stepSprings();
        float dp = getResources().getDisplayMetrics().density;
        float cx = getWidth() * 0.5f;
        float cy = getHeight() * 0.5f;

        // 64dp x 28dp capsule track (Kyant0 LiquidToggle.kt)
        float trackW = 64 * dp;
        float trackH = 28 * dp;
        float trackL = cx - trackW * 0.5f;
        float trackT = cy - trackH * 0.5f;
        trackRect.set(trackL, trackT, trackL + trackW, trackT + trackH);

        int offTrack = lightSurface ? Color.argb(52, 120, 120, 120) : Color.argb(92, 120, 120, 128);
        int onTrack = lightSurface ? Color.rgb(52, 199, 89) : Color.rgb(48, 209, 88);
        int curTrackCol = lerpColor(offTrack, onTrack, fraction);
        trackPaint.setColor(curTrackCol);
        canvas.drawRoundRect(trackRect, trackH * 0.5f, trackH * 0.5f, trackPaint);

        // 40dp x 24dp capsule thumb with 20dp travel (2dp horizontal padding)
        float baseThumbW = 40 * dp;
        float baseThumbH = 24 * dp;
        float vClampX = Math.max(-0.2f, Math.min(0.2f, dragVelNorm * 0.75f));
        float vClampY = Math.max(-0.2f, Math.min(0.2f, dragVelNorm * 0.25f));
        float tw = baseThumbW * (scaleX / (1f - vClampX));
        float th = baseThumbH * (scaleY * (1f - vClampY));

        float thumbCx = trackL + 2 * dp + baseThumbW * 0.5f + 20 * dp * fraction;
        thumbRect.set(thumbCx - tw * 0.5f, cy - th * 0.5f, thumbCx + tw * 0.5f, cy + th * 0.5f);
        float rad = th * 0.5f;

        shadowPaint.setColor(Color.argb((int) (24 + 20 * pressProgress), 0, 0, 0));
        canvas.drawRoundRect(thumbRect.left, thumbRect.top + 1.5f * dp, thumbRect.right, thumbRect.bottom + 2.5f * dp, rad, rad, shadowPaint);

        if (pressProgress > 0.02f) {
            canvas.save();
            thumbClip.reset();
            thumbClip.addRoundRect(thumbRect, rad, rad, Path.Direction.CW);
            canvas.clipPath(thumbClip);
            thumbPaint.setColor(Color.argb(Math.round(pressProgress * 84f), 230, 240, 255));
            canvas.drawRoundRect(thumbRect, rad, rad, thumbPaint);
            sheenPaint.setShader(new LinearGradient(
                    thumbRect.left, thumbRect.top, thumbRect.right, thumbRect.bottom,
                    new int[]{
                            Color.argb((int) (185 * pressProgress), 255, 255, 255),
                            Color.argb((int) (35 * pressProgress), 255, 255, 255),
                            Color.argb((int) (110 * pressProgress), 205, 240, 255)
                    },
                    new float[]{0f, 0.52f, 1f}, Shader.TileMode.CLAMP));
            canvas.drawRoundRect(thumbRect, rad, rad, sheenPaint);
            canvas.restore();
        }

        int whiteAlpha = Math.round((1f - pressProgress * 0.76f) * 255f);
        thumbPaint.setColor(Color.argb(whiteAlpha, 255, 255, 255));
        canvas.drawRoundRect(thumbRect, rad, rad, thumbPaint);

        if (pressProgress > 0.02f) {
            chromaRimPaint.setStrokeWidth(1.7f * dp);
            chromaRimPaint.setShader(new LinearGradient(
                    thumbRect.left, thumbRect.top, thumbRect.right, thumbRect.bottom,
                    new int[]{
                            Color.argb((int) (215 * pressProgress), 120, 230, 255),
                            Color.argb((int) (235 * pressProgress), 255, 255, 255),
                            Color.argb((int) (195 * pressProgress), 255, 145, 215)
                    }, null, Shader.TileMode.CLAMP));
            canvas.drawRoundRect(thumbRect, rad, rad, chromaRimPaint);
        }

        if (animating) postInvalidateOnAnimation();
    }
}
