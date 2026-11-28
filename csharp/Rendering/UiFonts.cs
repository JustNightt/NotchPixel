using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace OpenPixel.Rendering
{
    // ========================================================================
    // Unified Typography for NotchPixel:
    // - All 6 selectable fonts are embedded directly inside the assembly:
    //   0: Geist (main default), 1: Inter, 2: Roboto, 3: Noto Sans, 4: Manrope, 5: Nunito
    // - Mono / tabular numbers always use embedded GeistMono-Medium / Regular.
    // - Day Clock display uses embedded Anurati-Regular.otf.
    // ========================================================================
    public static class UiFonts
    {
        public static readonly string[] FontNames =
        {
            "Geist", "Inter", "Roboto", "Noto Sans", "Manrope", "Nunito"
        };

        private static readonly object Sync = new();
        private static readonly string[] EmbeddedFonts =
        {
            "OpenPixel.Fonts.Geist-Regular.ttf",
            "OpenPixel.Fonts.Geist-Medium.ttf",
            "OpenPixel.Fonts.Geist-SemiBold.ttf",
            "OpenPixel.Fonts.Geist-Bold.ttf",
            "OpenPixel.Fonts.GeistMono-Regular.ttf",
            "OpenPixel.Fonts.GeistMono-Medium.ttf",
            "OpenPixel.Fonts.GeistPixel-Square.ttf",
            "OpenPixel.Fonts.Anurati-Regular.otf",
            "OpenPixel.Fonts.Quicksand-Bold.otf",
            "OpenPixel.Fonts.Inter-Regular.ttf",
            "OpenPixel.Fonts.Inter-Medium.ttf",
            "OpenPixel.Fonts.Inter-SemiBold.ttf",
            "OpenPixel.Fonts.Inter-Bold.ttf",
            "OpenPixel.Fonts.Roboto-Regular.ttf",
            "OpenPixel.Fonts.Roboto-Medium.ttf",
            "OpenPixel.Fonts.Roboto-SemiBold.ttf",
            "OpenPixel.Fonts.Roboto-Bold.ttf",
            "OpenPixel.Fonts.NotoSans-Regular.ttf",
            "OpenPixel.Fonts.NotoSans-Medium.ttf",
            "OpenPixel.Fonts.NotoSans-SemiBold.ttf",
            "OpenPixel.Fonts.NotoSans-Bold.ttf",
            "OpenPixel.Fonts.Manrope-Regular.ttf",
            "OpenPixel.Fonts.Manrope-Medium.ttf",
            "OpenPixel.Fonts.Manrope-SemiBold.ttf",
            "OpenPixel.Fonts.Manrope-Bold.ttf",
            "OpenPixel.Fonts.Nunito-Regular.ttf",
            "OpenPixel.Fonts.Nunito-Medium.ttf",
            "OpenPixel.Fonts.Nunito-SemiBold.ttf",
            "OpenPixel.Fonts.Nunito-Bold.ttf"
        };

        private static bool _initialized;
        private static PrivateFontCollection _collection;
        private static readonly FontFamily[] _regularByIdx = new FontFamily[6];
        private static readonly FontFamily[] _boldByIdx = new FontFamily[6];
        private static FontFamily _mono;
        private static FontFamily _anurati;
        private static FontFamily _quicksand;

        public const string FamilyName = "Geist";

        public static bool IsGeist
        {
            get { EnsureInitialized(); return _regularByIdx[0] != null; }
        }

        public static float SizeScaleFactor
        {
            get { EnsureInitialized(); return _regularByIdx[0] != null ? 1.0f : 0.94f; }
        }

        public static bool IsAvailable
        {
            get { EnsureInitialized(); return _regularByIdx[0] != null || _regularByIdx[1] != null; }
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

                        IntPtr memory = Marshal.AllocCoTaskMem(bytes.Length);
                        Marshal.Copy(bytes, 0, memory, bytes.Length);
                        collection.AddMemoryFont(memory, bytes.Length);
                    }

                    _collection = collection;
                    foreach (FontFamily family in collection.Families)
                    {
                        string name = family.Name;
                        if (name.IndexOf("Mono", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            _mono ??= family;
                            continue;
                        }
                        if (name.IndexOf("Anurati", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            _anurati ??= family;
                            continue;
                        }
                        if (name.IndexOf("Quicksand", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            _quicksand ??= family;
                            continue;
                        }
                        if (name.IndexOf("Pixel", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                        bool isBoldFam = name.IndexOf("SemiBold", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                         name.IndexOf("Bold", StringComparison.OrdinalIgnoreCase) >= 0;

                        int slot = name.IndexOf("Geist", StringComparison.OrdinalIgnoreCase) >= 0 ? 0
                                 : name.IndexOf("Inter", StringComparison.OrdinalIgnoreCase) >= 0 ? 1
                                 : name.IndexOf("Roboto", StringComparison.OrdinalIgnoreCase) >= 0 ? 2
                                 : name.IndexOf("Noto", StringComparison.OrdinalIgnoreCase) >= 0 ? 3
                                 : name.IndexOf("Manrope", StringComparison.OrdinalIgnoreCase) >= 0 ? 4
                                 : name.IndexOf("Nunito", StringComparison.OrdinalIgnoreCase) >= 0 ? 5
                                 : -1;

                        if (slot >= 0)
                        {
                            if (isBoldFam) _boldByIdx[slot] ??= family;
                            else _regularByIdx[slot] ??= family;
                        }
                    }

                    for (int i = 0; i < 6; ++i)
                    {
                        _regularByIdx[i] ??= _boldByIdx[i] ?? _regularByIdx[0];
                        _boldByIdx[i] ??= _regularByIdx[i] ?? _boldByIdx[0];
                    }
                }
                catch
                {
                    Array.Clear(_regularByIdx);
                    Array.Clear(_boldByIdx);
                    _mono = null;
                    _anurati = null;
                }
            }
        }

        public static void EnsureFontAvailable(int fontIndex, Action onReady = null)
        {
            EnsureInitialized();
            onReady?.Invoke();
        }

        public static Font GetQuicksand(float sizePoints)
        {
            EnsureInitialized();
            float size = Math.Max(8.0f, sizePoints);
            if (_quicksand != null)
            {
                try
                {
                    FontStyle style = _quicksand.IsStyleAvailable(FontStyle.Bold) ? FontStyle.Bold : FontStyle.Regular;
                    return new Font(_quicksand, size, style, GraphicsUnit.Pixel);
                }
                catch { }
            }
            return Get(sizePoints * 0.75f, true, false);
        }

        public static Font GetAnurati(float sizePoints)
        {
            EnsureInitialized();
            float size = Math.Max(8.0f, sizePoints);
            if (_anurati != null)
            {
                try { return new Font(_anurati, size, FontStyle.Regular, GraphicsUnit.Pixel); }
                catch { }
            }
            return Get(sizePoints * 0.75f, false, false);
        }

        public static Font Get(float sizePoints, bool bold, bool mono = false)
        {
            EnsureInitialized();
            float userScale = 1.0f;
            int fontIndex = 0;
            try
            {
                var s = OpenPixel.Core.GlobalAppState.Instance.Settings;
                userScale = Math.Clamp(s.TextScale, 0.8f, 1.45f);
                fontIndex = Math.Clamp(s.FontIndex, 0, FontNames.Length - 1);
            }
            catch { }

            float size = Math.Max(4.0f, sizePoints * 0.94f * userScale);
            if (mono)
            {
                if (_mono != null) return new Font(_mono, size, FontStyle.Regular);
                return new Font("Consolas", size, bold ? FontStyle.Bold : FontStyle.Regular);
            }

            try
            {
                FontFamily reg = _regularByIdx[fontIndex] ?? _regularByIdx[0];
                FontFamily bld = _boldByIdx[fontIndex] ?? _boldByIdx[0] ?? reg;
                if (reg != null)
                {
                    FontFamily chosen = bold ? bld : reg;
                    FontStyle style = (bold && chosen == reg && chosen.IsStyleAvailable(FontStyle.Bold))
                        ? FontStyle.Bold : FontStyle.Regular;
                    return new Font(chosen, size, style);
                }
                return new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular);
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
                Array.Clear(_regularByIdx);
                Array.Clear(_boldByIdx);
                _mono = null;
                _anurati = null;
                _collection?.Dispose();
                _collection = null;
                _initialized = false;
            }
        }
    }
}
