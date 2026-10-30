using System;
using System.Drawing;

namespace LiteMonitor.src.UI.Graphite
{
    /// <summary>
    /// 深浅模式切换的中转：任何窗口里的明暗按钮都通过它切换整个程序；
    /// 切换前通知所有打开的窗口先存下旧画面（用于圆形揭开），切换后通知它们重绘。
    /// </summary>
    public static class UIModeHub
    {
        /// <summary>配色即将改变：参数为发起切换的窗口（可空）与圆心（屏幕坐标，可空）。</summary>
        public static event Action<object?, Point?>? BeforeChange;
        /// <summary>配色已经改变。</summary>
        public static event Action? Changed;

        /// <summary>由主窗口注册：真正执行切换（保存设置、更新面板 / 菜单 / 主题）。参数为模式。</summary>
        public static Action<string>? Apply { get; set; }

        public static void Request(string mode, object? source, Point? screenOrigin)
        {
            BeforeChange?.Invoke(source, screenOrigin);
            Apply?.Invoke(mode);
            Changed?.Invoke();
        }

        internal static void RaiseBefore(object? source, Point? origin) => BeforeChange?.Invoke(source, origin);
        internal static void RaiseChanged() => Changed?.Invoke();
    }
}
