using System;
using System.Windows.Forms;

namespace LiteMonitor.src.UI.Graphite
{
    /// <summary>
    /// 每个垂直同步调用一次 frame（跟随显示器刷新率），静止时 Stop。
    /// 与全局 FrameClock 共用同一个时钟，所有动画同频。
    /// </summary>
    internal sealed class VsyncPump : IDisposable
    {
        private readonly FrameTimer _timer = new() { Interval = 0 };

        public VsyncPump(Control owner, Action frame)
        {
            _timer.Tick += (_, __) => { if (!owner.IsDisposed) frame(); };
        }

        public bool Running => _timer.Enabled;
        public void Start() => _timer.Start();
        public void Stop() => _timer.Stop();
        public void Dispose() => _timer.Dispose();
    }
}
