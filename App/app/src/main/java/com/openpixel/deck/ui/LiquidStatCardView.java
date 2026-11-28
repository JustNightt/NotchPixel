package com.openpixel.deck.ui;

import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Paint;
import android.graphics.RectF;
import android.os.SystemClock;
import android.view.View;

/**
 * Telemetry card: a large tabular value, its label, an optional temperature and
 * a thin accent meter along the bottom edge.
 *
 * <p>The reading eases to its new value over a few frames and then stops drawing
 * entirely, so an idle deck costs no frames.</p>
 */
public final class LiquidStatCardView extends View {

    private final String metricTitle;
    private final int accentColor;

    private final Paint cardPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint rimPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint meterTrackPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint meterFillPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint titlePaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint valuePaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint unitPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint captionPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final RectF cardRect = new RectF();
    private final RectF meterRect = new RectF();

    private int targetPercent = 0;
    private float displayPercent = 0f;
    private boolean tempAvailable = false;
    private int tempC = 0;
    private boolean lightSurface = false;
    private long lastFrameMs = 0L;

    public LiquidStatCardView(Context ctx, String title, int accentColor) {
        super(ctx);
        this.metricTitle = title;
        this.accentColor = accentColor;
        rimPaint.setStyle(Paint.Style.STROKE);
        rimPaint.setStrokeWidth(ctx.getResources().getDisplayMetrics().density);
        titlePaint.setTypeface(Type.MEDIUM);
        titlePaint.setLetterSpacing(0.09f);
        valuePaint.setTypeface(Type.MEDIUM);
        valuePaint.setLetterSpacing(-0.03f);
        valuePaint.setFontFeatureSettings("'tnum'");
        unitPaint.setTypeface(Type.MEDIUM);
        captionPaint.setTypeface(Type.REGULAR);
    }

    public void updateMetric(int pct, boolean hasTemp, int tC) {
        int clamped = Math.max(0, Math.min(100, pct));
        boolean changed = clamped != targetPercent || hasTemp != tempAvailable || tC != tempC;
        targetPercent = clamped;
        tempAvailable = hasTemp;
        tempC = tC;
        if (!changed) return;
        if (Motion.reduced()) {
            displayPercent = clamped;
            invalidate();
        } else {
            lastFrameMs = SystemClock.uptimeMillis();
            postInvalidateOnAnimation();
        }
    }

    public void setLightSurface(boolean light) {
        if (lightSurface == light) return;
        lightSurface = light;
        invalidate();
    }

    @Override
    protected void onMeasure(int widthMeasureSpec, int heightMeasureSpec) {
        float dp = getResources().getDisplayMetrics().density;
        int w = MeasureSpec.getSize(widthMeasureSpec);
        int hMode = MeasureSpec.getMode(heightMeasureSpec);
        int h = (hMode == MeasureSpec.EXACTLY)
                ? MeasureSpec.getSize(heightMeasureSpec)
                : Math.round(96 * dp);
        setMeasuredDimension(w, h);
    }

    @Override
    protected void onDraw(Canvas canvas) {
        super.onDraw(canvas);
        float density = getResources().getDisplayMetrics().density;
        int w = getWidth();
        int h = getHeight();
        if (w <= 0 || h <= 0) return;

        float diff = targetPercent - displayPercent;
        boolean easing = Math.abs(diff) > 0.12f && !Motion.reduced();
        if (easing) {
            long now = SystemClock.uptimeMillis();
            float dt = lastFrameMs == 0L ? 0.016f : (now - lastFrameMs) / 1000f;
            lastFrameMs = now;
            displayPercent += diff * (1f - (float) Math.exp(-dt * 9f));
        } else {
            displayPercent = targetPercent;
        }

        float inset = 1f * density;
        float radius = 18f * density;
        cardRect.set(inset, inset, w - inset, h - inset);

        cardPaint.setColor(lightSurface
                ? Color.argb(238, 255, 255, 255)
                : Color.argb(206, 24, 27, 34));
        canvas.drawRoundRect(cardRect, radius, radius, cardPaint);

        rimPaint.setColor(lightSurface ? Color.argb(52, 18, 24, 32) : Color.argb(34, 255, 255, 255));
        canvas.drawRoundRect(cardRect, radius, radius, rimPaint);

        float pad = 13f * density;

        titlePaint.setTextSize(9.5f * getResources().getDisplayMetrics().scaledDensity);
        titlePaint.setColor(lightSurface ? Color.argb(190, 48, 56, 68) : Color.argb(168, 205, 214, 228));
        canvas.drawText(metricTitle, pad, 22f * density, titlePaint);

        int rounded = Math.round(displayPercent);
        valuePaint.setTextSize(25f * getResources().getDisplayMetrics().scaledDensity);
        valuePaint.setColor(lightSurface ? Color.rgb(14, 17, 22) : Color.rgb(247, 249, 252));
        String valueText = String.valueOf(rounded);
        float valueBaseline = 54f * density;
        canvas.drawText(valueText, pad, valueBaseline, valuePaint);

        unitPaint.setTextSize(11f * getResources().getDisplayMetrics().scaledDensity);
        unitPaint.setColor(lightSurface ? Color.argb(150, 48, 56, 68) : Color.argb(150, 200, 210, 224));
        canvas.drawText("%", pad + valuePaint.measureText(valueText) + 2f * density,
                valueBaseline - 1f * density, unitPaint);

        captionPaint.setTextSize(10f * getResources().getDisplayMetrics().scaledDensity);
        captionPaint.setColor(tempAvailable
                ? accentColor
                : (lightSurface ? Color.argb(120, 70, 78, 90) : Color.argb(120, 172, 182, 196)));
        String caption = tempAvailable ? (tempC + "\u00B0C") : "No sensor";
        canvas.drawText(caption, pad, h - 20f * density, captionPaint);

        float meterH = 3f * density;
        float meterLeft = pad;
        float meterRight = w - pad;
        float meterTop = h - 12f * density;
        meterRect.set(meterLeft, meterTop, meterRight, meterTop + meterH);
        meterTrackPaint.setColor(lightSurface ? Color.argb(26, 18, 24, 32) : Color.argb(28, 255, 255, 255));
        canvas.drawRoundRect(meterRect, meterH * 0.5f, meterH * 0.5f, meterTrackPaint);

        float level = Math.max(0f, Math.min(1f, displayPercent / 100f));
        if (level > 0.01f) {
            meterRect.right = meterLeft + (meterRight - meterLeft) * level;
            meterFillPaint.setColor(accentColor);
            canvas.drawRoundRect(meterRect, meterH * 0.5f, meterH * 0.5f, meterFillPaint);
        }

        if (easing) postInvalidateOnAnimation();
    }
}
