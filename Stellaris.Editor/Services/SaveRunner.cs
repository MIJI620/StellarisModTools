// 文件: Stellaris.Editor/Services/SaveRunner.cs
// 通用保存运行器：所有保存（样式 / 地图等）统一使用本模块——
//   转圈进度窗口（SaveProgressWindow）+ 后台线程执行（不阻塞 UI 动画）+
//   完成自动关闭 + 仅失败才弹警告框。禁止各处自行 MessageBox 完成提示。

using System;
using System.Threading.Tasks;
using System.Windows;

namespace Stellaris.Editor;

/// <summary>
/// 统一保存执行器（规范格式）：
///   - 显示转圈进度窗口（文案取本地化键 statusKey）；
///   - work 在后台线程执行（返回是否成功）；
///   - 完成后在 UI 线程关闭进度窗；成功可选 onSuccess 回调（UI 线程，如写配置），失败弹警告框；
///   - 异常统一捕获并弹警告。
/// </summary>
public static class SaveRunner
{
    /// <summary>执行一次规范格式的保存。</summary>
    /// <param name="services">引擎服务（取本地化文案）。</param>
    /// <param name="statusKey">进度窗口文案的本地化键（如 "status.saving"）。</param>
    /// <param name="work">后台线程执行的真实保存逻辑，返回是否成功。</param>
    /// <param name="onSuccess">保存成功后、UI 线程执行的收尾（可为 null；如写本地配置）。</param>
    /// <param name="failMessage">失败时的警告文案（可为 null，用默认）。</param>
    public static void Run(EngineServices services, string statusKey, Func<bool> work,
        Action? onSuccess = null, string? failMessage = null)
    {
        var progress = new SaveProgressWindow(services.Localisation.Get(statusKey));
        progress.Show();

        _ = Task.Run(() =>
        {
            try
            {
                bool ok = work();
                Application.Current.Dispatcher.Invoke(() =>
                {
                    progress.Close();
                    if (ok)
                    {
                        onSuccess?.Invoke();
                    }
                    else
                    {
                        MessageBox.Show(failMessage ?? services.Localisation.Get("status.save_failed"),
                            "Stellaris Mod Tools", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                });
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    progress.Close();
                    MessageBox.Show($"保存失败: {ex.Message}", "Stellaris Mod Tools",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                });
            }
        });
    }
}
