using LiteMonitor.src.Core;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace LiteMonitor
{
    public static class UIRenderer
    {
        // ★★★ 优化：移除本地画刷缓存，改为调用 UIUtils ★★★
        // private static readonly Dictionary<string, SolidBrush> _brushCache = new();
        // private static SolidBrush GetBrush(string color, Theme t) { ... }

        // [替换] ClearCache 方法
        public static void ClearCache() 
        {
            // 委托给 UIUtils 清理
            UIUtils.ClearBrushCache();
        }

        public static void Render(Graphics g, List<GroupLayoutInfo> groups, Theme t)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // 1. 绘制背景
            // ★★★ [核心修复] 扩大绘制区域，解决左侧和上侧漏黑边的问题 ★★★
            // 原理：从 (-5, -5) 开始画，确保绝对覆盖掉 (0,0) 处的物理像素死角。
            // 多余的部分会被系统自动裁剪，不会有副作用。
            g.FillRectangle(UIUtils.GetBrush(t.Color.Background), 
                new Rectangle(-5, -5, (int)g.VisibleClipBounds.Width + 10, (int)g.VisibleClipBounds.Height + 10));

            // 2. 绘制主标题
            DrawMainTitle(g, t);

            // 3. 绘制各分组
            foreach (var gr in groups)
            {
                DrawGroupBackground(g, gr, t);
                if (gr.Bounds.Height <= 0) continue;

                // 折叠动画过程中，把子项裁剪在正在收缩的组框内
                bool clipping = gr.Bounds.Height < gr.FullBounds.Height;
                var saved = g.Save();
                if (clipping) g.SetClip(gr.Bounds);

                // 遍历子项绘制 (不再区分 NET/DISK，统一由 Item.Style 决定)
                for (int i = 0; i < gr.Items.Count; i++)
                {
                    var it = gr.Items[i];
                    bool isLast = (i == gr.Items.Count - 1);
                    if (clipping && it.Bounds.Top >= gr.Bounds.Bottom) continue;

                    if (it.Style == MetricRenderStyle.TwoColumn)
                        DrawTwoColumnItem(g, it, t);
                    else if (it.Style == MetricRenderStyle.TextOnly) // [新增]
                        DrawTextItem(g, it, t, isLast);
                    else
                        DrawStandardItem(g, it, t);
                }

                g.Restore(saved);
            }
        }

        // TextRenderer 默认忽略 Graphics 的裁剪区，折叠动画需要它遵守
        private const TextFormatFlags Clip = TextFormatFlags.PreserveGraphicsClipping;

        /// <summary>悬停高亮：圆角底色 + 背景里的近 40 秒走势线，随 HoverAmount 淡入淡出。</summary>
        private static void DrawHover(Graphics g, MetricItem it, Theme t)
        {
            float h = it.HoverAmount;
            if (h <= 0.01f || it.Bounds.Width <= 0) return;

            float s = t.Layout.LayoutScale > 0 ? t.Layout.LayoutScale : 1f;
            var rect = Rectangle.Inflate(it.Bounds, -(int)(2 * s), 0);
            Color baseBg = ThemeManager.ParseColor(t.Color.GroupBackground);
            Color ink = ThemeManager.ParseColor(t.Color.TextPrimary);
            Color hover = Blend(baseBg, ink, 0.08f);
            UIUtils.FillRoundRect(g, rect, (int)(8 * s), Color.FromArgb((int)(255 * h), hover));

            int n = it.HistoryCount;
            if (n < 2) return;
            float lo = float.MaxValue, hi = float.MinValue;
            for (int i = 0; i < n; i++) { float v = it.HistoryAt(i); lo = Math.Min(lo, v); hi = Math.Max(hi, v); }
            float pad = Math.Max((hi - lo) * 0.2f, Math.Max(Math.Abs(hi) * 0.05f, 0.5f));
            lo -= pad; hi += pad;

            float top = rect.Top + 4 * s, bottom = rect.Bottom - 2 * s;
            var pts = new PointF[n];
            for (int i = 0; i < n; i++)
            {
                float x = rect.Left + rect.Width * i / (float)(n - 1);
                float y = bottom - (it.HistoryAt(i) - lo) / (hi - lo) * (bottom - top);
                pts[i] = new PointF(x, y);
            }

            Color line = it.GetBarColor(t);
            using (var path = new GraphicsPath())
            {
                path.AddLines(pts);
                path.AddLine(pts[n - 1].X, rect.Bottom, pts[0].X, rect.Bottom);
                path.CloseFigure();
                using var fill = new SolidBrush(Color.FromArgb((int)(26 * h), line));
                g.FillPath(fill, path);
            }
            using var pen = new Pen(Color.FromArgb((int)(150 * h), line), 1.3f * s) { LineJoin = LineJoin.Round };
            g.DrawLines(pen, pts);
        }

        private static Color Blend(Color a, Color b, float k)
        {
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * k),
                (int)(a.G + (b.G - a.G) * k),
                (int)(a.B + (b.B - a.B) * k));
        }

        private static void DrawMainTitle(Graphics g, Theme t)
        {
            string title = LanguageManager.T("Title");
            if (string.IsNullOrEmpty(title) || title == "Title") return;

            // 直接使用字体高度，不需要测量
            int titleH = t.FontTitle.Height;
            
            // ★★★ [优化] 标题下方的微调间距也要随 DPI 缩放 ★★★
            int titlePadding = (int)(4 * t.Layout.LayoutScale);
            
            var titleRect = new Rectangle(t.Layout.Padding, t.Layout.Padding, t.Layout.Width - t.Layout.Padding * 2, titleH + titlePadding);

            TextRenderer.DrawText(g, title, t.FontTitle, titleRect,
                ThemeManager.ParseColor(t.Color.TextTitle),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | Clip);
        }

        private static void DrawGroupBackground(Graphics g, GroupLayoutInfo gr, Theme t)
        {
            int gp = t.Layout.GroupPadding;
            
            // 绘制圆角背景
            UIUtils.FillRoundRect(g, gr.Bounds, t.Layout.GroupRadius, ThemeManager.ParseColor(t.Color.GroupBackground));

            // 绘制组标题 (CPU, GPU...)
            // ★★★ 优化：直接使用缓存的 gr.Label，不再每帧调用 LanguageManager.T ★★★
            string label = string.IsNullOrEmpty(gr.Label) ? gr.GroupName : gr.Label;

            int titleH = t.FontGroup.Height;
            int titleY = gr.Bounds.Y - t.Layout.GroupTitleOffset - titleH;

            // [Fix] 修复 Windows 10 下 Emoji 显示不全的问题
            // Emoji 字符高度通常大于普通字体高度，且 TextRenderer 的 VerticalCenter 可能导致上下被裁剪
            // 解决方案：扩大绘制区域的高度，并保持中心对齐
            int extraH = (int)(8 * t.Layout.LayoutScale);
            
            // Y 向上偏移一半 extraH，高度增加 extraH，这样中心点不变
            var rectTitle = new Rectangle(gr.Bounds.X + gp, 
                System.Math.Max(0, titleY - extraH / 2), 
                gr.Bounds.Width - gp * 2, 
                titleH + extraH);

            Color groupColor = ThemeManager.ParseColor(t.Color.TextGroup);
            TextRenderer.DrawText(g, label, t.FontGroup, rectTitle, groupColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | Clip);

            // 折叠指示：右侧的箭头随展开程度旋转；折叠时在标题行显示前两项的数值摘要
            float s = t.Layout.LayoutScale > 0 ? t.Layout.LayoutScale : 1f;
            float collapse = 1f - Math.Clamp(gr.Expand, 0f, 1f);
            float cx = gr.Bounds.Right - gp - 4 * s;
            float cy = rectTitle.Top + rectTitle.Height / 2f;
            var saved = g.Save();
            g.TranslateTransform(cx, cy);
            g.RotateTransform(-90f * collapse);
            using (var pen = new Pen(Color.FromArgb(170, groupColor), 1.4f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLines(pen, new[] { new PointF(-3.5f * s, -1.8f * s), new PointF(0, 1.8f * s), new PointF(3.5f * s, -1.8f * s) });
            }
            g.Restore(saved);

            if (collapse > 0.01f && gr.Items.Count > 0)
            {
                string summary = string.Join(" · ", gr.Items.Take(2).Select(it => it.GetFormattedText(true)));
                Color bg = ThemeManager.ParseColor(t.Color.Background);
                Color sumColor = Blend(bg, groupColor, collapse);
                var rectSum = new Rectangle(rectTitle.X, rectTitle.Y, rectTitle.Width - (int)(14 * s), rectTitle.Height);
                TextRenderer.DrawText(g, summary, t.FontGroup, rectSum, sumColor,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        /// <summary>
        /// 绘制标准项 (标签 + 数值 + 进度条)
        /// </summary>
        private static void DrawStandardItem(Graphics g, MetricItem it, Theme t)
        {
            if (it.Bounds == Rectangle.Empty) return;
            DrawHover(g, it, t);

            // Label (左对齐)
            // ★★★ 优化：直接使用缓存的 it.Label ★★★
            string label = string.IsNullOrEmpty(it.Label) ? it.Key : it.Label;

            TextRenderer.DrawText(g, label, t.FontItem, it.LabelRect,
                ThemeManager.ParseColor(t.Color.TextPrimary),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | Clip);

            // Value (右对齐)  传入 false 表示竖屏/普通模式
            string valText = it.GetFormattedText(false);
            
            
            Color valColor = it.GetTextColor(t);

            TextRenderer.DrawText(g, valText, t.FontValue, it.ValueRect,
                valColor,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | Clip);

            // Bar - 注意：这里调用的是 UIUtils.DrawBar，它现在已经使用了优化的画刷逻辑
            UIUtils.DrawBar(g, it, t);
        }

        /// <summary>
        /// 绘制双列项 (居中标签 + 居中数值)
        /// </summary>
        private static void DrawTwoColumnItem(Graphics g, MetricItem it, Theme t)
        {
            if (it.Bounds == Rectangle.Empty) return;
            DrawHover(g, it, t);

            // Label (居中顶部)
            string label = string.IsNullOrEmpty(it.Label) ? it.Key : it.Label;
            
            TextRenderer.DrawText(g, label, t.FontItem, it.LabelRect,
                ThemeManager.ParseColor(t.Color.TextPrimary),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPadding | Clip);

            // Value (居中底部)
            // ★★★ [优化]：如果用户使用了自定义单位 (HasCustomUnit)，则跳过窄屏自动精简逻辑 ★★★
            // 原逻辑：if (narrow) valText = FormatHorizontalValue(...)
            bool narrow = t.Layout.Width < 240 * t.Layout.LayoutScale;
            string valText = it.GetFormattedText(narrow);
            


            Color valColor = it.GetTextColor(t);

            TextRenderer.DrawText(g, valText, t.FontValue, it.ValueRect,
                valColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Bottom | TextFormatFlags.NoPadding | Clip);
        }

        // [新增] 绘制纯文本项方法
        private static void DrawTextItem(Graphics g, MetricItem it, Theme t, bool isLast)
        {
            if (it.Bounds == Rectangle.Empty) return;

            // 1. 绘制左侧标签 (IP)
            string label = string.IsNullOrEmpty(it.Label) ? it.Key : it.Label;
            TextRenderer.DrawText(g, label, t.FontItem, it.LabelRect,
                ThemeManager.ParseColor(t.Color.TextPrimary),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | Clip);

            // 2. 绘制右侧数值 (192.168.x.x)
            // ★★★ 修复：直接使用布局计算好的 ValueRect (已修正为全宽) ★★★
            // 优先调用 GetFormattedText 以支持单位显示
            string text = it.GetFormattedText(false);
            
            // ★★★ 修改：字体使用 FontItem (与标签一致)，颜色保持 ValueSafe ★★★
            // [Fix] Use GetTextColor(t) to respect Plugin Color Override
            Color valColor = it.GetTextColor(t);
            TextRenderer.DrawText(g, text, t.FontItem, it.ValueRect,
                valColor, 
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | Clip);

            // ★★★ 修复：补充分割线 (最后一行不画) ★★★
            if (!isLast)
            {
                // 使用半透明的 BarBackground 作为分割线
                Color divColor = Color.FromArgb(100, ThemeManager.ParseColor(t.Color.BarBackground));
                
                // [优化] 使用缓存的 Pen
                var pen = UIUtils.GetPen(divColor, 1);
                
                // 线条稍微缩进一点，更美观
                int lineY = it.Bounds.Bottom - 1;
                g.DrawLine(pen, it.Bounds.Left + 5, lineY, it.Bounds.Right - 5, lineY);
            }
        }

    }
}