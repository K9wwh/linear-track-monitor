namespace TrackMonitor.Demo.Services;

/// <summary>
/// 轴位置传感器抽象(轮询 / poll 模型)。
///
/// 自从速度 / ETA 的计算搬进控件、控件按节拍自行轮询 Position 后,源头不必再用事件"推",
/// 改成被消费方"拉":周期性调 <see cref="Poll"/> 刷新读数,再读 <see cref="Position"/>。
/// 真实传感器的 Poll 就是读一次硬件寄存器;模拟器的 Poll 则按真实 Δt 推进仿真。
/// 没有事件、没有后台线程,故也不需要 IDisposable。
/// </summary>
public interface IAxisSensor
{
    /// <summary>当前绝对位置(最近一次 Poll 的读数)。</summary>
    double Position { get; }

    /// <summary>下发一个目标位置,部件随后朝它移动(运动前的"通知目标")。</summary>
    void MoveTo(double target);

    /// <summary>刷新一次读数:真实传感器读硬件;模拟器按真实 Δt 推进仿真。</summary>
    void Poll();
}
