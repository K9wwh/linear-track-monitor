# LinearTrackMonitor

A WPF lookless custom control that read-only displays the live state of a single moving part on a linear track. Feed it `Position` and the built-in engine derives speed, ETA and `Idle`/`Running`/`Fault` status internally — one-way data flow, no user interaction. `net10.0-windows` · `UseWPF` · no third-party dependencies.

WPF 无外观自定义控件 (lookless control):只读展示线性滑轨上单个可动部件的实时状态。调用方只喂 `Position`,自带的计算引擎在内部算出速度、ETA 与 `Idle`/`Running`/`Fault` 状态 —— 单向数据流,无用户交互。`net10.0-windows` · `UseWPF` · 无第三方依赖。

## Install / 安装

```
dotnet add package TrackMonitor.Controls --version 1.1.0
```

NuGet: https://www.nuget.org/packages/TrackMonitor.Controls

## Demo

```
dotnet build
dotnet run --project TrackMonitor.Demo
```

📖 **Docs:** [English](README.en.md) · [中文](README.cn.md)

License: MIT — see [LICENSE](LICENSE).
