using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TrackMonitor.Demo.ViewModels;

/// <summary>
/// MVVM 基石:实现 INotifyPropertyChanged。属性变化时自动通知 View 刷新。
/// (标准 MVVM 基础设施实现。)
/// </summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>设置字段,值变了才发通知。用法:set => Set(ref _x, value);</summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
