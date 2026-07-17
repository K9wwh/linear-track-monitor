using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace TrackMonitor.Controls.Converters;

/// <summary>
/// TrackStatus → 颜色画刷 (Brush):空闲灰 / 运行绿 / 报警红。
///
/// IValueConverter 是 WPF 绑定管线里的“翻译官”:把源的值(这里是枚举)
/// 转成目标需要的类型(这里是 Brush)。绑定时用 Converter={StaticResource ...} 挂上。
/// </summary>
public class StatusToBrushConverter : IValueConverter
{
    // 画刷做成静态只读 + Freeze:避免每次转换都 new,且冻结后跨线程安全、更省。
    private static readonly Brush Idle    = Freeze("#888888");
    private static readonly Brush Running = Freeze("#4CAF50");
    private static readonly Brush Fault   = Freeze("#E05252");

    /// <summary>正向:值 → 显示。</summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value switch
        {
            TrackStatus.Running => Running,
            TrackStatus.Fault   => Fault,
            _                   => Idle,   // Idle 及任何意外值
        };

    /// <summary>反向:显示 → 值。本控件只读单向显示,用不到,直接禁掉。</summary>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static Brush Freeze(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
