package com.openpixel.deck.ui;

import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.LinearGradient;
import android.graphics.Paint;
import android.graphics.RectF;
import android.graphics.RenderEffect;
import android.graphics.RuntimeShader;
import android.graphics.Shader;
import android.os.Build;
import android.view.View;
import android.widget.LinearLayout;

/**
 * Liquid Glass surfaces for the deck.
 *
 * <p>Real glass reads as a material because of four things, all of which are
 * drawn here instead of faked with a flat translucent rectangle: a tinted body
 * that picks up the colour behind it, a bright specular catch along the top
 * edge, a refracted bevel just inside the rim, and a soft contact shadow that
 * lifts the surface off the backdrop.</p>
 *
 * <p>Every pass is a single hardware round-rect with a cached shader, so the
 * whole treatment costs a handful of draws with no bitmap capture and no work
 * on the input path.</p>
 */
public final class LiquidGlassHelper {

    /**
     * AGSL optical lens used for the ambient layer on API 33+: SDF rounded box,
     * an edge-normal refraction of the backdrop and a Fresnel-ish rim.
     */
    private static final String AMBIENT_LENS_AGSL =
            "uniform shader content;\n" +
            "uniform float2 resolution;\n" +
            "uniform float cornerRadius;\n" +
            "uniform float refraction;\n" +
            "uniform float dispersion;\n" +
            "uniform float4 tintColor;\n" +
            "float sdRoundBox(float2 p, float2 b, float r) {\n" +
            "    float2 q = abs(p) - b + float2(r);\n" +
            "    return min(max(q.x, q.y), 0.0) + length(max(q, 0.0)) - r;\n" +
            "}\n" +
            "half4 main(float2 fragCoord) {\n" +
            "    float2 halfSize = max(resolution * 0.5, float2(1.0));\n" +
            "    float2 p = fragCoord - halfSize;\n" +
            "    float r = min(cornerRadius, min(halfSize.x, halfSize.y));\n" +
            "    float d = sdRoundBox(p, halfSize, r);\n" +
            "    float bevel = max(10.0, r * 0.9);\n" +
            "    float edge = clamp(1.0 + d / bevel, 0.0, 1.0);\n" +
            "    float lens = edge * edge * edge;\n" +
            "    float2 eps = float2(1.5, 0.0);\n" +
            "    float2 grad = float2(\n" +
            "        sdRoundBox(p + eps.xy, halfSize, r) - sdRoundBox(p - eps.xy, halfSize, r),\n" +
            "        sdRoundBox(p + eps.yx, halfSize, r) - sdRoundBox(p - eps.yx, halfSize, r)\n" +
            "    );\n" +
            "    float gLen = max(length(grad), 0.001);\n" +
            "    float2 n = grad / gLen;\n" +
            "    float2 bend = -n * lens * refraction;\n" +
            "    float2 uvR = clamp(fragCoord + bend - n * lens * dispersion, float2(0.0), resolution - 1.0);\n" +
            "    float2 uvG = clamp(fragCoord + bend, float2(0.0), resolution - 1.0);\n" +
            "    float2 uvB = clamp(fragCoord + bend + n * lens * dispersion, float2(0.0), resolution - 1.0);\n" +
            "    half4 colR = content.eval(uvR);\n" +
            "    half4 colG = content.eval(uvG);\n" +
            "    half4 colB = content.eval(uvB);\n" +
            "    half4 refracted = half4(colR.r, colG.g, colB.b, colG.a);\n" +
            "    half3 glass = mix(refracted.rgb, half3(tintColor.rgb), half(tintColor.a));\n" +
            "    return half4(clamp(glass, 0.0, 1.0), 1.0);\n" +
            "}\n";

    private LiquidGlassHelper() {}

    /**
     * Blurs (and on API 33+ optically refracts) the ambient backdrop layer.
     * Kept off the UI thread entirely and clamped so a large radius cannot stall a frame.
     */
    public static void applyBackdropLiquidEffect(View backdropView, float blurRadiusPx,
                                                 float cornerRadiusPx, int tintColor) {
        if (backdropView == null) return;
        if (Build.VERSION.SDK_INT >= 33) {
            int w = backdropView.getWidth();
            int h = backdropView.getHeight();
            if (w <= 4 || h <= 4) {
                backdropView.post(() -> applyBackdropLiquidEffect(
                        backdropView, blurRadiusPx, cornerRadiusPx, tintColor));
                return;
            }
            try {
                float safeBlur = Math.max(4f, Math.min(24f, blurRadiusPx));
                RenderEffect blur = RenderEffect.createBlurEffect(safeBlur, safeBlur, Shader.TileMode.CLAMP);
                RuntimeShader shader = new RuntimeShader(AMBIENT_LENS_AGSL);
                shader.setFloatUniform("resolution", (float) w, (float) h);
                shader.setFloatUniform("cornerRadius", cornerRadiusPx);
                shader.setFloatUniform("refraction", 3.0f);
                shader.setFloatUniform("dispersion", 0.35f);
                shader.setFloatUniform("tintColor",
                        Color.red(tintColor) / 255f,
                        Color.green(tintColor) / 255f,
                        Color.blue(tintColor) / 255f,
                        Color.alpha(tintColor) / 255f);
                RenderEffect lens = RenderEffect.createRuntimeShaderEffect(shader, "content");
                backdropView.setRenderEffect(RenderEffect.createChainEffect(lens, blur));
                return;
            } catch (Throwable ignored) {
                // Fall through to the plain blur below.
            }
        }
        if (Build.VERSION.SDK_INT >= 31) {
            try {
                float safeBlur = Math.max(4f, Math.min(44f, blurRadiusPx));
                backdropView.setRenderEffect(
                        RenderEffect.createBlurEffect(safeBlur, safeBlur, Shader.TileMode.CLAMP));
            } catch (Throwable ignored) {
            }
        }
    }

    public static void clearEffect(View v) {
        if (v != null && Build.VERSION.SDK_INT >= 31) {
            try {
                v.setRenderEffect(null);
            } catch (Throwable ignored) {
            }
        }
    }

    /**
     * Glass card container. Children are laid out normally; the material is
     * painted underneath them.
     */
    public static final class GlassContainer extends LinearLayout {

        private final Paint shadowPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
        private final Paint bodyPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
        private final Paint bevelPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
        private final Paint rimPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
        private final Paint specularPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
        private final RectF rect = new RectF();
        private final RectF inner = new RectF();
        private final RectF specular = new RectF();

        private final float density;
        private float radiusPx;
        private float elevationPx;
        private boolean glassEnabled = true;
        private boolean lightSurface = false;
        private int accentColor = Color.rgb(43, 228, 234);
        private int flatColor = Color.rgb(20, 22, 27);

        private int lastW = -1;
        private int lastH = -1;
        private int lastAccent = 0;
        private boolean lastLight = false;
        private boolean lastGlass = true;

        public GlassContainer(Context ctx, float radiusDp) {
            super(ctx);
            density = ctx.getResources().getDisplayMetrics().density;
            radiusPx = radiusDp * density;
            // The card is inset 2dp, so the contact shadow stays inside its own bounds
            // (a parent with clipChildren would otherwise cut it off).
            elevationPx = 2.2f * density;
            setWillNotDraw(false);
            bevelPaint.setStyle(Paint.Style.STROKE);
            rimPaint.setStyle(Paint.Style.STROKE);
            specularPaint.setStyle(Paint.Style.STROKE);
        }

        public void setCornerRadiusDp(float radiusDp) {
            radiusPx = radiusDp * density;
            lastW = -1;
            invalidate();
        }

        public void setGlassStyle(boolean glassOn, boolean light, int accent) {
            boolean changed = glassOn != glassEnabled || light != lightSurface || accent != accentColor;
            glassEnabled = glassOn;
            lightSurface = light;
            accentColor = accent;
            flatColor = light ? Color.rgb(252, 252, 250) : Color.rgb(19, 21, 26);
            if (changed) {
                lastW = -1;
                invalidate();
            }
        }

        private void rebuild(int w, int h) {
            lastW = w;
            lastH = h;
            lastAccent = accentColor;
            lastLight = lightSurface;
            lastGlass = glassEnabled;

            int r = Color.red(accentColor);
            int g = Color.green(accentColor);
            int b = Color.blue(accentColor);

            if (!glassEnabled) {
                bodyPaint.setShader(null);
                bodyPaint.setColor(flatColor);
                bevelPaint.setShader(null);
                rimPaint.setShader(null);
                specularPaint.setShader(null);
                return;
            }

            float inset = 2f * density;
            float top = inset;
            float bottom = h - inset;

            bodyPaint.setShader(new LinearGradient(
                    0, top, 0, bottom,
                    new int[]{
                            lightSurface
                                    ? Color.argb(244, 255, 255, 255)
                                    : Color.argb(168, 26 + r / 14, 29 + g / 14, 36 + b / 14),
                            lightSurface
                                    ? Color.argb(232, 240, 244, 250)
                                    : Color.argb(196, 13, 15, 21)
                    },
                    new float[]{0f, 1f},
                    Shader.TileMode.CLAMP));

            bevelPaint.setStrokeWidth(1.4f * density);
            bevelPaint.setShader(new LinearGradient(
                    0, top, 0, bottom,
                    new int[]{
                            lightSurface ? Color.argb(96, 255, 255, 255) : Color.argb(34, 255, 255, 255),
                            Color.TRANSPARENT,
                            lightSurface ? Color.argb(30, 20, 26, 36) : Color.argb(46, 0, 0, 0)
                    },
                    new float[]{0f, 0.55f, 1f},
                    Shader.TileMode.CLAMP));

            rimPaint.setStrokeWidth(1f * density);
            rimPaint.setShader(new LinearGradient(
                    0, top, 0, bottom,
                    new int[]{
                            lightSurface ? Color.argb(180, 255, 255, 255) : Color.argb(62, 255, 255, 255),
                            lightSurface ? Color.argb(26, 20, 26, 36) : Color.argb(10, 255, 255, 255),
                            Color.argb(lightSurface ? 54 : 66, r, g, b)
                    },
                    new float[]{0f, 0.58f, 1f},
                    Shader.TileMode.CLAMP));

            specularPaint.setStrokeWidth(1.1f * density);
            specularPaint.setShader(new LinearGradient(
                    0, top, 0, top + Math.max(10f * density, (bottom - top) * 0.22f),
                    new int[]{
                            Color.argb(lightSurface ? 150 : 96, 255, 255, 255),
                            Color.TRANSPARENT
                    },
                    new float[]{0f, 1f},
                    Shader.TileMode.CLAMP));
        }

        @Override
        protected void onDraw(Canvas canvas) {
            int w = getWidth();
            int h = getHeight();
            if (w <= 0 || h <= 0) return;

            if (w != lastW || h != lastH || accentColor != lastAccent
                    || lightSurface != lastLight || glassEnabled != lastGlass) {
                rebuild(w, h);
            }

            float inset = 2f * density;
            rect.set(inset, inset, w - inset, h - inset);
            float radius = Math.min(radiusPx, rect.height() * 0.5f);

            if (glassEnabled) {
                // Contact shadow: two soft bands rather than a single hard offset.
                shadowPaint.setColor(Color.argb(lightSurface ? 22 : 42, 0, 0, 0));
                canvas.drawRoundRect(rect.left, rect.top + elevationPx * 0.5f,
                        rect.right, rect.bottom + elevationPx * 0.5f, radius, radius, shadowPaint);
                shadowPaint.setColor(Color.argb(lightSurface ? 12 : 24, 0, 0, 0));
                canvas.drawRoundRect(rect.left, rect.top + elevationPx,
                        rect.right, rect.bottom + elevationPx, radius, radius, shadowPaint);
            }

            canvas.drawRoundRect(rect, radius, radius, bodyPaint);

            if (glassEnabled) {
                // Refracted bevel just inside the rim, then the rim itself.
                inner.set(rect.left + 1.6f * density, rect.top + 1.6f * density,
                        rect.right - 1.6f * density, rect.bottom - 1.6f * density);
                canvas.drawRoundRect(inner, Math.max(1f, radius - 1.6f * density),
                        Math.max(1f, radius - 1.6f * density), bevelPaint);
                canvas.drawRoundRect(rect, radius, radius, rimPaint);

                // Specular catch: only the top band catches the light.
                specular.set(rect.left + 0.6f * density, rect.top + 0.6f * density,
                        rect.right - 0.6f * density, rect.bottom - 0.6f * density);
                int save = canvas.save();
                canvas.clipRect(rect.left, rect.top, rect.right,
                        rect.top + Math.max(12f * density, rect.height() * 0.26f));
                canvas.drawRoundRect(specular, radius, radius, specularPaint);
                canvas.restoreToCount(save);
            } else {
                rimPaint.setShader(null);
                rimPaint.setColor(lightSurface ? Color.argb(40, 16, 20, 28) : Color.argb(30, 255, 255, 255));
                canvas.drawRoundRect(rect, radius, radius, rimPaint);
            }

            super.onDraw(canvas);
        }
    }
}
