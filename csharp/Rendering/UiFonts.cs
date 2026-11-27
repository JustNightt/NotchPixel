using System;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace OpenPixel.Rendering
{
    // ========================================================================
    // Geist — the island's own type family on notchpixel.com — ships inside the
    // assembly, with Inter kept as the fallback. Both are embedded so every
    // window, island and widget draws the same family whether or not the fonts
    // are installed on the machine.
    // ========================================================================
    public static class UiFonts
    {
        private static readonly object Sync = new();
        private static readonly string[] EmbeddedFonts =
        {
            "OpenPixel.Fonts.Geist-Regular.ttf",
            "OpenPixel.Fonts.Geist-SemiBold.ttf",
            "OpenPixel.Fonts.GeistMono-Medium.ttf",
            "OpenPixel.Fonts.Inter-Regular.ttf",
            "OpenPixel.Fonts.Inter-Medium.ttf",
            "OpenPixel.Fonts.Inter-SemiBold.ttf",
            "OpenPixel.Fonts.Inter-Bold.ttf"
        };

        private static bool _initialized;
        private static PrivateFontCollection _collection;
        private static FontFamily _regular;
        private static FontFamily _bold;
        private static FontFamily _mono;
        private static bool _usingGeist;
        private static float _sizeScale = 1.0f;

        public const string FamilyName = "Geist";

        public static bool IsGeist
        {
            get { EnsureInitialized(); return _usingGeist; }
        }

        public static float SizeScaleFactor
        {
            get { EnsureInitialized(); return _sizeScale; }
        }

        public static bool IsAvailable
        {
            get { EnsureInitialized(); return _regular != null; }
        }

        public static string MonoFamilyName
        {
            get { EnsureInitialized(); return _mono?.Name ?? "(none)"; }
        }

        private static void EnsureInitialized()
        {
            if (_initialized) return;
            lock (Sync)
            {
                if (_initialized) return;
                _initialized = true;
                try
                {
                    var collection = new PrivateFontCollection();
                    Assembly assembly = typeof(UiFonts).Assembly;
                    foreach (string resource in EmbeddedFonts)
                    {
                        using Stream stream = assembly.GetManifestResourceStream(resource);
                        if (stream == null) continue;
                        byte[] bytes = new byte[stream.Length];
                        int offset = 0;
                        while (offset < bytes.Length)
                        {
                            int read = stream.Read(bytes, offset, bytes.Length - offset);
                            if (read <= 0) break;
                            offset += read;
                        }
                        if (offset < bytes.Length) Array.Resize(ref bytes, offset);

                        // The font collection reads from this buffer for its whole
                        // lifetime, so the unmanaged block is intentionally kept.
                        IntPtr memory = Marshal.AllocCoTaskMem(bytes.Length);
                        Marshal.Copy(bytes, 0, memory, bytes.Length);
                        collection.AddMemoryFont(memory, bytes.Length);
                    }

                    if (collection.Families.Length == 0) return;
                    _collection = collection;

                    FontFamily geist = null, geistBold = null, inter = null, interBold = null, mono = null;
                    foreach (FontFamily family in collection.Families)
                    {
                        string name = family.Name;
                        if (name.IndexOf("Mono", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            mono ??= family;
                            continue;
                        }
                        if (name.IndexOf("Geist", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            if (name.IndexOf("SemiBold", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                name.IndexOf("Bold", StringComparison.OrdinalIgnoreCase) >= 0)
                                geistBold ??= family;
                            else
                                geist ??= family;
                        }
                        else if (name.IndexOf("Inter", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            if (name.IndexOf("SemiBold", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                name.IndexOf("Bold", StringComparison.OrdinalIgnoreCase) >= 0)
                                interBold ??= family;
                            else
                                inter ??= family;
                        }
                    }

                    _usingGeist = geist != null || geistBold != null;
                    _regular = geist ?? geistBold ?? inter ?? interBold;
                    _bold = geistBold ?? geist ?? interBold ?? inter;
                    _mono = mono;
                    // Geist's advances match the demo frame for frame; Inter is a touch
                    // wider, so its point sizes keep the old 0.94 compensation.
                    _sizeScale = _usingGeist ? 1.0f : 0.94f;
                }
                catch
                {
                    _regular = null;
                    _bold = null;
                    _mono = null;
                    _usingGeist = false;
                    _sizeScale = 0.94f;
                }
            }
        }

        // Returns Arial at the requested point size scaled by Settings.TextScale.
        public static Font Get(float sizePoints, bool bold, bool mono = false)
        {
            EnsureInitialized();
            float userScale = 1.0f;
            try { userScale = Math.Clamp(OpenPixel.Core.GlobalAppState.Instance.Settings.TextScale, 0.8f, 1.45f); } catch { }
            float size = Math.Max(4.0f, sizePoints * 0.94f * userScale);
            if (mono)
            {
                if (_mono != null) return new Font(_mono, size, FontStyle.Regular);
                return new Font("Consolas", size, bold ? FontStyle.Bold : FontStyle.Regular);
            }
            try
            {
                return new Font("Arial", size, bold ? FontStyle.Bold : FontStyle.Regular);
            }
            catch
            {
                return new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular);
            }
        }

        public static void Release()
        {
            lock (Sync)
            {
                _regular = null;
                _bold = null;
                _mono = null;
                _collection?.Dispose();
                _collection = null;
                _initialized = false;
            }
        }
    }
}
