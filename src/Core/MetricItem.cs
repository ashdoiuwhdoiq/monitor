using System;
using System.Drawing;
using System.Linq; // 需要 Linq 来查询 Config
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices.InfoService; // [New] For Plugin Color Override

namespace LiteMonitor
{
    public enum MetricRenderStyle
    {
        StandardBar, 
        TwoColumn,   
        TextOnly     
    }

    public class MetricItem
    {
        // [新增] 绑定原始配置对象，实现动态 Label
        public MonitorItemConfig BoundConfig { get; set; }

        private string _key = "";
        
        // [Optimization] 缓存 InfoService 查找键
        private string? _dashColorKey;
        private const string PluginPrefix = "DASH.";

        public string Key 
        { 
            get => _key;
            set 
            {
                _key = UIUtils.Intern(value);
                // 预计算查找键
                // [Fix] 使用 OrdinalIgnoreCase 并正确处理可空性
                if (_key.StartsWith(PluginPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    // 移除 "DASH." 前缀 (长度=5) 并追加 ".Color"
                    // 使用 Span 避免中间子字符串分配 (CA1845)
                    _dashColorKey = UIUtils.Intern(string.Concat(_key.AsSpan(PluginPrefix.Length), ".Color".AsSpan()));
                }
                else
                {
                    _dashColorKey = null;
                }
            } 
        }

        private string _label = "";
        public string Label 
        {
            get 
            {
                // [Refactor] 使用统一的 Label 解析器
                string labelResolved = MetricLabelResolver.ResolveLabel(BoundConfig);
                if (!string.IsNullOrEmpty(labelResolved)) return labelResolved;

                return _label;
            }
            set => _label = UIUtils.Intern(value);
        }
        
        private string _shortLabel = "";
        public string ShortLabel 
        {
            get 
            {
                // [Refactor] 使用统一的 Label 解析器
                string shortResolved = MetricLabelResolver.ResolveShortLabel(BoundConfig);
                if (!string.IsNullOrEmpty(shortResolved)) return shortResolved;

                return _shortLabel;
            }
            set => _shortLabel = UIUtils.Intern(value);
        }
        
        public float? Value { get; set; } = null;
        public float DisplayValue { get; set; } = 0f;
        public string TextValue { get; set; } = null;

        // =============================
        // 缓存字段
        // =============================
        private float _cachedDisplayValue = -99999f; 
        private (bool Ac, bool Charging) _cachedPowerState = (false, false); // [Fix] 缓存完整电源状态
        private string _cachedNormalText = "";       // 完整文本 (值+单位)
        private string _cachedHorizontalText = "";   // 完整横屏文本
        
        // ★★★ [新增] 分离缓存 ★★★
        public string CachedValueText { get; private set; } = "";
        public string CachedUnitText { get; private set; } = "";
        public bool HasCustomUnit { get; private set; } = false; // 标记是否使用了自定义单位


        public int CachedColorState { get; private set; } = 0;
        public double CachedPercent { get; private set; } = 0.0;

        public Color GetTextColor(Theme t)
        {
            return _colorInit ? _textColor : UIUtils.GetStateColor(CachedColorState, t, true);
        }

        public Color GetBarColor(Theme t)
        {
            return _colorInit ? _barColor : UIUtils.GetStateColor(CachedColorState, t, false);
        }

        // =============================
        // 动画状态（由 UIController 的动画时钟逐帧推进）
        // =============================
        private const int HistoryCapacity = 40;
        private const float ColorTauMs = 160f;
        private const float HoverTauMs = 70f;

        private readonly float[] _history = new float[HistoryCapacity];
        private int _historyStart, _historyCount;
        private bool _colorInit;
        private Color _textColor, _barColor;

        /// <summary>悬停高亮的显现程度 0~1。</summary>
        public float HoverAmount { get; private set; }
        public bool HoverTarget { get; set; }

        public int HistoryCount => _historyCount;
        public float HistoryAt(int i) => _history[(_historyStart + i) % HistoryCapacity];

        public void PushHistory(float v)
        {
            if (_historyCount < HistoryCapacity)
            {
                _history[(_historyStart + _historyCount) % HistoryCapacity] = v;
                _historyCount++;
            }
            else
            {
                _history[_historyStart] = v;
                _historyStart = (_historyStart + 1) % HistoryCapacity;
            }
        }

        /// <summary>
        /// 推进一帧：数值按时间常数逼近采样值，告警色与悬停高亮平滑过渡。
        /// speed 即设置里的 AnimationSpeed，越大越快，≥0.9 时不做数值补间。
        /// 返回 true 表示仍在变化、需要继续重绘。
        /// </summary>
        public bool Animate(float dtMs, double speed, Theme t)
        {
            bool moving = false;

            if (Value.HasValue)
            {
                float target = Value.Value;
                float diff = target - DisplayValue;
                // 差距小到肉眼看不出（约 0.3%）就直接吸附，让动画时钟尽早停下、省 CPU
                float eps = Math.Max(0.01f, Math.Abs(target) * 0.003f);
                if (speed >= 0.9 || Math.Abs(diff) <= eps)
                {
                    DisplayValue = target;
                }
                else
                {
                    float tau = (float)(60 + (1 - Math.Clamp(speed, 0, 1)) * 360);
                    DisplayValue += diff * Ease(dtMs, tau);
                    moving = true;
                }
            }

            Color textTarget = UIUtils.GetStateColor(CachedColorState, t, true);
            Color barTarget = UIUtils.GetStateColor(CachedColorState, t, false);
            if (!_colorInit)
            {
                _textColor = textTarget;
                _barColor = barTarget;
                _colorInit = true;
            }
            else
            {
                float k = Ease(dtMs, ColorTauMs);
                moving |= StepColor(ref _textColor, textTarget, k);
                moving |= StepColor(ref _barColor, barTarget, k);
            }

            float hoverTarget = HoverTarget ? 1f : 0f;
            float hd = hoverTarget - HoverAmount;
            if (Math.Abs(hd) < 0.01f) HoverAmount = hoverTarget;
            else { HoverAmount += hd * Ease(dtMs, HoverTauMs); moving = true; }

            return moving;
        }

        private static float Ease(float dtMs, float tauMs) => 1f - MathF.Exp(-dtMs / tauMs);

        private static bool StepColor(ref Color c, Color target, float k)
        {
            if (c.ToArgb() == target.ToArgb()) return false;
            int Ch(int a, int b)
            {
                int v = (int)Math.Round(a + (b - a) * k);
                return v == a ? a + Math.Sign(b - a) : v; // 至少走一个色阶，避免尾段停住不动
            }
            c = Color.FromArgb(Ch(c.A, target.A), Ch(c.R, target.R), Ch(c.G, target.G), Ch(c.B, target.B));
            return true;
        }

        public string GetFormattedText(bool isHorizontal)
        {
            // [Debug & Fix] 1. Always update color state for Plugin Items FIRST
            if (_dashColorKey != null) // Use cached check
            {
                string colorVal = InfoService.Instance.GetValue(_dashColorKey);
                
                if (!string.IsNullOrEmpty(colorVal))
                {
                    if (int.TryParse(colorVal, out int state)) 
                    {
                        CachedColorState = state;
                    }
                }
                else
                {
                    CachedColorState = 0; // Default Safe if no color override
                }
            }

            // 2. Load Config (Optimized)
            var cfg = BoundConfig;
            if (cfg == null)
            {
                 // Fallback: This should rarely happen if initialized correctly
                 cfg = Settings.Load().MonitorItems.FirstOrDefault(x => x.Key == Key);
            }
            
            string? userFormat = isHorizontal ? cfg?.UnitTaskbar : cfg?.UnitPanel;
            HasCustomUnit = !string.IsNullOrEmpty(userFormat);

            // 3. Return TextValue (Plugin/Dashboard items)
           if (TextValue != null) 
            {
                // [Optimization] 统一使用 MetricUtils 处理单位逻辑
                // 1. 获取默认单位 (自动处理：插件查InfoService，硬件查表)
                var ctx = isHorizontal ? MetricUtils.UnitContext.Taskbar : MetricUtils.UnitContext.Panel;
                string defUnit = MetricUtils.GetUnitStr(Key, null, ctx);

                // 2. 确定最终单位 (自动处理：用户设为Null时用默认，否则用自定义)
                string finalUnit = MetricUtils.GetDisplayUnit(Key, defUnit, userFormat);

                // 3. 智能拼接：如果文本里还没包含这个单位，就拼上去
                if (!string.IsNullOrEmpty(finalUnit) && !TextValue.EndsWith(finalUnit))
                {
                    return TextValue + finalUnit;
                }

                return TextValue;
            }

            // 4. Numeric Value Processing (Hardware items)
            // [Fix] 增加充电状态检查：如果数值变了 OR (是电池相关项 AND 电源状态变了) -> 强制刷新
            bool isBat = Key.StartsWith("BAT", StringComparison.OrdinalIgnoreCase);
            var currentPower = MetricUtils.GetPowerStatus();
            bool powerChanged = isBat && (_cachedPowerState != currentPower);

            // [Fix] 降低缓存失效阈值：从 0.05 降低到 0.005
            // 之前的 0.05 太大，导致当数值从 0.06 变为 0.00 时，虽然 DisplayValue 变了，
            // 但因为 delta (0.06) 刚刚好大于 0.05 可能触发更新，
            // 但如果是 0.04 -> 0.00，delta=0.04 < 0.05，导致文本缓存不更新，UI 仍显示 0.04。
            // 考虑到显示精度通常为 0.00，阈值应至少小于 0.01。
            if (Math.Abs(DisplayValue - _cachedDisplayValue) > 0.005f || powerChanged)
            {
                _cachedDisplayValue = DisplayValue;
                if (isBat) _cachedPowerState = currentPower;

                // [Refactor] 使用新的原子函数分别构建普通和紧凑文本
                
                // === 1. 更新主界面缓存 (Panel) ===
                string? userFmtPanel = cfg?.UnitPanel;
                string valNormal = MetricUtils.GetValueStr(Key, DisplayValue, false, userFmtPanel);
                string unitNormal = MetricUtils.GetUnitStr(Key, DisplayValue, MetricUtils.UnitContext.Panel);
                
                string finalUnitPanel = MetricUtils.GetDisplayUnit(Key, unitNormal, userFmtPanel);
                _cachedNormalText = valNormal + finalUnitPanel;

                // === 2. 更新任务栏缓存 (Taskbar/Horizontal) ===
                // 逻辑修正：任务栏模式下，必须使用 Taskbar 上下文 (例如不带 /s)
                string? userFmtTaskbar = cfg?.UnitTaskbar;
                bool hasCustomTaskbar = !string.IsNullOrEmpty(userFmtTaskbar);
                
                // 自动模式：启用数值压缩 (Compact=true) 和 紧凑单位 (Taskbar Context)
                // 自定义模式：不做数值压缩 (false)，但单位仍需基于 Taskbar 上下文计算基础值
                bool compact = !hasCustomTaskbar;
                
                string valTaskbar = MetricUtils.GetValueStr(Key, DisplayValue, compact, userFmtTaskbar);
                string unitTaskbar = MetricUtils.GetUnitStr(Key, DisplayValue, MetricUtils.UnitContext.Taskbar);
                
                string finalUnitTaskbar = MetricUtils.GetDisplayUnit(Key, unitTaskbar, userFmtTaskbar);
                _cachedHorizontalText = valTaskbar + finalUnitTaskbar;

                // 更新公共属性以便调试 (显示当前请求模式的值)
                if (isHorizontal)
                {
                    CachedValueText = valTaskbar;
                    CachedUnitText = finalUnitTaskbar;
                    HasCustomUnit = hasCustomTaskbar;
                }
                else
                {
                    CachedValueText = valNormal;
                    CachedUnitText = finalUnitPanel;
                    HasCustomUnit = !string.IsNullOrEmpty(userFmtPanel);
                }

                // Only calculate color if NOT a plugin item (already handled above)
                if (!Key.StartsWith("DASH."))
                {
                    CachedColorState = MetricUtils.GetState(Key, DisplayValue);
                }
                
                CachedPercent = MetricUtils.GetProgressValue(Key, DisplayValue);
            }
            return isHorizontal ? _cachedHorizontalText : _cachedNormalText;
        }

        public MetricRenderStyle Style { get; set; } = MetricRenderStyle.StandardBar;
        public Rectangle Bounds { get; set; } = Rectangle.Empty;

        public Rectangle LabelRect;   
        public Rectangle ValueRect;   
        public Rectangle BarRect;     
        public Rectangle BackRect;    

        public void TickSmooth(double speed)
        {
            if (!Value.HasValue) return;
            float target = Value.Value;
            float diff = Math.Abs(target - DisplayValue);
            
            // ★★★ [Fix] 核心修复：微小差异直接吸附，而不是跳过 ★★★
            if (diff < 0.05f) 
            {
                DisplayValue = target;
                return;
            }

            // 大幅跳变或高速模式直接更新
            if (diff > 15f || speed >= 0.9) DisplayValue = target;
            else DisplayValue += (float)((target - DisplayValue) * speed);
        }
    }
}
