namespace Stellaris.Parser;

/// <summary>
/// 全局解析配置
/// </summary>
public static class Config
{
    private static int _maxIterationDepth = 127;

    /// <summary>
    /// 内联脚本展开和本地化常量替换的最大迭代深度。
    /// 默认 127，用户可调范围 7 ~ 65535。
    /// </summary>
    public static int MaxIterationDepth
    {
        get => _maxIterationDepth;
        set
        {
            if (value < 7)
                value = 7;
            if (value > 65535)
                value = 65535;
            _maxIterationDepth = value;
        }
    }
}