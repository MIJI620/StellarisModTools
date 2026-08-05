// 文件: Stellaris.Editor/Pages/PlaceholderPage.xaml.cs
// 通用占位页：显示"建设中：{0}"（本地化键 page.placeholder）。

using System.Windows.Controls;

namespace Stellaris.Editor.Pages;

/// <summary>占位页（初稿阶段未实现的导航项使用）。</summary>
public partial class PlaceholderPage : UserControl
{
    public PlaceholderPage(EngineServices services, string titleKey)
    {
        InitializeComponent();
        Message.Text = services.Localisation.Format("page.placeholder", services.Localisation.Get(titleKey));
    }
}
