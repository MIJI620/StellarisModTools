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

    /// <summary>设置浮层文本（线程安全：自动切回 UI 线程）。主文本限制只显示最后 3 行（内容再多也看不到更早的）。</summary>
    public void SetStatus(string mainText, string? subText = null)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetStatus(mainText, subText));
            return;
        }
        MainText.Text = CompactLines(mainText);
        MainText.Foreground = Brushes.Black;
        SubText.Text = CompactLines(subText);
        SubText.Visibility = string.IsNullOrEmpty(subText) ? Visibility.Collapsed : Visibility.Visible;
        ExitButton.Visibility = Visibility.Collapsed;
    }

    /// <summary>限制最多 3 行：≤3 行全显示；超过 3 行 → 显示**前 2 行 + 省略标记 + 最后 1 行**（中间省略）。
    /// 例：4 行 → 第 1、2、…、4 行；5 行 → 第 1、2、…、5 行。</summary>
    /// <summary>加载浮窗文本截断（用户算法）：≤108 字符（36×3）全显示（行内空白归一）；
    /// >108 字符 → 第 1 行显示前 36 字符，第 3 行显示后 36 字符，中间用 "..."（固定 3 显示行）。</summary>
    private static string CompactLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;
        var flat = text.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");
        if (flat.Length <= 108)
            return flat;
        return flat.Substring(0, 36) + "\n...\n" + flat.Substring(flat.Length - 36);
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
