using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using OpenPixel.Core;

namespace OpenPixel.Rendering
{
    public sealed class HitRegion
    {
        public string Id { get; init; } = "";
        public RectangleF Rect { get; init; }
        public Action OnClick { get; init; }
        public Action<float> OnDrag { get; init; }
        public bool VerticalDrag { get; init; }
    }

    // ========================================================================
    // Crash-Proof Per-Pixel Alpha Layered Window Renderer
    // - Uses UpdateLayeredWindow with a persistent 32-bpp ARGB DIB section
    // - Never recreates or disposes surfaces mid-frame on slider drags
    // - Liquid Glass & Candy Glass live background blur + cubic edge refraction
    //   with 64-point static background hash caching (0% CPU when static)
    // - Wrapped in try/catch fallbacks so if a glass surface fails on any
    //   display or GPU, it degrades gracefully to Black (#0B0B0C)
    // ========================================================================
    public sealed class LiquidGlassRenderer : IDisposable
    {
        private IntPtr _memDc = IntPtr.Zero;
        private IntPtr _dibBitmap = IntPtr.Zero;
        private IntPtr _oldBitmap = IntPtr.Zero;
        private IntPtr _dibBits = IntPtr.Zero;
        private Bitmap _managedSurface;
        private Graphics _g;
        private int _surfaceW = 0;
        private int _surfaceH = 0;
        private float _dpiScale = 1.0f;

        // Static-background cache for 0% CPU Liquid Glass when background is static
        private ulong _cachedBackdropHash = 0;
        private int _cachedGlassW = 0;
        private int _cachedGlassH = 0;
        private float _cachedBlurRadius = -1.0f;
        private int[] _cachedBlurredPixels = Array.Empty<int>();
        private int[] _capturePixels = Array.Empty<int>();
        private int[] _refractedPixels = Array.Empty<int>();
        private int[] _blurTempPixels = Array.Empty<int>();
        private Bitmap _cachedGlassBitmap;
        private long _lastGlassCaptureTs = 0;

        // Luma variance of the last blurred backdrop: 0 means a flat fill, so the
        // self-test can prove the glass is sampling real screen content.
        public double BackdropVariance { get; private set; }
        public ulong BackdropOutputHash { get; private set; }

        private readonly List<HitRegion> _hitRegions = new();
        private string _activeDragId = "";

        // Per-frame caches. Creating a GDI+ Font per string (and a brush/pen per
        // primitive) every frame is what made theme/scroll animations stutter:
        // dozens of fonts per frame cost more than the drawing itself.
        private readonly Dictionary<(bool Mono, float Size, bool Bold), Font> _fontCache = new();
        private readonly Dictionary<(StringAlignment H, StringAlignment V, bool NoWrap), StringFormat> _formatCache = new();
        private readonly SolidBrush _paintBrush = new(Color.White);
        private readonly Pen _paintPen = new(Color.White, 1.0f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        private readonly GraphicsPath _shapePath = new();

        public float DpiScale => _dpiScale;
        public Graphics G => _g;
        public float TextScaleMultiplier { get; set; } = 1.0f;
        public float Opacity { get; set; } = 1.0f;

        private Color ApplyOpacity(Color color)
        {
            float opacity = Math.Clamp(Opacity, 0.0f, 1.0f);
            return opacity >= 0.999f ? color : Color.FromArgb((int)MathF.Round(color.A * opacity), color.R, color.G, color.B);
        }

        public bool BeginLayeredFrame(int widthPx, int heightPx, float dpiScale)
        {
            if (widthPx <= 4 || heightPx <= 4 || widthPx > 4096 || heightPx > 4096) return false;
            _dpiScale = Math.Clamp(dpiScale, 1.0f, 4.0f);

            try
            {
                if (_memDc == IntPtr.Zero || widthPx != _surfaceW || heightPx != _surfaceH || _dibBits == IntPtr.Zero)
                {
                    ReleaseSurface();

                    IntPtr screenDc = Win32Layered.GetDC(IntPtr.Zero);
                    if (screenDc == IntPtr.Zero) return false;
                    _memDc = Win32Layered.CreateCompatibleDC(screenDc);

                    var bmi = new Win32Layered.BITMAPINFO();
                    bmi.bmiHeader.biSize = Marshal.SizeOf<Win32Layered.BITMAPINFOHEADER>();
                    bmi.bmiHeader.biWidth = widthPx;
                    bmi.bmiHeader.biHeight = -heightPx; // Top-down DIB
                    bmi.bmiHeader.biPlanes = 1;
                    bmi.bmiHeader.biBitCount = 32;
                    bmi.bmiHeader.biCompression = 0; // BI_RGB

                    _dibBitmap = Win32Layered.CreateDIBSection(screenDc, ref bmi, 0, out _dibBits, IntPtr.Zero, 0);
                    Win32Layered.ReleaseDC(IntPtr.Zero, screenDc);

                    if (_dibBitmap == IntPtr.Zero || _dibBits == IntPtr.Zero)
                    {
                        ReleaseSurface();
                        return false;
                    }

                    _oldBitmap = Win32Layered.SelectObject(_memDc, _dibBitmap);
                    _managedSurface = new Bitmap(widthPx, heightPx, widthPx * 4, PixelFormat.Format32bppPArgb, _dibBits);
                    _g = Graphics.FromImage(_managedSurface);
                    _surfaceW = widthPx;
                    _surfaceH = heightPx;
                }

                _g.ResetTransform();
                _g.ResetClip();
                _g.Clear(Color.Transparent);
                _g.ScaleTransform(_dpiScale, _dpiScale);
                _g.SmoothingMode = SmoothingMode.AntiAlias;
                _g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                // ClearType cannot blend against a per-pixel-alpha layered surface:
                // its subpixel filter leaves coloured fringes. AntiAlias keeps the
                // glyph edges whole-pixel and neutral.
                _g.TextRenderingHint = TextRenderingHint.AntiAlias;
                _g.InterpolationMode = InterpolationMode.Bilinear;
                return true;
            }
            catch
            {
                return false;
            }
        }

        // Copies the current layered surface to a PNG (test/preview hook).
        public bool SaveSurfacePng(string path)
        {
            try
            {
                if (_managedSurface == null || _surfaceW <= 0 || _surfaceH <= 0) return false;
                using var copy = new Bitmap(_surfaceW, _surfaceH, PixelFormat.Format32bppPArgb);
                using (var g = Graphics.FromImage(copy))
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.DrawImageUnscaled(_managedSurface, 0, 0);
                }
                copy.Save(path, ImageFormat.Png);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void EndLayeredFrame(IntPtr hwnd, Point windowScreenPos, byte globalAlpha = 255)
        {
            if (_g == null || _memDc == IntPtr.Zero) return;
            try
            {
                _g.Flush(FlushIntention.Flush);
                if (hwnd != IntPtr.Zero && Win32Layered.IsWindow(hwnd))
                {
                    IntPtr screenDc = Win32Layered.GetDC(IntPtr.Zero);
                    if (screenDc != IntPtr.Zero)
                    {
                        var size = new Win32Layered.SIZE { cx = _surfaceW, cy = _surfaceH };
                        var srcPt = new Win32Layered.POINT { x = 0, y = 0 };
                        var dstPt = new Win32Layered.POINT { x = windowScreenPos.X, y = windowScreenPos.Y };
                        var blend = new Win32Layered.BLENDFUNCTION
                        {
                            BlendOp = 0, // AC_SRC_OVER
                            BlendFlags = 0,
                            SourceConstantAlpha = globalAlpha,
                            AlphaFormat = 1 // AC_SRC_ALPHA
                        };

                        Win32Layered.UpdateLayeredWindow(hwnd, screenDc, ref dstPt, ref size, _memDc, ref srcPt, 0, ref blend, 2);
                        Win32Layered.ReleaseDC(IntPtr.Zero, screenDc);
                    }
                }
            }
            catch { }
        }

        // ====================================================================
        // Exact Notch & Floating Island GraphicsPath Builder
        // ====================================================================
        public GraphicsPath CreateIslandOrNotchPath(
            float left, float top, float width, float height,
            float cornerRadius, float notchFactor01, EdgePlacement edge)
        {
            left   = SpringPresets.SnapDipToWholePixel(left, _dpiScale);
            top    = SpringPresets.SnapDipToWholePixel(top, _dpiScale);
            width  = Math.Max(8.0f, SpringPresets.SnapDipToWholePixel(width, _dpiScale));
            height = Math.Max(8.0f, SpringPresets.SnapDipToWholePixel(height, _dpiScale));

            var path = new GraphicsPath();
            float r = Math.Clamp(cornerRadius, 2.0f, Math.Min(width * 0.5f, height * 0.5f));
            float d = r * 2.0f;
            float n = Math.Clamp(notchFactor01, 0.0f, 1.0f);

            if (edge == EdgePlacement.TopCenter && n > 0.5f)
            {
                float flare = DipMetrics.FlareR * ((n - 0.5f) * 2.0f);
                float right = left + width;
                float bottom = top + height;

                if (flare > 1.0f)
                {
                    float fd = flare * 2.0f;
                    path.AddArc(left - fd, top, fd, fd, 270, 90);
                    path.AddArc(left, bottom - d, d, d, 180, -90);
                    path.AddArc(right - d, bottom - d, d, d, 90, -90);
                    path.AddArc(right, top, fd, fd, 180, 90);
                }
                else
                {
                    path.AddLine(left, top, left, bottom - r);
                    path.AddArc(left, bottom - d, d, d, 180, -90);
                    path.AddArc(right - d, bottom - d, d, d, 90, -90);
                    path.AddLine(right, bottom - r, right, top);
                }
                path.CloseFigure();
            }
            else
            {
                path.AddArc(left, top, d, d, 180, 90);
                path.AddArc(left + width - d, top, d, d, 270, 90);
                path.AddArc(left + width - d, top + height - d, d, d, 0, 90);
                path.AddArc(left, top + height - d, d, d, 90, 90);
                path.CloseFigure();
            }
            return path;
        }

        public GraphicsPath CreateBottomTaskbarNotchPath(
            float left, float top, float width, float height, float cornerRadius)
        {
            left   = SpringPresets.SnapDipToWholePixel(left, _dpiScale);
            top    = SpringPresets.SnapDipToWholePixel(top, _dpiScale);
            width  = Math.Max(16.0f, SpringPresets.SnapDipToWholePixel(width, _dpiScale));
            height = Math.Max(12.0f, SpringPresets.SnapDipToWholePixel(height, _dpiScale));

            var path = new GraphicsPath();
            float r = Math.Clamp(cornerRadius, 4.0f, Math.Min(width * 0.4f, height * 0.65f));
            float d = r * 2.0f;
            float flare = Math.Min(8.0f, left);
            float bottom = top + height;
            float right = left + width;

            if (flare >= 2.0f)
            {
                float fd = flare * 2.0f;
                path.AddArc(left - fd, bottom - fd, fd, fd, 90, -90);
                path.AddArc(left, top, d, d, 180, 90);
                path.AddArc(right - d, top, d, d, 270, 90);
                path.AddArc(right, bottom - fd, fd, fd, 180, -90);
            }
            else
            {
                path.AddArc(left, top, d, d, 180, 90);
                path.AddArc(right - d, top, d, d, 270, 90);
                path.AddLine(right, top + r, right, bottom);
                path.AddLine(right, bottom, left, bottom);
            }
            path.CloseFigure();
            return path;
        }

        // ====================================================================
        // Crash-Proof Liquid Glass & Candy Glass Live Blur + Lens Refraction
        // - Clamps blurRadius strictly within [0.0f, 40.0f] (never passes 0 or
        //   negative kernel sizes to the separable blur)
        // - Hashes 64 sample pixels: reuses cached glass bitmap at 0% CPU when
        //   the background behind the island is static
        // - Wrapped in try/catch: returns false on any failure so caller falls
        //   back cleanly to Black (#0B0B0C)
        // ====================================================================
        public bool RenderLiquidGlassBackdrop(
            Rectangle screenRect,
            float dipX, float dipY, float dipW, float dipH,
            float blurRadiusPx, bool isCandyGlass)
        {
            if (_g == null || dipW <= 4.0f || dipH <= 4.0f) return false;
            if (screenRect.Width <= 8 || screenRect.Height <= 8 ||
                screenRect.Width > 4096 || screenRect.Height > 4096)
            {
                return false;
            }

            try
            {
                // Clamp blur radius strictly to [0.0f, 40.0f]
                float safeBlur = Math.Clamp(blurRadiusPx, 0.0f, 40.0f);
                int capW = Math.Clamp(screenRect.Width / 2, 16, 240);
                int capH = Math.Clamp(screenRect.Height / 2, 12, 160);

                IntPtr screenDc = Win32Layered.GetDC(IntPtr.Zero);
                if (screenDc == IntPtr.Zero) return false;

                IntPtr capDc = Win32Layered.CreateCompatibleDC(screenDc);
                var bmi = new Win32Layered.BITMAPINFO();
                bmi.bmiHeader.biSize = Marshal.SizeOf<Win32Layered.BITMAPINFOHEADER>();
                bmi.bmiHeader.biWidth = capW;
                bmi.bmiHeader.biHeight = -capH;
                bmi.bmiHeader.biPlanes = 1;
                bmi.bmiHeader.biBitCount = 32;

                IntPtr capBmp = Win32Layered.CreateDIBSection(screenDc, ref bmi, 0, out IntPtr rawBits, IntPtr.Zero, 0);
                if (capBmp == IntPtr.Zero || rawBits == IntPtr.Zero)
                {
                    if (capBmp != IntPtr.Zero) Win32Layered.DeleteObject(capBmp);
                    if (capDc != IntPtr.Zero) Win32Layered.DeleteDC(capDc);
                    Win32Layered.ReleaseDC(IntPtr.Zero, screenDc);
                    return false;
                }

                IntPtr old = Win32Layered.SelectObject(capDc, capBmp);
                Win32Layered.SetStretchBltMode(capDc, 4); // HALFTONE
                bool bltOk = Win32Layered.StretchBlt(
                    capDc, 0, 0, capW, capH,
                    screenDc, screenRect.Left, screenRect.Top, screenRect.Width, screenRect.Height,
                    0x00CC0020); // SRCCOPY

                Win32Layered.SelectObject(capDc, old);
                Win32Layered.DeleteDC(capDc);
                Win32Layered.ReleaseDC(IntPtr.Zero, screenDc);

                if (!bltOk)
                {
                    Win32Layered.DeleteObject(capBmp);
                    return false;
                }

                int totalPixels = capW * capH;
                if (_capturePixels.Length != totalPixels) _capturePixels = new int[totalPixels];
                int[] srcPixels = _capturePixels;
                Marshal.Copy(rawBits, srcPixels, 0, totalPixels);
                Win32Layered.DeleteObject(capBmp);

                // 64-point FNV-1a hash check -> 0% CPU when background behind island is static!
                ulong hash = 14695981039346656037UL;
                int stride = Math.Max(1, totalPixels / 64);
                for (int i = 0; i < totalPixels; i += stride)
                {
                    hash ^= (uint)srcPixels[i];
                    hash *= 1099511628211UL;
                }
                if (isCandyGlass) hash ^= 0xCA4D1UL;

                bool cacheHit = (hash == _cachedBackdropHash &&
                                 capW == _cachedGlassW &&
                                 capH == _cachedGlassH &&
                                 MathF.Abs(safeBlur - _cachedBlurRadius) < 0.5f &&
                                 _cachedGlassBitmap != null &&
                                 _cachedBlurredPixels.Length == totalPixels);

                if (!cacheHit)
                {
                    _cachedBackdropHash = hash;
                    _cachedGlassW = capW;
                    _cachedGlassH = capH;
                    _cachedBlurRadius = safeBlur;
                    if (_cachedBlurredPixels.Length != totalPixels)
                    {
                        _cachedBlurredPixels = new int[totalPixels];
                    }

                    if (_refractedPixels.Length != totalPixels) _refractedPixels = new int[totalPixels];
                    int[] refracted = _refractedPixels;
                    float cx = capW * 0.5f;
                    float cy = capH * 0.5f;
                    float invRx = 1.0f / Math.Max(1.0f, cx);
                    float invRy = 1.0f / Math.Max(1.0f, cy);

                    // Pass 1: Exact port of Kyant0/AndroidLiquidGlass Shaders.kt:
                    // sdRoundedRect + gradSdRoundedRect (gradRadius = min(r*1.5, min(hw, hh)))
                    // + circleMap lens refraction + 7-tap spectral chromatic dispersion
                    // + subtle 45-degree directional highlight (ZERO milky white side fade).
                    float halfW = capW * 0.5f;
                    float halfH = capH * 0.5f;
                    float cornerR = MathF.Min(halfW, halfH) * 0.42f;
                    float gradRadius = MathF.Min(cornerR * 1.5f, MathF.Min(halfW, halfH));
                    float refractionHeight = MathF.Max(6.0f, MathF.Min(capW, capH) * 0.24f);
                    float refractionAmount = -MathF.Max(8.0f, MathF.Min(capW, capH) * 0.16f); // negated as in Lens.kt
                    float invHalfArea = 1.0f / MathF.Max(1.0f, halfW * halfH);
                    const float cos45 = 0.70710678f;
                    const float sin45 = 0.70710678f;

                    static float SdRoundedRect(float cxp, float cyp, float hw, float hh, float r)
                    {
                        float qx = MathF.Abs(cxp) - (hw - r);
                        float qy = MathF.Abs(cyp) - (hh - r);
                        float ox = MathF.Max(qx, 0.0f);
                        float oy = MathF.Max(qy, 0.0f);
                        float outside = MathF.Sqrt(ox * ox + oy * oy) - r;
                        float inside = MathF.Min(MathF.Max(qx, qy), 0.0f);
                        return outside + inside;
                    }

                    static (float gx, float gy) GradSdRoundedRect(float cxp, float cyp, float hw, float hh, float r)
                    {
                        float sx = cxp < 0.0f ? -1.0f : 1.0f;
                        float sy = cyp < 0.0f ? -1.0f : 1.0f;
                        float qx = MathF.Abs(cxp) - (hw - r);
                        float qy = MathF.Abs(cyp) - (hh - r);
                        if (qx >= 0.0f || qy >= 0.0f)
                        {
                            float mx = MathF.Max(qx, 0.0f);
                            float my = MathF.Max(qy, 0.0f);
                            float len = MathF.Sqrt(mx * mx + my * my);
                            if (len < 1e-4f) return (sx, 0.0f);
                            return (sx * (mx / len), sy * (my / len));
                        }
                        return qx >= qy ? (sx, 0.0f) : (0.0f, sy);
                    }

                    static (float r, float g, float b) SampleBilinear(int[] buf, int w, int h, float fx, float fy)
                    {
                        float cxp = Math.Clamp(fx, 0.0f, w - 1.001f);
                        float cyp = Math.Clamp(fy, 0.0f, h - 1.001f);
                        int x0 = (int)cxp, y0 = (int)cyp;
                        int x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
                        float tx = cxp - x0, ty = cyp - y0;
                        uint p00 = (uint)buf[y0 * w + x0];
                        uint p10 = (uint)buf[y0 * w + x1];
                        uint p01 = (uint)buf[y1 * w + x0];
                        uint p11 = (uint)buf[y1 * w + x1];
                        float w00 = (1f - tx) * (1f - ty), w10 = tx * (1f - ty), w01 = (1f - tx) * ty, w11 = tx * ty;
                        float r = (((p00 >> 16) & 0xFF) * w00 + ((p10 >> 16) & 0xFF) * w10 + ((p01 >> 16) & 0xFF) * w01 + ((p11 >> 16) & 0xFF) * w11) / 255f;
                        float g = (((p00 >> 8) & 0xFF) * w00 + ((p10 >> 8) & 0xFF) * w10 + ((p01 >> 8) & 0xFF) * w01 + ((p11 >> 8) & 0xFF) * w11) / 255f;
                        float b = ((p00 & 0xFF) * w00 + (p10 & 0xFF) * w10 + (p01 & 0xFF) * w01 + (p11 & 0xFF) * w11) / 255f;
                        return (r, g, b);
                    }

                    for (int y = 0; y < capH; ++y)
                    {
                        float py = (y + 0.5f) - halfH;
                        for (int x = 0; x < capW; ++x)
                        {
                            float pxCoord = (x + 0.5f) - halfW;
                            float sd = SdRoundedRect(pxCoord, py, halfW, halfH, cornerR);

                            float r, g, b;
                            if (-sd >= refractionHeight)
                            {
                                uint p = (uint)srcPixels[y * capW + x];
                                r = ((p >> 16) & 0xFF) / 255.0f;
                                g = ((p >> 8) & 0xFF) / 255.0f;
                                b = (p & 0xFF) / 255.0f;
                            }
                            else
                            {
                                float clampedSd = MathF.Min(sd, 0.0f);
                                float normIn = Math.Clamp(1.0f - (-clampedSd / refractionHeight), 0.0f, 1.0f);
                                // Kyant0 circleMap(x) = 1.0 - sqrt(1.0 - x*x)
                                float d = (1.0f - MathF.Sqrt(MathF.Max(0.0f, 1.0f - normIn * normIn))) * refractionAmount;
                                var (gx, gy) = GradSdRoundedRect(pxCoord, py, halfW, halfH, gradRadius);
                                float refX = x + d * gx;
                                float refY = y + d * gy;

                                // Kyant0 7-tap spectral chromatic dispersion (curves at corners, zero side haze)
                                float dispInt = (pxCoord * py) * invHalfArea;
                                float dxD = d * gx * dispInt;
                                float dyD = d * gy * dispInt;
                                if (MathF.Abs(dxD) + MathF.Abs(dyD) > 0.12f)
                                {
                                    var cRed    = SampleBilinear(srcPixels, capW, capH, refX + dxD,             refY + dyD);
                                    var cOrange = SampleBilinear(srcPixels, capW, capH, refX + dxD * (2f/3f),   refY + dyD * (2f/3f));
                                    var cYellow = SampleBilinear(srcPixels, capW, capH, refX + dxD * (1f/3f),   refY + dyD * (1f/3f));
                                    var cGreen  = SampleBilinear(srcPixels, capW, capH, refX,                   refY);
                                    var cCyan   = SampleBilinear(srcPixels, capW, capH, refX - dxD * (1f/3f),   refY - dyD * (1f/3f));
                                    var cBlue   = SampleBilinear(srcPixels, capW, capH, refX - dxD * (2f/3f),   refY - dyD * (2f/3f));
                                    var cPurple = SampleBilinear(srcPixels, capW, capH, refX - dxD,             refY - dyD);
                                    r = (cRed.r + cOrange.r + cYellow.r) / 3.5f + cPurple.r / 7.0f;
                                    g = cOrange.g / 7.0f + (cYellow.g + cGreen.g + cCyan.g) / 3.5f;
                                    b = (cCyan.b + cBlue.b + cPurple.b) / 3.0f;
                                }
                                else
                                {
                                    (r, g, b) = SampleBilinear(srcPixels, capW, capH, refX, refY);
                                }
                            }

                            // Subtle vibrancy boost matching real optical glass
                            float luma = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                            r = Math.Clamp(luma + (r - luma) * 1.32f, 0.0f, 1.0f);
                            g = Math.Clamp(luma + (g - luma) * 1.32f, 0.0f, 1.0f);
                            b = Math.Clamp(luma + (b - luma) * 1.32f, 0.0f, 1.0f);

                            if (isCandyGlass)
                            {
                                float grad = (float)x / Math.Max(1, capW);
                                r = r * 0.62f + (0.28f * (1.0f - grad) + 0.16f * grad);
                                g = g * 0.62f + (0.12f * (1.0f - grad) + 0.22f * grad);
                                b = b * 0.62f + (0.26f * (1.0f - grad) + 0.34f * grad);
                            }
                            else
                            {
                                // Clean dark-smoked glass readability tint + delicate 1.5px 45-deg edge specular ONLY
                                float edgeRim = (-sd < 1.8f) ? (1.0f - Math.Clamp(-sd / 1.8f, 0.0f, 1.0f)) : 0.0f;
                                float spec = 0.0f;
                                if (edgeRim > 0.01f)
                                {
                                    var (hx, hy) = GradSdRoundedRect(pxCoord, py, halfW, halfH, gradRadius);
                                    spec = MathF.Abs(hx * cos45 + hy * sin45) * edgeRim * 0.16f;
                                }
                                r = Math.Clamp(r * 0.68f + 0.03f + spec, 0.0f, 1.0f);
                                g = Math.Clamp(g * 0.68f + 0.032f + spec, 0.0f, 1.0f);
                                b = Math.Clamp(b * 0.70f + 0.042f + spec, 0.0f, 1.0f);
                            }

                            int rb = Math.Clamp((int)(r * 255.0f), 0, 255);
                            int gb = Math.Clamp((int)(g * 255.0f), 0, 255);
                            int bb = Math.Clamp((int)(b * 255.0f), 0, 255);
                            refracted[y * capW + x] = unchecked((int)(0xFF000000u | ((uint)rb << 16) | ((uint)gb << 8) | (uint)bb));
                        }
                    }

                    // Pass 2: 3-pass Separable Sliding-Window Gaussian Blur (true frosted glass)
                    int kernelRadius = Math.Clamp((int)MathF.Round(safeBlur * 0.55f), 0, 22);
                    if (kernelRadius <= 0)
                    {
                        Array.Copy(refracted, _cachedBlurredPixels, totalPixels);
                    }
                    else
                    {
                        if (_blurTempPixels.Length != totalPixels) _blurTempPixels = new int[totalPixels];
                        int[] temp = _blurTempPixels;
                        int[] source = refracted;
                        for (int iter = 0; iter < 3; ++iter)
                        {
                            BoxBlurHorizontal(source, temp, capW, capH, kernelRadius);
                            BoxBlurVertical(temp, _cachedBlurredPixels, capW, capH, kernelRadius);
                            source = _cachedBlurredPixels;
                        }
                    }

                    if (_cachedGlassBitmap == null || _cachedGlassBitmap.Width != capW || _cachedGlassBitmap.Height != capH)
                    {
                        _cachedGlassBitmap?.Dispose();
                        _cachedGlassBitmap = new Bitmap(capW, capH, PixelFormat.Format32bppPArgb);
                    }

                    BitmapData bd = _cachedGlassBitmap.LockBits(
                        new Rectangle(0, 0, capW, capH), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
                    Marshal.Copy(_cachedBlurredPixels, 0, bd.Scan0, totalPixels);
                    _cachedGlassBitmap.UnlockBits(bd);

                    double lumaSum = 0, lumaSqSum = 0;
                    int samples = 0;
                    for (int i = 0; i < totalPixels; i += 17)
                    {
                        uint px = (uint)_cachedBlurredPixels[i];
                        double luma = 0.2126 * ((px >> 16) & 0xFF) + 0.7152 * ((px >> 8) & 0xFF) + 0.0722 * (px & 0xFF);
                        lumaSum += luma;
                        lumaSqSum += luma * luma;
                        ++samples;
                    }
                    if (samples > 0)
                    {
                        double mean = lumaSum / samples;
                        BackdropVariance = Math.Max(0.0, lumaSqSum / samples - mean * mean);
                    }

                    ulong outHash = 14695981039346656037UL;
                    for (int i = 0; i < totalPixels; i += 13)
                    {
                        outHash ^= (uint)_cachedBlurredPixels[i];
                        outHash *= 1099511628211UL;
                    }
                    BackdropOutputHash = outHash;
                }

                if (_cachedGlassBitmap != null)
                {
                    var prevInterp = _g.InterpolationMode;
                    _g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    _g.DrawImage(_cachedGlassBitmap, new RectangleF(dipX, dipY, dipW, dipH));
                    _g.InterpolationMode = prevInterp;
                    return true;
                }
            }
            catch
            {
                // Graceful fallback to Black (#0B0B0C) on any capture or GDI/GPU error
            }
            return false;
        }

        // ====================================================================
        // Soft Themes Particle Ambience (Rosé, Lavender, Peach, Sakura)
        // ====================================================================
        public void RenderThemeAmbience(SurfaceTheme theme, float left, float top, float width, float height, float timeSec, float intensity = 1.0f)
        {
            if (_g == null) return;
            intensity = Math.Clamp(intensity, 0.0f, 1.0f);
            if (intensity <= 0.02f) return;
            if (theme == SurfaceTheme.Rose)
            {
                for (int i = 0; i < 9; ++i)
                {
                    float seed = i * 1.37f;
                    float phase = (timeSec * (0.14f + (i % 3) * 0.04f) + seed) % 1.0f;
                    float hx = left + width * (0.10f + 0.80f * ((i * 0.6180339f) % 1.0f)) + MathF.Sin(timeSec * 1.4f + seed) * 5.0f;
                    float hy = top + height * (1.05f - phase * 1.10f);
                    int alpha = Math.Clamp((int)(MathF.Sin(phase * MathF.PI) * 110.0f * intensity), 0, 255);
                    if (alpha <= 1) continue;
                    FillCircle(hx, hy, 3.2f + (i % 3), Color.FromArgb(alpha, 255, 143, 177));
                }
            }
            else if (theme == SurfaceTheme.Lavender)
            {
                for (int i = 0; i < 16; ++i)
                {
                    float sx = left + width * (0.06f + 0.88f * ((i * 0.754877f) % 1.0f));
                    float sy = top + height * (0.12f + 0.78f * ((i * 0.569840f) % 1.0f));
                    float twinkle = 0.5f + 0.5f * MathF.Sin(timeSec * (2.2f + (i % 4) * 0.6f) + i * 1.9f);
                    int alpha = Math.Clamp((int)((0.12f + twinkle * 0.48f) * 255.0f * intensity), 0, 255);
                    if (alpha <= 1) continue;
                    float r = 2.0f + twinkle * 2.2f;
                    Color col = (i % 2 == 0) ? Color.FromArgb(alpha, 239, 237, 232) : Color.FromArgb(alpha, 195, 166, 255);
                    DrawLine(sx - r, sy, sx + r, sy, col, 1.2f);
                    DrawLine(sx, sy - r, sx, sy + r, col, 1.2f);
                }
            }
            else if (theme == SurfaceTheme.Peach)
            {
                for (int i = 0; i < 7; ++i)
                {
                    float seed = i * 2.1f;
                    float ox = left + width * (0.12f + 0.76f * ((i * 0.618f) % 1.0f)) + MathF.Sin(timeSec * 0.6f + seed) * 14.0f;
                    float oy = top + height * (0.20f + 0.60f * ((i * 0.414f) % 1.0f)) + MathF.Cos(timeSec * 0.5f + seed) * 9.0f;
                    float rad = 14.0f + (i % 3) * 8.0f;
                    Color col = (i % 2 == 0)
                        ? Color.FromArgb((int)(30 * intensity), 255, 179, 138)
                        : Color.FromArgb((int)(25 * intensity), 255, 214, 168);
                    FillCircle(ox, oy, rad, col);
                }
            }
            else if (theme == SurfaceTheme.Sakura)
            {
                for (int i = 0; i < 11; ++i)
                {
                    float seed = i * 1.19f;
                    float phase = (timeSec * (0.12f + (i % 4) * 0.03f) + seed) % 1.0f;
                    float px = left + width * (((i * 0.618f) + phase * 0.25f) % 1.0f) + MathF.Sin(timeSec * 1.5f + seed) * 6.0f;
                    float py = top + height * phase;
                    int alpha = Math.Clamp((int)(MathF.Sin(phase * MathF.PI) * 112.0f * intensity), 0, 255);
                    if (alpha <= 1) continue;
                    FillRoundedRect(new RectangleF(px - 3.5f, py - 2.0f, 7.0f, 4.0f), 2.0f, Color.FromArgb(alpha, 255, 196, 210));
                }
            }
        }

        // No session, no backdrop: the guard sits on the entry point so no caller
        // can paint an album gradient from empty media state.
        // The hero demo's music backdrop (IslandRenderer.Music.cs): the cover squeezed
        // Cached dominant + secondary vibrant colors extracted from the active album art.
        private Image _albumColorSource;
        private Color _albumDomColor = Color.FromArgb(255, 34, 62, 92);
        private Color _albumSecColor = Color.FromArgb(255, 14, 18, 26);

        private static (Color Dom, Color Sec) ExtractDominantVibrantColor(Image img, Color fallbackDom, Color fallbackSec)
        {
            if (img == null) return (fallbackDom, fallbackSec);
            try
            {
                using var thumb = new Bitmap(24, 24);
                using (var sg = Graphics.FromImage(thumb))
                {
                    sg.InterpolationMode = InterpolationMode.Bilinear;
                    sg.DrawImage(img, 0, 0, 24, 24);
                }

                // 12-hue-bucket weighted vote favoring saturated, mid-luminance pixels
                double[] rSum = new double[12], gSum = new double[12], bSum = new double[12], wSum = new double[12];
                double allR = 0, allG = 0, allB = 0, allW = 0;

                for (int y = 2; y < 22; y++)
                {
                    for (int x = 2; x < 22; x++)
                    {
                        Color c = thumb.GetPixel(x, y);
                        float rf = c.R / 255f, gf = c.G / 255f, bf = c.B / 255f;
                        float max = MathF.Max(rf, MathF.Max(gf, bf));
                        float min = MathF.Min(rf, MathF.Min(gf, bf));
                        float delta = max - min;
                        float lum = (max + min) * 0.5f;
                        float sat = delta < 0.001f ? 0f : delta / (1f - MathF.Abs(2f * lum - 1f));

                        double baseW = 0.25 + sat * sat * 3.0;
                        if (lum < 0.10f || lum > 0.92f) baseW *= 0.25;
                        allR += c.R * baseW; allG += c.G * baseW; allB += c.B * baseW; allW += baseW;

                        if (sat > 0.14f && lum >= 0.12f && lum <= 0.88f)
                        {
                            float hue;
                            if (max == rf) hue = ((gf - bf) / delta) % 6f;
                            else if (max == gf) hue = (bf - rf) / delta + 2f;
                            else hue = (rf - gf) / delta + 4f;
                            hue *= 60f;
                            if (hue < 0f) hue += 360f;
                            int bucket = Math.Clamp((int)(hue / 30f), 0, 11);
                            double w = sat * sat * (1.0 - Math.Abs(lum - 0.5) * 0.7);
                            rSum[bucket] += c.R * w;
                            gSum[bucket] += c.G * w;
                            bSum[bucket] += c.B * w;
                            wSum[bucket] += w;
                        }
                    }
                }

                int best = -1;
                double bestW = 0;
                for (int i = 0; i < 12; i++)
                {
                    if (wSum[i] > bestW) { bestW = wSum[i]; best = i; }
                }

                Color dom;
                if (best >= 0 && bestW > 0.05)
                {
                    dom = Color.FromArgb(255,
                        Math.Clamp((int)(rSum[best] / bestW), 0, 255),
                        Math.Clamp((int)(gSum[best] / bestW), 0, 255),
                        Math.Clamp((int)(bSum[best] / bestW), 0, 255));
                }
                else if (allW > 0.01)
                {
                    dom = Color.FromArgb(255,
                        Math.Clamp((int)(allR / allW), 0, 255),
                        Math.Clamp((int)(allG / allW), 0, 255),
                        Math.Clamp((int)(allB / allW), 0, 255));
                }
                else
                {
                    dom = fallbackDom;
                }

                // Deepened companion shade so the wash melts smoothly into the obsidian card
                Color sec = Color.FromArgb(255,
                    Math.Clamp((int)(dom.R * 0.24f) + 6, 8, 64),
                    Math.Clamp((int)(dom.G * 0.24f) + 6, 8, 64),
                    Math.Clamp((int)(dom.B * 0.24f) + 8, 10, 72));
                return (dom, sec);
            }
            catch
            {
                return (fallbackDom, fallbackSec);
            }
        }

        public void RenderAlbumBackdrop(
            MediaSessionState media, float left, float top, float width, float height, float bgK = 1.0f)
        {
            if (_g == null || media == null || !media.HasSession) return;
            float a = Math.Clamp(bgK, 0.0f, 1.0f);
            if (a <= 0.003f || width <= 2 || height <= 2) return;

            if (media.AlbumArt != null)
            {
                if (!ReferenceEquals(_albumColorSource, media.AlbumArt))
                {
                    var (d, s) = ExtractDominantVibrantColor(media.AlbumArt, media.DominantColor, media.SecondaryColor);
                    _albumDomColor = d;
                    _albumSecColor = s;
                    _albumColorSource = media.AlbumArt;
                }
            }
            else
            {
                _albumColorSource = null;
                _albumDomColor = media.DominantColor;
                _albumSecColor = media.SecondaryColor;
            }

            // Smooth dominant-color ambient wash (no stretched artwork image)
            Color dom = _albumDomColor;
            Color sec = _albumSecColor;
            var washRect = new RectangleF(left - 12, top - 8, width + 24, height + 16);
            using (var wash = new LinearGradientBrush(
                washRect,
                Color.FromArgb((int)(195 * a), dom.R, dom.G, dom.B),
                Color.FromArgb((int)(215 * a), sec.R, sec.G, sec.B),
                22.0f))
            {
                var blend = new ColorBlend(3)
                {
                    Colors = new[]
                    {
                        Color.FromArgb((int)(195 * a), dom.R, dom.G, dom.B),
                        Color.FromArgb((int)(155 * a), (dom.R + sec.R) / 2, (dom.G + sec.G) / 2, (dom.B + sec.B) / 2),
                        Color.FromArgb((int)(225 * a), 11, 11, 13)
                    },
                    Positions = new[] { 0.0f, 0.52f, 1.0f }
                };
                wash.InterpolationColors = blend;
                _g.FillRectangle(wash, washRect);
            }

            // Balanced dark scrims so the whole card stays richly colored while text stays crisp
            using (var across = new LinearGradientBrush(
                new RectangleF(left - 12, top - 6, width + 24, height + 6), Color.Black, Color.Black, 0f))
            {
                across.InterpolationColors = new ColorBlend
                {
                    Colors = new[] { Color.FromArgb((int)(135 * a), 0, 0, 0), Color.FromArgb((int)(68 * a), 0, 0, 0), Color.FromArgb((int)(110 * a), 0, 0, 0) },
                    Positions = new[] { 0f, 0.45f, 1f }
                };
                _g.FillRectangle(across, left - 12, top - 6, width + 24, height + 6);
            }
            using (var up = new LinearGradientBrush(
                new RectangleF(left - 12, top + height * 0.55f, width + 24, height * 0.45f + 6), Color.Black, Color.Black, 90f))
            {
                up.InterpolationColors = new ColorBlend
                {
                    Colors = new[] { Color.FromArgb(0, 0, 0, 0), Color.FromArgb((int)(132 * a), 0, 0, 0) },
                    Positions = new[] { 0f, 1f }
                };
                _g.FillRectangle(up, left - 12, top + height * 0.55f, width + 24, height * 0.45f + 6);
            }
        }

        // ====================================================================
        // Whole-Pixel Snapped Primitives & Typography
        // ====================================================================
        public void FillRoundedRect(RectangleF r, float radius, Color color)
        {
            if (_g == null || r.Width <= 0.5f || r.Height <= 0.5f) return;
            GraphicsPath path = BuildRoundedRectPath(r, radius);
            _paintBrush.Color = ApplyOpacity(color);
            _g.FillPath(_paintBrush, path);
        }

        public void DrawRoundedRect(RectangleF r, float radius, Color color, float strokeWidth = 1.0f)
        {
            if (_g == null || r.Width <= 0.5f || r.Height <= 0.5f) return;
            GraphicsPath path = BuildRoundedRectPath(r, radius);
            _paintPen.Color = ApplyOpacity(color);
            _paintPen.Width = strokeWidth;
            _g.DrawPath(_paintPen, path);
        }

        // Builds into the shared path (reset every call). Callers must finish drawing
        // before invoking another shape primitive - they all do, one shape at a time.
        private GraphicsPath BuildRoundedRectPath(RectangleF r, float radius)
        {
            float x = SpringPresets.SnapDipToWholePixel(r.X, _dpiScale);
            float y = SpringPresets.SnapDipToWholePixel(r.Y, _dpiScale);
            float w = Math.Max(1.0f, SpringPresets.SnapDipToWholePixel(r.Width, _dpiScale));
            float h = Math.Max(1.0f, SpringPresets.SnapDipToWholePixel(r.Height, _dpiScale));
            float rad = Math.Clamp(radius, 0.5f, Math.Min(w * 0.5f, h * 0.5f));
            float d = rad * 2.0f;

            GraphicsPath path = _shapePath;
            path.Reset();
            path.AddArc(x, y, d, d, 180, 90);
            path.AddArc(x + w - d, y, d, d, 270, 90);
            path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            path.AddArc(x, y + h - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public void FillCircle(float cx, float cy, float radius, Color color)
        {
            if (_g == null || radius <= 0.2f) return;
            _paintBrush.Color = ApplyOpacity(color);
            _g.FillEllipse(_paintBrush, cx - radius, cy - radius, radius * 2.0f, radius * 2.0f);
        }

        // Idle indicator: three dots that breathe and morph between circle,
        // squircle and diamond, exactly the kind of motion the site's HUD uses.
        public void DrawMorphingDots(float rightCenterX, float cy, float timeSec, Color color, int count = 3, float spacing = 6.4f)
        {
            if (_g == null || count <= 0) return;
            float startX = rightCenterX - spacing * (count - 1);
            for (int i = 0; i < count; ++i)
            {
                float phase = (timeSec * 0.9f + i * 0.33f) % 1.0f;
                float breathe = 0.5f - 0.5f * MathF.Cos(phase * MathF.PI * 2.0f);
                float shape = (timeSec * 0.55f + i * 0.21f) % 1.0f; // 0..1 morph cycle
                float radius = 2.0f + breathe * 1.1f;
                float x = startX + i * spacing;
                float y = cy + MathF.Sin(phase * MathF.PI * 2.0f) * 1.2f;
                int alpha = 120 + (int)(breathe * 110.0f);
                Color dot = Color.FromArgb(Math.Clamp(alpha, 0, 255), color.R, color.G, color.B);

                if (shape < 0.33f)
                {
                    FillCircle(x, y, radius, dot);
                }
                else if (shape < 0.66f)
                {
                    float roundness = 0.55f + 0.35f * breathe;
                    FillRoundedRect(new RectangleF(x - radius, y - radius, radius * 2.0f, radius * 2.0f), radius * roundness, dot);
                }
                else
                {
                    // Diamond: square rotated through four points
                    var p = new PointF[]
                    {
                        new PointF(x, y - radius * 1.05f),
                        new PointF(x + radius * 1.05f, y),
                        new PointF(x, y + radius * 1.05f),
                        new PointF(x - radius * 1.05f, y)
                    };
                    _paintBrush.Color = ApplyOpacity(dot);
                    _g.FillPolygon(_paintBrush, p);
                }
            }
        }

        public void DrawCircle(float cx, float cy, float radius, Color color, float strokeWidth = 1.5f)
        {
            if (_g == null || radius <= 0.2f) return;
            _paintPen.Color = ApplyOpacity(color);
            _paintPen.Width = strokeWidth;
            _g.DrawEllipse(_paintPen, cx - radius, cy - radius, radius * 2.0f, radius * 2.0f);
        }

        public void DrawLine(float x1, float y1, float x2, float y2, Color color, float strokeWidth = 1.5f)
        {
            if (_g == null) return;
            _paintPen.Color = ApplyOpacity(color);
            _paintPen.Width = strokeWidth;
            _g.DrawLine(_paintPen,
                SpringPresets.SnapDipToWholePixel(x1, _dpiScale),
                SpringPresets.SnapDipToWholePixel(y1, _dpiScale),
                SpringPresets.SnapDipToWholePixel(x2, _dpiScale),
                SpringPresets.SnapDipToWholePixel(y2, _dpiScale));
        }

        public void DrawTextStr(
            string text, RectangleF rect, float fontSizeDip, bool bold, Color color,
            bool mono = false, StringAlignment hAlign = StringAlignment.Near,
            StringAlignment vAlign = StringAlignment.Center, bool noWrap = true)
        {
            if (_g == null || string.IsNullOrEmpty(text) || rect.Width <= 1 || rect.Height <= 1) return;
            rect = new RectangleF(
                SpringPresets.SnapDipToWholePixel(rect.X, _dpiScale),
                SpringPresets.SnapDipToWholePixel(rect.Y, _dpiScale),
                Math.Max(2.0f, SpringPresets.SnapDipToWholePixel(rect.Width, _dpiScale)),
                Math.Max(2.0f, SpringPresets.SnapDipToWholePixel(rect.Height, _dpiScale)));
            float scale = Math.Clamp(GlobalAppState.Instance.Settings.TextScale, 0.85f, 1.5f);
            // fontSizeDip is DIPs: at 96 DPI one DIP is one pixel, so convert through the
            // surface's real DPI. Without this, text renders 25% larger on a 125% display
            // and every label overflows the DIP box it was measured against.
            float surfaceDpi = _g.DpiY > 1.0f ? _g.DpiY : 96.0f;
            // Geist is drawn at exactly its DIP size (0.75 pt per DIP); Inter keeps the
            // 0.78 bump that compensates for its smaller x-height.
            float dipToPoints = UiFonts.IsGeist ? 0.75f : 0.78f;
            float sz = Math.Max(6.0f, fontSizeDip * scale * Math.Clamp(TextScaleMultiplier, 0.65f, 2.1f) * dipToPoints * (96.0f / surfaceDpi));

            // Inter everywhere (bundled inside the assembly), rounded to 0.1 pt so
            // tiny float jitter cannot fill the cache with fonts.
            var fontKey = (Mono: mono, Size: MathF.Round(sz * 10.0f) / 10.0f, Bold: bold);
            if (!_fontCache.TryGetValue(fontKey, out Font font))
            {
                if (_fontCache.Count >= 96)
                {
                    foreach (Font cached in _fontCache.Values) cached.Dispose();
                    _fontCache.Clear();
                }
                font = UiFonts.Get(fontKey.Size, bold, mono);
                _fontCache[fontKey] = font;
            }

            var formatKey = (H: hAlign, V: vAlign, NoWrap: noWrap);
            if (!_formatCache.TryGetValue(formatKey, out StringFormat sf))
            {
                sf = new StringFormat
                {
                    Alignment = hAlign,
                    LineAlignment = vAlign,
                    Trimming = StringTrimming.EllipsisCharacter,
                    FormatFlags = noWrap ? StringFormatFlags.NoWrap : 0
                };
                _formatCache[formatKey] = sf;
            }

            _paintBrush.Color = ApplyOpacity(color);
            _g.DrawString(text, font, _paintBrush, rect, sf);
        }

        // Wide letter-spaced uppercase typography (day name, date, "- 11:30 AM -").
        // Each glyph is placed individually because GDI+ has no tracking support.
        public void DrawTrackedText(
            string text, RectangleF rect, float fontSizeDip, bool bold, Color color,
            float trackingDip, StringAlignment hAlign = StringAlignment.Center, bool mono = true)
        {
            if (_g == null || string.IsNullOrEmpty(text) || rect.Width <= 1 || rect.Height <= 1) return;
            float scale = Math.Clamp(GlobalAppState.Instance.Settings.TextScale, 0.85f, 1.5f);
            // fontSizeDip is DIPs: at 96 DPI one DIP is one pixel, so convert through the
            // surface's real DPI. Without this, text renders 25% larger on a 125% display
            // and every label overflows the DIP box it was measured against.
            float surfaceDpi = _g.DpiY > 1.0f ? _g.DpiY : 96.0f;
            // Geist is drawn at exactly its DIP size (0.75 pt per DIP); Inter keeps the
            // 0.78 bump that compensates for its smaller x-height.
            float dipToPoints = UiFonts.IsGeist ? 0.75f : 0.78f;
            float sz = Math.Max(6.0f, fontSizeDip * scale * Math.Clamp(TextScaleMultiplier, 0.65f, 2.1f) * dipToPoints * (96.0f / surfaceDpi));
            float tracking = trackingDip * scale;

            var fontKey = (Mono: mono, Size: MathF.Round(sz * 10.0f) / 10.0f, Bold: bold);
            if (!_fontCache.TryGetValue(fontKey, out Font font))
            {
                if (_fontCache.Count >= 96)
                {
                    foreach (Font cached in _fontCache.Values) cached.Dispose();
                    _fontCache.Clear();
                }
                font = UiFonts.Get(fontKey.Size, bold, mono);
                _fontCache[fontKey] = font;
            }

            StringFormat sf = StringFormat.GenericTypographic;
            float total = 0.0f;
            // GenericTypographic trims trailing whitespace, so a space would
            // measure as zero and collapse tracked text ('21JANUARY,2023.').
            float spaceWidth = Math.Max(1.0f,
                _g.MeasureString("i i", font, PointF.Empty, sf).Width -
                2.0f * _g.MeasureString("i", font, PointF.Empty, sf).Width);
            var widths = new float[text.Length];
            for (int i = 0; i < text.Length; ++i)
            {
                float glyph = char.IsWhiteSpace(text[i])
                    ? spaceWidth
                    : _g.MeasureString(text[i].ToString(), font, PointF.Empty, sf).Width;
                widths[i] = glyph + tracking;
                total += widths[i];
            }
            if (text.Length > 0) total -= tracking;

            float x = rect.Left + (hAlign switch
            {
                StringAlignment.Center => (rect.Width - total) * 0.5f,
                StringAlignment.Far => rect.Width - total,
                _ => 0.0f
            });
            float lineHeight = _g.MeasureString("Ag", font, PointF.Empty, sf).Height;
            float y = rect.Top + (rect.Height - lineHeight) * 0.5f;

            _paintBrush.Color = ApplyOpacity(color);
            for (int i = 0; i < text.Length; ++i)
            {
                if (text[i] != ' ') _g.DrawString(text[i].ToString(), font, _paintBrush, new PointF(x, y), sf);
                x += widths[i];
            }
        }

        public void DrawLucideIcon(string iconName, float cx, float cy, float sizeDip, Color color, float strokeWidth = 1.75f)
        {
            if (_g == null) return;
            cx = SpringPresets.SnapDipToWholePixel(cx, _dpiScale);
            cy = SpringPresets.SnapDipToWholePixel(cy, _dpiScale);
            float r = sizeDip * 0.5f;
            switch (iconName)
            {
                case "search":
                    DrawCircle(cx - r * 0.12f, cy - r * 0.12f, r * 0.48f, color, strokeWidth);
                    DrawLine(cx + r * 0.22f, cy + r * 0.22f, cx + r * 0.70f, cy + r * 0.70f, color, strokeWidth);
                    break;
                case "music":
                    DrawLine(cx - r * 0.4f, cy + r * 0.4f, cx - r * 0.4f, cy - r * 0.6f, color, strokeWidth);
                    DrawLine(cx + r * 0.55f, cy + r * 0.25f, cx + r * 0.55f, cy - r * 0.75f, color, strokeWidth);
                    DrawLine(cx - r * 0.4f, cy - r * 0.6f, cx + r * 0.55f, cy - r * 0.75f, color, strokeWidth);
                    FillCircle(cx - r * 0.55f, cy + r * 0.45f, r * 0.25f, color);
                    FillCircle(cx + r * 0.36f, cy + r * 0.30f, r * 0.25f, color);
                    break;
                case "globe":
                    DrawCircle(cx, cy, r * 0.80f, color, strokeWidth);
                    DrawLine(cx - r * 0.78f, cy, cx + r * 0.78f, cy, color, strokeWidth * 0.85f);
                    DrawLine(cx, cy - r * 0.78f, cx, cy + r * 0.78f, color, strokeWidth * 0.85f);
                    break;
                case "bell":
                    DrawRoundedRect(new RectangleF(cx - r * 0.55f, cy - r * 0.65f, r * 1.10f, r * 1.0f), r * 0.45f, color, strokeWidth);
                    DrawLine(cx - r * 0.75f, cy + r * 0.35f, cx + r * 0.75f, cy + r * 0.35f, color, strokeWidth);
                    FillCircle(cx, cy + r * 0.68f, r * 0.16f, color);
                    break;
                case "message-circle":
                    DrawCircle(cx, cy - r * 0.05f, r * 0.78f, color, strokeWidth);
                    DrawLine(cx - r * 0.35f, cy + r * 0.65f, cx - r * 0.72f, cy + r * 0.82f, color, strokeWidth);
                    break;
                case "folder":
                    DrawRoundedRect(new RectangleF(cx - r * 0.80f, cy - r * 0.40f, r * 1.60f, r * 1.10f), 2.5f, color, strokeWidth);
                    DrawLine(cx - r * 0.78f, cy - r * 0.62f, cx - r * 0.15f, cy - r * 0.62f, color, strokeWidth);
                    break;
                case "clipboard":
                    DrawRoundedRect(new RectangleF(cx - r * 0.60f, cy - r * 0.60f, r * 1.20f, r * 1.40f), 2.5f, color, strokeWidth);
                    FillRoundedRect(new RectangleF(cx - r * 0.30f, cy - r * 0.80f, r * 0.60f, r * 0.35f), 1.5f, color);
                    break;
                case "layout-grid":
                    float s = r * 0.66f, g = r * 0.14f;
                    DrawRoundedRect(new RectangleF(cx - s - g, cy - s - g, s, s), 2.0f, color, strokeWidth);
                    DrawRoundedRect(new RectangleF(cx + g, cy - s - g, s, s), 2.0f, color, strokeWidth);
                    DrawRoundedRect(new RectangleF(cx - s - g, cy + g, s, s), 2.0f, color, strokeWidth);
                    DrawRoundedRect(new RectangleF(cx + g, cy + g, s, s), 2.0f, color, strokeWidth);
                    break;
                case "sliders-horizontal":
                    DrawLine(cx - r * 0.80f, cy - r * 0.45f, cx + r * 0.80f, cy - r * 0.45f, color, strokeWidth);
                    FillCircle(cx - r * 0.25f, cy - r * 0.45f, r * 0.22f, color);
                    DrawLine(cx - r * 0.80f, cy + r * 0.45f, cx + r * 0.80f, cy + r * 0.45f, color, strokeWidth);
                    FillCircle(cx + r * 0.30f, cy + r * 0.45f, r * 0.22f, color);
                    break;
                case "grip-horizontal":
                    for (int row = -1; row <= 1; row += 2)
                        for (int col = -1; col <= 1; ++col)
                            FillCircle(cx + col * r * 0.55f, cy + row * r * 0.32f, r * 0.16f, color);
                    break;
                case "headphones":
                case "mic":
                    DrawCircle(cx, cy - r * 0.20f, r * 0.36f, color, strokeWidth);
                    DrawLine(cx, cy + r * 0.20f, cx, cy + r * 0.72f, color, strokeWidth);
                    DrawLine(cx - r * 0.40f, cy + r * 0.72f, cx + r * 0.40f, cy + r * 0.72f, color, strokeWidth);
                    break;
                case "settings":
                    DrawCircle(cx, cy, r * 0.45f, color, strokeWidth);
                    for (int i = 0; i < 6; ++i)
                    {
                        float a = i * MathF.PI / 3.0f;
                        DrawLine(cx + MathF.Cos(a) * r * 0.50f, cy + MathF.Sin(a) * r * 0.50f,
                                 cx + MathF.Cos(a) * r * 0.84f, cy + MathF.Sin(a) * r * 0.84f, color, strokeWidth);
                    }
                    break;
                case "play":
                    using (var brush = new SolidBrush(color))
                    {
                        _g.FillPolygon(brush, new[]
                        {
                            new PointF(cx - r * 0.45f, cy - r * 0.65f),
                            new PointF(cx + r * 0.68f, cy),
                            new PointF(cx - r * 0.45f, cy + r * 0.65f)
                        });
                    }
                    break;
                case "pause":
                    FillRoundedRect(new RectangleF(cx - r * 0.55f, cy - r * 0.62f, r * 0.38f, r * 1.24f), 1.5f, color);
                    FillRoundedRect(new RectangleF(cx + r * 0.17f, cy - r * 0.62f, r * 0.38f, r * 1.24f), 1.5f, color);
                    break;
                case "skip-back":
                    DrawLine(cx - r * 0.55f, cy - r * 0.55f, cx - r * 0.55f, cy + r * 0.55f, color, strokeWidth);
                    DrawLine(cx + r * 0.55f, cy - r * 0.55f, cx - r * 0.35f, cy, color, strokeWidth);
                    DrawLine(cx - r * 0.35f, cy, cx + r * 0.55f, cy + r * 0.55f, color, strokeWidth);
                    break;
                case "skip-forward":
                    DrawLine(cx + r * 0.55f, cy - r * 0.55f, cx + r * 0.55f, cy + r * 0.55f, color, strokeWidth);
                    DrawLine(cx - r * 0.55f, cy - r * 0.55f, cx + r * 0.35f, cy, color, strokeWidth);
                    DrawLine(cx + r * 0.35f, cy, cx - r * 0.55f, cy + r * 0.55f, color, strokeWidth);
                    break;
                case "heart-filled":
                    FillCircle(cx - r * 0.30f, cy - r * 0.18f, r * 0.34f, color);
                    FillCircle(cx + r * 0.30f, cy - r * 0.18f, r * 0.34f, color);
                    using (var b = new SolidBrush(color))
                    {
                        _g.FillPolygon(b, new[]
                        {
                            new PointF(cx - r * 0.62f, cy - r * 0.05f),
                            new PointF(cx + r * 0.62f, cy - r * 0.05f),
                            new PointF(cx, cy + r * 0.70f)
                        });
                    }
                    break;
                case "heart":
                    DrawCircle(cx, cy, r * 0.55f, color, strokeWidth * 0.85f);
                    break;
                case "palette":
                    DrawCircle(cx, cy, r * 0.78f, color, strokeWidth);
                    FillCircle(cx - r * 0.26f, cy - r * 0.20f, r * 0.14f, color);
                    FillCircle(cx + r * 0.26f, cy - r * 0.20f, r * 0.14f, color);
                    FillCircle(cx, cy + r * 0.26f, r * 0.16f, color);
                    break;
                case "volume":
                case "volume-2":
                case "volume1":
                case "volume-x":
                    DrawLine(cx - r * 0.72f, cy - r * 0.28f, cx - r * 0.72f, cy + r * 0.28f, color, strokeWidth);
                    DrawLine(cx - r * 0.72f, cy - r * 0.28f, cx - r * 0.40f, cy - r * 0.28f, color, strokeWidth);
                    DrawLine(cx - r * 0.72f, cy + r * 0.28f, cx - r * 0.40f, cy + r * 0.28f, color, strokeWidth);
                    DrawLine(cx - r * 0.40f, cy - r * 0.28f, cx - r * 0.05f, cy - r * 0.62f, color, strokeWidth);
                    DrawLine(cx - r * 0.40f, cy + r * 0.28f, cx - r * 0.05f, cy + r * 0.62f, color, strokeWidth);
                    DrawLine(cx - r * 0.05f, cy - r * 0.62f, cx - r * 0.05f, cy + r * 0.62f, color, strokeWidth);
                    if (iconName == "volume-x")
                    {
                        DrawLine(cx + r * 0.28f, cy - r * 0.28f, cx + r * 0.78f, cy + r * 0.28f, color, strokeWidth);
                        DrawLine(cx + r * 0.78f, cy - r * 0.28f, cx + r * 0.28f, cy + r * 0.28f, color, strokeWidth);
                    }
                    else
                    {
                        using var arcPen = new Pen(ApplyOpacity(color), strokeWidth) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                        _g.DrawArc(arcPen, cx + r * 0.02f, cy - r * 0.36f, r * 0.44f, r * 0.72f, -55, 110);
                        if (iconName == "volume-2" || iconName == "volume")
                            _g.DrawArc(arcPen, cx + r * 0.14f, cy - r * 0.62f, r * 0.68f, r * 1.24f, -55, 110);
                    }
                    break;
                case "wifi":
                    using (var wfPen = new Pen(ApplyOpacity(color), strokeWidth) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    {
                        _g.DrawArc(wfPen, cx - r * 0.85f, cy - r * 0.55f, r * 1.70f, r * 1.70f, 225, 90);
                        _g.DrawArc(wfPen, cx - r * 0.52f, cy - r * 0.22f, r * 1.04f, r * 1.04f, 225, 90);
                    }
                    FillCircle(cx, cy + r * 0.50f, r * 0.14f, color);
                    break;
                case "bluetooth":
                    DrawLine(cx, cy - r * 0.78f, cx, cy + r * 0.78f, color, strokeWidth);
                    DrawLine(cx, cy - r * 0.78f, cx + r * 0.48f, cy - r * 0.34f, color, strokeWidth);
                    DrawLine(cx + r * 0.48f, cy - r * 0.34f, cx - r * 0.42f, cy + r * 0.34f, color, strokeWidth);
                    DrawLine(cx - r * 0.42f, cy - r * 0.34f, cx + r * 0.48f, cy + r * 0.34f, color, strokeWidth);
                    DrawLine(cx + r * 0.48f, cy + r * 0.34f, cx, cy + r * 0.78f, color, strokeWidth);
                    break;
                case "moon":
                    using (var mnPen = new Pen(ApplyOpacity(color), strokeWidth) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    {
                        _g.DrawArc(mnPen, cx - r * 0.65f, cy - r * 0.65f, r * 1.30f, r * 1.30f, 35, 235);
                        _g.DrawArc(mnPen, cx - r * 0.25f, cy - r * 0.68f, r * 1.05f, r * 1.05f, 115, 130);
                    }
                    break;
                case "bell-off":
                    DrawCircle(cx, cy - r * 0.10f, r * 0.48f, color, strokeWidth);
                    DrawLine(cx - r * 0.65f, cy + r * 0.42f, cx + r * 0.65f, cy + r * 0.42f, color, strokeWidth);
                    DrawLine(cx - r * 0.72f, cy - r * 0.72f, cx + r * 0.72f, cy + r * 0.72f, color, strokeWidth);
                    break;
                case "sun":
                    DrawCircle(cx, cy, r * 0.42f, color, strokeWidth);
                    for (int i = 0; i < 8; ++i)
                    {
                        float a = i * MathF.PI / 4.0f;
                        DrawLine(cx + MathF.Cos(a) * r * 0.58f, cy + MathF.Sin(a) * r * 0.58f,
                                 cx + MathF.Cos(a) * r * 0.85f, cy + MathF.Sin(a) * r * 0.85f, color, strokeWidth);
                    }
                    break;
                case "calendar":
                    DrawRoundedRect(new RectangleF(cx - r * 0.75f, cy - r * 0.60f, r * 1.50f, r * 1.35f), 2.5f, color, strokeWidth);
                    DrawLine(cx - r * 0.75f, cy - r * 0.15f, cx + r * 0.75f, cy - r * 0.15f, color, strokeWidth);
                    break;
                case "smartphone":
                    DrawRoundedRect(new RectangleF(cx - r * 0.50f, cy - r * 0.80f, r * 1.00f, r * 1.60f), 2.5f, color, strokeWidth);
                    FillCircle(cx, cy + r * 0.54f, r * 0.12f, color);
                    break;
                case "eye":
                    DrawRoundedRect(new RectangleF(cx - r * 0.85f, cy - r * 0.45f, r * 1.70f, r * 0.90f), r * 0.45f, color, strokeWidth);
                    FillCircle(cx, cy, r * 0.25f, color);
                    break;
                case "gamepad":
                    DrawRoundedRect(new RectangleF(cx - r * 0.85f, cy - r * 0.50f, r * 1.70f, r * 1.00f), r * 0.45f, color, strokeWidth);
                    DrawLine(cx - r * 0.45f, cy, cx - r * 0.15f, cy, color, strokeWidth);
                    DrawLine(cx - r * 0.30f, cy - r * 0.15f, cx - r * 0.30f, cy + r * 0.15f, color, strokeWidth);
                    FillCircle(cx + r * 0.38f, cy, r * 0.14f, color);
                    break;
                case "cpu":
                    DrawRoundedRect(new RectangleF(cx - r * 0.55f, cy - r * 0.55f, r * 1.10f, r * 1.10f), 2.0f, color, strokeWidth);
                    DrawLine(cx - r * 0.85f, cy - r * 0.25f, cx - r * 0.55f, cy - r * 0.25f, color, strokeWidth);
                    DrawLine(cx - r * 0.85f, cy + r * 0.25f, cx - r * 0.55f, cy + r * 0.25f, color, strokeWidth);
                    DrawLine(cx + r * 0.55f, cy - r * 0.25f, cx + r * 0.85f, cy - r * 0.25f, color, strokeWidth);
                    DrawLine(cx + r * 0.55f, cy + r * 0.25f, cx + r * 0.85f, cy + r * 0.25f, color, strokeWidth);
                    break;
                case "command":
                {
                    // Apple Command key (⌘): central square + 4 corner loops
                    float lr = r * 0.28f;
                    float off = r * 0.50f;
                    DrawRoundedRect(new RectangleF(cx - off + lr, cy - off + lr, (off - lr) * 2f, (off - lr) * 2f), 0.5f, color, strokeWidth);
                    DrawCircle(cx - off, cy - off, lr, color, strokeWidth);
                    DrawCircle(cx + off, cy - off, lr, color, strokeWidth);
                    DrawCircle(cx - off, cy + off, lr, color, strokeWidth);
                    DrawCircle(cx + off, cy + off, lr, color, strokeWidth);
                    break;
                }
                case "key":
                    DrawCircle(cx - r * 0.35f, cy, r * 0.36f, color, strokeWidth);
                    DrawLine(cx, cy, cx + r * 0.80f, cy, color, strokeWidth);
                    DrawLine(cx + r * 0.55f, cy, cx + r * 0.55f, cy + r * 0.32f, color, strokeWidth);
                    break;
                case "video":
                    DrawRoundedRect(new RectangleF(cx - r * 0.80f, cy - r * 0.52f, r * 1.10f, r * 1.04f), 2.0f, color, strokeWidth);
                    DrawLine(cx + r * 0.30f, cy - r * 0.20f, cx + r * 0.80f, cy - r * 0.45f, color, strokeWidth);
                    DrawLine(cx + r * 0.80f, cy - r * 0.45f, cx + r * 0.80f, cy + r * 0.45f, color, strokeWidth);
                    DrawLine(cx + r * 0.80f, cy + r * 0.45f, cx + r * 0.30f, cy + r * 0.20f, color, strokeWidth);
                    break;
                case "info":
                    DrawCircle(cx, cy, r * 0.80f, color, strokeWidth);
                    FillCircle(cx, cy - r * 0.35f, r * 0.12f, color);
                    DrawLine(cx, cy - r * 0.08f, cx, cy + r * 0.44f, color, strokeWidth);
                    break;
                case "plus":
                    DrawLine(cx - r * 0.55f, cy, cx + r * 0.55f, cy, color, strokeWidth);
                    DrawLine(cx, cy - r * 0.55f, cx, cy + r * 0.55f, color, strokeWidth);
                    break;
                case "check":
                    DrawLine(cx - r * 0.5f, cy + r * 0.02f, cx - r * 0.1f, cy + r * 0.4f, color, strokeWidth);
                    DrawLine(cx - r * 0.1f, cy + r * 0.4f, cx + r * 0.55f, cy - r * 0.4f, color, strokeWidth);
                    break;
                case "chevron-right":
                    DrawLine(cx - r * 0.22f, cy - r * 0.5f, cx + r * 0.3f, cy, color, strokeWidth);
                    DrawLine(cx + r * 0.3f, cy, cx - r * 0.22f, cy + r * 0.5f, color, strokeWidth);
                    break;
                case "chevron-down":
                    DrawLine(cx - r * 0.5f, cy - r * 0.18f, cx, cy + r * 0.32f, color, strokeWidth);
                    DrawLine(cx, cy + r * 0.32f, cx + r * 0.5f, cy - r * 0.18f, color, strokeWidth);
                    break;
                case "camera":
                    DrawRoundedRect(new RectangleF(cx - r * 0.92f, cy - r * 0.55f, r * 1.84f, r * 1.28f), 2.6f, color, strokeWidth);
                    DrawCircle(cx, cy + r * 0.08f, r * 0.33f, color, strokeWidth);
                    DrawLine(cx - r * 0.4f, cy - r * 0.55f, cx - r * 0.24f, cy - r * 0.82f, color, strokeWidth);
                    DrawLine(cx - r * 0.24f, cy - r * 0.82f, cx + r * 0.24f, cy - r * 0.82f, color, strokeWidth);
                    DrawLine(cx + r * 0.24f, cy - r * 0.82f, cx + r * 0.4f, cy - r * 0.55f, color, strokeWidth);
                    break;
                case "scissors":
                    DrawCircle(cx - r * 0.42f, cy + r * 0.48f, r * 0.2f, color, strokeWidth);
                    DrawCircle(cx - r * 0.42f, cy - r * 0.48f, r * 0.2f, color, strokeWidth);
                    DrawLine(cx - r * 0.26f, cy + r * 0.36f, cx + r * 0.5f, cy - r * 0.55f, color, strokeWidth);
                    DrawLine(cx - r * 0.26f, cy - r * 0.36f, cx + r * 0.5f, cy + r * 0.55f, color, strokeWidth);
                    break;
                case "download":
                    DrawLine(cx, cy - r * 0.62f, cx, cy + r * 0.18f, color, strokeWidth);
                    DrawLine(cx - r * 0.36f, cy - r * 0.12f, cx, cy + r * 0.28f, color, strokeWidth);
                    DrawLine(cx + r * 0.36f, cy - r * 0.12f, cx, cy + r * 0.28f, color, strokeWidth);
                    DrawLine(cx - r * 0.55f, cy + r * 0.6f, cx + r * 0.55f, cy + r * 0.6f, color, strokeWidth);
                    break;
                case "upload":
                    DrawLine(cx, cy + r * 0.62f, cx, cy - r * 0.18f, color, strokeWidth);
                    DrawLine(cx - r * 0.36f, cy + r * 0.12f, cx, cy - r * 0.28f, color, strokeWidth);
                    DrawLine(cx + r * 0.36f, cy + r * 0.12f, cx, cy - r * 0.28f, color, strokeWidth);
                    DrawLine(cx - r * 0.55f, cy + r * 0.6f, cx + r * 0.55f, cy + r * 0.6f, color, strokeWidth);
                    break;
                case "trash":
                    DrawLine(cx - r * 0.6f, cy - r * 0.52f, cx + r * 0.6f, cy - r * 0.52f, color, strokeWidth);
                    DrawLine(cx - r * 0.18f, cy - r * 0.72f, cx + r * 0.18f, cy - r * 0.72f, color, strokeWidth);
                    DrawLine(cx - r * 0.45f, cy - r * 0.52f, cx - r * 0.38f, cy + r * 0.62f, color, strokeWidth);
                    DrawLine(cx + r * 0.45f, cy - r * 0.52f, cx + r * 0.38f, cy + r * 0.62f, color, strokeWidth);
                    DrawLine(cx - r * 0.38f, cy + r * 0.62f, cx + r * 0.38f, cy + r * 0.62f, color, strokeWidth);
                    break;
                case "file-text":
                    DrawRoundedRect(new RectangleF(cx - r * 0.45f, cy - r * 0.72f, r * 0.9f, r * 1.44f), 2f, color, strokeWidth);
                    DrawLine(cx - r * 0.2f, cy - r * 0.28f, cx + r * 0.2f, cy - r * 0.28f, color, strokeWidth);
                    DrawLine(cx - r * 0.2f, cy + r * 0.02f, cx + r * 0.2f, cy + r * 0.02f, color, strokeWidth);
                    break;
                case "image":
                    DrawRoundedRect(new RectangleF(cx - r * 0.8f, cy - r * 0.62f, r * 1.6f, r * 1.28f), 2.4f, color, strokeWidth);
                    DrawCircle(cx - r * 0.34f, cy - r * 0.2f, r * 0.16f, color, strokeWidth);
                    DrawLine(cx - r * 0.7f, cy + r * 0.52f, cx - r * 0.08f, cy - r * 0.08f, color, strokeWidth);
                    DrawLine(cx - r * 0.08f, cy - r * 0.08f, cx + r * 0.68f, cy + r * 0.52f, color, strokeWidth);
                    break;
                case "battery":
                    DrawRoundedRect(new RectangleF(cx - r * 0.82f, cy - r * 0.38f, r * 1.5f, r * 0.76f), 2f, color, strokeWidth);
                    DrawLine(cx + r * 0.72f, cy - r * 0.16f, cx + r * 0.72f, cy + r * 0.16f, color, strokeWidth * 1.6f);
                    break;
                default:
                    DrawCircle(cx, cy, r * 0.65f, color, strokeWidth);
                    break;
            }
        }

        public void DrawQrCode(string payload, float left, float top, float sizeDip, Color fgColor, Color bgColor)
        {
            if (_g == null || sizeDip <= 16.0f) return;
            FillRoundedRect(new RectangleF(left, top, sizeDip, sizeDip), 8.0f, bgColor);

            string effectivePayload = string.IsNullOrEmpty(payload) ? "openpixel-deck://pair" : payload;
            QrMatrix qr;
            try
            {
                qr = GetCachedQrMatrix(effectivePayload);
            }
            catch
            {
                return;
            }

            // 4-module quiet zone is required for reliable camera scanning.
            int n = qr.Size;
            float cell = sizeDip / (n + 8.0f);
            using var brush = new SolidBrush(fgColor);
            for (int y = 0; y < n; ++y)
            {
                for (int x = 0; x < n; ++x)
                {
                    if (!qr.Modules[y, x]) continue;
                    float x0 = left + (x + 4) * cell;
                    float y0 = top + (y + 4) * cell;
                    float x1 = left + (x + 5) * cell;
                    float y1 = top + (y + 5) * cell;
                    float rx0 = MathF.Floor(x0), ry0 = MathF.Floor(y0);
                    float rx1 = MathF.Ceiling(x1), ry1 = MathF.Ceiling(y1);
                    _g.FillRectangle(brush, rx0, ry0, Math.Max(1.0f, rx1 - rx0), Math.Max(1.0f, ry1 - ry0));
                }
            }
        }

        public void ClearHitRegions() => _hitRegions.Clear();

        public void AddHitRegion(string id, RectangleF r, Action onClick, Action<float> onDrag = null, bool verticalDrag = false)
        {
            _hitRegions.Add(new HitRegion { Id = id, Rect = r, OnClick = onClick, OnDrag = onDrag, VerticalDrag = verticalDrag });
        }

        public bool IsHovered(RectangleF r, float dipX, float dipY) => r.Contains(dipX, dipY);

        public bool DispatchClick(float dipX, float dipY)
        {
            for (int i = _hitRegions.Count - 1; i >= 0; --i)
            {
                var hr = _hitRegions[i];
                if (hr.Rect.Contains(dipX, dipY))
                {
                    if (hr.OnDrag != null)
                    {
                        _activeDragId = hr.Id;
                        float norm = hr.VerticalDrag
                            ? Math.Clamp(1.0f - (dipY - hr.Rect.Top) / Math.Max(1.0f, hr.Rect.Height), 0.0f, 1.0f)
                            : Math.Clamp((dipX - hr.Rect.Left) / Math.Max(1.0f, hr.Rect.Width), 0.0f, 1.0f);
                        hr.OnDrag(norm);
                        return true;
                    }
                    if (hr.OnClick != null)
                    {
                        hr.OnClick();
                        return true;
                    }
                }
            }
            _activeDragId = "";
            return false;
        }

        public bool DispatchDrag(float dipX, float dipY)
        {
            for (int i = _hitRegions.Count - 1; i >= 0; --i)
            {
                var hr = _hitRegions[i];
                if (hr.OnDrag != null && (hr.Id == _activeDragId || hr.Rect.Contains(dipX, dipY)))
                {
                    _activeDragId = hr.Id;
                    float norm = hr.VerticalDrag
                        ? Math.Clamp(1.0f - (dipY - hr.Rect.Top) / Math.Max(1.0f, hr.Rect.Height), 0.0f, 1.0f)
                        : Math.Clamp((dipX - hr.Rect.Left) / Math.Max(1.0f, hr.Rect.Width), 0.0f, 1.0f);
                    hr.OnDrag(norm);
                    return true;
                }
            }
            return false;
        }

        // Cache the encoded pairing matrix: the Settings window redraws continuously
        // while sliders move and must not re-run Reed-Solomon encoding every frame.
        private static readonly object QrCacheLock = new();
        private static string _cachedQrPayload;
        private static QrMatrix _cachedQrMatrix;

        private static QrMatrix GetCachedQrMatrix(string payload)
        {
            lock (QrCacheLock)
            {
                if (_cachedQrMatrix != null && _cachedQrPayload == payload) return _cachedQrMatrix;
                QrMatrix matrix = QrCodeEncoder.Encode(payload);
                _cachedQrPayload = payload;
                _cachedQrMatrix = matrix;
                return matrix;
            }
        }

        // O(width) separable box blur with clamped edge pixels (same result shape
        // as the previous per-pixel convolution, far cheaper).
        private static void BoxBlurHorizontal(int[] src, int[] dst, int w, int h, int radius)
        {
            int window = radius * 2 + 1;
            for (int y = 0; y < h; ++y)
            {
                int row = y * w;
                int sumR = 0, sumG = 0, sumB = 0;
                for (int k = -radius; k <= radius; ++k)
                {
                    uint c = (uint)src[row + Math.Clamp(k, 0, w - 1)];
                    sumB += (int)(c & 0xFF);
                    sumG += (int)((c >> 8) & 0xFF);
                    sumR += (int)((c >> 16) & 0xFF);
                }
                for (int x = 0; x < w; ++x)
                {
                    dst[row + x] = PackRgb(sumR / window, sumG / window, sumB / window);

                    uint outC = (uint)src[row + Math.Clamp(x - radius, 0, w - 1)];
                    uint inC = (uint)src[row + Math.Clamp(x + radius + 1, 0, w - 1)];
                    sumB += (int)(inC & 0xFF) - (int)(outC & 0xFF);
                    sumG += (int)((inC >> 8) & 0xFF) - (int)((outC >> 8) & 0xFF);
                    sumR += (int)((inC >> 16) & 0xFF) - (int)((outC >> 16) & 0xFF);
                }
            }
        }

        private static void BoxBlurVertical(int[] src, int[] dst, int w, int h, int radius)
        {
            int window = radius * 2 + 1;
            for (int x = 0; x < w; ++x)
            {
                int sumR = 0, sumG = 0, sumB = 0;
                for (int k = -radius; k <= radius; ++k)
                {
                    uint c = (uint)src[Math.Clamp(k, 0, h - 1) * w + x];
                    sumB += (int)(c & 0xFF);
                    sumG += (int)((c >> 8) & 0xFF);
                    sumR += (int)((c >> 16) & 0xFF);
                }
                for (int y = 0; y < h; ++y)
                {
                    dst[y * w + x] = PackRgb(sumR / window, sumG / window, sumB / window);

                    uint outC = (uint)src[Math.Clamp(y - radius, 0, h - 1) * w + x];
                    uint inC = (uint)src[Math.Clamp(y + radius + 1, 0, h - 1) * w + x];
                    sumB += (int)(inC & 0xFF) - (int)(outC & 0xFF);
                    sumG += (int)((inC >> 8) & 0xFF) - (int)((outC >> 8) & 0xFF);
                    sumR += (int)((inC >> 16) & 0xFF) - (int)((outC >> 16) & 0xFF);
                }
            }
        }

        private static int PackRgb(int r, int g, int b)
        {
            return unchecked((int)(0xFF000000u |
                ((uint)Math.Clamp(r, 0, 255) << 16) |
                ((uint)Math.Clamp(g, 0, 255) << 8) |
                (uint)Math.Clamp(b, 0, 255)));
        }

        private void ReleaseSurface()
        {
            _g?.Dispose();
            _g = null;
            _managedSurface?.Dispose();
            _managedSurface = null;
            if (_memDc != IntPtr.Zero)
            {
                if (_oldBitmap != IntPtr.Zero) Win32Layered.SelectObject(_memDc, _oldBitmap);
                if (_dibBitmap != IntPtr.Zero) Win32Layered.DeleteObject(_dibBitmap);
                Win32Layered.DeleteDC(_memDc);
                _memDc = IntPtr.Zero;
                _dibBitmap = IntPtr.Zero;
                _oldBitmap = IntPtr.Zero;
                _dibBits = IntPtr.Zero;
            }
            _surfaceW = 0;
            _surfaceH = 0;
        }

        public void Dispose()
        {
            _cachedGlassBitmap?.Dispose();
            _cachedGlassBitmap = null;
            _shapePath.Dispose();
            _paintBrush.Dispose();
            _paintPen.Dispose();
            foreach (Font font in _fontCache.Values) font.Dispose();
            _fontCache.Clear();
            foreach (StringFormat sf in _formatCache.Values) sf.Dispose();
            _formatCache.Clear();
            ReleaseSurface();
        }
    }

    public static class Win32Layered
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int x, y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE { public int cx, cy; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public int biSize;
            public int biWidth;
            public int biHeight;
            public short biPlanes;
            public short biBitCount;
            public int biCompression;
            public int biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public int biClrUsed;
            public int biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            public int bmiColors;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct CURSORINFO
        {
            public int cbSize;
            public int flags;
            public IntPtr hCursor;
            public POINT ptScreenPos;
        }

        public const int CURSOR_SHOWING = 0x00000001;
        public const uint WDA_NONE = 0x00000000;
        public const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);
        [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
        [DllImport("user32.dll")] public static extern bool GetCursorInfo(ref CURSORINFO pci);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowW(string lpClassName, string lpWindowName);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] public static extern short GetKeyState(int nVirtKey);
        [DllImport("user32.dll")] public static extern bool AddClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
        [DllImport("user32.dll")] public static extern bool LockWorkStation();

        // Taskbar mirroring: enumerate real app windows and focus them.
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextLengthW(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);

        // Desktop layer (WorkerW) hosting for widgets and the live wallpaper.
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string className, string windowName);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll")] public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);
        [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint Msg, UIntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);

        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr ho);
        [DllImport("gdi32.dll")] public static extern int SetStretchBltMode(IntPtr hdc, int mode);
        [DllImport("gdi32.dll")] public static extern bool StretchBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest, IntPtr hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, int rop);
        [DllImport("gdi32.dll")] public static extern uint GetPixel(IntPtr hdc, int x, int y);
        [DllImport("gdi32.dll")] public static extern unsafe bool SetDeviceGammaRamp(IntPtr hDC, void* lpRamp);

        // 1 ms multimedia timer resolution while animating: WM_TIMER otherwise
        // jitters by 5-20 ms, which is exactly what makes motion look choppy.
        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")] private static extern uint TimeBeginPeriod(uint uPeriod);
        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")] private static extern uint TimeEndPeriod(uint uPeriod);

        private static int _highResTimerRefs = 0;

        public static void BeginHighResolutionTimer()
        {
            if (Interlocked.Increment(ref _highResTimerRefs) == 1)
            {
                try { TimeBeginPeriod(1); } catch { }
            }
        }

        public static void EndHighResolutionTimer()
        {
            if (Interlocked.Decrement(ref _highResTimerRefs) <= 0)
            {
                Interlocked.Exchange(ref _highResTimerRefs, 0);
                try { TimeEndPeriod(1); } catch { }
            }
        }
    }
}
