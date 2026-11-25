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
    private int percent = 0;
    private boolean tempAvailable = false;
    private int tempC = 0;
    private boolean lightSurface = false;
    private float wavePhase = 0f;

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
        this.percent = Math.max(0, Math.min(100, pct));
        this.tempAvailable = hasTemp;
        this.tempC = tC;
        this.wavePhase += 0.45f;
        invalidate();
    }

    public void setLightSurface(boolean light) {
        this.lightSurface = light;
        invalidate();
    }

    @Override
    protected void onMeasure(int widthMeasureSpec, int heightMeasureSpec) {
        float dp = getResources().getDisplayMetrics().density;
        int w = MeasureSpec.getSize(widthMeasureSpec);
        setMeasuredDimension(w, Math.round(112 * dp));
    }

    @Override
    protected void onDraw(Canvas canvas) {
        super.onDraw(canvas);
        float dp = getResources().getDisplayMetrics().density;
        int w = getWidth();
        int h = getHeight();
        float rad = 18 * dp;
        cardRect.set(dp, dp, w - dp, h - dp);

        cardPaint.setColor(lightSurface ? Color.argb(235, 240, 244, 250) : Color.argb(225, 22, 26, 34));
        canvas.drawRoundRect(cardRect, rad, rad, cardPaint);

        // Liquid fill level
        canvas.save();
        clipPath.reset();
        clipPath.addRoundRect(cardRect, rad, rad, Path.Direction.CW);
        canvas.clipPath(clipPath);

        float fillFrac = Math.max(0.06f, percent / 100f);
        float baseY = cardRect.bottom - cardRect.height() * fillFrac;
        float amp = 4.5f * dp;

        wavePath.reset();
        wavePath.moveTo(cardRect.left, cardRect.bottom);
        wavePath.lineTo(cardRect.left, baseY);
        int steps = 16;
        for (int i = 0; i <= steps; i++) {
            float x = cardRect.left + (cardRect.width() * i / (float) steps);
            float y = baseY + (float) Math.sin(wavePhase + i * 0.6f) * amp;
            wavePath.lineTo(x, y);
        }
        wavePath.lineTo(cardRect.right, cardRect.bottom);
        wavePath.close();

        liquidPaint.setColor(accentColor);
        liquidPaint.setAlpha(72);
        canvas.drawPath(wavePath, liquidPaint);

        liquidCrestPaint.setColor(accentColor);
        liquidCrestPaint.setAlpha(120);
        liquidCrestPaint.setStyle(Paint.Style.STROKE);
        liquidCrestPaint.setStrokeWidth(1.5f * dp);
        canvas.drawLine(cardRect.left, baseY, cardRect.right, baseY, liquidCrestPaint);

        canvas.restore();

        borderPaint.setColor(lightSurface ? Color.argb(60, 20, 30, 45) : Color.argb(65, 255, 255, 255));
        canvas.drawRoundRect(cardRect, rad, rad, borderPaint);

        titlePaint.setTextSize(12 * dp);
        titlePaint.setColor(lightSurface ? Color.rgb(75, 85, 100) : Color.rgb(160, 170, 185));
        canvas.drawText(metricTitle, 14 * dp, 24 * dp, titlePaint);

        valuePaint.setTextSize(26 * dp);
        valuePaint.setColor(lightSurface ? Color.rgb(16, 20, 26) : Color.rgb(248, 250, 252));
        canvas.drawText(percent + "%", 14 * dp, 64 * dp, valuePaint);

        tempPaint.setTextSize(12 * dp);
        tempPaint.setColor(tempAvailable ? accentColor : (lightSurface ? Color.rgb(110, 120, 132) : Color.rgb(125, 134, 148)));
        canvas.drawText(tempAvailable ? (tempC + " C") : "Temp N/A", 14 * dp, h - 14 * dp, tempPaint);
    }
}
