using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices;
using LiteMonitor.src.SystemServices.InfoService;
using LiteMonitor.src.UI.SettingsPage;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Windows.Forms;

namespace LiteMonitor.src.UI.Graphite
{
    /// <summary>设置窗口各部分共享的上下文：草稿、实时设置与变更通知。</summary>
    public sealed class SettingsContext
    {
        public Settings Draft = null!;
        public Settings Live = null!;
        public UIController Ui = null!;
        public MainForm Main = null!;
        /// <summary>草稿被改动（用于刷新预览、未保存提示）。</summary>
        public Action? Changed;
        /// <summary>立即生效、不进入草稿比较的改动（如界面模式）之后调用，用于重设基线。</summary>
        public Action? ImmediateApplied;
        public void Touch() => Changed?.Invoke();
    }

    /// <summary>设计稿版式的设置页：一个整页自绘画布（可滚动），挂在旧的页面基类上以复用保存流程。</summary>
    public sealed class DesignPage : SettingsPageBase
    {
        public readonly DesignPageCanvas Canvas;
        public DesignPage(DesignPageCanvas canvas)
        {
            Canvas = canvas;
            canvas.Dock = DockStyle.Fill;
            Controls.Add(canvas);
            BackColor = Gx.Current.Win;
        }
        public override void OnShow()
        {
            base.OnShow();
            BackColor = Gx.Current.Win;
            Canvas.PlayEntrance();
        }
    }

    /// <summary>设计页公共部分：标题、分区卡片、行、开关 / 滑块 / 分段等控件，以及页面入场与分区依次浮现。</summary>
    public abstract class DesignPageCanvas : GraphiteCanvas
    {
        protected readonly SettingsContext Ctx;
        protected Settings D => Ctx.Draft;
        private int _showKey;
        private int _section;

        protected DesignPageCanvas(SettingsContext ctx) { Ctx = ctx; Scrollable = true; }

        /// <summary>页面入场：整页上浮 14px 淡入（.55s），各分区依次浮现（间隔 50ms）。</summary>
        public void PlayEntrance() { _showKey++; ResetScroll(); Invalidate(); }

        protected abstract int DrawPage(Graphics g, int x, int y, int w);

        protected override int Draw(Graphics g)
        {
            var tk = Tk;
            using (var bg = new SolidBrush(tk.Win)) g.FillRectangle(bg, ClientRectangle);
            _section = 0;
            float page = AFrom("page." + _showKey, 0, 1, 550, Motion.Out);
            float fade = AFrom("pagefade." + _showKey, 0, 1, 300, Motion.Out);
            var st = g.Save();
            g.TranslateTransform(0, -ScrollY + (1 - page) * S(14));
            int h = DrawPage(g, S(36), S(28), ClientSize.Width - S(72)) + S(100);
            g.Restore(st);
            if (fade < 0.999f)
            {
                using var veil = new SolidBrush(Color.FromArgb((int)(255 * (1 - fade)), tk.Win));
                g.FillRectangle(veil, ClientRectangle);
            }
            DrawScrollbar(g, ClientRectangle);
            return h;
        }

        protected int PageTitle(Graphics g, int x, int y, int w, string title, string sub)
        {
            GxDraw.Text(g, title, Gx.F(22, true), new Rectangle(x, y, w, S(30)), Tk.Text);
            GxDraw.Text(g, sub, Gx.F(13), new Rectangle(x, y + S(36), w, S(20)), Tk.Faint, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            return y + S(56);
        }

        /// <summary>开始一个分区：可选的小标题，返回分区内容起点。分区以依次浮现的方式入场。</summary>
        protected (float k, int y) BeginSection(Graphics g, int x, int y, int w, string? heading)
        {
            float k = AFrom($"sec.{_showKey}.{_section}", 0, 1, 500, Motion.Out, 40 + _section * 50);
            _section++;
            y += S(26);
            if (heading != null)
            {
                GxDraw.Text(g, heading, Gx.F(11), new Rectangle(x, y + (int)((1 - k) * S(8)), w, S(16)), Gx.Mix(Tk.Win, Tk.Faint, k));
                y += S(26);
            }
            return (k, y);
        }

        /// <summary>分区卡片：rows 为每行高度；返回每行矩形。入场时整体下移并淡入。</summary>
        protected Rectangle[] Card(Graphics g, int x, int y, int w, float k, params int[] rows)
        {
            int dy = (int)((1 - k) * S(8));
            int h = rows.Sum();
            var r = new Rectangle(x, y + dy, w, h);
            GxDraw.Round(g, r, S(14), Gx.Mix(Tk.Win, Tk.Card, k));
            GxDraw.RoundStroke(g, r, S(14), Gx.Mix(Tk.Win, Tk.Line2, k));
            var res = new Rectangle[rows.Length];
            int yy = r.Y;
            for (int i = 0; i < rows.Length; i++)
            {
                res[i] = new Rectangle(x, yy, w, rows[i]);
                if (i > 0) using (var pen = new Pen(Gx.Mix(Tk.Win, Tk.Line2, k))) g.DrawLine(pen, x + S(16), yy, x + w - S(16), yy);
                yy += rows[i];
            }
            return res;
        }

        protected void RowLabel(Graphics g, Rectangle row, string label, string? desc, float alpha = 1)
        {
            Color C(Color c) => Gx.Mix(Tk.Card, c, alpha);
            if (string.IsNullOrEmpty(desc))
                GxDraw.Text(g, label, Gx.F(13), new Rectangle(row.X + S(16), row.Y, row.Width / 2, row.Height), C(Tk.Text));
            else
            {
                GxDraw.Text(g, label, Gx.F(13), new Rectangle(row.X + S(16), row.Y + row.Height / 2 - S(19), row.Width - S(260), S(20)), C(Tk.Text));
                GxDraw.Text(g, desc, Gx.F(12), new Rectangle(row.X + S(16), row.Y + row.Height / 2 + S(1), row.Width - S(260), S(18)), C(Tk.Faint), TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            }
        }

        /// <summary>右侧开关（40×24，.5s 弹簧）。</summary>
        protected void SwitchAt(Graphics g, string id, Rectangle row, bool on, Action<bool> set)
        {
            var r = new Rectangle(row.Right - S(16) - S(40), row.Y + (row.Height - S(24)) / 2, S(40), S(24));
            float k = A(id, on ? 1 : 0, 500, Motion.Spring);
            GxDraw.Switch(g, r, k, Tk, IsPressed(id));
            Hit(id, r, () => { set(!on); Ctx.Touch(); });
        }

        /// <summary>右侧分段控件（每段 segW）。</summary>
        protected void SegAt(Graphics g, string id, Rectangle row, string[] labels, int selected, int segW, Action<int> pick)
        {
            var r = new Rectangle(row.Right - S(16) - S(segW * labels.Length + 6), row.Y + (row.Height - S(34)) / 2, S(segW * labels.Length + 6), S(34));
            Seg(g, id, r, labels, selected, i => { pick(i); Ctx.Touch(); });
        }

        /// <summary>右侧滑块 + 数值；拖动时手柄放大并带光晕。</summary>
        protected void SliderAt(Graphics g, string id, Rectangle row, double min, double max, double step, double value, string text, Action<double> set, int width = 220, Color? accent = null, int rightPad = 16)
        {
            var numR = new Rectangle(row.Right - S(rightPad) - S(52), row.Y, S(52), row.Height);
            GxDraw.Text(g, text, Gx.F(12, false, true), numR, Tk.Text2, TextFormatFlags.Right);
            var r = new Rectangle(numR.X - S(14) - S(width), row.Y + row.Height / 2 - S(12), S(width), S(24));
            bool dragging = IsPressed(id);
            float hot = A(id + ".hot", dragging || Hot(r) ? 1 : 0, 200, Motion.Out);
            float v01 = (float)((value - min) / Math.Max(1e-9, max - min));
            float shown = A(id + ".v", v01, dragging ? 60 : 300, Motion.Out);
            GxDraw.Slider(g, r, shown, accent ?? Tk.Acc, Tk, hot);
            DragHit(id, Rectangle.Inflate(r, S(8), S(4)), p =>
            {
                float f = Math.Clamp((p.X - r.X) / (float)r.Width, 0, 1);
                double v = min + f * (max - min);
                v = Math.Round(v / step) * step;
                set(Math.Clamp(v, min, max));
                Ctx.Touch();
            });
        }
    }

    // =====================================================================
    // 外观
    // =====================================================================
    internal sealed class AppearanceCanvas : DesignPageCanvas
    {
        private static readonly (string hex, string name)[] Accents =
        {
            ("", "主题默认"), ("#6CB6FF", "天蓝"), ("#1F6FD1", "钴蓝"), ("#4FD1C5", "青绿"), ("#F59E5B", "琥珀"), ("#C4A1FF", "藕紫")
        };

        public AppearanceCanvas(SettingsContext ctx) : base(ctx) { }

        protected override int DrawPage(Graphics g, int x, int y, int w)
        {
            var tk = Tk;
            y = PageTitle(g, x, y, w, "外观", "面板相关的改动先出现在右侧预览里，保存后才会应用到正在运行的面板。");

            // 界面模式（立即生效）
            var (k0, y0) = BeginSection(g, x, y, w, null);
            var rows = Card(g, x, y0, w, k0, S(68));
            RowLabel(g, rows[0], "界面模式", "作用于设置、流量统计和趋势等窗口，立即生效", k0);
            string[] modes = { "dark", "light", "system" };
            int mi = Math.Max(0, Array.IndexOf(modes, Ctx.Live.UIThemeMode));
            var segR = new Rectangle(rows[0].Right - S(16) - S(84 * 3 + 6), rows[0].Y + (rows[0].Height - S(34)) / 2, S(84 * 3 + 6), S(34));
            float thumb = A("mode.thumb", mi, 500, Motion.Spring);
            var segs = GxDraw.Segmented(g, segR, new[] { "", "", "" }, thumb, mi, tk, Gx.F(12));
            string[] labels = { "深色", "浅色", "跟随系统" };
            string[] icons = { "moon", "sun", "monitor" };
            for (int i = 0; i < 3; i++)
            {
                float on = Math.Clamp(1 - Math.Abs(thumb - i), 0, 1);
                Color c = Gx.Mix(tk.Faint, tk.Text, on);
                int tw = GxDraw.Measure(g, labels[i], Gx.F(12)) + S(20);
                int sx = segs[i].X + (segs[i].Width - tw) / 2;
                GxDraw.Icon(g, icons[i], new RectangleF(sx, segs[i].Y + (segs[i].Height - S(14)) / 2f, S(14), S(14)), c);
                GxDraw.Text(g, labels[i], Gx.F(12), new Rectangle(sx + S(20), segs[i].Y, tw, segs[i].Height), c);
                int ii = i;
                Hit("mode." + i, segs[i], () =>
                {
                    if (ii == mi) return;
                    var center = PointToScreen(new Point(segs[ii].X + segs[ii].Width / 2, segs[ii].Y + segs[ii].Height / 2 - ScrollY));
                    UIModeHub.Request(modes[ii], this, center);
                    D.UIThemeMode = modes[ii];
                    Ctx.ImmediateApplied?.Invoke();
                });
            }
            y = rows[0].Bottom;

            // 面板主题卡片
            var (k1, y1) = BeginSection(g, x, y, w, "面板主题");
            var themes = ThemeManager.GetAvailableThemes().ToList();
            int cols = 4, gap = S(14);
            int cw = (w - gap * (cols - 1)) / cols;
            int cardH = S(84) + S(30);
            for (int i = 0; i < themes.Count; i++)
            {
                int cx = x + (i % cols) * (cw + gap), cy = y1 + (i / cols) * (cardH + S(12)) + (int)((1 - k1) * S(8));
                DrawThemeCard(g, themes[i], new Rectangle(cx, cy, cw, cardH), k1);
            }
            y = y1 + ((themes.Count + cols - 1) / cols) * (cardH + S(12));

            // 细节
            var (k2, y2) = BeginSection(g, x, y, w, "细节");
            rows = Card(g, x, y2, w, k2, S(68), S(56), S(56), S(56), S(68));
            RowLabel(g, rows[0], "强调色", "用于面板进度条、实时点与选中态", k2);
            int sx2 = rows[0].Right - S(16);
            for (int i = Accents.Length - 1; i >= 0; i--)
            {
                var (hex, name) = Accents[i];
                int d = S(26);
                sx2 -= d;
                var r = new Rectangle(sx2, rows[0].Y + (rows[0].Height - d) / 2, d, d);
                bool sel = string.Equals(D.PanelAccent ?? "", hex, StringComparison.OrdinalIgnoreCase);
                float hov = A("sw.h." + i, Hot(r) ? 1 : 0, 350, Motion.Spring);
                float sc = 1 + 0.15f * hov;
                var rr = new RectangleF(r.X + r.Width / 2f - d * sc / 2, r.Y + r.Height / 2f - d * sc / 2, d * sc, d * sc);
                Color c = hex == "" ? DefaultAccent() : ColorTranslator.FromHtml(hex);
                float ring = A("sw.r." + i, sel ? 1 : 0, 300, Motion.Out);
                if (ring > 0.01f)
                {
                    using var pr = new Pen(Color.FromArgb((int)(255 * ring), c), S(2));
                    g.DrawEllipse(pr, RectangleF.Inflate(rr, S(4), S(4)));
                }
                using (var b = new SolidBrush(c)) g.FillEllipse(b, rr);
                if (hex == "")
                {
                    using var pen = new Pen(tk.Card, S(2));
                    g.DrawLine(pen, rr.X + rr.Width * 0.25f, rr.Bottom - rr.Height * 0.25f, rr.Right - rr.Width * 0.25f, rr.Y + rr.Height * 0.25f);
                }
                var hh = hex;
                Hit("sw." + i, r, () => { D.PanelAccent = hh; Ctx.Touch(); });
                sx2 -= S(10);
            }

            RowLabel(g, rows[1], "不透明度", null, k2);
            SliderAt(g, "opacity", rows[1], 40, 100, 1, Math.Round(D.Opacity * 100), $"{Math.Round(D.Opacity * 100)}%", v => D.Opacity = v / 100.0);
            RowLabel(g, rows[2], "面板宽度", null, k2);
            SliderAt(g, "width", rows[2], 180, 600, 10, D.PanelWidth, $"{D.PanelWidth}px", v => D.PanelWidth = (int)v);
            RowLabel(g, rows[3], "密度", null, k2);
            SegAt(g, "density", rows[3], new[] { "舒适", "紧凑" }, D.PanelCompact ? 1 : 0, 64, i => D.PanelCompact = i == 1);
            RowLabel(g, rows[4], "动效", "数值补间、展开折叠与悬停反馈的强度", k2);
            string[] levels = { "lively", "calm", "off" };
            SegAt(g, "motion", rows[4], new[] { "灵动", "克制", "关闭" }, Math.Max(0, Array.IndexOf(levels, D.MotionLevel)), 64, i =>
            {
                D.MotionLevel = levels[i];
                D.AnimationSpeed = i switch { 0 => 0.35, 1 => 0.6, _ => 1.0 };
            });
            return rows[4].Bottom;
        }

        private Color DefaultAccent()
        {
            var t = ThemeManager.LoadDetached(D.Skin);
            return t != null ? ThemeManager.ParseColor(t.Color.BarLow) : Tk.Acc;
        }

        private void DrawThemeCard(Graphics g, string name, Rectangle r, float k)
        {
            var tk = Tk;
            var t = ThemeManager.LoadDetached(name);
            bool sel = string.Equals(D.Skin, name, StringComparison.OrdinalIgnoreCase);
            bool hot = Hot(r);
            float lift = A("tc.l." + name, hot ? 1 : 0, 350, Motion.Spring);
            float press = A("tc.p." + name, IsPressed("tc." + name) ? 1 : 0, IsPressed("tc." + name) ? 100 : 350, IsPressed("tc." + name) ? Motion.Out : Motion.Spring);
            var st = g.Save();
            float sc = 1 - 0.03f * press;
            g.TranslateTransform(r.X + r.Width / 2f, r.Y + r.Height / 2f - lift * S(3));
            g.ScaleTransform(sc, sc);
            g.TranslateTransform(-(r.X + r.Width / 2f), -(r.Y + r.Height / 2f));

            var prev = new Rectangle(r.X, r.Y, r.Width, S(84));
            Color bg = t != null ? ThemeManager.ParseColor(t.Color.Background) : tk.Card;
            Color text = t != null ? ThemeManager.ParseColor(t.Color.TextTitle) : tk.Text;
            Color track = t != null ? ThemeManager.ParseColor(t.Color.BarBackground) : tk.Track;
            Color acc = t != null ? ThemeManager.ParseColor(t.Color.BarLow) : tk.Acc;
            Color mid = t != null ? ThemeManager.ParseColor(t.Color.BarMid) : tk.Warn;
            float selK = A("tc.s." + name, sel ? 1 : 0, 350, Motion.Out);
            if (selK > 0.01f) GxDraw.Shadow(g, prev, S(12), Color.FromArgb((int)(60 * selK), tk.Acc), 14, 8);
            GxDraw.Round(g, prev, S(12), Gx.Mix(tk.Win, bg, k));
            GxDraw.RoundStroke(g, prev, S(12), Gx.Mix(tk.Line, tk.Acc, selK), 1 + selK * 1.2f);
            int lx = prev.X + S(12), lw = prev.Width - S(24);
            GxDraw.Round(g, new Rectangle(lx, prev.Y + S(12), (int)(lw * 0.4f), S(5)), S(3), Color.FromArgb(204, text));
            float[] fills = { 0.55f, 0.3f, 0.72f };
            for (int i = 0; i < 3; i++)
            {
                var tr = new Rectangle(lx, prev.Y + S(12) + S(12) * (i + 1), (int)(lw * 0.8f), S(5));
                GxDraw.Round(g, tr, S(3), track);
                GxDraw.Round(g, new Rectangle(tr.X, tr.Y, (int)(tr.Width * fills[i]), tr.Height), S(3), i == 2 ? mid : acc);
            }
            // 选中标记：弹出放大（.45s，先放大到 1.25 再回落）
            if (sel)
            {
                float pop = AFrom("tc.pop." + name + "." + D.Skin, 0, 1, 450, Motion.Spring);
                float d = S(20) * pop;
                var cr = new RectangleF(prev.Right - S(8) - S(20) + (S(20) - d) / 2, prev.Y + S(8) + (S(20) - d) / 2, d, d);
                using (var b = new SolidBrush(tk.Acc)) g.FillEllipse(b, cr);
                if (pop > 0.3f) GxDraw.Icon(g, "check", RectangleF.Inflate(cr, -d * 0.22f, -d * 0.22f), tk.OnAcc, 2.6f);
            }
            GxDraw.Text(g, name.Replace('_', ' '), Gx.F(13), new Rectangle(r.X, prev.Bottom + S(8), r.Width, S(20)), Gx.Mix(tk.Win, tk.Text, k), TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            g.Restore(st);
            Hit("tc." + name, r, () => { D.Skin = name; Ctx.Touch(); });
        }
    }

    // =====================================================================
    // 监控项
    // =====================================================================
    internal sealed class MonitorItemsCanvas : DesignPageCanvas
    {
        public MonitorItemsCanvas(SettingsContext ctx) : base(ctx) { }

        protected override int DrawPage(Graphics g, int x, int y, int w)
        {
            var items = D.MonitorItems ?? new List<MonitorItemConfig>();
            int shown = items.Count(i => i.VisibleInPanel);
            y = PageTitle(g, x, y, w, "监控项", $"已显示 {shown} / {items.Count} 项，打开或关闭会立刻反映在预览中。排序与改名在「监控项高级」中设置。");
            foreach (var grp in items.GroupBy(i => i.UIGroup).OrderBy(gp => gp.Min(i => i.SortIndex)))
            {
                string gname = LanguageManager.T("Groups." + grp.Key);
                int s = 0; while (s < gname.Length && (!char.IsLetterOrDigit(gname[s]) || char.IsSurrogate(gname[s]) || gname[s] > 0x2000 && gname[s] < 0x2C00 || gname[s] == 0xFE0F)) s++;
                var (k, yy) = BeginSection(g, x, y, w, s < gname.Length ? gname.Substring(s) : gname);
                var list = grp.OrderBy(i => i.SortIndex).ToList();
                var rows = Card(g, x, yy, w, k, list.Select(_ => S(52)).ToArray());
                for (int i = 0; i < list.Count; i++)
                {
                    var it = list[i];
                    GxDraw.Icon(g, "grip", new RectangleF(rows[i].X + S(14), rows[i].Y + (rows[i].Height - S(14)) / 2f, S(14), S(14)), Tk.Ghost);
                    string label = string.IsNullOrWhiteSpace(it.UserLabel) ? LanguageManager.T("Items." + it.Key) : it.UserLabel;
                    GxDraw.Text(g, label, Gx.F(13), new Rectangle(rows[i].X + S(38), rows[i].Y, rows[i].Width - S(120), rows[i].Height), Gx.Mix(Tk.Card, Tk.Text, k));
                    var cfgItem = it;
                    SwitchAt(g, "vis." + it.Key, rows[i], it.VisibleInPanel, v => cfgItem.VisibleInPanel = v);
                }
                y = rows[^1].Bottom;
            }
            return y;
        }
    }

    // =====================================================================
    // 阈值与告警
    // =====================================================================
    internal sealed class ThresholdCanvas : DesignPageCanvas
    {
        public ThresholdCanvas(SettingsContext ctx) : base(ctx) { }

        protected override int DrawPage(Graphics g, int x, int y, int w)
        {
            var tk = Tk;
            var th = D.Thresholds;
            y = PageTitle(g, x, y, w, "阈值与告警", "拖动滑块，色带会同步展示正常、偏高与过高三个区间。");
            var defs = new (string id, string label, string unit, ValueRange vr, double max, double step)[]
            {
                ("load", "负载", "%", th.Load, 100, 1),
                ("temp", "温度", "°C", th.Temp, 100, 1),
                ("up", "网络上传", "MB/s", th.NetUpMB, 10, 0.5),
                ("down", "网络下载", "MB/s", th.NetDownMB, 20, 0.5),
                ("disk", "磁盘读写", "MB/s", th.DiskIOMB, 50, 1),
                ("dayup", "今日上传", "MB", th.DataUpMB, 10240, 128),
                ("daydown", "今日下载", "MB", th.DataDownMB, 20480, 256),
            };
            var (k, yy) = BeginSection(g, x, y, w, null);
            var rows = Card(g, x, yy, w, k, defs.Select(_ => S(92)).ToArray());
            for (int i = 0; i < defs.Length; i++)
            {
                var (id, label, unit, vr, max, step) = defs[i];
                var r = rows[i];
                GxDraw.Text(g, label, Gx.F(13), new Rectangle(r.X + S(16), r.Y + S(18), S(110), S(20)), Gx.Mix(tk.Card, tk.Text, k));
                GxDraw.Text(g, unit, Gx.F(12), new Rectangle(r.X + S(16), r.Y + S(40), S(110), S(18)), Gx.Mix(tk.Card, tk.Faint, k));
                int bx = r.X + S(136), bw = r.Width - S(136) - S(16);
                // 色带：三段宽度随阈值变化（.45s 缓出）
                float w0 = A("band0." + id, (float)(vr.Warn / max), 450, Motion.Out);
                float w1 = A("band1." + id, (float)((vr.Crit - vr.Warn) / max), 450, Motion.Out);
                var band = new Rectangle(bx, r.Y + S(18), bw, S(8));
                using (var clip = UIUtils.RoundRect(band, S(4)))
                {
                    var st = g.Save();
                    g.SetClip(clip, CombineMode.Intersect);
                    using (var b0 = new SolidBrush(tk.Acc)) g.FillRectangle(b0, band.X, band.Y, bw * w0, band.Height);
                    using (var b1 = new SolidBrush(tk.Warn)) g.FillRectangle(b1, band.X + bw * w0, band.Y, bw * w1, band.Height);
                    using (var b2 = new SolidBrush(tk.Crit)) g.FillRectangle(b2, band.X + bw * (w0 + w1), band.Y, bw, band.Height);
                    g.Restore(st);
                }
                var half = (bw - S(18)) / 2;
                var warnRow = new Rectangle(bx - S(16), r.Y + S(40), half + S(16), S(36));
                var critRow = new Rectangle(bx + half + S(18) - S(16), r.Y + S(40), half + S(16), S(36));
                GxDraw.Text(g, "偏高", Gx.F(12), new Rectangle(bx, warnRow.Y, S(30), warnRow.Height), tk.Muted);
                GxDraw.Text(g, "过高", Gx.F(12), new Rectangle(critRow.X + S(16), critRow.Y, S(30), critRow.Height), tk.Muted);
                string F(double v) => step < 1 ? v.ToString("0.0") : v.ToString("0");
                int sw = (half - S(30) - S(14) - S(52)) ;
                SliderAt(g, "w." + id, warnRow, 0, max, step, vr.Warn, F(vr.Warn), v => vr.Warn = Math.Min(v, vr.Crit), (int)(sw / UIUtils.ScaleFactor), tk.Warn, 0);
                SliderAt(g, "c." + id, critRow, 0, max, step, vr.Crit, F(vr.Crit), v => vr.Crit = Math.Max(v, vr.Warn), (int)(sw / UIUtils.ScaleFactor), tk.Crit, 0);
            }
            y = rows[^1].Bottom;

            var (k2, y2) = BeginSection(g, x, y, w, null);
            var rows2 = Card(g, x, y2, w, k2, S(68), S(56));
            RowLabel(g, rows2[0], "高温通知", "温度持续 5 秒超过阈值时弹出系统通知，3 分钟内不重复", k2);
            SwitchAt(g, "alert", rows2[0], D.AlertTempEnabled, v => D.AlertTempEnabled = v);
            float en = A("alert.row", D.AlertTempEnabled ? 1 : 0.4f, 300, Motion.Out);
            RowLabel(g, rows2[1], "通知阈值", null, en);
            SliderAt(g, "alertAt", rows2[1], 60, 100, 1, D.AlertTempThreshold, $"{D.AlertTempThreshold}°C", v => D.AlertTempThreshold = (int)v);
            return rows2[1].Bottom;
        }
    }

    // =====================================================================
    // 任务栏
    // =====================================================================
    internal sealed class TaskbarDesignCanvas : DesignPageCanvas
    {
        public TaskbarDesignCanvas(SettingsContext ctx) : base(ctx) { }

        protected override int DrawPage(Graphics g, int x, int y, int w)
        {
            y = PageTitle(g, x, y, w, "任务栏", "嵌入任务栏的精简读数。使用逐像素透明绘制，文字边缘不再有灰色毛边。");
            var (k, yy) = BeginSection(g, x, y, w, null);
            var rows = Card(g, x, yy, w, k, S(56), S(56), S(68), S(68), S(56), S(56), S(68), S(68));
            RowLabel(g, rows[0], "在任务栏显示", null, k);
            SwitchAt(g, "tb.show", rows[0], D.ShowTaskbar, v => D.ShowTaskbar = v);
            RowLabel(g, rows[1], "布局", null, k);
            SegAt(g, "tb.layout", rows[1], new[] { "双行", "单行" }, D.TaskbarSingleLine ? 1 : 0, 64, i => D.TaskbarSingleLine = i == 1);
            // 采样间隔是全局的（面板、任务栏、历史记录共用）；任务栏数字刷新不会快于它
            RowLabel(g, rows[2], "采样间隔", "面板、任务栏与历史记录共用", k);
            SliderAt(g, "tb.sample", rows[2], 0.2, 5, 0.1, D.RefreshMs / 1000.0, $"{D.RefreshMs / 1000.0:0.0#} 秒",
                v => D.RefreshMs = (int)Math.Round(v * 1000), 200);
            RowLabel(g, rows[3], "任务栏数字刷新", "读数多久换一次；最左为跟随采样", k);
            int effTb = Math.Max(D.TaskbarRefreshMs, D.RefreshMs);
            SliderAt(g, "tb.refresh", rows[3], 0.2, 10, 0.1, effTb / 1000.0,
                D.TaskbarRefreshMs <= D.RefreshMs ? "随采样" : $"{effTb / 1000.0:0.0#} 秒",
                v => D.TaskbarRefreshMs = (int)Math.Round(v * 1000), 200);
            RowLabel(g, rows[4], "字号", null, k);
            var style = D.GetStyle();
            SliderAt(g, "tb.size", rows[4], 9, 14, 0.5, style.Size, $"{style.Size:0.#}pt", v =>
            {
                EnsureCustomLayout();
                D.TaskbarFontSize = (float)v;
            });
            RowLabel(g, rows[5], "数值加粗", null, k);
            SwitchAt(g, "tb.bold", rows[5], style.Bold, v => { EnsureCustomLayout(); D.TaskbarFontBold = v; });
            RowLabel(g, rows[6], "跟随系统明暗", "任务栏切换为浅色时自动换用深色文字", k);
            SwitchAt(g, "tb.follow", rows[6], !D.TaskbarCustomStyle, v => D.TaskbarCustomStyle = !v);
            RowLabel(g, rows[7], "平滑文字边缘", "逐像素透明；若任务栏条显示异常可关闭，退回旧方式", k);
            SwitchAt(g, "tb.alpha", rows[7], D.TaskbarPerPixelAlpha, v => D.TaskbarPerPixelAlpha = v);
            return rows[7].Bottom;
        }

        /// <summary>字号 / 加粗需要「自定义布局」才生效：首次修改时把当前预设的参数带过去。</summary>
        private void EnsureCustomLayout()
        {
            if (D.TaskbarCustomLayout) return;
            var s = D.GetStyle();
            D.TaskbarCustomLayout = true;
            D.TaskbarFontFamily = s.Font;
            D.TaskbarFontSize = s.Size;
            D.TaskbarFontBold = s.Bold;
        }
    }

    // =====================================================================
    // 右侧实时预览
    // =====================================================================
    internal sealed class PreviewCanvas : GraphiteCanvas
    {
        private readonly SettingsContext _ctx;
        private readonly GraphitePanel _panel = new();
        private readonly Dictionary<string, MetricItem> _items = new();
        private readonly System.Windows.Forms.Timer _tick = new() { Interval = 1000 };
        private Theme? _theme;
        private string _themeKey = "";
        private long _lastAnim;
        public bool TaskbarMode { get; set; }
        public Action? CloseRequested;

        public PreviewCanvas(SettingsContext ctx)
        {
            _ctx = ctx;
            _tick.Tick += (_, __) => { Sample(); Invalidate(); };
            _tick.Start();
            _panel.BeginEntrance();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _tick.Dispose(); _panel.Dispose(); _theme?.DisposeFonts(); }
            base.Dispose(disposing);
        }

        private void Sample()
        {
            foreach (var it in _items.Values)
            {
                if (it.Key.StartsWith("DASH.")) { it.TextValue = InfoService.Instance.GetValue(it.Key.Substring(5)); continue; }
                var v = HardwareMonitor.Instance?.Get(it.Key);
                it.Value = v;
                if (v.HasValue) it.PushHistory(v.Value);
            }
        }

        private Theme? PreviewTheme()
        {
            var d = _ctx.Draft;
            string key = d.Skin + "|" + d.PanelAccent;
            if (_theme != null && key == _themeKey) return _theme;
            _theme?.DisposeFonts();
            _theme = null;
            _themeKey = key;
            var src = ThemeManager.LoadDetached(d.Skin);
            if (src == null) return null;
            // 预览里可能改强调色：复制一份，避免改到缓存里的主题
            var json = System.Text.Json.JsonSerializer.Serialize(src);
            _theme = System.Text.Json.JsonSerializer.Deserialize<Theme>(json);
            if (_theme == null) return null;
            if (!string.IsNullOrWhiteSpace(d.PanelAccent)) _theme.Color.BarLow = d.PanelAccent;
            _theme.BuildFonts();
            _theme.Layout.LayoutScale = UIUtils.ScaleFactor;
            return _theme;
        }

        private List<GroupLayoutInfo> Groups()
        {
            var groups = new List<GroupLayoutInfo>();
            var d = _ctx.Draft;
            foreach (var grp in (d.MonitorItems ?? new()).Where(i => i.VisibleInPanel).GroupBy(i => i.UIGroup).OrderBy(gp => gp.Min(i => i.SortIndex)))
            {
                var list = new List<MetricItem>();
                foreach (var c in grp.OrderBy(i => i.SortIndex))
                {
                    if (!_items.TryGetValue(c.Key, out var it))
                    {
                        it = new MetricItem { Key = c.Key, BoundConfig = c, Label = LanguageManager.T("Items." + c.Key), ShortLabel = LanguageManager.T("Short." + c.Key) };
                        _items[c.Key] = it;
                        if (c.Key.StartsWith("DASH.")) it.TextValue = InfoService.Instance.GetValue(c.Key.Substring(5));
                        else { var v = HardwareMonitor.Instance?.Get(c.Key); it.Value = v; if (v.HasValue) { it.DisplayValue = v.Value; it.PushHistory(v.Value); } }
                    }
                    it.BoundConfig = c;
                    list.Add(it);
                }
                groups.Add(new GroupLayoutInfo(grp.Key, list) { Label = LanguageManager.T("Groups." + grp.Key) });
            }
            return groups;
        }

        protected override int Draw(Graphics g)
        {
            var tk = Tk;
            using (var bg = new SolidBrush(tk.Side)) g.FillRectangle(bg, ClientRectangle);
            using (var pen = new Pen(tk.Line2)) g.DrawLine(pen, 0, 0, 0, Height);
            int x = S(28), w = Width - S(56);
            GxDraw.Text(g, "实时预览", Gx.F(11), new Rectangle(x, S(24), S(120), S(32)), tk.Faint);
            var closeR = new Rectangle(Width - S(28) - S(32), S(24), S(32), S(32));
            var modeR = new Rectangle(closeR.X - S(36), S(24), S(32), S(32));
            ModeButton(g, modeR);
            bool ch = Hot(closeR);
            if (ch) GxDraw.Round(g, closeR, S(8), tk.Hover);
            GxDraw.Icon(g, "close", new RectangleF(closeR.X + S(8), closeR.Y + S(8), S(16), S(16)), ch ? tk.Text : tk.Muted);
            Hit("close", closeR, () => CloseRequested?.Invoke());

            int top = S(78);
            float swap = A("pv.mode", TaskbarMode ? 1 : 0, 450, Motion.Out);
            if (swap < 0.999f) DrawPanelPreview(g, new Rectangle(x, top + (int)(swap * S(14)), w, Height - top - S(20)), 1 - swap);
            if (swap > 0.001f) DrawTaskbarPreview(g, new Rectangle(x, top + (int)((1 - swap) * S(14)), w, S(120)), swap);
            KeepAnimating();
            return Height;
        }

        private void DrawPanelPreview(Graphics g, Rectangle area, float alpha)
        {
            var t = PreviewTheme();
            if (t == null) return;
            long now = Motion.Now;
            float dt = Math.Clamp(now - _lastAnim, 1, 100);
            _lastAnim = now;
            var groups = Groups();
            foreach (var gr in groups) foreach (var it in gr.Items) { it.GetFormattedText(false); it.Animate(dt, _ctx.Draft.AnimationSpeed, t); }
            _panel.Compact.Set(_ctx.Draft.PanelCompact ? 1 : 0, 400, Motion.Out);
            _panel.SegThumb.Set(_ctx.Draft.PanelCompact ? 1 : 0, 500, Motion.Spring);
            _panel.TopMost = _ctx.Draft.TopMost;
            _panel.Uptime = InfoService.Instance.GetValue("Uptime");
            _panel.RefreshMs = _ctx.Draft.RefreshMs;
            int pw = Math.Min(area.Width, (int)(_ctx.Draft.PanelWidth * UIUtils.ScaleFactor));
            int h = _panel.Build(groups, t, pw);
            h = Math.Min(h, area.Height);
            if (pw <= 0 || h <= 0) return;
            using var bmp = new Bitmap(pw, h, PixelFormat.Format32bppArgb);
            using (var bg = Graphics.FromImage(bmp)) _panel.Render(bg, groups, t, new Size(pw, h));
            float op = (float)Math.Clamp(_ctx.Draft.Opacity, 0.1, 1) * alpha;
            var dest = new Rectangle(area.X + (area.Width - pw) / 2, area.Y, pw, h);
            GxDraw.Shadow(g, dest, S(16), Color.FromArgb((int)(Tk.Shadow.A * op), Tk.Shadow));
            using (var clip = UIUtils.RoundRect(dest, S(16)))
            {
                var st = g.Save();
                g.SetClip(clip, CombineMode.Intersect);
                using var ia = new ImageAttributes();
                ia.SetColorMatrix(new ColorMatrix { Matrix33 = op });
                g.DrawImage(bmp, dest, 0, 0, pw, h, GraphicsUnit.Pixel, ia);
                g.Restore(st);
            }
            GxDraw.RoundStroke(g, dest, S(16), Color.FromArgb((int)(255 * op), Gx.Mix(Tk.Side, ThemeManager.ParseColor(t.Color.TextTitle), 0.12f)));
        }

        private void DrawTaskbarPreview(Graphics g, Rectangle area, float alpha)
        {
            var tk = Tk;
            var d = _ctx.Draft;
            var style = d.GetStyle();
            Color A_(Color c) => Color.FromArgb((int)(255 * alpha), c);
            var bar = new Rectangle(area.X, area.Y, area.Width, S(44));
            GxDraw.Round(g, bar, S(10), A_(tk.Float));
            GxDraw.RoundStroke(g, bar, S(10), A_(tk.Line));
            var cells = (d.MonitorItems ?? new()).Where(i => i.VisibleInTaskbar).OrderBy(i => i.TaskbarSortIndex).Take(d.TaskbarSingleLine ? 5 : 8).ToList();
            using var font = new Font(style.Font, style.Size, style.Bold ? FontStyle.Bold : FontStyle.Regular);
            int x = bar.X + S(10);
            int per = d.TaskbarSingleLine ? 1 : 2;
            for (int i = 0; i < cells.Count; i += per)
            {
                int colW = 0;
                for (int j = 0; j < per && i + j < cells.Count; j++)
                {
                    var c = cells[i + j];
                    string label = string.IsNullOrWhiteSpace(c.TaskbarLabel) ? LanguageManager.T("Short." + c.Key) : c.TaskbarLabel;
                    float? v = HardwareMonitor.Instance?.Get(c.Key);
                    string val = v.HasValue ? MetricUtils.GetValueStr(c.Key, v, true, c.UnitTaskbar) + MetricUtils.GetDisplayUnit(c.Key, MetricUtils.GetUnitStr(c.Key, v, MetricUtils.UnitContext.Taskbar), c.UnitTaskbar) : "--";
                    int st = v.HasValue ? MetricUtils.GetState(c.Key, v.Value) : 0;
                    Color vc = st == 2 ? tk.Crit : st == 1 ? tk.Warn : tk.Text;
                    int lh = (int)font.GetHeight(g) + S(1);
                    int cy = per == 1 ? bar.Y + (bar.Height - lh) / 2 : bar.Y + S(4) + j * (lh);
                    int lw = GxDraw.Measure(g, label, font), vw = GxDraw.Measure(g, val, font);
                    GxDraw.Text(g, label, font, new Rectangle(x, cy, lw + S(2), lh), A_(tk.Muted));
                    GxDraw.Text(g, val, font, new Rectangle(x + lw + S(6), cy, vw + S(2), lh), A_(vc));
                    colW = Math.Max(colW, lw + S(6) + vw);
                }
                x += colW + S(14);
                if (x > bar.Right - S(30)) break;
            }
            GxDraw.Text(g, "任务栏宽度不足时会自动省略最后几项。", Gx.F(12), new Rectangle(area.X, bar.Bottom + S(12), area.Width, S(18)), A_(tk.Faint));
        }
    }

    // =====================================================================
    // 左侧导航
    // =====================================================================
    internal sealed class SideCanvas : GraphiteCanvas
    {
        public sealed record Item(string Key, string Icon, string Label, bool Advanced);
        private readonly List<Item> _items;
        public string Current = "";
        public Action<string>? Navigate;
        public Action? ResetRequested;

        public SideCanvas(List<Item> items) { _items = items; }

        protected override int Draw(Graphics g)
        {
            var tk = Tk;
            using (var bg = new SolidBrush(tk.Side)) g.FillRectangle(bg, ClientRectangle);
            using (var pen = new Pen(tk.Line2)) g.DrawLine(pen, Width - 1, 0, Width - 1, Height);

            // 品牌
            var logo = new Rectangle(S(18), S(20), S(26), S(26));
            GxDraw.Round(g, logo, S(8), tk.Acc);
            GxDraw.Icon(g, "logo", new RectangleF(logo.X + S(6), logo.Y + S(6), S(14), S(14)), tk.OnAcc, 2.4f);
            GxDraw.Text(g, "LiteMonitor", Gx.F(14, true), new Rectangle(logo.Right + S(10), logo.Y, S(140), logo.Height), tk.Text);

            int x = S(12), w = Width - S(24), y = S(66);
            var slots = new Dictionary<string, Rectangle>();
            bool advHeader = false;
            foreach (var it in _items)
            {
                if (it.Advanced && !advHeader)
                {
                    advHeader = true;
                    y += S(10);
                    GxDraw.Text(g, "更多设置", Gx.F(11), new Rectangle(x + S(14), y, w, S(20)), tk.Faint);
                    y += S(24);
                }
                slots[it.Key] = new Rectangle(x, y, w, S(40));
                y += S(44);
            }

            // 选中底色与竖条：.55s 轻弹簧滑到新位置
            if (slots.TryGetValue(Current, out var cur))
            {
                float py = A("pill.y", cur.Y, 550, Motion.SpringSoft);
                var pill = new Rectangle(cur.X, (int)Math.Round(py), cur.Width, cur.Height);
                GxDraw.Round(g, pill, S(10), Gx.Mix(tk.Side, tk.Text, 0.07f));
                GxDraw.Round(g, new Rectangle(pill.X, pill.Y + S(11), S(3), S(18)), S(2), tk.Acc);
            }

            foreach (var it in _items)
            {
                var r = slots[it.Key];
                bool on = it.Key == Current, hot = Hot(r);
                float hov = A("nav.h." + it.Key, hot && !on ? 1 : 0, 250, Motion.Out);
                if (hov > 0.01f) GxDraw.Round(g, r, S(10), Color.FromArgb((int)(255 * hov), Gx.Mix(tk.Side, tk.Text, 0.04f)));
                Color fg = on ? tk.Text : Gx.Mix(tk.Muted, tk.Text, hov);
                GxDraw.Icon(g, it.Icon, new RectangleF(r.X + S(14), r.Y + (r.Height - S(17)) / 2f, S(17), S(17)), on ? tk.Acc : fg);
                GxDraw.Text(g, it.Label, Gx.F(13, on), new Rectangle(r.X + S(41), r.Y, r.Width - S(45), r.Height), fg, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
                var key = it.Key;
                Hit("nav." + key, r, () => Navigate?.Invoke(key));
            }

            // 底部：恢复默认 + 版本
            var resetR = new Rectangle(S(18), Height - S(62), Width - S(36), S(22));
            bool rh = Hot(resetR);
            GxDraw.Text(g, "恢复默认设置…", Gx.F(12), resetR, rh ? tk.Warn : tk.Faint);
            Hit("reset", resetR, () => ResetRequested?.Invoke());
            string ver = System.Diagnostics.FileVersionInfo.GetVersionInfo(typeof(SideCanvas).Assembly.Location).FileVersion ?? "";
            if (ver.Split('.').Length > 3) ver = string.Join(".", ver.Split('.').Take(3));
            GxDraw.Text(g, "LiteMonitor " + ver + " · Graphite", Gx.F(11), new Rectangle(S(18), Height - S(34), Width - S(36), S(18)), tk.Faint);
            return Height;
        }
    }

    // =====================================================================
    // 底部浮出的保存条
    // =====================================================================
    internal sealed class SaveBar : GraphiteCanvas
    {
        public int Changes;
        public bool Flash;
        private Size _regionSize;
        public Action? Revert, Save;

        protected override int Draw(Graphics g)
        {
            var tk = Tk;
            // 控件按圆角裁剪，叠在卡片上方时不露出矩形底色
            var r = new Rectangle(0, 0, Width, Height);
            if (Region == null || _regionSize != Size)
            {
                _regionSize = Size;
                using var path = UIUtils.RoundRect(r, S(14));
                Region = new Region(path);
            }
            GxDraw.Round(g, r, S(14), tk.Float);
            GxDraw.RoundStroke(g, r, S(14), tk.Line);
            string text = Flash ? "设置已应用到面板" : $"有 {Changes} 项未保存的更改";
            GxDraw.Text(g, text, Gx.F(13), new Rectangle(r.X + S(18), r.Y, r.Width - S(200), r.Height), tk.Text2);
            var save = new Rectangle(r.Right - S(8) - S(84), r.Y + (r.Height - S(34)) / 2, S(84), S(34));
            var revert = new Rectangle(save.X - S(8) - S(64), save.Y, S(64), S(34));
            void Btn(string id, Rectangle b, string label, bool primary, Action? act)
            {
                float press = A(id + ".p", IsPressed(id) ? 1 : 0, IsPressed(id) ? 100 : 300, IsPressed(id) ? Motion.Out : Motion.Spring);
                var st = g.Save();
                float sc = 1 - 0.06f * press, cx = b.X + b.Width / 2f, cy = b.Y + b.Height / 2f;
                g.TranslateTransform(cx, cy); g.ScaleTransform(sc, sc); g.TranslateTransform(-cx, -cy);
                bool hot = Hot(b);
                if (primary) GxDraw.Round(g, b, S(9), hot ? tk.Acc2 : tk.Acc);
                else if (hot) GxDraw.Round(g, b, S(9), tk.Hover);
                GxDraw.Text(g, label, Gx.F(13, primary), b, primary ? tk.OnAcc : tk.Text2, TextFormatFlags.HorizontalCenter);
                g.Restore(st);
                Hit(id, b, () => act?.Invoke());
            }
            Btn("revert", revert, "撤销", false, Revert);
            Btn("save", save, Flash ? "已保存" : "保存", true, Save);
            return Height;
        }
    }
}
