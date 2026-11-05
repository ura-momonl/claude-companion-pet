// The panel next to the pet: layout of concepts/board-v4 (3 px dark outline, hard offset shadow, cream paper, tab strip 剩余 / 上下文 / 用量, rounded-square
// state icons, amber highlight for the session that needs you with a small "回到窗口 ↗" button, Claude/Codex pill after the project name).
// Everything it draws comes from the Board that Display.Build made out of Backend.ReadSnapshot(): page 1 quota windows + the session list, page 2 the
// context of one session, page 3 token usage and the API-equivalent price. A figure that is unknown, stale, still being counted or only partly priced
// is drawn as exactly that. A click on a row asks the backend to jump (Backend.TryJump); the panel never probes windows or processes itself.
// Its own borderless, per-pixel-alpha, top-most, never-activated window. Hover over the pet shows it, a click on the pet pins it.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;

namespace ClaudeCompanionPet
{
    class PanelForm : Form
    {
        public const string WindowTitle = "claude-companion-panel";
        const int WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_TOPMOST = 8, WS_EX_NOACTIVATE = 0x08000000;
        const int WM_MOUSEACTIVATE = 0x21, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_MOUSEMOVE = 0x200, WM_MOUSELEAVE = 0x2A;
        static readonly string[] PERIODS = { "today", "7d", "30d" };

        readonly PetForm pet; readonly string fontName; readonly float k;
        public bool Up;                                   // window is on screen
        public bool Hover;                                // the pointer is over the panel
        public int X, Y, W, H;                            // screen rectangle (of the whole picture, shadow included)
        readonly List<Rectangle> rowRects = new List<Rectangle>();   // client coordinates of the clickable rows (same order as items; rows without a window are present but not links)
        readonly List<Rectangle> chipRects = new List<Rectangle>(); readonly List<string> chipKeys = new List<string>();
        readonly Rectangle[] tabRects = new Rectangle[3];
        Board board = new Board(); List<Item> items = new List<Item>();
        int hoverRow = -1, hoverTab = -1, hoverChip = -1, tab; string lastSig = "";
        Rectangle pinRect = Rectangle.Empty; bool hoverPin;
        string infoText = ""; Rectangle infoRect = Rectangle.Empty; bool hoverInfo;   // the (i) mark: its explanatory note shows while the pointer rests on it
        public bool Dragging { get { return dragged; } }   // the user is carrying the window
        Bitmap cache; byte lastAlpha = 255; long fadeStart; const int FADE = 150;   // the picture fades in when the window appears or switches between strip and panel
        public static bool GlassOn = Env.Get("PET_GLASS") != "off";   // frosted glass (the system blurs what is behind the card); off = solid card with soft shadows. Falls back by itself when the system refuses
        bool glassOk, glassDark; int glassW, glassH, glassMode;   // glassMode 2: the system rounds the corners (always 8 px at 100%)
        public bool Expanded;                             // false: the small floating strip (remaining quota only); true: the full three-page panel
        int compW, compH; bool md, dragged; Point mdScr;
        string ctxSel = "", period = "7d", usageBy = "who";   // usageBy: who = split by assistant / model, kind = split by token kind
        public int Tab { get { return tab; } }
        public int RowCount { get { return rowRects.Count; } }
        public Rectangle RowRect(int i) { return i >= 0 && i < rowRects.Count ? rowRects[i] : Rectangle.Empty; }
        public string TabsScreen() { List<string> l = new List<string>(); foreach (Rectangle r in tabRects) l.Add((X + r.X) + "," + (Y + r.Y) + "," + r.Width + "," + r.Height); return string.Join("|", l.ToArray()); }
        public string ChipsScreen() { List<string> l = new List<string>(); for (int i = 0; i < chipRects.Count; i++) l.Add(chipKeys[i] + "@" + (X + chipRects[i].X) + "," + (Y + chipRects[i].Y) + "," + chipRects[i].Width + "," + chipRects[i].Height); return string.Join("|", l.ToArray()); }
        public string RowsScreen() { List<string> l = new List<string>(); foreach (Rectangle r in rowRects) l.Add((X + r.X) + "," + (Y + r.Y) + "," + r.Width + "," + r.Height); return string.Join("|", l.ToArray()); }

        // fonts: sizes are never below the concept board (name 13.5 px, status 12 px, tabs 14 px, small text 12 px)
        readonly Font fName, fStat, fTab, fTabOn, fSmall, fSmallB, fBtn, fPill, fNote, fBig, fBig2, fHuge, fInfo;
        readonly StringFormat sfLeft, sfRight, sfMid;

        public PanelForm(PetForm owner, string font, float scale)
        {
            pet = owner; fontName = font; k = scale;
            fName = new Font(fontName, 12.5f * k, FontStyle.Bold, GraphicsUnit.Pixel);
            fStat = new Font(fontName, 12f * k, FontStyle.Regular, GraphicsUnit.Pixel);
            fTab = new Font(fontName, 12.5f * k, FontStyle.Regular, GraphicsUnit.Pixel);
            fTabOn = new Font(fontName, 12.5f * k, FontStyle.Bold, GraphicsUnit.Pixel);
            fSmall = new Font(fontName, 12f * k, FontStyle.Regular, GraphicsUnit.Pixel);
            fSmallB = new Font(fontName, 12f * k, FontStyle.Bold, GraphicsUnit.Pixel);
            fBtn = new Font(fontName, 12f * k, FontStyle.Bold, GraphicsUnit.Pixel);
            fPill = new Font(fontName, 12.5f * k, FontStyle.Bold, GraphicsUnit.Pixel);
            fNote = new Font(fontName, 11.5f * k, FontStyle.Regular, GraphicsUnit.Pixel);
            fBig = new Font(fontName, 12.5f * k, FontStyle.Bold, GraphicsUnit.Pixel);
            fBig2 = new Font(fontName, 19f * k, FontStyle.Bold, GraphicsUnit.Pixel);
            fHuge = new Font(fontName, 34f * k, FontStyle.Bold, GraphicsUnit.Pixel);
            fInfo = new Font(fontName, 8.5f * k, FontStyle.Bold, GraphicsUnit.Pixel);
            sfLeft = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter, LineAlignment = StringAlignment.Center };
            sfRight = new StringFormat(StringFormatFlags.NoWrap) { Alignment = StringAlignment.Far, Trimming = StringTrimming.EllipsisCharacter, LineAlignment = StringAlignment.Center };
            sfMid = new StringFormat(StringFormatFlags.NoWrap) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
            Text = WindowTitle; Size = new Size(10, 10);
            IntPtr h = Handle;
        }
        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE; return cp; }
        }
        protected override bool ShowWithoutActivation { get { return true; } }

        // ---------- texts ----------
        public static string Dur(long ms)
        {
            long s = Math.Max(0, ms / 1000);
            if (s < 60) return Lang.T(s + " 秒", s + " s");
            long m = s / 60; if (m < 60) return Lang.T(m + " 分钟", m + " min");
            long h = m / 60; return Lang.T(h + " 小时" + (m % 60 > 0 ? " " + (m % 60) + " 分" : ""), h + " h" + (m % 60 > 0 ? " " + (m % 60) + " min" : ""));
        }
        public static string DetailOf(Item it, long now)
        {
            long age = it.Since > 0 ? now - it.Since : -1;
            string t;
            if (it.StateQuality != "observed" && it.StateQuality != "") t = it.Label;   // unknown / not confirmed: no invented duration
            else switch (it.State)
            {
                case "waiting": case "error": t = it.Label + (age >= 0 ? " · " + Dur(age) : ""); break;
                case "busy": t = Lang.T("在忙", "working") + (age >= 0 ? " · " + Dur(age) : ""); break;
                case "done": t = Lang.T("做完了", "done") + (age >= 0 ? " · " + Lang.T(Dur(age) + "前", Dur(age) + " ago") : ""); break;
                default: t = string.IsNullOrEmpty(it.Label) ? Lang.T("待命", "ready") : it.Label; break;
            }
            if (it.Ctx >= 0) t += " · " + Lang.T("上下文约 ", "context ~") + Math.Round(it.Ctx) + "%";
            return t;
        }
        // the backend names a session "<project> · <short id>"; the id is shown (muted) only when another session in the list has the same project name
        public static string BaseName(string name) { int i = name == null ? -1 : name.LastIndexOf(" · "); return i > 0 ? name.Substring(0, i) : name; }
        static string IdPart(string name) { int i = name == null ? -1 : name.LastIndexOf(" · "); return i > 0 ? name.Substring(i + 3) : ""; }
        bool Dup(List<Item> list, Item it) { foreach (Item o in list) if (o != it && BaseName(o.Name) == BaseName(it.Name)) return true; return false; }
        public static string NoWindowNote(Item it) { return Display.WindowNote(it.WindowReason, it.Provider); }
        static string Key(Item it) { return it.Provider + ":" + it.Id; }
        string SigText(Board b, long now)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (Item it in b.View.Items) sb.Append(it.Id).Append('|').Append(DetailOf(it, now)).Append('|').Append(it.Name).Append('|').Append(it.Agent).Append('|').Append(it.NoWin).Append('|').Append(it.WindowReason).Append(';');
            foreach (QRow q in b.Quota) sb.Append(q.Label).Append(q.Pct).Append(q.Tag).Append(q.Reset).Append(';');
            sb.Append(b.Sig).Append('#').Append(now / 5000);
            return sb + "#" + tab + "#" + ctxSel + "#" + period + "#" + usageBy + "#" + Theme.Current().Dark + "#" + (b.View.Items.Count == 0) + "#" + b.View.LiveCount + "#" + b.View.NeedCount + "#" + hoverInfo + "#" + Expanded + "#" + SimpleStrip;
        }

        // ---------- layout ----------
        int Px(float v) { return (int)Math.Round(v * k); }
        float Meas(string s, Font f) { using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) { g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit; return g.MeasureString(s, f, 4000, sfLeft).Width + Px(3); } } // same text rendering as the drawing, plus a little slack so a name is never cut at its last character
        int cardW, cardH; string layoutSig = ""; int[] rowH = new int[0]; int maxW = 0;
        int ShM { get { return GlassOn ? 0 : Px(14); } }   // room around the card for its soft shadow (none when the blur window is clipped to the card)
        int CardR(float design) { return Px(8); }   // one corner radius for the glass card (the system rounds it to 8) and for the solid fallback, so the two look alike
        static Color Fade(Color c, int a) { return Color.FromArgb(a, c.R, c.G, c.B); }

        // the card: cool-grey paper, 1px hairline, a faint top highlight, two soft shadows (near + far)
        void CardShape(Graphics g, Theme t, int w, int h, int r)
        {
            int bd = Math.Max(1, Px(0.7f)); float hb = bd / 2f;
            if (!GlassOn)
            {
                Theme.SoftShadow(g, t, hb, hb, w - bd, h - bd, r, Px(10), 0, Px(6), t.Dark ? 3 : 2);
                Theme.SoftShadow(g, t, hb, hb, w - bd, h - bd, r, Px(3), 0, Px(1), t.Dark ? 9 : 6);
            }
            using (GraphicsPath card = Theme.Round(hb, hb, w - bd, h - bd, r))
            {
                using (SolidBrush br = new SolidBrush(Fade(t.Paper, GlassOn ? t.GlassA : t.Alpha))) g.FillPath(br, card);
                using (Pen p = new Pen(t.Edge, bd)) g.DrawPath(p, card);
            }
            using (Pen hi = new Pen(Color.FromArgb(t.Dark ? 24 : 170, 255, 255, 255), 1f)) g.DrawLine(hi, r, bd + 1, w - r, bd + 1);
        }

        // ---------- the small floating strip: only the remaining quota ----------
        const int STRIP_W = 280;
        public static bool SimpleStrip = true;   // the small floating window: one line, the tightest remaining per assistant; false = the rows with bars
        static readonly string[] SIMPLE = { "claude", "codex" };
        string SimpleText(Board b, string p) { double l = b.Left(p); return l < 0 ? "\u2014" : Math.Round(l) + "%"; }
        int SimpleWidth(Board b)
        {
            int w = Px(12); bool any = false;
            foreach (string p in SIMPLE) { if (!b.Has(p)) continue; any = true; w += Px(7) + Px(6) + (int)Math.Ceiling(Meas(Display.ProviderLabel(p), fStat)) + Px(6) + (int)Math.Ceiling(Meas(SimpleText(b, p), fBig)) + Px(16); }
            if (!any) w += (int)Math.Ceiling(Meas(Lang.T("没有额度数据", "no quota data"), fStat)) + Px(16);
            return w - Px(4);
        }
        // text whose baseline (not its top) is at y
        void BaseText(Graphics g, string text, Font f, Brush br, float x, float baseline)
        {
            FontFamily ff = f.FontFamily; float asc = f.Size * ff.GetCellAscent(f.Style) / ff.GetEmHeight(f.Style);
            g.DrawString(text, f, br, x, baseline - asc, StringFormat.GenericTypographic);
        }
        Bitmap PaintSimple(Board b, Size full)
        {
            Theme t = Theme.Current();
            Bitmap bmp = new Bitmap(full.Width, full.Height, PixelFormat.Format32bppPArgb);
            rowRects.Clear(); chipRects.Clear(); chipKeys.Clear(); infoRect = Rectangle.Empty; pinRect = Rectangle.Empty; for (int i = 0; i < 3; i++) tabRects[i] = Rectangle.Empty;
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent); g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                CardShape(g, t, compW, compH, CardR(compH / (float)Px(1) / 2f));
                int x = Px(12), cy = compH / 2; bool any = false;
                using (SolidBrush mute = new SolidBrush(t.Ink3)) using (SolidBrush mute2 = new SolidBrush(t.Ink2))
                {
                    foreach (string p in SIMPLE)
                    {
                        if (!b.Has(p)) continue; any = true;
                        double l = b.Left(p); string name = Display.ProviderLabel(p), num = SimpleText(b, p);
                        Color nc = l < 0 ? t.Ink3 : l < 15 ? t.Red : l < 30 ? t.Amber : t.Ink;
                        using (SolidBrush pb = new SolidBrush(p == "codex" ? t.Codex : t.Claude)) g.FillEllipse(pb, x, cy - Px(3.5f), Px(7), Px(7));
                        x += Px(7) + Px(6);
                        float bl = cy + Px(4.5f);   // one baseline for the name and the number, so the letters and the digits sit on the same line
                        float nw = Meas(name, fStat); BaseText(g, name, fStat, mute2, x, bl); x += (int)Math.Ceiling(nw) + Px(6);
                        float vw = Meas(num, fBig); using (SolidBrush nb = new SolidBrush(nc)) BaseText(g, num, fBig, nb, x, bl); x += (int)Math.Ceiling(vw) + Px(16);
                    }
                    if (!any) g.DrawString(Lang.T("没有额度数据", "no quota data"), fStat, mute, new RectangleF(x, cy - Px(10), compW - x, Px(20)), sfLeft);
                }
            }
            return bmp;
        }
        Size CompactSize(Board b)
        {
            int padV = Px(8), rows = Math.Max(1, Math.Min(b.Quota.Count, 6));
            if (SimpleStrip) { compH = Px(30); compW = SimpleWidth(b); return new Size(compW + ShM, compH + ShM); }
            bool note = StaleNote(b) != "";
            compW = Px(STRIP_W); compH = padV * 2 + rows * Px(22) + (note ? Px(26) : 0);
            return new Size(compW + ShM, compH + ShM);
        }
        static string StaleNote(Board b) { foreach (QRow q in b.Quota) if (q.Muted && q.Tag != "") return q.Tag; return ""; }
        Bitmap PaintCompact(Board b, Size full)
        {
            if (SimpleStrip) return PaintSimple(b, full);
            Theme t = Theme.Current();
            Bitmap bmp = new Bitmap(full.Width, full.Height, PixelFormat.Format32bppPArgb);
            rowRects.Clear(); chipRects.Clear(); chipKeys.Clear(); infoRect = Rectangle.Empty; pinRect = Rectangle.Empty; for (int i = 0; i < 3; i++) tabRects[i] = Rectangle.Empty;
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent); g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                CardShape(g, t, compW, compH, CardR(11));
                int x0 = Px(13), x1 = compW - Px(13), y = Px(8);
                if (b.Quota.Count == 0) using (SolidBrush mu = new SolidBrush(t.Ink3)) g.DrawString(Lang.T("没有额度数据", "no quota data"), fStat, mu, new RectangleF(x0, y, x1 - x0, Px(22)), sfLeft);
                else y = QuotaRows(g, t, b, x0, x1, y, true);
                string note = StaleNote(b);
                if (note != "")
                {
                    using (Pen p = new Pen(t.Div, 1f)) g.DrawLine(p, x0, y + Px(3), x1, y + Px(3));
                    int d = Px(14); Theme.Icon(g, t, "wait", x0, y + Px(8), d, k);
                    using (SolidBrush br = new SolidBrush(t.Ink2)) g.DrawString(note, fNote, br, new RectangleF(x0 + d + Px(7), y + Px(5), x1 - x0 - d - Px(7), Px(20)), sfLeft);
                }
            }
            return bmp;
        }
        public void GlassChanged()
        {   // the window loses / gets its blur and its rounded clip; size and picture are laid out again
            glassOk = false; glassW = glassH = 0; lastSig = ""; layoutSig = "";
            if (!GlassOn) { Native.Glass(Handle, 0, 0, 0, 0, 0); glassMode = 0; }
        }
        public void SetExpanded(bool on)
        {
            if (Expanded == on) return;
            fadeStart = Env.Now() - 40; Expanded = on; hoverRow = -1; hoverTab = -1; hoverChip = -1; hoverInfo = false; hoverPin = false; infoRect = Rectangle.Empty;
            if (!on) tab = 0;
            lastSig = ""; layoutSig = "";
        }

        // ---------- the full panel ----------
        const int PANEL_W = 408, PAD_X = 14, PAD_T = 12, TABS_H = 30;
        int BodyTop() { return Px(PAD_T + TABS_H + 10); }
        public Size Measure(Board b, long now, int maxWidth)
        {
            if (!Expanded) return CompactSize(b);
            string sig = SigText(b, now) + "@" + maxWidth;
            if (sig == layoutSig) return new Size(cardW + ShM, cardH + ShM);
            layoutSig = sig; maxW = maxWidth;
            View v = b.View;
            int n = v.Items.Count, pad = Px(PAD_X), sh = ShM;
            int need = Px(PANEL_W);
            rowH = new int[n];
            for (int i = 0; i < n; i++)
            {
                Item it = v.Items[i];
                rowH[i] = it.NoWin ? Px(50) : Px(32);
                float w = Px(16) + Px(9) + Meas(BaseName(it.Name), fName) + (Dup(v.Items, it) ? Math.Min(Meas(IdPart(it.Name), fNote), Px(120)) + Px(6) : 0) + Px(14) + Meas(DetailOf(it, now), fStat) + Px(8);
                if (it.Need && !it.NoWin) w += Px(8) + BtnW();
                need = Math.Max(need, (int)Math.Ceiling(w) + 2 * pad);
            }
            cardW = Math.Min(Math.Min(need, Px(500)), Math.Max(Px(PANEL_W), maxWidth - sh));   // never wider than 500: long names and notes are cut, not the card stretched
            // the card is exactly as tall as the page it shows (a trial pass lays the page out and reports where its content ends)
            board = b; items = v.Items;
            int bottom;
            using (Bitmap tb = new Bitmap(1, 1)) using (Graphics g = Graphics.FromImage(tb))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                Theme t = Theme.Current(); int x0 = pad, x1 = cardW - pad, y0 = BodyTop();
                infoRect = Rectangle.Empty; infoText = "";
                bottom = tab == 1 ? PageContext(g, t, b, x0, x1, y0) : tab == 2 ? PageUsage(g, t, b, x0, x1, y0) : PageRemaining(g, t, b, now, x0, x1, y0);
            }
            cardH = Math.Max(bottom, NoteBottom(bottom)) + Px(14);
            return new Size(cardW + sh, cardH + sh);
        }
        float BtnW() { return Meas(Lang.T("回到窗口", "Open window"), fBtn) + Px(18); }

        // ---------- drawing ----------
        public void Render(Board b, long now)
        {
            board = b; items = b.View.Items;
            Size sz = Measure(b, now, maxW > 0 ? maxW : Px(640));
            string sig = SigText(b, now) + "#" + hoverRow + "#" + hoverTab + "#" + hoverChip + "#" + hoverPin + "#" + pet.Pinned;
            long el = Env.Now() - fadeStart; byte alpha = el >= FADE ? (byte)255 : (byte)(255 * Math.Max(0.0, (double)el / FADE));
            if (sig == lastSig && Up)
            {
                if (alpha != lastAlpha && cache != null) { lastAlpha = alpha; Native.PresentBitmap(Handle, cache, X, Y, alpha); }
                return;
            }
            lastSig = sig;
            using (Bitmap bmp = Expanded ? PaintCard(b, now, sz) : PaintCompact(b, sz))
            {
                W = bmp.Width; H = bmp.Height;
                if (cache != null) cache.Dispose();
                if (GlassOn)
                {
                    int cw = Expanded ? cardW : compW, ch = Expanded ? cardH : compH, rr = Px(8);
                    bool dk = Theme.Current().Dark;
                    if (!glassOk || glassW != cw || glassH != ch || glassDark != dk)
                    {
                        string mode = Env.Get("PET_GLASS"); int st = mode == "blur" ? 3 : 4;
                        glassMode = Native.Glass(Handle, st, dk ? 0x70201A14 : 0x70F4F8FC, cw, ch, rr); glassOk = glassMode != 0; glassW = cw; glassH = ch; glassDark = dk;
                        pet.Log("glass " + (glassOk ? "on" : "refused") + " state " + st + " " + cw + "x" + ch + " corners " + (glassMode == 2 ? "system" : "region"));
                        if (glassMode == 2) { lastSig = ""; }   // painted again with the system's corner radius
                        if (!glassOk) { GlassOn = false; lastSig = ""; layoutSig = ""; pet.Log("glass refused: solid card instead"); }
                    }
                }
                cache = (Bitmap)bmp.Clone(); lastAlpha = alpha;
                Native.PresentBitmap(Handle, bmp, X, Y, alpha);
            }
        }
        Bitmap PaintCard(Board b, long now, Size full)
        {
            Theme t = Theme.Current(); View v = b.View;
            int pad = Px(PAD_X);
            Bitmap bmp = new Bitmap(full.Width, full.Height, PixelFormat.Format32bppPArgb);
            rowRects.Clear(); chipRects.Clear(); chipKeys.Clear(); infoRect = Rectangle.Empty; pinRect = Rectangle.Empty; for (int i = 0; i < 3; i++) tabRects[i] = Rectangle.Empty;
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                CardShape(g, t, cardW, cardH, CardR(12));
                int x0 = pad, x1 = cardW - pad;
                // ---- segmented tabs + pin ----
                int ty = Px(PAD_T), th = Px(TABS_H), pinD = Px(28);
                Rectangle strip = new Rectangle(x0, ty, x1 - x0 - pinD - Px(8), th);
                using (GraphicsPath sp = Theme.Round(strip.X, strip.Y, strip.Width, strip.Height, Px(9))) using (SolidBrush br = new SolidBrush(t.Paper2)) g.FillPath(br, sp);
                string[] names = { Lang.T("剩余", "Remaining"), Lang.T("上下文", "Context"), Lang.T("用量", "Usage") };
                int ig = Px(2), tw = (strip.Width - 2 * ig - 2 * ig) / 3;
                for (int i = 0; i < 3; i++)
                {
                    Rectangle tr = new Rectangle(strip.X + ig + i * (tw + ig), strip.Y + ig, tw, th - 2 * ig); tabRects[i] = tr;
                    bool on = i == tab;
                    if (on)
                    {
                        using (GraphicsPath tp = Theme.Round(tr.X, tr.Y + Px(1), tr.Width, tr.Height, Px(7))) using (SolidBrush br = new SolidBrush(Color.FromArgb(t.Dark ? 70 : 34, 0, 0, 0))) g.FillPath(br, tp);
                        using (GraphicsPath tp = Theme.Round(tr.X, tr.Y, tr.Width, tr.Height, Px(7))) using (SolidBrush br = new SolidBrush(t.Sel)) g.FillPath(br, tp);
                    }
                    else if (i == hoverTab) using (GraphicsPath tp = Theme.Round(tr.X, tr.Y, tr.Width, tr.Height, Px(7))) using (SolidBrush br = new SolidBrush(Fade(t.Sel, 110))) g.FillPath(br, tp);
                    using (SolidBrush br = new SolidBrush(on ? t.Ink : t.Ink2)) g.DrawString(names[i], on ? fTabOn : fTab, br, tr, sfMid);
                }
                pinRect = new Rectangle(x1 - pinD, ty + (th - pinD) / 2, pinD, pinD);
                bool pinned = pet.Pinned;
                if (pinned || hoverPin) using (GraphicsPath pp = Theme.Round(pinRect.X, pinRect.Y, pinRect.Width, pinRect.Height, Px(7))) using (SolidBrush br = new SolidBrush(t.Paper2)) g.FillPath(br, pp);
                PinGlyph(g, t, pinRect, pinned);
                int y = BodyTop();
                if (tab == 1) PageContext(g, t, b, x0, x1, y);
                else if (tab == 2) PageUsage(g, t, b, x0, x1, y);
                else PageRemaining(g, t, b, now, x0, x1, y);
                if (hoverInfo && infoText != "" && infoRect != Rectangle.Empty) InfoOverlay(g, t, x0, x1);
            }
            return bmp;
        }
        void PinGlyph(Graphics g, Theme t, Rectangle r, bool on)
        {
            Color c = on ? t.Ink : t.Ink3; float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, u = r.Width / 28f;
            using (Pen p = new Pen(c, Math.Max(1.4f, 1.5f * u)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.TranslateTransform(cx, cy); g.RotateTransform(35f);
                if (on) using (SolidBrush br = new SolidBrush(c)) g.FillEllipse(br, -3.4f * u, -8f * u, 6.8f * u, 8.4f * u); else g.DrawEllipse(p, -3.4f * u, -8f * u, 6.8f * u, 8.4f * u);
                g.DrawLine(p, -5f * u, 0.6f * u, 5f * u, 0.6f * u); g.DrawLine(p, 0, 0.6f * u, 0, 8f * u);
                g.ResetTransform();
            }
        }

        // the quota windows: provider (named once per group) | window | bar | what is left | reset (panel only)
        int QuotaRows(Graphics g, Theme t, Board b, int x0, int x1, int y, bool compact)
        {
            using (SolidBrush ink = new SolidBrush(t.Ink)) using (SolidBrush mute = new SolidBrush(t.Ink3)) using (SolidBrush mute2 = new SolidBrush(t.Ink2))
            {
                int whoW = Px(78), winW = Px(40), pctW = Px(46), gap = Px(8), resetW = compact ? 0 : Px(96), rowH = compact ? Px(22) : Px(26), bh = Px(5);
                string prevProv = null;
                foreach (QRow q in b.Quota)
                {
                    int cy = y + rowH / 2;
                    Color pc = q.Provider == "codex" ? t.Codex : q.Provider == "claude" ? t.Claude : q.Provider == "deepseek" ? t.Blue : t.Ink2;
                    string provider = q.Provider == "" ? "" : Display.ProviderLabel(q.Provider);
                    if (provider != "" && provider != prevProv)
                    {
                        using (SolidBrush pb = new SolidBrush(pc)) g.FillEllipse(pb, x0, cy - Px(3.5f), Px(7), Px(7));
                        g.DrawString(provider, fPill, ink, new RectangleF(x0 + Px(13), cy - Px(10), whoW - Px(13) + Px(10), Px(20)), sfLeft);
                    }
                    prevProv = provider;
                    int wx = x0 + whoW + gap;
                    g.DrawString(q.Label, fStat, mute2, new RectangleF(wx, cy - Px(10), winW + Px(10), Px(20)), sfLeft);
                    int bx = wx + winW + gap;
                    if (q.Pct < 0)
                    {   // no number: say why, do not draw an empty bar
                        g.DrawString(q.Tag != "" ? q.Tag : Lang.T("没有数据", "no data"), fNote, mute, new RectangleF(bx, cy - Px(10), x1 - bx, Px(20)), sfLeft);
                    }
                    else
                    {   // the bar and the number are what is LEFT of the window (the tab is called "remaining"); green while plenty, amber getting low, red nearly out; grey when it is not fresh
                        double left = Math.Min(100, Math.Max(0, 100 - q.Pct));
                        int bwid = compact ? Px(86) : x1 - bx - pctW - resetW - 2 * gap;
                        Rectangle track = new Rectangle(bx, cy - bh / 2, Math.Max(20, bwid), bh);
                        using (GraphicsPath tp = Theme.Round(track.X, track.Y, track.Width, track.Height, bh / 2f)) using (SolidBrush br = new SolidBrush(t.Track)) g.FillPath(br, tp);
                        int fw = (int)Math.Round(track.Width * left / 100.0);
                        Color fc = q.Muted ? Fade(t.Ink3, 150) : left < 15 ? t.Red : left < 30 ? t.Amber : t.Aqua;
                        if (fw > 1) using (GraphicsPath fp = Theme.Round(track.X, track.Y, Math.Max(bh, fw), track.Height, bh / 2f)) using (SolidBrush br = new SolidBrush(fc)) g.FillPath(br, fp);
                        RectangleF pr = new RectangleF(track.Right + gap, cy - Px(10), pctW, Px(20));
                        string num = Math.Round(left) + "%";
                        g.DrawString(num, fBig, q.Muted ? mute : ink, pr, sfRight);
                        float nw = Meas(num, fBig) - Px(3);
                        g.DrawString(Lang.T("剩", "") , fNote, mute2, new RectangleF(pr.Right - nw - Px(18), pr.Y, Px(18), pr.Height), sfRight);
                        if (!compact)
                        {
                            string rt = q.Tag != "" ? q.Tag : q.Reset != "" ? Lang.T(q.Reset + "后", "in " + q.Reset) : "";
                            using (SolidBrush rb = new SolidBrush(q.Tag != "" ? t.Warn : t.Ink3)) g.DrawString(rt, fNote, rb, new RectangleF(pr.Right + gap, cy - Px(10), x1 - pr.Right - gap, Px(20)), sfRight);
                        }
                    }
                    y += rowH;
                }
                return y;
            }
        }

        // ---- page 1: quota windows above, the session list below ----
        int PageRemaining(Graphics g, Theme t, Board b, long now, int x0, int x1, int y)
        {
            View v = b.View; int n = v.Items.Count;
            using (SolidBrush ink = new SolidBrush(t.Ink)) using (SolidBrush mute = new SolidBrush(t.Ink3)) using (SolidBrush mute2 = new SolidBrush(t.Ink2))
            {
                if (b.Quota.Count > 0) y = QuotaRows(g, t, b, x0, x1, y, false) + Px(4);
                using (Pen p = new Pen(t.Div, 1f)) g.DrawLine(p, x0, y + Px(4), x1, y + Px(4));
                y += Px(10);
                g.DrawString(Lang.T("会话 · " + v.LiveCount, "Sessions · " + v.LiveCount), fNote, mute, new RectangleF(x0, y, 200 * k, Px(18)), sfLeft);
                if (v.NeedCount > 0) g.DrawString(Lang.T("需要你的排在前面", "needs-you first"), fNote, mute, new RectangleF(x1 - 200 * k, y, 200 * k, Px(18)), sfRight);
                y += Px(22);
                if (n == 0)
                {
                    g.DrawString(Lang.T("没有在运行的会话", "No session running"), fName, ink, new RectangleF(x0, y, x1 - x0, Px(24)), sfLeft);
                    g.DrawString(Lang.T("开个会话，我就动起来", "Start one and I will move"), fStat, mute2, new RectangleF(x0, y + Px(24), x1 - x0, Px(22)), sfLeft);
                    y += Px(50);
                }
                for (int i = 0; i < n; i++)
                {
                    Item it = v.Items[i]; int rh = rowH[i];
                    int bleed = Px(8);
                    Rectangle row = new Rectangle(x0 - bleed, y, x1 - x0 + 2 * bleed, rh); rowRects.Add(row);
                    bool link = !it.NoWin, hot = i == hoverRow && link;
                    if (it.Need)
                        using (GraphicsPath rp = Theme.Round(row.X, row.Y, row.Width, row.Height, Px(8))) using (SolidBrush br = new SolidBrush(it.State == "error" ? Mix2(t.Paper, t.Red, hot ? .20 : .14) : (hot ? Mix2(t.Paper, t.Amber, .22) : t.AmBg))) g.FillPath(br, rp);
                    else if (hot) using (GraphicsPath rp = Theme.Round(row.X, row.Y, row.Width, row.Height, Px(8))) using (SolidBrush br = new SolidBrush(t.Paper2)) g.FillPath(br, rp);
                    int d = Px(16); int ix = row.X + bleed, cy = it.NoWin ? row.Y + Px(16) : row.Y + row.Height / 2;
                    Theme.Icon(g, t, Theme.KindOf(it), ix, cy - d / 2, d, k * 0.72f);
                    int rx = row.Right - bleed;
                    if (it.Need && link)
                    {   // the solid "back to window" button
                        int bwid = (int)Math.Ceiling(BtnW()), bhh = Px(24);
                        Rectangle btn = new Rectangle(rx - bwid, cy - bhh / 2, bwid, bhh);
                        using (GraphicsPath bp = Theme.Round(btn.X, btn.Y, btn.Width, btn.Height, Px(6))) using (SolidBrush br = new SolidBrush(hot ? Mix2(t.Teal, t.Dark ? Color.White : Color.Black, .10) : t.Teal)) g.FillPath(br, bp);
                        using (SolidBrush br = new SolidBrush(t.AccText)) g.DrawString(Lang.T("回到窗口", "Open window"), fBtn, br, btn, sfMid);
                        rx = btn.X - Px(8);
                    }
                    string det = DetailOf(it, now);
                    float statW = Meas(det, fStat) + Px(4);
                    int nx = ix + d + Px(9);
                    string nm = BaseName(it.Name), idp = Dup(v.Items, it) ? IdPart(it.Name) : "";
                    float idW = idp == "" ? 0 : Math.Min(Meas(idp, fNote), Px(120)) + Px(6);
                    float nameW = Math.Min(Meas(nm, fName) + Px(2), Math.Max(Px(40), rx - nx - Px(6) - Px(14) - statW - idW));
                    Rectangle nameR = new Rectangle(nx, cy - Px(12), (int)Math.Ceiling(nameW), Px(24));
                    using (SolidBrush br = new SolidBrush(t.Ink)) g.DrawString(nm, fName, br, nameR, sfLeft);
                    if (idp != "") { using (SolidBrush br = new SolidBrush(t.Ink3)) g.DrawString(idp, fNote, br, new RectangleF(nameR.Right + Px(3), cy - Px(12), idW, Px(24)), sfLeft); nameR = new Rectangle(nameR.X, nameR.Y, nameR.Width + (int)idW, nameR.Height); }
                    // which assistant: a small dot in its colour after the name
                    using (SolidBrush br = new SolidBrush(it.Provider == "deepseek" ? t.Blue : it.Agent == "Codex" ? t.Codex : t.Claude)) g.FillEllipse(br, nameR.Right + Px(6), cy - Px(3), Px(6), Px(6));
                    float sx = nameR.Right + Px(18);
                    using (SolidBrush br = new SolidBrush(it.Ctx >= Ctx.TIRED_AT ? t.Warn : t.Ink2))
                        g.DrawString(det, fStat, br, new RectangleF(sx, cy - Px(12), Math.Max(10, rx - sx), Px(24)), sfRight);
                    if (it.NoWin)
                        using (SolidBrush br = new SolidBrush(t.Ink3)) g.DrawString(NoWindowNote(it), fNote, br, new RectangleF(nx, row.Y + Px(29), row.Right - nx - bleed, Px(18)), sfLeft);
                    y += rh + Px(2);
                }
                return y - (n > 0 ? Px(2) : 0);
            }
        }
        static Color Mix2(Color a, Color b, double f) { return Color.FromArgb(255, (int)Math.Round(a.R + (b.R - a.R) * f), (int)Math.Round(a.G + (b.G - a.G) * f), (int)Math.Round(a.B + (b.B - a.B) * f)); }

        // ---- chips (session picker / period picker): pills that sit on a soft fill; the chosen one is raised ----
        int Chip(Graphics g, Theme t, string text, string key, bool on, int x, int y, int maxRight)
        {
            int w = (int)Math.Ceiling(Meas(text, fSmall)) + Px(20), h = Px(24);
            bool first = chipRects.Count == 0;
            if (x + w > maxRight) { if (!first || maxRight - x < Px(50)) return -1; w = maxRight - x; }
            Rectangle r = new Rectangle(x, y, w, h); chipRects.Add(r); chipKeys.Add(key);
            bool hot = chipRects.Count - 1 == hoverChip;
            using (GraphicsPath p = Theme.Round(r.X, r.Y, r.Width, r.Height, r.Height / 2f))
            {
                if (on) { using (GraphicsPath p2 = Theme.Round(r.X, r.Y + Px(1), r.Width, r.Height, r.Height / 2f)) using (SolidBrush sb = new SolidBrush(Color.FromArgb(t.Dark ? 70 : 30, 0, 0, 0))) g.FillPath(sb, p2); }
                using (SolidBrush br = new SolidBrush(on ? t.Sel : (hot ? Mix2(t.Paper2, t.Ink, .06) : t.Paper2))) g.FillPath(br, p);
            }
            using (StringFormat sf = new StringFormat(StringFormatFlags.NoWrap) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter })
            using (SolidBrush br = new SolidBrush(on ? t.Ink : t.Ink2)) g.DrawString(text, on ? fSmallB : fSmall, br, new RectangleF(r.X + Px(6), r.Y, r.Width - Px(12), r.Height), sf);
            return r.Right + Px(6);
        }

        // the small (i): its note shows while the pointer rests on it
        int InfoMark(Graphics g, Theme t, int x, int cy)
        {
            int d = Px(13); Rectangle r = new Rectangle(x, cy - d / 2, d, d); infoRect = Rectangle.Inflate(r, Px(5), Px(5));
            bool on = hoverInfo;
            if (on) using (SolidBrush br = new SolidBrush(t.Paper2)) g.FillEllipse(br, r);
            using (Pen p = new Pen(on ? t.Ink2 : t.Ink3, Math.Max(1f, Px(1f)))) g.DrawEllipse(p, r);
            using (SolidBrush br = new SolidBrush(on ? t.Ink : t.Ink2)) g.DrawString("i", fInfo, br, new RectangleF(r.X - 2, r.Y - 1, r.Width + 4, r.Height + 2), sfMid);
            return r.Right + Px(4);
        }
        // the note of the (i), drawn as a small card over the content just below the mark (the card is tall enough to hold it, so showing it never resizes anything)
        int NoteHeight(Graphics g, int w)
        {
            using (StringFormat sf = new StringFormat { Trimming = StringTrimming.None }) return (int)Math.Ceiling(g.MeasureString(infoText, fNote, w, sf).Height) + Px(16);
        }
        int NoteBottom(int bottom)
        {
            if (infoText == "" || infoRect == Rectangle.Empty) return bottom;
            using (Bitmap tb = new Bitmap(1, 1)) using (Graphics g = Graphics.FromImage(tb))
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                int w = cardW - 2 * Px(PAD_X);
                return infoRect.Bottom + Px(2) + NoteHeight(g, w - Px(22)) + Px(8);
            }
        }
        void InfoOverlay(Graphics g, Theme t, int x0, int x1)
        {
            int w = Math.Min(x1 - x0, Px(262)), h = NoteHeight(g, w - Px(22)), y = infoRect.Bottom + Px(2);
            int x = Math.Max(x0, Math.Min(infoRect.X - Px(10), x1 - w));
            Theme.SoftShadow(g, t, x, y, w, h, Px(8), Px(7), 0, Px(4), 6);
            using (GraphicsPath cp = Theme.Round(x, y, w, h, Px(8)))
            {
                using (SolidBrush br = new SolidBrush(t.Dark ? Color.FromArgb(52, 57, 68) : Color.FromArgb(252, 252, 253))) g.FillPath(br, cp);
                using (Pen p = new Pen(t.Dark ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(34, 0, 0, 0), 1f)) g.DrawPath(p, cp);
            }
            using (StringFormat sf = new StringFormat { Trimming = StringTrimming.None }) using (SolidBrush br = new SolidBrush(t.Dark ? Color.FromArgb(238, 240, 243) : Color.FromArgb(27, 29, 33)))
                g.DrawString(infoText, fNote, br, new RectangleF(x + Px(11), y + Px(8), w - Px(22), h - Px(10)), sf);
        }

        // ---- page 2: context of one session ----
        int PageContext(Graphics g, Theme t, Board b, int x0, int x1, int y)
        {
            List<Item> its = b.View.Items;
            using (SolidBrush ink = new SolidBrush(t.Ink)) using (SolidBrush mute = new SolidBrush(t.Ink3)) using (SolidBrush mute2 = new SolidBrush(t.Ink2))
            {
                if (its.Count == 0) { g.DrawString(Lang.T("没有在运行的会话", "No session running"), fName, ink, new RectangleF(x0, y, x1 - x0, Px(24)), sfLeft); g.DrawString(Lang.T("开个会话后这里显示它的上下文占用", "Start a session and its context use shows here"), fStat, mute2, new RectangleF(x0, y + Px(24), x1 - x0, Px(22)), sfLeft); return y + Px(50); }
                Item sel = null; foreach (Item it in its) if (Key(it) == ctxSel) sel = it;
                if (sel == null) { sel = its[0]; ctxSel = Key(sel); }
                int selIdx = its.IndexOf(sel);
                if (its.Count > 1)
                {   // several sessions: a short list, one row each (state dot, project and what was last said in it, how full its context is); click one to look at it
                    int rh = Px(27), shown = Math.Min(its.Count, 6), s0 = selIdx >= shown ? selIdx - shown + 1 : 0;
                    for (int r = 0; r < shown; r++)
                    {
                        Item it = its[s0 + r]; bool on = it == sel;
                        Rectangle rc = new Rectangle(x0 - Px(6), y + r * rh, x1 - x0 + Px(12), rh - Px(3));
                        chipRects.Add(rc); chipKeys.Add(Key(it)); bool hot = chipRects.Count - 1 == hoverChip;
                        using (GraphicsPath rp = Theme.Round(rc.X, rc.Y, rc.Width, rc.Height, Px(8)))
                        {
                            if (on) { using (GraphicsPath rp2 = Theme.Round(rc.X, rc.Y + Px(1), rc.Width, rc.Height, Px(8))) using (SolidBrush sb = new SolidBrush(Color.FromArgb(t.Dark ? 70 : 24, 0, 0, 0))) g.FillPath(sb, rp2); }
                            if (on || hot) using (SolidBrush br = new SolidBrush(on ? t.Sel : Mix2(t.Paper2, t.Ink, .04))) g.FillPath(br, rp);
                        }
                        Color dc = it.Need ? t.Red : it.State == "busy" ? t.Amber : it.State == "done" ? t.Aqua : t.Ink3;
                        using (SolidBrush db = new SolidBrush(dc)) g.FillEllipse(db, rc.X + Px(9), rc.Y + (rc.Height - Px(8)) / 2, Px(8), Px(8));
                        string pct = it.Ctx >= 0 ? Math.Round(it.Ctx) + "%" : "\u2014"; float pw = Meas(pct, fBig);
                        Color pc = it.Ctx >= 90 ? t.Red : it.Ctx >= 75 ? t.Amber : on ? t.Ink : t.Ink2;
                        using (SolidBrush pb = new SolidBrush(pc)) using (StringFormat sfm = new StringFormat(StringFormatFlags.NoWrap) { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center }) g.DrawString(pct, fBig, pb, new RectangleF(rc.Right - Px(10) - pw - Px(8), rc.Y, pw + Px(8), rc.Height), sfm);
                        float tx = rc.X + Px(24), tw = rc.Right - Px(14) - pw - tx;
                        string nm = BaseName(it.Name), id = IdPart(it.Name);
                        using (StringFormat sf = new StringFormat(StringFormatFlags.NoWrap) { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter })
                        {
                            float nw = Math.Min(Meas(nm, on ? fSmallB : fSmall), tw * 0.5f);
                            using (SolidBrush nb = new SolidBrush(on ? t.Ink : t.Ink2)) g.DrawString(nm, on ? fSmallB : fSmall, nb, new RectangleF(tx, rc.Y, nw + Px(2), rc.Height), sf);
                            if (id != "") using (SolidBrush ib = new SolidBrush(on ? t.Ink2 : t.Ink3)) g.DrawString(id, fSmall, ib, new RectangleF(tx + nw + Px(8), rc.Y, Math.Max(10, tw - nw - Px(8)), rc.Height), sf);
                        }
                    }
                    y += shown * rh;
                    if (its.Count > shown) { g.DrawString("+" + (its.Count - shown), fSmall, mute, new RectangleF(x0, y - Px(2), Px(60), Px(18)), sfLeft); y += Px(16); }
                    y += Px(6);
                }
                else
                {
                int limit = x1 - Px(30), avail = limit - x0, start = 0;
                    {   // the row begins at the first chip that still lets the chosen session fit (a very long name is cut instead of disappearing)
                        for (; start < selIdx; start++)
                        {
                            int sum = 0; for (int q = start; q <= selIdx; q++) sum += Math.Min((int)Math.Ceiling(Meas(BaseName(its[q].Name) + (Dup(its, its[q]) ? " " + IdPart(its[q].Name) : ""), fSmall)) + Px(20), avail) + Px(6);
                            if (sum - Px(6) <= avail) break;
                        }
                    }
                    int cx = x0, chipsDrawn = 0;
                    if (start > 0) { g.DrawString("‹" + start, fSmall, mute, new RectangleF(cx, y, Px(30), Px(24)), sfLeft); cx += Px(30); }
                    for (int ii = start; ii < its.Count; ii++)
                    {
                        Item it = its[ii];
                        string nm = BaseName(it.Name) + (Dup(its, it) ? " " + IdPart(it.Name) : "");
                        int nx = Chip(g, t, nm, Key(it), it == sel, cx, y, limit);
                        if (nx < 0) break; cx = nx; chipsDrawn++;
                    }
                    if (start + chipsDrawn < its.Count) g.DrawString("+" + (its.Count - start - chipsDrawn), fSmall, mute, new RectangleF(cx, y, Px(30), Px(24)), sfLeft);
                    y += Px(34);
                }
                CtxView c = Display.Context(b, sel);
                float hw = Meas(c.Headline, fHuge);
                g.DrawString(c.Headline, fHuge, ink, new RectangleF(x0, y, hw + Px(4), Px(40)), sfLeft);
                float hx = x0 + hw + Px(8);
                string cap = c.HasUsed ? Lang.T("已用 token", "tokens used") : "";
                g.DrawString(cap, fStat, mute2, new RectangleF(hx, y + Px(10), Math.Max(10, x1 - hx), Px(24)), sfLeft);
                InfoMark(g, t, (int)(hx + Meas(cap, fStat)) + Px(2), y + Px(22));
                y += Px(44);
                string noBar = c.HasUsed && !c.HasWindow ? Lang.T("不知道窗口多大，所以不画占用比例", "window size unknown, so no fill bar") : "";
                if (c.HasUsed && c.HasWindow)
                {   // how much of the window: a thin bar, the share on the left, the window and what is left on the right
                    int bh = Px(5); Rectangle track = new Rectangle(x0, y, x1 - x0, bh);
                    using (GraphicsPath tp = Theme.Round(track.X, track.Y, track.Width, track.Height, bh / 2f)) using (SolidBrush br = new SolidBrush(t.Track)) g.FillPath(br, tp);
                    double share = Math.Min(100, Math.Max(0, c.Pct >= 0 ? c.Pct : 100.0 * c.Used / Math.Max(1, c.Window)));
                    int fw = (int)Math.Round(track.Width * share / 100.0);
                    Color fc = share >= 90 ? t.Red : share >= 75 ? t.Amber : t.Aqua;
                    if (fw > 1) using (GraphicsPath fp = Theme.Round(track.X, track.Y, Math.Max(bh, fw), bh, bh / 2f)) using (SolidBrush br = new SolidBrush(fc)) g.FillPath(br, fp);
                    g.DrawString(Lang.T("占窗口的 ", "of window ") + Math.Round(share) + "%", fNote, mute2, new RectangleF(x0, y + Px(8), (x1 - x0) / 2, Px(18)), sfLeft);
                    g.DrawString(Lang.T("窗口 ", "window ") + Display.Tok(c.Window) + (c.Remain != "" ? " · " + c.Remain : ""), fNote, mute2, new RectangleF(x0 + (x1 - x0) / 2, y + Px(8), (x1 - x0) / 2, Px(18)), sfRight);
                    y += Px(30);
                }
                else y += Px(2);
                if (c.HasUsed && c.Split != null && c.Split.Count > 0)
                {   // what the used part is made of, (the parts marked as estimates are scaled so that they add up)
                    double tot = 0; foreach (SplitRow sr in c.Split) tot += Math.Max(0, sr.Tokens);
                    int sx = x0, segH = Px(6), gapS = Px(2), cnt = c.Split.Count;
                    for (int i = 0; i < cnt; i++)
                    {
                        double wpx = tot > 0 ? (x1 - x0 - gapS * (cnt - 1)) * Math.Max(0, c.Split[i].Tokens) / tot : 0;
                        int w = (int)Math.Max(Px(3), Math.Round(wpx));
                        using (GraphicsPath sp = Theme.Round(sx, y, w, segH, segH / 2f)) using (SolidBrush br = new SolidBrush(SplitColor(t, c.Split[i].Key))) g.FillPath(br, sp);
                        sx += w + gapS;
                    }
                    y += segH + Px(10);
                    foreach (SplitRow sr in c.Split)
                    {
                        using (SolidBrush br = new SolidBrush(SplitColor(t, sr.Key))) g.FillEllipse(br, x0, y + Px(7), Px(8), Px(8));
                        string lab = sr.Label; float lw = Meas(lab, fStat);
                        g.DrawString(lab, fStat, ink, new RectangleF(x0 + Px(15), y, (x1 - x0) * 0.66f, Px(22)), sfLeft);
                        if (sr.Estimated) using (SolidBrush br = new SolidBrush(t.Ink3)) g.DrawString(Lang.T("估算", "est."), fNote, br, new RectangleF(x0 + Px(15) + lw + Px(5), y + Px(1), Px(40), Px(22)), sfLeft);
                        g.DrawString(Display.Tok(sr.Tokens), fBig, ink, new RectangleF(x1 - Px(70), y, Px(70), Px(22)), sfRight);
                        g.DrawString(tot > 0 ? Math.Round(100.0 * sr.Tokens / tot) + "%" : "", fNote, mute, new RectangleF(x1 - Px(116), y, Px(42), Px(22)), sfRight);
                        y += Px(24);
                    }
                }
                else if (c.HasUsed)
                {   // no breakdown for this session (not a Claude transcript): the same list, from what the provider reported for the latest request
                    string[] lab; string[] val; string[] keys;
                    if (c.Codex)
                    {
                        long fresh = c.Input >= 0 && c.CacheRead >= 0 ? Math.Max(0, c.Input - c.CacheRead) : c.Input;
                        lab = new string[] { Lang.T("新输入", "New input"), Lang.T("缓存读取", "Cache read"), Lang.T("输出（已含在内）", "Output (included)") };
                        val = new string[] { fresh >= 0 ? Display.Tok(fresh) : "—", c.CacheRead >= 0 ? Display.Tok(c.CacheRead) : "—", c.Output >= 0 ? Display.Tok(c.Output) : "—" };
                        keys = new string[] { "user", "assistant", "call" };
                    }
                    else
                    {
                        lab = new string[] { Lang.T("新输入", "New input"), Lang.T("缓存读取", "Cache read"), Lang.T("缓存写入", "Cache write"), Lang.T("输出（另计）", "Output (extra)") };
                        val = new string[] { c.Input >= 0 ? Display.Tok(c.Input) : "—", c.CacheRead >= 0 ? Display.Tok(c.CacheRead) : "—", c.CacheWrite >= 0 ? Display.Tok(c.CacheWrite) : "—", c.Output >= 0 ? Display.Tok(c.Output) : "—" };
                        keys = new string[] { "user", "assistant", "call", "cmd" };
                    }
                    for (int i = 0; i < lab.Length; i++)
                    {
                        using (SolidBrush br = new SolidBrush(SplitColor(t, keys[i]))) g.FillEllipse(br, x0, y + Px(7), Px(8), Px(8));
                        g.DrawString(lab[i], fStat, ink, new RectangleF(x0 + Px(15), y, (x1 - x0) * 0.66f, Px(22)), sfLeft);
                        g.DrawString(val[i], fBig, ink, new RectangleF(x1 - Px(70), y, Px(70), Px(22)), sfRight);
                        y += Px(24);
                    }
                    g.DrawString(Lang.T("这个会话没有可读的对话记录，只能显示最近一次请求的 token 构成。", "No readable transcript for this session: only the token kinds of the latest request are shown."), fNote, mute, new RectangleF(x0, y + Px(2), x1 - x0, Px(18)), sfLeft);
                    y += Px(22);
                }
                {   // the note of the (i): how the number was obtained, why there is no bar, where it came from
                    string nt = c.Quality; if (noBar != "") nt += " · " + noBar; if (c.Source != "") nt += "\n" + c.Source;
                    if (c.WindowSource == "model_table") nt += "\n" + Lang.T("窗口大小是按模型推定的（状态栏没有报告）。", "The window size is assumed from the model (the status line did not report it).");
                    if (c.Split != null && c.Split.Count > 0) nt += "\n" + Lang.T("“估算”的几项按对话原文的字数折算，合计对齐已用 token；固定开销由第一次和最近一次请求推出。", "Parts marked est. are scaled from the character counts of the transcript so that they add up to the used tokens; the fixed overhead is worked out from the first and the latest request.");
                    infoText = nt;
                }
                return y;
            }
        }

        // ---- page 3: usage ----
        int PageUsage(Graphics g, Theme t, Board b, int x0, int x1, int y)
        {
            using (SolidBrush ink = new SolidBrush(t.Ink)) using (SolidBrush mute = new SolidBrush(t.Ink3)) using (SolidBrush mute2 = new SolidBrush(t.Ink2))
            {
                string[] pn = { Lang.T("今天", "Today"), Lang.T("7 天", "7 days"), Lang.T("30 天", "30 days") };
                // the three periods share one soft pill
                float pw = 0; for (int i = 0; i < 3; i++) pw += (int)Math.Ceiling(Meas(pn[i], fSmall)) + Px(20) + Px(6);
                using (GraphicsPath cp = Theme.Round(x0, y - Px(2), pw + Px(2), Px(28), Px(10))) using (SolidBrush br = new SolidBrush(t.Paper2)) g.FillPath(br, cp);
                int cx = x0 + Px(2);
                for (int i = 0; i < 3; i++) { int nx = Chip(g, t, pn[i], PERIODS[i], PERIODS[i] == period, cx, y, x1); if (nx > 0) cx = nx - Px(4); }
                UsageView u = Display.Usage(b, period);
                cx = x0 + (int)pw + Px(8);
                using (SolidBrush ab = new SolidBrush(u.Busy ? t.Warn : t.Ink3))
                    g.DrawString(u.State, fNote, ab, new RectangleF(cx, y, Math.Max(10, x1 - cx), Px(24)), sfRight);
                y += Px(32);
                {   // what the figures are split by
                    string[] mn = { Lang.T("按助手", "By assistant"), Lang.T("按 token 类型", "By token kind") }; string[] mk = { "who", "kind" }; int mx0 = x0;
                    for (int i = 0; i < 2; i++) { int nx = Chip(g, t, mn[i], "mode:" + mk[i], usageBy == mk[i], mx0, y - Px(2), x1); if (nx > 0) mx0 = nx; }
                    y += Px(32);
                }
                bool kindMode = usageBy == "kind";
                string[] kn = { Lang.T("输入", "Input"), Lang.T("缓存读取", "Cache read"), Lang.T("缓存写入", "Cache write"), Lang.T("输出", "Output") };
                Color[] kc = { t.C1, t.C2, t.C3, t.C4 };
                if (!u.HasNumbers) { g.DrawString(Lang.T("还没有可显示的数字", "No figures to show yet"), fName, ink, new RectangleF(x0, y, x1 - x0, Px(24)), sfLeft); g.DrawString(Lang.T("没有数字不等于零用量", "no figures is not zero use"), fStat, mute2, new RectangleF(x0, y + Px(24), x1 - x0, Px(22)), sfLeft); return y + Px(50); }
                // the figure, and on the right what it is: API-equivalent price (i), tokens
                string big = u.Cost; float bwid = Meas(big, fHuge) + Px(4);
                g.DrawString(big, fHuge, ink, new RectangleF(x0, y, bwid, Px(42)), sfLeft);
                {
                    string lbl = Lang.T("等值 API 价", "API-equivalent price"); float lw = Meas(lbl, fNote);
                    int markX = x1 - Px(13);
                    g.DrawString(lbl, fNote, mute2, new RectangleF(markX - Px(6) - lw, y + Px(6), lw, Px(18)), sfLeft);
                    InfoMark(g, t, markX, y + Px(15));
                    g.DrawString(Display.Tok(u.Total) + " tokens", fBig, ink, new RectangleF(x0 + bwid, y + Px(20), x1 - x0 - bwid, Px(22)), sfRight);
                }
                y += Px(48);
                // how it splits between the two assistants
                double sc = 0, sx2 = 0, so = 0; foreach (Bar br in u.Bars) { sc += br.Claude; sx2 += br.Codex; so += br.Other; }
                double[] parts = kindMode ? new double[] { u.Input, u.CacheRead, u.CacheWrite, u.Output } : new double[] { sc, sx2, so };
                Color[] pcs = kindMode ? kc : new Color[] { t.Claude, t.Codex, t.Ink3 };
                double st = 0; foreach (double d0 in parts) st += Math.Max(0, d0);
                if (st > 0)
                {
                    int sh = Px(6), gp = Px(2), sxx = x0; int cnt = 0; foreach (double d0 in parts) if (d0 > 0) cnt++;
                    for (int i = 0; i < parts.Length; i++)
                    {
                        if (parts[i] <= 0) continue;
                        int w = (int)Math.Max(Px(4), Math.Round((x1 - x0 - gp * (cnt - 1)) * parts[i] / st));
                        using (GraphicsPath sp = Theme.Round(sxx, y, w, sh, sh / 2f)) using (SolidBrush br = new SolidBrush(pcs[i])) g.FillPath(br, sp);
                        sxx += w + gp;
                    }
                    y += sh + Px(8);
                }
                if (kindMode)
                {   // legend: each kind with its tokens
                    int lx = x0, ly = y;
                    for (int i = 0; i < 4; i++)
                    {
                        string part = kn[i] + " " + Display.Tok((long)parts[i]); float w = Meas(part, fNote);
                        if (lx + Px(12) + w > x1) { lx = x0; ly += Px(18); }
                        using (SolidBrush br = new SolidBrush(kc[i])) g.FillEllipse(br, lx, ly + Px(6), Px(7), Px(7));
                        g.DrawString(part, fNote, ink, new RectangleF(lx + Px(11), ly, w + Px(6), Px(18)), sfLeft);
                        lx += (int)w + Px(24);
                    }
                    y = ly + Px(24);
                }
                if (!kindMode && u.ByProvider != "")
                {
                    string[] pp = u.ByProvider.Split(new string[] { " · " }, StringSplitOptions.None); int lx = x0;
                    foreach (string part in pp)
                    {
                        Color dc = part.StartsWith("Codex") ? t.Codex : t.Claude;
                        using (SolidBrush br = new SolidBrush(dc)) g.FillEllipse(br, lx, y + Px(7), Px(7), Px(7));
                        float w = Meas(part, fStat);
                        g.DrawString(part, fStat, ink, new RectangleF(lx + Px(12), y, w + Px(6), Px(20)), sfLeft);
                        lx += (int)w + Px(26);
                    }
                    y += Px(26);
                }
                // bars: one per day (or hour), Claude under Codex
                int ch = Px(76), n = u.Bars.Count; y += Px(2);
                if (n > 0)
                {
                    double mx = 1; foreach (Bar br in u.Bars) mx = Math.Max(mx, kindMode ? br.In + br.CacheRead + br.CacheWrite + br.Out : br.Claude + br.Codex + br.Other);
                    int gap = n > 14 ? 2 : Px(9), bwd = Math.Max(3, ((x1 - x0) - gap * (n - 1)) / n);
                    for (int i = 0; i < n; i++)
                    {
                        Bar br = u.Bars[i]; int bx = x0 + i * (bwd + gap); int baseY = y + ch;
                        double[] sg = kindMode ? new double[] { br.In, br.CacheRead, br.CacheWrite, br.Out } : new double[] { br.Claude, br.Codex, br.Other };
                        Color[] sgc = kindMode ? kc : new Color[] { t.Claude, t.Codex, t.Ink3 };
                        int top = baseY, anyH = 0;
                        for (int q = 0; q < sg.Length; q++)
                        {
                            int hq = (int)Math.Round(ch * sg[q] / mx); if (hq <= 0) continue;
                            using (SolidBrush bq = new SolidBrush(sgc[q])) g.FillRectangle(bq, bx, top - hq, bwd, hq);
                            top -= hq + 1; anyH += hq;
                        }
                        if (anyH == 0) using (SolidBrush b4 = new SolidBrush(t.Track)) g.FillRectangle(b4, bx, baseY - Px(2), bwd, Px(2));
                    }
                    using (Pen p = new Pen(t.Div, 1f)) g.DrawLine(p, x0, y + ch + 1, x1, y + ch + 1);
                }
                y += ch + Px(5);
                g.DrawString(u.BarFrom, fNote, mute, new RectangleF(x0, y, Px(80), Px(16)), sfLeft); g.DrawString(u.BarTo, fNote, mute, new RectangleF(x1 - Px(80), y, Px(80), Px(16)), sfRight);
                y += Px(24);
                using (Pen p = new Pen(t.Div, 1f)) g.DrawLine(p, x0, y - Px(4), x1, y - Px(4));
                if (kindMode)
                {
                    double ktot = 0; double[] kv = { u.Input, u.CacheRead, u.CacheWrite, u.Output }; foreach (double d0 in kv) ktot += d0;
                    string[] kd = { Lang.T("没命中缓存的输入", "uncached input"), Lang.T("命中缓存的输入", "cached input"), Lang.T("写入缓存", "written to cache"), Lang.T("模型输出", "model output") };
                    for (int i = 0; i < 4; i++)
                    {
                        using (SolidBrush dot = new SolidBrush(kc[i])) g.FillEllipse(dot, x0, y + Px(8), Px(7), Px(7));
                        g.DrawString(kn[i], fStat, ink, new RectangleF(x0 + Px(14), y, Px(80), Px(23)), sfLeft);
                        g.DrawString(kd[i], fNote, mute, new RectangleF(x0 + Px(94), y + Px(1), (x1 - x0) - Px(94) - Px(130), Px(23)), sfLeft);
                        g.DrawString(Display.Tok((long)kv[i]), fStat, mute2, new RectangleF(x1 - Px(130), y, Px(70), Px(23)), sfRight);
                        g.DrawString(ktot > 0 ? (100.0 * kv[i] / ktot < 1 ? (100.0 * kv[i] / ktot).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : Math.Round(100.0 * kv[i] / ktot).ToString()) + "%" : "", fBig, ink, new RectangleF(x1 - Px(58), y, Px(58), Px(23)), sfRight);
                        y += Px(25);
                    }
                }
                else
                {
                foreach (ModelRow m in u.Models)
                    {
                        using (SolidBrush dot = new SolidBrush(m.Provider == "codex" ? t.Codex : m.Provider == "deepseek" ? t.Blue : t.Claude)) g.FillEllipse(dot, x0, y + Px(8), Px(7), Px(7));
                        g.DrawString(m.Label, fStat, ink, new RectangleF(x0 + Px(14), y, (x1 - x0) / 2, Px(23)), sfLeft);
                        g.DrawString(Display.Tok(m.Tokens), fStat, mute2, new RectangleF(x0 + (x1 - x0) / 2, y, (x1 - x0) / 2 - Px(62), Px(23)), sfRight);
                        g.DrawString(m.Cost, fBig, m.Priced ? ink : mute2, new RectangleF(x1 - Px(62), y, Px(62), Px(23)), sfRight);
                        y += Px(25);
                    }
                    if (u.MoreModels > 0)
                    {
                        using (SolidBrush dot = new SolidBrush(t.Ink3)) g.FillEllipse(dot, x0, y + Px(8), Px(7), Px(7));
                        g.DrawString(Lang.T("其他 " + u.MoreModels + " 个模型", u.MoreModels + " other models"), fStat, mute2, new RectangleF(x0 + Px(14), y, (x1 - x0) / 2, Px(23)), sfLeft);
                        g.DrawString(Display.Tok(u.MoreTokens), fStat, mute2, new RectangleF(x0 + (x1 - x0) / 2, y, (x1 - x0) / 2, Px(23)), sfRight);
                        y += Px(25);
                    }
                }
                // a caution (part of the tokens has no price, the count is still running ...) sits in a soft amber box
                string caution = u.Footer != "" ? u.Footer : u.CostNote != "" ? u.CostNote : "";
                if (caution != "")
                {
                    y += Px(6);
                    using (StringFormat sf = new StringFormat { Trimming = StringTrimming.None })
                    {
                        float th = g.MeasureString(caution, fNote, x1 - x0 - Px(40), sf).Height;
                        int bh2 = (int)Math.Ceiling(th) + Px(14);
                        using (GraphicsPath bp = Theme.Round(x0, y, x1 - x0, bh2, Px(7))) using (SolidBrush br = new SolidBrush(t.AmBg)) g.FillPath(br, bp);
                        Theme.Icon(g, t, "wait", x0 + Px(9), y + (bh2 - Px(14)) / 2, Px(14), k * 0.6f);
                        using (SolidBrush br = new SolidBrush(t.Ink)) g.DrawString(caution, fNote, br, new RectangleF(x0 + Px(30), y + Px(6), x1 - x0 - Px(40), th + Px(2)), sf);
                        y += bh2;
                    }
                }
                infoText = Lang.T("等值 API 价：把已用 token 按公开 API 单价折算的参考值，不是账单；订阅内的用量不会额外收费。只含这台电脑上看得到的用量 · " + u.Requests + " 次请求。缓存读 " + Display.Tok(u.CacheRead) + " · 缓存写 " + Display.Tok(u.CacheWrite) + " · 新输入 " + Display.Tok(u.Input) + " · 输出 " + Display.Tok(u.Output),
                    "API-equivalent price: the used tokens at public API list prices, a reference and not a bill; use inside a subscription costs nothing extra. Only usage visible on this PC · " + u.Requests + " requests. Cache read " + Display.Tok(u.CacheRead) + " · cache write " + Display.Tok(u.CacheWrite) + " · new input " + Display.Tok(u.Input) + " · output " + Display.Tok(u.Output));
                return y;
            }
        }

        static Color SplitColor(Theme t, string key)
        {
            switch (key)
            {
                case "fixed": return t.Ink3; case "summary": return Col("#9b8ccf"); case "user": return t.C1; case "assistant": return t.C2; case "call": return t.C4;
                case "read": return Col("#3aa5c6"); case "cmd": return t.C3; case "search": return Col("#d46f9a"); case "web": return Col("#7aa04a");
            }
            return Col("#a3ad5c");
        }
        static Color Col(string h) { return ColorTranslator.FromHtml(h); }
        // small diagonal "go there" arrow centred at (cx,cy)
        static void Arrow(Graphics g, int cx, int cy, float kk, Color color)
        {
            float s = 3.6f * kk;
            using (Pen p = new Pen(color, Math.Max(1.6f, 1.6f * kk)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(p, cx - s, cy + s, cx + s, cy - s);
                g.DrawLine(p, cx - s * 0.2f, cy - s, cx + s, cy - s); g.DrawLine(p, cx + s, cy - s, cx + s, cy + s * 0.2f);
            }
        }

        // ---------- placement ----------
        public void MoveTo(int x, int y) { X = x; Y = y; if (Up) Native.SetWindowPos(Handle, IntPtr.Zero, X, Y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | 4); }
        public void ShowIt() { if (Up) return; Up = true; Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE); Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE); }
        public void HideIt() { if (!Up) return; fadeStart = Env.Now(); Up = false; Hover = false; hoverRow = -1; hoverTab = -1; hoverChip = -1; hoverInfo = false; hoverPin = false; tab = 0; Expanded = false; lastSig = ""; layoutSig = ""; Native.ShowWindow(Handle, Native.SW_HIDE); }
        public void Invalidate_() { lastSig = ""; }
        // for screenshots and tests: show another page / pick the session or period (same as clicking)
        public void Select(int page, string key) { tab = page; if (page == 1 && key != "") ctxSel = key; if (page == 2 && key != "") period = key; lastSig = ""; layoutSig = ""; }

        // ---------- mouse ----------
        int RowAt(int x, int y) { for (int i = 0; i < rowRects.Count; i++) if (rowRects[i].Contains(x, y)) return i; return -1; }
        int TabAt(int x, int y) { for (int i = 0; i < 3; i++) if (tabRects[i].Contains(x, y)) return i; return -1; }
        int ChipAt(int x, int y) { for (int i = 0; i < chipRects.Count; i++) if (chipRects[i].Contains(x, y)) return i; return -1; }
        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case WM_MOUSEACTIVATE: m.Result = (IntPtr)3; return;
                case WM_MOUSEMOVE:
                    {
                        if (md)
                        {   // a press on the card that moves a few pixels drags the whole floating window
                            Point c = Cursor.Position; int dx = c.X - mdScr.X, dy = c.Y - mdScr.Y;
                            if (!dragged && (Math.Abs(dx) > 4 || Math.Abs(dy) > 4)) dragged = true;
                            if (dragged) pet.PanelDrag(dx, dy);
                            return;
                        }
                        int x = (short)(m.LParam.ToInt64() & 0xFFFF), y = (short)((m.LParam.ToInt64() >> 16) & 0xFFFF);
                        if (!Hover)
                        {
                            Hover = true;
                            Native.TRACKMOUSEEVENT t = new Native.TRACKMOUSEEVENT(); t.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.TRACKMOUSEEVENT)); t.dwFlags = 2; t.hwndTrack = Handle; Native.TrackMouseEvent(ref t);
                        }
                        int row = RowAt(x, y), tb = TabAt(x, y), ch = ChipAt(x, y); bool inf = infoRect.Contains(x, y), pnh = pinRect.Contains(x, y);
                        if (pnh != hoverPin) { hoverPin = pnh; lastSig = ""; Cursor = pnh ? Cursors.Hand : Cursors.Default; }
                        if (inf != hoverInfo) { hoverInfo = inf; lastSig = ""; Cursor = inf ? Cursors.Hand : Cursors.Default; }
                        bool link = row >= 0 && row < items.Count && !items[row].NoWin;
                        if (row != hoverRow || tb != hoverTab || ch != hoverChip) { hoverRow = row; hoverTab = tb; hoverChip = ch; lastSig = ""; Cursor = link || tb >= 0 || ch >= 0 || inf ? Cursors.Hand : Cursors.Default; }
                        return;
                    }
                case WM_MOUSELEAVE: Hover = false; hoverInfo = false; hoverPin = false; if (hoverRow != -1 || hoverTab != -1 || hoverChip != -1) { hoverRow = -1; hoverTab = -1; hoverChip = -1; lastSig = ""; } pet.PanelLeft(); return;
                case WM_LBUTTONDOWN: md = true; dragged = false; mdScr = Cursor.Position; pet.PanelDragStart(); Capture = true; return;
                case WM_LBUTTONUP:
                    {
                        if (md) { md = false; Capture = false; if (dragged) { dragged = false; pet.PanelDragEnd(); return; } }
                        int x = (short)(m.LParam.ToInt64() & 0xFFFF), y = (short)((m.LParam.ToInt64() >> 16) & 0xFFFF);
                        if (pinRect.Contains(x, y)) { pet.PanelClick(); return; }
                        int tb = TabAt(x, y);
                        if (tb >= 0) { if (tb != tab) { tab = tb; lastSig = ""; layoutSig = ""; hoverChip = -1; pet.Log("panel tab " + tb); } return; }
                        int chip = ChipAt(x, y);
                        if (chip >= 0) { if (tab == 1) ctxSel = chipKeys[chip]; else if (tab == 2) { if (chipKeys[chip].StartsWith("mode:")) usageBy = chipKeys[chip].Substring(5); else period = chipKeys[chip]; } lastSig = ""; pet.Log("panel chip " + chipKeys[chip]); return; }
                        int row = RowAt(x, y);
                        if (Expanded && tab == 0 && row >= 0 && row < items.Count) pet.RowClick(items[row], "panel row " + row);
                        else pet.PanelClick();
                        return;
                    }
            }
            base.WndProc(ref m);
        }
    }
}
