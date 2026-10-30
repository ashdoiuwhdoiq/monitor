using LiteMonitor.src.Core;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace LiteMonitor.src.UI.Graphite
{
    /// <summary>硬件趋势窗口（设计稿 Trends.dc.html，1280×800）。</summary>
    public sealed class TrendsWindow : GraphiteWindow
    {
        public TrendsWindow(Settings cfg) : base(new TrendsCanvas(cfg), "硬件趋势", 1280, 800) { }
    }

    internal sealed class TrendsCanvas : GraphiteCanvas
    {
        private sealed record Series(string Key, string Label, string Unit, int ColorIndex, char Chart);

        // 下方图表的类别：设计稿为功耗；旧版的频率、帧率也保留，在标题处切换
        private static readonly (string id, string title, string unit, Series[] series)[] Lower =
        {
            ("power", "功耗", "W", new[] { new Series("CPU.Power", "CPU 功耗", "W", 5, 'B'), new Series("GPU.Power", "GPU 功耗", "W", 6, 'B') }),
            ("clock", "频率", "MHz", new[] { new Series("CPU.Clock", "CPU 频率", "MHz", 5, 'B'), new Series("GPU.Clock", "GPU 频率", "MHz", 6, 'B') }),
            ("fps", "帧率", "FPS", new[] { new Series("FPS", "帧率", "FPS", 5, 'B') }),
        };

        private static readonly Series[] Upper =
        {
            new("CPU.Load", "CPU 负载", "%", 0, 'A'), new("CPU.Temp", "CPU 温度", "°C", 1, 'A'),
            new("GPU.Load", "GPU 负载", "%", 2, 'A'), new("GPU.Temp", "GPU 温度", "°C", 3, 'A'),
            new("MEM.Load", "内存", "%", 4, 'A'),
        };

        private readonly Settings _cfg;
        private string _range = "h1";
        private int _lower;
        private readonly HashSet<string> _hidden = new();
        private string? _hl;
        private int _idx = -1;
        private int _revealKey;
        private List<HardwareHistoryPoint> _points = new();
        private DateTime _end = DateTime.Now;
        private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 10000 };

        public TrendsCanvas(Settings cfg)
        {
            _cfg = cfg;
            Reload();
            _refresh.Tick += (_, __) => { Reload(); Invalidate(); };
            _refresh.Start();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _refresh.Dispose();
            base.Dispose(disposing);
        }

        private void Reload()
        {
            _points = HardwareHistoryLogger.GetPointsSnapshot();
            _end = DateTime.Now;
        }

        private (int n, TimeSpan step) RangeSpec() => _range switch
        {
            "d1" => (96, TimeSpan.FromMinutes(15)),
            "d7" => (84, TimeSpan.FromHours(2)),
            _ => (60, TimeSpan.FromMinutes(1)),
        };

        private DateTime TimeAt(int i, int n, TimeSpan step) => _end - TimeSpan.FromTicks(step.Ticks * (n - 1 - i));

        /// <summary>按当前范围把分钟点分桶：每桶取平均值的平均与最大值的最大。</summary>
        private (float?[] avg, float max) Bucket(string key)
        {
            var (n, step) = RangeSpec();
            var avg = new float?[n];
            var sum = new double[n];
            var cnt = new int[n];
            float max = float.MinValue;
            DateTime start = _end - TimeSpan.FromTicks(step.Ticks * n);
            foreach (var p in _points)
            {
                if (p.Time <= start || p.Time > _end) continue;
                if (!p.Values.TryGetValue(key, out var v)) continue;
                int i = (int)((p.Time - start).Ticks / step.Ticks);
                if (i < 0 || i >= n) continue;
                sum[i] += v.Avg; cnt[i]++;
                max = Math.Max(max, v.Max);
            }
            for (int i = 0; i < n; i++) avg[i] = cnt[i] > 0 ? (float)(sum[i] / cnt[i]) : null;
            return (avg, max == float.MinValue ? 0 : max);
        }

        protected override int Draw(Graphics g)
        {
            var tk = Tk;
            using (var bg = new SolidBrush(tk.Win)) g.FillRectangle(bg, ClientRectangle);
            int x0 = S(36), y0 = S(28), cw = ClientSize.Width - S(72);

            // ===== 标题行 =====
            GxDraw.Text(g, "硬件趋势", Gx.F(22, true), new Rectangle(x0, y0, S(300), S(30)), tk.Text);
            GxDraw.Text(g, $"每 10 秒采样，保留最近 7 天 · 截至 {_end:M月d日 HH:mm}", Gx.F(12), new Rectangle(x0, y0 + S(34), S(500), S(18)), tk.Faint);
            var modeR = new Rectangle(x0 + cw - S(32), y0 + S(8), S(32), S(32));
            ModeButton(g, modeR);
            var segR = new Rectangle(modeR.X - S(12) - S(72 * 3 + 6), y0 + S(10), S(72 * 3 + 6), S(34));
            string[] ranges = { "h1", "d1", "d7" };
            Seg(g, "range", segR, new[] { "1 小时", "24 小时", "7 天" }, Array.IndexOf(ranges, _range), i =>
            {
                _range = ranges[i]; _idx = -1; _revealKey++; Reload();
            });
            // 导出 CSV（旧版功能）
            var expR = new Rectangle(segR.X - S(12) - S(32), y0 + S(10), S(32), S(34));
            bool expHot = Hot(expR);
            if (expHot) GxDraw.Round(g, expR, S(9), tk.Hover);
            GxDraw.Icon(g, "download", new RectangleF(expR.X + S(7), expR.Y + S(8), S(18), S(18)), expHot ? tk.Text : tk.Muted);
            Hit("export", expR, ExportCsv);

            // ===== 统计卡片（数值补间）=====
            var upper = Upper.ToDictionary(s => s.Key, s => Bucket(s.Key));
            var lowerDef = Lower[_lower];
            var lower = lowerDef.series.ToDictionary(s => s.Key, s => Bucket(s.Key));
            var memVals = upper["MEM.Load"].avg.Where(v => v.HasValue).Select(v => v!.Value).ToList();
            var stats = new (string label, float val, string unit, Color c)[]
            {
                ("CPU 温度峰值", upper["CPU.Temp"].max, "°C", tk.Series[1]),
                ("GPU 温度峰值", upper["GPU.Temp"].max, "°C", tk.Series[3]),
                ("CPU 功耗峰值", Bucket("CPU.Power").max, "W", tk.Series[5]),
                ("GPU 功耗峰值", Bucket("GPU.Power").max, "W", tk.Series[6]),
                ("内存平均占用", memVals.Count > 0 ? memVals.Average() : 0, "%", tk.Series[4]),
            };
            int sy = y0 + S(74), gap = S(14);
            int sw = (cw - gap * 4) / 5;
            for (int i = 0; i < stats.Length; i++)
            {
                var r = new Rectangle(x0 + i * (sw + gap), sy, sw, S(78));
                float rise = AFrom($"stat.rise.{i}", 0, 1, 500, Motion.Out, i * 50);
                r.Offset(0, (int)((1 - rise) * S(8)));
                GxDraw.Round(g, r, S(14), Gx.Mix(tk.Win, tk.Card, rise));
                GxDraw.RoundStroke(g, r, S(14), Gx.Mix(tk.Win, tk.Line2, rise));
                Color F(Color c) => Gx.Mix(tk.Win, c, rise);
                using (var dot = new SolidBrush(F(stats[i].c))) g.FillEllipse(dot, r.X + S(16), r.Y + S(19), S(8), S(8));
                GxDraw.Text(g, stats[i].label, Gx.F(12), new Rectangle(r.X + S(30), r.Y + S(14), r.Width - S(40), S(18)), F(tk.Muted));
                float v = A($"stat.v.{i}", stats[i].val, 600, Motion.Out);
                string vs = v.ToString("0");
                var fv = Gx.F(26, false, true);
                int vw = GxDraw.Measure(g, vs, fv);
                GxDraw.Text(g, vs, fv, new Rectangle(r.X + S(16), r.Y + S(38), vw + S(4), S(32)), F(tk.Text));
                GxDraw.Text(g, stats[i].unit, Gx.F(12), new Rectangle(r.X + S(16) + vw + S(4), r.Y + S(48), S(40), S(18)), F(tk.Faint));
            }

            // ===== 图例：悬停突出、单击显示 / 隐藏 =====
            var all = Upper.Concat(lowerDef.series).ToList();
            int lx = x0, ly = sy + S(78) + S(18);
            string? hoverLegend = null;
            foreach (var s in all)
            {
                var f = Gx.F(12);
                int w = GxDraw.Measure(g, s.Label, f) + S(12 + 8 + 7 + 12);
                var r = new Rectangle(lx, ly, w, S(30));
                bool off = _hidden.Contains(s.Key), hot = Hot(r);
                if (hot) hoverLegend = s.Key;
                float press = A("chip.p." + s.Key, IsPressed("chip." + s.Key) ? 1 : 0, IsPressed("chip." + s.Key) ? 100 : 300, Motion.Spring);
                float offK = A("chip.off." + s.Key, off ? 1 : 0, 300, Motion.Out);
                var st = g.Save();
                float sc = 1 - 0.06f * press, cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
                g.TranslateTransform(cx, cy); g.ScaleTransform(sc, sc); g.TranslateTransform(-cx, -cy);
                Color bgc = hot ? tk.Hover : tk.Card;
                float vis = 1 - 0.58f * offK;
                GxDraw.Round(g, r, r.Height / 2f, Gx.Mix(tk.Win, bgc, vis));
                GxDraw.RoundStroke(g, r, r.Height / 2f, Gx.Mix(tk.Win, hot ? tk.Ghost : tk.Line, vis));
                float dr = S(4) * (1 - 0.5f * offK);
                using (var dot = new SolidBrush(Gx.Mix(tk.Win, tk.Series[s.ColorIndex], vis))) g.FillEllipse(dot, r.X + S(16) - dr, cy - dr, dr * 2, dr * 2);
                GxDraw.Text(g, s.Label, f, new Rectangle(r.X + S(27), r.Y, w - S(27), r.Height), Gx.Mix(tk.Win, tk.Text2, vis));
                g.Restore(st);
                var key = s.Key;
                Hit("chip." + key, r, () => { if (!_hidden.Remove(key)) _hidden.Add(key); });
                lx += w + S(8);
            }
            _hl = hoverLegend != null && !_hidden.Contains(hoverLegend) ? hoverLegend : null;

            // ===== 图表卡片 =====
            var card = new Rectangle(x0, ly + S(30) + S(14), cw, S(470));
            GxDraw.Card(g, card, tk);
            GxDraw.Text(g, "负载与温度 · % / °C", Gx.F(12), new Rectangle(card.X + S(16), card.Y + S(12), S(300), S(18)), tk.Muted);
            // 下方图表标题即类别切换
            int tx = card.X + S(16);
            for (int i = 0; i < Lower.Length; i++)
            {
                string t = $"{Lower[i].title} · {Lower[i].unit}";
                var tf = Gx.F(12);
                int w = GxDraw.Measure(g, t, tf);
                var tr = new Rectangle(tx, card.Y + S(290), w, S(18));
                float act = A("lower." + i, i == _lower ? 1 : 0, 300, Motion.Out);
                GxDraw.Text(g, t, tf, tr, Gx.Mix(Hot(tr) ? tk.Text2 : tk.Ghost, tk.Muted, act));
                if (act > 0.02f) GxDraw.Round(g, new Rectangle(tr.X, tr.Bottom + S(1), (int)(w * act), S(2)), S(1), Color.FromArgb((int)(255 * act), tk.Acc));
                int ii = i;
                Hit("lower." + i, tr, () => { if (_lower != ii) { _lower = ii; _revealKey++; } });
                tx += w + S(18);
            }

            int W = card.Width - S(48) - S(16);
            var plotA = new Rectangle(card.X + S(48), card.Y + S(36), W, S(240));
            var plotB = new Rectangle(card.X + S(48), card.Y + S(320), W, S(110));
            var (n, step) = RangeSpec();
            float XOf(int i) => plotA.X + (n <= 1 ? 0 : i / (float)(n - 1) * W);

            // 关机时段：用 CPU 负载缺数据的连续区间画浅色带
            var presence = upper["CPU.Load"].avg;
            int gs = -1;
            for (int i = 0; i <= n; i++)
            {
                bool missing = i < n && presence[i] == null;
                if (missing && gs < 0) gs = i;
                if (!missing && gs >= 0)
                {
                    float xa = XOf(Math.Max(0, gs) ) - (gs > 0 ? W / (2f * (n - 1)) : 0);
                    float xb = XOf(i - 1) + (i - 1 < n - 1 ? W / (2f * (n - 1)) : 0);
                    using var band = new SolidBrush(Gx.Mix(tk.Card, tk.Text, 0.04f));
                    g.FillRectangle(band, xa, plotA.Y, xb - xa, plotA.Height);
                    g.FillRectangle(band, xa, plotB.Y, xb - xa, plotB.Height);
                    if (xb - xa > S(60)) GxDraw.Text(g, "未运行", Gx.F(11), new Rectangle((int)xa, card.Y + S(150), (int)(xb - xa), S(16)), tk.Ghost, TextFormatFlags.HorizontalCenter);
                    gs = -1;
                }
            }

            // 网格与纵轴
            float maxB = NiceMax(lower.Values.Max(v => v.max), lowerDef.id);
            for (int k = 0; k <= 4; k++)
            {
                float y = plotA.Bottom - plotA.Height * k / 4f;
                using (var pen = new Pen(tk.Line2)) g.DrawLine(pen, plotA.X, y, plotA.Right, y);
                GxDraw.Text(g, (k * 25).ToString(), Gx.F(11, false, true), new Rectangle(card.X, (int)y - S(8), S(40), S(16)), tk.Faint, TextFormatFlags.Right);
            }
            for (int k = 0; k <= 2; k++)
            {
                float y = plotB.Bottom - plotB.Height * k / 2f;
                using (var pen = new Pen(tk.Line2)) g.DrawLine(pen, plotB.X, y, plotB.Right, y);
                GxDraw.Text(g, (maxB * k / 2).ToString("0"), Gx.F(11, false, true), new Rectangle(card.X, (int)y - S(8), S(40), S(16)), tk.Faint, TextFormatFlags.Right);
            }

            // 曲线：切换范围 / 类别时从左往右重新画出（.9s 缓出）
            float reveal = AFrom("reveal." + _revealKey, 0, 1, 900, Motion.Out);
            var clipSt = g.Save();
            g.SetClip(new RectangleF(plotA.X - S(2), card.Y, (W + S(4)) * reveal, card.Height), CombineMode.Intersect);
            foreach (var s in Upper) DrawSeries(g, s, upper[s.Key].avg, plotA, 0, 100, XOf);
            foreach (var s in lowerDef.series) DrawSeries(g, s, lower[s.Key].avg, plotB, 0, maxB, XOf);
            g.Restore(clipSt);

            // 横轴刻度
            foreach (float f in new[] { 0f, 0.25f, 0.5f, 0.75f, 1f })
            {
                int i = (int)Math.Round(f * (n - 1));
                var t = TimeAt(i, n, step);
                string label = f == 1 ? "现在" : _range == "d7" ? $"{t.Month}/{t.Day}" : t.ToString("HH:mm");
                GxDraw.Text(g, label, Gx.F(11, false, true), new Rectangle((int)XOf(i) - S(40), card.Y + S(440), S(80), S(16)), tk.Faint, TextFormatFlags.HorizontalCenter);
            }

            // ===== 十字准线（吸附到最近采样）+ 圆点 + 浮窗 =====
            var hoverArea = new Rectangle(plotA.X, plotA.Y, W, plotB.Bottom - plotA.Y);
            HoverArea("plot", hoverArea, Cursors.Cross);
            if (Hot(hoverArea) && n > 1)
                _idx = (int)Math.Round(Math.Clamp((ContentMouse.X - plotA.X) / (float)W, 0, 1) * (n - 1));
            else _idx = -1;
            float show = A("cross.show", _idx >= 0 ? 1 : 0, 200, Motion.Out);
            if (show > 0.01f)
            {
                int idx = _idx >= 0 ? _idx : (int)Peek("cross.last", 0);
                if (_idx >= 0) A("cross.last", _idx, 0, Motion.Out);
                float cxp = A("cross.x", XOf(idx), 120, Motion.Out);
                using (var pen = new Pen(Color.FromArgb((int)(90 * show), tk.Text))) g.DrawLine(pen, cxp, plotA.Y, cxp, plotB.Bottom);
                var rows = new List<(Series s, float? v)>();
                foreach (var s in all)
                {
                    float? v = s.Chart == 'A' ? upper[s.Key].avg[idx] : lower[s.Key].avg[idx];
                    rows.Add((s, v));
                    if (v == null || _hidden.Contains(s.Key)) continue;
                    var plot = s.Chart == 'A' ? plotA : plotB;
                    float mx = s.Chart == 'A' ? 100 : maxB;
                    float py = A("pt.y." + s.Key, plot.Bottom - plot.Height * Math.Clamp(v.Value / mx, 0, 1.05f), 180, Motion.Out);
                    float a = show * (_hl != null && _hl != s.Key ? 0.25f : 1);
                    float rr = S(4.5f);
                    using var border = new SolidBrush(Color.FromArgb((int)(255 * a), tk.Card));
                    using var fill = new SolidBrush(Color.FromArgb((int)(255 * a), tk.Series[s.ColorIndex]));
                    g.FillEllipse(border, cxp - rr - S(2), py - rr - S(2), (rr + S(2)) * 2, (rr + S(2)) * 2);
                    g.FillEllipse(fill, cxp - rr, py - rr, rr * 2, rr * 2);
                }
                var t = TimeAt(idx, n, step);
                string tt = $"{t.Month}月{t.Day}日 {t:HH:mm}" + (presence[idx] == null ? " · 未运行" : "");
                int tipH = S(44 + 22 * rows.Count);
                float tipX = A("tip.x", cxp > card.X + card.Width * 0.67f ? cxp - S(232) : cxp + S(16), 350, new Ease(.34, 1.2, .64, 1));
                var tip = new Rectangle((int)tipX, card.Y + S(46), S(216), tipH);
                int al = (int)(255 * show);
                GxDraw.Shadow(g, tip, S(12), Color.FromArgb(al / 2, tk.Shadow));
                GxDraw.Round(g, tip, S(12), Color.FromArgb(Math.Min(al, 250), tk.Float));
                GxDraw.RoundStroke(g, tip, S(12), Color.FromArgb(al, tk.Line));
                GxDraw.Text(g, tt, Gx.F(12, false, true), new Rectangle(tip.X + S(14), tip.Y + S(10), tip.Width - S(28), S(18)), Color.FromArgb(al, tk.Muted));
                int ry = tip.Y + S(34);
                foreach (var (s, v) in rows)
                {
                    float ra = _hidden.Contains(s.Key) ? 0.35f : 1f;
                    Color C(Color c) => Color.FromArgb((int)(al * ra), c);
                    using (var dot = new SolidBrush(C(tk.Series[s.ColorIndex]))) g.FillEllipse(dot, tip.X + S(14), ry + S(5), S(8), S(8));
                    GxDraw.Text(g, s.Label, Gx.F(12), new Rectangle(tip.X + S(30), ry, S(100), S(18)), C(tk.Text2));
                    GxDraw.Text(g, v == null ? "—" : $"{v.Value:0} {s.Unit}", Gx.F(12, false, true), new Rectangle(tip.X + S(14), ry, tip.Width - S(28), S(18)), C(tk.Text), TextFormatFlags.Right);
                    ry += S(22);
                }
            }
            return ClientSize.Height;
        }

        private void DrawSeries(Graphics g, Series s, float?[] vals, Rectangle plot, float min, float max, Func<int, float> xOf)
        {
            var tk = Tk;
            float vis = A("ser.vis." + s.Key, _hidden.Contains(s.Key) ? 0 : (_hl != null && _hl != s.Key ? 0.18f : 1f), 300, Motion.Out);
            if (vis <= 0.01f) return;
            float width = A("ser.w." + s.Key, _hl == s.Key ? 2.6f : 1.8f, 300, Motion.Out);
            using var pen = new Pen(Color.FromArgb((int)(255 * vis), tk.Series[s.ColorIndex]), Sf(width)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            var seg = new List<PointF>();
            void Flush()
            {
                if (seg.Count >= 2) g.DrawLines(pen, seg.ToArray());
                else if (seg.Count == 1) g.DrawEllipse(pen, seg[0].X - 0.5f, seg[0].Y - 0.5f, 1, 1);
                seg.Clear();
            }
            for (int i = 0; i < vals.Length; i++)
            {
                if (vals[i] == null) { Flush(); continue; }
                float y = plot.Bottom - plot.Height * Math.Clamp((vals[i]!.Value - min) / Math.Max(1e-6f, max - min), 0, 1.05f);
                seg.Add(new PointF(xOf(i), y));
            }
            Flush();
        }

        private static float Sf(float px) => px * UIUtils.ScaleFactor;

        private static float NiceMax(float v, string kind)
        {
            float floor = kind == "power" ? 160 : kind == "clock" ? 2000 : 60;
            v = Math.Max(v * 1.15f, floor);
            float step = kind == "clock" ? 500 : kind == "fps" ? 30 : 40;
            return (float)Math.Ceiling(v / step) * step;
        }

        private void ExportCsv()
        {
            using var dlg = new SaveFileDialog
            {
                Title = "导出硬件趋势",
                Filter = "CSV|*.csv",
                FileName = $"LiteMonitor_趋势_{DateTime.Now:yyyyMMdd_HHmm}.csv"
            };
            if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
            var keys = Upper.Concat(Lower.SelectMany(l => l.series)).Select(s => s.Key).Distinct().ToList();
            var sb = new StringBuilder();
            sb.AppendLine("Time," + string.Join(",", keys.SelectMany(k => new[] { k + ".Avg", k + ".Max" })));
            foreach (var p in _points.OrderBy(p => p.Time))
            {
                sb.Append(p.Time.ToString("yyyy-MM-dd HH:mm"));
                foreach (var k in keys)
                    sb.Append(p.Values.TryGetValue(k, out var v) ? $",{v.Avg:0.##},{v.Max:0.##}" : ",,");
                sb.AppendLine();
            }
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
        }
    }
}
