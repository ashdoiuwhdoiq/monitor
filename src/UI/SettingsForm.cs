using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using LiteMonitor.src.Core.Actions;
using LiteMonitor.src.UI.Controls;
using LiteMonitor.src.UI.Graphite;
using LiteMonitor.src.UI.SettingsPage;

namespace LiteMonitor.src.UI
{
    /// <summary>
    /// 设置窗口（设计稿 Settings.dc.html，1200×760）：左侧导航（选中块弹簧滑动）、中间页面（入场上浮、分区依次浮现）、
    /// 右侧实时预览、底部浮出的保存条。设计稿的四个页面全部自绘；旧版的其余设置页作为「更多设置」保留，功能不丢。
    /// 所有改动先写入草稿，保存时才合并进实时设置（Draft-Commit）。
    /// </summary>
    public class SettingsForm : Form
    {
        private readonly Settings _cfg;      // Live Settings
        private readonly Settings _draftCfg; // Draft Settings
        private readonly UIController _ui;
        private readonly MainForm _mainForm;
        private readonly SettingsContext _ctx;

        private SideCanvas _side = null!;
        private Panel _host = null!;
        private PreviewCanvas _preview = null!;
        private SaveBar _saveBar = null!;

        private readonly Dictionary<string, Func<SettingsPageBase>> _factories = new();
        private readonly Dictionary<string, SettingsPageBase> _pages = new();
        private readonly HashSet<string> _designKeys = new() { "Appearance", "Items", "Threshold", "TaskbarDesign" };
        private string _currentKey = "";

        // 未保存提示：与打开窗口（或上次保存）时的草稿快照逐字段比较
        private Dictionary<string, string> _baseline = new();
        private string _baselineFull = "";
        private readonly System.Windows.Forms.Timer _dirtyTimer = new() { Interval = 300 };
        private readonly System.Windows.Forms.Timer _barTimer = new() { Interval = 15 };
        private readonly Tween _barY = new Tween(0);
        private long _flashUntil;

        public SettingsForm(Settings cfg, UIController ui, MainForm mainForm)
        {
            _cfg = cfg;
            _ui = ui;
            _mainForm = mainForm;
            _draftCfg = _cfg.DeepClone();
            _ctx = new SettingsContext { Draft = _draftCfg, Live = _cfg, Ui = ui, Main = mainForm };
            _ctx.Changed = () => { _preview?.Invalidate(); UpdateDirtyState(); };
            _ctx.ImmediateApplied = () => BeginInvoke(new Action(() => { _baseline = Snapshot(out _baselineFull); UpdateDirtyState(); }));

            InitializeComponent();
            InitPages();
        }

        private void InitializeComponent()
        {
            UIUtils.ScaleFactor = DeviceDpi / 96f;
            UIColors.Apply(_cfg.UIThemeMode);
            UIColors.ApplyTitleBar(this);
            Text = LanguageManager.T("Menu.SettingsPanel");
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            Font = new Font("Microsoft YaHei UI", 9F);
            BackColor = Gx.Current.Win;
            ForeColor = UIColors.TextMain;
            ClientSize = new Size(UIUtils.S(1200), UIUtils.S(760));
            try { Icon = Properties.Resources.AppIcon; } catch { }

            var items = new List<SideCanvas.Item>
            {
                new("Appearance", "palette", "外观", false),
                new("Items", "list", "监控项", false),
                new("Threshold", "alert", "阈值与告警", false),
                new("TaskbarDesign", "taskbar", "任务栏", false),
                new("MainPanel", "sliders", "面板高级", true),
                new("Taskbar", "taskbar", "任务栏高级", true),
                new("Monitor", "list", "监控项高级", true),
                new("System", "chip", LanguageManager.T("Menu.SystemHardwar"), true),
                new("Plugins", "grid", LanguageManager.T("Menu.Plugins"), true),
            };
            _side = new SideCanvas(items) { Dock = DockStyle.Left, Width = UIUtils.S(212) };
            _side.Navigate = SwitchPage;
            _side.ResetRequested = ResetAll;

            _preview = new PreviewCanvas(_ctx) { Dock = DockStyle.Right, Width = UIUtils.S(360) };
            _preview.CloseRequested = Close;

            _host = new BufferedPanel { Dock = DockStyle.Fill, BackColor = Gx.Current.Win };

            _saveBar = new SaveBar { Height = UIUtils.S(62), Width = UIUtils.S(380), Visible = false };
            _saveBar.Revert = Revert;
            _saveBar.Save = ApplySettings;

            Controls.Add(_host);
            Controls.Add(_preview);
            Controls.Add(_side);
            Controls.Add(_saveBar);
            _host.BringToFront();
            _saveBar.BringToFront();

            _dirtyTimer.Tick += (s, e) => UpdateDirtyState();
            _barTimer.Tick += (s, e) => PlaceSaveBar();
            Resize += (s, e) => PlaceSaveBar();
            FormClosed += (s, e) => { _dirtyTimer.Dispose(); _barTimer.Dispose(); };

            UIModeHub.BeforeChange += OnBeforeModeChange;
            UIModeHub.Changed += OnModeChanged;
            FormClosed += (s, e) => { UIModeHub.BeforeChange -= OnBeforeModeChange; UIModeHub.Changed -= OnModeChanged; };
        }

        private void InitPages()
        {
            _factories["Appearance"] = () => new DesignPage(new AppearanceCanvas(_ctx));
            _factories["Items"] = () => new DesignPage(new MonitorItemsCanvas(_ctx));
            _factories["Threshold"] = () => new DesignPage(new ThresholdCanvas(_ctx));
            _factories["TaskbarDesign"] = () => new DesignPage(new TaskbarDesignCanvas(_ctx));
            _factories["MainPanel"] = () => new MainPanelPage();
            _factories["Taskbar"] = () => new TaskbarPage();
            _factories["Monitor"] = () => new MonitorPage();
            _factories["System"] = () => new SystemHardwarPage();
            _factories["Plugins"] = () => new PluginPage();

            SwitchPage("Appearance");
            Shown += (s, e) => BeginInvoke(new Action(() => { _baseline = Snapshot(out _baselineFull); _dirtyTimer.Start(); }));
        }

        private SettingsPageBase GetPage(string key)
        {
            if (!_pages.TryGetValue(key, out var page))
            {
                page = _factories[key]();
                page.SetContext(_draftCfg, _mainForm, _ui);
                _pages[key] = page;
            }
            return page;
        }

        public void SwitchPage(string key)
        {
            if (_currentKey == key || !_factories.ContainsKey(key)) return;

            // 旧版「监控项高级」保存时会整表写回草稿：离开时先提交，并丢弃实例，下次进入按最新草稿重建
            if (_currentKey == "Monitor" && _pages.TryGetValue("Monitor", out var mon))
            {
                mon.Save();
                _pages.Remove("Monitor");
                _host.Controls.Remove(mon);
                mon.Dispose();
            }

            _currentKey = key;
            _side.Current = key;
            _side.Invalidate();

            bool design = _designKeys.Contains(key);
            _preview.Visible = design;
            _preview.TaskbarMode = key == "TaskbarDesign";
            _preview.Invalidate();

            var page = GetPage(key);
            _host.SuspendLayout();
            try
            {
                foreach (Control c in _host.Controls.Cast<Control>().ToList()) if (c != page) _host.Controls.Remove(c);
                if (!_host.Controls.Contains(page))
                {
                    page.Size = _host.ClientSize;
                    page.Dock = DockStyle.Fill;
                    _host.Controls.Add(page);
                }
                page.OnShow();
                if (!design) ApplyScrollTheme(page);
            }
            finally { _host.ResumeLayout(); }
            PlaceSaveBar();
        }

        /// <summary>旧版页面里的原生滚动条、下拉框跟随深浅色（Windows 10 1809+ 的 DarkMode_Explorer 主题）。</summary>
        private static void ApplyScrollTheme(Control root)
        {
            string theme = Gx.Current.Dark ? "DarkMode_Explorer" : "Explorer";
            void Walk(Control c)
            {
                try { if (c.IsHandleCreated) SetWindowTheme(c.Handle, theme, null); } catch { }
                foreach (Control child in c.Controls) Walk(child);
            }
            Walk(root);
        }

        [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hwnd, string? appName, string? idList);

        // ===== 未保存提示 =====

        private Dictionary<string, string> Snapshot(out string full)
        {
            full = JsonSerializer.Serialize(_draftCfg);
            var dict = new Dictionary<string, string>();
            try
            {
                var node = JsonNode.Parse(full)?.AsObject();
                if (node != null)
                    foreach (var kv in node)
                        if (!SettingsChanger.IsRuntimeProp(kv.Key)) dict[kv.Key] = kv.Value?.ToJsonString() ?? "null";
            }
            catch { }
            return dict;
        }

        private int CountChanges()
        {
            if (_baseline.Count == 0) return 0;
            var now = Snapshot(out _);
            int n = 0;
            foreach (var kv in now)
                if (!_baseline.TryGetValue(kv.Key, out var old) || old != kv.Value) n++;
            return n;
        }

        private void UpdateDirtyState()
        {
            if (_saveBar == null) return;
            bool flash = Motion.Now < _flashUntil;
            int changes = flash ? 0 : CountChanges();
            if (_saveBar.Changes != changes || _saveBar.Flash != flash)
            {
                _saveBar.Changes = changes;
                _saveBar.Flash = flash;
                _saveBar.Invalidate();
            }
            bool show = changes > 0 || flash;
            // 浮出 / 收回：.55s 弹簧（.34,1.4,.64,1）
            _barY.Set(show ? 1 : 0, show ? 550 : 350, show ? new Ease(.34, 1.4, .64, 1) : Motion.Out);
            if (_barY.IsRunning(Motion.Now) || show != _saveBar.Visible) _barTimer.Start();
        }

        private void PlaceSaveBar()
        {
            if (_saveBar == null || _host == null) return;
            float k = _barY.Value();
            var host = _host.Bounds;
            int hiddenTop = host.Bottom + UIUtils.S(10);
            int shownTop = host.Bottom - UIUtils.S(20) - _saveBar.Height;
            _saveBar.Left = host.X + (host.Width - _saveBar.Width) / 2;
            _saveBar.Top = (int)Math.Round(hiddenTop + (shownTop - hiddenTop) * k);
            bool vis = k > 0.01f;
            if (_saveBar.Visible != vis) _saveBar.Visible = vis;
            if (vis) _saveBar.BringToFront();
            if (!_barY.IsRunning(Motion.Now)) _barTimer.Stop();
        }

        private void Revert()
        {
            var original = JsonSerializer.Deserialize<Settings>(_baselineFull);
            if (original == null) return;
            SettingsChanger.Merge(_draftCfg, original);
            // 旧版页面按草稿刷新
            if (_pages.TryGetValue("Monitor", out var mon) && _currentKey != "Monitor") { _pages.Remove("Monitor"); mon.Dispose(); }
            if (_pages.TryGetValue(_currentKey, out var cur)) cur.OnShow();
            _ctx.Touch();
            foreach (var p in _pages.Values) p.Invalidate(true);
        }

        private void ResetAll()
        {
            if (MessageBox.Show(LanguageManager.T("Menu.ResetConfirm"), LanguageManager.T("Menu.Reset"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            try
            {
                Settings.GlobalBlockSave = true;
                SettingsHelper.DeleteStoredSettings();
                Application.Restart();
                Environment.Exit(0);
            }
            catch (Exception ex) { Settings.GlobalBlockSave = false; MessageBox.Show(ex.Message); }
        }

        private void OnBeforeModeChange(object? source, Point? screenOrigin)
        {
            if (IsDisposed || !Visible) return;
            foreach (var c in new GCanvas[] { _side, _preview, _saveBar })
                c.BeginReveal(screenOrigin.HasValue ? c.PointToClient(screenOrigin.Value) : new Point(c.Width / 2, c.Height / 2));
            if (_pages.TryGetValue(_currentKey, out var page) && page is DesignPage dp)
                dp.Canvas.BeginReveal(screenOrigin.HasValue ? dp.Canvas.PointToClient(screenOrigin.Value) : new Point(dp.Canvas.Width / 2, dp.Canvas.Height / 2));
        }

        private void OnModeChanged()
        {
            if (IsDisposed) return;
            BackColor = Gx.Current.Win;
            _host.BackColor = Gx.Current.Win;
            UIColors.ApplyTitleBar(this);
            try { var s = Size; Size = new Size(s.Width, s.Height + 1); Size = s; } catch { }
            // 旧版页面的控件在创建时取色：丢弃后按新配色重建
            foreach (var key in _pages.Keys.Where(k => !_designKeys.Contains(k) && k != _currentKey).ToList())
            {
                if (key == "Monitor") _pages[key].Save();
                _pages[key].Dispose();
                _pages.Remove(key);
            }
            if (!_designKeys.Contains(_currentKey))
            {
                string cur = _currentKey;
                if (_pages.TryGetValue(cur, out var p))
                {
                    if (cur == "Monitor") p.Save();
                    _host.Controls.Remove(p);
                    p.Dispose();
                    _pages.Remove(cur);
                }
                _currentKey = "";
                SwitchPage(cur);
            }
            else if (_pages.TryGetValue(_currentKey, out var dp)) dp.BackColor = Gx.Current.Win;
            Invalidate(true);
        }

        private void ApplySettings()
        {
            if (Motion.Now < _flashUntil) return;
            bool oldAutoStart = _cfg.AutoStart;

            // 保存逻辑顺序：先其余页面，最后监控项（它会整表写回）
            foreach (var kv in _pages) if (kv.Key != "Monitor") kv.Value.Save();
            if (_pages.TryGetValue("Monitor", out var mon)) mon.Save();

            // 1. 全局校验 (防止全隐藏死锁)
            bool noInteractiveWindow =
                (_draftCfg.HideMainForm || _draftCfg.ClickThrough) &&
                (!_draftCfg.ShowTaskbar || _draftCfg.TaskbarClickThrough) &&
                _draftCfg.HideTrayIcon;
            if (noInteractiveWindow)
            {
                _draftCfg.HideTrayIcon = false;
                MessageBox.Show("为了防止所有可交互入口都被死锁（隐藏或穿透+隐藏托盘），已强制显示托盘图标。", "LiteMonitor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            // 2. 合并变更到 Live Settings → 3. 持久化 → 4. 应用
            SettingsChanger.Merge(_cfg, _draftCfg);
            bool autoStartChanged = oldAutoStart != _cfg.AutoStart;
            _cfg.Save();
            AppActions.ApplyAllSettings(_cfg, _mainForm, _ui, autoStartChanged);

            // 5. 草稿与实时设置对齐（插件生成的监控项等）
            SettingsChanger.RebaseDraftMonitorItems(_cfg, _draftCfg);

            // 保存条显示「设置已应用 / 已保存」1.4 秒后收回
            _baseline = Snapshot(out _baselineFull);
            _flashUntil = Motion.Now + 1400;
            UpdateDirtyState();
            var t = new System.Windows.Forms.Timer { Interval = 1450 };
            t.Tick += (s, e) => { t.Dispose(); UpdateDirtyState(); };
            t.Start();
        }
    }
}
