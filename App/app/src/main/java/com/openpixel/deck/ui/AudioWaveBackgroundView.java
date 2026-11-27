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
 * Smooth, flowing multi-layer liquid audio wave background shown ONLY behind Tab 1 (Now).
 * Reacts smoothly to live PC CoreAudio output levels in the active music's dominant color.
 */
public final class AudioWaveBackgroundView extends View {
    private final Paint wavePaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint gradBgPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Path wavePath = new Path();
    private int waveColor = Color.rgb(43, 228, 234);
    private int customGradTop = 0;
    private int customGradMid = 0;
    private int customGradBot = 0;
    private boolean hasCustomGradient = false;
    private float targetLevel = 0.0f;
    private float smoothLevel = 0.0f;
    private boolean playing = false;
    private boolean enabled = true;
    private long startMs = SystemClock.uptimeMillis();
    private long lastDrawMs = 0L;
    private float animPhase = 0f;

    // Cached shaders (zero per-frame LinearGradient allocations)
    private final LinearGradient[] cachedWaveShaders = new LinearGradient[3];
    private LinearGradient cachedCustomShader = null;
    private int cachedW = -1, cachedH = -1, cachedWaveColor = 0;
    private int cachedGradTop = 0, cachedGradMid = 0, cachedGradBot = 0;

    public AudioWaveBackgroundView(Context ctx) {
        super(ctx);
        setLayerType(LAYER_TYPE_HARDWARE, null);
    }

    public void setCustomGradient(boolean active, int topCol, int midCol, int botCol) {
        if (this.hasCustomGradient == active && this.customGradTop == topCol
                && this.customGradMid == midCol && this.customGradBot == botCol) {
            return;
        }
        this.hasCustomGradient = active;
        this.customGradTop = topCol;
        this.customGradMid = midCol;
        this.customGradBot = botCol;
        this.cachedCustomShader = null;
        setVisibility((enabled || hasCustomGradient) ? VISIBLE : GONE);
        invalidate();
    }

    public void setWaveState(int color, int audioLevel0to100, boolean isPlaying, boolean waveEnabled) {
        boolean colorChanged = (this.waveColor != color);
        this.waveColor = color;
        boolean nowPlaying = isPlaying || audioLevel0to100 > 5;
        boolean startedPlaying = nowPlaying && !this.playing;
        this.playing = nowPlaying;
        this.targetLevel = nowPlaying ? Math.max(0.18f, Math.min(1.0f, audioLevel0to100 / 100f)) : 0.0f;
        this.enabled = waveEnabled;
        setVisibility((waveEnabled || hasCustomGradient) ? VISIBLE : GONE);
        if (waveEnabled && (nowPlaying || smoothLevel > 0.01f || colorChanged || startedPlaying)) {
            if (startedPlaying) lastDrawMs = SystemClock.uptimeMillis();
            postInvalidateOnAnimation();
        } else if (hasCustomGradient) {
            invalidate();
        }
    }

    private void ensureShaders(int w, int h) {
        if (hasCustomGradient && (cachedCustomShader == null || cachedW != w || cachedH != h
                || cachedGradTop != customGradTop || cachedGradMid != customGradMid || cachedGradBot != customGradBot)) {
            cachedCustomShader = new LinearGradient(
                    0, 0, w * 0.35f, h,
                    new int[]{customGradTop, customGradMid, customGradBot},
                    new float[]{0f, 0.52f, 1f},
                    Shader.TileMode.CLAMP);
            cachedGradTop = customGradTop;
            cachedGradMid = customGradMid;
            cachedGradBot = customGradBot;
        }
        if (cachedW != w || cachedH != h || cachedWaveColor != waveColor || cachedWaveShaders[0] == null) {
            int r = Color.red(waveColor);
            int g = Color.green(waveColor);
            int b = Color.blue(waveColor);
            int[] alphas = {82, 62, 46};
            float[] topFrac = {0.42f, 0.54f, 0.66f};
            for (int i = 0; i < 3; i++) {
                cachedWaveShaders[i] = new LinearGradient(
                        0, h * topFrac[i], 0, h,
                        new int[]{Color.argb(alphas[i], r, g, b), Color.argb(alphas[i] / 3, r, g, b), Color.TRANSPARENT},
                        new float[]{0f, 0.65f, 1f},
                        Shader.TileMode.CLAMP);
            }
            cachedW = w;
            cachedH = h;
            cachedWaveColor = waveColor;
        }
    }

    @Override
    protected void onDraw(Canvas canvas) {
        if (!enabled && !hasCustomGradient) return;
        int w = getWidth();
        int h = getHeight();
        if (w <= 0 || h <= 0) return;

        ensureShaders(w, h);

        if (hasCustomGradient && cachedCustomShader != null) {
            gradBgPaint.setShader(cachedCustomShader);
            canvas.drawRect(0, 0, w, h, gradBgPaint);
        }

        if (!enabled) return;

        long now = SystemClock.uptimeMillis();
        float dt = lastDrawMs > 0L ? Math.min(0.05f, Math.max(0.004f, (now - lastDrawMs) / 1000f)) : 0.016f;
        lastDrawMs = now;

        float target = playing ? Math.max(0.24f, targetLevel) : 0.0f;
        float lerpRate = playing ? (1f - (float) Math.exp(-dt * 8.5f)) : (1f - (float) Math.exp(-dt * 6.5f));
        smoothLevel += (target - smoothLevel) * lerpRate;

        // Completely stop rendering & animating when silent
        if (!playing && smoothLevel < 0.008f) {
            smoothLevel = 0f;
            return;
        }

        animPhase += dt * (0.95f + 0.65f * smoothLevel);

        // 3 smooth overlapping sine-spline audio waves rising organically from the lower half
        float[] baseYFrac = {0.64f, 0.74f, 0.84f};
        float[] freq = {1.25f, 1.75f, 2.25f};
        float[] speed = {1.35f, -1.75f, 2.20f};

        int steps = 20;
        float stepX = (float) w / steps;
        float visAlpha = Math.min(1f, smoothLevel / 0.16f);

        for (int layer = 0; layer < 3; layer++) {
            float amp = (h * (0.032f + 0.115f * smoothLevel)) * (1f - layer * 0.16f);
            float baseY = h * baseYFrac[layer] - smoothLevel * h * 0.07f;

            wavePath.reset();
            wavePath.moveTo(0, h);
            float prevX = 0f;
            float prevY = baseY + (float) Math.sin(animPhase * speed[layer] + layer * 1.4f) * amp;
            wavePath.lineTo(prevX, prevY);

            for (int i = 1; i <= steps; i++) {
                float x = i * stepX;
                float norm = (float) i / steps;
                float env = (float) Math.sin(norm * Math.PI);
                float y = baseY
                        + (float) Math.sin(norm * Math.PI * 2.0 * freq[layer] + animPhase * speed[layer] + layer * 1.7f) * amp * (0.45f + 0.55f * env)
                        + (float) Math.cos(norm * Math.PI * 2.8 - animPhase * speed[layer] * 0.65f) * amp * 0.28f;
                float midX = (prevX + x) * 0.5f;
                float midY = (prevY + y) * 0.5f;
                wavePath.quadTo(prevX, prevY, midX, midY);
                prevX = x;
                prevY = y;
            }
            wavePath.lineTo(w, prevY);
            wavePath.lineTo(w, h);
            wavePath.close();

            wavePaint.setShader(cachedWaveShaders[layer]);
            wavePaint.setAlpha((int) (255 * visAlpha));
            canvas.drawPath(wavePath, wavePaint);
        }

        if (playing || smoothLevel > 0.008f) {
            postInvalidateOnAnimation();
        }
    }
}
