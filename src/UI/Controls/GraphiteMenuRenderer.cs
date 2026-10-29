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

        // ===== 弹出动效：菜单淡入并下滑到位，菜单项依次浮现（设计稿：每项间隔 25ms，.32s 缓出）=====
        private const float FadeMs = 160, ItemMs = 320, ItemStaggerMs = 25, SlidePx = 6;
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ToolStripDropDown, OpenState> _open = new();
        private static readonly System.Collections.Generic.List<ToolStripDropDown> _animating = new();
        private static readonly System.Windows.Forms.Timer _animTimer = CreateTimer();

        private sealed class OpenState { public long Start; public Point Target; }

        private static System.Windows.Forms.Timer CreateTimer()
        {
            var t = new System.Windows.Forms.Timer { Interval = 15 };
            t.Tick += (_, __) =>
            {
                long now = LiteMonitor.Motion.Now;
                for (int i = _animating.Count - 1; i >= 0; i--)
                {
                    var dd = _animating[i];
                    if (dd.IsDisposed || !dd.Visible || !_open.TryGetValue(dd, out var st)) { _animating.RemoveAt(i); continue; }
                    long el = now - st.Start;
                    float fade = Math.Clamp(el / FadeMs, 0f, 1f);
                    float slide = LiteMonitor.Motion.Spring.Eval(Math.Clamp(el / 380f, 0f, 1f));
                    try
                    {
                        dd.Opacity = fade;
                        dd.Location = new Point(st.Target.X, st.Target.Y - (int)Math.Round((1 - slide) * UIUtils.S((int)SlidePx)));
                    }
                    catch { }
                    dd.Invalidate();
                    if (el > ItemStaggerMs * dd.Items.Count + ItemMs + 40 && el > 400) _animating.RemoveAt(i);
                }
                if (_animating.Count == 0) t.Stop();
            };
            return t;
        }

        private static float ItemProgress(ToolStripItem item)
        {
            if (item.Owner is not ToolStripDropDown dd || !_open.TryGetValue(dd, out var st)) return 1f;
            int index = dd.Items.IndexOf(item);
            return LiteMonitor.Motion.Out.Eval(LiteMonitor.Motion.Progress(st.Start, index * ItemStaggerMs, ItemMs, LiteMonitor.Motion.Now));
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

                dd.Opening += (s, e) => { try { dd.Opacity = 0; } catch { } };
                dd.Opened += (s, e) =>
                {
                    var st = _open.GetOrCreateValue(dd);
                    st.Start = LiteMonitor.Motion.Now;
                    st.Target = dd.Location;
                    if (!_animating.Contains(dd)) _animating.Add(dd);
                    _animTimer.Start();
                };
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
            float k = ItemProgress(e.Item);
            Color c = e.Item.Enabled ? UIColors.TextMain : UIColors.Muted;
            e.TextColor = Mix(UIColors.CardBg, c, k);
            var r = e.TextRectangle;
            r.Offset(0, (int)Math.Round((1 - k) * UIUtils.S(6)));
            e.TextRectangle = r;
            base.OnRenderItemText(e);

            // 设计稿里可开关的菜单项画成右侧的开关
            if (e.Item is ToolStripMenuItem mi && mi.CheckOnClick)
                DrawSwitch(e.Graphics, mi, k);
        }

        private static void DrawSwitch(Graphics g, ToolStripMenuItem mi, float k)
        {
            int w = UIUtils.S(30), h = UIUtils.S(18);
            var track = new Rectangle(mi.Width - UIUtils.S(14) - w, (mi.Height - h) / 2 + (int)Math.Round((1 - k) * UIUtils.S(6)), w, h);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color on = UIColors.Primary, off = UIColors.LineSoft;
            UIUtils.FillRoundRect(g, track, h / 2, Mix(UIColors.CardBg, mi.Checked ? on : off, k));
            int d = h - UIUtils.S(4);
            int x = mi.Checked ? track.Right - d - UIUtils.S(2) : track.X + UIUtils.S(2);
            using var knob = new SolidBrush(Mix(UIColors.CardBg, Color.White, k));
            g.FillEllipse(knob, x, track.Y + UIUtils.S(2), d, d);
        }

        private static Color Mix(Color a, Color b, float k)
        {
            k = Math.Clamp(k, 0f, 1f);
            return Color.FromArgb((int)(a.R + (b.R - a.R) * k), (int)(a.G + (b.G - a.G) * k), (int)(a.B + (b.B - a.B) * k));
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
            if (e.Item is ToolStripMenuItem mi && mi.CheckOnClick) return; // 已画成右侧开关
            if (ItemProgress(e.Item) < 0.5f) return;
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
