using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace OpenPixel.Rendering
{
    // ========================================================================
    // Geometric Anurati-style stencil display face for the desktop Day Clock.
    //
    // Matches the reference screenshots in /Images: thick geometric strokes,
    // open/detached stencil terminals (A without crossbar, open D/R, stemless E,
    // asymmetric Y, curved S/U), and crisp whole-pixel snapping at any DPI.
    // ========================================================================
    public static class StencilFont
    {
        private const float GlyphW = 5.0f;
        private const float GlyphH = 7.0f;
        private const float SpaceW = 2.6f;

        private static readonly HashSet<char> Supported = new()
        {
            'A', 'D', 'E', 'F', 'H', 'I', 'M', 'N', 'O', 'R', 'S', 'T', 'U', 'W', 'Y'
        };

        public static bool Supports(char c) => Supported.Contains(char.ToUpperInvariant(c));

        public static bool SupportsAll(string text)
        {
            foreach (char c in text)
            {
                if (c == ' ') continue;
                if (!Supports(c)) return false;
            }
            return !string.IsNullOrEmpty(text);
        }

        public static float WidthOf(string text, float capHeight, float tracking)
        {
            if (string.IsNullOrEmpty(text) || capHeight <= 0) return 0.0f;
            float unit = capHeight / GlyphH;
            float total = 0.0f;
            foreach (char c in text)
            {
                total += (c == ' ' ? SpaceW : GlyphW) * unit + tracking;
            }
            if (total > 0.0f) total -= tracking;
            return total;
        }

        public static void Draw(
            Graphics g, string text, float centerX, float top, float capHeight,
            float tracking, Color color, float weightScale = 1.0f)
        {
            if (g == null || string.IsNullOrEmpty(text) || capHeight <= 0.5f) return;
            float unit = capHeight / GlyphH;
            float total = WidthOf(text, capHeight, tracking);
            float x = MathF.Round(centerX - total * 0.5f);
            float y = MathF.Round(top);
            float stroke = Math.Max(1.4f, capHeight * 0.132f * Math.Clamp(weightScale, 0.6f, 2.0f));

            SmoothingMode prevSmooth = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using var pen = new Pen(color, stroke)
            {
                StartCap = LineCap.Flat,
                EndCap = LineCap.Flat,
                LineJoin = LineJoin.Miter,
                MiterLimit = 1.6f
            };
            using var roundPen = new Pen(color, stroke)
            {
                StartCap = LineCap.Flat,
                EndCap = LineCap.Flat,
                LineJoin = LineJoin.Round
            };

            for (int i = 0; i < text.Length; ++i)
            {
                char c = char.ToUpperInvariant(text[i]);
                if (c == ' ')
                {
                    x += SpaceW * unit + tracking;
                    continue;
                }
                // Windhawk Mond Desktop Clock Anurati D-A kerning (-14 DIP scaled to capHeight)
                if (i > 0 && c == 'A' && char.ToUpperInvariant(text[i - 1]) == 'D')
                {
                    x -= capHeight * 0.19f;
                }
                if (!Supported.Contains(c))
                {
                    g.DrawRectangle(pen, x + unit, y + unit, GlyphW * unit - unit * 2.0f, capHeight - unit * 2.0f);
                    x += GlyphW * unit + tracking;
                    continue;
                }

                DrawGlyph(g, pen, roundPen, c, x, y, unit);
                x += GlyphW * unit + tracking;
            }

            g.SmoothingMode = prevSmooth;
        }

        private static void DrawGlyph(Graphics g, Pen pen, Pen roundPen, char c, float x, float y, float u)
        {
            float Px(float ux) => x + ux * u;
            float Py(float uy) => y + uy * u;

            switch (c)
            {
                case 'A':
                {
                    // Exact Anurati 'A': two converging diagonal legs with NO crossbar and an open apex gap
                    g.DrawLine(pen, Px(0.15f), Py(7.0f), Px(2.15f), Py(0.75f));
                    g.DrawLine(pen, Px(2.85f), Py(0.75f), Px(4.85f), Py(7.0f));
                    break;
                }
                case 'D':
                {
                    // Open left side: flat top, smooth semicircle right, flat bottom
                    using var p = new GraphicsPath();
                    p.AddLine(Px(0.2f), Py(0.35f), Px(1.85f), Py(0.35f));
                    p.AddArc(Px(-1.45f), Py(0.35f), 6.25f * u, 6.3f * u, -90, 180);
                    p.AddLine(Px(1.85f), Py(6.65f), Px(0.2f), Py(6.65f));
                    g.DrawPath(roundPen, p);
                    break;
                }
                case 'E':
                {
                    // Three crisp horizontal bars, no vertical stem
                    g.DrawLine(pen, Px(0.2f), Py(0.45f), Px(4.8f), Py(0.45f));
                    g.DrawLine(pen, Px(0.2f), Py(3.5f),  Px(4.3f), Py(3.5f));
                    g.DrawLine(pen, Px(0.2f), Py(6.55f), Px(4.8f), Py(6.55f));
                    break;
                }
                case 'F':
                {
                    g.DrawLine(pen, Px(0.2f), Py(0.45f), Px(4.8f), Py(0.45f));
                    g.DrawLine(pen, Px(0.45f), Py(3.5f), Px(4.1f), Py(3.5f));
                    g.DrawLine(pen, Px(0.45f), Py(3.05f), Px(0.45f), Py(7.0f));
                    break;
                }
                case 'H':
                {
                    g.DrawLine(pen, Px(0.45f), Py(2.8f), Px(0.45f), Py(7.0f));
                    g.DrawLine(pen, Px(0.45f), Py(3.5f), Px(4.55f), Py(3.5f));
                    g.DrawLine(pen, Px(4.55f), Py(0.0f), Px(4.55f), Py(7.0f));
                    break;
                }
                case 'I':
                {
                    g.DrawLine(pen, Px(2.5f), Py(0.0f), Px(2.5f), Py(7.0f));
                    break;
                }
                case 'M':
                {
                    // Exact Anurati 'M': no left vertical stem — diagonal down-right, up-right, then full right vertical stem
                    using var p = new GraphicsPath();
                    p.AddLine(Px(0.25f), Py(0.35f), Px(2.5f), Py(4.35f));
                    p.AddLine(Px(2.5f), Py(4.35f), Px(4.65f), Py(0.35f));
                    p.AddLine(Px(4.65f), Py(0.35f), Px(4.65f), Py(7.0f));
                    g.DrawPath(pen, p);
                    break;
                }
                case 'N':
                {
                    // Exact Anurati 'N': no left vertical stem — diagonal joining full right vertical stem
                    g.DrawLine(pen, Px(0.35f), Py(0.35f), Px(4.55f), Py(6.65f));
                    g.DrawLine(pen, Px(4.55f), Py(0.0f), Px(4.55f), Py(7.0f));
                    break;
                }
                case 'O':
                {
                    g.DrawEllipse(roundPen, Px(0.25f), Py(0.35f), 4.5f * u, 6.3f * u);
                    break;
                }
                case 'R':
                {
                    // Open left side: top bar, curved bowl, diagonal leg
                    using var p = new GraphicsPath();
                    p.AddLine(Px(0.2f), Py(0.35f), Px(2.6f), Py(0.35f));
                    p.AddArc(Px(0.75f), Py(0.35f), 3.85f * u, 3.45f * u, -90, 180);
                    p.AddLine(Px(2.6f), Py(3.8f), Px(0.7f), Py(3.8f));
                    g.DrawPath(roundPen, p);
                    g.DrawLine(pen, Px(2.2f), Py(3.8f), Px(4.75f), Py(7.0f));
                    break;
                }
                case 'S':
                {
                    using var p = new GraphicsPath();
                    p.AddLine(Px(4.6f), Py(0.4f), Px(2.1f), Py(0.4f));
                    p.AddArc(Px(0.35f), Py(0.4f), 3.2f * u, 3.05f * u, -90, -180);
                    p.AddLine(Px(2.0f), Py(3.45f), Px(3.05f), Py(3.45f));
                    p.AddArc(Px(1.45f), Py(3.45f), 3.2f * u, 3.15f * u, -90, 180);
                    p.AddLine(Px(3.05f), Py(6.6f), Px(0.4f), Py(6.6f));
                    g.DrawPath(roundPen, p);
                    break;
                }
                case 'T':
                {
                    // Exact Anurati 'T': left vertical stem + detached top-right horizontal arm
                    g.DrawLine(pen, Px(1.1f), Py(0.35f), Px(1.1f), Py(7.0f));
                    g.DrawLine(pen, Px(2.25f), Py(0.45f), Px(4.9f), Py(0.45f));
                    break;
                }
                case 'U':
                {
                    using var p = new GraphicsPath();
                    p.AddLine(Px(0.4f), Py(0.0f), Px(0.4f), Py(4.45f));
                    p.AddArc(Px(0.4f), Py(2.25f), 4.2f * u, 4.4f * u, 180, -180);
                    p.AddLine(Px(4.6f), Py(4.45f), Px(4.6f), Py(2.85f));
                    g.DrawPath(roundPen, p);
                    break;
                }
                case 'W':
                {
                    // Exact Anurati 'W': left diagonal '\' + detached right 'V'
                    g.DrawLine(pen, Px(0.15f), Py(0.0f), Px(1.45f), Py(6.65f));
                    using var p = new GraphicsPath();
                    p.AddLine(Px(2.15f), Py(0.35f), Px(3.45f), Py(6.45f));
                    p.AddLine(Px(3.45f), Py(6.45f), Px(4.85f), Py(0.0f));
                    g.DrawPath(pen, p);
                    break;
                }
                case 'Y':
                {
                    // Exact Anurati 'Y': full diagonal from top-right to bottom-left + detached short upper-left diagonal
                    g.DrawLine(pen, Px(0.35f), Py(0.25f), Px(1.85f), Py(2.55f));
                    g.DrawLine(pen, Px(4.7f), Py(0.25f), Px(0.6f), Py(7.0f));
                    break;
                }
            }
        }
    }
}
