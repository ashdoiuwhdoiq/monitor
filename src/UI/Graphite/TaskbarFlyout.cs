using LiteMonitor.src.Core;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace LiteMonitor.src.UI.Graphite
{
    /// <summary>
    /// 任务栏悬停浮卡（设计稿 Taskbar.dc.html）：显示悬停那一列的两项（大号数值、近 40 次走势、峰值），
    /// 从任务栏弹簧升起（.5s，略过冲），指针在各列之间移动时浮卡与指向箭头弹簧滑动跟随，而不是闪烁重建。
    /// </summary>
    public sealed class TaskbarFlyout : Form
    {
        private const int W = 272, H = 206, Caret = 7;

        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15 };
        private readonly Tween _show = new(0), _fade = new(0), _x = new(0), _caret = new(0);
        private MetricItem? _a, _b;
        private string _title = "";
        private bool _dark = true;
        private int _baseY;
        private bool _want;

        public TaskbarFlyout()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            DoubleBuffered = true;
            Opacity = 0;
            Size = new Size(S(W), S(H) + S(Caret));
            _timer.Tick += (_, __) => Frame();
        }

        private static int S(float px) => (int)Math.Round(px * UIUtils.ScaleFactor);
        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80 | 0x08000000 | 0x20; // TOOLWINDOW | NOACTIVATE | TRANSPARENT（鼠标穿透，不挡任务栏）
                return cp;
            }
        }

        /// <summary>显示或切换到某一列。columnScreen 为该列在屏幕上的区域，taskbar 为任务栏条区域。</summary>
        public void ShowFor(MetricItem? a, MetricItem? b, string title, Rectangle columnScreen, Rectangle taskbar, bool dark)
        {
            _a = a; _b = b; _title = title; _dark = dark;
            var area = Screen.FromRectangle(taskbar).WorkingArea;
            var bounds = Screen.FromRectangle(taskbar).Bounds;
            bool bottom = taskbar.Top > bounds.Top + bounds.Height / 2;
            int cx = columnScreen.X + columnScreen.Width / 2;
            int left = Math.Clamp(cx - S(W) / 2, bounds.Left + S(16), bounds.Right - S(16) - S(W));
            _baseY = bottom ? taskbar.Top - S(12) - Height : taskbar.Bottom + S(12);

            bool appearing = !_want || !Visible;
            if (appearing) { _x.Snap(left); _caret.Snap(cx - left); }
            else
            {
                _x.Set(left, 500, new Ease(.34, 1.2, .64, 1));
                _caret.Set(cx - left, 500, new Ease(.34, 1.2, .64, 1));
            }
            _show.Set(1, 500, new Ease(.34, 1.45, .64, 1));
            _fade.Set(1, 220, Motion.Out);
            _want = true;
            if (!Visible) { Place(); Show(); }
            _timer.Start();
            Invalidate();
        }

        public void HideFlyout()
        {
            if (!_want) return;
            _want = false;
            _show.Set(0, 300, Motion.Out);
            _fade.Set(0, 220, Motion.Out);
            _timer.Start();
        }

        public void RefreshContent() { if (Visible) Invalidate(); }

        private void Frame()
        {
            long now = Motion.Now;
            Place();
            Invalidate();
            if (!_want && !_fade.IsRunning(now)) { _timer.Stop(); Hide(); return; }
            bool settled = !_show.IsRunning(now) && !_x.IsRunning(now) && !_fade.IsRunning(now);
            _timer.Interval = settled ? 250 : 15;
        }

        private void Place()
        {
            float k = _show.Value();
            int dy = (int)Math.Round((1 - k) * S(14));
            Location = new Point((int)Math.Round(_x.Value()), _baseY + dy);
            Opacity = Math.Clamp(_fade.Value(), 0, 1);
            UpdateRegion();
        }

        private float _lastCaret = float.NaN;
        private void UpdateRegion()
        {
            float c = _caret.Value();
            if (Math.Abs(c - _lastCaret) < 0.5f && Region != null) return;
            _lastCaret = c;
            using var path = UIUtils.RoundRect(new Rectangle(0, 0, Width, S(H)), S(16));
            float cx = Math.Clamp(c, S(22), Width - S(22));
            path.AddPolygon(new[] { new PointF(cx - S(Caret + 1), S(H) - 1), new PointF(cx, S(H) + S(Caret)), new PointF(cx + S(Caret + 1), S(H) - 1) });
            path.FillMode = FillMode.Winding;
            Region = new Region(path);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            try { Paint(e.Graphics); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[TaskbarFlyout] " + ex.Message); }
        }

        private void Paint(Graphics g)
        {
            var tk = Gx.For(_dark);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(tk.Float);
            var body = new Rectangle(0, 0, Width, S(H));
            GxDraw.RoundStroke(g, body, S(16), tk.Line);

            // 弹出时内容从 .96 放大
            float sc = 0.96f + 0.04f * _show.Value();
            g.TranslateTransform(Width / 2f, S(H)); g.ScaleTransform(sc, sc); g.TranslateTransform(-Width / 2f, -S(H));

            int x = S(16), y = S(16), w = Width - S(32);
            GxDraw.Text(g, _title, Gx.F(12, true), new Rectangle(x, y, w, S(18)), tk.Text2);
            GxDraw.Text(g, "近 40 秒", Gx.F(11), new Rectangle(x, y, w, S(18)), tk.Faint, TextFormatFlags.Right);
            y += S(30);

            Color StateColor(MetricItem it, bool line)
            {
                int st = it.CachedColorState;
                return st == 2 ? tk.Crit : st == 1 ? tk.Warn : line ? tk.Acc : tk.Text;
            }
            void Big(MetricItem? it, int cx, int cw)
            {
                if (it == null) return;
                it.GetFormattedText(false);
                GxDraw.Text(g, Name(it), Gx.F(11), new Rectangle(cx, y, cw, S(16)), tk.Muted, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
                string v = it.TextValue != null ? it.GetFormattedText(false) : it.CachedValueText;
                string u = it.TextValue != null ? "" : it.CachedUnitText;
                var fv = Gx.F(28, false, true);
                int vw = GxDraw.Measure(g, v, fv);
                GxDraw.Text(g, v, fv, new Rectangle(cx, y + S(20), Math.Min(vw + S(4), cw), S(32)), StateColor(it, false));
                GxDraw.Text(g, u, Gx.F(12), new Rectangle(cx + vw + S(3), y + S(32), S(60), S(18)), tk.Faint);
            }
            int half = (w - S(12)) / 2;
            Big(_a, x, half);
            Big(_b, x + half + S(12), half);
            y += S(62);

            // 走势：第一项带面积，第二项为淡色线
            var spark = new Rectangle(x, y, S(240), S(56));
            // 两条线各自按自身范围缩放（单位往往不同，如 % 与 °C），留 15% 上下余量
            PointF[]? Pts(MetricItem? it)
            {
                if (it == null || it.HistoryCount < 2) return null;
                int n = it.HistoryCount;
                float lo = float.MaxValue, hi = float.MinValue;
                for (int i = 0; i < n; i++) { lo = Math.Min(lo, it.HistoryAt(i)); hi = Math.Max(hi, it.HistoryAt(i)); }
                float span = Math.Max(hi - lo, Math.Max(Math.Abs(hi) * 0.05f, 1e-3f));
                lo -= span * 0.15f; hi += span * 0.15f;
                var p = new PointF[n];
                for (int i = 0; i < n; i++)
                    p[i] = new PointF(spark.X + spark.Width * i / (float)(n - 1), spark.Bottom - S(2) - Math.Clamp((it.HistoryAt(i) - lo) / (hi - lo), 0, 1) * (spark.Height - S(4)));
                return p;
            }
            var pa = Pts(_a);
            if (pa != null)
            {
                Color c = StateColor(_a!, true);
                using (var path = new GraphicsPath())
                {
                    path.AddLines(pa);
                    path.AddLine(pa[^1].X, spark.Bottom, pa[0].X, spark.Bottom);
                    path.CloseFigure();
                    using var fill = new SolidBrush(Color.FromArgb(33, c));
                    g.FillPath(fill, path);
                }
                using var pen = new Pen(c, S(1.6f)) { LineJoin = LineJoin.Round };
                g.DrawLines(pen, pa);
            }
            var pb = Pts(_b);
            if (pb != null)
            {
                using var pen = new Pen(Color.FromArgb(140, tk.Muted), S(1.6f)) { LineJoin = LineJoin.Round };
                g.DrawLines(pen, pb);
            }
            y = spark.Bottom + S(10);

            // 图例与峰值（峰值靠右，图例只占剩余宽度，放不下就省略号）
            int legendRight = x + w;
            if (_a != null && _a.HistoryCount > 0)
            {
                float peak = float.MinValue;
                for (int i = 0; i < _a.HistoryCount; i++) peak = Math.Max(peak, _a.HistoryAt(i));
                string ps = MetricUtils.GetValueStr(_a.Key, peak, false, _a.BoundConfig?.UnitPanel) +
                            MetricUtils.GetDisplayUnit(_a.Key, MetricUtils.GetUnitStr(_a.Key, peak, MetricUtils.UnitContext.Panel), _a.BoundConfig?.UnitPanel);
                int pw = GxDraw.Measure(g, ps, Gx.F(11, false, true));
                GxDraw.Text(g, ps, Gx.F(11, false, true), new Rectangle(x + w - pw, y, pw, S(16)), tk.Text2);
                GxDraw.Text(g, "峰值", Gx.F(11), new Rectangle(x + w - pw - S(32), y, S(30), S(16)), tk.Faint);
                legendRight = x + w - pw - S(40);
            }
            int lx = x;
            void Legend(MetricItem? it, Color c)
            {
                if (it == null || lx + S(30) > legendRight) return;
                GxDraw.Round(g, new Rectangle(lx, y + S(7), S(8), S(2)), S(1), c);
                string l = Name(it);
                int lw = Math.Min(GxDraw.Measure(g, l, Gx.F(11)) + S(2), legendRight - lx - S(13));
                GxDraw.Text(g, l, Gx.F(11), new Rectangle(lx + S(13), y, lw, S(16)), tk.Faint, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
                lx += S(13) + lw + S(12);
            }
            if (_a != null) Legend(_a, StateColor(_a, true));
            Legend(_b, tk.Muted);
        }

        /// <summary>标题已写明分组，两项去掉共同的英文前缀（如 "CPU使用率 / CPU温度" → "使用率 / 温度"）。</summary>
        private string Name(MetricItem it)
        {
            string l = string.IsNullOrEmpty(it.Label) ? it.Key : it.Label;
            var other = it == _a ? _b : _a;
            if (other == null) return l;
            string o = string.IsNullOrEmpty(other.Label) ? other.Key : other.Label;
            int n = 0;
            while (n < l.Length && n < o.Length && l[n] == o[n] && char.IsAscii(l[n])) n++;
            if (n >= 2 && n < l.Length && n < o.Length) l = l.Substring(n).Trim();
            return l;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _timer.Dispose();
            base.Dispose(disposing);
        }
    }
}
