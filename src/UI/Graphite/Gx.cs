using LiteMonitor.src.Core;
using LiteMonitor.src.UI.Controls;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace LiteMonitor.src.UI.Graphite
{
    /// <summary>
    /// 设计稿（lm-theme.css）的色板，深浅两套逐值照搬。随 UIColors.IsDark 切换。
    /// </summary>
    public sealed class Gx
    {
        public Color Bg, Dot, Win, Side, Surf, Card, Float, Hover, Sel, Line, Line2, Track, Seg, Thumb, SwOff;
        public Color Text, Text2, Muted, Faint, Ghost, Acc, Acc2, OnAcc, Warn, Crit, Up, Shadow;
        public Color[] Series = Array.Empty<Color>();
        public bool Dark;

        private static Gx? _dark, _light;
        public static Gx Current => For(UIColors.IsDark);
        /// <summary>指定深浅的色板（任务栏浮卡跟随系统任务栏的明暗，而不是界面模式）。</summary>
        public static Gx For(bool dark) => dark ? (_dark ??= Build(true)) : (_light ??= Build(false));

        private static Color H(string s) => ColorTranslator.FromHtml(s);

        private static Gx Build(bool dark) => dark
            ? new Gx
            {
                Dark = true,
                Bg = H("#0B0D10"), Dot = Color.FromArgb(11, 255, 255, 255), Win = H("#111317"), Side = H("#0D0F12"),
                Surf = H("#14171C"), Card = H("#161920"), Float = H("#1C2028"), Hover = H("#1A1E25"), Sel = H("#1C2129"),
                Line = H("#262A32"), Line2 = H("#20242C"), Track = H("#232830"), Seg = H("#0D0F12"), Thumb = H("#262B34"), SwOff = H("#3A404B"),
                Text = H("#E8EAED"), Text2 = H("#C9CDD3"), Muted = H("#9AA1AC"), Faint = H("#7D8590"), Ghost = H("#4B525D"),
                Acc = H("#6CB6FF"), Acc2 = H("#9CCDFF"), OnAcc = H("#0B0D10"), Warn = H("#F2B24C"), Crit = H("#FF6B6B"), Up = H("#F59E5B"),
                Shadow = Color.FromArgb(128, 0, 0, 0),
                Series = new[] { H("#6CB6FF"), H("#F59E5B"), H("#4FD1C5"), H("#F472B6"), H("#C4A1FF"), H("#9CCDFF"), H("#2BB3A6") }
            }
            : new Gx
            {
                Dark = false,
                Bg = H("#E9ECF0"), Dot = Color.FromArgb(18, 20, 30, 50), Win = H("#F7F8FA"), Side = H("#EFF1F4"),
                Surf = H("#FFFFFF"), Card = H("#FFFFFF"), Float = H("#FFFFFF"), Hover = H("#F1F3F6"), Sel = H("#EAF2FD"),
                Line = H("#DFE3E8"), Line2 = H("#ECEEF2"), Track = H("#E5E8ED"), Seg = H("#E8EBEF"), Thumb = H("#FFFFFF"), SwOff = H("#C5CBD3"),
                Text = H("#15181D"), Text2 = H("#353B44"), Muted = H("#555C66"), Faint = H("#666D77"), Ghost = H("#A2A8B1"),
                Acc = H("#1F6FD1"), Acc2 = H("#1B5FB4"), OnAcc = Color.White, Warn = H("#9A5B00"), Crit = H("#CF3535"), Up = H("#DC7424"),
                Shadow = Color.FromArgb(36, 20, 30, 50),
                Series = new[] { H("#1F6FD1"), H("#D9711F"), H("#0E9488"), H("#D23C8A"), H("#7C5CD6"), H("#5E9BE6"), H("#12A394") }
            };

        /// <summary>color-mix(in srgb, c k%, transparent) 叠在 bg 上的结果。</summary>
        public static Color Mix(Color bg, Color c, float k)
        {
            k = Math.Clamp(k, 0f, 1f);
            return Color.FromArgb((int)(bg.R + (c.R - bg.R) * k), (int)(bg.G + (c.G - bg.G) * k), (int)(bg.B + (c.B - bg.B) * k));
        }

        // ===== 字体：设计稿字号以 px 给出，Geist → 微软雅黑 UI，Geist Mono → Cascadia Mono / Consolas =====
        private static readonly Dictionary<(float, bool, bool), Font> _fonts = new();
        private static readonly string UiFamily = "Microsoft YaHei UI";
        private static readonly string MonoFamily = GraphitePanel.ResolveFamily("Cascadia Mono", "Consolas");

        public static Font F(float px, bool bold = false, bool mono = false)
        {
            var key = (px, bold, mono);
            if (!_fonts.TryGetValue(key, out var f))
            {
                f = new Font(mono ? MonoFamily : UiFamily, px * 0.75f, bold ? FontStyle.Bold : FontStyle.Regular);
                _fonts[key] = f;
            }
            return f;
        }
    }

    /// <summary>
    /// 自绘画布：每次绘制时登记可点击 / 可拖动区域，鼠标事件按登记分发；
    /// 动效以字符串 id 为键保存补间，任何补间在跑就持续重绘；支持纵向平滑滚动与深浅切换的圆形揭开。
    /// </summary>
    public abstract class GCanvas : Control
    {
        private sealed class HitArea
        {
            public string Id = "";
            public Rectangle R;
            public Action? Click;
            public Action<Point>? Drag;
            public Action? Release;
            public Cursor Cursor = Cursors.Hand;
        }

        private readonly List<HitArea> _hits = new();
        private readonly Dictionary<string, Tween> _anims = new();
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15 };
        private HitArea? _captured;
        private bool _wantFrames;

        protected Point Mouse { get; private set; } = new Point(-9999, -9999);
        protected string PressedId { get; private set; } = "";
        protected Gx Tk => Gx.Current;

        // 滚动：内容高度由 Draw 返回
        protected bool Scrollable { get; set; }
        private readonly Tween _scroll = new Tween(0);
        private int _contentHeight;
        protected int ScrollY => (int)Math.Round(_scroll.Value());

        protected GCanvas()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            _timer.Tick += (_, __) => Invalidate();
        }

        /// <summary>按设计稿 px 缩放到当前 DPI。</summary>
        protected static int S(float px) => (int)Math.Round(px * UIUtils.ScaleFactor);
        protected static float Sf(float px) => px * UIUtils.ScaleFactor;

        /// <summary>绘制内容；返回内容总高度（用于滚动）。坐标为内容坐标（已扣除滚动）。</summary>
        protected abstract int Draw(Graphics g);

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            _hits.Clear();
            _wantFrames = false;
            try
            {
                _contentHeight = Draw(g);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[GCanvas] 绘制失败: {ex}");
            }
            DrawReveal(g);

            long now = Motion.Now;
            bool running = _wantFrames || _scroll.IsRunning(now) || RevealRunning(now);
            if (!running) foreach (var t in _anims.Values) if (t.IsRunning(now)) { running = true; break; }
            if (running) { if (!_timer.Enabled) _timer.Start(); }
            else _timer.Stop();
        }

        /// <summary>需要连续重绘（例如脉冲、数值补间）时在 Draw 中调用。</summary>
        protected void KeepAnimating() => _wantFrames = true;

        /// <summary>以 id 为键的补间：目标变化时按时长与缓动过渡，返回当前值。「动效：关闭」时立即到位。</summary>
        protected float A(string id, float target, float durMs, Ease ease, float delayMs = 0)
        {
            if (!_anims.TryGetValue(id, out var t)) { t = new Tween(target); _anims[id] = t; return target; }
            t.Set(target, durMs, ease, delayMs);
            return t.Value();
        }

        /// <summary>第一次出现时从 from 过渡到 to（入场类动效），之后与 A 相同。</summary>
        protected float AFrom(string id, float from, float to, float durMs, Ease ease, float delayMs = 0)
        {
            if (!_anims.TryGetValue(id, out var t)) { t = new Tween(from); _anims[id] = t; }
            t.Set(to, durMs, ease, delayMs);
            return t.Value();
        }

        /// <summary>重新播放某个从 from 到 to 的补间（例如每次切换页面都要重新入场）。</summary>
        protected void Replay(string id, float from, float to, float durMs, Ease ease, float delayMs = 0)
        {
            var t = new Tween(from);
            _anims[id] = t;
            t.Set(to, durMs, ease, delayMs);
            Invalidate();
        }

        protected float Peek(string id, float fallback) => _anims.TryGetValue(id, out var t) ? t.Value() : fallback;

        /// <summary>登记点击区域（内容坐标）。</summary>
        protected void Hit(string id, Rectangle r, Action click, Cursor? cursor = null) =>
            _hits.Add(new HitArea { Id = id, R = ToClient(r), Click = click, Cursor = cursor ?? Cursors.Hand });

        /// <summary>登记拖动区域：按下与拖动时以客户区坐标回调。</summary>
        protected void DragHit(string id, Rectangle r, Action<Point> drag, Action? release = null) =>
            _hits.Add(new HitArea { Id = id, R = ToClient(r), Drag = drag, Release = release, Cursor = Cursors.Hand });

        /// <summary>只登记悬停（不响应点击），用于浮窗等。</summary>
        protected void HoverArea(string id, Rectangle r, Cursor? cursor = null) =>
            _hits.Add(new HitArea { Id = id, R = ToClient(r), Cursor = cursor ?? Cursors.Default });

        private Rectangle ToClient(Rectangle r) => Scrollable ? new Rectangle(r.X, r.Y - ScrollY, r.Width, r.Height) : r;

        /// <summary>鼠标是否在某内容区域上。</summary>
        protected bool Hot(Rectangle r) => ToClient(r).Contains(Mouse);

        protected bool IsPressed(string id) => PressedId == id;

        protected Point ContentMouse => Scrollable ? new Point(Mouse.X, Mouse.Y + ScrollY) : Mouse;

        private HitArea? HitAt(Point p)
        {
            for (int i = _hits.Count - 1; i >= 0; i--) if (_hits[i].R.Contains(p)) return _hits[i];
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Mouse = e.Location;
            if (_captured?.Drag != null) { _captured.Drag(e.Location); Invalidate(); return; }
            Cursor = HitAt(e.Location)?.Cursor ?? Cursors.Default;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_captured == null) Mouse = new Point(-9999, -9999);
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button != MouseButtons.Left) return;
            var h = HitAt(e.Location);
            PressedId = h?.Id ?? "";
            if (h?.Drag != null) { _captured = h; Capture = true; h.Drag(e.Location); }
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            if (_captured != null)
            {
                _captured.Release?.Invoke();
                _captured = null;
                Capture = false;
            }
            else
            {
                var h = HitAt(e.Location);
                if (h != null && h.Id == PressedId) h.Click?.Invoke();
            }
            PressedId = "";
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!Scrollable) return;
            int max = Math.Max(0, _contentHeight - ClientSize.Height);
            float target = Math.Clamp(_scroll.Target - e.Delta * 0.9f, 0, max);
            _scroll.Set(target, 260, Motion.Out);
            Invalidate();
        }

        protected void ResetScroll() => _scroll.Snap(0);

        /// <summary>画细滚动条（只在内容超出时出现）。</summary>
        protected void DrawScrollbar(Graphics g, Rectangle viewport)
        {
            if (!Scrollable || _contentHeight <= viewport.Height) return;
            float ratio = viewport.Height / (float)_contentHeight;
            int h = Math.Max(S(30), (int)(viewport.Height * ratio));
            int y = viewport.Y + (int)((viewport.Height - h) * (ScrollY / (float)Math.Max(1, _contentHeight - viewport.Height)));
            UIUtils.FillRoundRect(g, new Rectangle(viewport.Right - S(6), y, S(4), h), S(2), Gx.Mix(Tk.Win, Tk.Text, 0.10f));
        }

        // ===== 深浅切换：圆形揭开（.76s，InOut）=====
        private Bitmap? _revealSnap;
        private PointF _revealOrigin;
        private long _revealStart = -100000;
        private bool RevealRunning(long now) => _revealSnap != null && now - _revealStart < 760 * Math.Max(0.01f, Motion.DurationScale);

        /// <summary>在切换配色前调用：存下当前画面，新配色从 origin（客户区坐标）以圆形扩散盖过旧画面。</summary>
        public void BeginReveal(Point origin)
        {
            if (Width <= 0 || Height <= 0 || Motion.DurationScale <= 0) return;
            var bmp = new Bitmap(Width, Height);
            DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
            _revealSnap?.Dispose();
            _revealSnap = bmp;
            _revealOrigin = origin;
            _revealStart = Motion.Now;
            Invalidate();
        }

        private void DrawReveal(Graphics g)
        {
            if (_revealSnap == null) return;
            long now = Motion.Now;
            if (!RevealRunning(now)) { _revealSnap.Dispose(); _revealSnap = null; return; }
            float p = Motion.InOut.Eval(Motion.Progress(_revealStart, 0, 760 * Motion.DurationScale, now));
            float maxR = 0;
            foreach (var c in new[] { new PointF(0, 0), new PointF(Width, 0), new PointF(0, Height), new PointF(Width, Height) })
                maxR = Math.Max(maxR, (float)Math.Sqrt(Math.Pow(c.X - _revealOrigin.X, 2) + Math.Pow(c.Y - _revealOrigin.Y, 2)));
            float r = maxR * p;
            using var circle = new GraphicsPath();
            circle.AddEllipse(_revealOrigin.X - r, _revealOrigin.Y - r, r * 2, r * 2);
            using var outside = new Region(new Rectangle(0, 0, Width, Height));
            outside.Exclude(circle);
            var st = g.Save();
            g.ResetTransform();
            g.SetClip(outside, CombineMode.Replace);
            g.DrawImageUnscaled(_revealSnap, 0, 0);
            g.Restore(st);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _timer.Dispose(); _revealSnap?.Dispose(); }
            base.Dispose(disposing);
        }
    }

    /// <summary>设计稿里反复出现的小部件的画法。</summary>
    public static class GxDraw
    {
        public static int S(float px) => (int)Math.Round(px * UIUtils.ScaleFactor);

        public static void Text(Graphics g, string text, Font font, Rectangle r, Color c, TextFormatFlags flags = TextFormatFlags.Left) =>
            GraphitePanel.DrawText(g, text, font, r, c, flags);

        public static int Measure(Graphics g, string text, Font font) => GraphitePanel.MeasureW(g, text, font);

        public static void Round(Graphics g, Rectangle r, float radius, Color c)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            UIUtils.FillRoundRect(g, r, (int)Math.Round(radius), c);
        }

        public static void RoundStroke(Graphics g, Rectangle r, float radius, Color c, float width = 1f)
        {
            if (r.Width <= 1 || r.Height <= 1) return;
            using var path = UIUtils.RoundRect(new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1), (int)Math.Round(radius));
            using var pen = new Pen(c, width);
            g.DrawPath(pen, path);
        }

        /// <summary>卡片：背景 + 1px 边框，圆角 14/16。</summary>
        public static void Card(Graphics g, Rectangle r, Gx tk, float radius = 16)
        {
            Round(g, r, S(radius), tk.Card);
            RoundStroke(g, r, S(radius), tk.Line2);
        }

        /// <summary>阴影：几层逐渐扩大的半透明圆角矩形（模拟 box-shadow）。</summary>
        public static void Shadow(Graphics g, Rectangle r, float radius, Color shadow, int spread = 18, int offsetY = 10)
        {
            for (int i = spread; i > 0; i -= 3)
            {
                int a = (int)(shadow.A * 0.18f * (1 - i / (float)(spread + 1)));
                if (a <= 0) continue;
                var rr = new Rectangle(r.X - i / 2, r.Y + offsetY - i / 2 + 2, r.Width + i, r.Height + i);
                Round(g, rr, radius + i / 2f, Color.FromArgb(a, shadow));
            }
        }

        /// <summary>开关：40×24（设计稿），on 为 0~1 的过渡进度（弹簧 .5s），按下时滑块略微拉宽。</summary>
        public static void Switch(Graphics g, Rectangle r, float on, Gx tk, bool pressed, Color? accent = null)
        {
            Color acc = accent ?? tk.Acc;
            Round(g, r, r.Height / 2f, Gx.Mix(tk.SwOff, acc, Math.Clamp(on, 0, 1)));
            int pad = S(3);
            int d = r.Height - pad * 2;
            int knobW = d + (pressed ? S(4) : 0);
            float travel = r.Width - pad * 2 - knobW;
            float x = r.X + pad + travel * on;
            using var shadow = new SolidBrush(Color.FromArgb(60, 0, 0, 0));
            using var knob = new SolidBrush(Color.White);
            var kr = new RectangleF(x, r.Y + pad, knobW, d);
            using var path = RoundF(new RectangleF(kr.X, kr.Y + 1, kr.Width, kr.Height), d / 2f);
            g.FillPath(shadow, path);
            using var path2 = RoundF(kr, d / 2f);
            g.FillPath(knob, path2);
        }

        public static GraphicsPath RoundF(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>分段控件：底 + 滑块（thumb 为 0..n-1 之间的连续位置，弹簧动画）+ 文字。返回每段区域。</summary>
        public static Rectangle[] Segmented(Graphics g, Rectangle r, string[] labels, float thumb, int selected, Gx tk, Font font, int[]? icons = null)
        {
            Round(g, r, S(10), tk.Seg);
            RoundStroke(g, r, S(10), tk.Line);
            int pad = S(3);
            int w = (r.Width - pad * 2) / labels.Length;
            var segs = new Rectangle[labels.Length];
            for (int i = 0; i < labels.Length; i++) segs[i] = new Rectangle(r.X + pad + i * w, r.Y + pad, w, r.Height - pad * 2);
            var th = new RectangleF(r.X + pad + thumb * w, r.Y + pad, w, r.Height - pad * 2);
            using (var sh = new SolidBrush(Color.FromArgb(tk.Dark ? 0 : 30, 20, 30, 50)))
            using (var sp = RoundF(new RectangleF(th.X, th.Y + 1, th.Width, th.Height), S(7)))
                g.FillPath(sh, sp);
            using (var tb = new SolidBrush(tk.Thumb))
            using (var tp = RoundF(th, S(7)))
                g.FillPath(tb, tp);
            for (int i = 0; i < labels.Length; i++)
            {
                float k = Math.Clamp(1 - Math.Abs(thumb - i), 0, 1);
                Text(g, labels[i], font, segs[i], Gx.Mix(tk.Faint, tk.Text, k), TextFormatFlags.HorizontalCenter);
            }
            return segs;
        }

        /// <summary>滑块：轨道 + 已选部分 + 圆形手柄（悬停 / 拖动时放大）。</summary>
        public static void Slider(Graphics g, Rectangle r, float value01, Color accent, Gx tk, float hot)
        {
            int cy = r.Y + r.Height / 2;
            var track = new Rectangle(r.X, cy - S(2), r.Width, S(4));
            Round(g, track, S(2), tk.Track);
            int fx = (int)Math.Round(r.Width * Math.Clamp(value01, 0, 1));
            Round(g, new Rectangle(r.X, track.Y, fx, track.Height), S(2), accent);
            float rad = Sf(7 + 2 * hot);
            float x = r.X + fx;
            using var halo = new SolidBrush(Color.FromArgb((int)(50 * hot), accent));
            g.FillEllipse(halo, x - rad - Sf(4), cy - rad - Sf(4), (rad + Sf(4)) * 2, (rad + Sf(4)) * 2);
            using var b = new SolidBrush(accent);
            g.FillEllipse(b, x - rad, cy - rad, rad * 2, rad * 2);
            using var inner = new SolidBrush(tk.Card);
            float ir = rad * 0.42f;
            g.FillEllipse(inner, x - ir, cy - ir, ir * 2, ir * 2);
        }

        private static float Sf(float px) => px * UIUtils.ScaleFactor;

        /// <summary>月牙：大圆 (12,12,r8.5) 减去右上的小圆 (16.5,7.5,r7)，按两圆交点拼出轮廓。</summary>
        private static PointF[] Crescent(RectangleF r, float k)
        {
            double c1x = 12, c1y = 12, r1 = 8.5, c2x = 16.5, c2y = 7.5, r2 = 7;
            double dx = c2x - c1x, dy = c2y - c1y, d = Math.Sqrt(dx * dx + dy * dy);
            double a = (r1 * r1 - r2 * r2 + d * d) / (2 * d), h = Math.Sqrt(Math.Max(0, r1 * r1 - a * a));
            double mx = c1x + a * dx / d, my = c1y + a * dy / d;
            var p1 = (x: mx + h * dy / d, y: my - h * dx / d);
            var p2 = (x: mx - h * dy / d, y: my + h * dx / d);
            var pts = new List<PointF>();
            void Arc(double cx, double cy, double rad, (double x, double y) from, (double x, double y) to, bool keepInside)
            {
                double a0 = Math.Atan2(from.y - cy, from.x - cx), a1 = Math.Atan2(to.y - cy, to.x - cx);
                double sweep = a1 - a0;
                if (sweep < 0) sweep += 2 * Math.PI;
                // 选取中点满足条件（在另一个圆外 / 内）的那一侧
                double mid = a0 + sweep / 2;
                double mxp = cx + rad * Math.Cos(mid) - (keepInside ? c1x : c2x), myp = cy + rad * Math.Sin(mid) - (keepInside ? c1y : c2y);
                bool inside = Math.Sqrt(mxp * mxp + myp * myp) < (keepInside ? r1 : r2);
                if (inside != keepInside) sweep -= 2 * Math.PI;
                for (int i = 0; i <= 24; i++)
                {
                    double t = a0 + sweep * i / 24;
                    pts.Add(new PointF(r.X + (float)(cx + rad * Math.Cos(t)) * k, r.Y + (float)(cy + rad * Math.Sin(t)) * k));
                }
            }
            Arc(c1x, c1y, r1, p1, p2, keepInside: false); // 大圆上、位于小圆之外的一段
            Arc(c2x, c2y, r2, p2, p1, keepInside: true);  // 小圆上、位于大圆之内的一段
            return pts.ToArray();
        }

        /// <summary>线条图标（24 视图框路径的简化子集：M/L/H/V/Z，以及圆）。</summary>
        public static void Icon(Graphics g, string kind, RectangleF r, Color c, float width = 1.7f)
        {
            float k = r.Width / 24f;
            PointF P(float x, float y) => new PointF(r.X + x * k, r.Y + y * k);
            using var pen = new Pen(c, Sf(width)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            switch (kind)
            {
                case "moon":
                    using (var p = new GraphicsPath())
                    {
                        p.AddPolygon(Crescent(r, k));
                        g.DrawPath(pen, p);
                    }
                    break;
                case "sun":
                    g.DrawEllipse(pen, r.X + 8 * k, r.Y + 8 * k, 8 * k, 8 * k);
                    for (int i = 0; i < 8; i++)
                    {
                        double a = i * Math.PI / 4;
                        g.DrawLine(pen, P(12 + 6.5f * (float)Math.Cos(a), 12 + 6.5f * (float)Math.Sin(a)), P(12 + 9.5f * (float)Math.Cos(a), 12 + 9.5f * (float)Math.Sin(a)));
                    }
                    break;
                case "monitor":
                    g.DrawRectangle(pen, r.X + 3 * k, r.Y + 5 * k, 18 * k, 11 * k);
                    g.DrawLine(pen, P(8, 20), P(16, 20)); g.DrawLine(pen, P(12, 16), P(12, 20));
                    break;
                case "check":
                    g.DrawLines(pen, new[] { P(5, 12), P(10, 17), P(19, 7) });
                    break;
                case "close":
                    g.DrawLine(pen, P(6, 6), P(18, 18)); g.DrawLine(pen, P(18, 6), P(6, 18));
                    break;
                case "grip":
                    using (var b = new SolidBrush(c))
                        foreach (var (x, y) in new[] { (9, 6), (15, 6), (9, 12), (15, 12), (9, 18), (15, 18) })
                            g.FillEllipse(b, r.X + x * k - 1.3f * k, r.Y + y * k - 1.3f * k, 2.6f * k, 2.6f * k);
                    break;
                case "palette":
                    g.DrawEllipse(pen, r.X + 3 * k, r.Y + 3 * k, 18 * k, 18 * k);
                    using (var b = new SolidBrush(c))
                        foreach (var (x, y) in new[] { (7.5f, 11.5f), (10f, 7.5f), (15f, 7.5f) })
                            g.FillEllipse(b, r.X + x * k - 1.2f * k, r.Y + y * k - 1.2f * k, 2.4f * k, 2.4f * k);
                    break;
                case "list":
                    g.DrawLine(pen, P(4, 6), P(20, 6)); g.DrawLine(pen, P(4, 12), P(20, 12)); g.DrawLine(pen, P(4, 18), P(14, 18));
                    break;
                case "alert":
                    g.DrawPolygon(pen, new[] { P(12, 3), P(21, 19), P(3, 19) });
                    g.DrawLine(pen, P(12, 10), P(12, 14)); g.DrawLine(pen, P(12, 17), P(12, 17.1f));
                    break;
                case "taskbar":
                    g.DrawRectangle(pen, r.X + 3 * k, r.Y + 16 * k, 18 * k, 4 * k);
                    g.DrawLine(pen, P(7, 18), P(10, 18));
                    break;
                case "chip":
                    g.DrawRectangle(pen, r.X + 6 * k, r.Y + 6 * k, 12 * k, 12 * k);
                    foreach (float t in new[] { 9f, 15f })
                    {
                        g.DrawLine(pen, P(t, 2), P(t, 6)); g.DrawLine(pen, P(t, 18), P(t, 22));
                        g.DrawLine(pen, P(2, t), P(6, t)); g.DrawLine(pen, P(18, t), P(22, t));
                    }
                    break;
                case "grid":
                    foreach (var (x, y) in new[] { (3, 3), (13, 3), (3, 13), (13, 13) })
                        g.DrawRectangle(pen, r.X + x * k, r.Y + y * k, 8 * k, 8 * k);
                    break;
                case "sliders":
                    g.DrawLine(pen, P(4, 7), P(20, 7)); g.DrawLine(pen, P(4, 17), P(20, 17));
                    g.DrawEllipse(pen, r.X + 7 * k, r.Y + 5 * k, 4 * k, 4 * k); g.DrawEllipse(pen, r.X + 13 * k, r.Y + 15 * k, 4 * k, 4 * k);
                    break;
                case "logo":
                    g.DrawLines(pen, new[] { P(3, 12), P(7, 12), P(10, 5), P(14, 19), P(17, 12), P(21, 12) });
                    break;
                case "download":
                    g.DrawLine(pen, P(12, 4), P(12, 16)); g.DrawLines(pen, new[] { P(7, 11), P(12, 16), P(17, 11) }); g.DrawLine(pen, P(5, 20), P(19, 20));
                    break;
                case "trash":
                    g.DrawLine(pen, P(4, 7), P(20, 7)); g.DrawRectangle(pen, r.X + 6 * k, r.Y + 7 * k, 12 * k, 13 * k); g.DrawLine(pen, P(10, 4), P(14, 4));
                    break;
            }
        }
    }
}
