# LinearTrackMonitor

[English](README.en.md) · **中文**

WPF 无外观自定义控件 (lookless `Control`):只读展示线性滑轨上单个可动部件的实时状态。**自带计算引擎——调用方只喂 `Position`,控件内部算出速度 / ETA / 状态。** 单向数据流,无用户交互。

- **v1.1** · `net10.0-windows` · `UseWPF` · 无第三方依赖。
- 已嵌 `[assembly: ThemeInfo]`,`Themes/Generic.xaml` 自动加载,引用方无需手动合并资源字典。
- 随包附 `TrackMonitor.Controls.xml`,**与 DLL 放同一目录**即可获得 IntelliSense 提示。

## 安装 (Install)

```
dotnet add package TrackMonitor.Controls --version 1.1.0
```

NuGet: https://www.nuget.org/packages/TrackMonitor.Controls

## 用法

```xml
xmlns:tm="clr-namespace:TrackMonitor.Controls;assembly=TrackMonitor.Controls"

<!-- 最简:只喂位置,其余自算 -->
<tm:LinearTrackMonitor Minimum="0" Maximum="150" Position="{Binding Pos}"/>

<!-- 完整:可选目标 / 故障 / 引擎调参 -->
<tm:LinearTrackMonitor
    Minimum="0" Maximum="150"
    Position="{Binding Pos}"
    TargetPosition="{Binding Target}"      
    IsFaulted="{Binding Faulted}"
    SpeedSmoothingWindow="5" SamplePeriod="0:0:0.1" AnimationDuration="0:0:0.1"/>
```

## 工作原理

内部一个 `DispatcherTimer` 按 `SamplePeriod` 采样 `Position`,以 `Stopwatch` 量真实 Δt 做差分 (differencing) 得瞬时速度 → 滑动平均 (SMA) 平滑写 `Speed`;有目标时算 `Eta`;按 `MovingThreshold` 判 `Running`/`Idle`(`IsFaulted` 优先 → `Fault`)。引擎仅在 `Loaded`~`Unloaded` 间运行,不漏定时器。

> **状态用原始速度、显示用平滑速度**:`Status` 以未平滑的瞬时速度判定(对启停灵敏、反向不抖),`Speed` 显示 SMA 平滑值(稳)。因此可能出现 `Status=Running` 而 `Speed` 读数尚滞后于近 0 的瞬间,或 `Status=Idle` 而 `Speed` 读数 > `MovingThreshold` 的瞬间。

## 属性

### 输入(调用方写)

| 属性 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `Minimum` / `Maximum` | `double` | 0 / 100 | 行程区间;**不限正负大小**:`Max<Min` 即反向行程(Min 端仍在左/下),仅 `Max==Min` 退化为比例 0 |
| `Position` | `double` | 0 | 当前位置,驱动滑块并喂引擎。越界值**不夹紧**:只有滑块贴到端点、`Percentage` 停在 0/100,而 `Speed`/`Eta`/位置读数用的是原始值。非有限值(NaN/±∞)整帧跳过:滑块与 `Percentage` 保持上一个好值(文字读数仍照实显示) |
| `TargetPosition` | `double?` | `null` | 可选目标;`null` = 无目标(隐藏三角与 ETA)。非有限值等同无目标 |
| `IsFaulted` | `bool` | false | 外部故障标志;真 → 强制 `Status = Fault` |
| `Orientation` | `Orientation` | `Horizontal` | 水平 / 垂直;垂直时 Min 在下、Max 在上 |
| `SpeedSmoothingWindow` | `int` | 5 | SMA 窗口 N;`1` = 不平滑。取值自动夹到 `1..1000`。运行时调小,多余旧样本在**下一个采样周期**挤出(该属性无变更回调) |
| `SamplePeriod` | `TimeSpan` | 100ms | 引擎采样周期 (sample period),应与数据回传节奏对齐。自动夹进可用区间:非正 → 回落 100ms,超过 `int.MaxValue` 毫秒(≈24.9 天)→ 夹到上界,**怎么赋都不抛** |
| `MovingThreshold` | `double` | 0.1 | 原始速度绝对值超过它判 `Running`,否则 `Idle` |
| `AnimationDuration` | `Duration` | 100ms | 滑块补间时长。凡不是**正的具体时长**(`0` / `Forever` / `Automatic`)一律按瞬时处理。运行时随时可切(见“注意”的格式陷阱) |

### 输出(控件只读,可绑定观察)

| 属性 | 类型 | 说明 |
|---|---|---|
| `Speed` | `double` | 平滑速度。**符号 = `Position` 数值增减的方向**(+ = 变大)。正向行程下即"+ 朝 `Max`";**反向行程(`Max<Min`)下几何方向相反**,画箭头需结合 `Max-Min` 的符号 |
| `Eta` | `double` | 预计到达时间(秒),用**平滑后**的 `Speed` 算。无目标、目标非有限、或 `\|Speed\| ≤ 1e-3` 时为 0。**不判方向**:背离目标行驶时仍给出有限倒计时。极端组合(距离极大 + 速度刚过阈值)下相除仍可能溢出,此时 `Eta` 回落到 `double.MaxValue` —— 一个刻意取的"极大但有限"值,默认模板会把它显示成约 309 位数字的秒数 |
| `Status` | `TrackStatus` | `Idle` / `Running` / `Fault`,驱动配色(灰 / 绿 / 红) |
| `Percentage` | `double` | `Position` 归一化到 0–100 |
| `HasTarget` | `bool` | 是否有**可用**目标 = `TargetPosition` 非 null **且有限**(且 `Minimum`/`Maximum` 也有限);驱动三角 / ETA 显隐 |

## 重写模板

保留命名部件,控件按名取用并定位:`PART_Track`（`Canvas`）、`PART_Thumb`、`PART_TargetMarker`。主轴定位:水平用 `Canvas.Left`、垂直用 `Canvas.Top`(交叉轴居中由模板里的静态 `Canvas.Top`/`Left` 负责)。目标三角与 ETA 的显隐绑 `HasTarget`(经 `BooleanToVisibilityConverter`)。

## 注意

- **`AnimationDuration` 字符串格式** = `[d.]hh:mm:ss[.fff]`:`0:0:0.1` = 100ms、`0` = 瞬时;**裸数字按“天”**(`100` = 100 天),`0.1` 解析失败抛 `XamlParseException`。代码内赋值用 `TimeSpan.FromMilliseconds(...)`。⚠️ 写错成天的后果不是报错,而是**滑块看上去冻住** —— 100 天的补间确实在动,只是每秒几微米,而数字读数照常刷新。现场看到"读数在跳、滑块不动",先查这里。
- **只读输出属性** 若被“写入式”绑定 → **编译期** `MC3080`(非运行时)。
- **刚起步的头几拍 `Eta` 偏大**,两个原因叠乘:
  1. **窗口里残留着静止期的 0**。引擎自控件加载起就一直按节拍采样 (sampling),轴不动时每拍存进一个 `0`。运动一开,这些 0 还在窗口里拉低均值,要等约 `SpeedSmoothingWindow` 拍才被完全挤出。
     > ⚠️ 这**不是**"窗口还没填满"。`Smooth()` 除的是**实际样本数**(`sum / _speedSamples.Count`),所以半满窗口本身**不引入任何偏差** —— 偏差全来自那些 0 是**真实采到的样本**。
  2. **首拍通常只截到一段行程**。运动多半从采样周期 (sample period) 中途开始,那一拍的位移不足一个整周期,而 Δt 是完整的,故原始速度偏小。

  实测默认窗口 5、真实 20 单位/秒、距离 80:首拍约截到 44% 的周期,`Speed` 报 **1.76** 而非 20(欠报 ≈ 5× × 2.3× ≈ **11×**),`Eta` 首读 **45.4 秒**,第 3~4 拍收敛到 ~4 秒(`45.4 → 13.3 → 7.7 → 5.3 → 4.0`),之后严格单调递减。首读**取决于运动开始的相位 (phase)**,不是定值:同一场景另一次跑出 36.2 秒;若恰好对齐周期边界则是 19.5 秒。

  规避:把 `SpeedSmoothingWindow` 调小(`1` 即无此现象),或在 `Status` 转 `Running` 后延迟约 `SpeedSmoothingWindow` 拍再显示 `Eta`。
- **坏采样不会崩,但三种坏值的表现各不相同**,喂 `NaN` / `±∞` 时:
  - `Position` → **整帧跳过**(滑块几何与 `Percentage` 保持上一个好值),**且引擎也跳过这一拍、不推进内部时钟** —— 所以 `NaN` 不进滑动平均窗口、`Speed` 保持上一个好值,恢复后按真实 Δt 算出的速度依然准、不出假尖峰。
  - `Minimum` / `Maximum` → **这一帧从几何计算起被跳过**(几何与 `Percentage` 保持上一个好值);注意 `HasTarget` 在那次提前返回之前已经算过并转为 `false`,所以同一帧里目标三角与整个 ETA 面板确实会隐藏。**引擎不受影响**,`Speed` / `Eta` / `Status` 照常按节拍刷新 —— 引擎只判 `Position`。
  - 反过来,`Min`/`Max` 各自有限、相减却溢出(如 `double.MinValue` 与 `double.MaxValue`)**不算坏值**:比例改用折半运算,映射依然正确,不会冻住。
  - `TargetPosition` → **等同无目标**:`HasTarget` 转 `false`,三角与 ETA 面板一起隐藏(`Eta` 会在下一拍引擎被置 0 —— **但这只在 `Position` 有限时成立**;若 `Position` 同时也是非有限值,引擎会跳过该拍,`Eta` 保持原值,直到来了好的 `Position`)。
  - ⚠️ 默认模板里的**位置 / `Min` / `Max` 文字读数是直接绑原始依赖属性 (dependency property) 的**,会照实显示 `NaN`。要让文字也不显示坏值,请在重写模板时自行加转换器。
  - ⚠️ **传感器长期送坏值(如断线后一直是 `NaN`)时,`Speed` 与 `Status` 会停在最后一次的活值**(除非你改动 `IsFaulted` / `MovingThreshold`,那会立即重判状态) —— 面板可能持续显示「Running」。控件不判断传感器存活,**上位机需自行做通信超时监视**。
- **窗口 move/resize 模态循环期间会冻结**:与一切 UI 线程驱动的 WPF 内容一样,拖拽 / 缩放窗口时 `DispatcherTimer` 与 `DoubleAnimation` 暂停 → 读数与滑块冻结。因推进按真实 Δt,退出循环后位置自动追平(仅中途未绘制)。如需拖拽中仍刷新,在**宿主窗口**侧 hook `WM_ENTERSIZEMOVE` + `SetTimer` 泵消息(属宿主职责,不在控件内)。

## 更新记录

### v1.1.1

**纯文档修订,代码行为与 v1.1 完全一致**(仅版本号与 XML 注释有别,IL 未变)。

- **更正**「刚起步的头几拍 `Eta` 偏大」的**成因**。v1.1 的 README 把它归因成"滑动平均窗口还没填满",这是错的:`Smooth()` 除的是**实际样本数**(`sum / _speedSamples.Count`),半满窗口本身零偏差。真正的成因是**静止期真实采到的一串 `0` 仍压在窗口里**,再叠上**首拍只截到一段周期**,两者相乘才有约 11 倍欠报。现已按代码重写该条,并补上"首读取决于运动开始的相位"(实测同场景出现过 45.4s / 36.2s,对齐周期边界则为 19.5s)。
- `Eta` 的 XML 注释同步补上这条起步告警,使用方在 IntelliSense 里可见。

### v1.1

- **修**:`Position` / `TargetPosition` / `Minimum` / `Maximum` 收到 `NaN` 会抛 `ArgumentException`(`DoubleAnimation.To` 拒收),在 UI 线程上直接终止宿主进程。现改为:`Position` 非有限时整帧跳过、`Minimum`/`Maximum` 非有限时从几何步骤起跳过(`HasTarget` 仍会先算出来)、`TargetPosition` 非有限时跳过三角定位;其中 **`Position` 坏值还会让引擎跳过该拍且不推进内部时钟**(故 `NaN` 不进滑动平均窗口、恢复后速度不出假尖峰),`Min`/`Max` 坏值不影响引擎。各读数的具体表现与残留风险见「注意」。
- **修**:`AnimationDuration` 在控件**首次布局之后**被设为 `0`,滑块会永久停住(动画时钟 `FillBehavior.HoldEnd` 长期附着,遮蔽 `SetValue` 写的本地值),表现为"读数在刷新、滑块却不动"。现改为瞬时定位前先解除动画时钟,运行时随时可切。
- **加**:随包发布 XML 文档文件,使用方引用 DLL 即有 IntelliSense;程序集写入真实版本号(此前恒为 `1.0.0.0`)。
- **修**:`SamplePeriod` 超过 `int.MaxValue` 毫秒(≈24.9 天)会抛 `ArgumentOutOfRangeException` 打死进程。更阴的是依赖属性**先提交值、再跑变更回调**,所以调用方 `try/catch` 吞掉异常后坏值已存进属性,控件下次加载(如切回标签页)会在 `StartEngine` 里再炸一次。现改用**强制转换回调 (CoerceValueCallback)** 在提交前夹紧,属性里再不可能存下不可用的值。
- **修**:`AnimationDuration` 取 `Duration.Forever`(纯 XAML 写 `"Forever"` 即可)会让滑块永久冻住而数字读数照常刷新。现改为凡不是正的具体时长一律走瞬时。
- **修**:`TargetPosition` 喂非有限值时会画出一个钉在 `Minimum` 端的**幽灵三角**并显示「ETA 0.0s」。现 `HasTarget` 要求"非 null 且有限",视同无目标。
- **修**:健壮性若干 ——
  - 位置差溢出产生的非有限速度不再进滑动平均窗口(否则一个 `±∞` 会污染整个窗口的均值,要等它被挤出去才恢复);
  - `SpeedSmoothingWindow` 改由强制转换回调夹进 `1..1000`,避免长期运行队列只涨不落,且属性值与生效值一致;
  - **轨道与元素**的尺寸判断都改为只看当前方向用到的那根轴 —— 重写模板时把 `PART_Track` 放进 Auto 尺寸行、或滑块某一轴尺寸为 0,不再导致滑块永不定位;
  - `Min`/`Max` 各自有限而相减溢出时(如 `±double.MaxValue`)改用**折半运算**求比例,不再把滑块钉死在一端;
  - `Orientation` 挂上变更回调 —— 用一份不随方向切换的自定义模板时,切方向后滑块也会立刻重新定位;
  - 反向行程在 `Minimum` 端不再算出 IEEE 负零,读数不会显示成 `-0%`;
  - `HasTarget` 改为**同时**要求目标有限**和边界有限** —— 否则边界坏掉时这一帧被跳过、三角得不到定位却仍可见,又会冒出幽灵三角;
  - 切换 `Orientation` 时主动松开另一根轴(`ClearValue` + 解除动画时钟),让模板设的交叉轴居中值重新生效,自定义单模板下滑块不再偏出;
  - `Eta` 的距离改用折半相减,行程跨满 `double` 时不再溢出成 `∞`。
- **文档**:校正与实现不符的描述(`Position` 越界不夹紧、`SpeedSmoothingWindow` 下一拍才裁、`Eta` 近零速也为 0 且不判方向、坏采样对三类属性的不同表现)。

## 限制

- 单一可动部件。
- 速度由位置流差分得出:`SamplePeriod` 与数据节奏相差过大会失真(过快 → 抖,过慢 → 滞后),用 `SpeedSmoothingWindow` 平滑。

## 构建与运行 (Build & Run)

```
dotnet build
dotnet run --project TrackMonitor.Demo
```

或在 Visual Studio 中打开 `TrackMonitor.slnx`,以 `TrackMonitor.Demo` 为启动项目运行。

## License

MIT — 见 [LICENSE](LICENSE)。
