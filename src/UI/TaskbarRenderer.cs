using LiteMonitor.src.Core;
using System.Drawing.Text;

namespace LiteMonitor
{
    /// <summary>
    /// 任务栏渲染器（仅负责绘制，不再负责布局）
    /// </summary>
    public static class TaskbarRenderer
    {
        //private static readonly Settings _settings = Settings.Load();
        
        // 字体缓存 - 直接初始化，避免每次渲染都创建字体
        private static Font? _cachedFont = null;

        // 设计稿配色：标签用淡色，正常数值用正文色，偏高琥珀、过高珊瑚红
        // 浅色主题
        private static readonly Color LABEL_LIGHT = ColorTranslator.FromHtml("#555C66");
        private static readonly Color SAFE_LIGHT = ColorTranslator.FromHtml("#15181D");
        private static readonly Color WARN_LIGHT = ColorTranslator.FromHtml("#9A5B00");
        private static readonly Color CRIT_LIGHT = ColorTranslator.FromHtml("#CF3535");

        // 深色主题
        private static readonly Color LABEL_DARK = ColorTranslator.FromHtml("#9AA1AC");
        private static readonly Color SAFE_DARK = ColorTranslator.FromHtml("#E8EAED");
        private static readonly Color WARN_DARK = ColorTranslator.FromHtml("#F2B24C");
        private static readonly Color CRIT_DARK = ColorTranslator.FromHtml("#FF6B6B");

        /// <summary>偏高 / 过高时数值前的呼吸点（设计稿 1.8s / 1.2s 一个周期），由任务栏窗口逐帧传入时间。</summary>
        public static long BreathClock;
        /// <summary>本帧是否画了呼吸点（需要继续逐帧重绘）。</summary>
        public static bool HasBreathing;

        // ★★★ [新增] 自定义颜色缓存 ★★★
        private static bool _useCustom = false;
        private static Color _cLabel, _cSafe, _cWarn, _cCrit;

        // ★★★ [新增] 极简的核心：手动刷新缓存 ★★★
        // 在 UIController 初始化或配置变更时调用它
        public static void ReloadStyle(Settings cfg)
        {     
            var s = cfg.GetStyle();

            // 任务栏字体由本类独占：不能取 UIUtils 的共享缓存，
            // 否则 UIUtils.ClearBrushCache()（重建菜单、切换主题时都会调用）会把它释放掉，
            // 之后 GDI+ 的 DrawString 用到已释放的字体就会抛出 "Parameter is not valid."
            var old = _cachedFont;
            try
            {
                _cachedFont = new Font(s.Font, s.Size, s.Bold ? FontStyle.Bold : FontStyle.Regular);
            }
            catch
            {
                _cachedFont = new Font(SystemFonts.DefaultFont.FontFamily, s.Size, s.Bold ? FontStyle.Bold : FontStyle.Regular);
            }
            try { old?.Dispose(); } catch { }

            // 颜色依然允许自定义
            _useCustom = cfg.TaskbarCustomStyle;
            if (_useCustom)
            {
                try {
                    _cLabel = ColorTranslator.FromHtml(cfg.TaskbarColorLabel);
                    _cSafe = ColorTranslator.FromHtml(cfg.TaskbarColorSafe);
                    _cWarn = ColorTranslator.FromHtml(cfg.TaskbarColorWarn);
                    _cCrit = ColorTranslator.FromHtml(cfg.TaskbarColorCrit);
                } catch {
                    // 容错：如果解析失败，回退到默认
                    _useCustom = false; 
                }
            }
        }

        // 逐像素透明模式下画在 32 位透明位图上：GDI 的 TextRenderer 会丢掉 Alpha，必须改用 GDI+ 灰度抗锯齿
        private static bool _alphaText;

        public static void Render(Graphics g, List<Column> cols, bool light, bool alphaText = false)
        {
            _alphaText = alphaText;
            HasBreathing = false;
            // [防空策略] 万一还没人调用 ReloadStyle，就自己兜底初始化一次
            if (_cachedFont == null)
            {
                // 兜底：读磁盘配置（仅第一次）
                ReloadStyle(Settings.Load());
            }
            
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.TextRenderingHint = alphaText ? TextRenderingHint.AntiAliasGridFit : TextRenderingHint.ClearTypeGridFit;

            // 使用传入的 light 参数，避免每次都查询系统主题瓠
            //bool light = IsSystemLight();

            foreach (var col in cols)
            {
                // ★★★ [新增]：如果只有 Top 没有 Bottom，强制使用全高绘制（居中）
                if (col.Top != null && col.Bottom == null && col.Bounds != Rectangle.Empty)
                {
                    DrawItem(g, col.Top, col.Bounds, light);
                    continue; // 处理完这个特殊情况直接跳过本次循环
                }

                if (col.BoundsTop != Rectangle.Empty && col.Top != null)
                    DrawItem(g, col.Top, col.BoundsTop, light);

                if (col.BoundsBottom != Rectangle.Empty && col.Bottom != null)
                    DrawItem(g, col.Bottom, col.BoundsBottom, light);
            }
        }

        private static void DrawItem(Graphics g, MetricItem item, Rectangle rc, bool light)
        {
            // ★★★ 优化：直接使用缓存的 ShortLabel，避免每帧生成 Key 和查询字典 ★★★
            string label = item.ShortLabel;

            // ★★★ 修复：如果 ShortLabel 被显式设为空格或空，则视为隐藏标签 ★★★
            // 适用于 IP/Dashboard 文本，直接绘制 Value (左对齐)
            bool hideLabel = (string.IsNullOrEmpty(label) || label == " ");

            // 如果不是隐藏，且为空，则回退到 Label 或 Key
            if (!hideLabel)
            {
                if (string.IsNullOrEmpty(label)) label = item.Label;
                if (string.IsNullOrEmpty(label)) label = item.Key;
            }

            string value = item.GetFormattedText(true);

            // 直接使用缓存的字体，不再 new Font
            Font font = _cachedFont!;

            Color labelColor, valueColor;

            // ★★★ [修改] 颜色选择逻辑 ★★★
            if (_useCustom)
            {
                // 自定义模式：忽略系统明暗，强制使用自定义色
                labelColor = _cLabel;
                valueColor = GetCustomStateColor(item.CachedColorState);
            }
            else
            {
                // 原有模式
                labelColor = light ? LABEL_LIGHT : LABEL_DARK;
                valueColor = GetStateColor(item.CachedColorState, light);
            }

            // ★★★ 修复：如果开启了隐藏标签 (如 IP/Dashboard)，则仅绘制 Value (左对齐) ★★★
            if (hideLabel)
            {
                DrawText(g, value, font, rc, valueColor, false);
                return;
            }

            DrawText(g, label, font, rc, labelColor, false); // Label 左对齐
            DrawText(g, value, font, rc, valueColor, true);  // Value 右对齐

            // 偏高 / 过高：数值左侧的呼吸点（透明度 1→.35、大小 1→.7 往复）
            int state = item.CachedColorState;
            if (state > 0 && _alphaText)
            {
                HasBreathing = true;
                float period = state == 2 ? 1200f : 1800f;
                float ph = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * (BreathClock % (long)period) / period));
                float a = 1 - 0.65f * ph, sc = 1 - 0.3f * ph;
                float vw = g.MeasureString(value, font, PointF.Empty, StringFormat.GenericTypographic).Width;
                float d = 5f * UIUtils.ScaleFactor * sc;
                float cx = rc.Right - vw - 6f * UIUtils.ScaleFactor, cy = rc.Y + rc.Height / 2f;
                using var b = new SolidBrush(Color.FromArgb((int)(255 * a), valueColor));
                var sm = g.SmoothingMode;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.FillEllipse(b, cx - d / 2, cy - d / 2, d, d);
                g.SmoothingMode = sm;
            }
        }

        private static void DrawText(Graphics g, string text, Font font, Rectangle rc, Color color, bool alignRight)
        {
            if (!_alphaText)
            {
                TextRenderer.DrawText(g, text, font, rc, color,
                    (alignRight ? TextFormatFlags.Right : TextFormatFlags.Left) |
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoClipping);
                return;
            }

            // 与面板共用 GDI+ 绘制：语言包里的 🔼🔽 等 emoji 自动换用 Segoe UI Emoji，不再显示成方框
            GraphitePanel.DrawText(g, text, font, rc, color, alignRight ? TextFormatFlags.Right : TextFormatFlags.Left);
        }
        // [新增] 辅助：根据状态快速获取颜色 (替代原来的 PickColor)
        private static Color GetStateColor(int state, bool light)
        {
            if (state == 2) return light ? CRIT_LIGHT : CRIT_DARK;
            if (state == 1) return light ? WARN_LIGHT : WARN_DARK;
            return light ? SAFE_LIGHT : SAFE_DARK;
        }

        // [新增] 辅助：自定义模式
        private static Color GetCustomStateColor(int state)
        {
            if (state == 2) return _cCrit;
            if (state == 1) return _cWarn;
            return _cSafe;
        }
    }
}
