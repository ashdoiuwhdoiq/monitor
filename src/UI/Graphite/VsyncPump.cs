using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace LiteMonitor.src.UI.Graphite
{
    /// <summary>
    /// 跟显示器刷新率走的帧泵。WinForms 的 Timer 受系统时钟精度限制，最快约 64fps（15.6ms 一跳），
    /// 165Hz 屏上看着就是卡。这里用后台线程等 DwmFlush（每次桌面合成返回一次，即每个垂直同步），
    /// 再把一帧投递到界面线程；界面线程来不及时自动合并，不会堆积。只在有动画时运行，静止即停。
    /// </summary>
    internal sealed class VsyncPump : IDisposable
    {
        private readonly Control _owner;
        private readonly Action _frame;
        private readonly AutoResetEvent _wake = new(false);
        private Thread? _thread;
        private volatile bool _run, _disposed;
        private int _pending;

        public VsyncPump(Control owner, Action frame)
        {
            _owner = owner;
            _frame = frame;
        }

        public bool Running => _run;

        public void Start()
        {
            if (_disposed || _run) return;
            _run = true;
            if (_thread == null)
            {
                _thread = new Thread(Loop) { IsBackground = true, Name = "LiteMonitor.Vsync", Priority = ThreadPriority.AboveNormal };
                _thread.Start();
            }
            _wake.Set();
        }

        public void Stop() => _run = false;

        private void Loop()
        {
            while (!_disposed)
            {
                if (!_run) { _wake.WaitOne(); continue; }
                // 合成被关闭或调用失败（远程桌面等）时退回约 120fps 的睡眠节拍
                if (DwmFlush() != 0) Thread.Sleep(8);
                if (!_run || _disposed) continue;
                if (Interlocked.Exchange(ref _pending, 1) != 0) continue; // 上一帧还没画完：跳过这次同步
                try
                {
                    if (!_owner.IsHandleCreated || _owner.IsDisposed) { _pending = 0; Thread.Sleep(16); continue; }
                    _owner.BeginInvoke(new Action(() =>
                    {
                        Interlocked.Exchange(ref _pending, 0);
                        if (_run && !_disposed) _frame();
                    }));
                }
                catch
                {
                    Interlocked.Exchange(ref _pending, 0);
                    Thread.Sleep(16);
                }
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _run = false;
            _wake.Set();
        }

        [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    }
}
