// 文件: Stellaris.Editor/StatusOverlay.xaml.cs
// 初始化状态浮层（规范 4.5）：固定 400×300、半透明白、无边框、无系统控制、
// 无拖动、固定居中；文本靠底部居中。SetStatus 线程安全（经 Dispatcher 切换）。

using System;
using System.Windows;
using System.Windows.Media;

namespace Stellaris.Editor;

public partial class StatusOverlay : Window
{
    public StatusOverlay()
    {
        InitializeComponent();
    }

    /// <summary>设置浮层文本（线程安全：自动切回 UI 线程）。</summary>
    public void SetStatus(string mainText, string? subText = null)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetStatus(mainText, subText));
            return;
        }
        MainText.Text = mainText ?? string.Empty;
        MainText.Foreground = Brushes.Black;
        SubText.Text = subText ?? string.Empty;
        SubText.Visibility = string.IsNullOrEmpty(subText) ? Visibility.Collapsed : Visibility.Visible;
        ExitButton.Visibility = Visibility.Collapsed;
    }

    /// <summary>设置主状态行（线程安全）。</summary>
    public void SetMain(string mainText) => SetStatus(mainText, null);

    /// <summary>初始化失败：红色错误文本 + 显示退出按钮（用户确认后才退出）。</summary>
    public void ShowError(string message)
    {
        if (!IsVisible)
            return; // 窗口已关闭则不再操作（防御性）
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => ShowError(message));
            return;
        }
        MainText.Text = message ?? string.Empty;
        MainText.Foreground = Brushes.DarkRed;
        SubText.Text = string.Empty;
        SubText.Visibility = Visibility.Collapsed;
        ExitButton.Visibility = Visibility.Visible;
    }

    private void OnExit(object sender, RoutedEventArgs e)
        => Application.Current.Shutdown();
}
