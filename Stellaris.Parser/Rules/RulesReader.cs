using System.Text.Json.Nodes;

namespace Stellaris.Parser.Rules;

/// <summary>
/// 规则专用读取器：**所有规则统一从这里读取**（排除规则、覆盖规则等），各模块不自己读文件。
/// 规则文件位于打包分发的 rules/ 目录（可外部编辑）：
/// - modifier_exclusions.json：加成字典排除规则（exclude_keys / exclude_keywords / exclude_exact / exclude_values）
/// - overwrite_rules.json：群星各 common 文件夹的覆盖规则（"后读覆盖"/"只读一次"/"自动整合"等，
///   未配置的文件夹 = 自动整合）
/// 懒加载 + 线程安全（后台预热与 UI 查询并发安全）。
/// </summary>
public sealed class RulesReader
{
    private readonly string _rulesDir;
    private readonly object _lock = new();

    private IReadOnlyDictionary<string, List<int>>? _excludeKeys;
    private IReadOnlyCollection<string>? _excludeKeywords;
    private IReadOnlyCollection<string>? _excludeExact;
    private IReadOnlyCollection<string>? _excludeValues;
    private IReadOnlyDictionary<string, string>? _overwriteRules;

    public RulesReader(string? rulesDir = null)
    {
        _rulesDir = rulesDir ?? System.IO.Path.Combine(AppContext.BaseDirectory, "rules");
    }

    /// <summary>排除父键（key → 深度列表：[1] 查父、[1,2] 查父+祖父、[0] 查自身）。</summary>
    public IReadOnlyDictionary<string, List<int>> ExcludeKeys
    {
        get { EnsureLoaded(); return _excludeKeys!; }
    }

    /// <summary>排除关键词（引用键包含——如 "$"）。</summary>
    public IReadOnlyCollection<string> ExcludeKeywords
    {
        get { EnsureLoaded(); return _excludeKeywords!; }
    }

    /// <summary>完全匹配排除（modifier 块内引用键精确等于）。</summary>
    public IReadOnlyCollection<string> ExcludeExact
    {
        get { EnsureLoaded(); return _excludeExact!; }
    }

    /// <summary>value 拒绝（引用键的值精确等于——如 yes/no）。</summary>
    public IReadOnlyCollection<string> ExcludeValues
    {
        get { EnsureLoaded(); return _excludeValues!; }
    }

    /// <summary>取某 common 文件夹的覆盖规则（"后读覆盖"/"只读一次"/"自动整合"…）；未配置返回 null（= 自动整合）。</summary>
    public string? GetOverwriteRule(string folder)
    {
        EnsureLoaded();
        return _overwriteRules!.TryGetValue(folder, out var rule) ? rule : null;
    }

    private void EnsureLoaded()
    {
        if (_excludeKeys != null)
            return;
        lock (_lock)
        {
            if (_excludeKeys != null)
                return;
            _excludeKeys = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            _excludeKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _excludeExact = new HashSet<string>(StringComparer.Ordinal);
            _excludeValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _overwriteRules = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            LoadModifierExclusions();
            LoadOverwriteRules();
        }
    }

    private void LoadModifierExclusions()
    {
        var json = ReadJson("modifier_exclusions.json");
        if (json == null)
            return;
        // exclude_keys：对象 key → 深度数组
        if (json.TryGetPropertyValue("exclude_keys", out var obj) && obj is JsonObject jo)
        {
            foreach (var prop in jo)
            {
                var depths = new List<int>();
                if (prop.Value is JsonArray ja)
                {
                    foreach (var d in ja)
                    {
                        if (d is JsonValue jv && jv.TryGetValue<int>(out var n))
                            depths.Add(n);
                    }
                }
                if (depths.Count > 0)
                    ((Dictionary<string, List<int>>)_excludeKeys!)[prop.Key] = depths;
            }
        }
        if (json.TryGetPropertyValue("exclude_keywords", out var a2) && a2 is JsonArray ja2)
            foreach (var item in ja2)
                if (item?.GetValue<string>() is string s && s.Length > 0)
                    ((HashSet<string>)_excludeKeywords!).Add(s);
        if (json.TryGetPropertyValue("exclude_exact", out var a3) && a3 is JsonArray ja3)
            foreach (var item in ja3)
                if (item?.GetValue<string>() is string s && s.Length > 0)
                    ((HashSet<string>)_excludeExact!).Add(s);
        if (json.TryGetPropertyValue("exclude_values", out var a4) && a4 is JsonArray ja4)
            foreach (var item in ja4)
                if (item?.GetValue<string>() is string s && s.Length > 0)
                    ((HashSet<string>)_excludeValues!).Add(s);
    }

    private void LoadOverwriteRules()
    {
        var json = ReadJson("overwrite_rules.json");
        if (json == null || !json.TryGetPropertyValue("overwrite_rules", out var obj) || obj is not JsonObject jo)
            return;
        foreach (var prop in jo)
        {
            if (prop.Value?.GetValue<string>() is string rule && rule.Length > 0)
                ((Dictionary<string, string>)_overwriteRules!)[prop.Key] = rule;
        }
    }

    private JsonObject? ReadJson(string fileName)
    {
        try
        {
            string path = System.IO.Path.Combine(_rulesDir, fileName);
            if (!System.IO.File.Exists(path))
                return null;
            return System.Text.Json.JsonSerializer.Deserialize<JsonObject>(System.IO.File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }
}
