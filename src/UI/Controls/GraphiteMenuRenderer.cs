using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using LiteMonitor.src.Core;

namespace LiteMonitor.src.UI.Controls
{
    /// <summary>
    /// 右键菜单与托盘菜单的 Graphite 外观：跟随 UIColors 的深浅色板，圆角选中块、细分隔线、自绘勾选标记。
    /// 通过 ToolStripManager.Renderer 全局生效，子菜单一并套用。
    /// </summary>
    public class GraphiteMenuRenderer : ToolStripProfessionalRenderer
    {
        public GraphiteMenuRenderer() : base(new GraphiteColorTable())
        {
            RoundedEdges = false;
        }

        /// <summary>按当前 UIColors 重新设置全局菜单渲染器。</summary>
        public static void Install()
        {
            ToolStripManager.Renderer = new GraphiteMenuRenderer();
        }

        protected override void Initialize(ToolStrip toolStrip)
        {
            base.Initialize(toolStrip);
            toolStrip.ForeColor = UIColors.TextMain;
            if (toolStrip is ToolStripDropDown dd)
            {
                dd.Padding = new Padding(UIUtils.S(4), UIUtils.S(4), UIUtils.S(4), UIUtils.S(4));
                dd.HandleCreated += (s, e) => ApplyWin11Corners(dd);
                if (dd.IsHandleCreated) ApplyWin11Corners(dd);
            }
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var b = new SolidBrush(UIColors.CardBg);
            e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var p = new Pen(UIColors.Border);
            var r = e.AffectedBounds;
            e.Graphics.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            // 不画左侧灰色图标栏，整块菜单保持同一底色
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            var r = new Rectangle(UIUtils.S(2), 1, e.Item.Width - UIUtils.S(4), e.Item.Height - 2);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            UIUtils.FillRoundRect(e.Graphics, r, UIUtils.S(6), UIColors.NavSelected);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? UIColors.TextMain : UIColors.Muted;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using var p = new Pen(UIColors.Divider);
            e.Graphics.DrawLine(p, UIUtils.S(10), y, e.Item.Width - UIUtils.S(10), y);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = UIColors.TextSub;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var r = e.ImageRectangle;
            int size = Math.Min(r.Width, r.Height);
            var box = new Rectangle(r.X + (r.Width - size) / 2, r.Y + (r.Height - size) / 2, size, size);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            UIUtils.FillRoundRect(g, box, UIUtils.S(4), Color.FromArgb(40, UIColors.Primary));
            using var pen = new Pen(UIColors.Primary, Math.Max(1.6f, UIUtils.S(1.8f))) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            float x = box.X, y = box.Y, w = box.Width;
            g.DrawLines(pen, new[]
            {
                new PointF(x + w * 0.26f, y + w * 0.52f),
                new PointF(x + w * 0.44f, y + w * 0.70f),
                new PointF(x + w * 0.76f, y + w * 0.32f)
            });
        }

        private static void ApplyWin11Corners(Control c)
        {
            try
            {
                int pref = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(c.Handle, 33, ref pref, sizeof(int));
            }
            catch { }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private class GraphiteColorTable : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => UIColors.CardBg;
            public override Color MenuBorder => UIColors.Border;
            public override Color MenuItemBorder => Color.Transparent;
            public override Color MenuItemSelected => UIColors.NavSelected;
            public override Color MenuItemSelectedGradientBegin => UIColors.NavSelected;
            public override Color MenuItemSelectedGradientEnd => UIColors.NavSelected;
            public override Color MenuItemPressedGradientBegin => UIColors.NavSelected;
            public override Color MenuItemPressedGradientEnd => UIColors.NavSelected;
            public override Color ImageMarginGradientBegin => UIColors.CardBg;
            public override Color ImageMarginGradientMiddle => UIColors.CardBg;
            public override Color ImageMarginGradientEnd => UIColors.CardBg;
            public override Color SeparatorDark => UIColors.Divider;
            public override Color SeparatorLight => UIColors.Divider;
            public override Color CheckBackground => Color.Transparent;
            public override Color CheckSelectedBackground => Color.Transparent;
            public override Color CheckPressedBackground => Color.Transparent;
        }
    }
}
