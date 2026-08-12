using System;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Stellaris.Editor.Controls;

/// <summary>搜索框防抖：输入停止 2 秒后自动触发搜索（TextChanged 重启计时器）。
/// 返回计时器——调用方在 Enter 手动搜索时可 Stop() 避免 2 秒后重复触发。</summary>
public static class SearchDebouncer
{
    public static DispatcherTimer Attach(TextBox box, Action onSearch)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) => { timer.Stop(); onSearch(); };
        box.TextChanged += (_, _) => { timer.Stop(); timer.Start(); };
        return timer;
    }
}
