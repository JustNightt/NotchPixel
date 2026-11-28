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
 * Liquid glass slider.
 *
 * <p>A thin, precise track with a solid knob that grows slightly and turns to
 * glass only while it is held. Values update 1:1 with the finger; the knob's
 * return is a critically damped spring so an interrupted drag never snaps.</p>
 */
public final class LiquidSliderView extends View {

    public interface OnChangeListener {
        void onChanged(int value, boolean fromUser);

        void onStartTracking();

        void onStopTracking(int value);
    }

    private static final float THUMB_DP = 22f;
    private static final float TRACK_DP = 5f;
    private static final float HORIZONTAL_PAD_DP = 16f;

    private final Paint trackPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint fillPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint thumbPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint rimPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint shadowPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final RectF trackRect = new RectF();
    private final RectF fillRect = new RectF();
    private final RectF thumbRect = new RectF();
    private final Path thumbClip = new Path();

    private final Motion.Spring display = new Motion.Spring(Motion.DAMPING_CRITICAL, 0.24f);
    private final Motion.Spring press = new Motion.Spring(Motion.DAMPING_CRITICAL, 0.18f);

    private int max = 100;
    private float value = 0f;
    private boolean pressed = false;
    private long lastFrameMs = 0L;

    private int accentColor = Color.rgb(43, 228, 234);
    private boolean lightSurface = false;
    private OnChangeListener listener;
    private VelocityTracker velocityTracker;

    private LinearGradient sheen;

    public LiquidSliderView(Context ctx) {
        super(ctx);
        rimPaint.setStyle(Paint.Style.STROKE);
        setLayerType(LAYER_TYPE_HARDWARE, null);
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
        if (Math.abs(display.target - clamped) < 0.0005f) return;
        value = clamped;
        display.to(clamped);
        if (Motion.reduced()) {
            display.snapTo(clamped);
            invalidate();
        } else {
            lastFrameMs = SystemClock.uptimeMillis();
            postInvalidateOnAnimation();
        }
    }

    public void setStyle(int accent, boolean light) {
        if (accentColor == accent && lightSurface == light) return;
        accentColor = accent;
        lightSurface = light;
        invalidate();
    }

    public void setOnChangeListener(OnChangeListener l) {
        listener = l;
    }

    @Override
    protected void onMeasure(int widthMeasureSpec, int heightMeasureSpec) {
        float dp = getResources().getDisplayMetrics().density;
        int w = MeasureSpec.getSize(widthMeasureSpec);
        setMeasuredDimension(w, Math.round(40 * dp));
    }

    private float trackWidthPx() {
        float pad = HORIZONTAL_PAD_DP * getResources().getDisplayMetrics().density;
        return Math.max(1f, getWidth() - pad * 2f);
    }

    @Override
    public boolean onTouchEvent(MotionEvent ev) {
        switch (ev.getActionMasked()) {
            case MotionEvent.ACTION_DOWN:
                getParent().requestDisallowInterceptTouchEvent(true);
                pressed = true;
                press.to(1f);
                if (velocityTracker != null) velocityTracker.recycle();
                velocityTracker = VelocityTracker.obtain();
                velocityTracker.addMovement(ev);
                if (listener != null) listener.onStartTracking();
                updateFromTouch(ev.getX());
                lastFrameMs = SystemClock.uptimeMillis();
                postInvalidateOnAnimation();
                return true;

            case MotionEvent.ACTION_MOVE:
                if (velocityTracker != null) {
                    velocityTracker.addMovement(ev);
                    velocityTracker.computeCurrentVelocity(1000);
                }
                updateFromTouch(ev.getX());
                postInvalidateOnAnimation();
                return true;

            case MotionEvent.ACTION_UP:
            case MotionEvent.ACTION_CANCEL:
                pressed = false;
                press.to(0f);
                display.to(value);
                if (velocityTracker != null) {
                    velocityTracker.recycle();
                    velocityTracker = null;
                }
                if (listener != null) listener.onStopTracking(getProgress());
                lastFrameMs = SystemClock.uptimeMillis();
                postInvalidateOnAnimation();
                return true;

            default:
                return super.onTouchEvent(ev);
        }
    }

    private void updateFromTouch(float x) {
        float pad = HORIZONTAL_PAD_DP * getResources().getDisplayMetrics().density;
        float next = Math.max(0f, Math.min(1f, (x - pad) / trackWidthPx()));
        int previous = Math.round(value * max);
        value = next;
        display.snapTo(next);
        int current = Math.round(value * max);
        if (current != previous && listener != null) listener.onChanged(current, true);
    }

    @Override
    protected void onDraw(Canvas canvas) {
        float density = getResources().getDisplayMetrics().density;
        int w = getWidth();
        int h = getHeight();
        if (w <= 0 || h <= 0) return;

        long now = SystemClock.uptimeMillis();
        float dt = lastFrameMs == 0L ? 0.016f : (now - lastFrameMs) / 1000f;
        lastFrameMs = now;

        boolean animating;
        if (Motion.reduced()) {
            display.snapTo(display.target);
            press.snapTo(press.target);
            animating = false;
        } else {
            animating = display.step(dt);
            animating |= press.step(dt);
        }

        float pad = HORIZONTAL_PAD_DP * density;
        float trackH = TRACK_DP * density;
        float cy = h * 0.5f;
        float left = pad;
        float right = w - pad;
        trackRect.set(left, cy - trackH * 0.5f, right, cy + trackH * 0.5f);
        float trackR = trackH * 0.5f;

        trackPaint.setColor(lightSurface ? Color.argb(34, 26, 32, 42) : Color.argb(46, 255, 255, 255));
        canvas.drawRoundRect(trackRect, trackR, trackR, trackPaint);

        float progress = Math.max(0f, Math.min(1f, display.value));
        float fillRight = left + (right - left) * progress;
        if (fillRight - left > trackH * 0.5f) {
            fillRect.set(left, cy - trackH * 0.5f, fillRight, cy + trackH * 0.5f);
            fillPaint.setColor(accentColor);
            canvas.drawRoundRect(fillRect, trackR, trackR, fillPaint);
        }

        float diameter = THUMB_DP * density * (1f + 0.08f * press.value);
        float thumbCx = Math.max(left, Math.min(right, fillRight));
        thumbRect.set(thumbCx - diameter * 0.5f, cy - diameter * 0.5f,
                thumbCx + diameter * 0.5f, cy + diameter * 0.5f);
        float thumbR = diameter * 0.5f;

        shadowPaint.setColor(Color.argb((int) (24 + 22 * press.value), 0, 0, 0));
        canvas.drawRoundRect(thumbRect.left, thumbRect.top + 1.5f * density,
                thumbRect.right, thumbRect.bottom + 2.5f * density, thumbR, thumbR, shadowPaint);

        thumbPaint.setShader(null);
        thumbPaint.setColor(Motion.blend(Color.WHITE, Color.argb(126, 242, 248, 255), press.value));
        canvas.drawRoundRect(thumbRect, thumbR, thumbR, thumbPaint);

        if (press.value > 0.02f) {
            if (sheen == null || sheenSize != Math.round(diameter)) {
                sheenSize = Math.round(diameter);
                sheen = new LinearGradient(0f, 0f, diameter, diameter,
                        new int[]{Color.argb(190, 255, 255, 255), Color.argb(30, 255, 255, 255),
                                Color.argb(46, 206, 234, 255)},
                        new float[]{0f, 0.58f, 1f}, Shader.TileMode.CLAMP);
            }
            int save = canvas.save();
            canvas.translate(thumbRect.left, thumbRect.top);
            thumbClip.reset();
            thumbClip.addRoundRect(0f, 0f, diameter, diameter, thumbR, thumbR, Path.Direction.CW);
            canvas.clipPath(thumbClip);
            sheenPaintForPress.setAlpha((int) (255 * press.value));
            sheenPaintForPress.setShader(sheen);
            canvas.drawRoundRect(0f, 0f, diameter, diameter, thumbR, thumbR, sheenPaintForPress);
            canvas.restoreToCount(save);
        }

        rimPaint.setStrokeWidth(1f * density);
        rimPaint.setColor(lightSurface
                ? Color.argb(40, 16, 22, 30)
                : Color.argb(190 - (int) (80 * press.value), 255, 255, 255));
        canvas.drawRoundRect(thumbRect, thumbR, thumbR, rimPaint);

        if (animating || pressed) postInvalidateOnAnimation();
    }

    private final Paint sheenPaintForPress = new Paint(Paint.ANTI_ALIAS_FLAG);
    private int sheenSize = -1;
}
