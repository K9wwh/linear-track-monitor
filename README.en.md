# LinearTrackMonitor

**English** · [中文](README.cn.md)

A WPF lookless custom control (无外观自定义控件, a lookless `Control`): a read-only display of the live state of a single moving part on a linear track. **It ships its own calculation engine — the caller only feeds `Position`, and the control derives speed / ETA / status internally.** One-way data flow, no user interaction.

- **v1.1** · `net10.0-windows` · `UseWPF` · no third-party dependencies.
- `[assembly: ThemeInfo]` is already embedded and `Themes/Generic.xaml` loads automatically, so consumers do not have to merge a resource dictionary by hand.
- `TrackMonitor.Controls.xml` ships with the package; **put it in the same folder as the DLL** to get IntelliSense.

## Install

```
dotnet add package TrackMonitor.Controls --version 1.1.0
```

NuGet: https://www.nuget.org/packages/TrackMonitor.Controls

## Usage

```xml
xmlns:tm="clr-namespace:TrackMonitor.Controls;assembly=TrackMonitor.Controls"

<!-- Minimal: feed the position only, everything else is derived -->
<tm:LinearTrackMonitor Minimum="0" Maximum="150" Position="{Binding Pos}"/>

<!-- Full: optional target / fault flag / engine tuning -->
<tm:LinearTrackMonitor
    Minimum="0" Maximum="150"
    Position="{Binding Pos}"
    TargetPosition="{Binding Target}"      
    IsFaulted="{Binding Faulted}"
    SpeedSmoothingWindow="5" SamplePeriod="0:0:0.1" AnimationDuration="0:0:0.1"/>
```

## How it works

Internally a `DispatcherTimer` samples `Position` on every `SamplePeriod`, a `Stopwatch` measures the real Δt, and differencing (差分) yields the instantaneous speed → smoothed by a simple moving average (滑动平均, SMA) and written to `Speed`; when a target exists, `Eta` is computed too; `Running`/`Idle` is decided against `MovingThreshold` (`IsFaulted` takes priority → `Fault`). The engine only runs between `Loaded` and `Unloaded`, so no timer is leaked.

> **Status uses the raw speed, the readout uses the smoothed speed**: `Status` is decided from the unsmoothed instantaneous speed (sensitive to start/stop, no jitter on reversal), while `Speed` displays the SMA-smoothed value (steady). So you may briefly see `Status=Running` while the `Speed` readout still lags near 0, or `Status=Idle` while the `Speed` readout is still > `MovingThreshold`.

## Properties

### Input (written by the caller)

| Property | Type | Default | Description |
|---|---|---|---|
| `Minimum` / `Maximum` | `double` | 0 / 100 | Travel range; **no restriction on sign or magnitude**: `Max<Min` simply means a reversed range (the Min end is still on the left / bottom); only `Max==Min` degenerates to a constant ratio of 0 |
| `Position` | `double` | 0 | Current position; drives the thumb and feeds the engine. Out-of-range values are **not clamped**: only the thumb pins to the end point and `Percentage` stops at 0/100, while `Speed`/`Eta`/the position readout use the raw value. A non-finite value (NaN/±∞) makes the whole frame be skipped: the thumb and `Percentage` keep the last good value (the text readouts still show it as-is) |
| `TargetPosition` | `double?` | `null` | Optional target; `null` = no target (hides the triangle and the ETA). A non-finite value is equivalent to no target |
| `IsFaulted` | `bool` | false | External fault flag; true → forces `Status = Fault` |
| `Orientation` | `Orientation` | `Horizontal` | Horizontal / vertical; when vertical, Min is at the bottom and Max at the top |
| `SpeedSmoothingWindow` | `int` | 5 | SMA window N; `1` = no smoothing. Automatically clamped to `1..1000`. If you shrink it at runtime, the surplus old samples are pushed out on the **next sample period** (this property has no change callback) |
| `SamplePeriod` | `TimeSpan` | 100ms | Engine sample period (采样周期), which should line up with the cadence at which data arrives. Automatically coerced into the usable range: non-positive → falls back to 100ms; longer than `int.MaxValue` milliseconds (≈24.9 days) → clamped to that upper bound. **It never throws, whatever you assign** |
| `MovingThreshold` | `double` | 0.1 | If the absolute raw speed exceeds it → `Running`, otherwise `Idle` |
| `AnimationDuration` | `Duration` | 100ms | Thumb tween duration. Anything that is not a **positive concrete duration** (`0` / `Forever` / `Automatic`) is treated as instant. Can be switched at any time at runtime (see the format trap under "Caveats") |

### Output (read-only on the control, bindable for observation)

| Property | Type | Description |
|---|---|---|
| `Speed` | `double` | Smoothed speed. **The sign = the direction in which the `Position` value is changing** (+ = increasing). On a forward range that is the same as "+ = toward `Max`"; **on a reversed range (`Max<Min`) the geometric direction is the opposite**, so combine it with the sign of `Max-Min` when drawing an arrow |
| `Eta` | `double` | Estimated time of arrival (seconds), computed from the **smoothed** `Speed`. It is 0 when there is no target, the target is non-finite, or `\|Speed\| ≤ 1e-3`. **It does not judge direction**: driving away from the target still yields a finite countdown. In an extreme combination (a huge distance plus a speed barely above the threshold) the division can still overflow; `Eta` then falls back to `double.MaxValue` — a deliberately huge but finite value, which the default template renders as a ~309-digit number of seconds |
| `Status` | `TrackStatus` | `Idle` / `Running` / `Fault`, drives the colour (grey / green / red) |
| `Percentage` | `double` | `Position` normalized to 0–100 |
| `HasTarget` | `bool` | Whether a **usable** target exists = `TargetPosition` is non-null **and finite** (and `Minimum`/`Maximum` are finite as well); drives the visibility of the triangle / ETA |

## Overriding the template

Keep the named parts — the control looks them up by name and positions them: `PART_Track` (a `Canvas`), `PART_Thumb`, `PART_TargetMarker`. Main-axis positioning: `Canvas.Left` when horizontal, `Canvas.Top` when vertical (cross-axis centring is the job of the static `Canvas.Top`/`Left` in the template). The visibility of the target triangle and the ETA is bound to `HasTarget` (through `BooleanToVisibilityConverter`).

## Caveats

- **The `AnimationDuration` string format** = `[d.]hh:mm:ss[.fff]`: `0:0:0.1` = 100ms, `0` = instant; **a bare number is parsed as DAYS** (`100` = 100 days), and `0.1` fails to parse and throws `XamlParseException`. In code, assign with `TimeSpan.FromMilliseconds(...)`. ⚠️ Getting this wrong is not reported as an error — the consequence is that **the thumb looks frozen**: a 100-day tween really is moving, just a few micrometres per second, while the numeric readouts refresh as usual. If you see "the readouts are ticking but the thumb does not move" on site, check here first.
- **Read-only output properties** bound in a "write" direction → **compile-time** `MC3080` (not at runtime).
- **`Eta` reads high for the first few ticks after motion starts**, from two causes that multiply:
  1. **The window still holds real zeros from the idle period.** The engine samples on schedule from the moment the control loads, so while the axis is stationary every tick stores a `0`. When motion begins those zeros are still in the window, dragging the average down; they take about `SpeedSmoothingWindow` ticks to be pushed out completely.
     > ⚠️ This is **not** "the window has not filled up yet". `Smooth()` divides by the **number of samples actually collected** (`sum / _speedSamples.Count`), so a half-full window introduces **no bias at all** — the bias comes entirely from those zeros being **genuinely sampled values**.
  2. **The first tick usually captures only part of a period.** Motion typically starts partway through a sample period, so that tick's displacement is less than a full period's worth while Δt is complete — the raw speed comes out low.

  Measured with the default window of 5, a true speed of 20 units/second and a distance of 80: the first tick catches about 44% of the period, so `Speed` reports **1.76** instead of 20 (an under-report of ≈ 5× × 2.3× ≈ **11×**), the first `Eta` reads **45.4 seconds**, and it converges to ~4 seconds by the 3rd–4th tick (`45.4 → 13.3 → 7.7 → 5.3 → 4.0`), strictly decreasing thereafter. The first reading **depends on the phase at which motion begins** and is not a fixed value: another run of the same scenario gave 36.2 seconds, and landing exactly on a period boundary gives 19.5 seconds.

  Avoiding it: lower `SpeedSmoothingWindow` (`1` removes the effect entirely), or delay showing `Eta` for about `SpeedSmoothingWindow` ticks after `Status` turns `Running`.
- **Bad samples will not crash anything, but the three kinds of bad value behave differently.** When fed `NaN` / `±∞`:
  - `Position` → **the whole frame is skipped** (the thumb geometry and `Percentage` keep the last good value), **and the engine also skips that tick and does not advance its internal clock** — so the `NaN` never enters the moving-average window, `Speed` keeps its last good value, and after recovery the speed computed from the real Δt is still accurate, with no false spike.
  - `Minimum` / `Maximum` → **the frame is skipped from the geometry step onward** (geometry and `Percentage` keep the last good value); note that `HasTarget` is still evaluated before that early return and turns `false`, so the target triangle and the whole ETA panel do hide in that same frame. **The engine is unaffected**: `Speed` / `Eta` / `Status` keep refreshing on schedule — the engine only looks at `Position`.
  - Conversely, `Min`/`Max` each being finite while their difference overflows (e.g. `double.MinValue` and `double.MaxValue`) does **not** count as a bad value: the ratio is computed by halving both sides, the mapping is still correct, and nothing freezes.
  - `TargetPosition` → **equivalent to no target**: `HasTarget` turns `false`, and the triangle and the ETA panel are hidden together (`Eta` is set to 0 by the engine on the next tick — **but only while `Position` is finite**; if `Position` is non-finite at the same time the engine skips the tick, so `Eta` keeps its previous value until a good `Position` arrives).
  - ⚠️ In the default template the **position / `Min` / `Max` text readouts are bound straight to the raw dependency properties**, so they will display `NaN` as-is. If you want the text to hide bad values too, add your own converter when you override the template.
  - ⚠️ **When a sensor keeps sending bad values for a long time (e.g. always `NaN` after the link drops), `Speed` and `Status` stay at their last live values** (unless you change `IsFaulted` / `MovingThreshold`, which re-evaluates the status immediately) — the panel may keep showing "Running". The control does not judge whether the sensor is alive; **the host must implement its own communication-timeout monitoring.**
- **Frozen during the window move/resize modal loop**: like all UI-thread-driven WPF content, dragging or resizing the window pauses `DispatcherTimer` and `DoubleAnimation` → readouts and thumb freeze. Because progress is measured by the real Δt, the position catches up automatically once the loop exits (only the intermediate frames were never drawn). If you need refreshes while dragging, hook `WM_ENTERSIZEMOVE` + `SetTimer` to pump messages on the **host window** side (that is the host's responsibility, not the control's).

## Changelog

### v1.1.1

**Documentation revision only — code behaviour is identical to v1.1** (only the version number and the XML comments differ; the IL is unchanged).

- **Corrected the cause** of "`Eta` reads high for the first few ticks after motion starts". The v1.1 README attributed it to "the moving-average window has not filled up yet", which is wrong: `Smooth()` divides by the **number of samples actually collected** (`sum / _speedSamples.Count`), so a half-full window carries zero bias. The real cause is **a run of genuinely sampled `0`s from the idle period still sitting in the window**, compounded by **the first tick capturing only part of a period** — the two multiply to the ≈11× under-report. That entry is now rewritten to match the code, with the added note that the first reading **depends on the phase at which motion begins** (45.4 s and 36.2 s were both measured in the same scenario; landing on a period boundary gives 19.5 s).
- `Eta`'s XML comment now carries the same start-up warning, so it is visible to consumers in IntelliSense.

### v1.1

- **Fixed**: `Position` / `TargetPosition` / `Minimum` / `Maximum` receiving `NaN` threw `ArgumentException` (`DoubleAnimation.To` refuses it), killing the host process outright on the UI thread. Now: when `Position` is non-finite the whole frame is skipped, when `Minimum`/`Maximum` are non-finite the frame is skipped from the geometry step onward (`HasTarget` is still evaluated first), and when `TargetPosition` is non-finite the triangle is simply not positioned; on top of that, **a bad `Position` value also makes the engine skip that tick without advancing its internal clock** (so the `NaN` never enters the moving-average window and there is no false speed spike after recovery), while bad `Min`/`Max` values do not affect the engine. For how each readout behaves and what risk remains, see "Caveats".
- **Fixed**: `AnimationDuration` set to `0` **after the control's first layout pass** froze the thumb permanently (the animation clock stays attached with `FillBehavior.HoldEnd` and shadows the local value written by `SetValue`), which looked like "the readouts are refreshing but the thumb does not move". Now the animation clock is detached before instant positioning, so it can be switched at any time at runtime.
- **Added**: the XML documentation file ships with the package, so consumers get IntelliSense just by referencing the DLL; the assembly now carries the real version number (previously always `1.0.0.0`).
- **Fixed**: a `SamplePeriod` longer than `int.MaxValue` milliseconds (≈24.9 days) threw `ArgumentOutOfRangeException` and killed the process. What is nastier is that a dependency property **commits the value first and runs the change callback afterwards**, so even after the caller swallowed the exception with `try/catch` the bad value was already stored in the property, and the next time the control loaded (e.g. switching back to the tab) it would blow up again inside `StartEngine`. It now uses a **coerce value callback (CoerceValueCallback)** to clamp before the commit, so an unusable value can never be stored in the property.
- **Fixed**: `AnimationDuration` set to `Duration.Forever` (writing `"Forever"` in pure XAML is enough) froze the thumb permanently while the numeric readouts kept refreshing. Now anything that is not a positive concrete duration goes down the instant path.
- **Fixed**: feeding `TargetPosition` a non-finite value drew a **ghost triangle** pinned to the `Minimum` end and showed "ETA 0.0s". `HasTarget` now requires "non-null and finite", so it is treated as no target.
- **Fixed**: assorted robustness —
  - a non-finite speed produced by an overflowing position difference no longer enters the moving-average window (otherwise a single `±∞` poisons the whole window's mean, and you have to wait for it to be pushed out before the readout recovers);
  - `SpeedSmoothingWindow` is now clamped into `1..1000` by a coerce value callback, which avoids a queue that only grows and never shrinks during long runs and keeps the property value equal to the effective value;
  - the size checks for **both the track and the elements** now look only at the axis actually used in the current orientation — putting `PART_Track` into an Auto-sized row when overriding the template, or a thumb whose size is 0 on one axis, no longer stops the thumb from ever being positioned;
  - when `Min`/`Max` are each finite but their difference overflows (e.g. `±double.MaxValue`), the ratio is computed by **halving** both sides, so the thumb is no longer pinned to one end;
  - `Orientation` now has a change callback — with a custom template that does not switch with the orientation, the thumb is also repositioned immediately after the orientation changes;
  - a reversed range no longer produces an IEEE negative zero at the `Minimum` end, so the readout will not show `-0%`;
  - `HasTarget` now requires **both** a finite target **and** finite bounds — otherwise, when the bounds go bad this frame is skipped, the triangle never gets positioned yet stays visible, and the ghost triangle appears again;
  - when `Orientation` changes, the other axis is actively released (`ClearValue` + detaching the animation clock) so the cross-axis centring set by the template takes effect again, and with a single custom template the thumb no longer drifts out;
  - `Eta`'s distance is computed by halving before subtracting, so a range spanning the full `double` no longer overflows to `∞`.
- **Docs**: corrected descriptions that did not match the implementation (`Position` out of range is not clamped; `SpeedSmoothingWindow` only trims on the next tick; `Eta` is 0 at near-zero speed too and does not judge direction; the different behaviour of bad samples across the three kinds of property).

## Limitations

- A single moving part.
- Speed is derived by differencing the position stream: too large a mismatch between `SamplePeriod` and the data cadence distorts it (too fast → jitter, too slow → lag); smooth it with `SpeedSmoothingWindow`.

## Build & Run (构建与运行)

```
dotnet build
dotnet run --project TrackMonitor.Demo
```

Or open `TrackMonitor.slnx` in Visual Studio and run with `TrackMonitor.Demo` as the startup project.

## License

MIT — see [LICENSE](LICENSE).
