using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;

namespace OpenPixel.Rendering
{
    // The brand mark, embedded in the assembly (assets/island/logo.png).
    // Loaded once and shared by the settings window, the onboarding wizard and the tray icon.
    internal static class BrandLogo
    {
        private static Bitmap _mark;
        private static bool _tried;
        private static readonly object Gate = new object();

        public static Bitmap Mark()
        {
            if (_tried) return _mark;
            lock (Gate)
            {
                if (_tried) return _mark;
                _tried = true;
                try
                {
                    using Stream stream = Assembly.GetExecutingAssembly()
                        .GetManifestResourceStream("OpenPixel.Island.logo.png");
                    if (stream != null)
                    {
                        using var loaded = new Bitmap(stream);
                        _mark = new Bitmap(loaded);
                    }
                }
                catch { _mark = null; }
                return _mark;
            }
        }

        // Draws the mark centred inside dest, keeping its aspect ratio and transparency.
        public static bool Draw(Graphics g, RectangleF dest)
        {
            Bitmap mark = Mark();
            if (mark == null || g == null || dest.Width <= 0.0f || dest.Height <= 0.0f) return false;
            InterpolationMode prevInterpolation = g.InterpolationMode;
            PixelOffsetMode prevOffset = g.PixelOffsetMode;
            try
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                // Never stretch the mark: fit it inside the slot and centre the result.
                float scale = Math.Min(dest.Width / mark.Width, dest.Height / mark.Height);
                float w = mark.Width * scale;
                float h = mark.Height * scale;
                var target = new RectangleF(
                    dest.X + (dest.Width - w) * 0.5f,
                    dest.Y + (dest.Height - h) * 0.5f,
                    w, h);
                g.DrawImage(mark, target);

                g.InterpolationMode = prevInterpolation;
                g.PixelOffsetMode = prevOffset;
            }
            catch
            {
                g.InterpolationMode = prevInterpolation;
                g.PixelOffsetMode = prevOffset;
                return false;
            }
            return true;
        }

        // The multi-size app icon: the taskbar, window title bars, the tray and Explorer
        // all get the same crisp brand mark instead of the generic fallback icon.
        private static Icon LoadIcon(int width, int height)
        {
            try
            {
                using Stream stream = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("OpenPixel.Brand.OpenPixel.ico");
                if (stream == null) return null;
                using var loaded = new Icon(stream, new Size(width, height));
                return (Icon)loaded.Clone();
            }
            catch { return null; }
        }

        public static Icon AppIcon() => LoadIcon(32, 32);

        public static Icon WindowIcon() => LoadIcon(32, 32);

        public static Icon TrayIcon() => LoadIcon(16, 16);
    }
}
