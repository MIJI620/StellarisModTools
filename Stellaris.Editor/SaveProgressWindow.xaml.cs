// 文件: Stellaris.Editor/SaveProgressWindow.xaml.cs
// 保存进度弹窗：无边框、半透明、可拖动、带转圈加载动画。
// 保存（SaveAllStyles）在后台线程执行，此窗口在 UI 线程播放动画并提示"正在保存中"。

using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace Stellaris.Editor;

public partial class SaveProgressWindow : Window
{
    public SaveProgressWindow(string message)
    {
        InitializeComponent();
        ProgressText.Text = message;

        // 无限旋转动画（转圈加载）
        var anim = new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = TimeSpan.FromSeconds(0.8),
            RepeatBehavior = RepeatBehavior.Forever
        };
        SpinnerRotate.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, anim);
    }

    /// <summary>无边框窗口拖动（按住任意位置拖动）。</summary>
    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }
}
