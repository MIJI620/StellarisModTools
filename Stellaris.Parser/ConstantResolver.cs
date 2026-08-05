using System;
using System.Collections.Generic;

namespace Stellaris.Parser;

/// <summary>
/// 常量解析器，支持局部和全局常量，且可独立克隆作用域。
/// </summary>
public class ConstantResolver
{
    private readonly Dictionary<string, object> _localConstants = new();
    private readonly Dictionary<string, object> _globalConstants = new();

    /// <summary>
    /// 默认构造函数（全局常量空）
    /// </summary>
    public ConstantResolver() { }

    /// <summary>
    /// 从已有全局常量复制（用于创建独立文件作用域）
    /// </summary>
    public ConstantResolver(ConstantResolver globalSource)
    {
        if (globalSource != null)
        {
            foreach (var kv in globalSource._globalConstants)
                _globalConstants[kv.Key] = kv.Value;
        }
    }

    public void SetLocal(string name, object value) => _localConstants[name] = value;
    public void SetGlobal(string name, object value) => _globalConstants[name] = value;
    public object? Resolve(string name)
    {
        if (_localConstants.TryGetValue(name, out object? localVal))
            return localVal;
        if (_globalConstants.TryGetValue(name, out object? globalVal))
            return globalVal;
        return null;
    }

    public IReadOnlyDictionary<string, object> GetAllGlobals() => _globalConstants;
    public IReadOnlyDictionary<string, object> GetAllLocals() => _localConstants;

    public void ClearLocal() => _localConstants.Clear();
    public void ClearGlobal() => _globalConstants.Clear();
}