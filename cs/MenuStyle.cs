// The right-click menu in the card's own look: paper colour, soft grey text in a light weight, a hairline border, a rounded highlight, a thin check mark.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ClaudeCompanionPet
{
    class MenuStyle : ToolStripProfessionalRenderer
    {
        readonly Theme t; readonly Color paper, text, soft, hair, hover;
        static Color Mix(Color a, Color b, double f) { return Color.FromArgb(255, (int)Math.Round(a.R + (b.R - a.R) * f), (int)Math.Round(a.G + (b.G - a.G) * f), (int)Math.Round(a.B + (b.B - a.B) * f)); }
        public MenuStyle(Theme th) : base(new ProfessionalColorTable())
        {
            t = th; RoundedEdges = false;
            paper = t.Paper; text = t.Ink2; soft = t.Ink3; hair = Mix(t.Paper, t.Ink, .20); hover = t.Dark ? Mix(t.Paper, Color.White, .10) : Mix(t.Paper, t.Ink, .07);
        }
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) { using (SolidBrush b = new SolidBrush(paper)) e.Graphics.FillRectangle(b, e.AffectedBounds); }
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { using (Pen p = new Pen(hair)) e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1); }
        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath gp = Theme.Round(3, 1, e.Item.Width - 7, e.Item.Height - 3, 5)) using (SolidBrush b = new SolidBrush(hover)) e.Graphics.FillPath(b, gp);
        }
        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2; using (Pen p = new Pen(Mix(t.Paper, t.Ink, .10))) e.Graphics.DrawLine(p, 10, y, e.Item.Width - 10, y);
        }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) { e.TextColor = e.Item.Enabled ? (e.Item.Selected ? t.Ink : text) : soft; base.OnRenderItemText(e); }
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) { e.ArrowColor = soft; base.OnRenderArrow(e); }
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            Rectangle r = e.ImageRectangle; e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            using (Pen p = new Pen(t.Teal, 1.7f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                e.Graphics.DrawLines(p, new PointF[] { new PointF(cx - 4, cy), new PointF(cx - 1.2f, cy + 3), new PointF(cx + 4, cy - 3.5f) });
        }
    }
}
