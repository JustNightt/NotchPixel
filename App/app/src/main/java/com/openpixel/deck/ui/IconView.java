package com.openpixel.deck.ui;

import android.content.Context;
import android.view.View;

/**
 * A single vector icon occupying a fixed square. Used for artwork placeholders,
 * list-row leading icons and inline status marks.
 */
public final class IconView extends View {

    private String icon;
    private int color;
    private float sizeDp;
    private float weight = 1f;

    public IconView(Context ctx, String icon, float sizeDp, int color) {
        super(ctx);
        this.icon = icon;
        this.sizeDp = sizeDp;
        this.color = color;
        setImportantForAccessibility(IMPORTANT_FOR_ACCESSIBILITY_NO);
    }

    public void setIcon(String icon) {
        if (this.icon != null && this.icon.equals(icon)) return;
        this.icon = icon;
        invalidate();
    }

    public void setColor(int color) {
        if (this.color == color) return;
        this.color = color;
        invalidate();
    }

    public void setWeight(float weight) {
        this.weight = weight;
        invalidate();
    }

    @Override
    protected void onMeasure(int widthMeasureSpec, int heightMeasureSpec) {
        int size = Math.round(sizeDp * getResources().getDisplayMetrics().density);
        setMeasuredDimension(
                resolveSize(size, widthMeasureSpec),
                resolveSize(size, heightMeasureSpec));
    }

    @Override
    protected void onDraw(android.graphics.Canvas canvas) {
        Icons.draw(canvas, icon, getWidth() * 0.5f, getHeight() * 0.5f,
                Math.min(getWidth(), getHeight()), color, weight);
    }
}
