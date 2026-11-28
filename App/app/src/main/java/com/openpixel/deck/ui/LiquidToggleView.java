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
import android.view.HapticFeedbackConstants;
import android.view.MotionEvent;
import android.view.VelocityTracker;
import android.view.View;

/**
 * Liquid glass switch.
 *
 * <p>The thumb tracks the finger 1:1, carries the release velocity into the
 * spring that settles it, and only turns into glass while it is being touched —
 * at rest it stays a solid, legible white knob.</p>
 */
public final class LiquidToggleView extends View {

    public interface OnCheckedChangeListener {
        void onCheckedChanged(boolean checked);
    }

    private static final float TRACK_W_DP = 50f;
    private static final float TRACK_H_DP = 30f;
    private static final float THUMB_DP = 24f;
    private static final float TRAVEL_DP = 20f;

    private final Paint trackPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint thumbPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint rimPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint shadowPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final RectF trackRect = new RectF();
    private final RectF thumbRect = new RectF();
    private final Path thumbClip = new Path();

    private final Motion.Spring slide = new Motion.Spring(Motion.DAMPING_CRITICAL, 0.30f);
    private final Motion.Spring press = new Motion.Spring(Motion.DAMPING_CRITICAL, 0.20f);

    private boolean checked = false;
    private boolean pressed = false;
    private boolean didDrag = false;
    private float downX = 0f;
    private float downFraction = 0f;
    private long lastFrameMs = 0L;

    private boolean lightSurface = false;
    private int accentColor = Color.rgb(48, 209, 88);
    private OnCheckedChangeListener listener;
    private VelocityTracker velocityTracker;

    public LiquidToggleView(Context ctx) {
        super(ctx);
        rimPaint.setStyle(Paint.Style.STROKE);
        setLayerType(LAYER_TYPE_HARDWARE, null);
    }

    public boolean isChecked() {
        return checked;
    }

    public void setChecked(boolean isChecked) {
        this.checked = isChecked;
        slide.to(isChecked ? 1f : 0f);
        if (Motion.reduced()) slide.snapTo(isChecked ? 1f : 0f);
        lastFrameMs = SystemClock.uptimeMillis();
        postInvalidateOnAnimation();
    }

    public void setLightSurface(boolean light) {
        if (lightSurface == light) return;
        lightSurface = light;
        invalidate();
    }

    public void setAccentColor(int accent) {
        if (accentColor == accent) return;
        accentColor = accent;
        invalidate();
    }

    public void setOnCheckedChangeListener(OnCheckedChangeListener l) {
        listener = l;
    }

    @Override
    protected void onMeasure(int widthMeasureSpec, int heightMeasureSpec) {
        float dp = getResources().getDisplayMetrics().density;
        setMeasuredDimension(
                resolveSize(Math.round((TRACK_W_DP + 8f) * dp), widthMeasureSpec),
                resolveSize(Math.round(38 * dp), heightMeasureSpec));
    }

    @Override
    public boolean onTouchEvent(MotionEvent ev) {
        float density = getResources().getDisplayMetrics().density;
        float travel = TRAVEL_DP * density;

        switch (ev.getActionMasked()) {
            case MotionEvent.ACTION_DOWN:
                getParent().requestDisallowInterceptTouchEvent(true);
                pressed = true;
                didDrag = false;
                downX = ev.getX();
                downFraction = slide.value;
                press.to(1f);
                if (velocityTracker != null) velocityTracker.recycle();
                velocityTracker = VelocityTracker.obtain();
                velocityTracker.addMovement(ev);
                lastFrameMs = SystemClock.uptimeMillis();
                postInvalidateOnAnimation();
                return true;

            case MotionEvent.ACTION_MOVE: {
                float dx = ev.getX() - downX;
                if (Math.abs(dx) > 4f * density) didDrag = true;
                if (velocityTracker != null) velocityTracker.addMovement(ev);
                float next = Math.max(0f, Math.min(1f, downFraction + dx / travel));
                if (next != slide.value) {
                    slide.snapTo(next);
                    postInvalidateOnAnimation();
                }
                return true;
            }

            case MotionEvent.ACTION_UP: {
                pressed = false;
                press.to(0f);
                float vx = 0f;
                if (velocityTracker != null) {
                    velocityTracker.computeCurrentVelocity(1000, 6000f);
                    vx = velocityTracker.getXVelocity();
                    velocityTracker.recycle();
                    velocityTracker = null;
                }
                boolean next;
                if (didDrag && Math.abs(vx) > 240f) {
                    // A decisive flick wins over position.
                    next = vx > 0f;
                } else {
                    next = didDrag ? slide.value >= 0.5f : !checked;
                }
                slide.velocity = vx / travel;
                boolean changed = next != checked;
                checked = next;
                slide.to(checked ? 1f : 0f);
                if (changed) {
                    performHapticFeedback(HapticFeedbackConstants.VIRTUAL_KEY);
                    if (listener != null) listener.onCheckedChanged(checked);
                }
                lastFrameMs = SystemClock.uptimeMillis();
                postInvalidateOnAnimation();
                return true;
            }

            case MotionEvent.ACTION_CANCEL:
                pressed = false;
                press.to(0f);
                if (velocityTracker != null) {
                    velocityTracker.recycle();
                    velocityTracker = null;
                }
                slide.to(checked ? 1f : 0f);
                lastFrameMs = SystemClock.uptimeMillis();
                postInvalidateOnAnimation();
                return true;

            default:
                return super.onTouchEvent(ev);
        }
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
            slide.snapTo(slide.target);
            press.snapTo(press.target);
            animating = false;
        } else {
            animating = slide.step(dt);
            animating |= press.step(dt);
        }

        float trackW = TRACK_W_DP * density;
        float trackH = TRACK_H_DP * density;
        float trackL = (w - trackW) * 0.5f;
        float trackT = (h - trackH) * 0.5f;
        trackRect.set(trackL, trackT, trackL + trackW, trackT + trackH);
        float trackR = trackH * 0.5f;

        int offTrack = lightSurface ? Color.argb(46, 22, 26, 34) : Color.argb(74, 236, 240, 248);
        int onTrack = accentColor;
        trackPaint.setColor(Motion.blend(offTrack, onTrack, Math.max(0f, Math.min(1f, slide.value))));
        canvas.drawRoundRect(trackRect, trackR, trackR, trackPaint);

        // Rim keeps the capsule readable on both light and dark glass.
        rimPaint.setStrokeWidth(1f * density);
        rimPaint.setColor(lightSurface ? Color.argb(34, 16, 22, 30) : Color.argb(26, 0, 0, 0));
        canvas.drawRoundRect(trackRect, trackR, trackR, rimPaint);

        float pressAmount = press.value;
        float fraction = Math.max(0f, Math.min(1f, slide.value));
        float thumbDiameter = THUMB_DP * density * (1f + 0.06f * pressAmount);
        float travel = (trackW - thumbDiameter) - 4f * density;
        float thumbCx = trackL + 2f * density + thumbDiameter * 0.5f + travel * fraction;
        float thumbCy = h * 0.5f;
        thumbRect.set(thumbCx - thumbDiameter * 0.5f, thumbCy - thumbDiameter * 0.5f,
                thumbCx + thumbDiameter * 0.5f, thumbCy + thumbDiameter * 0.5f);
        float thumbR = thumbDiameter * 0.5f;

        shadowPaint.setColor(Color.argb((int) (22 + 20 * pressAmount), 0, 0, 0));
        canvas.drawRoundRect(thumbRect.left, thumbRect.top + 1f * density,
                thumbRect.right, thumbRect.bottom + 2f * density, thumbR, thumbR, shadowPaint);

        // Glass only while touched: a white body that dissolves into a lens.
        int body = Motion.blend(Color.WHITE, Color.argb(112, 240, 246, 255), pressAmount);
        thumbPaint.setColor(body);
        canvas.drawRoundRect(thumbRect, thumbR, thumbR, thumbPaint);

        if (pressAmount > 0.02f) {
            int save = canvas.save();
            thumbClip.reset();
            thumbClip.addRoundRect(thumbRect, thumbR, thumbR, Path.Direction.CW);
            canvas.clipPath(thumbClip);
            thumbPaint.setShader(new LinearGradient(
                    thumbRect.left, thumbRect.top, thumbRect.right, thumbRect.bottom,
                    new int[]{
                            Color.argb((int) (150 * pressAmount), 255, 255, 255),
                            Color.argb((int) (26 * pressAmount), 255, 255, 255),
                            Color.argb((int) (52 * pressAmount), 210, 236, 255)
                    },
                    new float[]{0f, 0.55f, 1f}, Shader.TileMode.CLAMP));
            canvas.drawRoundRect(thumbRect, thumbR, thumbR, thumbPaint);
            thumbPaint.setShader(null);
            canvas.restoreToCount(save);
        }

        rimPaint.setStrokeWidth(1f * density);
        rimPaint.setColor(lightSurface
                ? Color.argb(38, 16, 22, 30)
                : Color.argb(180 - (int) (90 * pressAmount), 255, 255, 255));
        canvas.drawRoundRect(thumbRect, thumbR, thumbR, rimPaint);

        if (animating || pressed) postInvalidateOnAnimation();
    }
}
