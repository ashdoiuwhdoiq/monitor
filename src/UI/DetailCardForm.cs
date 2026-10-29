using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using LiteMonitor.src.Core;

namespace LiteMonitor.src.UI
{
    /// <summary>
    /// 主面板单击某一行后，从面板侧边滑出的详情卡：大号数值、状态、近 40 次采样走势（含阈值线）、最低 / 平均 / 最高。
    /// 不抢焦点；滑入滑出与上下跟随都带缓动。配色取自当前面板主题。
    /// </summary>
    public class DetailCardForm : Form
    {
        private const int SlideOffset = 28;

        private readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer { Interval = 15 };

        private MetricItem? _item;
        private string _group = "";
        private Settings _cfg;
        // 设计稿动效：位移与缩放 .55s 轻弹簧（.34,1.35,.64,1），透明度 .28s，上下跟随所点的行 .55s 缓出
        private readonly Tween _slide = new Tween(0f);
        private readonly Tween _fade = new Tween(0f);
        private readonly Tween _y = new Tween(0f);
        private bool _wantShow;
        private Point _anchor;         // 目标左上角（屏幕坐标）
        private bool _fromRight;       // 卡片在面板右侧时从右往左滑
        private Rectangle _closeRect;
        private bool _closeHover;

        public MetricItem? CurrentItem => _wantShow ? _item : null;

        public DetailCardForm(Settings cfg)
        {
            _cfg = cfg;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            Opacity = 0;
            Size = new Size(UIUtils.S(340), UIUtils.S(318));
            _timer.Tick += (_, __) => Frame();
            MouseMove += (_, e) => { bool h = _closeRect.Contains(e.Location); if (h != _closeHover) { _closeHover = h; Invalidate(); } Cursor = h ? Cursors.Hand : Cursors.Default; };
            MouseLeave += (_, __) => { if (_closeHover) { _closeHover = false; Invalidate(); } };
            MouseUp += (_, e) => { if (e.Button == MouseButtons.Left && _closeRect.Contains(e.Location)) HideCard(); };
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80;        // WS_EX_TOOLWINDOW
                cp.ExStyle |= 0x08000000;  // WS_EX_NOACTIVATE
                return cp;
            }
        }

        /// <summary>为某一行显示（或切换到）详情卡。panel / row 均为屏幕坐标。</summary>
        public void ShowFor(MetricItem item, string group, Rectangle panel, Rectangle row, bool topMost)
        {
            _item = item;
            _group = group;
            TopMost = topMost;

            var area = Screen.FromRectangle(panel).WorkingArea;
            int gap = UIUtils.S(12);
            _fromRight = panel.Left - Width - gap < area.Left;
            int x = _fromRight ? panel.Right + gap : panel.Left - Width - gap;
            int y = Math.Clamp(row.Top - UIUtils.S(40), area.Top + gap, area.Bottom - Height - gap);
            _anchor = new Point(x, y);

            bool appearing = !_wantShow || !Visible;
            if (appearing) _y.Snap(y);
            else _y.Set(y, 550, Motion.Out);           // 已打开时切换到另一行：卡片上下滑到新位置
            _slide.Set(1f, 550, Motion.SpringSoft);
            _fade.Set(1f, 280, Motion.Out);
            _wantShow = true;
            if (!Visible) { ApplyPosition(); Show(); ApplyCorners(); }
            Start();
        }

        public void HideCard()
        {
            if (!_wantShow) return;
            _wantShow = false;
            _slide.Set(0f, 300, Motion.Out);
            _fade.Set(0f, 220, Motion.Out);
            Start();
        }

        private void Start()
        {
            _timer.Interval = 15;
            if (!_timer.Enabled) _timer.Start();
        }

        private void Frame()
        {
            long now = Motion.Now;
            ApplyPosition();
            Invalidate(); // 卡片可见期间持续刷新，数值与走势随面板一同变化

            if (!_wantShow && !_fade.IsRunning(now) && !_slide.IsRunning(now))
            {
                _timer.Stop();
                Hide();
                return;
            }

            // 动效结束后降到每秒 4 次刷新即可跟上数据变化
            bool settled = !_slide.IsRunning(now) && !_fade.IsRunning(now) && !_y.IsRunning(now);
            _timer.Interval = settled ? 250 : 15;
        }

        private void ApplyPosition()
        {
            int offset = (int)Math.Round((1f - _slide.Value()) * UIUtils.S(SlideOffset)) * (_fromRight ? -1 : 1);
            Location = new Point(_anchor.X + offset, (int)Math.Round(_y.Value()));
            Opacity = Math.Clamp(_fade.Value(), 0f, 1f) * Math.Clamp(_cfg.Opacity + 0.1, 0.5, 1.0);
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
                using var path = UIUtils.RoundRect(new Rectangle(0, 0, Width, Height), UIUtils.S(14));
                Region = new Region(path);
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            // 详情卡是附加功能：绘制出错只收起卡片，绝不让整个程序弹出崩溃
            try { PaintCard(e); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DetailCardForm] 绘制失败: {ex.Message}");
                BeginInvoke(new Action(HideCard));
            }
        }

        private void PaintCard(PaintEventArgs e)
        {
            var t = ThemeManager.Current;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            Color bg = ThemeManager.ParseColor(t.Color.Background);
            Color text = ThemeManager.ParseColor(t.Color.TextTitle);
            Color sub = ThemeManager.ParseColor(t.Color.TextGroup);
            Color track = ThemeManager.ParseColor(t.Color.BarBackground);
            Color panelBg = ThemeManager.ParseColor(t.Color.GroupBackground);
            g.Clear(bg);
            using (var border = new Pen(Blend(bg, text, 0.12f))) g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

            // 弹出时内容从 .96 放大到 1（以靠近面板的一侧为原点，带轻微过冲）
            float sc = 0.96f + 0.04f * _slide.Value();
            float ox = _fromRight ? 0 : Width, oy = Height / 2f;
            g.TranslateTransform(ox, oy); g.ScaleTransform(sc, sc); g.TranslateTransform(-ox, -oy);

            var it = _item;
            if (it == null) return;

            int pad = UIUtils.S(18);
            int w = Width - pad * 2;
            using var fSmall = new Font(t.FontItem.FontFamily, 9f);
            using var fLabel = new Font(t.FontItem.FontFamily, 10f);
            using var fBig = new Font(t.FontValue.FontFamily, 28f);
            using var fUnit = new Font(t.FontItem.FontFamily, 10.5f);
            using var fStat = new Font(t.FontValue.FontFamily, 11f);

            // 标题行
            int y = pad - UIUtils.S(2);
            string title = string.IsNullOrEmpty(_group) ? it.Label : $"{_group}  /  {it.Label}";
            GraphitePanel.DrawText(g, title, fLabel, new Rectangle(pad, y, w - UIUtils.S(30), UIUtils.S(24)), text, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            _closeRect = new Rectangle(Width - pad - UIUtils.S(24), y, UIUtils.S(24), UIUtils.S(24));
            if (_closeHover) UIUtils.FillRoundRect(g, _closeRect, UIUtils.S(6), Blend(bg, text, 0.10f));
            using (var xp = new Pen(_closeHover ? text : sub, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                var cr =Rectangle.Inflate(_closeRect, -UIUtils.S(7), -UIUtils.S(7));
                g.DrawLine(xp, cr.Left, cr.Top, cr.Right, cr.Bottom);
                g.DrawLine(xp, cr.Right, cr.Top, cr.Left, cr.Bottom);
            }

            // 大号数值 + 状态
            y += UIUtils.S(34);
            it.GetFormattedText(false);
            string valText = it.CachedValueText, unitText = it.CachedUnitText;
            Color valColor = it.GetTextColor(t);
            var bigSize = new Size(GraphitePanel.MeasureW(g, valText, fBig), (int)Math.Ceiling(fBig.GetHeight(g)));
            GraphitePanel.DrawText(g, valText, fBig, new Rectangle(pad, y, w, bigSize.Height), valColor, TextFormatFlags.Left);
            int unitH = (int)Math.Ceiling(fUnit.GetHeight(g));
            GraphitePanel.DrawText(g, unitText, fUnit, new Rectangle(pad + bigSize.Width + UIUtils.S(4), y + bigSize.Height - unitH - UIUtils.S(5), UIUtils.S(80), unitH), sub, TextFormatFlags.Left);

            int state = it.CachedColorState;
            string stateText = LanguageManager.T(state == 2 ? "Menu.DetailCritical" : state == 1 ? "Menu.DetailHigh" : "Menu.DetailNormal");
            Color stateColor = it.GetBarColor(t);
            var chipSize = new Size(GraphitePanel.MeasureW(g, stateText, fSmall), 0);
            var chip = new Rectangle(Width - pad - chipSize.Width - UIUtils.S(16), y + bigSize.Height - UIUtils.S(28), chipSize.Width + UIUtils.S(16), UIUtils.S(22));
            UIUtils.FillRoundRect(g, chip, chip.Height / 2, Color.FromArgb(40, stateColor));
            GraphitePanel.DrawText(g, stateText, fSmall, chip, Blend(stateColor, text, 0.15f), TextFormatFlags.HorizontalCenter);

            // 走势图
            y += bigSize.Height + UIUtils.S(14);
            var chart = new Rectangle(pad, y, w, UIUtils.S(110));
            int n = it.HistoryCount;
            float lo = 0, hi = 1;
            if (n > 0)
            {
                lo = float.MaxValue; hi = float.MinValue;
                for (int i = 0; i < n; i++) { float v = it.HistoryAt(i); lo = Math.Min(lo, v); hi = Math.Max(hi, v); }
            }

            var type = MetricUtils.GetType(it.Key);
            bool fixedScale = type is MetricType.Percent or MetricType.Temperature;
            (double warn, double crit) th = fixedScale ? MetricUtils.GetThresholds(it.Key) : (0, 0);
            if (fixedScale) { lo = 0; hi = 100; }
            else
            {
                float pad2 = Math.Max((hi - lo) * 0.25f, Math.Max(Math.Abs(hi) * 0.05f, 0.5f));
                lo = Math.Max(0, lo - pad2); hi += pad2;
            }
            float Y(double v) => chart.Bottom - (float)((v - lo) / Math.Max(1e-6, hi - lo)) * chart.Height;

            using (var gridPen = new Pen(Blend(bg, text, 0.07f))) { g.DrawLine(gridPen, chart.Left, chart.Bottom, chart.Right, chart.Bottom); }
            if (fixedScale)
            {
                using var warnPen = new Pen(Color.FromArgb(170, ThemeManager.ParseColor(t.Color.BarMid)), 1f) { DashStyle = DashStyle.Dash };
                using var critPen = new Pen(Color.FromArgb(170, ThemeManager.ParseColor(t.Color.BarHigh)), 1f) { DashStyle = DashStyle.Dash };
                if (th.warn > lo && th.warn < hi) g.DrawLine(warnPen, chart.Left, Y(th.warn), chart.Right, Y(th.warn));
                if (th.crit > lo && th.crit < hi) g.DrawLine(critPen, chart.Left, Y(th.crit), chart.Right, Y(th.crit));
            }

            if (n >= 2)
            {
                var pts = new PointF[n];
                for (int i = 0; i < n; i++) pts[i] = new PointF(chart.Left + chart.Width * i / (float)(n - 1), Y(it.HistoryAt(i)));
                using (var path = new GraphicsPath())
                {
                    path.AddLines(pts);
                    path.AddLine(pts[n - 1].X, chart.Bottom, pts[0].X, chart.Bottom);
                    path.CloseFigure();
                    using var fill = new SolidBrush(Color.FromArgb(34, stateColor));
                    g.FillPath(fill, path);
                }
                using var line = new Pen(stateColor, UIUtils.S(1.8f)) { LineJoin = LineJoin.Round };
                g.DrawLines(line, pts);
                var end = pts[n - 1];
                using var dot = new SolidBrush(stateColor);
                float r = UIUtils.S(3.5f);
                g.FillEllipse(dot, end.X - r, end.Y - r, r * 2, r * 2);
            }

            int seconds = Math.Max(1, (int)Math.Round(40 * Math.Max(80, _cfg.RefreshMs) / 1000.0));
            y = chart.Bottom + UIUtils.S(4);
            GraphitePanel.DrawText(g, string.Format(LanguageManager.T("Menu.DetailAgo"), seconds), fSmall, new Rectangle(pad, y, w / 2, UIUtils.S(18)), sub, TextFormatFlags.Left);
            GraphitePanel.DrawText(g, LanguageManager.T("Menu.DetailNow"), fSmall, new Rectangle(pad + w / 2, y, w / 2, UIUtils.S(18)), sub, TextFormatFlags.Right);

            // 最低 / 平均 / 最高
            y += UIUtils.S(24);
            float sum = 0, mn = float.MaxValue, mx = float.MinValue;
            for (int i = 0; i < n; i++) { float v = it.HistoryAt(i); sum += v; mn = Math.Min(mn, v); mx = Math.Max(mx, v); }
            string[] labels = { LanguageManager.T("Menu.DetailMin"), LanguageManager.T("Menu.DetailAvg"), LanguageManager.T("Menu.DetailMax") };
            float[] vals = { mn, n > 0 ? sum / n : 0, mx };
            int gapX = UIUtils.S(8);
            int cw = (w - gapX * 2) / 3;
            for (int i = 0; i < 3; i++)
            {
                var box = new Rectangle(pad + i * (cw + gapX), y, cw, UIUtils.S(52));
                UIUtils.FillRoundRect(g, box, UIUtils.S(10), panelBg == bg ? Blend(bg, text, 0.05f) : panelBg);
                GraphitePanel.DrawText(g, labels[i], fSmall, new Rectangle(box.X + UIUtils.S(10), box.Y + UIUtils.S(6), box.Width - UIUtils.S(12), UIUtils.S(18)), sub, TextFormatFlags.Left);
                string sv = n > 0 ? FormatValue(it, vals[i]) : "—";
                GraphitePanel.DrawText(g, sv, fStat, new Rectangle(box.X + UIUtils.S(10), box.Y + UIUtils.S(24), box.Width - UIUtils.S(12), UIUtils.S(22)), text, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            }
        }

        private static string FormatValue(MetricItem it, float v)
        {
            string? fmt = it.BoundConfig?.UnitPanel;
            string val = MetricUtils.GetValueStr(it.Key, v, false, fmt);
            string unit = MetricUtils.GetDisplayUnit(it.Key, MetricUtils.GetUnitStr(it.Key, v, MetricUtils.UnitContext.Panel), fmt);
            return val + unit;
        }

        private static Color Blend(Color a, Color b, float k) =>
            Color.FromArgb((int)(a.R + (b.R - a.R) * k), (int)(a.G + (b.G - a.G) * k), (int)(a.B + (b.B - a.B) * k));

        protected override void Dispose(bool disposing)
        {
            if (disposing) _timer.Dispose();
            base.Dispose(disposing);
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    }
}
