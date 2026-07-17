using System.ComponentModel;          // ISupportInitialize
using System.Windows.Input;
using System.Windows.Threading;       // Dispatcher
using TrackMonitor.Demo.Services;

namespace TrackMonitor.Demo.ViewModels;

/// <summary>
/// Demo 的 ViewModel。自从速度 / ETA / 状态都搬进控件后,这里只剩很薄一层:
///   - 用一个【后台线程】轮询循环周期 <c>Poll</c> 传感器、把位置中继给 <see cref="Position"/>;
///   - 管两种模式的"目标":
///       目标驱动 → 给一个具体目标(控件显示三角标记和 ETA);
///       随机巡航 → 把 <see cref="TargetPosition"/> 置 null(控件隐藏三角和 ETA),内部不断派随机点。
///
/// 轮询循环放在后台线程(<c>System.Timers.Timer.Elapsed</c> 在线程池线程触发),**不被 UI 线程的
/// 鼠标 Input 饿**,采样节奏稳、滑块不卡;每拍只把"设 Position"这一步 <c>InvokeAsync</c> 切回 UI 线程
/// (默认 Normal 优先级 > Input)。这正是当初根治"鼠标一动就卡"的办法——只是现在保留 poll 模型(传感器被动)。
/// 推进仿真在后台跑,所以按住标题栏按钮(模态循环冻结 UI 线程)期间机器仍在动,解冻后位置自然续上、不瞬移。
///
/// 本类实现 <see cref="ISupportInitialize"/>:Min/Max 由 XAML 在构造*之后*注入,故"起点贴最近端"放到 <see cref="EndInit"/> 做。
/// </summary>
public sealed class AxisMonitorViewModel : ViewModelBase, IDisposable, ISupportInitialize
{
    private const double ArriveEpsilon = 0.001;   // 到达容差:巡航判断"到点没"

    private IAxisSensor? _sensor;     // XAML(无参)路径下,传感器延迟到 EndInit 才按 Min/Max 定起点建出来
    private readonly Dispatcher _ui = System.Windows.Application.Current.Dispatcher;  // UI 调度器,后台线程靠它切回 UI 设属性
    private readonly Random _rng = new();

    // 后台轮询定时器:Elapsed 在线程池线程触发,不被鼠标 Input 饿(根治卡顿)。
    private readonly System.Timers.Timer _poll = new(50) { AutoReset = true };

    private bool _wandering;        // 是否处于随机巡航模式
    private double _wanderTarget;   // 巡航内部目标(只下发给传感器,不外显为 TargetPosition)

    /// <summary>XAML 无参构造:传感器延迟到 <see cref="EndInit"/>(那时 Min/Max 已就位)再建。</summary>
    public AxisMonitorViewModel() => InitCommands();

    /// <summary>注入构造(测试 / 生产):直接接入外部传感器。</summary>
    public AxisMonitorViewModel(IAxisSensor sensor)
    {
        InitCommands();
        AttachSensor(sensor);
    }

    private void InitCommands()
    {
        _poll.Elapsed += OnPoll;
        StartCommand  = new RelayCommand(StartMove);
        ResetCommand  = new RelayCommand(() => { TargetInput = 0; StartMove(); });   // 真复位到 0;0 越界时 StartMove 的夹紧会停在最近的那端
        WanderCommand = new RelayCommand(ToggleWander);
    }

    private void AttachSensor(IAxisSensor sensor)
    {
        _sensor = sensor;
        _poll.Start();
    }

    // ===== ISupportInitialize:XAML 设完所有属性(含 Min/Max)后回调 =====
    public void BeginInit() { }
    public void EndInit()
    {
        // 起点 = 离 0 最近的合法位置:0 在轨道内就是 0,否则贴最近的那端。
        double start = Clamp01(0);
        Position = start;
        if (_sensor is null)   // 注入路径下传感器已就位,只设起点不另建
            AttachSensor(new SimulatedAxisSensor(startPosition: start, maxSpeed: 12));
    }

    // ===== 绑定到控件的属性 =====
    private double _minimum;
    public double Minimum { get => _minimum; set => Set(ref _minimum, value); }

    private double _maximum = 100;
    public double Maximum { get => _maximum; set => Set(ref _maximum, value); }

    private double _position;
    public double Position { get => _position; set => Set(ref _position, value); }

    // 可空:null = 没有目标。巡航模式置 null,控件据此隐藏三角和 ETA。
    private double? _targetPosition;
    public double? TargetPosition { get => _targetPosition; set => Set(ref _targetPosition, value); }

    // 转交给控件的输入:模拟故障(true → 控件强制 Fault)
    private bool _isFaulted;
    public bool IsFaulted { get => _isFaulted; set => Set(ref _isFaulted, value); }

    // ===== 界面输入:目标位置 =====
    private double _targetInput = 70;
    public double TargetInput { get => _targetInput; set => Set(ref _targetInput, value); }

    public ICommand StartCommand { get; private set; } = null!;
    public ICommand ResetCommand { get; private set; } = null!;
    public ICommand WanderCommand { get; private set; } = null!;

    /// <summary>把一个值夹进 [Min,Max](自动兼容反向区间 Min&gt;Max)。</summary>
    private double Clamp01(double value)
        => Math.Clamp(value, Math.Min(Minimum, Maximum), Math.Max(Minimum, Maximum));

    /// <summary>目标驱动:下发一个具体目标,部件移动过去(并退出巡航)。</summary>
    public void StartMove()
    {
        _wandering = false;
        double target = Clamp01(TargetInput);   // 越界目标夹到最近端;Math.Clamp 在 min>max 时会抛异常,故先归正
        TargetPosition = target;                 // 有目标 → 控件显示三角标记和 ETA
        _sensor?.MoveTo(target);
    }

    /// <summary>随机巡航开关:开 → 在 [Min,Max] 里不停跑随机点;关 → 停在当前位置。</summary>
    private void ToggleWander()
    {
        _wandering = !_wandering;
        if (_wandering)
        {
            TargetPosition = null;        // 无目的地 → 控件隐藏三角和 ETA
            PickNextWanderTarget();
        }
        else
        {
            _sensor?.MoveTo(Position);    // 停在当前位置
        }
    }

    private void PickNextWanderTarget()
    {
        _wanderTarget = Minimum + _rng.NextDouble() * (Maximum - Minimum);
        _sensor?.MoveTo(_wanderTarget);   // 只告诉传感器去哪;不更新 TargetPosition
    }

    /// <summary>
    /// 轮询循环的一拍【后台线程】:推进传感器、读位置,再 InvokeAsync 切回 UI 线程设 Position。
    /// 巡航到点换下一个随机点的判断也放进 UI 回调里(那样 _wandering/_wanderTarget 只在 UI 线程读写)。
    /// </summary>
    private void OnPoll(object? sender, System.Timers.ElapsedEventArgs e)
    {
        if (_sensor is null || _ui.HasShutdownStarted) return;
        _sensor.Poll();                  // 后台推进仿真(真实传感器:刷新硬件读数)
        double p = _sensor.Position;
        _ui.InvokeAsync(() =>            // 设依赖属性必须回 UI 线程;默认 Normal 优先级 > 鼠标 Input
        {
            Position = p;
            if (_wandering && Math.Abs(p - _wanderTarget) < ArriveEpsilon)
                PickNextWanderTarget();   // 到一个随机点就换下一个 → 自然来回往复
        });
    }

    /// <summary>关窗时由宿主调用:停掉并释放后台轮询定时器(传感器是被动状态机,无需释放)。</summary>
    public void Dispose()
    {
        _poll.Stop();
        _poll.Dispose();
    }
}
