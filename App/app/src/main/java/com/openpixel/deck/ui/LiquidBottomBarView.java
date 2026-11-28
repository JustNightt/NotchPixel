package com.openpixel.deck.ui;

import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.LinearGradient;
import android.graphics.Paint;
import android.graphics.RectF;
import android.graphics.Shader;
import android.os.SystemClock;
import android.view.HapticFeedbackConstants;
import android.view.MotionEvent;
import android.view.VelocityTracker;
import android.view.View;

/**
 * Liquid glass tab bar.
 *
 * <p>The capsule tracks the finger 1:1, telegraphs the tab it is heading for by
 * lighting that label, and only commits the tab change on release. Release
 * velocity is projected forward so a flick lands on the tab it was thrown at,
 * and the spring inherits that velocity instead of restarting from zero.</p>
 */
public final class LiquidBottomBarView extends View {

    public interface OnTabSelectedListener {
        void onTabSelected(int index);
    }

    private static final String[] ICONS = {
            Icons.MUSIC, Icons.TRACKPAD, Icons.GRID, Icons.LAYERS, Icons.MONITOR
    };
    private static final String[] LABELS = {"Now", "Pad", "Apps", "Modes", "PC"};
    private static final int TAB_COUNT = ICONS.length;

    private final Paint dockPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint dockRimPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint specularPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint pillPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint pillRimPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint labelPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final RectF dockRect = new RectF();
    private final RectF pillRect = new RectF();

    private final Motion.Spring pill =
            new Motion.Spring(Motion.DAMPING_MOMENTUM, Motion.RESPONSE_UI);
    private final Motion.Spring press =
            new Motion.Spring(Motion.DAMPING_CRITICAL, Motion.RESPONSE_FAST);

    private OnTabSelectedListener listener;
    private int selectedIndex = 0;
    private int previewIndex = 0;
    private boolean dragging = false;
    private boolean hasDragged = false;
    private long lastFrameMs = 0L;

    private boolean glassSurface = true;
    private boolean lightSurface = false;
    private int accentColor = Color.rgb(43, 228, 234);

    private VelocityTracker velocityTracker;
    private float downX = 0f;
    private float downPill = 0f;

    private int shaderW = -1;
    private int shaderH = -1;
    private int shaderAccent = 0;
    private boolean shaderLight = false;

    public LiquidBottomBarView(Context ctx) {
        super(ctx);
        dockRimPaint.setStyle(Paint.Style.STROKE);
        pillRimPaint.setStyle(Paint.Style.STROKE);
        specularPaint.setStyle(Paint.Style.STROKE);
        labelPaint.setTypeface(Type.MEDIUM);
        labelPaint.setTextAlign(Paint.Align.CENTER);
        setLayerType(LAYER_TYPE_HARDWARE, null);
    }

    public void setOnTabSelectedListener(OnTabSelectedListener l) {
        this.listener = l;
    }

    public void setStyle(boolean glass, boolean light, int accent) {
        if (glass == glassSurface && light == lightSurface && accent == accentColor) return;
        this.glassSurface = glass;
        this.lightSurface = light;
        this.accentColor = accent;
        shaderW = -1;
        invalidate();
    }

    public void setSelectedTab(int idx, boolean animate) {
        int clamped = Math.max(0, Math.min(TAB_COUNT - 1, idx));
        boolean changed = clamped != selectedIndex || clamped != Math.round(pill.target);
        selectedIndex = clamped;
        previewIndex = clamped;
        if (!animate || Motion.reduced()) {
            pill.snapTo(clamped);
            lastFrameMs = 0L;
            invalidate();
            return;
        }
        pill.to(clamped);
        lastFrameMs = SystemClock.uptimeMillis();
        postInvalidateOnAnimation();
        if (changed) invalidate();
    }

    @Override
    protected void onMeasure(int widthMeasureSpec, int heightMeasureSpec) {
        float dp = getResources().getDisplayMetrics().density;
        int w = MeasureSpec.getSize(widthMeasureSpec);
        setMeasuredDimension(w, Math.round(68 * dp));
    }

    private float slotWidth() {
        float pad = 6f * getResources().getDisplayMetrics().density;
        return Math.max(1f, (getWidth() - pad * 2f) / TAB_COUNT);
    }

    private float indexAt(float x) {
        float pad = 6f * getResources().getDisplayMetrics().density;
        float slot = slotWidth();
        return Math.max(0f, Math.min(TAB_COUNT - 1f, (x - pad - slot * 0.5f) / slot));
    }

    @Override
    public boolean onTouchEvent(MotionEvent ev) {
        switch (ev.getActionMasked()) {
            case MotionEvent.ACTION_DOWN:
                getParent().requestDisallowInterceptTouchEvent(true);
                dragging = true;
                hasDragged = false;
                downX = ev.getX();
                downPill = pill.value;
                previewIndex = Math.max(0, Math.min(TAB_COUNT - 1, Math.round(indexAt(downX))));
                if (velocityTracker != null) velocityTracker.recycle();
                velocityTracker = VelocityTracker.obtain();
                velocityTracker.addMovement(ev);
                press.to(1f);
                lastFrameMs = SystemClock.uptimeMillis();
                postInvalidateOnAnimation();
                return true;

            case MotionEvent.ACTION_MOVE: {
                if (velocityTracker != null) {
                    velocityTracker.addMovement(ev);
                    velocityTracker.computeCurrentVelocity(1000);
                }
                float target = indexAt(ev.getX());
                // 8dp of hysteresis before a tap turns into a 1:1 capsule drag.
                float density = getResources().getDisplayMetrics().density;
                if (hasDragged || Math.abs(ev.getX() - downX) > 8f * density) {
                    hasDragged = true;
                    pill.snapTo(target);
                }
                int hover = hasDragged
                        ? Math.max(0, Math.min(TAB_COUNT - 1, Math.round(pill.value)))
                        : Math.max(0, Math.min(TAB_COUNT - 1, Math.round(target)));
                if (hover != previewIndex) {
                    previewIndex = hover;
                    performHapticFeedback(HapticFeedbackConstants.CLOCK_TICK);
                }
                postInvalidateOnAnimation();
                return true;
            }

            case MotionEvent.ACTION_UP: {
                dragging = false;
                press.to(0f);
                float vx = 0f;
                if (velocityTracker != null) {
                    velocityTracker.computeCurrentVelocity(1000, 6000f);
                    vx = velocityTracker.getXVelocity();
                    velocityTracker.recycle();
                    velocityTracker = null;
                }
                int target;
                float velocityInTabs = 0f;
                if (!hasDragged) {
                    // Direct tap on a tab slot: jump straight to the tapped tab.
                    target = Math.max(0, Math.min(TAB_COUNT - 1, Math.round(indexAt(ev.getX()))));
                } else {
                    float slot = Math.max(1f, slotWidth());
                    velocityInTabs = vx / slot;
                    float projected = pill.value + Motion.project(vx, 0.998f) / slot;
                    target = Math.max(0, Math.min(TAB_COUNT - 1, Math.round(projected)));
                    if (Math.abs(velocityInTabs) > 0.6f && Math.round(pill.value) == target) {
                        target = Math.max(0, Math.min(TAB_COUNT - 1,
                                Math.round(pill.value) + (velocityInTabs > 0f ? 1 : -1)));
                    }
                }
                hasDragged = false;
                boolean changed = target != selectedIndex;
                selectedIndex = target;
                previewIndex = target;
                pill.velocity = velocityInTabs;
                pill.to(target);
                lastFrameMs = SystemClock.uptimeMillis();
                if (changed) {
                    performHapticFeedback(HapticFeedbackConstants.VIRTUAL_KEY);
                    if (listener != null) listener.onTabSelected(target);
                }
                postInvalidateOnAnimation();
                return true;
            }

            case MotionEvent.ACTION_CANCEL:
                dragging = false;
                hasDragged = false;
                press.to(0f);
                if (velocityTracker != null) {
                    velocityTracker.recycle();
                    velocityTracker = null;
                }
                pill.to(selectedIndex);
                lastFrameMs = SystemClock.uptimeMillis();
                postInvalidateOnAnimation();
                return true;

            default:
                return super.onTouchEvent(ev);
        }
    }

    private void rebuildShaders(int w, int h) {
        shaderW = w;
        shaderH = h;
        shaderAccent = accentColor;
        shaderLight = lightSurface;

        int r = Color.red(accentColor);
        int g = Color.green(accentColor);
        int b = Color.blue(accentColor);
        float top = 2f * getResources().getDisplayMetrics().density;
        float bottom = h - top;

        dockPaint.setShader(new LinearGradient(0, top, 0, bottom,
                new int[]{
                        lightSurface ? Color.argb(238, 255, 255, 255) : Color.argb(196, 19, 22, 29),
                        lightSurface ? Color.argb(224, 238, 242, 248) : Color.argb(224, 10, 12, 17)
                },
                new float[]{0f, 1f}, Shader.TileMode.CLAMP));

        dockRimPaint.setStrokeWidth(1f * getResources().getDisplayMetrics().density);
        dockRimPaint.setShader(new LinearGradient(0, top, 0, bottom,
                new int[]{
                        lightSurface ? Color.argb(190, 255, 255, 255) : Color.argb(58, 255, 255, 255),
                        Color.TRANSPARENT,
                        Color.argb(lightSurface ? 40 : 34, r, g, b)
                },
                new float[]{0f, 0.62f, 1f}, Shader.TileMode.CLAMP));

        specularPaint.setStrokeWidth(1.1f * getResources().getDisplayMetrics().density);
        specularPaint.setShader(new LinearGradient(0, top, 0, top + Math.max(10f * getResources().getDisplayMetrics().density, h * 0.28f),
                new int[]{
                        Color.argb(lightSurface ? 140 : 78, 255, 255, 255),
                        Color.TRANSPARENT
                },
                new float[]{0f, 1f}, Shader.TileMode.CLAMP));

        // The capsule sits in a fixed vertical band, so its gradients never change per frame.
        float pad = 6f * getResources().getDisplayMetrics().density;
        pillPaint.setShader(new LinearGradient(0, pad, 0, h - pad,
                new int[]{
                        Color.argb(lightSurface ? 54 : 62, r, g, b),
                        Color.argb(lightSurface ? 30 : 34, r, g, b)
                },
                new float[]{0f, 1f}, Shader.TileMode.CLAMP));
        pillRimPaint.setShader(new LinearGradient(0, pad, 0, h - pad,
                new int[]{
                        Color.argb(lightSurface ? 170 : 120, 255, 255, 255),
                        Color.argb(lightSurface ? 40 : 56, r, g, b)
                },
                new float[]{0f, 1f}, Shader.TileMode.CLAMP));
    }

    @Override
    protected void onDraw(Canvas canvas) {
        super.onDraw(canvas);
        int w = getWidth();
        int h = getHeight();
        if (w <= 0 || h <= 0) return;

        float density = getResources().getDisplayMetrics().density;

        long now = SystemClock.uptimeMillis();
        float dt = lastFrameMs == 0L ? 0.016f : (now - lastFrameMs) / 1000f;
        lastFrameMs = now;

        boolean animating;
        if (Motion.reduced()) {
            pill.snapTo(pill.target);
            press.snapTo(press.target);
            animating = false;
        } else {
            animating = pill.step(dt);
            animating |= press.step(dt);
        }

        if (w != shaderW || h != shaderH || accentColor != shaderAccent || lightSurface != shaderLight) {
            rebuildShaders(w, h);
        }

        float inset = 2f * density;
        float dockRadius = (h - inset * 2f) * 0.5f;
        dockRect.set(inset, inset, w - inset, h - inset);
        canvas.drawRoundRect(dockRect, dockRadius, dockRadius, dockPaint);
        canvas.drawRoundRect(dockRect, dockRadius, dockRadius, dockRimPaint);

        int save = canvas.save();
        canvas.clipRect(0, 0, w, h * 0.4f);
        canvas.drawRoundRect(dockRect, dockRadius, dockRadius, specularPaint);
        canvas.restoreToCount(save);

        float pad = 6f * density;
        float slot = slotWidth();
        float pillH = h - pad * 2f;
        float pillW = slot - 4f * density;
        float centerX = pad + (pill.value + 0.5f) * slot;
        float centerY = h * 0.5f;
        float left = Math.max(pad + 2f * density,
                Math.min(w - pad - pillW - 2f * density, centerX - pillW * 0.5f));
        pillRect.set(left, centerY - pillH * 0.5f, left + pillW, centerY + pillH * 0.5f);
        float pillRadius = pillH * 0.5f;

        // Press lifts the capsule with a transform instead of rebuilding its shader.
        float pillScale = 1f + 0.012f * press.value;
        int pillSave = canvas.save();
        canvas.scale(pillScale, pillScale, centerX, centerY);
        canvas.drawRoundRect(pillRect, pillRadius, pillRadius, pillPaint);
        pillRimPaint.setStrokeWidth(1f * density);
        canvas.drawRoundRect(pillRect, pillRadius, pillRadius, pillRimPaint);
        canvas.restoreToCount(pillSave);

        int mutedInk = lightSurface ? Color.argb(190, 62, 68, 80) : Color.argb(186, 205, 213, 226);
        int activeInk = lightSurface ? Color.rgb(14, 16, 20) : Color.WHITE;
        float iconPx = 21f * density;

        labelPaint.setTextSize(9.5f * getResources().getDisplayMetrics().scaledDensity);
        labelPaint.setLetterSpacing(0.04f);

        for (int i = 0; i < TAB_COUNT; i++) {
            float tx = pad + (i + 0.5f) * slot;
            float proximity = Math.max(0f, 1f - Math.abs(pill.value - i));
            int iconColor = Motion.blend(mutedInk, accentColor, proximity);
            int textColor = Motion.blend(mutedInk, activeInk, proximity);
            float iconCy = centerY - 6.5f * density;
            Icons.draw(canvas, ICONS[i], tx, iconCy, iconPx, iconColor, 1f + 0.15f * proximity);
            labelPaint.setColor(textColor);
            canvas.drawText(LABELS[i], tx, centerY + 14.5f * density, labelPaint);
        }

        if (animating || dragging) postInvalidateOnAnimation();
    }
}
