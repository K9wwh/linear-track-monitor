using System.Diagnostics;               // Stopwatch
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;   // DoubleAnimation 等动画类型
using System.Windows.Threading;         // DispatcherTimer

namespace TrackMonitor.Controls;

/// <summary>
/// 线性滑轨位置监控控件(只读显示控件 / display control)。
///
/// 职责:把传感器回传的"位置 / 目标 / 速度 / 状态"等数值,显示成滑轨上一个滑块的几何位置。
/// 无外观控件 (lookless control):本类只管"数据"(依赖属性)和"行为"(数值→像素映射),
/// 长相 100% 来自 Themes/Generic.xaml。
/// </summary>
[TemplatePart(Name = PartTrack,        Type = typeof(Canvas))]
[TemplatePart(Name = PartThumb,        Type = typeof(FrameworkElement))]
[TemplatePart(Name = PartTargetMarker, Type = typeof(FrameworkElement))]
public class LinearTrackMonitor : Control
{
    private const string PartTrack        = "PART_Track";
    private const string PartThumb        = "PART_Thumb";
    private const string PartTargetMarker = "PART_TargetMarker";

    // 平滑动画时长:与传感器 100ms 节奏对齐,使滑块一段接一段连续滑动、不卡顿。
    public Duration AnimationDuration
    {
        get => (Duration)GetValue(AnimationDurationProperty);
        set => SetValue(AnimationDurationProperty, value);
    }
    public static readonly DependencyProperty AnimationDurationProperty =
        DependencyProperty.Register(nameof(AnimationDuration), typeof(Duration),
            typeof(LinearTrackMonitor),
            new PropertyMetadata(new Duration(TimeSpan.FromMilliseconds(100))));

    static LinearTrackMonitor()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(LinearTrackMonitor),
            new FrameworkPropertyMetadata(typeof(LinearTrackMonitor)));
    }

    // ======================== 速度 / ETA 引擎(控件内部计算)========================
    // 设计:控件自带一个 DispatcherTimer,按 SamplePeriod 节拍采样 Position,用 Stopwatch
    // 量真实 Δt 做差分 (differencing) 得瞬时速度,再用滑动平均 (SMA) 平滑;有目标时顺带算 ETA。
    // 于是使用者只喂 Position 就能直接拿到 Speed / Eta,不必自己算。

    // Render 优先级(7,高于鼠标 Input 5):鼠标移动 / 拖拽时引擎计算不被饿停。
    // 原默认 Background(4<Input)会被鼠标饿着 → 速度/状态读数发木。
    // 注:Win32 模态循环(按住标题栏按钮、拖动/缩放窗口边框)会把整个 UI 线程扣在嵌套消息循环里,
    //     届时任何 DispatcherTimer 与 DoubleAnimation 都暂停——这是 WPF 固有行为,优先级救不了;
    //     但循环结束后引擎按真实 Δt 正确续上(Δpos 与 dt 同跨那段空档,速度仍准,不会乱跳)。
    private readonly DispatcherTimer _engine = new(DispatcherPriority.Render);
    private readonly Stopwatch _clock = new();           // 量两拍之间的真实时间差
    private readonly Queue<double> _speedSamples = new();// 滑动平均的窗口缓存
    private double _lastSampledPosition;                 // 上一拍的位置,用来差分
    private long _lastTicks;                             // 上一拍的 Stopwatch 计数
    private double _rawSpeed;                            // _rawSpeed 用于计算 Status
    private const double SpeedEpsilon = 1e-3;            // 速度近 0 时不算 ETA,避免除以极小数得到天文数字

    public LinearTrackMonitor()
    {
        _engine.Tick += OnEngineTick;
        // 表只在控件挂到可视树上时跑,卸载即停 —— 不漏定时器 (timer leak)。
        Loaded   += (_, _) => StartEngine();
        Unloaded += (_, _) => StopEngine();
    }

    private void StartEngine()
    {
        _lastSampledPosition = Position;
        _speedSamples.Clear();
        _clock.Restart();
        _lastTicks = 0;
        // DispatcherTimer.Interval 默认100ms可随时改,非正值纠正为100ms,避免抛异常
        _engine.Interval = SamplePeriod > TimeSpan.Zero ? SamplePeriod : TimeSpan.FromMilliseconds(100);
        _engine.Start();
    }

    private void StopEngine()
    {
        _engine.Stop();
        _clock.Stop();
    }

    /// <summary>每个采样周期:量真实 Δt → 差分得瞬时速度 → SMA 平滑 → 更新 Speed/Eta。</summary>
    private void OnEngineTick(object? sender, EventArgs e)
    {
        long now = _clock.ElapsedTicks;
        double dt = (now - _lastTicks) / (double)Stopwatch.Frequency;
        _lastTicks = now;
        if (dt <= 0) return;   // 时钟还没走,跳过这一拍

        double current = Position;
        _rawSpeed = (current - _lastSampledPosition) / dt;   // 正=朝 Max,负=朝 Min
        _lastSampledPosition = current;

        Speed = Smooth(_rawSpeed);
        UpdateEta();
        UpdateStatus();
    }

    /// <summary>滑动平均 (simple moving average):窗口 = SpeedSmoothingWindow(至少 1,=1 即不平滑)。</summary>
    private double Smooth(double rawSpeed)
    {
        int window = Math.Max(1, SpeedSmoothingWindow);
        _speedSamples.Enqueue(rawSpeed);
        while (_speedSamples.Count > window)
            _speedSamples.Dequeue();   // 窗口缩小时,多余的旧样本下一拍挤出

        double sum = 0;
        foreach (double s in _speedSamples) sum += s;
        return sum / _speedSamples.Count;
    }

    /// <summary>有目标且速度不近 0 才算 ETA = 剩余距离 / 速度大小;否则置 0。</summary>
    private void UpdateEta()
    {
        if (TargetPosition is double target && Math.Abs(Speed) > SpeedEpsilon)
            Eta = Math.Abs(target - Position) / Math.Abs(Speed);
        else
            Eta = 0.0;
    }

    /// <summary>
    /// 由内部数据推断状态:Fault 优先(外部喂的 IsFaulted),否则按速度大小判 Running / Idle。
    /// IsFaulted 是机器才知道的外部条件,控件无法从位置自行推断,故保留为输入。
    /// 运行判断使用rawSpeed,而非平滑后的 Speed,避免平滑后从 Running 切换为 Idle 的延迟。
    /// </summary>
    private void UpdateStatus()
    {
        Status = IsFaulted               ? TrackStatus.Fault
               : Math.Abs(_rawSpeed) > MovingThreshold ? TrackStatus.Running
               :                          TrackStatus.Idle;
    }

    // ======================== 行为:坐标映射 ========================

    private Canvas? _track;
    private FrameworkElement? _thumb;
    private FrameworkElement? _targetMarker;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_track is not null)        _track.SizeChanged        -= OnLayoutSizeChanged;
        if (_thumb is not null)        _thumb.SizeChanged        -= OnLayoutSizeChanged;
        if (_targetMarker is not null) _targetMarker.SizeChanged -= OnLayoutSizeChanged;

        _track        = GetTemplateChild(PartTrack) as Canvas;
        _thumb        = GetTemplateChild(PartThumb) as FrameworkElement;
        _targetMarker = GetTemplateChild(PartTargetMarker) as FrameworkElement;

        if (_track is not null)        _track.SizeChanged        += OnLayoutSizeChanged;
        // 滑块/三角首次拿到真实尺寸时重新居中定位 —— 尤其三角平时 Collapsed 不被测量,
        // 从隐藏变可见那一刻 ActualWidth 才从 0 变真值,SizeChanged 会再触发一次 UpdateVisual。
        if (_thumb is not null)        _thumb.SizeChanged        += OnLayoutSizeChanged;
        if (_targetMarker is not null) _targetMarker.SizeChanged += OnLayoutSizeChanged;

        UpdateVisual();
    }

    private void OnLayoutSizeChanged(object sender, SizeChangedEventArgs e) => UpdateVisual();

    /// <summary>映射 Position/TargetPosition 到像素位置,并更新百分比读数。</summary>
    private void UpdateVisual()
    {
        Percentage = Fraction(Position) * 100.0;   // 百分比纯数值,先算先更新

        if (_track is null) return;

        double w = _track.ActualWidth, h = _track.ActualHeight;
        if (w <= 0 || h <= 0) return;   // 还没完成布局,等 SizeChanged 再触发一次

        PlaceElement(_thumb, Position, w, h);
        // 目标可空:有值才摆三角;为 null 时三角已被模板隐藏,这里直接跳过。
        if (TargetPosition is double target)
            PlaceElement(_targetMarker, target, w, h);
    }

    /// <summary>
    /// 值 → 0~1 比例(整个控件的"心脏"):防除零、越界贴边。
    /// Min/Max 不限正负与大小:`(value-Min)/(Max-Min)` 对反向行程(Max&lt;Min)天然成立
    /// —— value=Min → 0、value=Max → 1。只在 range==0(零长度行程)时退化为 0。
    /// </summary>
    private double Fraction(double value)
    {
        double range = Maximum - Minimum;
        double f = range != 0 ? (value - Minimum) / range : 0.0;
        return Math.Clamp(f, 0.0, 1.0);
    }

    /// <summary>
    /// 把元素按比例摆到轨道上,按 Orientation 选轴:
    ///   水平 → 动 Canvas.Left,坐标 = 比例 × 轨道宽(Min 左、Max 右);
    ///   垂直 → 动 Canvas.Top, 坐标 = (1−比例) × 轨道高(Min 下、Max 上,故反转)。
    /// 交叉轴的居中由模板里的静态 Canvas.Top/Left 负责,这里不碰。
    /// </summary>
    private void PlaceElement(FrameworkElement? element, double value, double trackWidth, double trackHeight)
    {
        if (element is null) return;

        // 元素刚从 Collapsed 变可见、本轮还没测量(ActualWidth/Height=0)→ 先别摆:
        // 否则下面减 ActualWidth/2 时减的是 0,等于按"左上角"而非中心定位,会偏出半个身位。
        // 它测量完后,OnApplyTemplate 挂的 SizeChanged 会再调一次 UpdateVisual,那时尺寸就对了。
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0) return;

        double f = Fraction(value);

        DependencyProperty axis;
        double to;
        if (Orientation == Orientation.Vertical)
        {
            axis = Canvas.TopProperty;
            to = (1.0 - f) * trackHeight - element.ActualHeight / 2.0;
        }
        else
        {
            axis = Canvas.LeftProperty;
            to = f * trackWidth - element.ActualWidth / 2.0;
        }

        // 由 AnimationDuration 统一控制:0 → 瞬时(卡顿版),否则平滑。要瞬时就解开下面分支。
        if (AnimationDuration.HasTimeSpan && AnimationDuration.TimeSpan == TimeSpan.Zero)
            MoveInstant(element, axis, to);
        else
            MoveAnimated(element, axis, to);
        //MoveAnimated(element, axis, to);
    }

    /// <summary>① 瞬时定位:立即把元素的指定轴(Canvas.Left 或 Canvas.Top)设为 to。</summary>
    private static void MoveInstant(FrameworkElement element, DependencyProperty axis, double to)
        => element.SetValue(axis, to);

    /// <summary>
    /// ② 动画定位:DoubleAnimation 在 AnimationDuration 内,从元素当前值平滑滑到 to。
    /// 不写 From → 从当前值起,每帧续上一段、连续不断。轴(Left/Top)由调用方给。
    /// </summary>
    private void MoveAnimated(FrameworkElement element, DependencyProperty axis, double to)
    {
        // 首帧该轴还没值(Canvas.Left/Top 默认 NaN),动画从 NaN 起步会抛异常;先直接落位。
        if (double.IsNaN((double)element.GetValue(axis)))
        {
            element.SetValue(axis, to);
            return;
        }
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = AnimationDuration,
            // EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        element.BeginAnimation(axis, animation);
    }

    /// <summary>共享变更回调:Position/Minimum/Maximum 任一变化都重新映射。</summary>
    private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((LinearTrackMonitor)d).UpdateVisual();

    /// <summary>
    /// TargetPosition 专用回调:先同步只读 HasTarget(驱动三角/ETA 显隐),再重新映射。
    /// </summary>
    private static void OnTargetPositionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (LinearTrackMonitor)d;
        control.HasTarget = ((double?)e.NewValue).HasValue;
        control.UpdateVisual();
    }

    /// <summary>有没有目标(只读):TargetPosition 非 null 时为 true。模板据此显隐三角标记和 ETA。</summary>
    public bool HasTarget
    {
        get => (bool)GetValue(HasTargetProperty);
        private set => SetValue(HasTargetPropertyKey, value);
    }
    private static readonly DependencyPropertyKey HasTargetPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(HasTarget), typeof(bool), typeof(LinearTrackMonitor),
            new PropertyMetadata(false));
    public static readonly DependencyProperty HasTargetProperty = HasTargetPropertyKey.DependencyProperty;

    // ======================== 只读计算属性:百分比 ========================

    /// <summary>当前位置在行程中的百分比 0~100(只读:外部能绑定看,但不能赋值)。</summary>
    public double Percentage
    {
        get => (double)GetValue(PercentageProperty);
        private set => SetValue(PercentagePropertyKey, value);
    }
    private static readonly DependencyPropertyKey PercentagePropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(Percentage), typeof(double), typeof(LinearTrackMonitor),
            new PropertyMetadata(0.0));
    public static readonly DependencyProperty PercentageProperty = PercentagePropertyKey.DependencyProperty;

    // ======================== 数据:依赖属性 ========================
    // (下面 4 个"影响几何"的属性在元数据里挂了 OnVisualPropertyChanged 回调)

    /// <summary>行程起点,默认 0。</summary>
    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }
    public static readonly DependencyProperty MinimumProperty =
        DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(LinearTrackMonitor),
            new PropertyMetadata(0.0, OnVisualPropertyChanged));

    /// <summary>行程终点 = 总行程 stroke,默认 100。</summary>
    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }
    public static readonly DependencyProperty MaximumProperty =
        DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(LinearTrackMonitor),
            new PropertyMetadata(100.0, OnVisualPropertyChanged));

    /// <summary>当前绝对位置(传感器每 100ms 回传)。</summary>
    public double Position
    {
        get => (double)GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }
    public static readonly DependencyProperty PositionProperty =
        DependencyProperty.Register(nameof(Position), typeof(double), typeof(LinearTrackMonitor),
            new PropertyMetadata(0.0, OnVisualPropertyChanged));

    /// <summary>
    /// 目标位置(可选 / optional)。null = 没有目标:不画目标标记、不显示 ETA。
    /// 有值时运动前下发,画面上以三角标记 (marker) 显示。
    /// </summary>
    public double? TargetPosition
    {
        get => (double?)GetValue(TargetPositionProperty);
        set => SetValue(TargetPositionProperty, value);
    }
    public static readonly DependencyProperty TargetPositionProperty =
        DependencyProperty.Register(nameof(TargetPosition), typeof(double?), typeof(LinearTrackMonitor),
            new PropertyMetadata(null, OnTargetPositionChanged));

    /// <summary>当前速度(正 = 朝 Maximum,负 = 朝 Minimum)。只读:由内部引擎算出,外部只能绑定来看。</summary>
    public double Speed
    {
        get => (double)GetValue(SpeedProperty);
        private set => SetValue(SpeedPropertyKey, value);
    }
    private static readonly DependencyPropertyKey SpeedPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(Speed), typeof(double), typeof(LinearTrackMonitor),
            new PropertyMetadata(0.0));
    public static readonly DependencyProperty SpeedProperty = SpeedPropertyKey.DependencyProperty;

    /// <summary>预计剩余到达时间 ETA(秒)。只读:由内部引擎算出(无目标时为 0)。</summary>
    public double Eta
    {
        get => (double)GetValue(EtaProperty);
        private set => SetValue(EtaPropertyKey, value);
    }
    private static readonly DependencyPropertyKey EtaPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(Eta), typeof(double), typeof(LinearTrackMonitor),
            new PropertyMetadata(0.0));
    public static readonly DependencyProperty EtaProperty = EtaPropertyKey.DependencyProperty;

    /// <summary>滑动平均窗口 (moving-average window):用最近 N 拍的原始速度求平均来平滑读数。默认 5;=1 即不平滑。</summary>
    public int SpeedSmoothingWindow
    {
        get => (int)GetValue(SpeedSmoothingWindowProperty);
        set => SetValue(SpeedSmoothingWindowProperty, value);
    }
    public static readonly DependencyProperty SpeedSmoothingWindowProperty =
        DependencyProperty.Register(nameof(SpeedSmoothingWindow), typeof(int), typeof(LinearTrackMonitor),
            new PropertyMetadata(5));

    /// <summary>内部引擎的采样周期,默认 100ms。应与数据回传节奏大致对齐。</summary>
    public TimeSpan SamplePeriod
    {
        get => (TimeSpan)GetValue(SamplePeriodProperty);
        set => SetValue(SamplePeriodProperty, value);
    }
    public static readonly DependencyProperty SamplePeriodProperty =
        DependencyProperty.Register(nameof(SamplePeriod), typeof(TimeSpan), typeof(LinearTrackMonitor),
            new PropertyMetadata(TimeSpan.FromMilliseconds(100), OnSamplePeriodChanged));

    private static void OnSamplePeriodChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (LinearTrackMonitor)d;
        var period = (TimeSpan)e.NewValue;
        if (period > TimeSpan.Zero)
            control._engine.Interval = period;   // DispatcherTimer.Interval 可随时改;非正值会抛异常,先判
    }

    /// <summary>运行状态:空闲 / 运行 / 报警。只读:由内部 UpdateStatus 依据速度和 IsFaulted 推断。</summary>
    public TrackStatus Status
    {
        get => (TrackStatus)GetValue(StatusProperty);
        private set => SetValue(StatusPropertyKey, value);
    }
    private static readonly DependencyPropertyKey StatusPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(Status), typeof(TrackStatus), typeof(LinearTrackMonitor),
            new PropertyMetadata(TrackStatus.Idle));
    public static readonly DependencyProperty StatusProperty = StatusPropertyKey.DependencyProperty;

    /// <summary>是否报警(输入):机器才知道的外部条件,为 true 时强制 Status=Fault。默认 false。</summary>
    public bool IsFaulted
    {
        get => (bool)GetValue(IsFaultedProperty);
        set => SetValue(IsFaultedProperty, value);
    }
    public static readonly DependencyProperty IsFaultedProperty =
        DependencyProperty.Register(nameof(IsFaulted), typeof(bool), typeof(LinearTrackMonitor),
            new PropertyMetadata(false, OnStatusInputChanged));

    /// <summary>判定"在动"的速度阈值:|速度| 超过它才算 Running,否则 Idle。默认 0.1。</summary>
    public double MovingThreshold
    {
        get => (double)GetValue(MovingThresholdProperty);
        set => SetValue(MovingThresholdProperty, value);
    }
    public static readonly DependencyProperty MovingThresholdProperty =
        DependencyProperty.Register(nameof(MovingThreshold), typeof(double), typeof(LinearTrackMonitor),
            new PropertyMetadata(0.1, OnStatusInputChanged));

    /// <summary>IsFaulted / MovingThreshold 变化时立即重判状态(不必等下一拍引擎)。</summary>
    private static void OnStatusInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((LinearTrackMonitor)d).UpdateStatus();

    /// <summary>滑轨方向:水平 / 垂直(复用 WPF 内置枚举)。P6 才真正用到。</summary>
    public Orientation Orientation
    {
        get => (Orientation)GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }
    public static readonly DependencyProperty OrientationProperty =
        DependencyProperty.Register(nameof(Orientation), typeof(Orientation), typeof(LinearTrackMonitor),
            new PropertyMetadata(Orientation.Horizontal));
}
