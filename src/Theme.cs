using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace HotUploadPreview
{
    static class Theme
    {
        public static readonly Color Back = Color.FromArgb(12, 17, 22);
        public static readonly Color Side = Color.FromArgb(15, 22, 28);
        public static readonly Color Surface = Color.FromArgb(21, 30, 37);
        public static readonly Color Input = Color.FromArgb(14, 23, 29);
        public static readonly Color Line = Color.FromArgb(47, 65, 73);
        public static readonly Color Ink = Color.FromArgb(236, 244, 245);
        public static readonly Color Muted = Color.FromArgb(161, 181, 189);
        public static readonly Color Mint = Color.FromArgb(108, 235, 177);
        public static readonly Color Cyan = Color.FromArgb(122, 213, 231);
        public static readonly Color Amber = Color.FromArgb(244, 200, 121);
        public static readonly Color Red = Color.FromArgb(255, 153, 155);
        public static float Scale = 1;
        public static int S(float x) { return (int)Math.Round(x * Scale); }
        public static Font Font(float size, bool bold = false, bool latin = false)
        {
            return new Font(latin ? "Bahnschrift" : "Microsoft YaHei UI", size * Scale,
                bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
        }
        public static GraphicsPath Round(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
        public static void Bounds(Control c, int x, int y, int w, int h)
        {
            c.SetBounds(S(x), S(y), S(w), S(h));
        }
    }

    class Surface : Panel
    {
        public Color Fill = Theme.Surface;
        public Color Border = Theme.Line;
        public bool Dashed = false;
        public Surface()
        {
            DoubleBuffered = true;
            BackColor = Theme.Back;
            Padding = new Padding(Theme.S(1));
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            if (r.Width < 2 || r.Height < 2) return;
            using (var path = Theme.Round(r, Theme.S(12)))
            using (var b = new SolidBrush(Fill))
            using (var p = new Pen(Border))
            {
                if (Dashed) p.DashPattern = new float[] { 5, 5 };
                e.Graphics.FillPath(b, path);
                e.Graphics.DrawPath(p, path);
            }
            base.OnPaint(e);
        }
    }

    class ActionButton : Button
    {
        public bool Primary;
        public bool Selected;
        public bool Quiet;
        bool hover;
        public ActionButton(string text, bool primary)
        {
            Text = text;
            Primary = primary;
            Font = Theme.Font(15, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            BackColor = Theme.Surface;
            ForeColor = Theme.Ink;
            Cursor = Cursors.Hand;
            AccessibleName = text;
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent is Surface ? ((Surface)Parent).Fill : Parent == null ? Theme.Back : Parent.BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill = Primary ? Theme.Mint : Selected ? Color.FromArgb(29, 57, 47) : hover ? Color.FromArgb(41, 56, 63) : BackColor;
            if (!Enabled) fill = Theme.Line;
            var rect = new Rectangle(1, 1, Width - 3, Height - 3);
            using (var path = Theme.Round(rect, Theme.S(8)))
            using (var b = new SolidBrush(fill))
            using (var p = new Pen(Selected ? Theme.Mint : Theme.Line))
            {
                g.FillPath(b, path);
                if (!Quiet || Selected) g.DrawPath(p, path);
            }
            var ink = Primary ? Theme.Back : Selected ? Theme.Mint : Enabled ? ForeColor : Theme.Muted;
            TextRenderer.DrawText(g, Text, Font, rect, ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            if (Focused && ShowFocusCues)
            {
                using (var p = new Pen(Theme.Cyan, 2))
                using (var path = Theme.Round(new Rectangle(4, 4, Width - 9, Height - 9), Theme.S(6))) g.DrawPath(p, path);
            }
        }
    }

    sealed class ScrollViewport : Panel
    {
        public readonly Control Content;
        readonly HashSet<Control> hooked = new HashSet<Control>();
        int offset, maximum, dragStart, dragOffset;
        bool layingOut, dragging;
        public ScrollViewport(Control content)
        {
            DoubleBuffered = true;
            BackColor = Theme.Back;
            Content = content;
            Controls.Add(content);
            content.SizeChanged += delegate { RefreshViewport(); };
            content.ControlAdded += delegate { RefreshViewport(); };
            content.ControlRemoved += delegate { RefreshViewport(); };
            Resize += delegate { RefreshViewport(); };
            TabStop = true;
            AccessibleName = "可滚动内容区域";
            Hook(content);
        }
        void Hook(Control c)
        {
            if (!hooked.Add(c)) return;
            c.MouseWheel += delegate(object s, MouseEventArgs e) { ScrollBy(-e.Delta * Theme.S(54) / 120); };
            c.ControlAdded += delegate(object s, ControlEventArgs e) { Hook(e.Control); };
            c.Enter += delegate {
                if (layingOut || !c.Visible || c == Content) return;
                var point = PointToClient(c.PointToScreen(Point.Empty));
                if (point.Y < 0) ScrollBy(point.Y - Theme.S(12));
                else if (point.Y + c.Height > Height) ScrollBy(point.Y + c.Height - Height + Theme.S(12));
            };
            foreach (Control child in c.Controls) Hook(child);
        }
        public void RefreshViewport()
        {
            if (layingOut || IsDisposed) return;
            layingOut = true;
            try
            {
                int width = Math.Max(1, ClientSize.Width - Theme.S(14));
                Content.Width = width;
                var list = Content as FlowLayoutPanel;
                if (list != null)
                {
                    int height = 0;
                    foreach (Control row in list.Controls)
                    {
                        row.Width = width;
                        height += row.Height + row.Margin.Top + row.Margin.Bottom;
                        Hook(row);
                    }
                    Content.Height = Math.Max(1, height + Theme.S(2));
                }
                maximum = Math.Max(0, Content.Height - ClientSize.Height);
                offset = Math.Max(0, Math.Min(offset, maximum));
                Content.Location = new Point(0, -offset);
                Invalidate();
            }
            finally { layingOut = false; }
        }
        public void ScrollBy(int pixels)
        {
            offset = Math.Max(0, Math.Min(maximum, offset + pixels));
            Content.Top = -offset; Invalidate();
        }
        public int Offset { get { return offset; } }
        protected override void OnMouseWheel(MouseEventArgs e) { ScrollBy(-e.Delta * Theme.S(54) / 120); }
        protected override bool IsInputKey(Keys keyData) { return keyData == Keys.PageDown || keyData == Keys.PageUp || keyData == Keys.Home || keyData == Keys.End || base.IsInputKey(keyData); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.PageDown) ScrollBy(Height - Theme.S(32));
            else if (e.KeyCode == Keys.PageUp) ScrollBy(-Height + Theme.S(32));
            else if (e.KeyCode == Keys.Home) ScrollBy(-maximum);
            else if (e.KeyCode == Keys.End) ScrollBy(maximum);
            base.OnKeyDown(e);
        }
        int ThumbHeight { get { return Math.Max(Theme.S(30), Height * Height / Math.Max(1, Content.Height)); } }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (maximum <= 0) return;
            int h = Math.Min(Height, ThumbHeight), y = maximum == 0 ? 0 : offset * (Height - h) / maximum;
            using (var b = new SolidBrush(Color.FromArgb(66, 89, 97)))
                e.Graphics.FillRectangle(b, Width - Theme.S(6), y, Theme.S(4), h);
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.X >= Width - Theme.S(14) && maximum > 0)
            {
                Focus(); dragging = true; Capture = true; dragStart = e.Y; dragOffset = offset;
            }
            base.OnMouseDown(e);
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragging) { int target = dragOffset + (e.Y - dragStart) * maximum / Math.Max(1, Height - ThumbHeight); ScrollBy(target - offset); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseUp(MouseEventArgs e) { dragging = false; Capture = false; base.OnMouseUp(e); }
    }


}
