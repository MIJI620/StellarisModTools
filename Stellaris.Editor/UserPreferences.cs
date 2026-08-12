// 文件: Stellaris.Editor/UserPreferences.cs
// 偏好设置模块（Editor 规范第三章）：
// 存储用户上次使用的根目录列表（顺序即优先级）、界面语言与主窗口尺寸，
// 存放于 exe 所在目录 config/user_prefs.json，原子写入。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Stellaris.Editor;

/// <summary>
/// 用户偏好设置（规范 3.1）。
/// JSON 结构：
///   {
///     "roots": [ "D:/mods/root_a", "D:/mods/root_b" ],
///     "language": "zh-CN",
///     "window": { "width": 1280, "height": 800 }
///   }
/// roots 顺序即优先级（列表末尾索引最大、优先级最高，与 StellarisAdapter 一致）。
/// </summary>
public sealed class UserPreferences
{
    /// <summary>根目录列表（顺序 = 优先级，末尾最高，规范 3.1）。</summary>
    /// <summary>运行时目录副本（仅内存使用；配置不再保留——持久化走 RootsProfiles + ActiveRootsProfile）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public List<string> Roots { get; set; } = new();

    /// <summary>加载集合（多套根目录预设）：集合名 → 目录列表；切换集合即切换 Roots 并重新加载。</summary>
    public Dictionary<string, List<string>> RootsProfiles { get; set; } = new(StringComparer.Ordinal);

    /// <summary>当前激活的加载集合名（null = 未命名/直接编辑 Roots）。</summary>
    public string? ActiveRootsProfile { get; set; }

    /// <summary>标记为"游戏"（游戏本体）的 root 路径——**全局唯一**（游戏只有一个）。
    /// 解析层"只读一次"规则跳过它（不算最早，优先读它之后的 root；无其他 root 时回退）。</summary>
    public string? GameRoot { get; set; }

    /// <summary>界面语言（规范 3.2），缺省回退默认语言。</summary>
    public string Language { get; set; } = UILocalisationManager.DefaultLanguage;

    /// <summary>界面字体族（缺省用系统默认，如 "Segoe UI"）。</summary>
    public string FontFamily { get; set; } = "Segoe UI";

    /// <summary>界面字号（缺省 12）。</summary>
    public double FontSize { get; set; } = 12.0;

    /// <summary>科技卡片最小宽度（用户设置，缺省 400；科技图布局用）。</summary>
    public int TechCardMinWidth { get; set; } = 400;

    /// <summary>科技卡片最小高度（用户设置，缺省 96；描述换行自适应后不小于此值）。</summary>
    public int TechCardMinHeight { get; set; } = 96;

    // ---- 星系样式预览颜色（第三页"颜色"控制，规范 5.1-b 预览渲染）----
    /// <summary>形状填充色（ARGB hex，缺省半透明蓝）。</summary>
    public string PreviewShapeColor { get; set; } = "#284488CC";

    /// <summary>极坐标网格颜色（同心圆与角度线共用，缺省灰）。</summary>
    public string PreviewGridColor { get; set; } = "#50999999";

    public int WindowWidth { get; set; } = 1280;
    public int WindowHeight { get; set; } = 800;

    /// <summary>上次关闭时窗口是否为最大化（全屏）状态。</summary>
    public bool Maximized { get; set; }

    /// <summary>载入回退标记：Load 失败/为空时回退默认对象。为 true 时 Save 跳过——
    /// 防止"读失败 → 用默认覆盖原文件"把用户配置清空。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsFallback { get; set; }


    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        WriteIndented = true
    };

    /// <summary>偏好文件默认路径：exe 所在目录 config/user_prefs.json（规范 3.1）。</summary>
    public static string DefaultPath
        => Path.Combine(AppContext.BaseDirectory, "config", "user_prefs.json");

    /// <summary>
    /// 载入偏好设置（规范 3.3 防御性）：文件缺失 / 损坏 / 反序列化失败时
    /// 整体回退空偏好并记录警告，绝不抛异常。
    /// </summary>
    public static UserPreferences Load(string? path = null)
    {
        string p = path ?? DefaultPath;
        try
        {
            if (File.Exists(p))
            {
                var rawText = File.ReadAllText(p);
                var prefs = JsonSerializer.Deserialize<UserPreferences>(rawText, JsonOptions);
                if (prefs != null)
                {
                    // Roots 已不序列化（[JsonIgnore]）——旧配置里的 Roots 字段读不进，
                    // 手动从原始 JSON 取（仅用于迁移：无集合时建 "Default"）
                    if (prefs.RootsProfiles.Count == 0)
                    {
                        var legacyRoots = TryReadLegacyRoots(rawText);
                        if (legacyRoots.Count > 0)
                        {
                            prefs.RootsProfiles["Default"] = legacyRoots;
                            prefs.ActiveRootsProfile = "Default";
                        }
                    }
                    // 激活集合缺失但存在集合 → 默认第 1 个（用户要求：缺失默认播放第 1 个）
                    if (string.IsNullOrEmpty(prefs.ActiveRootsProfile)
                        || !prefs.RootsProfiles.ContainsKey(prefs.ActiveRootsProfile))
                    {
                        if (prefs.RootsProfiles.Count > 0)
                        {
                            prefs.ActiveRootsProfile = prefs.RootsProfiles.ContainsKey("Default")
                                ? "Default" : prefs.RootsProfiles.Keys.First();
                        }
                    }
                    return prefs;
                }
                BackupBroken(p);
                LogError($"[UserPreferences] 反序列化为空，回退默认偏好(已备份原文件): {p}");
                var fb = new UserPreferences { IsFallback = true };
                return fb;
            }
            // 文件不存在（首次启动）——正常空默认（非回退：允许保存）
            return new UserPreferences();
        }
        catch (Exception ex)
        {
            BackupBroken(p);
            LogError($"[UserPreferences] 载入失败，回退默认偏好(已备份原文件) {p}: {ex.Message}");
            return new UserPreferences { IsFallback = true };
        }
    }

    /// <summary>载入失败时备份原文件（防止后续 Save 覆盖后无法找回）。</summary>
    private static void BackupBroken(string p)
    {
        try
        {
            if (!File.Exists(p))
                return;
            string bak = p + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Copy(p, bak, overwrite: true);
        }
        catch { }
    }

    /// <summary>写 exe 目录 editor_debug.log（用户可查）——本地化/配置问题都走这里。</summary>
    private static void LogError(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "editor_debug.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Error: {message}{Environment.NewLine}");
        }
        catch { }
    }

    /// <summary>从旧配置原始 JSON 手动读取 Roots 字段（Roots 已 [JsonIgnore]，反序列化读不进——仅迁移用）。</summary>
    private static List<string> TryReadLegacyRoots(string rawText)
    {
        var list = new List<string>();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(rawText);
            if (doc.RootElement.TryGetProperty("Roots", out var roots)
                && roots.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in roots.EnumerateArray())
                {
                    if (item.ValueKind == System.Text.Json.JsonValueKind.String)
                        list.Add(item.GetString()!);
                }
            }
        }
        catch { }
        return list;
    }

    /// <summary>
    /// 保存偏好设置（规范 3.1 / 3.3）：原子写入（临时文件 + 重命名）。
    /// 保存失败仅记录警告，不影响当前会话。
    /// </summary>
    public void Save(string? path = null)
    {
        if (IsFallback)
        {
            // 载入回退默认（Load 失败）——绝不覆盖原文件（数据安全：防止清空用户配置）
            LogError("[UserPreferences] 跳过保存：当前为回退默认对象（Load 失败），不覆盖原文件");
            return;
        }
        string p = path ?? DefaultPath;
        try
        {
            string? dir = Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            string temp = p + ".temp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
            if (File.Exists(p))
                File.Delete(p);
            File.Move(temp, p);
            Debug.WriteLine($"[UserPreferences] 已保存: {p}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UserPreferences] 保存失败 {p}: {ex.Message}");
        }
    }

    /// <summary>判断是否含任何根目录（用于初始化阶段 2 的 Roots 弹出判断）。只读计算属性，不序列化。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasRoots => Roots.Count > 0;
}
