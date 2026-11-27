package com.openpixel.deck.ui;

import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.LinearGradient;
import android.graphics.Paint;
import android.graphics.RectF;
import android.graphics.Shader;
import android.os.SystemClock;
import android.view.MotionEvent;
import android.view.View;

/**
 * Interactive Liquid Glass Bottom Tab Slider modeled on Kyant0/AndroidLiquidGlass BottomTabs.
 * Supports both tap-to-slide and horizontal finger scrubbing with spring physics,
 * velocity stretch, and refractive glass capsule highlight.
 */
public final class LiquidBottomBarView extends View {
    public interface OnTabSelectedListener {
        void onTabSelected(int index);
    }

    private final String[] icons = {"♪", "▢", "⊞", "≡", "▭"};
    private final String[] labels = {"Now", "Pad", "Apps", "Modes", "PC"};
    private final int tabCount = 5;

    private final Paint dockBgPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint dockBorderPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint pillFillPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint pillRimPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint iconPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint labelPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final RectF dockRect = new RectF();
    private final RectF pillRect = new RectF();

    private OnTabSelectedListener listener;
    private int selectedIndex = 0;
    private float pillPos = 0f;       // continuous 0..(tabCount-1)
    private float pillVel = 0f;
    private float dragLift = 0f;      // 0..1 magnifying lift while dragging
    private boolean dragging = false;
    private long lastFrameMs = 0L;

    private boolean glassSurface = true;
    private boolean lightSurface = false;
    private int accentColor = Color.rgb(43, 228, 234);

    public LiquidBottomBarView(Context ctx) {
        super(ctx);
        dockBorderPaint.setStyle(Paint.Style.STROKE);
        pillRimPaint.setStyle(Paint.Style.STROKE);
        iconPaint.setTextAlign(Paint.Align.CENTER);
        labelPaint.setTextAlign(Paint.Align.CENTER);
        labelPaint.setFakeBoldText(true);
    }

    public void setOnTabSelectedListener(OnTabSelectedListener l) {
        this.listener = l;
    }

    public void setStyle(boolean glass, boolean light, int accent) {
        this.glassSurface = glass;
        this.lightSurface = light;
        this.accentColor = accent;
        invalidate();
    }

    public void setSelectedTab(int idx, boolean animate) {
        int clamped = Math.max(0, Math.min(tabCount - 1, idx));
        this.selectedIndex = clamped;
        if (!animate) {
            this.pillPos = clamped;
            this.pillVel = 0f;
        }
        lastFrameMs = SystemClock.uptimeMillis();
        postInvalidateOnAnimation();
    }

    @Override
    protected void onMeasure(int widthMeasureSpec, int heightMeasureSpec) {
        float dp = getResources().getDisplayMetrics().density;
        int w = MeasureSpec.getSize(widthMeasureSpec);
        setMeasuredDimension(w, Math.round(62 * dp));
    }

    @Override
    public boolean onTouchEvent(MotionEvent ev) {
        float dp = getResources().getDisplayMetrics().density;
        float pad = 6 * dp;
        float innerW = Math.max(1f, getWidth() - pad * 2f);
        float slotW = innerW / tabCount;
        float rawIdx = Math.max(0f, Math.min(tabCount - 1f, (ev.getX() - pad - slotW * 0.5f) / slotW));

        switch (ev.getActionMasked()) {
            case MotionEvent.ACTION_DOWN:
                dragging = true;
                lastFrameMs = SystemClock.uptimeMillis();
                postInvalidateOnAnimation();
                return true;

            case MotionEvent.ACTION_MOVE:
                dragging = true;
                pillPos = rawIdx;
                pillVel = 0f;
                int hoverIdx = Math.max(0, Math.min(tabCount - 1, Math.round(rawIdx)));
                if (hoverIdx != selectedIndex) {
                    selectedIndex = hoverIdx;
                    if (listener != null) listener.onTabSelected(selectedIndex);
                }
                invalidate();
                return true;

            case MotionEvent.ACTION_UP:
            case MotionEvent.ACTION_CANCEL:
                dragging = false;
                int target = Math.max(0, Math.min(tabCount - 1, Math.round(rawIdx)));
                boolean changed = target != selectedIndex;
                selectedIndex = target;
                if (changed && listener != null) {
                    listener.onTabSelected(selectedIndex);
                }
                lastFrameMs = SystemClock.uptimeMillis();
                postInvalidateOnAnimation();
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
        if (w <= 0 || h <= 0) return;

        long now = SystemClock.uptimeMillis();
        float dt = lastFrameMs > 0L ? Math.min(0.05f, Math.max(0.004f, (now - lastFrameMs) / 1000f)) : 0.016f;
        lastFrameMs = now;

        // Critically-damped spring for smooth sliding liquid glass capsule
        boolean anim = false;
        if (!dragging) {
            float target = (float) selectedIndex;
            float force = (target - pillPos) * 260f;
            float damp = -pillVel * 26f;
            pillVel += (force + damp) * dt;
            pillPos += pillVel * dt;
            if (Math.abs(target - pillPos) < 0.003f && Math.abs(pillVel) < 0.01f) {
                pillPos = target;
                pillVel = 0f;
            } else {
                anim = true;
            }
        }

        float liftTarget = dragging ? 1f : 0f;
        dragLift += (liftTarget - dragLift) * (1f - (float) Math.exp(-dt * 18f));
        if (Math.abs(liftTarget - dragLift) > 0.01f) anim = true;

        float dockRad = h * 0.5f;
        dockRect.set(dp, dp, w - dp, h - dp);

        // Outer dock shell (smoked liquid glass — zero harsh white glow)
        dockBgPaint.setColor(lightSurface
                ? Color.argb(215, 246, 248, 252)
                : (glassSurface ? Color.argb(180, 14, 16, 22) : Color.rgb(18, 20, 26)));
        canvas.drawRoundRect(dockRect, dockRad, dockRad, dockBgPaint);

        dockBorderPaint.setStrokeWidth(1.1f * dp);
        dockBorderPaint.setColor(lightSurface ? Color.argb(40, 12, 16, 24) : Color.argb(42, 255, 255, 255));
        canvas.drawRoundRect(dockRect, dockRad, dockRad, dockBorderPaint);

        // Sliding Liquid Glass Capsule Pill with velocity stretch + drag magnification
        float pad = 5.5f * dp;
        float innerW = w - pad * 2f;
        float slotW = innerW / tabCount;
        float stretch = Math.min(0.18f, Math.abs(pillVel) * 0.018f) + dragLift * 0.07f;
        float pillW = slotW * (1f + stretch);
        float pillH = (h - pad * 2f) * (1f + dragLift * 0.05f);
        float centerX = pad + (pillPos + 0.5f) * slotW;
        float centerY = h * 0.5f;
        float pLeft = Math.max(pad, Math.min(w - pad - pillW, centerX - pillW * 0.5f));
        pillRect.set(pLeft, centerY - pillH * 0.5f, pLeft + pillW, centerY + pillH * 0.5f);
        float pillRad = pillH * 0.5f;

        int ar = Color.red(accentColor), ag = Color.green(accentColor), ab = Color.blue(accentColor);
        pillFillPaint.setShader(new LinearGradient(
                pillRect.left, pillRect.top, pillRect.right, pillRect.bottom,
                new int[]{
                        Color.argb(lightSurface ? 48 : 78, ar, ag, ab),
                        Color.argb(lightSurface ? 28 : 42, ar, ag, ab)
                },
                null, Shader.TileMode.CLAMP));
        canvas.drawRoundRect(pillRect, pillRad, pillRad, pillFillPaint);

        // Crisp optical refractive glass rim around the sliding capsule
        pillRimPaint.setStrokeWidth((1.2f + dragLift * 0.5f) * dp);
        pillRimPaint.setShader(new LinearGradient(
                pillRect.left, pillRect.top, pillRect.left, pillRect.bottom,
                new int[]{
                        Color.argb(lightSurface ? 140 : 120, 255, 255, 255),
                        Color.argb(85, ar, ag, ab)
                },
                null, Shader.TileMode.CLAMP));
        canvas.drawRoundRect(pillRect, pillRad, pillRad, pillRimPaint);

        // Tab icons + labels (smoothly illuminated as the liquid capsule glides over them)
        int mutedCol = lightSurface ? Color.rgb(92, 98, 110) : Color.argb(175, 215, 222, 232);
        iconPaint.setTextSize(15.5f * dp);
        labelPaint.setTextSize(10.5f * dp);
        for (int i = 0; i < tabCount; i++) {
            float tx = pad + (i + 0.5f) * slotW;
            float dist = Math.abs(pillPos - i);
            float activeT = Math.max(0f, 1f - dist);
            int col = blendColor(mutedCol, lightSurface ? Color.rgb(16, 18, 22) : Color.WHITE, activeT);
            int iconCol = blendColor(mutedCol, accentColor, activeT);
            float scale = 1f + activeT * 0.08f;

            canvas.save();
            canvas.scale(scale, scale, tx, centerY);
            iconPaint.setColor(iconCol);
            labelPaint.setColor(col);
            canvas.drawText(icons[i], tx, centerY - 2.5f * dp, iconPaint);
            canvas.drawText(labels[i], tx, centerY + 13.5f * dp, labelPaint);
            canvas.restore();
        }

        if (anim || dragging) {
            postInvalidateOnAnimation();
        }
    }

    private static int blendColor(int c1, int c2, float t) {
        float inv = 1f - t;
        int a = Math.round(Color.alpha(c1) * inv + Color.alpha(c2) * t);
        int r = Math.round(Color.red(c1) * inv + Color.red(c2) * t);
        int g = Math.round(Color.green(c1) * inv + Color.green(c2) * t);
        int b = Math.round(Color.blue(c1) * inv + Color.blue(c2) * t);
        return Color.argb(a, r, g, b);
    }
}
