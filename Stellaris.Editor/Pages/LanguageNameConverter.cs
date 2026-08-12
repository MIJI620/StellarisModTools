using System;
using System.Globalization;
using System.Windows.Data;

namespace Stellaris.Editor.Pages;

/// <summary>语言字典结果表"语种"列转换器：语言 key → 当前 UI 语种下的译名（跟随界面语言）。</summary>
public sealed class LanguageNameConverter : IValueConverter
{
    private UILocalisationManager? _loc;

    /// <summary>带参构造（页面构造时注入——立即生效）。</summary>
    public LanguageNameConverter(UILocalisationManager loc)
    {
        _loc = loc;
    }

    /// <summary>无参构造（XAML 资源声明需要——Convert 时延迟从 App 静态获取）。</summary>
    public LanguageNameConverter()
    {
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        _loc ??= App.CurrentLocalisation;
        return value is string lang && _loc != null ? _loc.GetLanguageDisplayNameLocalized(lang) : (value ?? "");
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
