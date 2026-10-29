using LiteMonitor.src.Core;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LiteMonitor
{
    /// <summary>
    /// 操作反馈提示条（设计稿的 toast）：在面板下方居中，弹簧上浮 20px 并淡入（.5s / .25s），
    /// 停留 1.7 秒后淡出。颜色与面板主题相反（深色主题用浅底深字）。不抢焦点、鼠标可穿透。
    /// </summary>
    public sealed class ToastForm : Form
    {
        private const float HoldMs = 1700, InMs = 500, FadeInMs = 250, OutMs = 220, RisePx = 20;

        private readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer { Interval = 15 };
        private string _text = "";
        private long _shownAt;
        private Point _anchor;

        public ToastForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            Opacity = 0;
            _timer.Tick += (_, __) => Frame();
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80 | 0x08000000 | 0x20; // TOOLWINDOW | NOACTIVATE | TRANSPARENT（鼠标穿透）
                return cp;
            }
        }

        public void ShowNear(Rectangle panel, string text, bool topMost)
        {
            _text = text;
            TopMost = topMost;
            using (var g = CreateGraphics())
            using (var f = MakeFont())
            {
                var sz = g.MeasureString(text, f);
                Size = new Size((int)Math.Ceiling(sz.Width) + UIUtils.S(32), UIUtils.S(38));
            }

            var area = Screen.FromRectangle(panel).WorkingArea;
            int x = Math.Clamp(panel.Left + (panel.Width - Width) / 2, area.Left + 8, area.Right - Width - 8);
            int gap = UIUtils.S(12);
            int y = panel.Bottom + gap + Height <= area.Bottom ? panel.Bottom + gap : panel.Top + UIUtils.S(40);
            _anchor = new Point(x, y);

            _shownAt = Motion.Now;
            if (!Visible) Show();
            ApplyCorners();
            _timer.Start();
            Frame();
            Invalidate();
        }

        private void Frame()
        {
            long t = Motion.Now - _shownAt;
            float rise = Motion.Spring.Eval(Math.Clamp(t / InMs, 0f, 1f));
            float alpha = Math.Clamp(t / FadeInMs, 0f, 1f);
            float dy = (1 - rise) * UIUtils.S((int)RisePx);

            if (t > InMs + HoldMs)
            {
                float o = Math.Clamp((t - InMs - HoldMs) / OutMs, 0f, 1f);
                alpha = 1 - o;
                dy = o * UIUtils.S(8);
                if (o >= 1f)
                {
                    _timer.Stop();
                    Hide();
                    return;
                }
            }

            Location = new Point(_anchor.X, _anchor.Y + (int)Math.Round(dy));
            Opacity = alpha;
        }

        private Font MakeFont() => new Font(ThemeManager.Current.Font.Family, 9.75f, FontStyle.Bold);

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            var t = ThemeManager.Current;
            Color panelBg = ThemeManager.ParseColor(t.Color.Background);
            Color panelText = ThemeManager.ParseColor(t.Color.TextTitle);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(panelText);
            using var f = MakeFont();
            GraphitePanel.DrawText(g, _text, f, ClientRectangle, panelBg, TextFormatFlags.HorizontalCenter);
        }

        private void ApplyCorners()
        {
            try
            {
                int pref = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(Handle, 33, ref pref, sizeof(int));
            }
            catch { }
            if (Environment.OSVersion.Version.Build < 22000)
            {
                using var path = UIUtils.RoundRect(new Rectangle(0, 0, Width, Height), UIUtils.S(12));
                Region = new Region(path);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _timer.Dispose();
            base.Dispose(disposing);
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    }
}
