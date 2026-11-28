package com.openpixel.deck.ui;

import android.graphics.Canvas;
import android.graphics.CornerPathEffect;
import android.graphics.Paint;
import android.graphics.Path;
import android.graphics.RectF;

import java.util.HashMap;
import java.util.Map;

/**
 * The deck's vector icon set.
 *
 * <p>Every glyph is authored on a 24x24 grid as path data and drawn with a round
 * capped stroke, so the interface ships no emoji, no bitmap icon sprites and no
 * extra drawable resources. Icons scale cleanly to any density and tint from a
 * single colour token.</p>
 */
public final class Icons {

    public static final String PLAY = "play";
    public static final String PAUSE = "pause";
    public static final String PREV = "prev";
    public static final String NEXT = "next";
    public static final String VOLUME = "volume";
    public static final String VOLUME_OFF = "volume-off";
    public static final String MIC = "mic";
    public static final String MIC_OFF = "mic-off";
    public static final String MUSIC = "music";
    public static final String TRACKPAD = "trackpad";
    public static final String GRID = "grid";
    public static final String LAYERS = "layers";
    public static final String MONITOR = "monitor";
    public static final String CLIPBOARD = "clipboard";
    public static final String FILE = "file";
    public static final String CHECK = "check";
    public static final String SQUARE = "square";
    public static final String CHECK_SQUARE = "check-square";
    public static final String BOOKMARK = "bookmark";
    public static final String EXPAND = "expand";
    public static final String COLLAPSE = "collapse";
    public static final String CLOSE = "close";
    public static final String PLUS = "plus";
    public static final String CAMERA = "camera";
    public static final String GAMEPAD = "gamepad";
    public static final String RECORD = "record";
    public static final String PRESENT = "present";
    public static final String FILM = "film";
    public static final String WORK = "work";
    public static final String LOCK = "lock";
    public static final String MOON = "moon";
    public static final String RESTART = "restart";
    public static final String POWER = "power";
    public static final String COPY = "copy";
    public static final String PASTE = "paste";
    public static final String UNDO = "undo";
    public static final String SEND = "send";
    public static final String KEYBOARD = "keyboard";
    public static final String CHEVRON_DOWN = "chevron-down";
    public static final String CHEVRON_UP = "chevron-up";
    public static final String CHEVRON_LEFT = "chevron-left";
    public static final String CHEVRON_RIGHT = "chevron-right";
    public static final String ARROW_LEFT = "arrow-left";
    public static final String DOT = "dot";
    public static final String SLIDERS = "sliders";

    private static final Map<String, Glyph> GLYPHS = new HashMap<>();

    private static final Paint STROKE = new Paint(Paint.ANTI_ALIAS_FLAG);
    private static final Paint FILL = new Paint(Paint.ANTI_ALIAS_FLAG);

    static {
        STROKE.setStyle(Paint.Style.STROKE);
        STROKE.setStrokeWidth(1.85f);
        STROKE.setStrokeCap(Paint.Cap.ROUND);
        STROKE.setStrokeJoin(Paint.Join.ROUND);
        FILL.setStyle(Paint.Style.FILL);
        FILL.setPathEffect(new CornerPathEffect(1.2f));
    }

    private Icons() {}

    private static final class Glyph {
        final Path stroke;
        final Path fill;

        Glyph(Path stroke, Path fill) {
            this.stroke = stroke;
            this.fill = fill;
        }
    }

    private static final class Builder {
        final Path s = new Path();
        final Path f = new Path();

        Builder line(float x1, float y1, float x2, float y2) {
            s.moveTo(x1, y1);
            s.lineTo(x2, y2);
            return this;
        }

        Builder rect(float l, float t, float r, float b, float rad) {
            s.addRoundRect(new RectF(l, t, r, b), rad, rad, Path.Direction.CW);
            return this;
        }

        Builder oval(float l, float t, float r, float b) {
            s.addOval(new RectF(l, t, r, b), Path.Direction.CW);
            return this;
        }

        Builder arc(float l, float t, float r, float b, float start, float sweep) {
            s.addArc(new RectF(l, t, r, b), start, sweep);
            return this;
        }

        Builder shape(float... pts) {
            s.moveTo(pts[0], pts[1]);
            for (int i = 2; i + 1 < pts.length; i += 2) s.lineTo(pts[i], pts[i + 1]);
            s.close();
            return this;
        }

        Builder fillShape(float... pts) {
            f.moveTo(pts[0], pts[1]);
            for (int i = 2; i + 1 < pts.length; i += 2) f.lineTo(pts[i], pts[i + 1]);
            f.close();
            return this;
        }

        Builder dot(float cx, float cy, float r) {
            f.addCircle(cx, cy, r, Path.Direction.CW);
            return this;
        }

        Glyph build() {
            return new Glyph(s.isEmpty() ? null : s, f.isEmpty() ? null : f);
        }
    }

    private static Glyph glyph(String name) {
        if (name == null) return null;
        synchronized (GLYPHS) {
            Glyph cached = GLYPHS.get(name);
            if (cached != null) return cached;
            Glyph created = build(name);
            if (created != null) GLYPHS.put(name, created);
            return created;
        }
    }

    private static Glyph build(String name) {
        Builder b = new Builder();
        switch (name) {
            case PLAY:
                return b.fillShape(7.8f, 5.1f, 19.0f, 12.0f, 7.8f, 18.9f).build();

            case PAUSE:
                b.f.addRoundRect(new RectF(7.4f, 5.2f, 11.0f, 18.8f), 1.4f, 1.4f, Path.Direction.CW);
                b.f.addRoundRect(new RectF(13.0f, 5.2f, 16.6f, 18.8f), 1.4f, 1.4f, Path.Direction.CW);
                return b.build();

            case PREV:
                b.f.addRoundRect(new RectF(5.8f, 5.6f, 7.9f, 18.4f), 1.0f, 1.0f, Path.Direction.CW);
                b.fillShape(18.4f, 5.7f, 9.9f, 12.0f, 18.4f, 18.3f);
                return b.build();

            case NEXT:
                b.f.addRoundRect(new RectF(16.1f, 5.6f, 18.2f, 18.4f), 1.0f, 1.0f, Path.Direction.CW);
                b.fillShape(5.6f, 5.7f, 14.1f, 12.0f, 5.6f, 18.3f);
                return b.build();

            case VOLUME:
                b.shape(4.0f, 9.2f, 7.2f, 9.2f, 11.2f, 5.2f, 11.2f, 18.8f, 7.2f, 14.8f, 4.0f, 14.8f);
                b.arc(12.0f, 7.6f, 18.0f, 16.4f, -52f, 104f);
                b.arc(12.0f, 4.2f, 21.2f, 19.8f, -48f, 96f);
                return b.build();

            case VOLUME_OFF:
                b.shape(4.0f, 9.2f, 7.2f, 9.2f, 11.2f, 5.2f, 11.2f, 18.8f, 7.2f, 14.8f, 4.0f, 14.8f);
                b.line(15.0f, 9.4f, 19.4f, 13.8f);
                b.line(19.4f, 9.4f, 15.0f, 13.8f);
                return b.build();

            case MIC:
                b.rect(9.0f, 2.6f, 15.0f, 12.4f, 3.0f);
                b.arc(6.2f, 6.6f, 17.8f, 16.4f, 0f, 180f);
                b.line(12.0f, 16.4f, 12.0f, 20.2f);
                b.line(8.8f, 20.2f, 15.2f, 20.2f);
                return b.build();

            case MIC_OFF:
                b.rect(9.0f, 2.6f, 15.0f, 12.4f, 3.0f);
                b.arc(6.2f, 6.6f, 17.8f, 16.4f, 0f, 180f);
                b.line(12.0f, 16.4f, 12.0f, 20.2f);
                b.line(8.8f, 20.2f, 15.2f, 20.2f);
                b.line(4.4f, 3.4f, 19.6f, 20.6f);
                return b.build();

            case MUSIC:
                b.line(5.0f, 10.0f, 5.0f, 14.0f);
                b.line(8.5f, 6.4f, 8.5f, 17.6f);
                b.line(12.0f, 8.8f, 12.0f, 15.2f);
                b.line(15.5f, 4.8f, 15.5f, 19.2f);
                b.line(19.0f, 10.6f, 19.0f, 13.4f);
                return b.build();

            case TRACKPAD:
                b.rect(3.4f, 5.4f, 20.6f, 18.6f, 3.4f);
                b.dot(12.0f, 13.4f, 2.2f);
                return b.build();

            case GRID:
                b.rect(4.0f, 4.0f, 10.8f, 10.8f, 2.4f);
                b.rect(13.2f, 4.0f, 20.0f, 10.8f, 2.4f);
                b.rect(4.0f, 13.2f, 10.8f, 20.0f, 2.4f);
                b.rect(13.2f, 13.2f, 20.0f, 20.0f, 2.4f);
                return b.build();

            case LAYERS:
                b.rect(3.4f, 4.6f, 20.6f, 8.4f, 1.9f);
                b.rect(3.4f, 10.1f, 17.2f, 13.9f, 1.9f);
                b.rect(3.4f, 15.6f, 20.6f, 19.4f, 1.9f);
                return b.build();

            case MONITOR:
                b.rect(3.2f, 4.6f, 20.8f, 16.4f, 2.4f);
                b.line(12.0f, 16.4f, 12.0f, 19.8f);
                b.line(8.6f, 19.8f, 15.4f, 19.8f);
                return b.build();

            case CLIPBOARD:
                b.rect(5.8f, 4.6f, 18.2f, 20.4f, 2.8f);
                b.rect(9.4f, 2.8f, 14.6f, 6.4f, 1.6f);
                b.line(9.2f, 11.2f, 14.8f, 11.2f);
                b.line(9.2f, 14.8f, 14.8f, 14.8f);
                return b.build();

            case FILE:
                b.shape(6.4f, 3.6f, 13.6f, 3.6f, 17.8f, 7.8f, 17.8f, 20.4f, 6.4f, 20.4f);
                b.line(13.6f, 3.6f, 13.6f, 7.8f);
                b.line(13.6f, 7.8f, 17.8f, 7.8f);
                return b.build();

            case CHECK:
                b.line(5.2f, 12.4f, 9.6f, 16.8f);
                b.line(9.6f, 16.8f, 18.8f, 7.2f);
                return b.build();

            case SQUARE:
                b.rect(4.0f, 4.0f, 20.0f, 20.0f, 4.6f);
                return b.build();

            case CHECK_SQUARE:
                b.rect(3.8f, 3.8f, 20.2f, 20.2f, 4.8f);
                b.line(8.0f, 12.2f, 11.0f, 15.2f);
                b.line(11.0f, 15.2f, 16.2f, 9.4f);
                return b.build();

            case BOOKMARK:
                b.shape(7.0f, 4.2f, 17.0f, 4.2f, 17.0f, 20.2f, 12.0f, 16.2f, 7.0f, 20.2f);
                return b.build();

            case EXPAND:
                b.line(4.0f, 9.5f, 4.0f, 4.0f);
                b.line(4.0f, 4.0f, 9.5f, 4.0f);
                b.line(14.5f, 4.0f, 20.0f, 4.0f);
                b.line(20.0f, 4.0f, 20.0f, 9.5f);
                b.line(20.0f, 14.5f, 20.0f, 20.0f);
                b.line(20.0f, 20.0f, 14.5f, 20.0f);
                b.line(9.5f, 20.0f, 4.0f, 20.0f);
                b.line(4.0f, 20.0f, 4.0f, 14.5f);
                return b.build();

            case COLLAPSE:
                b.line(9.5f, 4.0f, 9.5f, 9.5f);
                b.line(9.5f, 9.5f, 4.0f, 9.5f);
                b.line(14.5f, 4.0f, 14.5f, 9.5f);
                b.line(14.5f, 9.5f, 20.0f, 9.5f);
                b.line(9.5f, 20.0f, 9.5f, 14.5f);
                b.line(9.5f, 14.5f, 4.0f, 14.5f);
                b.line(14.5f, 20.0f, 14.5f, 14.5f);
                b.line(14.5f, 14.5f, 20.0f, 14.5f);
                return b.build();

            case CLOSE:
                b.line(6.4f, 6.4f, 17.6f, 17.6f);
                b.line(17.6f, 6.4f, 6.4f, 17.6f);
                return b.build();

            case PLUS:
                b.line(12.0f, 5.6f, 12.0f, 18.4f);
                b.line(5.6f, 12.0f, 18.4f, 12.0f);
                return b.build();

            case CAMERA:
                b.rect(3.2f, 7.0f, 20.8f, 20.0f, 3.2f);
                b.s.moveTo(8.6f, 7.0f);
                b.s.lineTo(10.0f, 4.2f);
                b.s.lineTo(14.0f, 4.2f);
                b.s.lineTo(15.4f, 7.0f);
                b.oval(8.6f, 10.0f, 15.4f, 16.8f);
                return b.build();

            case GAMEPAD:
                b.rect(2.6f, 8.2f, 21.4f, 18.4f, 5.0f);
                b.line(6.8f, 13.3f, 10.6f, 13.3f);
                b.line(8.7f, 11.4f, 8.7f, 15.2f);
                b.oval(14.4f, 11.2f, 17.0f, 13.8f);
                b.oval(17.2f, 13.4f, 19.4f, 15.6f);
                return b.build();

            case RECORD:
                b.oval(4.0f, 4.0f, 20.0f, 20.0f);
                b.dot(12.0f, 12.0f, 3.9f);
                return b.build();

            case PRESENT:
                b.rect(3.2f, 4.4f, 20.8f, 15.6f, 2.2f);
                b.line(12.0f, 15.6f, 12.0f, 19.4f);
                b.line(8.8f, 19.4f, 15.2f, 19.4f);
                b.line(7.6f, 13.0f, 7.6f, 10.6f);
                b.line(12.0f, 13.0f, 12.0f, 8.6f);
                b.line(16.4f, 13.0f, 16.4f, 11.2f);
                return b.build();

            case FILM:
                b.rect(2.8f, 5.2f, 21.2f, 18.8f, 2.6f);
                b.line(7.6f, 5.2f, 7.6f, 18.8f);
                b.line(16.4f, 5.2f, 16.4f, 18.8f);
                b.dot(5.2f, 8.6f, 0.95f);
                b.dot(5.2f, 12.0f, 0.95f);
                b.dot(5.2f, 15.4f, 0.95f);
                b.dot(18.8f, 8.6f, 0.95f);
                b.dot(18.8f, 12.0f, 0.95f);
                b.dot(18.8f, 15.4f, 0.95f);
                return b.build();

            case WORK:
                b.rect(3.0f, 7.6f, 21.0f, 20.0f, 2.8f);
                b.rect(8.8f, 4.0f, 15.2f, 7.6f, 1.8f);
                b.line(3.0f, 12.4f, 21.0f, 12.4f);
                return b.build();

            case LOCK:
                b.rect(5.0f, 10.4f, 19.0f, 20.2f, 3.0f);
                b.arc(7.6f, 3.4f, 16.4f, 13.4f, 180f, 180f);
                b.dot(12.0f, 15.0f, 1.3f);
                b.line(12.0f, 15.4f, 12.0f, 17.4f);
                return b.build();

            case MOON: {
                Path outer = new Path();
                outer.addOval(new RectF(4.4f, 3.2f, 20.0f, 20.8f), Path.Direction.CW);
                Path cut = new Path();
                cut.addOval(new RectF(9.6f, 0.4f, 25.2f, 17.6f), Path.Direction.CW);
                Path crescent = new Path();
                crescent.op(outer, cut, Path.Op.DIFFERENCE);
                b.f.addPath(crescent);
                return b.build();
            }

            case RESTART:
                b.arc(4.6f, 4.6f, 19.4f, 19.4f, -58f, 296f);
                b.fillShape(12.2f, 2.2f, 17.6f, 5.1f, 12.6f, 8.2f);
                return b.build();

            case POWER:
                b.arc(4.8f, 5.2f, 19.2f, 20.4f, -50f, 280f);
                b.line(12.0f, 3.2f, 12.0f, 11.4f);
                return b.build();

            case COPY:
                b.s.moveTo(15.6f, 3.4f);
                b.s.lineTo(5.4f, 3.4f);
                b.s.quadTo(3.4f, 3.4f, 3.4f, 5.4f);
                b.s.lineTo(3.4f, 15.6f);
                b.rect(8.4f, 8.4f, 20.4f, 20.4f, 2.6f);
                return b.build();

            case PASTE:
                b.rect(5.8f, 4.6f, 18.2f, 20.4f, 2.8f);
                b.rect(9.4f, 2.8f, 14.6f, 6.4f, 1.6f);
                b.rect(8.8f, 9.6f, 15.2f, 17.4f, 1.6f);
                return b.build();

            case UNDO:
            case ARROW_LEFT:
                b.line(19.0f, 12.0f, 5.2f, 12.0f);
                b.line(10.8f, 5.8f, 5.0f, 12.0f);
                b.line(5.0f, 12.0f, 10.8f, 18.2f);
                return b.build();

            case SEND:
                b.shape(4.2f, 12.0f, 20.4f, 4.2f, 14.8f, 20.4f, 11.6f, 13.6f);
                b.line(11.6f, 13.6f, 20.4f, 4.2f);
                return b.build();

            case KEYBOARD:
                b.rect(2.6f, 6.2f, 21.4f, 17.8f, 2.6f);
                b.dot(6.0f, 9.6f, 0.95f);
                b.dot(9.2f, 9.6f, 0.95f);
                b.dot(12.4f, 9.6f, 0.95f);
                b.dot(15.6f, 9.6f, 0.95f);
                b.dot(18.8f, 9.6f, 0.95f);
                b.dot(6.0f, 13.0f, 0.95f);
                b.dot(18.8f, 13.0f, 0.95f);
                b.line(9.2f, 13.4f, 14.8f, 13.4f);
                return b.build();

            case CHEVRON_DOWN:
                b.line(7.4f, 9.8f, 12.0f, 14.4f);
                b.line(12.0f, 14.4f, 16.6f, 9.8f);
                return b.build();

            case CHEVRON_UP:
                b.line(7.4f, 14.2f, 12.0f, 9.6f);
                b.line(12.0f, 9.6f, 16.6f, 14.2f);
                return b.build();

            case CHEVRON_LEFT:
                b.line(14.4f, 5.6f, 9.0f, 12.0f);
                b.line(9.0f, 12.0f, 14.4f, 18.4f);
                return b.build();

            case CHEVRON_RIGHT:
                b.line(9.6f, 5.6f, 15.0f, 12.0f);
                b.line(15.0f, 12.0f, 9.6f, 18.4f);
                return b.build();

            case DOT:
                b.dot(12.0f, 12.0f, 5.0f);
                return b.build();

            case SLIDERS:
                b.line(4.0f, 7.2f, 7.0f, 7.2f);
                b.line(11.4f, 7.2f, 20.0f, 7.2f);
                b.oval(7.0f, 4.9f, 11.4f, 9.3f);
                b.line(4.0f, 12.0f, 13.2f, 12.0f);
                b.line(17.6f, 12.0f, 20.0f, 12.0f);
                b.oval(13.2f, 9.7f, 17.6f, 14.1f);
                b.line(4.0f, 16.8f, 8.6f, 16.8f);
                b.line(13.0f, 16.8f, 20.0f, 16.8f);
                b.oval(8.6f, 14.5f, 13.0f, 18.9f);
                return b.build();

            default:
                b.oval(6.0f, 6.0f, 18.0f, 18.0f);
                return b.build();
        }
    }

    /** Draw an icon centred on (cx, cy). */
    public static void draw(Canvas canvas, String name, float cx, float cy, float sizePx, int color) {
        draw(canvas, name, cx, cy, sizePx, color, 1f);
    }

    /**
     * @param weight multiplier for the stroke weight; 1 is the regular weight used
     *               across the deck (about 1.5dp at a 20dp icon).
     */
    public static void draw(Canvas canvas, String name, float cx, float cy, float sizePx,
                            int color, float weight) {
        Glyph g = glyph(name);
        if (g == null || sizePx <= 0f) return;
        float scale = sizePx / 24f;
        int save = canvas.save();
        canvas.translate(cx - sizePx * 0.5f, cy - sizePx * 0.5f);
        canvas.scale(scale, scale);
        if (g.fill != null) {
            FILL.setColor(color);
            canvas.drawPath(g.fill, FILL);
        }
        if (g.stroke != null) {
            STROKE.setColor(color);
            STROKE.setStrokeWidth(1.85f * Math.max(0.5f, weight));
            canvas.drawPath(g.stroke, STROKE);
        }
        canvas.restoreToCount(save);
    }
}
