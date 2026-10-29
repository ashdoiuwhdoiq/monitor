using LiteMonitor.src.Core;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace LiteMonitor
{
    /// <summary>
    /// 竖向悬浮面板（Graphite 设计稿）：标题栏（实时点、品牌、密度切换、置顶、更多）、
    /// 可折叠的分组标题行、「标签 + 数值 + 细进度条」的行、页脚（运行时长、采样间隔）。
    /// 动效与设计稿一一对应：入场上浮、行依次浮现、悬停左移、按下缩小、折叠回弹、分段滑块弹簧、
    /// 密度过渡、实时点与告警点脉冲。只负责布局与绘制；交互状态由 UIController 写入。
    /// </summary>
    public sealed class GraphitePanel : IDisposable
    {
        // ===== 设计稿尺寸（px @96DPI）=====
        private const int PadTop = 12, PadSide = 10, PadBottom = 8;
        private const int HeaderH = 28, HeaderGap = 10, IconBtn = 28, SegW = 80, SegH = 24;
        private const int GroupGap = 4, GroupHeadH = 30;
        private const int RowComfy = 51, RowCompact = 30, RowText = 30, LineH = 20, BarGap = 9, BarH = 4;
        private const int FooterGap = 6, FooterH = 27;
        private const int MinWidthForSeg = 290;

        // ===== 设计稿动效时长（ms）=====
        private const float PanelInMs = 700, RowInMs = 600, RowStaggerMs = 45;

        // ===== 布局结果（客户区坐标）=====
        public Rectangle HeaderRect, SegRect, SegComfyRect, SegCompactRect, PinRect, MoreRect, FooterRect;
        public bool ShowSeg { get; private set; }

        // ===== 交互与动效状态（由 UIController 写入）=====
        public Tween Compact { get; } = new Tween(0f);       // 0 舒适 → 1 紧凑：行高与进度条（.4s 缓出）
        public Tween SegThumb { get; } = new Tween(0f);      // 分段滑块位置（.5s 弹簧）
        public Tween PinPress { get; } = new Tween(0f);      // 按下缩小 .88，松开回弹
        public Tween MorePress { get; } = new Tween(0f);
        public Tween RowPress { get; } = new Tween(0f);
        public MetricItem? PressedItem;
        public string HoverButton = "";                       // "pin" / "more" / "comfy" / "compact"
        public GroupLayoutInfo? HoverGroup;
        public MetricItem? Selected;                          // 当前打开详情卡的行
        public float LivePulse = 1f;                          // 0~1：距上次采样的进度，用于实时点与告警点扩散
        public bool TopMost;
        public string Uptime = "";
        public int RefreshMs = 1000;

        private long _entranceStart = -100000;

        /// <summary>开始入场动效：面板上浮淡入（.7s），各行依次浮现（每行间隔 45ms，.6s）。</summary>
        public void BeginEntrance() => _entranceStart = Motion.Now;

        public bool IsAnimating(long now, int rowCount)
        {
            if (now - _entranceStart < PanelInMs + RowStaggerMs * rowCount + RowInMs) return true;
            return Compact.IsRunning(now) || SegThumb.IsRunning(now) || PinPress.IsRunning(now) || MorePress.IsRunning(now) || RowPress.IsRunning(now);
        }

        // ===== 字体缓存（随主题 / 缩放重建）=====
        private Theme? _fontTheme;
        private float _fontScale = -1;
        private Font? _fBrand, _fSmall, _fLabel, _fValue, _fUnit, _fMono;
        private float _scale = 1f;
        private float S => _scale;
        private int P(float px) => (int)Math.Round(px * _scale);

        // =====================================================================
        // 布局
        // =====================================================================
        public int Build(List<GroupLayoutInfo> groups, Theme t, int width)
        {
            _scale = t.Layout.LayoutScale > 0 ? t.Layout.LayoutScale : 1f;
            float c = Math.Clamp(Compact.Value(), 0f, 1f);

            int x0 = P(PadSide), w = width - P(PadSide) * 2;
            int y = P(PadTop);

            HeaderRect = new Rectangle(x0, y, w, P(HeaderH));
            MoreRect = new Rectangle(x0 + w - P(4) - P(IconBtn), y, P(IconBtn), P(IconBtn));
            PinRect = new Rectangle(MoreRect.X - P(8) - P(IconBtn), y, P(IconBtn), P(IconBtn));
            ShowSeg = width >= P(MinWidthForSeg);
            SegRect = new Rectangle(PinRect.X - P(8) - P(SegW), y + (P(HeaderH) - P(SegH)) / 2, P(SegW), P(SegH));
            int half = (SegRect.Width - P(4)) / 2;
            SegComfyRect = new Rectangle(SegRect.X + P(2), SegRect.Y + P(2), half, SegRect.Height - P(4));
            SegCompactRect = new Rectangle(SegComfyRect.Right, SegComfyRect.Y, half, SegComfyRect.Height);
            y += P(HeaderH) + P(HeaderGap);

            foreach (var g in groups)
            {
                y += P(GroupGap);
                g.HeaderRect = new Rectangle(x0, y, w, P(GroupHeadH));
                y += P(GroupHeadH);

                int bodyTop = y, by = y;
                foreach (var it in g.Items)
                {
                    bool text = it.Key.StartsWith("DASH.", StringComparison.OrdinalIgnoreCase);
                    it.Style = text ? MetricRenderStyle.TextOnly : MetricRenderStyle.StandardBar;

                    int rh = text ? P(RowText) : (int)Math.Round(Lerp(P(RowComfy), P(RowCompact), c));
                    int padTop = text ? P(5) : (int)Math.Round(Lerp(P(8), P(5), c));
                    it.Bounds = new Rectangle(x0, by, w, rh);
                    var line = new Rectangle(x0 + P(10), by + padTop, w - P(20), P(LineH));
                    it.LabelRect = line;
                    it.ValueRect = line;
                    it.BarRect = text ? Rectangle.Empty : new Rectangle(line.X, line.Bottom + P(BarGap), line.Width, P(BarH));
                    by += rh;
                }

                int full = by - bodyTop;
                g.FullBounds = new Rectangle(x0, bodyTop, w, full);
                g.Bounds = new Rectangle(x0, bodyTop, w, (int)Math.Round(full * Math.Clamp(g.Expand, 0f, 1.1f)));
                y = bodyTop + Math.Min(g.Bounds.Height, full);
            }

            y += P(FooterGap);
            FooterRect = new Rectangle(x0, y, w, P(FooterH));
            y += P(FooterH) + P(PadBottom);
            return y;
        }

        // =====================================================================
        // 绘制
        // =====================================================================
        public void Render(Graphics g, List<GroupLayoutInfo> groups, Theme t, Size client)
        {
            long now = Motion.Now;
            EnsureFonts(t);
            var pal = new Palette(t);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            using (var bg = new SolidBrush(pal.Bg))
                g.FillRectangle(bg, new Rectangle(-5, -5, client.Width + 10, client.Height + 10));

            // 面板入场：整体上浮 14px 并淡入
            float panelIn = Motion.Out.Eval(Motion.Progress(_entranceStart, 0, PanelInMs, now));
            float panelDy = (1 - panelIn) * 14 * S;

            WithEntrance(g, pal, panelDy, panelIn, Rectangle.Inflate(HeaderRect, P(4), P(4)), () => DrawHeader(g, pal));

            int index = 0;
            foreach (var gr in groups)
            {
                float k = RowIn(index++, now);
                WithEntrance(g, pal, panelDy + (1 - k) * 8 * S, k * panelIn, gr.HeaderRect, () => DrawGroupHeader(g, gr, pal));
                if (gr.Bounds.Height <= 0)
                {
                    index += gr.Items.Count;
                    continue;
                }

                var saved = g.Save();
                if (gr.Bounds.Height < gr.FullBounds.Height) g.SetClip(gr.Bounds);
                foreach (var it in gr.Items)
                {
                    float rk = RowIn(index++, now);
                    if (it.Bounds.Top >= gr.Bounds.Bottom) continue;
                    var area = Rectangle.Inflate(it.Bounds, P(4), 0);
                    WithEntrance(g, pal, panelDy + (1 - rk) * 8 * S, rk * panelIn, area, () =>
                    {
                        if (it.Style == MetricRenderStyle.TextOnly) DrawTextRow(g, it, pal);
                        else DrawRow(g, it, t, pal);
                    });
                }

                // 折叠时内容先于高度淡出
                float fade = Math.Clamp(gr.BodyFadeTween.Value(now), 0f, 1f);
                if (fade < 0.999f)
                {
                    using var veil = new SolidBrush(Color.FromArgb((int)(255 * (1 - fade)), pal.Bg));
                    g.FillRectangle(veil, Rectangle.Inflate(gr.Bounds, P(4), 0));
                }
                g.Restore(saved);
            }

            WithEntrance(g, pal, panelDy, panelIn, FooterRect, () => DrawFooter(g, pal));
        }

        private float RowIn(int index, long now) => Motion.Out.Eval(Motion.Progress(_entranceStart, PanelInMs * 0.25f + index * RowStaggerMs, RowInMs, now));

        /// <summary>按入场进度位移并「淡入」：先画内容，再盖一层不透明度为 (1-k) 的底色。</summary>
        private void WithEntrance(Graphics g, Palette pal, float dy, float k, Rectangle area, Action draw)
        {
            if (k >= 0.999f && Math.Abs(dy) < 0.1f) { draw(); return; }
            var state = g.Save();
            g.TranslateTransform(0, dy);
            draw();
            if (k < 0.999f)
            {
                using var veil = new SolidBrush(Color.FromArgb((int)(255 * (1 - Math.Clamp(k, 0f, 1f))), pal.Bg));
                g.FillRectangle(veil, area);
            }
            g.Restore(state);
        }

        private void DrawHeader(Graphics g, Palette pal)
        {
            var h = HeaderRect;
            float cy = h.Top + h.Height / 2f;

            // 实时点：每次采样后向外扩散一圈并淡出
            float dx = h.X + P(8) + P(3);
            float r = 3 * S;
            if (LivePulse < 1f)
            {
                float rr = r + 7 * S * LivePulse;
                using var ring = new SolidBrush(Color.FromArgb((int)(140 * (1 - LivePulse)), pal.Accent));
                g.FillEllipse(ring, dx - rr, cy - rr, rr * 2, rr * 2);
            }
            using (var dot = new SolidBrush(pal.Accent)) g.FillEllipse(dot, dx - r, cy - r, r * 2, r * 2);

            int brandX = h.X + P(8) + P(6) + P(8);
            int brandRight = (ShowSeg ? SegRect.X : PinRect.X) - P(6);
            DrawText(g, "LiteMonitor", _fBrand, new Rectangle(brandX, h.Y, Math.Max(0, brandRight - brandX), h.Height), pal.Text,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

            if (ShowSeg)
            {
                float c = SegThumb.Value();
                UIUtils.FillRoundRect(g, SegRect, P(9), pal.SegBg);
                using (var border = new Pen(pal.Line)) DrawRoundRect(g, border, SegRect, P(9));
                var thumb = new Rectangle((int)Math.Round(Lerp(SegComfyRect.X, SegCompactRect.X, c)), SegComfyRect.Y, SegComfyRect.Width, SegComfyRect.Height);
                if (!pal.Dark)
                {
                    using var shadow = new SolidBrush(Color.FromArgb(28, 20, 30, 50));
                    using var sp = UIUtils.RoundRect(new Rectangle(thumb.X, thumb.Y + 1, thumb.Width, thumb.Height), P(7));
                    g.FillPath(shadow, sp);
                }
                UIUtils.FillRoundRect(g, thumb, P(7), pal.SegThumb);
                float cc = Math.Clamp(c, 0f, 1f);
                DrawText(g, LanguageManager.T("Menu.PanelComfy"), _fSmall, SegComfyRect, Blend(pal.Faint, pal.Text, 1 - cc), TextFormatFlags.HorizontalCenter);
                DrawText(g, LanguageManager.T("Menu.PanelCompact"), _fSmall, SegCompactRect, Blend(pal.Faint, pal.Text, cc), TextFormatFlags.HorizontalCenter);
            }

            // 置顶（按下缩到 .88，松开弹回）
            bool pinHover = HoverButton == "pin";
            WithPress(g, PinRect, PinPress.Value(), () =>
            {
                if (TopMost) UIUtils.FillRoundRect(g, PinRect, P(8), Color.FromArgb(34, pal.Accent));
                else if (pinHover) UIUtils.FillRoundRect(g, PinRect, P(8), pal.Hover);
                DrawPinIcon(g, PinRect, TopMost ? pal.Accent : (pinHover ? pal.Text : pal.Muted));
            });

            // 更多
            bool moreHover = HoverButton == "more";
            WithPress(g, MoreRect, MorePress.Value(), () =>
            {
                if (moreHover) UIUtils.FillRoundRect(g, MoreRect, P(8), pal.Hover);
                using var b = new SolidBrush(moreHover ? pal.Text : pal.Muted);
                float mcx = MoreRect.X + MoreRect.Width / 2f, mcy = MoreRect.Y + MoreRect.Height / 2f, dr = 1.6f * S;
                foreach (float off in new[] { -5.5f, 0f, 5.5f })
                    g.FillEllipse(b, mcx + off * S - dr, mcy - dr, dr * 2, dr * 2);
            });
        }

        private static void WithPress(Graphics g, Rectangle r, float press, Action draw)
        {
            if (press <= 0.001f) { draw(); return; }
            float s = 1f - 0.12f * press;
            var state = g.Save();
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            g.TranslateTransform(cx, cy);
            g.ScaleTransform(s, s);
            g.TranslateTransform(-cx, -cy);
            draw();
            g.Restore(state);
        }

        private void DrawGroupHeader(Graphics g, GroupLayoutInfo gr, Palette pal)
        {
            var r = gr.HeaderRect;
            if (HoverGroup == gr) UIUtils.FillRoundRect(g, r, P(8), pal.Hover);
            Color fg = HoverGroup == gr ? pal.Text2 : pal.Faint;

            // 箭头：弹簧旋转（会略微转过头再回来）
            float chevron = gr.ChevronTween.Value();
            float cx = r.X + P(8) + P(6), cy = r.Y + r.Height / 2f + S;
            var saved = g.Save();
            g.TranslateTransform(cx, cy);
            g.RotateTransform(-90f * chevron);
            using (var pen = new Pen(fg, 1.6f * S) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                g.DrawLines(pen, new[] { new PointF(-3.5f * S, -1.8f * S), new PointF(0, 1.8f * S), new PointF(3.5f * S, -1.8f * S) });
            g.Restore(saved);

            string label = StripLeadingSymbols(string.IsNullOrEmpty(gr.Label) ? gr.GroupName : gr.Label);
            int lx = r.X + P(8) + P(12) + P(6);
            DrawText(g, label, _fSmall, new Rectangle(lx, r.Y + P(1), r.Right - lx - P(8), r.Height), fg, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

            // 折叠后在标题行右侧滑入前两项摘要
            float sum = Math.Clamp(gr.SummaryTween.Value(), 0f, 1f);
            if (sum > 0.01f && gr.Items.Count > 0)
            {
                string summary = string.Join(" · ", gr.Items.Take(2).Select(it =>
                {
                    if (it.TextValue != null) return it.GetFormattedText(false);
                    it.GetFormattedText(false);
                    return it.CachedValueText + it.CachedUnitText;
                }));
                int shift = (int)Math.Round((1 - sum) * P(8));
                var sumRect = new Rectangle(lx, r.Y + P(1), r.Right - lx - P(8) + shift, r.Height);
                DrawText(g, summary, _fMono, sumRect, Blend(pal.Bg, pal.Muted, sum), TextFormatFlags.Right);
            }
        }

        private void DrawRow(Graphics g, MetricItem it, Theme t, Palette pal)
        {
            float h = it.HoverAmount;
            bool sel = Selected == it;
            float lift = Math.Max(h, sel ? 1 : 0);
            int dx = -(int)Math.Round(3 * S * lift);
            var row = it.Bounds; row.Offset(dx, 0);

            // 按下时整行缩到 .985
            float press = PressedItem == it ? RowPress.Value() : 0f;
            var state = g.Save();
            if (press > 0.001f)
            {
                float s = 1f - 0.015f * press, cx = row.X + row.Width / 2f, cy = row.Y + row.Height / 2f;
                g.TranslateTransform(cx, cy); g.ScaleTransform(s, s); g.TranslateTransform(-cx, -cy);
            }

            if (sel)
            {
                UIUtils.FillRoundRect(g, row, P(11), pal.Selected);
                using var ring = new Pen(Color.FromArgb(100, pal.Accent), 1f);
                DrawRoundRect(g, ring, Rectangle.Inflate(row, -1, -1), P(10));
            }
            else if (h > 0.01f)
            {
                UIUtils.FillRoundRect(g, row, P(11), Color.FromArgb((int)(255 * h), pal.Hover));
            }

            if (lift > 0.01f) DrawSpark(g, it, t, row, lift);

            string valText = it.GetFormattedText(false);
            string num = it.CachedValueText, unit = it.CachedUnitText;
            if (it.HasCustomUnit) { num = valText; unit = ""; }

            var line = it.LabelRect; line.Offset(dx, 0);
            Color valColor = it.GetTextColor(t);

            // 数值靠右：单位用小号淡色，数字用等宽字体
            int unitW = string.IsNullOrEmpty(unit) ? 0 : MeasureW(g, unit, _fUnit!);
            if (unitW > 0)
                DrawText(g, unit, _fUnit, new Rectangle(line.Right - unitW, line.Y + P(1), unitW, line.Height), pal.Faint, TextFormatFlags.Right);
            var vr = new Rectangle(line.X, line.Y, line.Width - unitW - (unitW > 0 ? P(3) : 0), line.Height);
            DrawText(g, num, _fValue, vr, valColor, TextFormatFlags.Right);
            int valueW = MeasureW(g, num, _fValue!);

            string label = string.IsNullOrEmpty(it.Label) ? it.Key : it.Label;
            var lr = new Rectangle(line.X, line.Y, Math.Max(0, vr.Right - valueW - line.X - P(8)), line.Height);
            DrawText(g, label, _fLabel, lr, pal.Text2, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

            // 过高时标签后的脉冲红点
            if (it.CachedColorState == 2)
            {
                int lw = MeasureW(g, label, _fLabel!);
                float px = line.X + Math.Min(lw, lr.Width) + 10 * S, py = line.Y + line.Height / 2f;
                Color crit = ThemeManager.ParseColor(t.Color.BarHigh);
                float rr = 3 * S + 4 * S * LivePulse;
                using (var ringB = new SolidBrush(Color.FromArgb((int)(150 * (1 - LivePulse)), crit))) g.FillEllipse(ringB, px - rr, py - rr, rr * 2, rr * 2);
                using (var dotB = new SolidBrush(crit)) g.FillEllipse(dotB, px - 3 * S, py - 3 * S, 6 * S, 6 * S);
            }

            // 细进度条：紧凑模式下收起并淡出
            float barAlpha = 1f - Math.Clamp(Compact.Value(), 0f, 1f);
            if (barAlpha > 0.02f && it.BarRect.Width > 0)
            {
                var bar = it.BarRect; bar.Offset(dx, 0);
                int barH = Math.Max(1, (int)Math.Round(bar.Height * barAlpha));
                bar = new Rectangle(bar.X, bar.Y, bar.Width, barH);
                int a = (int)(255 * barAlpha);
                UIUtils.FillRoundRect(g, bar, bar.Height / 2, Color.FromArgb(a, pal.Track));
                int fw = (int)Math.Round(bar.Width * Math.Clamp(it.CachedPercent, 0, 1));
                if (it.CachedPercent > 0) fw = Math.Max(fw, bar.Height);
                if (fw > 0) UIUtils.FillRoundRect(g, new Rectangle(bar.X, bar.Y, fw, bar.Height), bar.Height / 2, Color.FromArgb(a, it.GetBarColor(t)));
            }

            g.Restore(state);
        }

        private void DrawTextRow(Graphics g, MetricItem it, Palette pal)
        {
            string label = string.IsNullOrEmpty(it.Label) ? it.Key : it.Label;
            string text = it.GetFormattedText(false);
            DrawText(g, label, _fLabel, it.LabelRect, pal.Text2, TextFormatFlags.Left);
            DrawText(g, text, _fLabel, it.ValueRect, pal.Text, TextFormatFlags.Right | TextFormatFlags.EndEllipsis);
        }

        private void DrawSpark(Graphics g, MetricItem it, Theme t, Rectangle row, float alpha)
        {
            int n = it.HistoryCount;
            if (n < 2) return;
            float lo = float.MaxValue, hi = float.MinValue;
            for (int i = 0; i < n; i++) { float v = it.HistoryAt(i); lo = Math.Min(lo, v); hi = Math.Max(hi, v); }
            float pad = Math.Max((hi - lo) * 0.25f, Math.Max(Math.Abs(hi) * 0.04f, 0.5f));
            lo -= pad; hi += pad;

            float top = row.Top + 6 * S, bottom = row.Bottom;
            var pts = new PointF[n];
            for (int i = 0; i < n; i++)
                pts[i] = new PointF(row.Left + row.Width * i / (float)(n - 1), bottom - (it.HistoryAt(i) - lo) / (hi - lo) * (bottom - top));

            Color c = it.GetBarColor(t);
            var saved = g.Save();
            using (var clip = UIUtils.RoundRect(row, P(11))) g.SetClip(clip, CombineMode.Intersect);
            using (var path = new GraphicsPath())
            {
                path.AddLines(pts);
                path.AddLine(pts[n - 1].X, bottom, pts[0].X, bottom);
                path.CloseFigure();
                using var fill = new SolidBrush(Color.FromArgb((int)(28 * alpha), c));
                g.FillPath(fill, path);
            }
            using (var pen = new Pen(Color.FromArgb((int)(170 * alpha), c), 1.3f * S) { LineJoin = LineJoin.Round })
                g.DrawLines(pen, pts);
            g.Restore(saved);
        }

        private void DrawFooter(Graphics g, Palette pal)
        {
            var f = FooterRect;
            using (var pen = new Pen(pal.Line2)) g.DrawLine(pen, f.X + P(8), f.Y, f.Right - P(8), f.Y);
            var text = new Rectangle(f.X + P(8), f.Y + P(10), f.Width - P(16), f.Height - P(10));

            string prefix = LanguageManager.T("Menu.PanelUptime");
            int pw = MeasureW(g, prefix + " ", _fSmall!);
            DrawText(g, prefix, _fSmall, text, pal.Faint, TextFormatFlags.Left);
            if (!string.IsNullOrEmpty(Uptime))
                DrawText(g, Uptime, _fMono, new Rectangle(text.X + pw, text.Y, text.Width - pw, text.Height), pal.Muted, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

            double sec = Math.Max(80, RefreshMs) / 1000.0;
            string rate = string.Format(LanguageManager.T("Menu.PanelSampleEvery"), sec.ToString(sec % 1 == 0 ? "0" : "0.#"));
            DrawText(g, rate, _fSmall, text, pal.Faint, TextFormatFlags.Right);
        }

        // =====================================================================
        // 资源与工具
        // =====================================================================
        private void EnsureFonts(Theme t)
        {
            // 主题字体的字号已乘过用户缩放，借它反推出缩放倍数
            float user = t.Font.Item > 0 ? t.FontItem.SizeInPoints / (float)t.Font.Item : 1f;
            if (_fontTheme == t && Math.Abs(_fontScale - user) < 0.001f && _fLabel != null) return;
            DisposeFonts();
            _fontTheme = t;
            _fontScale = user;

            string ui = t.Font.Family;
            string mono = ResolveFamily("Cascadia Mono", string.IsNullOrWhiteSpace(t.Font.ValueFamily) ? "Consolas" : t.Font.ValueFamily);
            // 设计稿字号（px）× 0.75 = pt
            _fBrand = new Font(ui, 10.5f * user, FontStyle.Bold);
            _fSmall = new Font(ui, 8.25f * user);
            _fLabel = new Font(ui, 9.75f * user);
            _fValue = new Font(mono, 11.25f * user);
            _fUnit = new Font(ui, 8.25f * user);
            _fMono = new Font(mono, 8.25f * user);
        }

        internal static string ResolveFamily(string preferred, string fallback)
        {
            try { using var ff = new FontFamily(preferred); return preferred; }
            catch { return fallback; }
        }

        private void DisposeFonts()
        {
            _fBrand?.Dispose(); _fSmall?.Dispose(); _fLabel?.Dispose();
            _fValue?.Dispose(); _fUnit?.Dispose(); _fMono?.Dispose();
            _fBrand = _fSmall = _fLabel = _fValue = _fUnit = _fMono = null;
        }

        public void Dispose() => DisposeFonts();

        private void DrawPinIcon(Graphics g, Rectangle r, Color c)
        {
            // 设计稿图标（24 视图框）：M9 4h6l-1 6 4 3v2H6v-2l4-3z  M12 15v5
            float k = 16 * S / 24f;
            float ox = r.X + (r.Width - 16 * S) / 2f, oy = r.Y + (r.Height - 16 * S) / 2f;
            PointF Pt(float x, float y) => new PointF(ox + x * k, oy + y * k);
            using var pen = new Pen(c, 1.7f * S) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawPolygon(pen, new[] { Pt(9, 4), Pt(15, 4), Pt(14, 10), Pt(18, 13), Pt(18, 15), Pt(6, 15), Pt(6, 13), Pt(10, 10) });
            g.DrawLine(pen, Pt(12, 15), Pt(12, 20));
        }

        private static void DrawRoundRect(Graphics g, Pen pen, Rectangle r, int radius)
        {
            using var path = UIUtils.RoundRect(new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1), radius);
            g.DrawPath(pen, path);
        }

        private static float Lerp(float a, float b, float k) => a + (b - a) * k;

        // GDI 的 TextRenderer 在离屏缓冲上画 ClearType，浅色背景下文字会发粗发糊；
        // 面板（双缓冲）因此统一用 GDI+ DrawString，参数沿用 TextFormatFlags 以便阅读。垂直方向总是居中。
        internal static void DrawText(Graphics g, string text, Font? font, Rectangle r, Color color, TextFormatFlags flags)
        {
            if (string.IsNullOrEmpty(text) || font == null || r.Width <= 0) return;
            if (HasEmoji(text)) { DrawTextWithEmoji(g, text, font, r, color, flags); return; }
            bool ellipsis = (flags & TextFormatFlags.EndEllipsis) != 0;
            using var sf = new StringFormat(StringFormat.GenericTypographic)
            {
                Alignment = (flags & TextFormatFlags.Right) != 0 ? StringAlignment.Far
                          : (flags & TextFormatFlags.HorizontalCenter) != 0 ? StringAlignment.Center : StringAlignment.Near,
                LineAlignment = StringAlignment.Center,
                Trimming = ellipsis ? StringTrimming.EllipsisCharacter : StringTrimming.None,
                FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces | (ellipsis ? 0 : StringFormatFlags.NoClip)
            };
            using var brush = new SolidBrush(color);
            g.DrawString(text, font, brush, r, sf);
        }

        // GDI+ 不会像 GDI 那样自动换字体补字：语言包里的 🔼🔽 等 emoji 在雅黑里没有字形会显示成方框，
        // 所以按「emoji / 普通文字」分段，emoji 段改用 Segoe UI Emoji 绘制。
        private static readonly string EmojiFamily = ResolveFamily("Segoe UI Emoji", "Segoe UI Symbol");

        private static bool HasEmoji(string s)
        {
            foreach (char c in s) if (char.IsSurrogate(c) || (c >= 0x2190 && c <= 0x2BFF)) return true;
            return false;
        }

        private static void DrawTextWithEmoji(Graphics g, string text, Font font, Rectangle r, Color color, TextFormatFlags flags)
        {
            var runs = new List<(string s, bool emoji)>();
            int i = 0;
            while (i < text.Length)
            {
                bool emoji = IsEmojiAt(text, i);
                int start = i;
                while (i < text.Length && IsEmojiAt(text, i) == emoji)
                    i += char.IsHighSurrogate(text[i]) && i + 1 < text.Length ? 2 : 1;
                while (i < text.Length && (text[i] == 0xFE0F || text[i] == 0x200D)) i++; // 变体选择符 / 连接符随前一段
                runs.Add((text.Substring(start, i - start), emoji));
            }

            using var emojiFont = new Font(EmojiFamily, font.Size * 0.92f, FontStyle.Regular, font.Unit);
            var widths = runs.Select(run => MeasureW(g, run.s, run.emoji ? emojiFont : font)).ToList();
            int total = widths.Sum();
            float x = (flags & TextFormatFlags.Right) != 0 ? r.Right - total
                    : (flags & TextFormatFlags.HorizontalCenter) != 0 ? r.X + (r.Width - total) / 2f : r.X;

            using var sf = new StringFormat(StringFormat.GenericTypographic)
            {
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip | StringFormatFlags.MeasureTrailingSpaces
            };
            using var brush = new SolidBrush(color);
            for (int k = 0; k < runs.Count; k++)
            {
                g.DrawString(runs[k].s, runs[k].emoji ? emojiFont : font, brush, new RectangleF(x, r.Y, widths[k] + 2, r.Height), sf);
                x += widths[k];
            }
        }

        private static bool IsEmojiAt(string s, int i) => char.IsSurrogate(s[i]) || (s[i] >= 0x2190 && s[i] <= 0x2BFF);

        internal static int MeasureW(Graphics g, string text, Font font)
        {
            using var sf = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces };
            return (int)Math.Ceiling(g.MeasureString(text, font, PointF.Empty, sf).Width);
        }

        /// <summary>语言包的分组名带 emoji 前缀（如「💻CPU」「ℹ️看板」），设计稿的分组标题只要文字。</summary>
        private static string StripLeadingSymbols(string s)
        {
            int i = 0;
            // 跳过 emoji（代理对）、符号区（如 ℹ U+2139）与变体选择符
            while (i < s.Length && (!char.IsLetterOrDigit(s[i]) || char.IsSurrogate(s[i]) || (s[i] >= 0x2000 && s[i] <= 0x2BFF) || s[i] == 0xFE0F)) i++;
            return i < s.Length ? s.Substring(i) : s;
        }

        private static Color Blend(Color a, Color b, float k)
        {
            k = Math.Clamp(k, 0f, 1f);
            return Color.FromArgb((int)(a.R + (b.R - a.R) * k), (int)(a.G + (b.G - a.G) * k), (int)(a.B + (b.B - a.B) * k));
        }

        /// <summary>从主题推导出设计稿里的各层颜色，任何主题都能套用。</summary>
        private readonly struct Palette
        {
            public readonly Color Bg, Text, Text2, Muted, Faint, Track, Accent, Hover, Selected, Line, Line2, SegBg, SegThumb;
            public readonly bool Dark;

            public Palette(Theme t)
            {
                Bg = ThemeManager.ParseColor(t.Color.Background);
                Text = ThemeManager.ParseColor(t.Color.TextTitle);
                Text2 = ThemeManager.ParseColor(t.Color.TextPrimary);
                Faint = ThemeManager.ParseColor(t.Color.TextGroup);
                Muted = Blend(Faint, Text, 0.25f);
                Track = ThemeManager.ParseColor(t.Color.BarBackground);
                Accent = ThemeManager.ParseColor(t.Color.BarLow);
                Dark = Bg.GetBrightness() < 0.5f;
                Hover = Blend(Bg, Text, Dark ? 0.06f : 0.045f);
                Selected = Blend(Bg, Text, Dark ? 0.09f : 0.06f);
                Line = Blend(Bg, Text, Dark ? 0.11f : 0.12f);
                Line2 = Blend(Bg, Text, Dark ? 0.07f : 0.08f);
                SegBg = Dark ? Blend(Bg, Color.Black, 0.28f) : Blend(Bg, Text, 0.07f);
                SegThumb = Dark ? Blend(Bg, Text, 0.12f) : Color.White;
            }
        }
    }
}
