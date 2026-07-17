# LinearTrackMonitor

WPF 无外观自定义控件 (lookless `Control`):只读展示线性滑轨上单个可动部件的实时状态。**自带计算引擎——调用方只喂 `Position`,控件内部算出速度 / ETA / 状态。** 单向数据流,无用户交互。

- `net10.0-windows` · `UseWPF` · 无第三方依赖。
- 已嵌 `[assembly: ThemeInfo]`,`Themes/Generic.xaml` 自动加载,引用方无需手动合并资源字典。

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
| `Position` | `double` | 0 | 当前位置,驱动滑块并喂引擎;越界按 `[Min,Max]` 夹紧 |
| `TargetPosition` | `double?` | `null` | 可选目标;`null` = 无目标(隐藏三角与 ETA) |
| `IsFaulted` | `bool` | false | 外部故障标志;真 → 强制 `Status = Fault` |
| `Orientation` | `Orientation` | `Horizontal` | 水平 / 垂直;垂直时 Min 在下、Max 在上 |
| `SpeedSmoothingWindow` | `int` | 5 | SMA 窗口 N;`1` = 不平滑。运行时调小会**立即**丢弃多余旧样本 |
| `SamplePeriod` | `TimeSpan` | 100ms | 引擎采样周期,应与数据回传节奏对齐 |
| `MovingThreshold` | `double` | 0.1 | 原始速度绝对值超过它判 `Running`,否则 `Idle` |
| `AnimationDuration` | `Duration` | 100ms | 滑块补间时长;`0` = 瞬时(见“注意”) |

### 输出(控件只读,可绑定观察)

| 属性 | 类型 | 说明 |
|---|---|---|
| `Speed` | `double` | 平滑速度;符号即方向(+ 朝 `Max`) |
| `Eta` | `double` | 预计到达时间(秒);无目标时为 0 |
| `Status` | `TrackStatus` | `Idle` / `Running` / `Fault`,驱动配色(灰 / 绿 / 红) |
| `Percentage` | `double` | `Position` 归一化到 0–100 |
| `HasTarget` | `bool` | `TargetPosition` 是否非 null;驱动三角 / ETA 显隐 |

## 重写模板

保留命名部件,控件按名取用并定位:`PART_Track`（`Canvas`）、`PART_Thumb`、`PART_TargetMarker`。主轴定位:水平用 `Canvas.Left`、垂直用 `Canvas.Top`(交叉轴居中由模板里的静态 `Canvas.Top`/`Left` 负责)。目标三角与 ETA 的显隐绑 `HasTarget`(经 `BooleanToVisibilityConverter`)。

## 注意

- **`AnimationDuration` 字符串格式** = `[d.]hh:mm:ss[.fff]`:`0:0:0.1` = 100ms、`0` = 瞬时;**裸数字按“天”**(`100` = 100 天),`0.1` 解析失败抛 `XamlParseException`。代码内赋值用 `TimeSpan.FromMilliseconds(...)`。
- **只读输出属性** 若被“写入式”绑定 → **编译期** `MC3080`(非运行时)。
- **窗口 move/resize 模态循环期间会冻结**:与一切 UI 线程驱动的 WPF 内容一样,拖拽 / 缩放窗口时 `DispatcherTimer` 与 `DoubleAnimation` 暂停 → 读数与滑块冻结。因推进按真实 Δt,退出循环后位置自动追平(仅中途未绘制)。如需拖拽中仍刷新,在**宿主窗口**侧 hook `WM_ENTERSIZEMOVE` + `SetTimer` 泵消息(属宿主职责,不在控件内)。

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
