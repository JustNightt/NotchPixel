package com.openpixel.deck.ui;

import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Paint;
import android.graphics.Path;
import android.graphics.RectF;
import android.os.Handler;
import android.os.Looper;
import android.view.HapticFeedbackConstants;
import android.view.MotionEvent;
import android.view.View;

/**
 * Press-and-hold confirmation for irreversible power actions.
 *
 * <p>Holding fills a thin accent bar inside the button; releasing early cancels
 * without side effects. The label stays visible the whole time so the action is
 * never ambiguous.</p>
 */
public final class HoldPowerButton extends View {

    public interface OnHoldConfirmedListener {
        void onConfirmed();
    }

    private static final long HOLD_MS = 900L;

    private final String label;
    private final int fillColor;
    private final OnHoldConfirmedListener listener;

    private final Paint bgPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint rimPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint progressPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint textPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint trackPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final RectF rect = new RectF();
    private final RectF trackRect = new RectF();
    private final RectF progressRect = new RectF();
    private final Path clipPath = new Path();
    private final Handler handler = new Handler(Looper.getMainLooper());

    private String icon = Icons.POWER;
    private final String holdLabel;
    private boolean lightSurface = false;
    private boolean holding = false;
    private boolean fired = false;
    private long downMs = 0L;
    private float progress = 0f;

    private final Runnable ticker = new Runnable() {
        @Override
        public void run() {
            if (!holding) return;
            long elapsed = System.currentTimeMillis() - downMs;
            progress = Math.min(1f, elapsed / (float) HOLD_MS);
            invalidate();
            if (progress >= 1f && !fired) {
                fired = true;
                holding = false;
                performHapticFeedback(HapticFeedbackConstants.LONG_PRESS);
                if (listener != null) listener.onConfirmed();
                postDelayed(() -> {
                    progress = 0f;
                    invalidate();
                }, 260);
                return;
            }
            handler.postDelayed(this, 16);
        }
    };

    public HoldPowerButton(Context ctx, String label, int fillColor, OnHoldConfirmedListener listener) {
        super(ctx);
        this.label = label;
        this.fillColor = fillColor;
        this.listener = listener;
        rimPaint.setStyle(Paint.Style.STROKE);
        rimPaint.setStrokeWidth(ctx.getResources().getDisplayMetrics().density);
        textPaint.setTypeface(Type.MEDIUM);
        textPaint.setTextAlign(Paint.Align.LEFT);
        this.holdLabel = "Hold to " + label.toLowerCase(java.util.Locale.US);
    }

    public void setIcon(String icon) {
        this.icon = icon;
        invalidate();
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
        setMeasuredDimension(w, Math.round(52 * dp));
    }

    @Override
    public boolean onTouchEvent(MotionEvent event) {
        switch (event.getActionMasked()) {
            case MotionEvent.ACTION_DOWN:
                holding = true;
                fired = false;
                downMs = System.currentTimeMillis();
                progress = 0f;
                getParent().requestDisallowInterceptTouchEvent(true);
                performHapticFeedback(HapticFeedbackConstants.VIRTUAL_KEY);
                handler.post(ticker);
                invalidate();
                return true;
            case MotionEvent.ACTION_UP:
            case MotionEvent.ACTION_CANCEL:
                holding = false;
                handler.removeCallbacks(ticker);
                progress = 0f;
                invalidate();
                return true;
            default:
                return super.onTouchEvent(event);
        }
    }

    @Override
    protected void onDraw(Canvas canvas) {
        float density = getResources().getDisplayMetrics().density;
        int w = getWidth();
        int h = getHeight();
        if (w <= 0 || h <= 0) return;

        rect.set(density, density, w - density, h - density);
        float radius = 16f * density;

        bgPaint.setColor(lightSurface ? Color.argb(236, 255, 255, 255) : Color.argb(214, 26, 30, 38));
        canvas.drawRoundRect(rect, radius, radius, bgPaint);

        rimPaint.setColor(holding
                ? Motion.blend(fillColor, Color.TRANSPARENT, 0.4f)
                : (lightSurface ? Color.argb(56, 18, 24, 32) : Color.argb(38, 255, 255, 255)));
        canvas.drawRoundRect(rect, radius, radius, rimPaint);

        float pad = 12f * density;
        float iconPx = 17f * density;
        int ink = lightSurface ? Color.rgb(18, 22, 28) : Color.rgb(246, 248, 251);
        Icons.draw(canvas, icon, pad + iconPx * 0.5f, h * 0.5f, iconPx,
                holding ? fillColor : (lightSurface ? Color.argb(210, 40, 48, 60) : Color.argb(210, 226, 232, 242)));

        textPaint.setTextSize(12f * getResources().getDisplayMetrics().scaledDensity);
        textPaint.setLetterSpacing(0.01f);
        textPaint.setColor(ink);
        float textX = pad + iconPx + 8f * density;
        String text = holding ? holdLabel : label;
        float maxW = Math.max(10f, w - textX - pad);
        float size = 12f * getResources().getDisplayMetrics().scaledDensity;
        textPaint.setTextSize(size);
        while (size > 9f * getResources().getDisplayMetrics().scaledDensity
                && textPaint.measureText(text) > maxW) {
            size -= 0.4f * getResources().getDisplayMetrics().scaledDensity;
            textPaint.setTextSize(size);
        }
        float baseline = h * 0.5f - (textPaint.ascent() + textPaint.descent()) * 0.5f;
        canvas.drawText(text, textX, baseline, textPaint);

        // Progress sits on the bottom edge so the label never moves while held.
        float trackH = 2.5f * density;
        trackRect.set(pad, h - 7f * density, w - pad, h - 7f * density + trackH);
        trackPaint.setColor(lightSurface ? Color.argb(22, 18, 24, 32) : Color.argb(22, 255, 255, 255));
        canvas.drawRoundRect(trackRect, trackH * 0.5f, trackH * 0.5f, trackPaint);

        if (progress > 0.001f) {
            int save = canvas.save();
            clipPath.reset();
            clipPath.addRoundRect(rect, radius, radius, Path.Direction.CW);
            canvas.clipPath(clipPath);
            progressRect.set(trackRect.left, trackRect.top,
                    trackRect.left + trackRect.width() * progress, trackRect.bottom);
            progressPaint.setColor(fillColor);
            canvas.drawRoundRect(progressRect, trackH * 0.5f, trackH * 0.5f, progressPaint);
            canvas.restoreToCount(save);
        }
    }

    @Override
    protected void onDetachedFromWindow() {
        holding = false;
        handler.removeCallbacksAndMessages(null);
        super.onDetachedFromWindow();
    }
}
