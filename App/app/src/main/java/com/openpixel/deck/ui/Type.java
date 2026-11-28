package com.openpixel.deck.ui;

import android.graphics.Typeface;
import android.util.TypedValue;
import android.widget.TextView;

/**
 * The deck's type scale.
 *
 * <p>Tracking is size specific, as in Apple's UI typography guidance: display
 * sizes pull letters together, small sizes open them up. Hierarchy comes from
 * weight + size + leading together, never from synthetic bold.</p>
 */
public final class Type {

    public static final Typeface REGULAR = Typeface.create("sans-serif", Typeface.NORMAL);
    public static final Typeface MEDIUM = Typeface.create("sans-serif-medium", Typeface.NORMAL);

    private Type() {}

    /** 24sp, tight. Screen titles and the big media line. */
    public static void display(TextView v) {
        style(v, 23f, MEDIUM, -0.022f);
    }

    /** 19sp. Section and screen headers. */
    public static void title(TextView v) {
        style(v, 18.5f, MEDIUM, -0.016f);
    }

    /** 15sp. Card headers. */
    public static void headline(TextView v) {
        style(v, 14.5f, MEDIUM, -0.008f);
    }

    /** 13.5sp. Body copy and list rows. */
    public static void body(TextView v) {
        style(v, 13.5f, REGULAR, 0.002f);
    }

    /** 11.5sp. Secondary lines and captions. */
    public static void caption(TextView v) {
        style(v, 11.5f, REGULAR, 0.012f);
    }

    /** 10sp, open tracking. All-caps metadata such as badges. */
    public static void label(TextView v) {
        style(v, 10f, MEDIUM, 0.085f);
    }

    /** Tabular figures so live numbers do not jitter their width. */
    public static void numeric(TextView v, float sp) {
        style(v, sp, MEDIUM, -0.022f);
        v.setFontFeatureSettings("'tnum'");
    }

    public static void style(TextView v, float sp, Typeface face, float tracking) {
        v.setTextSize(TypedValue.COMPLEX_UNIT_SP, sp);
        v.setTypeface(face);
        v.setLetterSpacing(tracking);
        v.getPaint().setFakeBoldText(false);
        v.setIncludeFontPadding(false);
    }

    public static float spToPx(TextView v, float sp) {
        return sp * v.getResources().getDisplayMetrics().scaledDensity;
    }
}
