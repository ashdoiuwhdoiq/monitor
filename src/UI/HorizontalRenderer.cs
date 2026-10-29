using LiteMonitor.src.Core;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace LiteMonitor
{
    /// <summary>
    /// 横版渲染器（基于列结构绘制），Graphite 风格：
    /// 标签用淡色、数值用主题数值字体并按状态着色，列与列之间有细分隔线。
    /// 布局仍由 HorizontalLayout 计算。
    /// </summary>
    public static class HorizontalRenderer
    {
        public static void Render(Graphics g, Theme t, List<Column> cols, int panelWidth)
        {
            int panelHeight = (int)g.VisibleClipBounds.Height;
            Color bg = ThemeManager.ParseColor(t.Color.Background);
            Color text = ThemeManager.ParseColor(t.Color.TextTitle);
            Color faint = ThemeManager.ParseColor(t.Color.TextGroup);
            Color line = Mix(bg, text, bg.GetBrightness() < 0.5f ? 0.08f : 0.10f);

            using (var bgBrush = new SolidBrush(bg))
                g.FillRectangle(bgBrush, new Rectangle(-5, -5, panelWidth + 10, panelHeight + 10));

            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            for (int i = 0; i < cols.Count; i++)
            {
                var col = cols[i];
                if (col.Bounds == Rectangle.Empty) continue;

                if (col.BoundsTop != Rectangle.Empty && col.Top != null)
                    DrawItem(g, col.Top, col.BoundsTop, t, faint);
                if (col.BoundsBottom != Rectangle.Empty && col.Bottom != null)
                    DrawItem(g, col.Bottom, col.BoundsBottom, t, faint);

                // 列间细分隔线（落在列间距的中线上）
                if (i < cols.Count - 1 && cols[i + 1].Bounds != Rectangle.Empty)
                {
                    float x = (col.Bounds.Right + cols[i + 1].Bounds.Left) / 2f;
                    int inset = col.Bounds.Height / 6;
                    using var pen = new Pen(line);
                    g.DrawLine(pen, x, col.Bounds.Top + inset, x, col.Bounds.Bottom - inset);
                }
            }
        }

        private static void DrawItem(Graphics g, MetricItem it, Rectangle rc, Theme t, Color faint)
        {
            string value = it.GetFormattedText(true);
            Color valColor = it.GetTextColor(t);

            // 隐藏标签（ShortLabel 为空或空格）时仅显示文本，如 IP、看板
            bool hideLabel = string.IsNullOrEmpty(it.ShortLabel) || it.ShortLabel == " ";
            if (hideLabel)
            {
                GraphitePanel.DrawText(g, value, t.FontItem, rc, valColor, TextFormatFlags.Left);
                return;
            }

            string label = !string.IsNullOrEmpty(it.ShortLabel) ? it.ShortLabel : it.Label;
            if (string.IsNullOrEmpty(label)) label = it.Key;

            GraphitePanel.DrawText(g, label, t.FontItem, rc, faint, TextFormatFlags.Left);
            GraphitePanel.DrawText(g, value, t.FontValue, rc, valColor, TextFormatFlags.Right);
        }

        private static Color Mix(Color a, Color b, float k) =>
            Color.FromArgb((int)(a.R + (b.R - a.R) * k), (int)(a.G + (b.G - a.G) * k), (int)(a.B + (b.B - a.B) * k));
    }
}
