using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace LiteMonitor
{
    /// <summary>
    /// 全局动画帧计时器，用法与 System.Windows.Forms.Timer 相同（Interval / Tick / Start / Stop / Enabled）。
    /// WinForms 的 Timer 受系统时钟精度限制，最快约 64fps；所有动画改用它后统一跟随显示器刷新率
    /// （165Hz 屏即 165fps），各窗口、菜单、面板的流畅度一致。
    /// Interval ≤ 20 表示每个垂直同步都触发；更大的值按间隔节流（例如静止时降频轮询）。
    /// </summary>
    public sealed class FrameTimer : IDisposable
    {
        public int Interval { get; set; } = 15;
        public event EventHandler? Tick;
        public bool Enabled { get; private set; }

        private long _last;
        private bool _disposed;

        public void Start()
        {
            if (_disposed || Enabled) return;
            Enabled = true;
            _last = FrameClock.NowMs;
            FrameClock.Add(this);
        }

        public void Stop()
        {
            if (!Enabled) return;
            Enabled = false;
            FrameClock.Remove(this);
        }

        internal void Fire(long now)
        {
            if (!Enabled) return;
            if (Interval > 20 && now - _last < Interval - 2) return;
            _last = now;
            Tick?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            Stop();
            _disposed = true;
            Tick = null;
        }
    }

    /// <summary>
    /// 唯一的垂直同步时钟：后台线程等 DwmFlush（每次桌面合成返回一次），再把一帧投递到界面线程，
    /// 依次驱动所有运行中的 FrameTimer。界面线程来不及时自动合并，不会堆积；没有计时器在跑时线程休眠。
    /// </summary>
    internal static class FrameClock
    {
        private static readonly Stopwatch _clock = Stopwatch.StartNew();
        public static long NowMs => _clock.ElapsedMilliseconds;

        private static readonly List<FrameTimer> _active = new();
        private static readonly AutoResetEvent _wake = new(false);
        private static SynchronizationContext? _ui;
        private static Thread? _thread;
        private static volatile int _count;
        private static int _pending;

        // 只在界面线程调用
        public static void Add(FrameTimer t)
        {
            _ui ??= SynchronizationContext.Current as WindowsFormsSynchronizationContext ?? new WindowsFormsSynchronizationContext();
            if (!_active.Contains(t)) _active.Add(t);
            _count = _active.Count;
            if (_thread == null)
            {
                _thread = new Thread(Loop) { IsBackground = true, Name = "LiteMonitor.FrameClock", Priority = ThreadPriority.AboveNormal };
                _thread.Start();
            }
            _wake.Set();
        }

        public static void Remove(FrameTimer t)
        {
            _active.Remove(t);
            _count = _active.Count;
        }

        private static void Loop()
        {
            while (true)
            {
                if (_count == 0) { _wake.WaitOne(); continue; }
                // 合成关闭或失败（远程桌面等）时退回约 120fps 的睡眠节拍
                if (DwmFlush() != 0) Thread.Sleep(8);
                if (_count == 0) continue;
                if (Interlocked.Exchange(ref _pending, 1) != 0) continue; // 上一帧还没处理完：跳过这次同步
                try { _ui!.Post(_ => Dispatch(), null); }
                catch { Interlocked.Exchange(ref _pending, 0); Thread.Sleep(16); }
            }
        }

        private static void Dispatch()
        {
            Interlocked.Exchange(ref _pending, 0);
            long now = NowMs;
            foreach (var t in _active.ToArray())
            {
                try { t.Fire(now); }
                catch (Exception ex) { Debug.WriteLine("[FrameClock] " + ex.Message); }
            }
        }

        [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    }
}
