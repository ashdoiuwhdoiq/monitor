using System;
using System.Diagnostics;

namespace LiteMonitor
{
    /// <summary>
    /// 动效基础：与设计稿 CSS 一致的三次贝塞尔缓动，以及按时间求值的补间。
    /// 设计稿里用到的曲线：
    ///   Out      = cubic-bezier(.2,.8,.2,1)    —— 位移、高度、宽度的主缓动
    ///   Spring   = cubic-bezier(.34,1.56,.64,1)—— 分段滑块、箭头、开关、按钮回弹（有过冲）
    ///   SpringSoft = cubic-bezier(.34,1.35,.64,1) —— 详情卡、浮卡弹出（轻过冲）
    ///   InOut    = cubic-bezier(.65,0,.35,1)   —— 深浅模式圆形揭开
    /// </summary>
    public static class Motion
    {
        private static readonly Stopwatch _clock = Stopwatch.StartNew();
        public static long Now => FixedNow ?? _clock.ElapsedMilliseconds;

        /// <summary>仅供离线渲染动画帧（截图工具）使用：设定后 Now 固定为该值。</summary>
        public static long? FixedNow { get; set; }

        public static readonly Ease Out = new Ease(.2, .8, .2, 1);
        public static readonly Ease Spring = new Ease(.34, 1.56, .64, 1);
        public static readonly Ease SpringSoft = new Ease(.34, 1.35, .64, 1);
        public static readonly Ease InOut = new Ease(.65, 0, .35, 1);
        public static readonly Ease Linear = new Ease(0, 0, 1, 1);

        /// <summary>延迟 delay 毫秒后、经 dur 毫秒的进度（0~1，未缓动）。</summary>
        public static float Progress(long start, float delay, float dur, long now)
        {
            if (dur <= 0) return 1f;
            return Math.Clamp((now - start - delay) / dur, 0f, 1f);
        }
    }

    /// <summary>CSS cubic-bezier(x1,y1,x2,y2)：给定时间进度求缓动值（允许 y 超出 0~1 形成过冲）。</summary>
    public readonly struct Ease
    {
        private readonly double _x1, _y1, _x2, _y2;

        public Ease(double x1, double y1, double x2, double y2)
        {
            _x1 = x1; _y1 = y1; _x2 = x2; _y2 = y2;
        }

        public float Eval(float t)
        {
            if (t <= 0) return 0f;
            if (t >= 1) return 1f;
            // 先由 x 解出曲线参数 u（牛顿迭代，失败时二分），再求 y
            double u = t;
            for (int i = 0; i < 6; i++)
            {
                double x = Bez(u, _x1, _x2) - t;
                double dx = DBez(u, _x1, _x2);
                if (Math.Abs(x) < 1e-5) break;
                if (Math.Abs(dx) < 1e-6) { u = Bisect(t); break; }
                u -= x / dx;
            }
            u = Math.Clamp(u, 0, 1);
            return (float)Bez(u, _y1, _y2);
        }

        private double Bisect(double t)
        {
            double lo = 0, hi = 1, u = t;
            for (int i = 0; i < 24; i++)
            {
                u = (lo + hi) / 2;
                if (Bez(u, _x1, _x2) < t) lo = u; else hi = u;
            }
            return u;
        }

        private static double Bez(double u, double p1, double p2)
        {
            double v = 1 - u;
            return 3 * v * v * u * p1 + 3 * v * u * u * p2 + u * u * u;
        }

        private static double DBez(double u, double p1, double p2)
        {
            double v = 1 - u;
            return 3 * v * v * p1 + 6 * v * u * (p2 - p1) + 3 * u * u * (1 - p2);
        }
    }

    /// <summary>
    /// 单值补间：Set 新目标时从当前值出发，按给定时长与缓动走到目标；中途改目标会平滑接续。
    /// </summary>
    public sealed class Tween
    {
        private float _from, _to;
        private long _start;
        private float _dur, _delay;
        private Ease _ease = Motion.Out;

        public Tween(float value) { _from = _to = value; _dur = 0; }

        public float Target => _to;

        public void Set(float target, float durMs, Ease ease, float delayMs = 0)
        {
            if (Math.Abs(target - _to) < 1e-4f && _dur > 0) return;
            long now = Motion.Now;
            _from = Value(now);
            _to = target;
            _start = now;
            _dur = durMs;
            _delay = delayMs;
            _ease = ease;
        }

        public void Snap(float value)
        {
            _from = _to = value;
            _dur = 0;
        }

        public float Value() => Value(Motion.Now);

        public float Value(long now)
        {
            if (_dur <= 0) return _to;
            float p = Motion.Progress(_start, _delay, _dur, now);
            return _from + (_to - _from) * _ease.Eval(p);
        }

        public bool IsRunning(long now) => _dur > 0 && now - _start < _delay + _dur;
    }
}
