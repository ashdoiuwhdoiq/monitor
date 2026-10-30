using LiteMonitor.src.Core;
using LiteMonitor.src.UI.Helpers;
using LiteMonitor.src.UI.Graphite;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using static LiteMonitor.src.UI.Helpers.NativeMethods;

namespace LiteMonitor
{
    public class TaskbarForm : Form
    {
        private readonly Settings _cfg;
        private readonly UIController _ui;
        private readonly MainForm _mainForm;
        private readonly System.Windows.Forms.Timer _timer = new();

        // ★★★ 双助手架构 ★★★
        private readonly TaskbarWinHelper _winHelper;
        private readonly TaskbarBizHelper _bizHelper;
        
        private HorizontalLayout _layout;
        private List<Column>? _cols;
        private ContextMenuStrip? _currentMenu;
        private DateTime _lastFindHandleTime = DateTime.MinValue;
        private string _lastLayoutSignature = "";
        // ★ 设计稿交互：悬停高亮胶囊、呼吸点、单击切换单/双行（交叉淡入）、悬停浮卡
        private readonly System.Windows.Forms.Timer _fx = new() { Interval = 33 };
        private readonly System.Windows.Forms.Timer _clickTimer = new();
        private readonly Tween _pillX = new(0), _pillW = new(0), _pillA = new(0), _swap = new(1);
        private int _hoverCol = -1;
        private long _hoverSince;
        private long _leftAt = -1;
        private Bitmap? _frameBmp, _contentBmp, _swapFrom; // 逐帧复用，不再每帧新建两张整条位图再复制一份
        private readonly LiteMonitor.src.UI.Graphite.VsyncPump _pump;
        private long _lastContentAt;
        private TaskbarFlyout? _flyout;
        
        // 公开属性
        public string TargetDevice { get; private set; } = "";

        // 判断菜单是否打开
        public bool IsMenuOpen => _currentMenu != null && !_currentMenu.IsDisposed && _currentMenu.Visible;
        
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_RBUTTONUP = 0x0205;
        private const int WM_LBUTTONDBLCLK = 0x0203;
        private bool _isWin11;

        public TaskbarForm(Settings cfg, UIController ui, MainForm mainForm)
        {
            _cfg = cfg;
            _ui = ui;
            _mainForm = mainForm;
            TargetDevice = _cfg.TaskbarMonitorDevice;

            _isWin11 = Environment.OSVersion.Version >= new Version(10, 0, 22000);

            // 初始化组件
            _winHelper = new TaskbarWinHelper(this) { PerPixelAlpha = _cfg.TaskbarPerPixelAlpha };
            _bizHelper = new TaskbarBizHelper(this, _cfg, _winHelper);

            // 窗体属性
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            ControlBox = false;
            TopMost = false;
            DoubleBuffered = true;

            ReloadLayout();

            _bizHelper.CheckTheme(true);
            _bizHelper.FindHandles();
            
            _bizHelper.AttachToTaskbar();
            _winHelper.ApplyLayeredStyle(_bizHelper.TransparentKey, _cfg.TaskbarClickThrough);

            _timer.Interval = Math.Max(_cfg.RefreshMs, 60);
            _timer.Tick += (_, __) => Tick();
            _timer.Start();

            _fx.Tick += (_, __) => FxTick();
            _fx.Start();
            // 悬停 / 动画期间按显示器刷新率出帧（165Hz 屏即 165fps），平时只靠 30fps 轮询
            _pump = new LiteMonitor.src.UI.Graphite.VsyncPump(this, FxTick);
            _clickTimer.Interval = Math.Max(SystemInformation.DoubleClickTime, 200);
            _clickTimer.Tick += (_, __) => { _clickTimer.Stop(); ToggleLines(); };

            Tick();
        }

        public void ReloadLayout()
        {
            _layout = new HorizontalLayout(ThemeManager.Current, 300, LayoutMode.Taskbar, _cfg);
            _lastLayoutSignature = ""; // 重置签名，强制重算
            if (_winHelper.PerPixelAlpha != _cfg.TaskbarPerPixelAlpha)
            {
                // 设置里切换了透明方式：切到逐像素前先清掉颜色键状态
                _winHelper.PerPixelAlpha = _cfg.TaskbarPerPixelAlpha;
                _layeredResetTried = false;
                if (_winHelper.PerPixelAlpha && IsHandleCreated) _winHelper.ResetLayeredState();
            }
            _winHelper.ApplyLayeredStyle(_bizHelper.TransparentKey, _cfg.TaskbarClickThrough);
            _bizHelper.CheckTheme(true);
            _timer.Interval = Math.Max(_cfg.RefreshMs, 60); // 设置里改了采样间隔时立即生效

            if (!_cfg.TaskbarHoverShowAll) _flyout?.HideFlyout();

            // 注意：这里仍然可能因为 _cols 为空而暂时不 Build，
            // 但随后的 Tick 会在获取到新数据后自动 Build
            if (_cols != null && _cols.Count > 0)
            {
                _layout.Build(_cols, _bizHelper.Height);
                Width = _layout.PanelWidth;
                _bizHelper.UpdatePlacement(Width);
            }
            Redraw();
        }

        /// <summary>
        /// 重绘任务栏条。逐像素透明模式下直接把带 Alpha 的位图提交给系统；
        /// 提交失败则永久退回原来的颜色键 + OnPaint 方式，保证任务栏条始终能显示。
        /// </summary>
        /// <param name="content">是否重画文字层。高亮胶囊滑动等纯动画帧只合成缓存好的文字层，开销很小。</param>
        private void Redraw(bool content = true)
        {
            if (!_winHelper.PerPixelAlpha || !IsHandleCreated || Width <= 0 || Height <= 0)
            {
                Invalidate();
                return;
            }

            var bmp = Reuse(ref _frameBmp);
            bool rendered;
            try
            {
                using var g = Graphics.FromImage(bmp);
                // Alpha=1 的底色肉眼不可见，但让整条区域都能接收鼠标（悬停浮窗、右键菜单）
                g.Clear(Color.FromArgb(1, 0, 0, 0));
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                DrawPill(g);
                bool fresh = _contentBmp == null || _contentBmp.Width != Width || _contentBmp.Height != Height;
                var layer = Reuse(ref _contentBmp);
                if (fresh || content)
                {
                    _lastContentAt = Motion.Now;
                    using (var cg = Graphics.FromImage(layer))
                    {
                        cg.Clear(Color.Transparent);
                        cg.SmoothingMode = SmoothingMode.HighQuality;
                        cg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        TaskbarRenderer.BreathClock = Motion.Now;
                        if (_cols != null) TaskbarRenderer.Render(cg, _cols, _bizHelper.LastIsLightTheme, alphaText: true);
                    }
                }
                {
                    float p = _swap.Value();
                    if (_swapFrom != null && p < 1)
                    {
                        // 单/双行切换：旧画面上移 10px 淡出，新画面从下方 10px 淡入（.55s）
                        float k = Math.Clamp(p, 0, 1);
                        DrawFaded(g, _swapFrom, -10 * k * UIUtils.ScaleFactor, 1 - k);
                        DrawFaded(g, layer, 10 * (1 - k) * UIUtils.ScaleFactor, k);
                    }
                    else
                    {
                        g.DrawImageUnscaled(layer, 0, 0);
                    }
                }
                rendered = true;
            }
            catch (Exception ex)
            {
                // 新绘制方式出任何问题都不能让程序崩溃：记一笔，然后退回原来的方式
                System.Diagnostics.Debug.WriteLine($"[TaskbarForm] 逐像素绘制失败，退回颜色键: {ex.Message}");
                rendered = false;
            }

            if (rendered && _winHelper.UpdateLayered(bmp)) return;

            if (rendered && !_layeredResetTried)
            {
                _layeredResetTried = true;
                _winHelper.ResetLayeredState();
                if (_winHelper.UpdateLayered(bmp)) return;
            }

            _winHelper.PerPixelAlpha = false;
            _winHelper.ApplyLayeredStyle(_bizHelper.TransparentKey, _cfg.TaskbarClickThrough);
            Invalidate();
        }

        private bool _layeredResetTried;

        private Bitmap Reuse(ref Bitmap? b)
        {
            if (b == null || b.Width != Width || b.Height != Height)
            {
                b?.Dispose();
                b = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            }
            return b;
        }

        private static void DrawFaded(Graphics g, Bitmap bmp, float dy, float alpha)
        {
            if (alpha <= 0.01f) return;
            using var ia = new ImageAttributes();
            ia.SetColorMatrix(new ColorMatrix { Matrix33 = Math.Clamp(alpha, 0, 1) });
            g.DrawImage(bmp, new Rectangle(0, (int)Math.Round(dy), bmp.Width, bmp.Height), 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel, ia);
        }

        /// <summary>悬停高亮：圆角胶囊在列之间弹簧滑动（.45s，略过冲），移出时淡出。</summary>
        private void DrawPill(Graphics g)
        {
            float a = _pillA.Value();
            if (a <= 0.01f || _pillW.Value() < 2) return;
            bool light = _bizHelper.LastIsLightTheme;
            int alpha = (int)Math.Round(Math.Clamp(a, 0, 1) * (light ? 18 : 22));
            float pad = 4 * UIUtils.ScaleFactor;
            var r = new RectangleF(_pillX.Value() - pad, pad, _pillW.Value() + pad * 2, Height - pad * 2);
            using var path = UIUtils.RoundRect(Rectangle.Round(r), (int)(8 * UIUtils.ScaleFactor));
            using var b = new SolidBrush(Color.FromArgb(alpha, light ? Color.Black : Color.White));
            g.FillPath(b, path);
        }

        private int ColumnAt(Point client)
        {
            if (_cols == null || !ClientRectangle.Contains(client)) return -1;
            bool vertical = _bizHelper.IsVertical();
            for (int i = 0; i < _cols.Count; i++)
            {
                var b = _cols[i].Bounds;
                if (vertical ? b.Contains(client) : client.X >= b.Left - 6 && client.X <= b.Right + 6) return i;
            }
            return -1;
        }

        /// <summary>
        /// 帧循环：平时 30fps 轮询指针（穿透模式也能悬停）、画呼吸点；指针在条上或有动画时改由垂直同步驱动，
        /// 高亮胶囊、单双行切换与浮卡跟手。没有任何变化时只做一次坐标判断，不重绘。
        /// </summary>
        private void FxTick()
        {
            if (IsDisposed || !IsHandleCreated || _cols == null) return;
            long now = Motion.Now;
            int col = Visible ? ColumnAt(PointToClient(Cursor.Position)) : -1;
            bool dirty = false;

            if (col != _hoverCol)
            {
                if (col >= 0)
                {
                    var b = _cols[col].Bounds;
                    var ease = new Ease(.34, 1.3, .64, 1);
                    if (_hoverCol < 0 && _pillA.Value() < 0.05f) { _pillX.Snap(b.X); _pillW.Snap(b.Width); }
                    else { _pillX.Set(b.X, 450, ease); _pillW.Set(b.Width, 450, ease); }
                    _pillA.Set(1, 200, Motion.Out);
                    if (_hoverCol < 0 && (_flyout == null || !_flyout.Visible)) _hoverSince = now;
                    _leftAt = -1;
                }
                else
                {
                    _pillA.Set(0, 250, Motion.Out);
                    _leftAt = now;
                }
                _hoverCol = col;
                dirty = true;
                if (col >= 0 && _flyout != null && _flyout.Visible) ShowFlyout(col);
            }

            // 浮卡：停留 400ms 出现；离开 140ms 后收起（经过列间缝隙时不闪）
            if (_cfg.TaskbarHoverShowAll && _hoverCol >= 0 && now - _hoverSince >= 400 && (_flyout == null || !_flyout.Visible || _flyoutCol != _hoverCol))
                ShowFlyout(_hoverCol);
            if (_hoverCol < 0 && _leftAt >= 0 && now - _leftAt >= 140) { _flyout?.HideFlyout(); _flyoutCol = -1; _leftAt = -1; }

            bool animating = _pillX.IsRunning(now) || _pillW.IsRunning(now) || _pillA.IsRunning(now) || _swap.IsRunning(now);
            // 呼吸点要重画文字层，限制在 30fps 以内即可
            bool breath = TaskbarRenderer.HasBreathing && now - _lastContentAt >= 33;
            if (dirty || animating || breath)
                Redraw(content: breath);
            if (_swapFrom != null && !_swap.IsRunning(now)) { _swapFrom.Dispose(); _swapFrom = null; Redraw(content: false); }
            if (animating || _hoverCol >= 0) _pump.Start(); else _pump.Stop();
        }

        private int _flyoutCol = -1;
        private void ShowFlyout(int col)
        {
            if (_cols == null || col < 0 || col >= _cols.Count || !_cfg.TaskbarHoverShowAll) return;
            var c = _cols[col];
            var first = c.Top ?? c.Bottom;
            if (first == null) return;
            string title = LanguageManager.T(first.BoundConfig?.CachedGroupsKey ?? "");
            if (string.IsNullOrEmpty(title) || title.StartsWith("Groups.")) title = first.Key.Split('.')[0];
            int s0 = 0;
            while (s0 < title.Length && !char.IsLetterOrDigit(title[s0])) s0++;
            title = title.Substring(s0).Trim();
            _flyout ??= new TaskbarFlyout();
            _flyout.ShowFor(c.Top, c.Bottom, title, RectangleToScreen(c.Bounds), RectangleToScreen(ClientRectangle), !_bizHelper.LastIsLightTheme);
            _flyoutCol = col;
        }

        /// <summary>单击任务栏条：在单行 / 双行之间切换，旧画面淡出上移、新画面淡入（.55s）。</summary>
        private void ToggleLines()
        {
            if (_contentBmp == null) return;
            _swapFrom?.Dispose();
            _swapFrom = (Bitmap)_contentBmp.Clone();
            _cfg.TaskbarSingleLine = !_cfg.TaskbarSingleLine;
            try { _cfg.Save(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[TaskbarForm] " + ex.Message); }
            _ui.RebuildTaskbarColumns();
            _lastLayoutSignature = "";
            _swap.Snap(0);
            _swap.Set(1, 550, Motion.InOut);
            _flyout?.HideFlyout();
            _flyoutCol = -1;
            Tick();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _winHelper?.RestoreTaskbar();
                _timer.Stop();
                _timer.Dispose();
                _currentMenu?.Dispose();
                _fx.Dispose();
                _pump.Dispose();
                _clickTimer.Dispose();
                _flyout?.Dispose();
                _frameBmp?.Dispose();
                _contentBmp?.Dispose();
                _swapFrom?.Dispose();
            }
            base.Dispose(disposing);
        }

        protected override void WndProc(ref Message m)
        {
            // [Fix] 兼容性修复：在 Win11 25H2 + StartAllBack 环境下，
            // 右键事件会穿透到原生任务栏。
            // 因此不再区分系统版本，统一拦截右键按下和抬起消息。
            if (m.Msg == WM_RBUTTONDOWN)
            {
                return; // 吞掉按下事件，防止穿透
            }

            if (m.Msg == WM_RBUTTONUP)
            {
                this.BeginInvoke(new Action(ShowContextMenu));
                return; 
            }

            // [Fix] 强制拦截双击事件
            // 当悬浮窗(Tooltip)显示时，WinForms 的标准双击事件可能因为焦点/激活状态的微妙变化而失效。
            // 这里直接在消息层处理 WM_LBUTTONDBLCLK，确保双击动作始终能被触发。
            if (m.Msg == WM_LBUTTONDBLCLK)
            {
                _clickTimer.Stop(); // 双击不触发单击的单/双行切换
                _bizHelper.HandleDoubleClick(_mainForm, _ui);
                return;
            }

            base.WndProc(ref m);
        }

        private void ShowContextMenu()
        {
            if (_currentMenu != null)
            {
                _currentMenu.Dispose();
                _currentMenu = null;
            }

            _currentMenu = MenuManager.Build(_mainForm, _cfg, _ui, "Taskbar");
            
            TaskbarWinHelper.ActivateWindow(this.Handle);
            _currentMenu.Show(Cursor.Position);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Right)
            {
                ShowContextMenu();
            }
            else if (e.Button == MouseButtons.Left && !_bizHelper.IsVertical())
            {
                // 等过双击间隔才判定为单击
                _clickTimer.Stop();
                _clickTimer.Start();
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button == MouseButtons.Left)
            {
                _bizHelper.HandleDoubleClick(_mainForm, _ui);
            }
        }

        private void Tick()
        {
            // [Fix] 周期性检查句柄，防止 Explorer 重启后句柄失效
            // 优化：仅在重试期或句柄无效时调用 FindHandles，且限制调用频率
            bool isHandleInvalid = !_bizHelper.IsTaskbarValid();
            
            // 如果处于重试期，或者句柄无效且距离上次查找超过2秒(防止无Explorer时高频空转)
            if (isHandleInvalid && (DateTime.Now - _lastFindHandleTime).TotalSeconds > 2)
            {
                _bizHelper.FindHandles();
                _lastFindHandleTime = DateTime.Now;
            }

            if (Math.Abs(Environment.TickCount) % 5000 < _cfg.RefreshMs) _bizHelper.CheckTheme();

            // [Fix Part 1] 防空数据保护
            // 使用临时变量接收，先判断数据有效性，再赋值给成员变量 _cols
            // 防止在 UI 重建期间(RebuildLayout)获取到空列表导致任务栏闪烁或清空
            var nextCols = _ui.GetTaskbarColumns();
            if (nextCols == null || nextCols.Count == 0) return;
            
            _cols = nextCols; // 确认有效后再更新引用

            _bizHelper.UpdateTaskbarRect(); 
            
            // [Fix Part 2] 布局变更检测
            if (_bizHelper.IsVertical())
            {
                // 垂直模式逻辑简单且无测量开销，直接重算即可
                _bizHelper.BuildVerticalLayout(_cols);
                _lastLayoutSignature = "vertical";
            }
            else
            {
                // [优化] 智能判断更新条件
                // 1. 必须检查：如果列表是新生成的（坐标还没算过，Bounds为空），必须重算！
                // 这解决了“主界面显隐导致任务栏消失”的问题
                bool isUninitialized = (_cols.Count > 0 && _cols[0].Bounds.IsEmpty);

                // 2. 常规检查：如果内容长度/结构变了（签名变了），也要重算
                string currentSig = _layout.GetLayoutSignature(_cols) + "_" + _bizHelper.Height;
                bool isContentChanged = (currentSig != _lastLayoutSignature);

                if (isUninitialized || isContentChanged)
                {
                    _layout.Build(_cols, _bizHelper.Height);
                    Width = _layout.PanelWidth;
                    Height = _bizHelper.Height;
                    
                    _lastLayoutSignature = currentSig;
                }
            }
            
            _bizHelper.UpdatePlacement(Width);
            
            _flyout?.RefreshContent();

            Redraw();
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(_bizHelper.TransparentKey);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            // ★ 调试验证用：如果消失时出现了一个红块，说明 OnPaint 被调用但 _cols 为空
            // e.Graphics.FillRectangle(Brushes.Red, 0, 0, 20, 20); 

            if (_cols == null) return;
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            TaskbarRenderer.Render(g, _cols, _bizHelper.LastIsLightTheme);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW;
                cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE (防止点击激活窗口，避免抢占焦点)
                if (_cfg != null && _cfg.TaskbarClickThrough)
                {
                    cp.ExStyle |= WS_EX_TRANSPARENT;
                }
                return cp;
            }
        }
    }
}
