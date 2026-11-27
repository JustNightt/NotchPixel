package com.openpixel.deck.ui;

import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Paint;
import android.graphics.Path;
import android.graphics.RectF;
import android.view.View;

/**
 * Liquid-fill telemetry stat card for CPU, GPU, and RAM (shows real temp only when reported).
 */
public final class LiquidStatCardView extends View {
    private final String metricTitle;
    private final int accentColor;
    private int targetPercent = 0;
    private float displayPercent = 0f;
    private boolean tempAvailable = false;
    private int tempC = 0;
    private boolean lightSurface = false;
    private float wavePhase = 0f;
    private long lastFrameMs = 0L;

    private final Paint cardPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint borderPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint liquidPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint liquidCrestPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint titlePaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint valuePaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint tempPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final RectF cardRect = new RectF();
    private final Path clipPath = new Path();
    private final Path wavePath = new Path();
    private final Path crestPath = new Path();

    public LiquidStatCardView(Context ctx, String title, int accentColor) {
        super(ctx);
        this.metricTitle = title;
        this.accentColor = accentColor;
        borderPaint.setStyle(Paint.Style.STROKE);
        borderPaint.setStrokeWidth(ctx.getResources().getDisplayMetrics().density * 1f);
        titlePaint.setFakeBoldText(true);
        valuePaint.setFakeBoldText(true);
    }

    public void updateMetric(int pct, boolean hasTemp, int tC) {
        int clamped = Math.max(0, Math.min(100, pct));
        boolean changed = (clamped != this.targetPercent || hasTemp != this.tempAvailable || tC != this.tempC);
        this.targetPercent = clamped;
        this.tempAvailable = hasTemp;
        this.tempC = tC;
        if (changed || Math.abs(targetPercent - displayPercent) > 0.25f) {
            postInvalidateOnAnimation();
        }
    }

    public void setLightSurface(boolean light) {
        this.lightSurface = light;
        invalidate();
    }

    @Override
    protected void onMeasure(int widthMeasureSpec, int heightMeasureSpec) {
        float dp = getResources().getDisplayMetrics().density;
        int w = MeasureSpec.getSize(widthMeasureSpec);
        int hMode = MeasureSpec.getMode(heightMeasureSpec);
        int h = (hMode == MeasureSpec.EXACTLY) ? MeasureSpec.getSize(heightMeasureSpec) : Math.round(112 * dp);
        setMeasuredDimension(w, h);
    }

    @Override
    protected void onDraw(Canvas canvas) {
        super.onDraw(canvas);
        float dp = getResources().getDisplayMetrics().density;
        int w = getWidth();
        int h = getHeight();
        float rad = 18 * dp;
        cardRect.set(dp, dp, w - dp, h - dp);

        long now = android.os.SystemClock.uptimeMillis();
        float dt = lastFrameMs > 0L ? Math.min(0.05f, Math.max(0.004f, (now - lastFrameMs) / 1000f)) : 0.016f;
        lastFrameMs = now;

        float diff = targetPercent - displayPercent;
        boolean stillEasing = Math.abs(diff) > 0.15f;
        if (stillEasing) {
            displayPercent += diff * (1f - (float) Math.exp(-dt * 7.5f));
            wavePhase += dt * 2.4f;
        } else {
            displayPercent = targetPercent;
        }

        cardPaint.setColor(lightSurface ? Color.argb(235, 240, 244, 250) : Color.argb(225, 22, 26, 34));
        canvas.drawRoundRect(cardRect, rad, rad, cardPaint);

        // Smooth interpolated liquid fill level
        canvas.save();
        clipPath.reset();
        clipPath.addRoundRect(cardRect, rad, rad, Path.Direction.CW);
        canvas.clipPath(clipPath);

        float fillFrac = Math.max(0.06f, displayPercent / 100f);
        float baseY = cardRect.bottom - cardRect.height() * fillFrac;
        float amp = 3.8f * dp;

        wavePath.reset();
        crestPath.reset();
        wavePath.moveTo(cardRect.left, cardRect.bottom);
        int steps = 16;
        float prevX = cardRect.left;
        float prevY = baseY + (float) Math.sin(wavePhase) * amp;
        wavePath.lineTo(prevX, prevY);
        crestPath.moveTo(prevX, prevY);
        for (int i = 1; i <= steps; i++) {
            float x = cardRect.left + (cardRect.width() * i / (float) steps);
            float y = baseY + (float) Math.sin(wavePhase + i * 0.55f) * amp;
            float midX = (prevX + x) * 0.5f;
            float midY = (prevY + y) * 0.5f;
            wavePath.quadTo(prevX, prevY, midX, midY);
            crestPath.quadTo(prevX, prevY, midX, midY);
            prevX = x;
            prevY = y;
        }
        wavePath.lineTo(cardRect.right, prevY);
        crestPath.lineTo(cardRect.right, prevY);
        wavePath.lineTo(cardRect.right, cardRect.bottom);
        wavePath.close();

        liquidPaint.setColor(accentColor);
        liquidPaint.setAlpha(74);
        canvas.drawPath(wavePath, liquidPaint);

        liquidCrestPaint.setColor(accentColor);
        liquidCrestPaint.setAlpha(150);
        liquidCrestPaint.setStyle(Paint.Style.STROKE);
        liquidCrestPaint.setStrokeWidth(1.6f * dp);
        canvas.drawPath(crestPath, liquidCrestPaint);

        canvas.restore();

        borderPaint.setColor(lightSurface ? Color.argb(60, 20, 30, 45) : Color.argb(65, 255, 255, 255));
        canvas.drawRoundRect(cardRect, rad, rad, borderPaint);

        titlePaint.setTextSize(12 * dp);
        titlePaint.setColor(lightSurface ? Color.rgb(75, 85, 100) : Color.rgb(160, 170, 185));
        canvas.drawText(metricTitle, 14 * dp, 24 * dp, titlePaint);

        valuePaint.setTextSize(26 * dp);
        valuePaint.setColor(lightSurface ? Color.rgb(16, 20, 26) : Color.rgb(248, 250, 252));
        canvas.drawText(Math.round(displayPercent) + "%", 14 * dp, 64 * dp, valuePaint);

        tempPaint.setTextSize(12 * dp);
        tempPaint.setColor(tempAvailable ? accentColor : (lightSurface ? Color.rgb(110, 120, 132) : Color.rgb(125, 134, 148)));
        canvas.drawText(tempAvailable ? (tempC + " C") : "Temp N/A", 14 * dp, h - 14 * dp, tempPaint);

        if (stillEasing) {
            postInvalidateOnAnimation();
        }
    }
}
