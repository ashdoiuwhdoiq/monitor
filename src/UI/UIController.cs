using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices.InfoService;
using LiteMonitor.src.SystemServices;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace LiteMonitor
{
    public class UIController : IDisposable
    {
        private readonly Settings _cfg;
        private readonly Form _form;
        private readonly HardwareMonitor _mon;
        private readonly System.Windows.Forms.Timer _timer;

        // 动画时钟：只在有数值、颜色、悬停或折叠在变化时运行，静止时自动停下
        private readonly System.Windows.Forms.Timer _animTimer;
        private readonly System.Diagnostics.Stopwatch _animClock = System.Diagnostics.Stopwatch.StartNew();
        private long _lastFrameMs;
        private MetricItem? _hoverItem;
        private const float CollapseTauMs = 110f;
        private const float LivePulseMs = 700f;
        private long _lastTickMs = -100000;

        // 竖向面板（Graphite 设计稿版式）
        private readonly GraphitePanel _panel = new GraphitePanel();

        private UILayout _layout;
        private bool _layoutDirty = true;
        private bool _dragging = false;
        private string _lastLayoutSignature = "";

        private List<GroupLayoutInfo> _groups = new();
        private List<Column> _hxColsHorizontal = new();
        private List<Column> _hxColsTaskbar = new();
        private HorizontalLayout? _hxLayout;
        private Dictionary<string, DateTime> _overheatStartTimes = new Dictionary<string, DateTime>();
        public MainForm MainForm => (MainForm)_form;
        
        // 辅助判断当前渲染状态是否为横屏 (用于 AppActions 判断是否切换了模式)
        // 只有在 Render 并在横屏分支运行后 _hxLayout 才不为 null
        // 竖屏模式下 ApplyTheme 会将其重置为 null
        public bool IsLayoutHorizontal => _hxLayout != null;

        public List<Column> GetTaskbarColumns() => _hxColsTaskbar;
        public List<GroupLayoutInfo> GetMainGroups() => _groups;

        public UIController(Settings cfg, Form form)
        {
            _cfg = cfg;
            _form = form;
            _mon = new HardwareMonitor(cfg);

            _layout = new UILayout(ThemeManager.Current);

            _timer = new System.Windows.Forms.Timer { Interval = Math.Max(80, _cfg.RefreshMs) };
            _timer.Tick += (_, __) => Tick();
            _timer.Start();

            _animTimer = new System.Windows.Forms.Timer { Interval = 15 };
            _animTimer.Tick += (_, __) => AnimFrame();

            ApplyTheme(_cfg.Skin);
        }

        public float GetCurrentDpiScale()
        {
            using (Graphics g = _form.CreateGraphics())
            {
                return g.DpiX / 96f;
            }
        }

        public void ApplyTheme(string name, bool retainData = false)
        {
            // 1. 先保留旧主题的引用 (为了稍后释放)
            var oldTheme = ThemeManager.Current;

            // 2. 清理全局画刷缓存 (这不会影响 ThemeManager 的字体了，因为解耦了)
            UIRenderer.ClearCache();
            UIUtils.ClearBrushCache();

            // 3. 加载新主题 (Current 指向新对象，包含全新的字体)
            // 如果主题名相同且要求保留数据，可以考虑跳过 Load，但为了应用 Scale 还是重新 Load 比较稳妥
            // 或者优化：ThemeManager.Load 内部判断是否已加载
            ThemeManager.Load(name);
            var t = ThemeManager.Current;

            // 4. 安全释放旧主题的字体
            // 此时 Current 已经是新主题了，Paint 事件只会用新字体，所以释放旧的是安全的
            if (oldTheme != null && oldTheme != t)
            {
                oldTheme.DisposeFonts();
            }

            // ... 后续缩放逻辑保持不变 ...
            float dpiScale = GetCurrentDpiScale();   
            float userScale = (float)_cfg.UIScale;    
            float finalScale = dpiScale * userScale;

            t.Scale(dpiScale, userScale); // Scale 内部现在会自动清理旧缩放字体

            // ... 边距修复逻辑 ...
            if (!_cfg.HorizontalMode)
            {
                t.Layout.Width = (int)(_cfg.PanelWidth * finalScale);
                _form.ClientSize = new Size(t.Layout.Width, _form.ClientSize.Height);
            }

            TaskbarRenderer.ReloadStyle(_cfg);

            _layout = new UILayout(t);
            _hxLayout = null;

            if (!retainData)
            {
                BuildMetrics();
                BuildHorizontalColumns();
            }
            else
            {
                // [Safety Check] Even if retaining data, ensure we have content.
                // This handles cases where we switch modes but data wasn't built for that mode yet.
                if (_groups.Count == 0) BuildMetrics();
                if (_hxColsHorizontal.Count == 0) BuildHorizontalColumns();
            }
            
            _layoutDirty = true;

            // 密度状态与设置同步（不播放过渡），换主题 / 启动时播放入场动效
            float compactNow = _cfg.PanelCompact ? 1f : 0f;
            _panel.Compact.Snap(compactNow);
            _panel.SegThumb.Snap(compactNow);
            if (!retainData) PlayEntrance();

            _form.BackColor = ThemeManager.ParseColor(t.Color.Background);

            _timer.Interval = Math.Max(80, _cfg.RefreshMs);
            _form.Invalidate();
            _form.Update();
        }

        public void RebuildLayout()
        {
            BuildMetrics();
            BuildHorizontalColumns(); 
            _layoutDirty = true;
            _form.Invalidate();
            _form.Update();
        }

        public void SetDragging(bool dragging) => _dragging = dragging;

        public void Render(Graphics g)
        {
            var t = ThemeManager.Current;
            _layout ??= new UILayout(t);

            // === 横屏模式 ===
            if (_cfg.HorizontalMode)
            {
                _hxLayout ??= new HorizontalLayout(t, _form.Width, LayoutMode.Horizontal);

                // [通用方案] 动态检测是否需要重新计算布局
                // 移至 Tick() 中低频检查，避免 Render 高频调用
                if (_layoutDirty)
                {
                    int h = _hxLayout.Build(_hxColsHorizontal);
                    // 同样建议横屏模式也使用 ClientSize
                    // _form.Width = ... 
                    // _form.Height = h;
                    _form.ClientSize = new Size(_hxLayout.PanelWidth, h);
                    _layoutDirty = false;
                }
                HorizontalRenderer.Render(g, t, _hxColsHorizontal, _hxLayout.PanelWidth);
                return;
            }

            // === 竖屏模式（Graphite 版式）===
            if (_layoutDirty)
            {
                int h = _panel.Build(_groups, t, _form.ClientSize.Width);
                // [修复2补充] 设置高度时也使用 ClientSize，确保高度精准
                _form.ClientSize = new Size(_form.ClientSize.Width, h);
                _layoutDirty = false;
            }

            _panel.TopMost = _cfg.TopMost;
            _panel.RefreshMs = _cfg.RefreshMs;
            _panel.Uptime = InfoService.Instance.GetValue("Uptime");
            _panel.Selected = (_form as MainForm)?.DetailCardItem;
            _panel.Render(g, _groups, t, _form.ClientSize);
            DrawReveal(g, _form.ClientSize);
        }

        private bool _busy = false;

        private async void Tick()
        {
            if (_dragging || _busy) return;
            _busy = true;

            try
            {
                await Task.Run(() => _mon.UpdateAll());

                // ① 更新竖屏 items
                foreach (var g in _groups)
                    foreach (var it in g.Items)
                    {
                        // [新增] Dashboard 实时更新
                        if (it.Key.StartsWith("DASH."))
                        {
                             string dashKey = it.Key.Substring(5);
                             string val = InfoService.Instance.GetValue(dashKey);
                             it.TextValue = val;
                        }
                        else
                        {
                            it.Value = _mon.Get(it.Key);
                            if (it.Value.HasValue) it.PushHistory(it.Value.Value);
                        }
                    }

                // ② 更新横版 / 任务栏 (清理了冗余代码)
                void UpdateCol(Column col)
                {
                    void UpdateItem(MetricItem it) 
                    {
                        if (it == null) return;
                        
                        // [新增] Dashboard 实时更新 (横版/任务栏)
                        if (it.Key.StartsWith("DASH."))
                        {
                             string dashKey = it.Key.Substring(5);
                             string val = InfoService.Instance.GetValue(dashKey);
                             it.TextValue = val;
                        }
                        else
                        {
                            it.Value = _mon.Get(it.Key);
                        }
                    }
                    UpdateItem(col.Top);
                    UpdateItem(col.Bottom);
                }
                
                foreach (var col in _hxColsHorizontal) UpdateCol(col);
                foreach (var col in _hxColsTaskbar) UpdateCol(col);

                HardwareHistoryLogger.RecordSnapshot(_cfg, key => _mon.Get(key));
 
                CheckTemperatureAlert();

                // 驱动 DashboardService 更新
                InfoService.Instance.Update();

                // [优化] 在数据更新后检查布局签名 (每秒一次)
                // 只有当充电状态等导致样本变化时，才标记 Dirty
                if (_cfg.HorizontalMode && _hxLayout != null)
                {
                    string currentLayoutSig = _hxLayout.GetLayoutSignature(_hxColsHorizontal);
                    if (currentLayoutSig != _lastLayoutSignature)
                    {
                        _lastLayoutSignature = currentLayoutSig;
                        _layoutDirty = true;
                    }
                }

                _form.Invalidate();
                _lastTickMs = _animClock.ElapsedMilliseconds;
                StartAnim();
            }
            finally
            {
                _busy = false;
            }
        }

        // ================= 动画与交互 =================

        private void StartAnim()
        {
            if (_animTimer.Enabled) return;
            _lastFrameMs = _animClock.ElapsedMilliseconds;
            _animTimer.Start();
        }

        private void AnimFrame()
        {
            long now = _animClock.ElapsedMilliseconds;
            float dt = Math.Clamp(now - _lastFrameMs, 1, 100);
            _lastFrameMs = now;

            var t = ThemeManager.Current;
            double speed = _cfg.AnimationSpeed;
            bool moving = false;

            long mnow = Motion.Now;

            // 面板自身的补间：入场、密度过渡、分段滑块、按钮按压
            if (_panel.IsAnimating(mnow, _groups.Sum(g => g.Items.Count + 1)))
            {
                moving = true;
                if (_panel.Compact.IsRunning(mnow)) _layoutDirty = true;
            }

            if (_revealSnapshot != null) moving = true;

            // 实时点：每次采样后扩散一圈
            _panel.LivePulse = Math.Clamp((now - _lastTickMs) / LivePulseMs, 0f, 1f);
            if (_panel.LivePulse < 1f) moving = true;

            foreach (var g in _groups)
            {
                // 分组折叠：高度按补间推进，需要重新布局（窗口高度随之变化）
                float e = g.ExpandTween.Value(mnow);
                if (g.Expand != e) { g.Expand = e; _layoutDirty = true; }
                if (g.ExpandTween.IsRunning(mnow) || g.BodyFadeTween.IsRunning(mnow) || g.ChevronTween.IsRunning(mnow) || g.SummaryTween.IsRunning(mnow))
                    moving = true;

                foreach (var it in g.Items) moving |= it.Animate(dt, speed, t);
            }

            void AnimCols(List<Column> cols)
            {
                foreach (var col in cols)
                {
                    if (col.Top != null) moving |= col.Top.Animate(dt, speed, t);
                    if (col.Bottom != null) moving |= col.Bottom.Animate(dt, speed, t);
                }
            }
            AnimCols(_hxColsHorizontal);
            AnimCols(_hxColsTaskbar);

            _form.Invalidate();
            if (!moving) _animTimer.Stop();
        }

        /// <summary>主面板鼠标移动：更新悬停行（仅竖屏模式）。传 null 表示离开面板。</summary>
        public void SetHover(Point? clientPt)
        {
            MetricItem? hit = null;
            if (clientPt.HasValue && !_cfg.HorizontalMode)
            {
                foreach (var g in _groups)
                {
                    if (g.Expand < 0.99f) continue;
                    foreach (var it in g.Items)
                    {
                        if (it.Style != MetricRenderStyle.TextOnly && it.Bounds.Contains(clientPt.Value)) { hit = it; break; }
                    }
                    if (hit != null) break;
                }
            }
            // 标题栏按钮与分组标题的悬停（即时，无需动画）
            string btn = clientPt.HasValue && !_cfg.HorizontalMode ? HitButton(clientPt.Value) : "";
            GroupLayoutInfo? hoverGroup = null;
            if (clientPt.HasValue && !_cfg.HorizontalMode && btn == "")
                hoverGroup = _groups.FirstOrDefault(g => g.HeaderRect.Contains(clientPt.Value));
            if (btn != _panel.HoverButton || hoverGroup != _panel.HoverGroup)
            {
                _panel.HoverButton = btn;
                _panel.HoverGroup = hoverGroup;
                _form.Invalidate();
            }

            if (hit == _hoverItem) return;
            if (_hoverItem != null) _hoverItem.HoverTarget = false;
            _hoverItem = hit;
            if (hit != null) hit.HoverTarget = true;
            StartAnim();
        }

        /// <summary>主面板单击（未拖动）：点在分组标题上则折叠或展开该组。返回是否处理。</summary>
        private string HitButton(Point pt)
        {
            if (_panel.PinRect.Contains(pt)) return "pin";
            if (_panel.MoreRect.Contains(pt)) return "more";
            if (_panel.ShowSeg && _panel.SegComfyRect.Contains(pt)) return "comfy";
            if (_panel.ShowSeg && _panel.SegCompactRect.Contains(pt)) return "compact";
            return "";
        }

        public bool HandleClick(Point clientPt)
        {
            if (_cfg.HorizontalMode) return false;

            switch (HitButton(clientPt))
            {
                case "pin":
                    _cfg.TopMost = !_cfg.TopMost;
                    _cfg.Save();
                    LiteMonitor.src.Core.Actions.AppActions.ApplyWindowAttributes(_cfg, MainForm);
                    MainForm.RebuildMenus();
                    MainForm.ShowToast(LanguageManager.T(_cfg.TopMost ? "Menu.ToastTopMostOn" : "Menu.ToastTopMostOff"));
                    _form.Invalidate();
                    return true;
                case "more":
                    MainForm.ContextMenuStrip?.Show(_form, new Point(_panel.MoreRect.Right, _panel.MoreRect.Bottom + 4), ToolStripDropDownDirection.BelowLeft);
                    return true;
                case "comfy":
                case "compact":
                    SetCompact(HitButton(clientPt) == "compact", toast: false);
                    return true;
            }

            foreach (var g in _groups)
            {
                if (!g.HeaderRect.Contains(clientPt)) continue;
                g.SetCollapsed(!g.Collapsed, animate: true);
                _cfg.CollapsedGroups ??= new List<string>();
                _cfg.CollapsedGroups.RemoveAll(x => x == g.GroupName);
                if (g.Collapsed) _cfg.CollapsedGroups.Add(g.GroupName);
                _cfg.Save();
                StartAnim();
                return true;
            }
            return false;
        }

        /// <summary>切换舒适 / 紧凑：行高与进度条 .4s 缓出，分段滑块 .5s 弹簧。</summary>
        public void SetCompact(bool compact, bool toast)
        {
            if (_cfg.PanelCompact != compact)
            {
                _cfg.PanelCompact = compact;
                _cfg.Save();
            }
            _panel.Compact.Set(compact ? 1f : 0f, 400, Motion.Out);
            _panel.SegThumb.Set(compact ? 1f : 0f, 500, Motion.Spring);
            if (toast) MainForm.ShowToast(LanguageManager.T(compact ? "Menu.PanelCompact" : "Menu.PanelComfy"));
            StartAnim();
        }

        /// <summary>按下：标题栏按钮缩到 .88，行缩到 .985（.12s）；松开时弹簧回弹。</summary>
        public void PressDown(Point pt)
        {
            if (_cfg.HorizontalMode) return;
            string b = HitButton(pt);
            if (b == "pin") _panel.PinPress.Set(1f, 120, Motion.Out);
            else if (b == "more") _panel.MorePress.Set(1f, 120, Motion.Out);
            else
            {
                var it = HitItem(pt, out _);
                if (it != null) { _panel.PressedItem = it; _panel.RowPress.Set(1f, 120, Motion.Out); }
            }
            StartAnim();
        }

        public void PressUp()
        {
            _panel.PinPress.Set(0f, 380, Motion.Spring);
            _panel.MorePress.Set(0f, 380, Motion.Spring);
            _panel.RowPress.Set(0f, 380, Motion.Spring);
            StartAnim();
        }

        /// <summary>播放面板入场动效（启动、切换主题、显示主窗口时）。圆形揭开进行中时不叠加。</summary>
        public void PlayEntrance()
        {
            if (RevealRunning(Motion.Now)) return;
            _panel.BeginEntrance();
            StartAnim();
        }

        // ===== 深浅模式切换：圆形揭开（设计稿：.76s，cubic-bezier(.65,0,.35,1)）=====
        private const float RevealMs = 760;
        private Bitmap? _revealSnapshot;
        private PointF _revealOrigin;
        private long _revealStart = -100000;

        private bool RevealRunning(long now) => _revealSnapshot != null && now - _revealStart < RevealMs;

        /// <summary>在切换主题前调用：先把当前画面存下来，之后新画面从 origin 处以圆形逐渐盖过旧画面。</summary>
        public void BeginReveal(Point? origin)
        {
            if (_cfg.HorizontalMode || _form.ClientSize.Width <= 0 || _form.ClientSize.Height <= 0) return;
            var size = _form.ClientSize;
            var snap = new Bitmap(size.Width, size.Height);
            using (var g = Graphics.FromImage(snap))
                _panel.Render(g, _groups, ThemeManager.Current, size);
            _revealSnapshot?.Dispose();
            _revealSnapshot = snap;
            var o = origin ?? new Point(size.Width / 2, size.Height / 2);
            _revealOrigin = new PointF(Math.Clamp(o.X, 0, size.Width), Math.Clamp(o.Y, 0, size.Height));
            _revealStart = Motion.Now;
            StartAnim();
        }

        private void DrawReveal(Graphics g, Size client)
        {
            long now = Motion.Now;
            if (_revealSnapshot == null) return;
            if (!RevealRunning(now))
            {
                _revealSnapshot.Dispose();
                _revealSnapshot = null;
                return;
            }
            float p = Motion.InOut.Eval(Motion.Progress(_revealStart, 0, RevealMs, now));
            float maxR = 0;
            foreach (var c in new[] { new PointF(0, 0), new PointF(client.Width, 0), new PointF(0, client.Height), new PointF(client.Width, client.Height) })
                maxR = Math.Max(maxR, (float)Math.Sqrt(Math.Pow(c.X - _revealOrigin.X, 2) + Math.Pow(c.Y - _revealOrigin.Y, 2)));
            float r = maxR * p;
            using var circle = new System.Drawing.Drawing2D.GraphicsPath();
            circle.AddEllipse(_revealOrigin.X - r, _revealOrigin.Y - r, r * 2, r * 2);
            using var outside = new Region(new Rectangle(0, 0, client.Width, client.Height));
            outside.Exclude(circle);
            var saved = g.Save();
            g.SetClip(outside, System.Drawing.Drawing2D.CombineMode.Replace);
            g.DrawImageUnscaled(_revealSnapshot, 0, 0);
            g.Restore(saved);
        }

        /// <summary>命中竖屏面板中的某一行（折叠中的组不算），并返回它所在分组的显示名。</summary>
        public MetricItem? HitItem(Point clientPt, out string groupLabel)
        {
            groupLabel = "";
            if (_cfg.HorizontalMode) return null;
            foreach (var g in _groups)
            {
                if (g.Expand < 0.99f) continue;
                foreach (var it in g.Items)
                {
                    if (it.Style == MetricRenderStyle.TextOnly || !it.Value.HasValue) continue;
                    if (!it.Bounds.Contains(clientPt)) continue;
                    groupLabel = string.IsNullOrEmpty(g.Label) ? g.GroupName : g.Label;
                    return it;
                }
            }
            return null;
        }

        /// <summary>鼠标是否在可点击的分组标题上（用于切换手形光标）。</summary>
        public bool IsOverHeader(Point clientPt)
        {
            if (_cfg.HorizontalMode) return false;
            if (HitButton(clientPt) != "") return true;
            foreach (var g in _groups) if (g.HeaderRect.Contains(clientPt)) return true;
            return false;
        }

        private void BuildMetrics()
        {
            _groups = new List<GroupLayoutInfo>();

            var activeItems = _cfg.MonitorItems
                .Where(x => x.VisibleInPanel)
                // ★★★ 终极排序逻辑 ★★★
                // 1. 先按 Group 分组，解决 "分裂问题" (物理聚类)
                .GroupBy(x => x.UIGroup)
                // 2. 组间排序：使用组内最小的 SortIndex 决定组的位置 (保留用户的整体排序意图)
                .OrderBy(g => g.Min(x => x.SortIndex))
                // 3. 组内排序并展平：组内按 SortIndex 排列
                .SelectMany(g => g.OrderBy(x => x.SortIndex))
                .ToList();

            if (activeItems.Count == 0) return;

            string currentGroupKey = "";
            List<MetricItem> currentGroupList = new List<MetricItem>();

            foreach (var cfgItem in activeItems)
            {
                string groupKey = cfgItem.UIGroup;

                if (groupKey != currentGroupKey && currentGroupList.Count > 0)
                {
                    var gr = new GroupLayoutInfo(currentGroupKey, currentGroupList);
                    string gName = LanguageManager.T(UIUtils.Intern("Groups." + currentGroupKey));
                    if (_cfg.GroupAliases.ContainsKey(currentGroupKey)) gName = _cfg.GroupAliases[currentGroupKey];
                    
                    gr.Label = gName;
                    ApplyCollapsed(gr);
                    _groups.Add(gr);
                    currentGroupList = new List<MetricItem>();
                }

                currentGroupKey = groupKey;

                string label = LanguageManager.T(UIUtils.Intern("Items." + cfgItem.Key));
                var item = new MetricItem 
                { 
                    Key = cfgItem.Key, 
                    BoundConfig = cfgItem 
                };
                
                // [修复] 初始化默认 Label (作为 Fallback)
                // 如果 BoundConfig.UserLabel 有值，Getter 会优先返回它
                item.Label = label;

                string defShort = LanguageManager.T(UIUtils.Intern("Short." + cfgItem.Key));
                item.ShortLabel = defShort;

                
                // ★★★ [新增] Dashboard 数据源绑定 ★★★
                if (cfgItem.Key.StartsWith("DASH."))
                {
                     string dashKey = cfgItem.Key.Substring(5); // DASH.HOST -> HOST
                     
                     // 直接从服务获取值，不再依赖 WidgetItem 对象
                     string val = InfoService.Instance.GetValue(dashKey);
                     item.TextValue = val;
                     item.Value = null; // Dashboard 项没有数值，只有文本
                }
                else
                {
                    float? val = _mon.Get(item.Key);
                    item.Value = val;
                    if (val.HasValue) item.DisplayValue = val.Value;
                }

                currentGroupList.Add(item);
            }

            if (currentGroupList.Count > 0)
            {
                var gr = new GroupLayoutInfo(currentGroupKey, currentGroupList);
                string gName = LanguageManager.T(UIUtils.Intern("Groups." + currentGroupKey));
                 if (_cfg.GroupAliases.ContainsKey(currentGroupKey)) gName = _cfg.GroupAliases[currentGroupKey];
                
                gr.Label = gName;
                    ApplyCollapsed(gr);
                _groups.Add(gr);
            }
        }

        private void ApplyCollapsed(GroupLayoutInfo gr)
        {
            gr.SetCollapsed(_cfg.CollapsedGroups?.Contains(gr.GroupName) == true, animate: false);
        }

        private void BuildHorizontalColumns()
        {
            _hxColsHorizontal = BuildColumnsCore(forTaskbar: false);
            _hxColsTaskbar = BuildColumnsCore(forTaskbar: true);
        }

        private List<Column> BuildColumnsCore(bool forTaskbar)
        {
            var cols = new List<Column>();

            // 1. 筛选
            var query = _cfg.MonitorItems
                .Where(x => forTaskbar ? x.VisibleInTaskbar : x.VisibleInPanel);

            // 2. 排序 (优化：先按组聚类，防止新插件跑到末尾)
            bool useTaskbarSort = forTaskbar || _cfg.HorizontalFollowsTaskbar;
            List<MonitorItemConfig> items;

            if (useTaskbarSort)
            {
                // [Taskbar Mode] Allow cross-group sorting (Flat Sort)
                // 用户要求：任务栏排序不受分组约束，可以跨组混排
                items = query
                    .OrderBy(item => item.TaskbarSortIndex)
                    .ToList();
            }
            else
            {
                // [Panel Mode] Enforce grouping (Grouped Sort)
                // 面板模式默认保持分组聚合的视觉习惯
                items = query
                    .GroupBy(x => x.UIGroup)
                    .OrderBy(g => g.Min(item => item.SortIndex))
                    .SelectMany(g => g.OrderBy(item => item.SortIndex))
                    .ToList();
            }

            bool singleLine = (forTaskbar && _cfg.TaskbarSingleLine) || 
                              (!forTaskbar && _cfg.HorizontalMode && _cfg.HorizontalSingleLine);
            int step = singleLine ? 1 : 2;

            for (int i = 0; i < items.Count; i += step)
            {
                var col = new Column();
                col.Top = CreateMetric(items[i]);

                if (!singleLine && i + 1 < items.Count)
                {
                    col.Bottom = CreateMetric(items[i + 1]);
                }
                cols.Add(col);
            }

            return cols;
        }

        private MetricItem CreateMetric(MonitorItemConfig cfg)
        {
            var item = new MetricItem 
            { 
                Key = cfg.Key,
                BoundConfig = cfg // [核心修复] 绑定 Config
            };
            
            // Standard initialization
            // 优先使用用户自定义的 Label/ShortLabel
            // 如果用户未定义 (null/empty)，则使用语言包中的默认值
            // 如果用户定义了 " " (空格)，则保留空格 (意味着隐藏标签)
            
            string defLabel = LanguageManager.T(UIUtils.Intern("Items." + cfg.Key));
            item.Label = defLabel; // 设为默认值，BoundConfig 会负责处理覆盖

            string defShort = LanguageManager.T(UIUtils.Intern("Short." + cfg.Key));
            item.ShortLabel = defShort;


            // [新增] Dashboard 数据源绑定
            if (cfg.Key.StartsWith("DASH."))
            {
                 string dashKey = cfg.Key.Substring(5);
                 // 立即尝试获取一次，如果是 Loading 且 NetworkManager 有缓存，则直接更新
                 // 避免异步等待导致的 "?"
                 if (dashKey == "IP")
                 {
                      string cachedIP = HardwareMonitor.Instance?.GetNetworkIP();
                      if (!string.IsNullOrEmpty(cachedIP) && cachedIP != "?")
                      {
                           InfoService.Instance.InjectIP(cachedIP);
                      }
                 }
                 
                 string val = InfoService.Instance.GetValue(dashKey);   
                 item.TextValue = val;
                 item.Value = null;
            }
            else
            {
                InitMetricValue(item);
            }
            
            return item;
        }

        private void InitMetricValue(MetricItem? item)
        {
            if (item == null) return;
            float? val = _mon.Get(item.Key);
            item.Value = val;
            if (val.HasValue) item.DisplayValue = val.Value;
        }
        
        private void CheckTemperatureAlert()
        {
            if (!_cfg.AlertTempEnabled) 
            {
                _overheatStartTimes.Clear();
                return;
            }

            if ((DateTime.Now - _cfg.LastAlertTime).TotalMinutes < 3) return;

            int globalThreshold = _cfg.AlertTempThreshold; 
            int diskThreshold = Math.Min(globalThreshold - 20, 60); 

            List<string> alertLines = new List<string>();
            List<string> alertKeys = new List<string>();
            string alertTitle = LanguageManager.T("Menu.AlertTemp");

            void Check(string key, float? val, int threshold, string label, string msgSuffix = "")
            {
                if (val.HasValue && val.Value >= threshold)
                {
                    if (!_overheatStartTimes.ContainsKey(key))
                        _overheatStartTimes[key] = DateTime.Now;
                    
                    if ((DateTime.Now - _overheatStartTimes[key]).TotalSeconds >= 5)
                    {
                        alertLines.Add($"{label} {alertTitle}: 🔥{val:F0}°C{msgSuffix}");
                        alertKeys.Add(key);
                    }
                }
                else
                {
                    if (_overheatStartTimes.ContainsKey(key))
                        _overheatStartTimes.Remove(key);
                }
            }

            Check("CPU.Temp", _mon.Get("CPU.Temp"), globalThreshold, LanguageManager.T("Short.CPU.Temp"));
            Check("GPU.Temp", _mon.Get("GPU.Temp"), globalThreshold, LanguageManager.T("Short.GPU.Temp"));
            Check("MOBO.Temp", _mon.Get("MOBO.Temp"), globalThreshold, LanguageManager.T("Short.MOBO.Temp"));
            Check("DISK.Temp", _mon.Get("DISK.Temp"), diskThreshold, LanguageManager.T("Short.DISK.Temp"), $" (>{diskThreshold}°C)");

            if (alertLines.Count > 0)
            {
                // 按监控键判断而非本地化后的标签，中文等语言下也能正确显示磁盘阈值
                string thresholdText = (alertKeys.Count == 1 && alertKeys[0] == "DISK.Temp")
                    ? $"(>{diskThreshold}°C)" 
                    : $"(>{globalThreshold}°C)";

                alertTitle += $" {thresholdText}";
                string bodyText = string.Join("\n", alertLines);
                
                ((MainForm)_form).ShowNotification(alertTitle, bodyText, ToolTipIcon.Warning);
                _cfg.LastAlertTime = DateTime.Now;
            }
        }
        public void Dispose()
        {
            _timer.Stop();
            _timer.Dispose();
            _animTimer.Stop();
            _animTimer.Dispose();
            _panel.Dispose();
            _mon.Dispose();
        }
    }
}
