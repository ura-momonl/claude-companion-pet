// Colours of the panel and the speech bubble, taken from concepts/board-v4.html (light: cream paper, dark ink; dark: follows Windows
// "app mode"). One place, so the bubble and the panel always agree.  Env PET_THEME=dark|light forces one (tests).
using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace ClaudeCompanionPet
{
    class Theme
    {
        public bool Dark;
        public Color Edge, Warn; public int Alpha = 255, ShadowA = 6, GlassA = 160;   // outline colour of the cards; how opaque the card paper is (255 = solid)
        public Color Ink, Ink2, Ink3, Paper, Paper2, Line, Teal, TealD, Amber, Blue, Aqua, Red, Orange, Claude, Codex;
        public Color Track, Div, Sel, AccText, AmBg, C1, C2, C3, C4;   // v5: bar track, hairline divider, raised/selected piece, text on the accent button, soft amber row, four data colours
        static Color C(string h) { return ColorTranslator.FromHtml(h); }
        static Color Mix(Color a, Color b, double f) { return Color.FromArgb(255, (int)Math.Round(a.R + (b.R - a.R) * f), (int)Math.Round(a.G + (b.G - a.G) * f), (int)Math.Round(a.B + (b.B - a.B) * f)); }
        // concepts/board-v5-fresh: a cool-grey card, text and fills are the ink mixed into the card colour (so they read the same on any background), 1px hairline, soft shadows
        static Theme Make(bool dk)
        {
            Theme t = new Theme { Dark = dk };
            t.Paper = C(dk ? "#1e222a" : "#f5f7fa"); Color ink = C(dk ? "#eef0f3" : "#1b1d21"), white = Color.White;
            t.Ink = ink; t.Ink2 = Mix(t.Paper, ink, dk ? .66 : .64); t.Ink3 = Mix(t.Paper, ink, dk ? .42 : .44);
            t.Paper2 = Mix(t.Paper, dk ? white : ink, dk ? .07 : .06); t.Track = Mix(t.Paper, dk ? white : ink, dk ? .13 : .10); t.Div = Mix(t.Paper, dk ? white : ink, .09);
            t.Sel = dk ? Mix(t.Paper, white, .14) : white;
            t.Line = C(dk ? "#000000" : "#141c30");
            t.Aqua = C(dk ? "#3cc7ad" : "#14a38b"); t.Amber = C(dk ? "#f0b34a" : "#e69a1c"); t.Red = C(dk ? "#ff6b61" : "#d9473f"); t.Blue = C(dk ? "#6aa4ff" : "#3a7bd5");
            t.Teal = C(dk ? "#3cc7ad" : "#12877a"); t.TealD = t.Teal; t.AccText = C(dk ? "#0b1f1c" : "#ffffff");
            t.AmBg = Mix(t.Paper, t.Amber, dk ? .16 : .15);
            t.Orange = C(dk ? "#f08a5d" : "#d2693f");
            t.Claude = C(dk ? "#f08a5d" : "#d2693f"); t.Codex = C(dk ? "#9a9ef5" : "#6a6fd8");   // Claude terracotta, Codex indigo
            t.C1 = C(dk ? "#6aa4ff" : "#5b8def"); t.C2 = C(dk ? "#3cc7ad" : "#2bb5a0"); t.C3 = C(dk ? "#b3a2ff" : "#8f7be0"); t.C4 = C(dk ? "#f0b34a" : "#e8a63a");
            t.Edge = dk ? Color.FromArgb(34, 255, 255, 255) : Color.FromArgb(26, 0, 0, 0);
            t.Warn = C(dk ? "#e0b25a" : "#a86f0e");
            t.Alpha = dk ? 238 : 244; t.ShadowA = dk ? 8 : 5; t.GlassA = dk ? 84 : 96;   // frosted glass: the card paper is mostly see-through so the blur behind it shows
            return t;
        }
        static readonly Theme light = Make(false);
        static readonly Theme dark = Make(true);
        public static string Mode;   // the user's choice from the menu: "light", "dark" (cold black glass) or null = follow Windows
        static long checkedAt; static bool isDark;
        public static Theme Current()
        {
            string force = Env.Get("PET_THEME") ?? Mode; if (force == "dark") return dark; if (force == "light") return light;
            long now = Env.Now();
            if (now - checkedAt > 2000)
            {
                checkedAt = now;
                try { object v = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1); isDark = v is int && (int)v == 0; }
                catch { isDark = false; }
            }
            return isDark ? dark : light;
        }

        // a soft shadow: stacked, slightly growing translucent rounded rectangles (no hard offset block); alpha per layer
        public static void SoftShadow(Graphics g, Theme t, float x, float y, float w, float h, float r, int blur, float dx, float dy) { SoftShadow(g, t, x, y, w, h, r, blur, dx, dy, t.ShadowA); }
        public static void SoftShadow(Graphics g, Theme t, float x, float y, float w, float h, float r, int blur, float dx, float dy, int alpha)
        {
            for (int i = blur; i >= 1; i--)
                using (GraphicsPath p = Round(x + dx - i * 0.8f, y + dy - i * 0.8f, w + i * 1.6f, h + i * 1.6f, r + i * 0.8f))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(alpha, 8, 12, 24))) g.FillPath(b, p);
        }

        public static GraphicsPath Round(float x, float y, float w, float h, float r)
        {
            GraphicsPath p = new GraphicsPath(); float d = Math.Max(1f, 2 * r);
            p.AddArc(x, y, d, d, 180, 90); p.AddArc(x + w - d, y, d, d, 270, 90); p.AddArc(x + w - d, y + h - d, d, d, 0, 90); p.AddArc(x, y + h - d, d, d, 90, 90); p.CloseFigure();
            return p;
        }

        // rounded-square state icon with a drawn glyph (board-v4: "!" needs you, "◐" working, "✓" done, "✕" error; "?" a question, a ring = ready)
        public static void Icon(Graphics g, Theme t, string kind, float x, float y, float d, float k)
        {
            Color fill = kind == "wait" ? t.Amber : kind == "answer" ? t.Teal : kind == "busy" ? t.Blue : kind == "done" ? t.Aqua : kind == "error" ? t.Red : kind == "quota" ? C("#8a7fa8") : t.Ink3;
            Color glyph = kind == "wait" ? C("#22313a") : Color.White;
            SmoothingMode sm = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath p = Round(x, y, d, d, d * 0.5f)) using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, p);
            float cx = x + d / 2f, cy = y + d / 2f, u = d / 22f;
            using (Pen w = new Pen(glyph, Math.Max(1.8f, d / 9f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            using (SolidBrush wb = new SolidBrush(glyph))
            {
                if (kind == "wait") { g.DrawLine(w, cx, cy - 6 * u, cx, cy + 1.5f * u); g.FillEllipse(wb, cx - 1.7f * u, cy + 4f * u, 3.4f * u, 3.4f * u); }
                else if (kind == "answer") { g.DrawArc(w, cx - 3.6f * u, cy - 6.5f * u, 7.2f * u, 7.2f * u, 200, 250); g.DrawLine(w, cx, cy + 0.5f * u, cx, cy + 2f * u); g.FillEllipse(wb, cx - 1.5f * u, cy + 4.4f * u, 3f * u, 3f * u); }
                else if (kind == "error") { g.DrawLine(w, cx - 4 * u, cy - 4 * u, cx + 4 * u, cy + 4 * u); g.DrawLine(w, cx + 4 * u, cy - 4 * u, cx - 4 * u, cy + 4 * u); }
                else if (kind == "busy") { g.DrawEllipse(w, cx - 5 * u, cy - 5 * u, 10 * u, 10 * u); g.FillPie(wb, cx - 5 * u, cy - 5 * u, 10 * u, 10 * u, 90, 180); }
                else if (kind == "done") g.DrawLines(w, new PointF[] { new PointF(cx - 5 * u, cy), new PointF(cx - 1.5f * u, cy + 4 * u), new PointF(cx + 5.5f * u, cy - 4 * u) });
                else if (kind == "quota") { foreach (float dx in new float[] { -4.5f, 0, 4.5f }) g.FillEllipse(wb, cx + dx * u - 1.5f * u, cy - 1.5f * u, 3f * u, 3f * u); }
                else g.DrawEllipse(w, cx - 4 * u, cy - 4 * u, 8 * u, 8 * u);
            }
            g.SmoothingMode = sm;
        }
        public static string KindOf(Item it)
        {
            if (it.State == "error") return "error";
            if (it.State == "waiting") return it.Detail == "answer" ? "answer" : "wait";
            if (it.State == "busy") return "busy";
            if (it.State == "done") return "done";
            return "idle";
        }
    }
}
