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
    public List<string> Roots { get; set; } = new();

    /// <summary>加载集合（多套根目录预设）：集合名 → 目录列表；切换集合即切换 Roots 并重新加载。</summary>
    public Dictionary<string, List<string>> RootsProfiles { get; set; } = new(StringComparer.Ordinal);

    /// <summary>当前激活的加载集合名（null = 未命名/直接编辑 Roots）。</summary>
    public string? ActiveRootsProfile { get; set; }

    /// <summary>界面语言（规范 3.2），缺省回退默认语言。</summary>
    public string Language { get; set; } = UILocalisationManager.DefaultLanguage;

    /// <summary>界面字体族（缺省用系统默认，如 "Segoe UI"）。</summary>
    public string FontFamily { get; set; } = "Segoe UI";

    /// <summary>界面字号（缺省 12）。</summary>
    public double FontSize { get; set; } = 12.0;

    // ---- 星系样式预览颜色（第三页"颜色"控制，规范 5.1-b 预览渲染）----
    /// <summary>形状填充色（ARGB hex，缺省半透明蓝）。</summary>
    public string PreviewShapeColor { get; set; } = "#284488CC";

    /// <summary>极坐标网格颜色（同心圆与角度线共用，缺省灰）。</summary>
    public string PreviewGridColor { get; set; } = "#50999999";

    public int WindowWidth { get; set; } = 1280;
    public int WindowHeight { get; set; } = 800;

    /// <summary>上次关闭时窗口是否为最大化（全屏）状态。</summary>
    public bool Maximized { get; set; }


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
                var prefs = JsonSerializer.Deserialize<UserPreferences>(
                    File.ReadAllText(p), JsonOptions);
                if (prefs != null)
                {
                    // 旧适配：有 Roots 但无加载集合 → 迁移为 "Default" 集合（初始播放集）
                    if (prefs.Roots.Count > 0 && prefs.RootsProfiles.Count == 0)
                    {
                        prefs.RootsProfiles["Default"] = new List<string>(prefs.Roots);
                        prefs.ActiveRootsProfile = "Default";
                    }
                    else if (string.IsNullOrEmpty(prefs.ActiveRootsProfile) && prefs.RootsProfiles.Count > 0)
                    {
                        prefs.ActiveRootsProfile = prefs.RootsProfiles.ContainsKey("Default")
                            ? "Default" : prefs.RootsProfiles.Keys.First();
                    }
                    return prefs;
                }
                Debug.WriteLine($"[UserPreferences] 反序列化为空，回退默认偏好: {p}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UserPreferences] 载入失败，回退默认偏好 {p}: {ex.Message}");
        }
        return new UserPreferences();
    }

    /// <summary>
    /// 保存偏好设置（规范 3.1 / 3.3）：原子写入（临时文件 + 重命名）。
    /// 保存失败仅记录警告，不影响当前会话。
    /// </summary>
    public void Save(string? path = null)
    {
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
