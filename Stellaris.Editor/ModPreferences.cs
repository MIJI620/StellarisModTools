// 文件: Stellaris.Editor/ModPreferences.cs
// 模组偏好设置（属于模组，不属于程序）：存放在模组根目录 .smt/ 下。
// 内容：模组前缀、样式导出开关。程序自身设置（语言/字体/窗口等）见 UserPreferences。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Stellaris.Editor;

/// <summary>样式独立开关条目（历史遗留迁移用，见 ModPreferences.StyleFlags）。</summary>
public sealed class StyleFlagEntry
{
    public bool Preview { get; set; }
    public bool Icon { get; set; }
}

/// <summary>
/// 模组偏好（per-mod）：与模组本身绑定，随模组目录存储（`.smt/user_prefs.json`）。
/// </summary>
public sealed class ModPreferences
{
    /// <summary>模组前缀（规范 14.5，用于精灵表 .gfx 与本地化文件名，{prefix}_{name} 带下划线）。</summary>
    public string ModPrefix { get; set; } = "smt";

    /// <summary>启用的本地化语种（模组偏好，与 ModPrefix 同级；空 = 全部启用）。</summary>
    public List<string>? EnabledLanguages { get; set; }

    /// <summary>
    /// 样式独立开关（历史遗留：曾存于本类别，现已迁移到银河类别 galaxy.json）。
    /// 保留该属性仅用于启动时读取旧数据并迁移（App 迁移后置 null）；
    /// 序列化时 null 不写出（见 JsonOptions.DefaultIgnoreCondition）。
    /// </summary>
    public Dictionary<string, StyleFlagEntry>? StyleFlags { get; set; }


    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        WriteIndented = true,
        // 导出尺寸已归位银河类别（galaxy.json），本类别不再持久化；null 字段不写出
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>模组偏好文件路径：{modRoot}/.smt/user_prefs.json（点目录，游戏忽略）。</summary>
    public static string GetDefaultPath(string modRoot)
        => Path.Combine(modRoot, ".smt", "user_prefs.json");

    /// <summary>载入模组偏好；缺失/损坏回退默认（绝不抛异常）。</summary>
    public static ModPreferences Load(string modRoot)
    {
        var prefs = new ModPreferences();
        if (string.IsNullOrEmpty(modRoot) || !Directory.Exists(modRoot))
            return prefs;

        string path = GetDefaultPath(modRoot);
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<ModPreferences>(File.ReadAllText(path), JsonOptions);
                if (loaded != null)
                    prefs = loaded;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ModPreferences] 载入失败 {path}: {ex.Message}");
        }

        // 前缀缺省时从 descriptor.mod 的 name 推断（小写、非字母数字转下划线）
        if (string.IsNullOrWhiteSpace(prefs.ModPrefix) || prefs.ModPrefix == "smt")
        {
            string? inferred = InferPrefixFromDescriptor(modRoot);
            if (!string.IsNullOrEmpty(inferred))
                prefs.ModPrefix = inferred;
        }
        return prefs;
    }

    /// <summary>保存模组偏好（原子写入）。失败返回 false 并记录。 </summary>
    public bool Save(string modRoot)
    {
        if (string.IsNullOrEmpty(modRoot))
            return false;
        try
        {
            string path = GetDefaultPath(modRoot);
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            string temp = path + ".temp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
            if (File.Exists(path))
                File.Delete(path);
            File.Move(temp, path);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ModPreferences] 保存失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>从 descriptor.mod 的 name 推断模组前缀（如 "More Galaxy(Standard Edition)" → more_galaxy_standard_edition）。</summary>
    public static string? InferPrefixFromDescriptor(string modRoot)
    {
        try
        {
            string desc = Path.Combine(modRoot, "descriptor.mod");
            if (!File.Exists(desc))
                return null;
            foreach (var line in File.ReadAllLines(desc))
            {
                string t = line.Trim();
                if (!t.StartsWith("name=", StringComparison.OrdinalIgnoreCase))
                    continue;
                string name = t.Substring(t.IndexOf('=') + 1).Trim('"', ' ', '\t');
                if (name.Length == 0)
                    return null;
                var sb = new System.Text.StringBuilder();
                foreach (char c in name.ToLowerInvariant())
                    sb.Append(char.IsLetterOrDigit(c) ? c : '_');
                string result = sb.ToString().Trim('_');
                return result.Length > 0 ? result : null;
            }
        }
        catch
        {
            // 忽略
        }
        return null;
    }
}
