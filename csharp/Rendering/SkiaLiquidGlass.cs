using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using OpenPixel.Core;
using SkiaSharp;

namespace OpenPixel.Rendering
{
    // ========================================================================
    // Clean, Smooth, High-FPS SkiaSharp Liquid Glass Engine
    // - ZERO rainbow/chromatic dispersion (pure clean optical glass).
    // - ZERO pixelation: HALFTONE capture + full-res Skia Gaussian blur +
    //   HighQualityBicubic upsampling.
    // - Crisp white glass specular corners & rim + smooth interior lens curve.
    // - Low idle RAM: compact 960x540 wallpaper cache + zero-copy reusable DIB.
    // ========================================================================
    public sealed class SkiaLiquidGlass : IDisposable
    {
        private const string LiquidGlassSksl = @"
uniform shader u_blur;
uniform float2 u_res;
uniform float u_radius;
uniform float u_flare; // >0 for top-docked notch with concave left/right hinges
uniform float u_mode;  // 0 = LiquidGlass, 1 = CandyGlass, 2 = Acrylic
uniform float u_sheen; // 0 = flat even rim, 1 = corner-concentrated specular

float3 sdAndNormal(float2 fragCoord) {
    float2 halfSize = u_res * 0.5;
    float2 p;
    float2 b;
    float r;

    if (u_flare > 0.5) {
        float bodyHalfW = max(4.0, halfSize.x - u_flare);
        r = clamp(u_radius, 3.0, min(bodyHalfW, u_res.y) - 1.0);
        float ax = abs(fragCoord.x - halfSize.x);
        if (ax >= bodyHalfW - 0.5 && fragCoord.y <= u_flare + 1.0) {
            float sx = fragCoord.x >= halfSize.x ? 1.0 : -1.0;
            float2 dArc = float2(ax - (bodyHalfW + u_flare), fragCoord.y - u_flare);
            float lenA = max(length(dArc), 0.0001);
            float sdH = u_flare - lenA;
            float2 nH = float2(-dArc.x * sx, -dArc.y) / lenA;
            return float3(sdH, nH.x, nH.y);
        }
        b = float2(bodyHalfW, u_res.y * 0.5 + r);
        p = float2(fragCoord.x - halfSize.x, fragCoord.y - (u_res.y * 0.5 - r));
    } else {
        b = halfSize;
        p = fragCoord - halfSize;
        r = clamp(u_radius, 3.0, min(halfSize.x, halfSize.y) - 1.0);
    }

    float2 s = float2(p.x >= 0.0 ? 1.0 : -1.0, p.y >= 0.0 ? 1.0 : -1.0);
    float2 q = abs(p) - b + float2(r);
    float2 mq = max(q, float2(0.0));
    float lenQ = length(mq);
    float sd = lenQ + min(max(q.x, q.y), 0.0) - r;

    float2 n;
    if (lenQ > 0.0001) {
        n = (mq / lenQ) * s;
    } else if (q.x > q.y) {
        n = float2(s.x, 0.0);
    } else {
        n = float2(0.0, s.y);
    }
    return float3(sd, n.x, n.y);
}

half4 main(float2 fragCoord) {
    float2 halfSize = u_res * 0.5;
    float3 sdf = sdAndNormal(fragCoord);
    float d = max(0.0, -sdf.x);
    float2 n = sdf.yz;

    float isAcrylic = step(1.5, u_mode);
    float isCandy = step(0.5, u_mode) * (1.0 - isAcrylic);

    // Pure clean blurred background with a whisper-thin specular edge shine (zero warping)
    half3 rgb = u_blur.eval(clamp(fragCoord, float2(0.5), u_res - float2(0.5))).rgb;

    // Clean neutral frosted scrim so icons/text on the notch stay razor-sharp
    half luma = dot(rgb, half3(0.2126, 0.7152, 0.0722));
    half scrim = mix(half(0.74), half(0.56), clamp(luma * half(1.10), half(0.0), half(1.0)));

    // Tiny subtle edge shine (suppressed along the flush top bezel)
    float topMask = u_flare > 0.5 ? smoothstep(0.0, 2.5, fragCoord.y) : 1.0;
    float cornerFactor = abs(n.x * n.y) * 2.0 * clamp(u_sheen, 0.0, 1.0);
    float rimLine = smoothstep(1.1, 0.0, d) * topMask;
    half whiteRim = half(rimLine * (0.018 + 0.028 * cornerFactor));

    half3 outCol;
    if (isAcrylic > 0.5) {
        outCol = rgb * half(0.56) + half3(0.044, 0.046, 0.054) + half3(whiteRim * half(0.40));
    } else if (isCandy > 0.5) {
        outCol = rgb * half3(0.84, 0.72, 0.80) * scrim
               + half3(0.040, 0.012, 0.032)
               + half3(whiteRim * half(0.80), whiteRim * half(0.65), whiteRim * half(0.75));
    } else {
        outCol = rgb * scrim + half3(0.025, 0.027, 0.033) + half3(whiteRim);
    }

    return half4(clamp(outCol, half3(0.0), half3(1.0)), half(1.0));
}
";

        private static readonly object EffectSync = new();
        private static SKRuntimeEffect _runtimeEffect;
        private static bool _effectCompiled;

        // Compact shared Windows desktop wallpaper cache (max 960x540 -> ~2 MB RAM)
        private static readonly object WallpaperSync = new();
        private static Bitmap _wallpaperFullCache;
        private static string _wallpaperCachedPath = "";
        private static DateTime _wallpaperCachedMtime = DateTime.MinValue;
        private static Rectangle _wallpaperCachedBounds = Rectangle.Empty;
        private static long _lastWallpaperCheckMs;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, StringBuilder pvParam, uint fWinIni);

        // Reusable zero-copy GDI DIB capture surface
        private IntPtr _capDc;
        private IntPtr _capBmp;
        private IntPtr _capOldBmp;
        private IntPtr _capBits;
        private int _capBufW;
        private int _capBufH;

        // Gaussian pre-blur target, kept across frames: allocating and clearing a
        // surface per frame was pure overhead while the glass animates.
        private SKSurface _blurSurface;
        private int _blurSurfW;
        private int _blurSurfH;

        private Bitmap _outputBitmap;
        private ulong _lastHash;
        private int _lastW;
        private int _lastH;
        private float _lastBlur = -1.0f;
        private float _lastRadius = -1.0f;
        private float _lastFlare = -1.0f;
        private float _lastSheen = 1.0f;
        private Rectangle _lastScreenRect;
        private float _lastDipW;
        private float _lastDipH;
        private int _lastTintKey;
        private SurfaceTheme _lastTheme;
        private long _lastRecomputeMs;

        public double LastVariance { get; private set; }
        public ulong LastOutputHash { get; private set; }
        // Profiling: how much CPU the lens itself costs (capture + blur + shader),
        // ignoring frames that reuse a cached lens. Surfaced by --idle-test.
        public static double LensComputeMs { get; private set; }
        public static long LensComputeCount { get; private set; }
        public static string LastEffectError { get; private set; } = "";
        public static string LastRenderError { get; private set; } = "";
        // Set while a screen capture tool (e.g. Snipping Tool) is active so glass
        // surfaces reuse their last clean backdrop instead of capturing themselves.
        public static volatile bool FreezeBackdropCapture;

        private static SKRuntimeEffect GetEffect()
        {
            if (_effectCompiled) return _runtimeEffect;
            lock (EffectSync)
            {
                if (_effectCompiled) return _runtimeEffect;
                _effectCompiled = true;
                try
                {
                    _runtimeEffect = SKRuntimeEffect.CreateShader(LiquidGlassSksl, out string err);
                    LastEffectError = err ?? "";
                }
                catch (Exception ex)
                {
                    LastEffectError = ex.ToString();
                    _runtimeEffect = null;
                }
                return _runtimeEffect;
            }
        }

        private bool EnsureCaptureBuffer(IntPtr screenDc, int w, int h)
        {
            if (_capDc != IntPtr.Zero && _capBmp != IntPtr.Zero && _capBits != IntPtr.Zero && _capBufW == w && _capBufH == h)
                return true;

            ReleaseCaptureBuffer();
            _capDc = Win32Layered.CreateCompatibleDC(screenDc);
            if (_capDc == IntPtr.Zero) return false;

            var bmi = new Win32Layered.BITMAPINFO();
            bmi.bmiHeader.biSize = Marshal.SizeOf<Win32Layered.BITMAPINFOHEADER>();
            bmi.bmiHeader.biWidth = w;
            bmi.bmiHeader.biHeight = -h;
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;

            _capBmp = Win32Layered.CreateDIBSection(screenDc, ref bmi, 0, out _capBits, IntPtr.Zero, 0);
            if (_capBmp == IntPtr.Zero || _capBits == IntPtr.Zero)
            {
                ReleaseCaptureBuffer();
                return false;
            }
            _capOldBmp = Win32Layered.SelectObject(_capDc, _capBmp);
            Win32Layered.SetStretchBltMode(_capDc, 4); // HALFTONE (smooth, zero pixelation!)
            _capBufW = w;
            _capBufH = h;
            return true;
        }

        private SKSurface EnsureBlurSurface(SKImageInfo info)
        {
            if (_blurSurface != null && _blurSurfW == info.Width && _blurSurfH == info.Height)
                return _blurSurface;

            _blurSurface?.Dispose();
            _blurSurface = null;
            _blurSurfW = 0;
            _blurSurfH = 0;
            try
            {
                _blurSurface = SKSurface.Create(info);
            }
            catch
            {
                return null;
            }
            if (_blurSurface != null)
            {
                _blurSurfW = info.Width;
                _blurSurfH = info.Height;
            }
            return _blurSurface;
        }

        private void ReleaseCaptureBuffer()
        {
            if (_capDc != IntPtr.Zero)
            {
                if (_capOldBmp != IntPtr.Zero) Win32Layered.SelectObject(_capDc, _capOldBmp);
                if (_capBmp != IntPtr.Zero) Win32Layered.DeleteObject(_capBmp);
                Win32Layered.DeleteDC(_capDc);
            }
            _capDc = IntPtr.Zero;
            _capBmp = IntPtr.Zero;
            _capOldBmp = IntPtr.Zero;
            _capBits = IntPtr.Zero;
            _capBufW = 0;
            _capBufH = 0;
        }

        private static Bitmap GetCachedWallpaperBitmap(Rectangle monitorBounds)
        {
            long now = Environment.TickCount64;
            lock (WallpaperSync)
            {
                if (_wallpaperFullCache != null &&
                    _wallpaperCachedBounds == monitorBounds &&
                    now - _lastWallpaperCheckMs < 4000)
                {
                    return _wallpaperFullCache;
                }
                _lastWallpaperCheckMs = now;

                try
                {
                    string path = "";
                    var sb = new StringBuilder(520);
                    if (SystemParametersInfoW(0x0073, (uint)sb.Capacity, sb, 0))
                        path = sb.ToString().TrimEnd('\0');
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    {
                        string transcoded = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                            "Microsoft", "Windows", "Themes", "TranscodedWallpaper");
                        if (File.Exists(transcoded)) path = transcoded;
                    }
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return _wallpaperFullCache;

                    DateTime mtime = File.GetLastWriteTimeUtc(path);
                    if (_wallpaperFullCache != null &&
                        string.Equals(_wallpaperCachedPath, path, StringComparison.OrdinalIgnoreCase) &&
                        _wallpaperCachedMtime == mtime &&
                        _wallpaperCachedBounds == monitorBounds)
                    {
                        return _wallpaperFullCache;
                    }

                    byte[] bytes = File.ReadAllBytes(path);
                    using var ms = new MemoryStream(bytes);
                    using var raw = Image.FromStream(ms);
                    // Cap wallpaper cache at 960x540 to keep idle RAM low (~2 MB instead of 16-32 MB)
                    float down = MathF.Min(1.0f, MathF.Min(960.0f / Math.Max(1, monitorBounds.Width), 540.0f / Math.Max(1, monitorBounds.Height)));
                    int w = Math.Max(64, (int)MathF.Round(monitorBounds.Width * down));
                    int h = Math.Max(64, (int)MathF.Round(monitorBounds.Height * down));
                    var scaled = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
                    using (var g = Graphics.FromImage(scaled))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.SmoothingMode = SmoothingMode.HighQuality;
                        float scale = MathF.Max((float)w / Math.Max(1, raw.Width), (float)h / Math.Max(1, raw.Height));
                        float dw = raw.Width * scale;
                        float dh = raw.Height * scale;
                        g.DrawImage(raw, (w - dw) * 0.5f, (h - dh) * 0.5f, dw, dh);
                    }
                    _wallpaperFullCache?.Dispose();
                    _wallpaperFullCache = scaled;
                    _wallpaperCachedPath = path;
                    _wallpaperCachedMtime = mtime;
                    _wallpaperCachedBounds = monitorBounds;
                    return _wallpaperFullCache;
                }
                catch
                {
                    return _wallpaperFullCache;
                }
            }
        }

        private bool TryCropWallpaperToCapBits(Rectangle screenRect, int capW, int capH)
        {
            try
            {
                Rectangle mon = Screen.FromPoint(new Point(screenRect.Left + screenRect.Width / 2, screenRect.Top + screenRect.Height / 2)).Bounds;
                Bitmap wp = GetCachedWallpaperBitmap(mon);
                if (wp == null || _capDc == IntPtr.Zero) return false;

                float sx = (float)wp.Width / Math.Max(1, mon.Width);
                float sy = (float)wp.Height / Math.Max(1, mon.Height);
                int relX = Math.Clamp((int)((screenRect.Left - mon.Left) * sx), 0, Math.Max(0, wp.Width - 1));
                int relY = Math.Clamp((int)((screenRect.Top - mon.Top) * sy), 0, Math.Max(0, wp.Height - 1));
                int relW = Math.Clamp((int)MathF.Ceiling(screenRect.Width * sx), 1, Math.Max(1, wp.Width - relX));
                int relH = Math.Clamp((int)MathF.Ceiling(screenRect.Height * sy), 1, Math.Max(1, wp.Height - relY));

                using var g = Graphics.FromHdc(_capDc);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(wp, new Rectangle(0, 0, capW, capH), new Rectangle(relX, relY, relW, relH), GraphicsUnit.Pixel);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void FillAmbientFallbackToCapBits(int capW, int capH, bool isCandy)
        {
            if (_capDc == IntPtr.Zero) return;
            using var g = Graphics.FromHdc(_capDc);
            using var br = new LinearGradientBrush(
                new Rectangle(0, 0, Math.Max(1, capW), Math.Max(1, capH)),
                isCandy ? Color.FromArgb(255, 68, 28, 62) : Color.FromArgb(255, 24, 26, 34),
                isCandy ? Color.FromArgb(255, 36, 20, 56) : Color.FromArgb(255, 14, 15, 20),
                LinearGradientMode.ForwardDiagonal);
            g.FillRectangle(br, 0, 0, capW, capH);
        }

        private bool CaptureBackdropZeroCopy(Rectangle screenRect, int capW, int capH, bool isCandy, out ulong inputHash)
        {
            inputHash = 0;
            IntPtr screenDc = Win32Layered.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) return false;
            try
            {
                if (!EnsureCaptureBuffer(screenDc, capW, capH)) return false;

                bool bltOk = Win32Layered.StretchBlt(
                    _capDc, 0, 0, capW, capH,
                    screenDc, screenRect.Left, screenRect.Top, screenRect.Width, screenRect.Height,
                    0x00CC0020);

                int totalPixels = capW * capH;
                int step = Math.Max(1, totalPixels / 48);
                bool gotValidCapture = false;
                if (bltOk)
                {
                    double sumL = 0, sumSq = 0;
                    int n = 0;
                    for (int i = 0; i < totalPixels; i += step)
                    {
                        uint p = unchecked((uint)Marshal.ReadInt32(_capBits, i * 4));
                        double l = 0.2126 * ((p >> 16) & 0xFF) + 0.7152 * ((p >> 8) & 0xFF) + 0.0722 * (p & 0xFF);
                        sumL += l;
                        sumSq += l * l;
                        ++n;
                    }
                    double meanL = n > 0 ? sumL / n : 0;
                    double varL = n > 0 ? Math.Max(0, sumSq / n - meanL * meanL) : 0;
                    gotValidCapture = (meanL >= 6.0 || varL >= 14.0);
                }

                if (!gotValidCapture)
                {
                    if (!TryCropWallpaperToCapBits(screenRect, capW, capH))
                        FillAmbientFallbackToCapBits(capW, capH, isCandy);
                }

                ulong hash = 14695981039346656037UL;
                for (int i = 0; i < totalPixels; i += step)
                {
                    hash ^= unchecked((uint)Marshal.ReadInt32(_capBits, i * 4));
                    hash *= 1099511628211UL;
                }
                inputHash = hash;
                return true;
            }
            finally
            {
                Win32Layered.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        // Pure frosted blur backdrop (for Settings window: NO LiquidGlass refraction or edge glow)
        public bool RenderBlurOnly(
            Graphics targetGraphics,
            Rectangle screenRect,
            float dipX, float dipY, float dipW, float dipH,
            float blurRadiusPx, Color tintOverlay)
        {
            if (targetGraphics == null || dipW <= 4.0f || dipH <= 4.0f) return false;
            if (screenRect.Width <= 8 || screenRect.Height <= 8 || screenRect.Width > 4096 || screenRect.Height > 4096)
                return false;

            try
            {
                float safeBlur = Math.Clamp(blurRadiusPx, 6.0f, 40.0f);
                // Compact capture for heavily frosted panels (Settings & Taskbar): 4x faster
                // CPU Gaussian blur than 460x320 while looking identically smooth when blurred.
                int capW = Math.Clamp((int)MathF.Round(screenRect.Width * 0.22f), 48, 220);
                int capH = Math.Clamp((int)MathF.Round(screenRect.Height * 0.22f), 20, 150);
                bool isPinkTint = tintOverlay.R > tintOverlay.G + 14;
                int tintKey = tintOverlay.ToArgb();

                long now = Environment.TickCount64;
                if (FreezeBackdropCapture && _outputBitmap != null)
                {
                    DrawOutput(targetGraphics, dipX, dipY, dipW, dipH);
                    return true;
                }

                // When the panel hasn't moved or resized, hold the cached frosted backdrop
                // for 1.2s so hovering/scrolling in Settings runs at zero blur CPU cost.
                bool moved = screenRect != _lastScreenRect;
                long reuseMs = moved ? 60 : 1200;
                if (_outputBitmap != null &&
                    tintKey == _lastTintKey &&
                    MathF.Abs(safeBlur - _lastBlur) < 0.4f &&
                    _lastRadius < 0.0f &&
                    now - _lastRecomputeMs < reuseMs)
                {
                    DrawOutput(targetGraphics, dipX, dipY, dipW, dipH);
                    return true;
                }

                if (!CaptureBackdropZeroCopy(screenRect, capW, capH, isPinkTint, out ulong hash))
                    return false;

                bool cacheHit = (hash == _lastHash &&
                                 capW == _lastW &&
                                 capH == _lastH &&
                                 MathF.Abs(safeBlur - _lastBlur) < 0.4f &&
                                 tintKey == _lastTintKey &&
                                 _lastRadius < 0.0f &&
                                 _outputBitmap != null);

                if (!cacheHit)
                {
                    _lastHash = hash;
                    _lastW = capW;
                    _lastH = capH;
                    _lastBlur = safeBlur;
                    _lastRadius = -1.0f;
                    _lastScreenRect = screenRect;
                    _lastTintKey = tintKey;
                    _lastRecomputeMs = now;

                    var rawInfo = new SKImageInfo(capW, capH, SKColorType.Bgra8888, SKAlphaType.Opaque);
                    var outInfo = new SKImageInfo(capW, capH, SKColorType.Bgra8888, SKAlphaType.Premul);
                    if (_outputBitmap == null || _outputBitmap.Width != capW || _outputBitmap.Height != capH)
                    {
                        _outputBitmap?.Dispose();
                        _outputBitmap = new Bitmap(capW, capH, PixelFormat.Format32bppPArgb);
                    }

                    using SKImage rawImg = SKImage.FromPixels(rawInfo, _capBits, capW * 4);
                    if (rawImg == null) return false;

                    // Keep the physical blur identical to the old capture scale, so a
                    // sharper capture does not read as a sharper (less frosted) panel.
                    float capScale = (float)capW / Math.Max(1, screenRect.Width);
                    float sigma = Math.Clamp(safeBlur * 0.38f * capScale * 3.0f, 3.0f, 20.0f);
                    BitmapData bd = _outputBitmap.LockBits(new Rectangle(0, 0, capW, capH), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
                    try
                    {
                        using SKSurface outSurf = SKSurface.Create(outInfo, bd.Scan0, bd.Stride);
                        outSurf.Canvas.Clear(SKColors.Black);
                        using (SKImageFilter blurFilter = SKImageFilter.CreateBlur(sigma, sigma))
                        using (var blurPaint = new SKPaint { ImageFilter = blurFilter })
                        {
                            outSurf.Canvas.DrawImage(rawImg, 0, 0, new SKSamplingOptions(SKFilterMode.Linear), blurPaint);
                        }
                        using (var tintPaint = new SKPaint { Color = new SKColor(tintOverlay.R, tintOverlay.G, tintOverlay.B, tintOverlay.A) })
                        {
                            outSurf.Canvas.DrawRect(0, 0, capW, capH, tintPaint);
                        }
                        outSurf.Flush();
                    }
                    finally
                    {
                        _outputBitmap.UnlockBits(bd);
                    }
                }

                DrawOutput(targetGraphics, dipX, dipY, dipW, dipH);
                return true;
            }
            catch (Exception ex)
            {
                LastRenderError = ex.ToString();
                return false;
            }
        }

        public bool Render(
            Graphics targetGraphics,
            Rectangle screenRect,
            float dipX, float dipY, float dipW, float dipH,
            float blurRadiusPx, SurfaceTheme surfaceTheme,
            float cornerRadiusPx = -1.0f,
            float topNotchFlarePx = 0.0f,
            float cornerSheen = 1.0f,
            float reuseWithoutCaptureMs = 0.0f,
            float minRefreshIntervalMs = 0.0f)
        {
            if (targetGraphics == null || dipW <= 4.0f || dipH <= 3.0f) return false;
            if (screenRect.Width <= 8 || screenRect.Height <= 3 || screenRect.Width > 4096 || screenRect.Height > 4096)
                return false;

            SKRuntimeEffect effect = GetEffect();
            if (effect == null) return false;

            try
            {
                float safeBlur = Math.Clamp(blurRadiusPx, 0.0f, 40.0f);
                long now = Environment.TickCount64;

                // Glass runs on its own slower clock than the surface it sits on.
                // Sampling the desktop and running the per-pixel lens for every 60 fps
                // animation frame is what made the island stutter on slower PCs, so the
                // cached lens is just scaled into the current rect between refreshes:
                // the shape keeps moving at full frame rate, the backdrop updates at
                // minRefreshIntervalMs. A still rect/size may hold the last lens even
                // longer (reuseWithoutCaptureMs), which is the zero-CPU resting case.
                if (FreezeBackdropCapture && _outputBitmap != null)
                {
                    DrawOutput(targetGraphics, dipX, dipY, dipW, dipH);
                    return true;
                }

                float refreshMs = minRefreshIntervalMs > 0.0f
                    ? Math.Clamp(minRefreshIntervalMs, 16.0f, 2000.0f)
                    : 90.0f;
                bool sameRect = screenRect == _lastScreenRect;
                bool sameSize = MathF.Abs(dipW - _lastDipW) < 0.6f && MathF.Abs(dipH - _lastDipH) < 0.6f;
                float windowMs = (sameRect && sameSize) ? MathF.Max(refreshMs, reuseWithoutCaptureMs) : refreshMs;
                if (_outputBitmap != null &&
                    surfaceTheme == _lastTheme &&
                    MathF.Abs(safeBlur - _lastBlur) < 0.4f &&
                    MathF.Abs(cornerSheen - _lastSheen) < 0.01f &&
                    _lastRadius >= 0.0f &&
                    now - _lastRecomputeMs < windowMs)
                {
                    DrawOutput(targetGraphics, dipX, dipY, dipW, dipH);
                    return true;
                }

                // Skia's raster runtime-effect interpreter is the single most expensive
                // step in a glass frame. A 4.5k-pixel lens cuts CPU shader time in half
                // while keeping the blurred backdrop smooth, and the crisp rim is stroked
                // at native resolution on top.
                double captureArea = Math.Max(1.0, (double)screenRect.Width * screenRect.Height);
                float downScale = (float)Math.Min(1.0, Math.Sqrt(4500.0 / captureArea));
                int capW = Math.Clamp((int)MathF.Round(screenRect.Width * downScale), 24, 240);
                int capH = Math.Clamp((int)MathF.Round(screenRect.Height * downScale), 6, 140);
                int totalPixels = capW * capH;

                if (!CaptureBackdropZeroCopy(screenRect, capW, capH, surfaceTheme == SurfaceTheme.CandyGlass, out ulong hash))
                    return false;
                hash ^= (ulong)((int)surfaceTheme + 1) * 0x9E3779B97F4A7C15UL;

                float scaleX = (float)capW / Math.Max(1.0f, screenRect.Width);
                float effectiveRadius = cornerRadiusPx > 1.0f
                    ? Math.Clamp(cornerRadiusPx * scaleX, 3.0f, Math.Min(capW, capH) * 0.48f)
                    : Math.Clamp(Math.Min(capW, capH) * 0.24f, 6.0f, 30.0f);
                float effectiveFlare = topNotchFlarePx > 0.5f
                    ? Math.Clamp(topNotchFlarePx * scaleX, 0.0f, capW * 0.25f)
                    : 0.0f;

                bool cacheHit = (hash == _lastHash &&
                                 capW == _lastW &&
                                 capH == _lastH &&
                                 MathF.Abs(safeBlur - _lastBlur) < 0.4f &&
                                 MathF.Abs(effectiveRadius - _lastRadius) < 0.8f &&
                                 MathF.Abs(effectiveFlare - _lastFlare) < 0.5f &&
                                 MathF.Abs(cornerSheen - _lastSheen) < 0.01f &&
                                 surfaceTheme == _lastTheme &&
                                 _outputBitmap != null);

                if (!cacheHit)
                {
                    long lensStart = Stopwatch.GetTimestamp();
                    _lastHash = hash;
                    _lastW = capW;
                    _lastH = capH;
                    _lastBlur = safeBlur;
                    _lastRadius = effectiveRadius;
                    _lastFlare = effectiveFlare;
                    _lastSheen = cornerSheen;
                    _lastScreenRect = screenRect;
                    _lastDipW = dipW;
                    _lastDipH = dipH;
                    _lastTheme = surfaceTheme;
                    _lastTintKey = 0;
                    _lastRecomputeMs = now;

                    var rawInfo = new SKImageInfo(capW, capH, SKColorType.Bgra8888, SKAlphaType.Opaque);
                    var premulInfo = new SKImageInfo(capW, capH, SKColorType.Bgra8888, SKAlphaType.Premul);
                    using SKImage rawImg = SKImage.FromPixels(rawInfo, _capBits, capW * 4);
                    if (rawImg == null) return false;

                    // Smooth frosted Gaussian blur (blurs the background cleanly without distorting the notch)
                    float sigma = surfaceTheme == SurfaceTheme.Acrylic
                        ? Math.Clamp(safeBlur * 0.46f * scaleX, 2.5f, 15.0f)
                        : Math.Clamp(safeBlur * 0.34f * scaleX, 1.8f, 12.0f);

                    var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
                    SKImage blurImg = null;
                    SKShader blurShader = null;
                    try
                    {
                        if (sigma > 0.20f)
                        {
                            SKSurface blurSurf = EnsureBlurSurface(premulInfo);
                            if (blurSurf == null) return false;
                            blurSurf.Canvas.Clear(SKColors.Transparent);
                            using SKImageFilter blurFilter = SKImageFilter.CreateBlur(sigma, sigma);
                            using var blurPaint = new SKPaint { ImageFilter = blurFilter };
                            blurSurf.Canvas.DrawImage(rawImg, 0, 0, sampling, blurPaint);
                            blurSurf.Flush();
                            blurImg = blurSurf.Snapshot();
                            blurShader = blurImg.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, sampling);
                        }
                        else
                        {
                            blurShader = rawImg.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, sampling);
                        }

                        var uniforms = new SKRuntimeEffectUniforms(effect)
                        {
                            ["u_res"] = new[] { (float)capW, (float)capH },
                            ["u_radius"] = effectiveRadius,
                            ["u_flare"] = effectiveFlare,
                            ["u_mode"] = surfaceTheme == SurfaceTheme.Acrylic ? 2.0f
                                       : (surfaceTheme == SurfaceTheme.CandyGlass ? 1.0f : 0.0f),
                            ["u_sheen"] = Math.Clamp(cornerSheen, 0.0f, 1.0f)
                        };
                        var children = new SKRuntimeEffectChildren(effect)
                        {
                            ["u_blur"] = blurShader
                        };

                        using SKShader finalShader = effect.ToShader(uniforms, children);
                        if (finalShader == null) return false;

                        if (_outputBitmap == null || _outputBitmap.Width != capW || _outputBitmap.Height != capH)
                        {
                            _outputBitmap?.Dispose();
                            _outputBitmap = new Bitmap(capW, capH, PixelFormat.Format32bppPArgb);
                        }

                        BitmapData bd = _outputBitmap.LockBits(new Rectangle(0, 0, capW, capH), ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
                        try
                        {
                            using SKSurface outSurf = SKSurface.Create(premulInfo, bd.Scan0, bd.Stride);
                            using var outPaint = new SKPaint { Shader = finalShader, IsAntialias = true };
                            outSurf.Canvas.DrawRect(0, 0, capW, capH, outPaint);
                            outSurf.Flush();

                            double lumaSum = 0, lumaSqSum = 0;
                            int sampleCount = 0;
                            ulong outHash = 14695981039346656037UL;
                            int sampleStep = Math.Max(1, totalPixels / 32);
                            for (int idx = 0; idx < totalPixels; idx += sampleStep)
                            {
                                uint px = unchecked((uint)Marshal.ReadInt32(bd.Scan0, idx * 4));
                                double luma = 0.2126 * ((px >> 16) & 0xFF) + 0.7152 * ((px >> 8) & 0xFF) + 0.0722 * (px & 0xFF);
                                lumaSum += luma;
                                lumaSqSum += luma * luma;
                                ++sampleCount;
                                outHash ^= px;
                                outHash *= 1099511628211UL;
                            }
                            if (sampleCount > 0)
                            {
                                double mean = lumaSum / sampleCount;
                                LastVariance = Math.Max(0.0, lumaSqSum / sampleCount - mean * mean);
                            }
                            LastOutputHash = outHash;
                        }
                        finally
                        {
                            _outputBitmap.UnlockBits(bd);
                        }
                    }
                    finally
                    {
                        LensComputeMs += Stopwatch.GetElapsedTime(lensStart).TotalMilliseconds;
                        ++LensComputeCount;
                        blurShader?.Dispose();
                        blurImg?.Dispose();
                    }
                }

                else
                {
                    // The desktop behind did not change enough to redo the lens, but the
                    // capture already happened: hold this frame for a full refresh window
                    // instead of grabbing the screen again on the next tick.
                    _lastRecomputeMs = now;
                }

                DrawOutput(targetGraphics, dipX, dipY, dipW, dipH);
                return true;
            }
            catch (Exception ex)
            {
                LastRenderError = ex.ToString();
                return false;
            }
        }

        private void DrawOutput(Graphics targetGraphics, float dipX, float dipY, float dipW, float dipH)
        {
            if (_outputBitmap == null) return;
            var prevInterp = targetGraphics.InterpolationMode;
            // The source is already blurred, so plain bilinear is indistinguishable from
            // HighQualityBilinear but cheaper — and this draw now happens on every frame.
            targetGraphics.InterpolationMode = InterpolationMode.Bilinear;
            targetGraphics.DrawImage(_outputBitmap, new RectangleF(dipX, dipY, dipW, dipH));
            targetGraphics.InterpolationMode = prevInterp;
        }

        public void Dispose()
        {
            ReleaseCaptureBuffer();
            _blurSurface?.Dispose();
            _blurSurface = null;
            _blurSurfW = 0;
            _blurSurfH = 0;
            _outputBitmap?.Dispose();
            _outputBitmap = null;
        }
    }
}
