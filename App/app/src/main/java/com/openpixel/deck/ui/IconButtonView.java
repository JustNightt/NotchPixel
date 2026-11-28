package com.openpixel.deck.ui;

import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Paint;
import android.graphics.RectF;
import android.os.SystemClock;
import android.text.TextPaint;
import android.view.HapticFeedbackConstants;
import android.view.MotionEvent;
import android.view.View;

/**
 * Vector-icon button with Apple-style press feedback.
 *
 * <p>The highlight lands on touch-down (never on release) and the return is a
 * critically damped spring from the current on-screen scale, so a tap that is
 * interrupted by a drag away cancels cleanly instead of finishing an animation.
 * </p>
 */
public final class IconButtonView extends View {

    public interface OnActionListener {
        void onAction();
    }

    public static final int ICON_ONLY = 0;
    public static final int ICON_LABEL_HORIZONTAL = 1;
    public static final int ICON_LABEL_VERTICAL = 2;

    private final Paint fillPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint rimPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final TextPaint labelPaint = new TextPaint(Paint.ANTI_ALIAS_FLAG);
    private final RectF rect = new RectF();
    private final Motion.Spring press =
            new Motion.Spring(Motion.DAMPING_CRITICAL, Motion.RESPONSE_FAST);

    private String icon = Icons.DOT;
    private String label = "";
    private int mode = ICON_ONLY;
    private float iconDp = 20f;
    private float padDp = 12f;
    private float gapDp = 8f;
    private float radiusDp = 16f;
    private float minHeightDp = 44f;
    private float labelSp = 12.5f;
    private int fillColor = Color.argb(38, 255, 255, 255);
    private int rimColor = Color.argb(28, 255, 255, 255);
    private int inkColor = Color.WHITE;

    private OnActionListener listener;
    private boolean pressed;
    private boolean canceled;
    private long lastFrameMs;

    public IconButtonView(Context ctx) {
        super(ctx);
        rimPaint.setStyle(Paint.Style.STROKE);
        labelPaint.setTypeface(Type.MEDIUM);
        labelPaint.setTextAlign(Paint.Align.LEFT);
        setFocusable(true);
        setClickable(true);
    }

    public void setContent(String icon, String label) {
        String next = label == null ? "" : label;
        boolean sameIcon = this.icon != null && this.icon.equals(icon);
        if (sameIcon && next.equals(this.label)) return;
        this.icon = icon;
        this.label = next;
        setContentDescription(this.label.isEmpty() ? icon : this.label);
        requestLayout();
        invalidate();
    }

    public void setIcon(String icon) {
        if (this.icon != null && this.icon.equals(icon)) return;
        this.icon = icon;
        invalidate();
    }

    public void setLabel(String label) {
        String next = label == null ? "" : label;
        if (next.equals(this.label)) return;
        this.label = next;
        requestLayout();
        invalidate();
    }

    public void setMode(int mode) {
        this.mode = mode;
        requestLayout();
        invalidate();
    }

    public void setIconSizeDp(float iconDp) {
        this.iconDp = iconDp;
        requestLayout();
        invalidate();
    }

    public void setPaddingDp(float padDp) {
        this.padDp = padDp;
        requestLayout();
        invalidate();
    }

    public void setGapDp(float gapDp) {
        this.gapDp = gapDp;
        requestLayout();
        invalidate();
    }

    public void setRadiusDp(float radiusDp) {
        this.radiusDp = radiusDp;
        invalidate();
    }

    public void setMinHeightDp(float minHeightDp) {
        this.minHeightDp = minHeightDp;
        requestLayout();
        invalidate();
    }

    public void setLabelSizeSp(float sp) {
        this.labelSp = sp;
        requestLayout();
        invalidate();
    }

    /** Fill, 1dp rim and content ink in one call (used for selected/unselected states). */
    public void setTokens(int fill, int rim, int ink) {
        if (fill == fillColor && rim == rimColor && ink == inkColor) return;
        this.fillColor = fill;
        this.rimColor = rim;
        this.inkColor = ink;
        invalidate();
    }

    public void setOnActionListener(OnActionListener l) {
        this.listener = l;
    }

    private boolean hasLabel() {
        return mode != ICON_ONLY && !label.isEmpty();
    }

    @Override
    protected void onMeasure(int widthMeasureSpec, int heightMeasureSpec) {
        float density = getResources().getDisplayMetrics().density;
        float iconPx = iconDp * density;
        float padPx = padDp * density;
        float gapPx = gapDp * density;

        float contentW;
        float contentH;
        if (!hasLabel()) {
            contentW = iconPx;
            contentH = iconPx;
        } else {
            labelPaint.setTextSize(labelSp * getResources().getDisplayMetrics().scaledDensity);
            float textW = labelPaint.measureText(label);
            float textH = labelPaint.descent() - labelPaint.ascent();
            if (mode == ICON_LABEL_VERTICAL) {
                contentW = Math.max(iconPx, textW);
                contentH = iconPx + gapPx + textH;
            } else {
                contentW = iconPx + gapPx + textW;
                contentH = Math.max(iconPx, textH);
            }
        }

        int width = Math.round(contentW + padPx * 2f);
        int height = Math.max(Math.round(contentH + padPx * 2f), Math.round(minHeightDp * density));
        setMeasuredDimension(resolveSize(width, widthMeasureSpec),
                resolveSize(height, heightMeasureSpec));
    }

    @Override
    public boolean onTouchEvent(MotionEvent ev) {
        switch (ev.getActionMasked()) {
            case MotionEvent.ACTION_DOWN:
                pressed = true;
                canceled = false;
                press.to(1f);
                lastFrameMs = SystemClock.uptimeMillis();
                performHapticFeedback(HapticFeedbackConstants.VIRTUAL_KEY);
                postInvalidateOnAnimation();
                return true;

            case MotionEvent.ACTION_MOVE: {
                float slop = 10f * getResources().getDisplayMetrics().density;
                boolean inside = ev.getX() > -slop && ev.getX() < getWidth() + slop
                        && ev.getY() > -slop && ev.getY() < getHeight() + slop;
                if (canceled && inside) {
                    canceled = false;
                    press.to(1f);
                    postInvalidateOnAnimation();
                } else if (!canceled && !inside) {
                    canceled = true;
                    press.to(0f);
                    postInvalidateOnAnimation();
                }
                return true;
            }

            case MotionEvent.ACTION_UP:
                pressed = false;
                press.to(0f);
                postInvalidateOnAnimation();
                if (!canceled) performClick();
                return true;

            case MotionEvent.ACTION_CANCEL:
                pressed = false;
                canceled = false;
                press.to(0f);
                postInvalidateOnAnimation();
                return true;

            default:
                return super.onTouchEvent(ev);
        }
    }

    @Override
    public boolean performClick() {
        super.performClick();
        if (listener != null) listener.onAction();
        return true;
    }

    @Override
    protected void onDraw(Canvas canvas) {
        int w = getWidth();
        int h = getHeight();
        if (w <= 0 || h <= 0) return;

        float density = getResources().getDisplayMetrics().density;

        long now = SystemClock.uptimeMillis();
        float dt = lastFrameMs == 0L ? 0.016f : (now - lastFrameMs) / 1000f;
        lastFrameMs = now;
        boolean animating;
        if (Motion.reduced()) {
            press.snapTo(press.target);
            animating = false;
        } else {
            animating = press.step(dt);
        }

        float stroke = Math.max(1f, density);
        float radius = Math.min(radiusDp * density, Math.min(w, h) * 0.5f);
        rect.set(stroke * 0.5f, stroke * 0.5f, w - stroke * 0.5f, h - stroke * 0.5f);

        float scale = 1f - 0.035f * press.value;
        int save = canvas.save();
        canvas.scale(scale, scale, w * 0.5f, h * 0.5f);

        fillPaint.setColor(Motion.blend(fillColor, Color.WHITE, 0.09f * press.value));
        canvas.drawRoundRect(rect, radius, radius, fillPaint);

        rimPaint.setStrokeWidth(stroke);
        rimPaint.setColor(rimColor);
        canvas.drawRoundRect(rect, radius, radius, rimPaint);

        float iconPx = iconDp * density;
        if (!hasLabel()) {
            Icons.draw(canvas, icon, w * 0.5f, h * 0.5f, iconPx, inkColor);
        } else {
            labelPaint.setColor(inkColor);
            labelPaint.setTextSize(labelSp * getResources().getDisplayMetrics().scaledDensity);
            float gapPx = gapDp * density;
            float textW = labelPaint.measureText(label);
            float textH = labelPaint.descent() - labelPaint.ascent();
            if (mode == ICON_LABEL_VERTICAL) {
                float total = iconPx + gapPx + textH;
                float top = (h - total) * 0.5f;
                Icons.draw(canvas, icon, w * 0.5f, top + iconPx * 0.5f, iconPx, inkColor);
                float baseline = top + iconPx + gapPx - labelPaint.ascent();
                labelPaint.setTextAlign(Paint.Align.CENTER);
                canvas.drawText(label, w * 0.5f, baseline, labelPaint);
                labelPaint.setTextAlign(Paint.Align.LEFT);
            } else {
                float total = iconPx + gapPx + textW;
                float left = (w - total) * 0.5f;
                Icons.draw(canvas, icon, left + iconPx * 0.5f, h * 0.5f, iconPx, inkColor);
                float baseline = (h - (labelPaint.descent() + labelPaint.ascent())) * 0.5f;
                canvas.drawText(label, left + iconPx + gapPx, baseline, labelPaint);
            }
        }

        canvas.restoreToCount(save);

        if (animating || pressed) postInvalidateOnAnimation();
    }
}
