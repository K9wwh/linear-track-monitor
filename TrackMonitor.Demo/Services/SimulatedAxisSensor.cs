using System.Diagnostics;   // Stopwatch

namespace TrackMonitor.Demo.Services;

/// <summary>
/// 模拟传感器(轮询版 / 被动状态机)。
///
/// 不再自带定时器 / 线程 / 事件:消费方(VM)在自己的轮询循环里周期调 <see cref="Poll"/>,
/// 每次按"距上次 Poll 的真实 Δt"朝目标随机推进一段;速度由控件从 Position 流自行差分还原。
/// 用 Stopwatch 在 Poll 里量真实 Δt,所以即便轮询节奏忽快忽慢,推进的物理速度也保持一致。
/// </summary>
public sealed class SimulatedAxisSensor : IAxisSensor
{
    private readonly Random _random = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly double _maxSpeed;        // 物理速度上限(单位/秒)
    private long _lastTicks;                  // 上次 Poll 的 Stopwatch 计时点
    private double _target;

    public double Position { get; private set; }

    public SimulatedAxisSensor(double startPosition = 0, double maxSpeed = 120)
    {
        Position   = startPosition;
        _target    = startPosition;
        _maxSpeed  = maxSpeed;
        _lastTicks = _clock.ElapsedTicks;
    }

    public void MoveTo(double target) => _target = target;

    public void Poll()
    {
        // 真实 Δt:量两次 Poll 之间的实际时间,推进距离 = 速度 × Δt
        long now = _clock.ElapsedTicks;
        double dt = (now - _lastTicks) / (double)Stopwatch.Frequency;
        _lastTicks = now;

        double remaining = _target - Position;
        if (remaining == 0) return;

        double velocity = _maxSpeed * (0.5 + _random.NextDouble());   // 每拍随机速度,模拟真实波动
        double step = velocity * dt;
        Position = step >= Math.Abs(remaining)
            ? _target                                   // 够近就贴上(正反都防过冲)
            : Position + Math.Sign(remaining) * step;   // 按方向推进
    }
}
