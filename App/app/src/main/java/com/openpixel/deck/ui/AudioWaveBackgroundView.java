package com.openpixel.deck.ui;

import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.LinearGradient;
import android.graphics.Paint;
import android.graphics.Path;
import android.graphics.Shader;
import android.os.SystemClock;
import android.view.View;

/**
 * Restrained two-layer audio response behind the Now screen.
 *
 * <p>It reads the live PC output level in the playing track's dominant colour,
 * eases toward it, and stops drawing the moment the room goes quiet — no idle
 * animation, no full-screen brightness swings.</p>
 */
public final class AudioWaveBackgroundView extends View {

    private static final int LAYERS = 2;

    private final Paint wavePaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint gradientPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Path wavePath = new Path();
    private final LinearGradient[] waveShaders = new LinearGradient[LAYERS];

    private int waveColor = Color.rgb(43, 228, 234);
    private int customTop = 0;
    private int customMid = 0;
    private int customBottom = 0;
    private boolean customGradient = false;

    private float targetLevel = 0f;
    private float level = 0f;
    private boolean playing = false;
    private boolean enabled = true;
    private float phase = 0f;
    private long lastDrawMs = 0L;

    private LinearGradient customShader;
    private int cachedW = -1;
    private int cachedH = -1;
    private int cachedColor = 0;
    private int cachedTop = 0;
    private int cachedMid = 0;
    private int cachedBottom = 0;

    public AudioWaveBackgroundView(Context ctx) {
        super(ctx);
        setLayerType(LAYER_TYPE_HARDWARE, null);
    }

    public void setCustomGradient(boolean active, int top, int mid, int bottom) {
        if (customGradient == active && customTop == top && customMid == mid && customBottom == bottom) {
            return;
        }
        customGradient = active;
        customTop = top;
        customMid = mid;
        customBottom = bottom;
        customShader = null;
        setVisibility((enabled || customGradient) ? VISIBLE : GONE);
        invalidate();
    }

    public void setWaveState(int color, int audioLevel0to100, boolean isPlaying, boolean waveEnabled) {
        boolean colorChanged = waveColor != color;
        waveColor = color;
        boolean nowPlaying = isPlaying || audioLevel0to100 > 6;
        playing = nowPlaying;
        targetLevel = nowPlaying ? Math.max(0.16f, Math.min(1f, audioLevel0to100 / 100f)) : 0f;
        enabled = waveEnabled;
        setVisibility((waveEnabled || customGradient) ? VISIBLE : GONE);
        if (waveEnabled && (nowPlaying || level > 0.01f || colorChanged)) {
            if (nowPlaying && level < 0.01f) lastDrawMs = SystemClock.uptimeMillis();
            postInvalidateOnAnimation();
        } else if (customGradient) {
            invalidate();
        }
    }

    private void ensureShaders(int w, int h) {
        if (customGradient && (customShader == null || cachedW != w || cachedH != h
                || cachedTop != customTop || cachedMid != customMid || cachedBottom != customBottom)) {
            customShader = new LinearGradient(0, 0, w * 0.3f, h,
                    new int[]{customTop, customMid, customBottom},
                    new float[]{0f, 0.55f, 1f}, Shader.TileMode.CLAMP);
            cachedTop = customTop;
            cachedMid = customMid;
            cachedBottom = customBottom;
        }
        if (cachedW != w || cachedH != h || cachedColor != waveColor || waveShaders[0] == null) {
            int r = Color.red(waveColor);
            int g = Color.green(waveColor);
            int b = Color.blue(waveColor);
            int[] alphas = {46, 28};
            float[] tops = {0.62f, 0.74f};
            for (int i = 0; i < LAYERS; i++) {
                waveShaders[i] = new LinearGradient(0, h * tops[i], 0, h,
                        new int[]{Color.argb(alphas[i], r, g, b), Color.TRANSPARENT},
                        new float[]{0f, 1f}, Shader.TileMode.CLAMP);
            }
            cachedW = w;
            cachedH = h;
            cachedColor = waveColor;
        }
    }

    @Override
    protected void onDraw(Canvas canvas) {
        if (!enabled && !customGradient) return;
        int w = getWidth();
        int h = getHeight();
        if (w <= 0 || h <= 0) return;

        ensureShaders(w, h);

        if (customGradient && customShader != null) {
            gradientPaint.setShader(customShader);
            canvas.drawRect(0, 0, w, h, gradientPaint);
        }

        if (!enabled) return;

        long now = SystemClock.uptimeMillis();
        float dt = lastDrawMs > 0L ? Math.min(0.05f, Math.max(0.004f, (now - lastDrawMs) / 1000f)) : 0.016f;
        lastDrawMs = now;

        float target = playing ? Math.max(0.22f, targetLevel) : 0f;
        float rate = 1f - (float) Math.exp(-dt * (playing ? 7f : 5.5f));
        level += (target - level) * rate;

        if (!playing && level < 0.006f) {
            level = 0f;
            return;
        }

        phase += dt * (0.55f + 0.35f * level);

        float[] baseY = {0.80f, 0.90f};
        float[] speed = {1f, -1.35f};
        int steps = 16;
        float stepX = (float) w / steps;
        float visibility = Math.min(1f, level / 0.14f);

        for (int layer = 0; layer < LAYERS; layer++) {
            float amp = h * (0.018f + 0.05f * level) * (1f - layer * 0.22f);
            float y0 = h * baseY[layer] - level * h * 0.02f;

            wavePath.reset();
            wavePath.moveTo(0, h);
            float prevX = 0f;
            float prevY = y0 + (float) Math.sin(phase * speed[layer] + layer) * amp;
            wavePath.lineTo(prevX, prevY);

            for (int i = 1; i <= steps; i++) {
                float x = i * stepX;
                float norm = (float) i / steps;
                float envelope = (float) Math.sin(norm * Math.PI);
                float y = y0 + (float) Math.sin(norm * Math.PI * 2.0 + phase * speed[layer] + layer * 1.3f)
                        * amp * (0.4f + 0.6f * envelope);
                float midX = (prevX + x) * 0.5f;
                float midY = (prevY + y) * 0.5f;
                wavePath.quadTo(prevX, prevY, midX, midY);
                prevX = x;
                prevY = y;
            }
            wavePath.lineTo(w, prevY);
            wavePath.lineTo(w, h);
            wavePath.close();

            wavePaint.setShader(waveShaders[layer]);
            wavePaint.setAlpha((int) (255 * visibility));
            canvas.drawPath(wavePath, wavePaint);
        }

        if (playing || level > 0.006f) postInvalidateOnAnimation();
    }
}
