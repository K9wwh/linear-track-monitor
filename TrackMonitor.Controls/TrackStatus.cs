namespace TrackMonitor.Controls;

/// <summary>
/// 滑轨可动部件的运行状态 (running status)。
/// 单独做成枚举 (enum),让"状态"成为一个有名字的类型,而不是散落的魔法数字/字符串。
/// </summary>
public enum TrackStatus
{
    /// <summary>空闲 / 停止。</summary>
    Idle,

    /// <summary>运行中。</summary>
    Running,

    /// <summary>报警 / 故障。</summary>
    Fault
}
