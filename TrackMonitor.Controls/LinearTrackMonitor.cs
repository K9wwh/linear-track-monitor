using System.Diagnostics;               // Stopwatch
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;   // DoubleAnimation 等动画类型
using System.Windows.Threading;         // DispatcherTimer

namespace TrackMonitor.Controls;

/// <summary>
/// 线性滑轨位置监控控件(只读显示控件 / display control)。
///
/// 职责:把传感器回传的**位置**显示成滑轨上一个滑块的几何位置,并**自带计算引擎** ——
/// 调用方只需喂 <see cref="Position"/>(外加可选的 <see cref="TargetPosition"/> 与 <see cref="IsFaulted"/>),
/// <see cref="Speed"/> / <see cref="Eta"/> / <see cref="Status"/> / <see cref="Percentage"/> 全由控件内部算出,均为只读。
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

    /// <summary>
    /// 滑块补间动画时长,默认 100ms(与传感器回传节奏对齐,使滑块一段接一段连续滑动、不卡顿)。
    /// 设为 0 即瞬时定位、不做补间;运行时随时可改。
    /// 凡不是"正的具体时长"的取值(<c>0</c>、<c>Duration.Forever</c>、<c>Duration.Automatic</c>)一律按瞬时处理 ——
    /// 尤其 <c>Forever</c> 若真去做动画会让滑块永久停住而数字读数照常刷新,那是最难察觉的失效方式。
    /// (负值无需处理:<c>Duration</c> 的构造函数本身就拒收负 <c>TimeSpan</c>,值根本到不了这里。)
    /// ⚠️ 一个**极长但合法**的时长(如误写成 100 天)同样会让滑块看上去冻住 —— 它确实在动,只是每秒几微米。
    /// 见 XAML 格式说明:裸数字是按"天"解析的。
    /// </summary>
    /// <remarks>
    /// ⚠️ XAML 字符串格式是 <c>[d.]hh:mm:ss[.fff]</c>:<c>0:0:0.1</c> = 100ms、<c>0</c> = 瞬时。
    /// <b>裸数字按"天"解析</b>(<c>100</c> 表示 100 天),而 <c>0.1</c> 会解析失败抛 XamlParseException。
    /// 代码里赋值请用 <c>new Duration(TimeSpan.FromMilliseconds(...))</c>。
    /// </remarks>
    public Duration AnimationDuration
    {
        get => (Duration)GetValue(AnimationDurationProperty);
        set => SetValue(AnimationDurationProperty, value);
    }
    /// <summary>标识 <see cref="AnimationDuration"/> 依赖属性。</summary>
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
    private bool _placeInstantly;                        // 切方向的那一帧强制瞬时定位,见 OnOrientationChanged
    private const double SpeedEpsilon = 1e-3;            // 速度近 0 时不算 ETA,避免除以极小数得到天文数字

    private static readonly TimeSpan DefaultSamplePeriod = TimeSpan.FromMilliseconds(100);
    // DispatcherTimer.Interval 的硬上界:超过 int.MaxValue 毫秒(≈24.9 天)赋值即抛 ArgumentOutOfRangeException。
    private static readonly TimeSpan MaxSamplePeriod = TimeSpan.FromMilliseconds(int.MaxValue);
    // 滑动平均窗口上限:窗口是队列长度,不封顶的话超大窗口会让样本只进不出、长期运行吃内存。
    private const int MaxSmoothingWindow = 1000;

    /// <summary>
    /// 创建控件实例:挂上内部引擎的 Tick,并让引擎随控件加载 / 卸载自动启停(不漏定时器)。
    /// </summary>
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
        // SamplePeriod 有 CoerceSamplePeriod 兜着,取到的一定落在 (0, MaxSamplePeriod],可直接用。
        _engine.Interval = SamplePeriod;
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
        double current = Position;

        // 坏采样防护 (NaN / ±∞):整拍跳过,且**不推进** _lastTicks —— 让 Δ位置 与 Δt 同跨这段空档,
        // 下一个好采样算出的速度依然准(与"窗口拖拽把引擎冻住"是同一个道理)。
        // 若只跳过差分却推进了时钟,下一拍会拿"两拍的位移 ÷ 一拍的时间",速度虚高一倍。
        // 更要紧的是:NaN 一旦进了 _speedSamples,整个窗口的平均值就都是 NaN,读数要坏 N 拍。
        if (!double.IsFinite(current)) return;

        // 上一个基准本身是坏的(例如控件加载时 Position 就是 NaN,被 StartEngine 当成了基准):
        // 用这个好值重新立基准,这一拍不出速度。
        if (!double.IsFinite(_lastSampledPosition))
        {
            _lastSampledPosition = current;
            _lastTicks = _clock.ElapsedTicks;
            return;
        }

        long now = _clock.ElapsedTicks;
        double dt = (now - _lastTicks) / (double)Stopwatch.Frequency;
        _lastTicks = now;
        if (dt <= 0) return;   // 时钟还没走,跳过这一拍

        double raw = (current - _lastSampledPosition) / dt;   // 符号 = Position 增减的方向,与 Min/Max 谁大谁小无关

        // 输入有限不代表结果有限:两次采样跨度极大时(如 ±1e308 之间跳)相减会溢出成 ±∞。
        // 非有限的样本绝不能进滑动平均队列:一个 ±∞ 会让整个窗口的平均值也变成 ±∞,
        // 要等它被挤出队列(最多 window 拍,即最长 1000 拍)读数才恢复。
        // 重设基准再走人,让下一拍从这个位置干净地重新差分。
        if (!double.IsFinite(raw))
        {
            _lastSampledPosition = current;
            return;
        }

        _rawSpeed = raw;
        _lastSampledPosition = current;

        Speed = Smooth(_rawSpeed);
        UpdateEta();
        UpdateStatus();
    }

    /// <summary>滑动平均 (simple moving average):窗口 = SpeedSmoothingWindow(至少 1,=1 即不平滑)。</summary>
    private double Smooth(double rawSpeed)
    {
        // 属性已由 CoerceSmoothingWindow 在提交前夹进 1..1000,这里直接取用。
        int window = SpeedSmoothingWindow;
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
        if (TargetPosition is double target && double.IsFinite(target) && Math.Abs(Speed) > SpeedEpsilon)
        {
            // 折半再相减,避免目标与当前位置分居 double 两端时距离溢出成 ∞
            // (Fraction 对 range 用的是同一手法)。行程区间允许跨满 double,这就是在合同之内的输入。
            double eta = Math.Abs(target / 2.0 - Position / 2.0) / Math.Abs(Speed) * 2.0;
            // 极端组合(距离极大 + 速度贴着阈值)仍可能溢出:给一个"极大但有限"的值。
            // 显示 ∞ 或 0 都会误导 —— 后者等于谎报"马上就到"。
            Eta = double.IsFinite(eta) ? eta : double.MaxValue;
        }
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

    /// <summary>
    /// 模板套用后按名取三个部件 (<c>PART_Track</c> / <c>PART_Thumb</c> / <c>PART_TargetMarker</c>) 并挂 SizeChanged。
    /// 换模板(如 Orientation 触发器切换水平/垂直)会再次触发,故先解绑旧部件再抓新部件,避免重复订阅。
    /// 重写模板时缺哪个部件都不会崩,只是对应元素不再被定位。
    /// </summary>
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
        // 目标"可用"= 目标本身有限 **且** 边界有限(边界坏了就算不出几何位置)。
        // 只判目标是不够的:边界坏掉时这一帧会被整帧跳过、三角永远得不到定位,若它仍可见,
        // 未设过的 Canvas.Left 是 NaN、被 WPF 当 0 渲染 → Min 端冒出一个幽灵三角配「ETA 0.0s」,
        // 等于谎报"正指令到最小端且即将到达"。所以显隐要跟着"这一帧到底摆不摆得出来"走。
        // 关键:三角的位置只依赖 **边界 + 目标**,与 Position 无关。所以 Position 坏掉时
        // 不能连累它 —— 早先版本把三角的显隐和"整帧"绑在一起,于是 Position=NaN 时三角
        // 被判为可见、却因整帧被丢弃而从未定位,又出幽灵三角。下面把两条链路彻底分开。
        bool boundsUsable = double.IsFinite(Minimum) && double.IsFinite(Maximum);
        bool positionUsable = boundsUsable && double.IsFinite(Position);
        HasTarget = boundsUsable && TargetPosition is double tgt && double.IsFinite(tgt);

        // 坏采样防护:Position / Minimum / Maximum 任一不是有限数(NaN 或 ±∞)时,这一帧整个跳过 ——
        // 滑块几何与 Percentage 都保持上一个好值(注意默认模板的位置/Min/Max 文字读数直接绑原始属性,
        // 会照实显示 NaN,那是模板的事)。监控控件宁可"暂时不动",也不该被一次坏读数带跑,更不该崩。
        // ⚠️ 为什么要在这里判、而不只靠 Fraction:Math.Clamp(NaN,0,1) 返回的仍是 NaN,NaN != 0 也为真,
        //    所以"夹紧"和"range != 0"都不构成对 NaN 的防护。NaN 一旦流到 DoubleAnimation.To 就抛
        //    ArgumentException,在 UI 线程上直接打死宿主进程。Fraction 里的 Clamp01 现在也兜了一道底,
        //    但那是第二道防线;这里先拦住,才能实现"坏值整帧跳过、读数保持上一个好值"的语义
        //    —— 光靠兜底只会把坏值悄悄映射成 0,那是另一种说谎。
        // 注意"两个边界各自有限"与"range 有限"是两回事:Min/Max 可以各自合法有限、相减却溢出成 ±∞
        //    (如 double.MinValue 与 double.MaxValue)。那种输入这里是放行的,由 Fraction 折半去算,
        //    不会冻画面 —— 见 Fraction 里的说明。
        if (positionUsable)
            Percentage = Fraction(Position) * 100.0;   // 百分比纯数值,先算先更新(不依赖轨道尺寸)

        if (!boundsUsable || _track is null) return;

        double w = _track.ActualWidth, h = _track.ActualHeight;
        // 只要求"当前方向真正用到的那根轴"有尺寸:水平只用宽,垂直只用高。
        // 过去两轴耦合,于是重写模板时把 PART_Track 放进 Auto 尺寸的行/列(Canvas 无固有尺寸,
        // 绝对定位的子元素不参与测量 → 该轴 Actual* 恒为 0)就会永远不给滑块定位,且无任何提示。
        if ((Orientation == Orientation.Vertical ? h : w) <= 0) return;   // 还没完成布局,等 SizeChanged 再触发一次

        // 三角:HasTarget 为真就一定摆得出来(它已经包含了"边界有限 + 目标有限"),与 Position 无关。
        if (HasTarget && TargetPosition is double target)
            PlaceElement(_targetMarker, target, w, h);

        // 滑块:只有 Position 也可用时才动;否则保持上一个好位置。
        if (positionUsable)
            PlaceElement(_thumb, Position, w, h);
    }

    /// <summary>
    /// 值 → 0~1 比例(整个控件的"心脏"):防除零、越界贴边。
    /// Min/Max 不限正负与大小:`(value-Min)/(Max-Min)` 对反向行程(Max&lt;Min)天然成立
    /// —— value=Min → 0、value=Max → 1。只在 range==0(零长度行程)时退化为 0。
    /// </summary>
    private double Fraction(double value)
    {
        double range = Maximum - Minimum;

        // Min/Max 各自有限、相减却溢出成 ±∞(如 double.MinValue 与 double.MaxValue):
        // 两边各折半再算 —— (v−min)/(max−min) ≡ (v/2−min/2)/(max/2−min/2),数学上等价,
        // 但中间结果不再溢出。直接算的话 f 会恒为 0(有限值 ÷ ∞),滑块永久钉在 Min 端不动 ——
        // 又是一次"静默冻结":读数在跳、滑块不动。
        if (!double.IsFinite(range))
        {
            double halfRange = Maximum / 2.0 - Minimum / 2.0;
            return Clamp01(halfRange != 0 ? (value / 2.0 - Minimum / 2.0) / halfRange : 0.0);
        }

        return Clamp01(range != 0 ? (value - Minimum) / range : 0.0);
    }

    /// <summary>
    /// 把比例夹进 [0,1]。两件兜底:
    /// ① NaN 不能往外漏(会一路污染 Percentage,并让 DoubleAnimation.To 抛异常)—— clamp 本身拦不住 NaN;
    /// ② 消掉 IEEE 负零:反向行程(Max&lt;Min)且 value 恰等于 Min 时,分子是 +0.0、分母为负,商为 -0.0,
    ///    数值上等于 0 但 .NET 会格式化成带负号,读数会显示成 "-0%"。加 0.0 即可归正(-0.0 + 0.0 == +0.0)。
    /// </summary>
    private static double Clamp01(double f)
        => double.IsNaN(f) ? 0.0 : Math.Clamp(f, 0.0, 1.0) + 0.0;

    /// <summary>
    /// 把元素按比例摆到轨道上,按 Orientation 选轴:
    ///   水平 → 动 Canvas.Left,坐标 = 比例 × 轨道宽 − 元素宽/2(Min 左、Max 右);
    ///   垂直 → 动 Canvas.Top, 坐标 = (1−比例) × 轨道高 − 元素高/2(Min 下、Max 上,故反转)。
    ///   减半个身位是为了让元素**居中**对准该点,而不是用左上角对准。
    /// 交叉轴的居中由模板里的静态 Canvas.Top/Left 负责,这里不碰。
    /// </summary>
    private void PlaceElement(FrameworkElement? element, double value, double trackWidth, double trackHeight)
    {
        if (element is null) return;

        // 元素刚从 Collapsed 变可见、本轮还没测量(Actual* = 0)→ 先别摆:
        // 否则下面减 Actual*/2 时减的是 0,等于按"左上角"而非中心定位,会偏出半个身位。
        // 它测量完后,OnApplyTemplate 挂的 SizeChanged 会再调一次 UpdateVisual,那时尺寸就对了。
        // 与轨道尺寸同理,只判"当前方向真正用到的那根轴":两轴耦合的话,一个高度为 0、
        // 宽度正常的滑块在水平模式下会永远不被定位(重写模板时很容易踩到)。
        bool vertical = Orientation == Orientation.Vertical;
        double extent = vertical ? element.ActualHeight : element.ActualWidth;
        if (extent <= 0) return;

        double f = Fraction(value);

        DependencyProperty axis, crossAxis;
        double to;
        if (vertical)
        {
            axis = Canvas.TopProperty;
            crossAxis = Canvas.LeftProperty;
            to = (1.0 - f) * trackHeight - extent / 2.0;
        }
        else
        {
            axis = Canvas.LeftProperty;
            crossAxis = Canvas.TopProperty;
            to = f * trackWidth - extent / 2.0;
        }

        // 松开另一根轴:切换 Orientation 后,上一次写在 Canvas.Left/Top 上的本地值(或仍挂着的
        // HoldEnd 动画时钟)不会自己消失,而这两者在依赖属性优先级里都盖过模板设的静态值,
        // 会把交叉轴的居中顶掉(用一份不随方向切换的自定义模板时,滑块会偏出老远)。
        // ClearValue 只清本地值,模板里的静态值优先级更低、随即重新生效 —— 这正是上面注释所说的
        // "交叉轴交给模板负责";不主动松手就等于食言。
        element.BeginAnimation(crossAxis, null);
        element.ClearValue(crossAxis);

        // 由 AnimationDuration 统一控制:凡不是"正的具体时长"一律走瞬时,否则平滑。两条路都支持随时切换。
        // _placeInstantly 是切换 Orientation 那一帧的强制瞬时开关(见 OnOrientationChanged)。
        if (_placeInstantly)
        {
            MoveInstant(element, axis, to);
            return;
        }
        // 判 !HasTimeSpan 是必须的:Duration.Forever 的动画永远走不完,滑块会被永久钉死在起点,
        // 而数字读数照常刷新 —— 与 AnimationDuration=0 那个坑同一种失效形态,且纯 XAML 写 "Forever" 就能触发。
        // Duration.Automatic 同样没有具体时长,一并按瞬时处理,语义可预期。
        if (!AnimationDuration.HasTimeSpan || AnimationDuration.TimeSpan <= TimeSpan.Zero)
            MoveInstant(element, axis, to);
        else
            MoveAnimated(element, axis, to);
    }

    /// <summary>
    /// ① 瞬时定位:立即把元素的指定轴(Canvas.Left 或 Canvas.Top)设为 to。
    /// ⚠️ 必须先 <c>BeginAnimation(axis, null)</c> 解除可能仍挂在该轴上的动画时钟 (AnimationClock):
    ///    MoveAnimated 用的是默认 <c>FillBehavior.HoldEnd</c>,动画跑完后时钟**永久附着**在属性上;
    ///    而 SetValue 写的是本地值 (local value),在依赖属性值优先级里**低于动画** —— 不解除就完全不生效。
    ///    症状是"数字读数照常刷新、滑块却永远冻在原地",对位置监控控件是最坏的失效方式。
    ///    只有"控件从未布局过就设 AnimationDuration=0"才碰不到这个坑,所以不能靠调用方规避。
    /// </summary>
    private static void MoveInstant(FrameworkElement element, DependencyProperty axis, double to)
    {
        element.BeginAnimation(axis, null);
        element.SetValue(axis, to);
    }

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

    /// <summary>共享变更回调:Position / Minimum / Maximum / Orientation 任一变化都重新映射。</summary>
    private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((LinearTrackMonitor)d).UpdateVisual();

    /// <summary>
    /// TargetPosition 专用回调:先同步只读 HasTarget(驱动三角/ETA 显隐),再重新映射。
    /// </summary>
    private static void OnTargetPositionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // HasTarget 现在由 UpdateVisual 统一算(它还要看 Min/Max 是否有限),这里只需触发重算。
        ((LinearTrackMonitor)d).UpdateVisual();
    }

    /// <summary>
    /// 有没有**可用**目标(只读):<see cref="TargetPosition"/> 非 null **且为有限值**时才为 true。
    /// 模板据此显隐三角标记和 ETA 面板。喂 <c>NaN</c> / <c>±∞</c> 视同无目标,不会画出无处安放的三角。
    /// </summary>
    public bool HasTarget
    {
        get => (bool)GetValue(HasTargetProperty);
        private set => SetValue(HasTargetPropertyKey, value);
    }
    private static readonly DependencyPropertyKey HasTargetPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(HasTarget), typeof(bool), typeof(LinearTrackMonitor),
            new PropertyMetadata(false));
    /// <summary>标识只读的 <see cref="HasTarget"/> 依赖属性。</summary>
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
    /// <summary>标识只读的 <see cref="Percentage"/> 依赖属性。</summary>
    public static readonly DependencyProperty PercentageProperty = PercentagePropertyKey.DependencyProperty;

    // ======================== 数据:依赖属性 ========================
    // ("影响几何"的属性都在元数据里挂了变更回调:Minimum / Maximum / Position / Orientation 用共享的
    //  OnVisualPropertyChanged;TargetPosition 用专属的 OnTargetPositionChanged,因为它还要同步 HasTarget。)

    /// <summary>
    /// 行程起点,默认 0。与 <see cref="Maximum"/> 一起定义行程区间,**不限正负与大小**:
    /// <c>Maximum &lt; Minimum</c> 即反向行程(映射自动反转,Minimum 端仍在左 / 下),
    /// 仅 <c>Maximum == Minimum</c> 退化为比例恒 0。
    /// 两个边界各自有限、相减却溢出(如 <c>double.MinValue</c> 与 <c>double.MaxValue</c>)仍能正确映射。
    /// 但边界本身为非有限值(<c>NaN</c> / <c>±∞</c>)时,几何与 <see cref="Percentage"/> 会整帧跳过、保持上一个好值。
    /// </summary>
    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }
    /// <summary>标识 <see cref="Minimum"/> 依赖属性。</summary>
    public static readonly DependencyProperty MinimumProperty =
        DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(LinearTrackMonitor),
            new PropertyMetadata(0.0, OnVisualPropertyChanged));

    /// <summary>
    /// 行程终点,默认 100。取值不限正负、也可小于 <see cref="Minimum"/>(即反向行程,映射自动反转,
    /// Minimum 端仍在左 / 下);仅 <c>Maximum == Minimum</c> 退化为比例恒 0。
    /// 两个边界各自有限、相减却溢出(如 <c>double.MinValue</c> 与 <c>double.MaxValue</c>)仍能正确映射。
    /// 非有限值(<c>NaN</c> / <c>±∞</c>)会让几何与 <see cref="Percentage"/> 整帧跳过、保持上一个好值。
    /// </summary>
    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }
    /// <summary>标识 <see cref="Maximum"/> 依赖属性。</summary>
    public static readonly DependencyProperty MaximumProperty =
        DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(LinearTrackMonitor),
            new PropertyMetadata(100.0, OnVisualPropertyChanged));

    /// <summary>
    /// 当前绝对位置(由传感器按节奏回传)。这是调用方唯一必须喂的实时数据 ——
    /// <see cref="Speed"/> / <see cref="Eta"/> / <see cref="Status"/> 都由内部引擎从它算出。
    /// </summary>
    /// <remarks>
    /// 越界值**不会被夹紧**:只有滑块的几何位置贴到端点、<see cref="Percentage"/> 停在 0 或 100;
    /// <see cref="Speed"/>、<see cref="Eta"/> 以及模板里的位置读数用的都是原始值。
    /// 非有限值(NaN / ±∞)会被整帧跳过:滑块几何与 <see cref="Percentage"/> 保持上一个好值,不跳位、不抛异常;
    /// 引擎也跳过该拍且不推进内部时钟,所以 NaN 不会污染滑动平均窗口、恢复后速度不出假尖峰。
    /// 但默认模板的位置文字读数是直接绑本属性的,会照实显示 NaN。
    /// </remarks>
    public double Position
    {
        get => (double)GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }
    /// <summary>标识 <see cref="Position"/> 依赖属性。</summary>
    public static readonly DependencyProperty PositionProperty =
        DependencyProperty.Register(nameof(Position), typeof(double), typeof(LinearTrackMonitor),
            new PropertyMetadata(0.0, OnVisualPropertyChanged));

    /// <summary>
    /// 目标位置(可选 / optional)。null = 没有目标:不画目标标记、不显示 ETA。
    /// 有值时运动前下发,画面上以三角标记 (marker) 显示。
    /// 非有限值(<c>NaN</c> / <c>±∞</c>)等同于无目标 —— <see cref="HasTarget"/> 为 false、三角与 ETA 一并隐藏。
    /// </summary>
    public double? TargetPosition
    {
        get => (double?)GetValue(TargetPositionProperty);
        set => SetValue(TargetPositionProperty, value);
    }
    /// <summary>标识 <see cref="TargetPosition"/> 依赖属性(类型为可空 <c>double?</c>)。</summary>
    public static readonly DependencyProperty TargetPositionProperty =
        DependencyProperty.Register(nameof(TargetPosition), typeof(double?), typeof(LinearTrackMonitor),
            new PropertyMetadata(null, OnTargetPositionChanged));

    /// <summary>
    /// 当前速度,单位 = 行程单位 / 秒。
    /// **符号 = <see cref="Position"/> 数值增减的方向**:正 = 数值变大,负 = 数值变小。
    /// ⚠️ 正向行程(<c>Maximum &gt; Minimum</c>)下这等同于"正 = 朝 Maximum";但**反向行程**
    /// (<c>Maximum &lt; Minimum</c>)下几何方向是反的 —— 此时 Position 减小反而是朝 Maximum 端走。
    /// 若要据此画方向箭头,请自行结合 <c>Maximum - Minimum</c> 的符号判断。
    /// 只读:由内部引擎差分算出、再经 <see cref="SpeedSmoothingWindow"/> 滑动平均平滑,外部只能绑定来看。
    /// 注意 <see cref="Status"/> 判的是**未平滑**的瞬时速度,所以两者短暂不一致是正常的。
    /// </summary>
    public double Speed
    {
        get => (double)GetValue(SpeedProperty);
        private set => SetValue(SpeedPropertyKey, value);
    }
    private static readonly DependencyPropertyKey SpeedPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(Speed), typeof(double), typeof(LinearTrackMonitor),
            new PropertyMetadata(0.0));
    /// <summary>标识只读的 <see cref="Speed"/> 依赖属性。</summary>
    public static readonly DependencyProperty SpeedProperty = SpeedPropertyKey.DependencyProperty;

    /// <summary>
    /// 预计剩余到达时间 ETA(秒)。只读:由内部引擎用**平滑后**的 <see cref="Speed"/> 算出。
    /// 无目标、目标为非有限值、或 |速度| 已近 0(≤1e-3,避免除以极小数得到天文数字)时为 0。
    /// 注意置 0 发生在**下一拍引擎**,不是赋值当时。
    /// ⚠️ 刚起步的头几拍读数会明显偏大:静止期采到的一串 0 还留在滑动平均窗口里拉低 <see cref="Speed"/>,
    /// 且首拍往往只截到一段周期。要等约 <see cref="SpeedSmoothingWindow"/> 拍才收敛,详见 README「注意」。
    /// 注意 ETA 只按距离与速率估算、**不判方向**:背离目标行驶时仍会给出一个有限的倒计时。
    /// </summary>
    public double Eta
    {
        get => (double)GetValue(EtaProperty);
        private set => SetValue(EtaPropertyKey, value);
    }
    private static readonly DependencyPropertyKey EtaPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(Eta), typeof(double), typeof(LinearTrackMonitor),
            new PropertyMetadata(0.0));
    /// <summary>标识只读的 <see cref="Eta"/> 依赖属性。</summary>
    public static readonly DependencyProperty EtaProperty = EtaPropertyKey.DependencyProperty;

    /// <summary>
    /// 滑动平均窗口 (moving-average window):用最近 N 拍的原始速度求平均来平滑 <see cref="Speed"/> 读数。
    /// 默认 5;<c>1</c> 即不平滑。取值自动夹到 <c>1..1000</c>(封上界是为了长期运行时队列不会只涨不落),不会抛异常。
    /// 运行时调小窗口,多余的旧样本在**下一个采样周期**才被挤出(该属性没有变更回调)。
    /// </summary>
    public int SpeedSmoothingWindow
    {
        get => (int)GetValue(SpeedSmoothingWindowProperty);
        set => SetValue(SpeedSmoothingWindowProperty, value);
    }
    /// <summary>标识 <see cref="SpeedSmoothingWindow"/> 依赖属性。</summary>
    public static readonly DependencyProperty SpeedSmoothingWindowProperty =
        DependencyProperty.Register(nameof(SpeedSmoothingWindow), typeof(int), typeof(LinearTrackMonitor),
            // 与 SamplePeriod 一样在提交前夹紧,而不是只在用的时候夹:
            // 否则属性里存着 5000、实际窗口却是 1000,绑定到该属性的界面会显示一个并未生效的数字。
            new PropertyMetadata(5, null, CoerceSmoothingWindow));

    /// <summary>把滑动平均窗口夹进 <c>1..1000</c>。封上界是因为窗口就是队列长度,超大窗口会让样本只进不出。</summary>
    private static object CoerceSmoothingWindow(DependencyObject d, object baseValue)
        => Math.Clamp((int)baseValue, 1, MaxSmoothingWindow);

    /// <summary>
    /// 内部引擎的采样周期,默认 100ms。应与数据回传节奏大致对齐:
    /// 过快则速度抖,过慢则滞后(可用 <see cref="SpeedSmoothingWindow"/> 平滑)。
    /// 取值会被自动夹进可用区间:非正 → 回落 100ms;超过 <c>int.MaxValue</c> 毫秒(≈24.9 天)→ 夹到该上界
    /// (这是 <c>DispatcherTimer.Interval</c> 的硬限制,超了会抛异常)。故本属性怎么赋都不会抛。
    /// </summary>
    public TimeSpan SamplePeriod
    {
        get => (TimeSpan)GetValue(SamplePeriodProperty);
        set => SetValue(SamplePeriodProperty, value);
    }
    /// <summary>标识 <see cref="SamplePeriod"/> 依赖属性。</summary>
    public static readonly DependencyProperty SamplePeriodProperty =
        DependencyProperty.Register(nameof(SamplePeriod), typeof(TimeSpan), typeof(LinearTrackMonitor),
            new PropertyMetadata(TimeSpan.FromMilliseconds(100), OnSamplePeriodChanged, CoerceSamplePeriod));

    /// <summary>
    /// 把采样周期夹进 <c>DispatcherTimer.Interval</c> 能接受的区间:非正 → 回落默认 100ms,
    /// 超过 <c>int.MaxValue</c> 毫秒(≈24.9 天)→ 夹到上界。
    /// 用**强制转换回调 (CoerceValueCallback)** 而不是在赋值点判,是因为依赖属性是"先提交值、再跑变更回调"的:
    /// 若只在变更回调里判并抛,坏值已经存进属性了 —— 调用方即使 try/catch 吞掉异常,
    /// 之后控件重新加载(如切回标签页)时 StartEngine 会拿这个坏值再炸一次,而且炸在另一个位置。
    /// 强制转换发生在提交之前,所以属性里**永远不可能存下不可用的值**。
    /// </summary>
    private static object CoerceSamplePeriod(DependencyObject d, object baseValue)
    {
        var period = (TimeSpan)baseValue;
        if (period <= TimeSpan.Zero) return DefaultSamplePeriod;
        return period > MaxSamplePeriod ? MaxSamplePeriod : period;
    }

    private static void OnSamplePeriodChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        // 变更回调拿到的已是强制转换后的值,直接用即可(DispatcherTimer.Interval 可随时改)。
        => ((LinearTrackMonitor)d)._engine.Interval = (TimeSpan)e.NewValue;

    /// <summary>
    /// 运行状态:空闲 / 运行 / 报警。只读,由控件内部推断:<see cref="IsFaulted"/> 为真则强制 Fault,
    /// 否则拿**未平滑的瞬时速度**与 <see cref="MovingThreshold"/> 比 —— 注意不是公开的 <see cref="Speed"/>,
    /// 后者是 SMA 平滑值,故两者可能短暂不一致。
    /// </summary>
    public TrackStatus Status
    {
        get => (TrackStatus)GetValue(StatusProperty);
        private set => SetValue(StatusPropertyKey, value);
    }
    private static readonly DependencyPropertyKey StatusPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(Status), typeof(TrackStatus), typeof(LinearTrackMonitor),
            new PropertyMetadata(TrackStatus.Idle));
    /// <summary>标识只读的 <see cref="Status"/> 依赖属性。</summary>
    public static readonly DependencyProperty StatusProperty = StatusPropertyKey.DependencyProperty;

    /// <summary>是否报警(输入):机器才知道的外部条件,为 true 时强制 Status=Fault。默认 false。</summary>
    public bool IsFaulted
    {
        get => (bool)GetValue(IsFaultedProperty);
        set => SetValue(IsFaultedProperty, value);
    }
    /// <summary>标识 <see cref="IsFaulted"/> 依赖属性。</summary>
    public static readonly DependencyProperty IsFaultedProperty =
        DependencyProperty.Register(nameof(IsFaulted), typeof(bool), typeof(LinearTrackMonitor),
            new PropertyMetadata(false, OnStatusInputChanged));

    /// <summary>
    /// 判定"在动"的速度阈值,默认 0.1:|速度| 超过它才算 Running,否则 Idle。
    /// ⚠️ 比的是**未平滑的瞬时速度**,不是公开的 <see cref="Speed"/>(那是 SMA 平滑值)——
    /// 这样启停判定不被均值拖慢,但也意味着 <see cref="Status"/> 与 <see cref="Speed"/> 读数可能短暂不一致。
    /// </summary>
    public double MovingThreshold
    {
        get => (double)GetValue(MovingThresholdProperty);
        set => SetValue(MovingThresholdProperty, value);
    }
    /// <summary>标识 <see cref="MovingThreshold"/> 依赖属性。</summary>
    public static readonly DependencyProperty MovingThresholdProperty =
        DependencyProperty.Register(nameof(MovingThreshold), typeof(double), typeof(LinearTrackMonitor),
            new PropertyMetadata(0.1, OnStatusInputChanged));

    /// <summary>IsFaulted / MovingThreshold 变化时立即重判状态(不必等下一拍引擎)。</summary>
    private static void OnStatusInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((LinearTrackMonitor)d).UpdateStatus();

    /// <summary>
    /// 滑轨方向:水平 / 垂直(复用 WPF 内置枚举 <see cref="System.Windows.Controls.Orientation"/>),默认水平。
    /// 水平时 <see cref="Minimum"/> 端在左、<see cref="Maximum"/> 端在右;垂直时 Minimum 在下、Maximum 在上。
    /// 默认主题为两个方向各备了一份 ControlTemplate,靠 Style 触发器整套切换;
    /// 若你用一份自己的、不随方向切换的模板,控件也会在方向变化时重新定位滑块。
    /// </summary>
    public Orientation Orientation
    {
        get => (Orientation)GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }
    /// <summary>标识 <see cref="Orientation"/> 依赖属性。</summary>
    public static readonly DependencyProperty OrientationProperty =
        DependencyProperty.Register(nameof(Orientation), typeof(Orientation), typeof(LinearTrackMonitor),
            new PropertyMetadata(Orientation.Horizontal, OnOrientationChanged));

    /// <summary>
    /// 方向变化:重新映射,但**这一帧强制瞬时定位、绝不启动动画**。
    /// </summary>
    /// <remarks>
    /// 为什么必须瞬时:默认主题靠 Style 触发器给两个方向各备一份 ControlTemplate。方向一变,本回调**先**跑,
    /// 此刻旧模板的滑块还在;若给它 <c>BeginAnimation</c> 挂上时钟,紧接着模板被换掉、旧滑块脱离模板,
    /// 它那根轴的基准值就退回 <c>NaN</c>(Default 级),而**已经在跑的动画时钟**下一帧去取基准值,
    /// 会在**渲染线程**抛 <c>AnimationException: cannot use default origin value of 'NaN'</c> —— 那里没有任何
    /// try/catch 能接,进程直接死。瞬时定位写的是本地值、不留时钟,旧滑块即使被弃用也无害;
    /// 新模板套上后 <c>OnApplyTemplate</c> 会再定位一次,画面依然正确。
    /// 挂这个回调本身是必要的:使用者若用一份**不随方向切换**的自定义模板,没有回调就会切了方向而滑块不动。
    /// </remarks>
    private static void OnOrientationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (LinearTrackMonitor)d;
        control._placeInstantly = true;
        try { control.UpdateVisual(); }
        finally { control._placeInstantly = false; }
    }
}
