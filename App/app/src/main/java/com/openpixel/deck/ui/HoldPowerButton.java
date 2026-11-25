package com.openpixel.deck.ui;

import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Paint;
import android.graphics.Path;
import android.graphics.RectF;
import android.os.Handler;
import android.os.Looper;
import android.view.MotionEvent;
import android.view.View;

/**
 * Press-and-hold safety button (900ms hold) for Sleep / Restart / Shut down.
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
    private final Paint sweepPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint borderPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint textPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final RectF rect = new RectF();
    private final Path clipPath = new Path();
    private final Handler handler = new Handler(Looper.getMainLooper());

    private boolean holding = false;
    private boolean fired = false;
    private long downMs = 0L;
    private float progress = 0f;

    private boolean lightSurface = false;

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
                if (listener != null) listener.onConfirmed();
                postDelayed(() -> {
                    progress = 0f;
                    invalidate();
                }, 250);
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
        borderPaint.setStyle(Paint.Style.STROKE);
        borderPaint.setStrokeWidth(ctx.getResources().getDisplayMetrics().density * 1f);
        textPaint.setTextAlign(Paint.Align.CENTER);
        textPaint.setFakeBoldText(true);
    }

    public void setLightSurface(boolean light) {
        this.lightSurface = light;
        invalidate();
    }

    @Override
    protected void onMeasure(int widthMeasureSpec, int heightMeasureSpec) {
        float dp = getResources().getDisplayMetrics().density;
        int w = MeasureSpec.getSize(widthMeasureSpec);
        setMeasuredDimension(w, Math.round(48 * dp));
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
        }
        return super.onTouchEvent(event);
    }

    @Override
    protected void onDraw(Canvas canvas) {
        super.onDraw(canvas);
        float dp = getResources().getDisplayMetrics().density;
        int w = getWidth();
        int h = getHeight();
        rect.set(dp, dp, w - dp, h - dp);
        float rad = 16 * dp;

        bgPaint.setColor(lightSurface ? Color.argb(230, 236, 240, 246) : Color.argb(220, 28, 32, 40));
        canvas.drawRoundRect(rect, rad, rad, bgPaint);

        if (progress > 0f) {
            canvas.save();
            clipPath.reset();
            clipPath.addRoundRect(rect, rad, rad, Path.Direction.CW);
            canvas.clipPath(clipPath);
            sweepPaint.setColor(fillColor);
            sweepPaint.setAlpha(185);
            canvas.drawRect(rect.left, rect.top, rect.left + rect.width() * progress, rect.bottom, sweepPaint);
            canvas.restore();
        }

        borderPaint.setColor(holding ? fillColor : (lightSurface ? Color.argb(70, 30, 40, 55) : Color.argb(70, 255, 255, 255)));
        canvas.drawRoundRect(rect, rad, rad, borderPaint);

        textPaint.setTextSize(13 * dp);
        textPaint.setColor(lightSurface ? Color.rgb(18, 22, 28) : Color.rgb(245, 247, 250));
        Paint.FontMetrics fm = textPaint.getFontMetrics();
        float cy = h / 2f - (fm.ascent + fm.descent) / 2f;
        canvas.drawText(holding ? ("Hold " + label + "...") : ("Hold: " + label), w / 2f, cy, textPaint);
    }
}
