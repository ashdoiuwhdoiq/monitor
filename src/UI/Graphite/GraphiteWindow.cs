using LiteMonitor.src.Core;
using LiteMonitor.src.UI.Controls;
using System;
using System.Drawing;
using System.Windows.Forms;

namespace LiteMonitor.src.UI.Graphite
{
    /// <summary>
    /// 设计稿窗口的外壳：按设计稿尺寸（96DPI 像素）开窗，标题栏跟随深浅模式，
    /// 承载一块铺满的自绘画布；程序切换深浅模式时先存下旧画面再以圆形揭开过渡。
    /// </summary>
    public class GraphiteWindow : Form
    {
        protected readonly GCanvas Canvas;

        public GraphiteWindow(GCanvas canvas, string title, int designW, int designH)
        {
            UIUtils.ScaleFactor = DeviceDpi / 96f;
            Canvas = canvas;
            Text = title;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            ShowInTaskbar = true;
            BackColor = Gx.Current.Win;
            ClientSize = new Size(UIUtils.S(designW), UIUtils.S(designH));
            try { Icon = Properties.Resources.AppIcon; } catch { }
            UIColors.ApplyTitleBar(this);

            canvas.Dock = DockStyle.Fill;
            Controls.Add(canvas);

            UIModeHub.BeforeChange += OnBeforeModeChange;
            UIModeHub.Changed += OnModeChanged;
            FormClosed += (_, __) =>
            {
                UIModeHub.BeforeChange -= OnBeforeModeChange;
                UIModeHub.Changed -= OnModeChanged;
            };
        }

        private void OnBeforeModeChange(object? source, Point? screenOrigin)
        {
            if (IsDisposed || !Visible) return;
            Point origin = screenOrigin.HasValue ? Canvas.PointToClient(screenOrigin.Value) : new Point(Canvas.Width / 2, Canvas.Height / 2);
            Canvas.BeginReveal(origin);
        }

        private void OnModeChanged()
        {
            if (IsDisposed) return;
            BackColor = Gx.Current.Win;
            UIColors.ApplyTitleBar(this);
            // 标题栏深浅只在重绘非客户区时更新：轻推一下窗口状态
            try { var s = Size; Size = new Size(s.Width, s.Height + 1); Size = s; } catch { }
            Canvas.Invalidate();
        }
    }

    /// <summary>窗口画布共用的小部件：明暗切换按钮（太阳 / 月亮旋入旋出）。</summary>
    public abstract class GraphiteCanvas : GCanvas
    {
        /// <summary>画明暗按钮并登记点击：点击后整个程序在深色 / 浅色之间切换，从按钮处圆形揭开。</summary>
        protected void ModeButton(Graphics g, Rectangle r)
        {
            bool hot = Hot(r);
            float press = A("modebtn.press", IsPressed("modebtn") ? 1 : 0, IsPressed("modebtn") ? 120 : 380, IsPressed("modebtn") ? Motion.Out : Motion.Spring);
            var st = g.Save();
            float s = 1 - 0.12f * press, cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            g.TranslateTransform(cx, cy); g.ScaleTransform(s, s); g.TranslateTransform(-cx, -cy);
            if (hot) GxDraw.Round(g, r, S(9), Tk.Hover);
            Color c = hot ? Tk.Text : Tk.Muted;
            // 太阳 / 月亮：一个旋转放大进入，一个旋转缩小离开（.6s 弹簧）
            float light = A("modebtn.light", Tk.Dark ? 0 : 1, 600, Motion.Spring);
            DrawRotIcon(g, "moon", r, c, 1 - light, 90 * light);
            DrawRotIcon(g, "sun", r, c, light, -90 * (1 - light));
            g.Restore(st);

            Hit("modebtn", r, () =>
            {
                string next = Tk.Dark ? "light" : "dark";
                var screen = PointToScreen(new Point(r.X + r.Width / 2, r.Y + r.Height / 2 - (Scrollable ? ScrollY : 0)));
                if (UIModeHub.Apply != null) UIModeHub.Request(next, this, screen);
                else
                {
                    BeginReveal(new Point(r.X + r.Width / 2, r.Y + r.Height / 2));
                    UIColors.Apply(next);
                    Invalidate();
                }
            });
        }

        private void DrawRotIcon(Graphics g, string kind, Rectangle r, Color c, float vis, float angle)
        {
            if (vis <= 0.02f) return;
            var st = g.Save();
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, sc = 0.4f + 0.6f * Math.Clamp(vis, 0, 1.2f);
            g.TranslateTransform(cx, cy); g.RotateTransform(angle); g.ScaleTransform(sc, sc); g.TranslateTransform(-cx, -cy);
            float isz = S(17);
            GxDraw.Icon(g, kind, new RectangleF(cx - isz / 2, cy - isz / 2, isz, isz), Color.FromArgb((int)(255 * Math.Clamp(vis, 0, 1)), c));
            g.Restore(st);
        }

        /// <summary>分段控件（带弹簧滑块，.5s），返回被点击的索引通过回调。</summary>
        protected void Seg(Graphics g, string id, Rectangle r, string[] labels, int selected, Action<int> pick, Font? font = null)
        {
            float thumb = A(id + ".thumb", selected, 500, Motion.Spring);
            var segs = GxDraw.Segmented(g, r, labels, thumb, selected, Tk, font ?? Gx.F(12));
            for (int i = 0; i < segs.Length; i++)
            {
                int k = i;
                Hit(id + "." + i, segs[i], () => { if (k != selected) pick(k); });
            }
        }
    }
}
