using LiteMonitor.src.Core;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace LiteMonitor.src.UI.Graphite
{
    /// <summary>流量统计窗口（设计稿 Traffic.dc.html，1200×800）。</summary>
    public sealed class TrafficWindow : GraphiteWindow
    {
        public TrafficWindow(Settings cfg) : base(new TrafficCanvas(cfg), LanguageManager.T("Menu.TrafficHistory") is var t && t != "Menu.TrafficHistory" ? t : "流量统计", 1200, 800) { }
    }

    internal sealed class TrafficCanvas : GraphiteCanvas
    {
        private sealed class Day
        {
            public DateTime Date;
            public int Index;
            public bool Missing;
            public double Up, Down; // GB
            public double Total => Up + Down;
        }

        private sealed class Period
        {
            public string Label = "", Full = "";
            public List<Day> Days = new();
            public double Up => Days.Sum(d => d.Up);
            public double Down => Days.Sum(d => d.Down);
            public double Total => Up + Down;
        }

        private readonly Settings _cfg;
        private List<Day> _days = new();
        private double[] _heatQ = { 0, 0, 0, 0 };
        private string _view = "day";
        private int _hover = -1, _lock = -1, _heatHover = -1;
        private int _barAnimKey;
        private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 5000 };

        public TrafficCanvas(Settings cfg)
        {
            _cfg = cfg;
            LoadData();
            _refresh.Tick += (_, __) => { LoadData(); Invalidate(); };
            _refresh.Start();
            ReplayBars();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _refresh.Dispose();
            base.Dispose(disposing);
        }

        private void LoadData()
        {
            Dictionary<string, DailyRecord> hist;
            hist = TrafficLogger.GetHistorySnapshot();
            var parsed = new Dictionary<DateTime, DailyRecord>();
            foreach (var kv in hist)
                if (DateTime.TryParseExact(kv.Key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) parsed[d.Date] = kv.Value;

            _days = new List<Day>();
            if (parsed.Count == 0) return;
            var start = parsed.Keys.Min();
            var end = DateTime.Today > parsed.Keys.Max() ? DateTime.Today : parsed.Keys.Max();
            int i = 0;
            for (var d = start; d <= end; d = d.AddDays(1))
            {
                bool has = parsed.TryGetValue(d, out var r);
                _days.Add(new Day { Date = d, Index = i++, Missing = !has, Up = has ? r!.Upload / 1e9 : 0, Down = has ? r!.Download / 1e9 : 0 });
            }
            var sorted = _days.Where(x => !x.Missing).Select(x => x.Down).OrderBy(x => x).ToList();
            double Q(double p) => sorted.Count == 0 ? 0 : sorted[(int)Math.Floor(p * (sorted.Count - 1))];
            _heatQ = new[] { Q(0.3), Q(0.6), Q(0.85), Q(0.95) };
        }

        private List<Period> Periods()
        {
            var ps = new List<Period>();
            if (_days.Count == 0) return ps;
            if (_view == "day")
            {
                foreach (var d in _days.TakeLast(30))
                    ps.Add(new Period { Label = $"{d.Date.Month}/{d.Date.Day}", Full = $"{d.Date.Month}月{d.Date.Day}日", Days = new() { d } });
            }
            else if (_view == "week")
            {
                // 按自然周（周一开始）汇总，每根柱子是一整周的合计
                foreach (var grp in _days.GroupBy(d => d.Date.AddDays(-(((int)d.Date.DayOfWeek + 6) % 7))))
                {
                    var a = grp.Key; var b = a.AddDays(6);
                    ps.Add(new Period { Label = $"{a.Month}/{a.Day}", Full = $"{a.Month}月{a.Day}日 – {b.Month}月{b.Day}日", Days = grp.ToList() });
                }
                if (ps.Count > 21) ps = ps.Skip(ps.Count - 21).ToList();
            }
            else
            {
                foreach (var grp in _days.GroupBy(d => new { d.Date.Year, d.Date.Month }))
                    ps.Add(new Period { Label = $"{grp.Key.Month}月", Full = $"{grp.Key.Year}年{grp.Key.Month}月", Days = grp.ToList() });
            }
            return ps;
        }

        private void ReplayBars() { _barAnimKey++; Invalidate(); }

        private string Unit => _view == "week" ? "周" : _view == "month" ? "月" : "天";

        private static (string v, string u) Gb(double gb)
        {
            if (gb >= 1000) return ((gb / 1000).ToString("0.00"), "TB");
            if (gb >= 100) return (gb.ToString("0"), "GB");
            if (gb >= 1) return (gb.ToString("0.0"), "GB");
            return ((gb * 1024).ToString("0"), "MB");
        }
        private static string Gbs(double gb) { var (v, u) = Gb(gb); return v + " " + u; }

        protected override int Draw(Graphics g)
        {
            var tk = Tk;
            using (var bg = new SolidBrush(tk.Win)) g.FillRectangle(bg, ClientRectangle);
            int W = ClientSize.Width;
            int x0 = S(36), y0 = S(28), cw = W - S(72);
            long now = Motion.Now;

            var ps = Periods();
            int n = ps.Count;

            // ===== 标题行 =====
            GxDraw.Text(g, "流量统计", Gx.F(22, true), new Rectangle(x0, y0, S(400), S(30)), tk.Text);
            string range = _days.Count == 0 ? "暂无记录" :
                $"{_days[0].Date:yyyy年M月d日} – {_days[^1].Date:M月d日} · 共 {_days.Count(d => !d.Missing)} 天有记录 · 本次运行 ↑ {MetricUtils.FormatDataSize(_cfg.SessionUploadBytes)}  ↓ {MetricUtils.FormatDataSize(_cfg.SessionDownloadBytes)}";
            GxDraw.Text(g, range, Gx.F(12), new Rectangle(x0, y0 + S(34), cw - S(260), S(18)), tk.Faint);

            var modeR = new Rectangle(x0 + cw - S(32), y0 + S(8), S(32), S(32));
            ModeButton(g, modeR);
            var segR = new Rectangle(modeR.X - S(14) - S(56 * 4 + 6), y0 + S(10), S(56 * 4 + 6), S(34));
            string[] views = { "day", "week", "month", "total" };
            Seg(g, "view", segR, new[] { "日", "周", "月", "累计" }, Array.IndexOf(views, _view), i =>
            {
                _view = views[i]; _hover = -1; _lock = -1; _cumHover = -1; ReplayBars();
            });

            if (_view == "total") return DrawTotalView(g, x0, y0, cw);

            // ===== 大号数字（补间到当前时段或锁定时段）=====
            int hy = y0 + S(74);
            double sumDn, sumUp; int dcount;
            var target = _lock >= 0 && _lock < n ? new List<Period> { ps[_lock] } : ps;
            sumDn = target.Sum(p => p.Down); sumUp = target.Sum(p => p.Up);
            // 平均值按当前视图的时段算：日视图除以有记录的天数，周 / 月视图除以有记录的周数 / 月数
            dcount = _view == "day" ? target.Sum(p => p.Days.Count(d => !d.Missing)) : target.Count(p => p.Days.Any(d => !d.Missing));
            double avg = dcount > 0 ? (sumDn + sumUp) / dcount : 0;
            float dDn = A("hero.dn", (float)sumDn, 600, Motion.Out), dUp = A("hero.up", (float)sumUp, 600, Motion.Out), dAvg = A("hero.avg", (float)avg, 600, Motion.Out);
            int hx = x0;
            hx = HeroBlock(g, hx, hy, "下载", tk.Acc, dDn, tk.Text) + S(44);
            hx = HeroBlock(g, hx, hy, "上传", tk.Up, dUp, tk.Text) + S(44);
            string avgLabel = _lock >= 0 && _lock < n
                ? (_view == "day" ? "当日合计" : _view == "week" ? "本周合计" : "该月合计")
                : (_view == "day" ? "日均" : _view == "week" ? "周均" : "月均");
            HeroBlock(g, hx, hy, avgLabel, null, dAvg, tk.Muted);

            string ctx = _lock >= 0 && _lock < n ? $"已锁定 {ps[_lock].Full} · 点空白处取消" : _view == "day" ? "近 30 天" : _view == "week" ? $"近 {n} 周" : $"全部 {n} 个月";
            var cf = Gx.F(12);
            int ctxW = GxDraw.Measure(g, ctx, cf) + S(20);
            var ctxR = new Rectangle(x0 + cw - ctxW, hy + S(28), ctxW, S(26));
            float lockK = A("ctx.lock", _lock >= 0 ? 1 : 0, 400, Motion.Out);
            GxDraw.Round(g, ctxR, ctxR.Height / 2f, Gx.Mix(tk.Hover, Gx.Mix(tk.Win, tk.Acc, 0.15f), lockK));
            GxDraw.Text(g, ctx, cf, ctxR, Gx.Mix(tk.Text2, tk.Acc2, lockK), TextFormatFlags.HorizontalCenter);

            // ===== 柱状图卡片 =====
            var card = new Rectangle(x0, hy + S(64) + S(22), cw, S(300));
            GxDraw.Card(g, card, tk);
            var plot = new Rectangle(card.X + S(56), card.Y + S(20), card.Width - S(76), card.Height - S(54));
            double maxT = n > 0 ? ps.Max(p => p.Total) : 1;
            double step = NiceStep(maxT / 4);
            double top = step * 4;
            for (int k = 0; k <= 4; k++)
            {
                int gy = plot.Bottom - (int)(plot.Height * k / 4.0);
                using (var pen = new Pen(tk.Line2)) g.DrawLine(pen, plot.X, gy, plot.Right, gy);
                GxDraw.Text(g, k == 0 ? "0" : Gbs(step * k), Gx.F(11), new Rectangle(card.X, gy - S(8), S(46), S(16)), tk.Faint, TextFormatFlags.Right);
            }

            double mean = n > 0 ? ps.Average(p => p.Total) : 0;
            double sd = n > 0 ? Math.Sqrt(ps.Average(p => (p.Total - mean) * (p.Total - mean))) : 0;
            float slotW = n > 0 ? plot.Width / (float)n : 0;
            int every = n > 24 ? 5 : n > 12 ? 3 : 1;
            int focus = _hover >= 0 ? _hover : _lock;
            float stagger = Math.Min(24, 500f / Math.Max(1, n));
            bool anyHover = false;

            for (int i = 0; i < n; i++)
            {
                var p = ps[i];
                var slot = new Rectangle(plot.X + (int)(i * slotW), plot.Y, (int)Math.Ceiling(slotW), plot.Height);
                // 柱子从底部依次长出（.7s 缓出，逐根延迟）
                float grow = AFrom($"bar.{_barAnimKey}.{i}", 0, 1, 700, Motion.Out, i * stagger);
                float opacity = A($"bar.op.{i}", focus >= 0 && focus != i ? 0.35f : 1f, 300, Motion.Out);
                int bw = Math.Min(S(36), slot.Width - S(6));
                int bh = (int)Math.Round(plot.Height * (p.Total / Math.Max(1e-9, top)) * grow);
                var stack = new Rectangle(slot.X + (slot.Width - bw) / 2, plot.Bottom - bh, bw, bh);
                double share = p.Total > 0 ? p.Up / p.Total : 0;
                int upH = (int)Math.Round(bh * share);
                bool hot = i == _hover;
                Color cDn = hot ? Brighten(tk.Acc) : tk.Acc, cUp = hot ? Brighten(tk.Up) : tk.Up;
                cDn = Gx.Mix(tk.Card, cDn, opacity); cUp = Gx.Mix(tk.Card, cUp, opacity);
                if (bh > 0)
                {
                    using (var clip = UIUtils.RoundRect(new Rectangle(stack.X, stack.Y, stack.Width, stack.Height + S(4)), S(4)))
                    {
                        var st = g.Save();
                        g.SetClip(clip, System.Drawing.Drawing2D.CombineMode.Intersect);
                        g.SetClip(new Rectangle(stack.X, stack.Y, stack.Width, stack.Height), System.Drawing.Drawing2D.CombineMode.Intersect);
                        using (var bUp = new SolidBrush(cUp)) g.FillRectangle(bUp, stack.X, stack.Y, stack.Width, upH);
                        using (var bDn = new SolidBrush(cDn)) g.FillRectangle(bDn, stack.X, stack.Y + upH, stack.Width, bh - upH);
                        g.Restore(st);
                    }
                    if (i == _lock)
                    {
                        GxDraw.RoundStroke(g, Rectangle.Inflate(stack, S(2), S(2)), S(5), tk.Win, S(2));
                        GxDraw.RoundStroke(g, Rectangle.Inflate(stack, S(3), S(3)), S(6), tk.Text, Sf(1.5f));
                    }
                }
                if (n > 6 && p.Total > mean + 2 * sd)
                {
                    using var pk = new SolidBrush(Gx.Mix(tk.Card, tk.Warn, opacity));
                    g.FillEllipse(pk, stack.X + stack.Width / 2f - S(3), stack.Y - S(14), S(6), S(6));
                }
                if (i % every == (n - 1) % every)
                    GxDraw.Text(g, p.Label, Gx.F(11), new Rectangle(slot.X - S(20), plot.Bottom + S(8), slot.Width + S(40), S(16)), tk.Faint, TextFormatFlags.HorizontalCenter);

                int ii = i;
                if (Hot(slot)) { anyHover = true; if (_hover != i && _heatHover < 0) { _hover = i; } }
                Hit("bar." + i, slot, () => { _lock = _lock == ii ? -1 : ii; Invalidate(); });
            }
            if (!anyHover && _heatHover < 0 && _hover >= 0 && !Hot(card)) _hover = -1;
            if (!Hot(plot) && _heatHover < 0) _hover = -1;

            // 浮窗：跟随柱子弹簧滑动
            int tipIdx = focus >= 0 ? focus : n - 1;
            float tipShow = A("tip.show", (_hover >= 0 || _lock >= 0) ? 1 : 0, (_hover >= 0 || _lock >= 0) ? 400 : 200, (_hover >= 0 || _lock >= 0) ? Motion.Spring : Motion.Out);
            if (n > 0 && tipShow > 0.01f)
            {
                var tp = ps[tipIdx];
                float center = plot.X + slotW * tipIdx + slotW / 2;
                int tipW = S(_view == "week" ? 230 : 200);
                float tipX = Math.Clamp(center + (center > card.X + card.Width * 0.7f ? -tipW - S(16) : S(16)), card.X + S(8), card.Right - tipW - S(8));
                float tx = A("tip.x", tipX, 450, new Ease(.34, 1.2, .64, 1));
                DrawTip(g, tp.Full, tp.Up, tp.Down, new Rectangle((int)tx, card.Y + S(12), S(_view == "week" ? 230 : 200), S(120)), tipShow, n > 6 && tp.Total > mean + 2 * sd);
            }

            // ===== 下方：热力日历 + 前五名 =====
            int by = card.Bottom + S(20);
            var heat = new Rectangle(x0, by, cw - S(420) - S(20), S(236));
            var topCard = new Rectangle(heat.Right + S(20), by, S(420), S(236));
            DrawHeat(g, heat, ps);
            DrawTop(g, topCard, ps);

            return ClientSize.Height;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            // 点在可点击区域以外的空白处时取消锁定
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left && _lock >= 0 && Cursor == Cursors.Default) { _lock = -1; Invalidate(); }
        }

        private int HeroBlock(Graphics g, int x, int y, string label, Color? sq, float valueGb, Color valueColor)
        {
            var tk = Tk;
            int lx = x;
            if (sq.HasValue) { GxDraw.Round(g, new Rectangle(x, y + S(5), S(8), S(8)), S(2), sq.Value); lx += S(14); }
            GxDraw.Text(g, label, Gx.F(12), new Rectangle(lx, y, S(200), S(18)), tk.Muted);
            var (v, u) = Gb(valueGb);
            var fv = Gx.F(34, false, true);
            int vw = GxDraw.Measure(g, v, fv);
            GxDraw.Text(g, v, fv, new Rectangle(x, y + S(22), vw + S(4), S(40)), valueColor);
            GxDraw.Text(g, u, Gx.F(13), new Rectangle(x + vw + S(4), y + S(36), S(40), S(22)), tk.Faint);
            return x + vw + S(4) + GxDraw.Measure(g, u, Gx.F(13));
        }

        private void DrawTip(Graphics g, string title, double up, double down, Rectangle r, float show, bool peak, string totalLabel = "合计")
        {
            var tk = Tk;
            var st = g.Save();
            float s = 0.97f + 0.03f * show, dy = (1 - show) * S(6);
            g.TranslateTransform(r.X + r.Width / 2f, r.Y + dy); g.ScaleTransform(s, s); g.TranslateTransform(-(r.X + r.Width / 2f), -r.Y);
            int a = (int)(255 * Math.Clamp(show, 0, 1));
            GxDraw.Shadow(g, r, S(12), Color.FromArgb(a / 2, tk.Shadow));
            GxDraw.Round(g, r, S(12), Color.FromArgb(Math.Min(a, 250), tk.Float));
            GxDraw.RoundStroke(g, r, S(12), Color.FromArgb(a, tk.Line));
            Color F(Color c) => Color.FromArgb(a, c);
            int x = r.X + S(14), y = r.Y + S(12), w = r.Width - S(28);
            GxDraw.Text(g, title, Gx.F(13, true), new Rectangle(x, y, w, S(18)), F(tk.Text));
            if (peak)
            {
                int pw = GxDraw.Measure(g, title, Gx.F(13, true));
                var chip = new Rectangle(x + pw + S(8), y + S(1), S(58), S(16));
                GxDraw.Round(g, chip, S(5), Color.FromArgb(a, Gx.Mix(tk.Float, tk.Warn, 0.16f)));
                GxDraw.Text(g, "异常偏高", Gx.F(10), chip, F(tk.Warn), TextFormatFlags.HorizontalCenter);
            }
            void Row(int yy, Color? sq, string label, string val, Color lc)
            {
                int lx = x;
                if (sq.HasValue) { GxDraw.Round(g, new Rectangle(x, yy + S(5), S(8), S(8)), S(2), F(sq.Value)); lx += S(16); }
                GxDraw.Text(g, label, Gx.F(12), new Rectangle(lx, yy, S(80), S(18)), F(lc));
                GxDraw.Text(g, val, Gx.F(12, false, true), new Rectangle(x, yy, w, S(18)), F(tk.Text), TextFormatFlags.Right);
            }
            Row(y + S(26), tk.Acc, "下载", Gbs(down), tk.Text2);
            Row(y + S(50), tk.Up, "上传", Gbs(up), tk.Text2);
            using (var pen = new Pen(F(tk.Line))) g.DrawLine(pen, x, y + S(76), x + w, y + S(76));
            Row(y + S(82), null, totalLabel, Gbs(up + down), tk.Muted);
            g.Restore(st);
        }

        private void DrawHeat(Graphics g, Rectangle card, List<Period> ps)
        {
            var tk = Tk;
            GxDraw.Card(g, card, tk);
            GxDraw.Text(g, "每日下载热力", Gx.F(13, true), new Rectangle(card.X + S(20), card.Y + S(18), S(200), S(18)), tk.Text);
            var hd = _heatHover >= 0 && _heatHover < _days.Count ? _days[_heatHover] : null;
            string info = hd == null ? "悬停任意一天查看" : $"{hd.Date.Month}月{hd.Date.Day}日 · " + (hd.Missing ? "无记录" : $"↓ {Gbs(hd.Down)}  ↑ {Gbs(hd.Up)}");
            GxDraw.Text(g, info, Gx.F(12, false, true), new Rectangle(card.X + S(220), card.Y + S(18), card.Width - S(240), S(18)), tk.Muted, TextFormatFlags.Right);

            int lx = card.X + S(20), ly = card.Y + S(60);
            string[] wd = { "一", "", "三", "", "五", "", "日" };
            for (int i = 0; i < 7; i++) if (wd[i] != "") GxDraw.Text(g, wd[i], Gx.F(10), new Rectangle(lx, ly + i * S(20) - S(2), S(16), S(20)), tk.Faint);

            int gx = card.X + S(44), gy = card.Y + S(58);
            // 只显示最近能放下的若干周
            int maxCols = Math.Max(1, (card.Width - S(64)) / S(20));
            int offset = _days.Count > 0 ? ((int)_days[0].Date.DayOfWeek + 6) % 7 : 0;
            int totalCols = (_days.Count + offset + 6) / 7;
            int firstCol = Math.Max(0, totalCols - maxCols);
            bool anyHot = false;
            foreach (var d in _days)
            {
                int col = (d.Index + offset) / 7 - firstCol, row = (d.Index + offset) % 7;
                if (col < 0) continue;
                var r = new Rectangle(gx + col * S(20), gy + row * S(20), S(16), S(16));
                Color c = tk.Track;
                if (!d.Missing)
                {
                    if (d.Down > _heatQ[3]) c = tk.Acc;
                    else if (d.Down > _heatQ[2]) c = Gx.Mix(tk.Card, tk.Acc, 0.7f);
                    else if (d.Down > _heatQ[1]) c = Gx.Mix(tk.Card, tk.Acc, 0.45f);
                    else if (d.Down > _heatQ[0]) c = Gx.Mix(tk.Card, tk.Acc, 0.25f);
                }
                bool hot = Hot(r);
                float sc = A("heat." + d.Index, hot || _heatHover == d.Index ? 1.3f : 1f, 300, Motion.Spring);
                var rr = new RectangleF(r.X + r.Width / 2f - r.Width * sc / 2, r.Y + r.Height / 2f - r.Height * sc / 2, r.Width * sc, r.Height * sc);
                if (d.Missing)
                {
                    using var pen = new Pen(tk.Ghost) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
                    using var path = GxDraw.RoundF(rr, S(4));
                    g.DrawPath(pen, path);
                }
                else
                {
                    using var path = GxDraw.RoundF(rr, S(4) * sc);
                    using var b = new SolidBrush(c);
                    g.FillPath(b, path);
                }
                if (sc > 1.02f)
                {
                    using var path = GxDraw.RoundF(RectangleF.Inflate(rr, 1, 1), S(5));
                    using var pen = new Pen(tk.Text, S(2));
                    g.DrawPath(pen, path);
                }
                if (hot)
                {
                    anyHot = true;
                    if (_heatHover != d.Index) { _heatHover = d.Index; _hover = BarIndexOf(d, ps); }
                }
                var dd = d;
                Hit("heat." + d.Index, r, () => { int bi = BarIndexOf(dd, ps); if (bi >= 0) { _lock = _lock == bi ? -1 : bi; Invalidate(); } });
            }
            if (!anyHot && _heatHover >= 0 && !TopHot) { _heatHover = -1; _hover = -1; }

            // 图例
            int ex = card.Right - S(20) - S(150), ey = card.Bottom - S(28);
            GxDraw.Text(g, "少", Gx.F(10), new Rectangle(ex, ey, S(16), S(14)), tk.Faint);
            Color[] legend = { tk.Track, Gx.Mix(tk.Card, tk.Acc, 0.25f), Gx.Mix(tk.Card, tk.Acc, 0.45f), Gx.Mix(tk.Card, tk.Acc, 0.7f), tk.Acc };
            for (int i = 0; i < legend.Length; i++) GxDraw.Round(g, new Rectangle(ex + S(18) + i * S(14), ey + S(2), S(10), S(10)), S(2), legend[i]);
            GxDraw.Text(g, "多", Gx.F(10), new Rectangle(ex + S(18) + legend.Length * S(14) + S(2), ey, S(16), S(14)), tk.Faint);
        }

        private bool TopHot;

        private int BarIndexOf(Day d, List<Period> ps)
        {
            for (int i = 0; i < ps.Count; i++) if (ps[i].Days.Any(x => x.Index == d.Index)) return i;
            return -1;
        }

        private void DrawTop(Graphics g, Rectangle card, List<Period> ps)
        {
            var tk = Tk;
            GxDraw.Card(g, card, tk);
            GxDraw.Text(g, $"流量最高的 5 {(_view == "day" ? "天" : _view == "week" ? "周" : "个月")}", Gx.F(13, true), new Rectangle(card.X + S(20), card.Y + S(18), S(300), S(18)), tk.Text);
            // 日视图排的是每一天；周 / 月视图排的是每周 / 每月的合计
            var source = _view == "day"
                ? _days.Where(d => !d.Missing).Select(d => (Label: $"{d.Date.Month}月{d.Date.Day}日", d.Total, Key: d.Index, Bar: -2)).ToList()
                : ps.Select((pp, i) => (Label: _view == "week" ? pp.Label + " 起" : pp.Full, pp.Total, Key: -1 - i, Bar: i)).Where(t => t.Total > 0).ToList();
            var ranked = source.OrderByDescending(t => t.Total).Take(5).ToList();
            double topMax = ranked.Count > 0 ? Math.Max(1e-9, ranked[0].Total) : 1;
            int y = card.Y + S(46);
            TopHot = false;
            for (int k = 0; k < ranked.Count; k++)
            {
                var d = ranked[k];
                int barIdx = d.Bar >= -1 ? d.Bar : BarIndexOf(_days[d.Key], ps);
                var row = new Rectangle(card.X + S(14), y, card.Width - S(28), S(34));
                bool hot = Hot(row) || (d.Key >= 0 ? _heatHover == d.Key : _hover == barIdx);
                if (Hot(row))
                {
                    TopHot = true;
                    if (d.Key >= 0) { if (_heatHover != d.Key) { _heatHover = d.Key; _hover = barIdx; } }
                    else _hover = barIdx;
                }
                float rise = AFrom($"top.rise.{_barAnimKey}.{k}", 0, 1, 500, Motion.Out, k * 50);
                float shift = A("top.shift." + k, hot ? 1 : 0, 350, Motion.Out);
                var r = row; r.Offset((int)(S(3) * shift), (int)((1 - rise) * S(8)));
                if (shift > 0.01f) GxDraw.Round(g, r, S(10), Gx.Mix(tk.Card, tk.Hover, shift));
                Color Fade(Color c) => Gx.Mix(tk.Card, c, rise);
                GxDraw.Text(g, (k + 1).ToString(), Gx.F(12, false, true), new Rectangle(r.X + S(10), r.Y, S(14), r.Height), Fade(tk.Faint));
                int labelW = _view == "month" ? S(96) : S(76);
                GxDraw.Text(g, d.Label, Gx.F(13), new Rectangle(r.X + S(36), r.Y, labelW, r.Height), Fade(tk.Text));
                int bx = r.X + S(48) + labelW;
                var bar = new Rectangle(bx, r.Y + r.Height / 2 - S(2), r.Right - S(100) - bx, S(4));
                int bw = (int)(bar.Width * (d.Total / topMax) * rise);
                GxDraw.Round(g, new Rectangle(bar.X, bar.Y, bw, bar.Height), S(2), Fade(tk.Acc));
                GxDraw.Text(g, Gbs(d.Total), Gx.F(13, false, true), new Rectangle(r.Right - S(90), r.Y, S(80), r.Height), Fade(tk.Text), TextFormatFlags.Right);
                int bi = barIdx;
                Hit("top." + k, row, () => { if (bi >= 0) { _lock = _lock == bi ? -1 : bi; Invalidate(); } });
                y += S(36);
            }
        }

        private int _cumHover = -1;

        /// <summary>
        /// 累计视图：自开始记录以来的总量；主图是逐日累计曲线（从左到右描出，900ms），
        /// 悬停出十字线与弹簧浮窗；下方左侧沿用热力日历，右侧是每月合计。
        /// </summary>
        private int DrawTotalView(Graphics g, int x0, int y0, int cw)
        {
            var tk = Tk;
            int hy = y0 + S(74);
            double allDn = _days.Sum(d => d.Down), allUp = _days.Sum(d => d.Up);
            int recDays = _days.Count(d => !d.Missing);
            float dDn = A("hero.dn", (float)allDn, 600, Motion.Out), dUp = A("hero.up", (float)allUp, 600, Motion.Out), dAll = A("hero.avg", (float)(allDn + allUp), 600, Motion.Out);
            int hx = x0;
            hx = HeroBlock(g, hx, hy, "累计下载", tk.Acc, dDn, tk.Text) + S(44);
            hx = HeroBlock(g, hx, hy, "累计上传", tk.Up, dUp, tk.Text) + S(44);
            HeroBlock(g, hx, hy, "累计合计", null, dAll, tk.Muted);

            string ctx = _days.Count == 0 ? "暂无记录" : $"自 {_days[0].Date:yyyy年M月d日} 起 · {recDays} 天有记录";
            var cf = Gx.F(12);
            int ctxW = GxDraw.Measure(g, ctx, cf) + S(20);
            var ctxR = new Rectangle(x0 + cw - ctxW, hy + S(28), ctxW, S(26));
            GxDraw.Round(g, ctxR, ctxR.Height / 2f, tk.Hover);
            GxDraw.Text(g, ctx, cf, ctxR, tk.Text2, TextFormatFlags.HorizontalCenter);

            var card = new Rectangle(x0, hy + S(64) + S(22), cw, S(300));
            GxDraw.Card(g, card, tk);
            GxDraw.Text(g, "累计曲线", Gx.F(12), new Rectangle(card.X + S(20), card.Y + S(14), S(200), S(16)), tk.Muted);
            var plot = new Rectangle(card.X + S(56), card.Y + S(40), card.Width - S(76), card.Height - S(74));
            int n = _days.Count;
            var cDn = new double[n]; var cUp = new double[n];
            for (int i = 0; i < n; i++) { cDn[i] = (i > 0 ? cDn[i - 1] : 0) + _days[i].Down; cUp[i] = (i > 0 ? cUp[i - 1] : 0) + _days[i].Up; }
            double maxV = n > 0 ? Math.Max(cDn[^1], cUp[^1]) : 1;
            // 刻度贴着数据取整，避免顶部空出一半
            double step = NiceStep(maxV / 4);
            int ticks = Math.Max(1, (int)Math.Ceiling(maxV / step));
            double top = step * ticks;
            for (int k = 0; k <= ticks; k++)
            {
                int gy = plot.Bottom - (int)(plot.Height * k / (double)ticks);
                using (var pen = new Pen(tk.Line2)) g.DrawLine(pen, plot.X, gy, plot.Right, gy);
                GxDraw.Text(g, k == 0 ? "0" : Gbs(step * k), Gx.F(11), new Rectangle(card.X, gy - S(8), S(46), S(16)), tk.Faint, TextFormatFlags.Right);
            }
            if (n >= 2)
            {
                PointF P(int i, double v) => new(plot.X + plot.Width * i / (float)(n - 1), plot.Bottom - (float)(plot.Height * v / Math.Max(1e-9, top)));
                var pDn = Enumerable.Range(0, n).Select(i => P(i, cDn[i])).ToArray();
                var pUp = Enumerable.Range(0, n).Select(i => P(i, cUp[i])).ToArray();
                // 从左到右描出
                float rev = AFrom($"cum.rev.{_barAnimKey}", 0, 1, 900, Motion.InOut);
                var st = g.Save();
                g.SetClip(new RectangleF(plot.X - S(4), card.Y, (plot.Width + S(8)) * rev, card.Height));
                using (var path = new System.Drawing.Drawing2D.GraphicsPath())
                {
                    path.AddLines(pDn);
                    path.AddLine(pDn[^1].X, plot.Bottom, pDn[0].X, plot.Bottom);
                    path.CloseFigure();
                    using var fill = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(plot.X, plot.Y - 1, plot.Width, plot.Height + 2),
                        Color.FromArgb(70, tk.Acc), Color.FromArgb(0, tk.Acc), 90f);
                    g.FillPath(fill, path);
                }
                using (var pen = new Pen(tk.Acc, Sf(2f)) { LineJoin = System.Drawing.Drawing2D.LineJoin.Round }) g.DrawLines(pen, pDn);
                using (var pen = new Pen(tk.Up, Sf(2f)) { LineJoin = System.Drawing.Drawing2D.LineJoin.Round }) g.DrawLines(pen, pUp);
                g.Restore(st);

                // 横轴：首尾加中间三个日期
                for (int k = 0; k <= 4; k++)
                {
                    int i = (int)Math.Round((n - 1) * k / 4.0);
                    var d = _days[i].Date;
                    var flags = k == 0 ? TextFormatFlags.Left : k == 4 ? TextFormatFlags.Right : TextFormatFlags.HorizontalCenter;
                    int lx = k == 0 ? (int)pDn[i].X : k == 4 ? (int)pDn[i].X - S(80) : (int)pDn[i].X - S(40);
                    GxDraw.Text(g, $"{d.Month}/{d.Day}", Gx.F(11, false, true), new Rectangle(lx, plot.Bottom + S(8), S(80), S(16)), tk.Faint, flags);
                }

                // 悬停：十字线 + 两个点 + 浮窗
                if (Hot(plot))
                {
                    var m = ContentMouse;
                    _cumHover = Math.Clamp((int)Math.Round((m.X - plot.X) / (float)plot.Width * (n - 1)), 0, n - 1);
                }
                else _cumHover = -1;
                float show = A("cum.tip", _cumHover >= 0 ? 1 : 0, _cumHover >= 0 ? 400 : 200, _cumHover >= 0 ? Motion.Spring : Motion.Out);
                int hi = _cumHover >= 0 ? _cumHover : _lastCumHover;
                if (_cumHover >= 0) _lastCumHover = _cumHover;
                if (hi >= 0 && hi < n && show > 0.01f)
                {
                    float cx = A("cum.x", pDn[hi].X, 300, new Ease(.34, 1.2, .64, 1));
                    using (var pen = new Pen(Color.FromArgb((int)(140 * Math.Clamp(show, 0, 1)), tk.Muted)) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash })
                        g.DrawLine(pen, cx, plot.Y, cx, plot.Bottom);
                    foreach (var (pt, c) in new[] { (pDn[hi], tk.Acc), (pUp[hi], tk.Up) })
                    {
                        float r = S(4) * Math.Clamp(show, 0, 1.2f);
                        using var b = new SolidBrush(c);
                        using var ring = new Pen(tk.Card, Sf(2f));
                        g.FillEllipse(b, cx - r, pt.Y - r, r * 2, r * 2);
                        g.DrawEllipse(ring, cx - r, pt.Y - r, r * 2, r * 2);
                    }
                    var d = _days[hi].Date;
                    float tipX = Math.Clamp(cx + (cx > card.X + card.Width * 0.7f ? -S(216) : S(16)), card.X + S(8), card.Right - S(208));
                    DrawTip(g, $"截至 {d.Month}月{d.Day}日", cUp[hi], cDn[hi], new Rectangle((int)tipX, card.Y + S(12), S(200), S(120)), show, false, "累计");
                }
            }
            else
            {
                GxDraw.Text(g, "记录满两天后显示累计曲线", Gx.F(13), plot, tk.Faint, TextFormatFlags.HorizontalCenter);
            }

            int by = card.Bottom + S(20);
            var heat = new Rectangle(x0, by, cw - S(420) - S(20), S(236));
            var right = new Rectangle(heat.Right + S(20), by, S(420), S(236));
            DrawHeat(g, heat, new List<Period>());
            DrawMonths(g, right);
            return ClientSize.Height;
        }

        private int _lastCumHover = -1;

        /// <summary>累计视图右下：最近 5 个月的月合计（下载 / 上传分段条）。</summary>
        private void DrawMonths(Graphics g, Rectangle card)
        {
            var tk = Tk;
            GxDraw.Card(g, card, tk);
            GxDraw.Text(g, "每月合计", Gx.F(13, true), new Rectangle(card.X + S(20), card.Y + S(18), S(300), S(18)), tk.Text);
            var months = _days.GroupBy(d => new { d.Date.Year, d.Date.Month })
                .Select(gp => (Label: $"{gp.Key.Year}年{gp.Key.Month}月", Up: gp.Sum(d => d.Up), Down: gp.Sum(d => d.Down)))
                .TakeLast(5).Reverse().ToList();
            double mx = months.Count > 0 ? Math.Max(1e-9, months.Max(m => m.Up + m.Down)) : 1;
            int y = card.Y + S(46);
            for (int k = 0; k < months.Count; k++)
            {
                var m = months[k];
                var row = new Rectangle(card.X + S(14), y, card.Width - S(28), S(34));
                bool hot = Hot(row);
                float rise = AFrom($"mon.rise.{_barAnimKey}.{k}", 0, 1, 500, Motion.Out, k * 50);
                float shift = A("mon.shift." + k, hot ? 1 : 0, 350, Motion.Out);
                var r = row; r.Offset((int)(S(3) * shift), (int)((1 - rise) * S(8)));
                if (shift > 0.01f) GxDraw.Round(g, r, S(10), Gx.Mix(tk.Card, tk.Hover, shift));
                Color Fade(Color c) => Gx.Mix(tk.Card, c, rise);
                GxDraw.Text(g, m.Label, Gx.F(13), new Rectangle(r.X + S(10), r.Y, S(100), r.Height), Fade(tk.Text));
                int bx = r.X + S(118);
                var bar = new Rectangle(bx, r.Y + r.Height / 2 - S(2), r.Right - S(100) - bx, S(4));
                int bwAll = (int)(bar.Width * ((m.Up + m.Down) / mx) * rise);
                int bwUp = (int)(bwAll * (m.Up + m.Down > 0 ? m.Up / (m.Up + m.Down) : 0));
                GxDraw.Round(g, new Rectangle(bar.X, bar.Y, bwAll, bar.Height), S(2), Fade(tk.Acc));
                if (bwUp > 0) GxDraw.Round(g, new Rectangle(bar.X + bwAll - bwUp, bar.Y, bwUp, bar.Height), S(2), Fade(tk.Up));
                GxDraw.Text(g, hot ? $"↓{Gbs(m.Down)} ↑{Gbs(m.Up)}" : Gbs(m.Up + m.Down), Gx.F(hot ? 11 : 13, false, true),
                    new Rectangle(r.Right - S(170), r.Y, S(160), r.Height), Fade(tk.Text), TextFormatFlags.Right);
                y += S(36);
            }
        }

        private static Color Brighten(Color c) =>
            Color.FromArgb(Math.Min(255, (int)(c.R * 1.18)), Math.Min(255, (int)(c.G * 1.18)), Math.Min(255, (int)(c.B * 1.18)));

        private static double NiceStep(double raw)
        {
            if (raw <= 0) return 1;
            double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            foreach (var n in new[] { 1, 2, 2.5, 5, 10 }) if (n * mag >= raw) return n * mag;
            return 10 * mag;
        }
    }
}
