// 文件: Stellaris.Engine/GalaxyStyle/GalaxyStyleEngine.cs

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Stellaris.Engine.ImageAsset;
using Stellaris.Engine.LocalConfigManager;
using Stellaris.Engine.SpriteManagement;
using Stellaris.Parser;
using System.Text.Json.Nodes;

namespace Stellaris.Engine.GalaxyStyle;

public enum EngineTaskType
{
    Idle,
    LoadingStyles,
    SavingStyles,
    ExportingPreview,
    ExportingIcon,
    ExportingAll,
    ComputingAreas
}

public class TaskChangedEventArgs : EventArgs
{
    public EngineTaskType TaskType { get; }
    public string? Argument { get; }
    public TaskChangedEventArgs(EngineTaskType taskType, string? argument = null)
    {
        TaskType = taskType;
        Argument = argument;
    }
}

public sealed class SaveResult
{
    public bool WriteSuccess { get; set; }
    public int IconSuccessCount { get; set; }
    public int IconFailedCount { get; set; }
    public int PreviewSuccessCount { get; set; }
    public int PreviewFailedCount { get; set; }
    public List<string> Errors { get; set; } = new();
    public bool LocalisationSuccess { get; set; } = true;
    public List<string> LocalisationErrors { get; set; } = new();
}

public sealed class GalaxyStyleEngine : IDisposable
{
    private readonly StellarisAdapter _adapter;
    private readonly ImageAssetEngine _imageEngine;
    private readonly SpriteManagementEngine _spriteEngine;
    private readonly ILogger _logger;
    private readonly object _syncRoot = new();
    private readonly GalaxyStyleTable _table;
    private readonly GalaxyAssetExporter _exporter;

    /// <summary>修改过的本地化文件登记（"lang\0相对路径"）——本地化修改 API 执行时登记，保存只写这些；保存后清除。</summary>
    private readonly HashSet<string> _dirtyLocalisationFiles = new(StringComparer.Ordinal);

    /// <summary>登记"本地化文件被修改"（保存时写入）。</summary>
    private void MarkLocalisationDirty(string lang, string path)
    {
        if (!string.IsNullOrEmpty(path))
            _dirtyLocalisationFiles.Add(lang + "\u0000" + path);
    }

    /// <summary>静态地图手动导出用的点集渲染覆盖（样式名 → 点集合；手动"生成预览/图标"时临时设置，
    /// 导出后由调用方清除——不做保存自动导出）。</summary>
    private readonly Dictionary<string, List<Vector2>> _staticPointOverrides = new(StringComparer.Ordinal);

    public void SetStaticPointOverride(string styleName, List<Vector2> points)
        => _staticPointOverrides[styleName] = points;

    public void ClearStaticPointOverrides()
        => _staticPointOverrides.Clear();

    /// <summary>
    /// 将内存样式表（含静态地图占位/绑定样式）写回 galaxy_shapes.txt（原子写入）。
    /// 供统一保存把静态地图同步创建的样式一起落盘——与 SaveAllStyles 使用同一写回机制，不设特例。
    /// </summary>
    public bool WriteStyleTableToDisk()
    {
        lock (_syncRoot)
        {
            try
            {
                _table.SaveToAdapter();
                return _adapter.WriteFile(ConfigPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "样式表写回失败");
                return false;
            }
        }
    }
    private readonly string _modPrefix;
    private readonly IConfigManager? _configManager;
    private bool _disposed;

    public const int LogicalCanvasSize = 500;
    public const float EndRadius = 450.0f;

    /// <summary>样式表配置文件相对路径（规范 14.5）</summary>
    internal const string ConfigPath = "map/galaxy/galaxy_shapes.txt";

    // ---- 本地化缓存（只读索引，数据源为适配器内存） ----
    private readonly Dictionary<string, Dictionary<string, string>> _localisationCache = new();

    // ---- 任务状态 ----
    public event EventHandler<TaskChangedEventArgs>? TaskChanged;
    private EngineTaskType _currentTask = EngineTaskType.Idle;
    public EngineTaskType CurrentTask => _currentTask;

    private void SetTask(EngineTaskType task, string? argument = null)
    {
        if (_currentTask != task || argument != null)
        {
            _currentTask = task;
            TaskChanged?.Invoke(this, new TaskChangedEventArgs(task, argument));
        }
    }

    public GalaxyStyleEngine(StellarisAdapter adapter, ImageAssetEngine imageEngine,
        SpriteManagementEngine spriteEngine, string modPrefix, ILogger? logger = null,
        IConfigManager? configManager = null)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _imageEngine = imageEngine ?? throw new ArgumentNullException(nameof(imageEngine));
        _spriteEngine = spriteEngine ?? throw new ArgumentNullException(nameof(spriteEngine));
        _modPrefix = modPrefix ?? throw new ArgumentNullException(nameof(modPrefix));
        _logger = logger ?? NullLogger.Instance;
        _configManager = configManager; // 未提供则本地配置功能静默禁用（规范 11.1）

        _table = new GalaxyStyleTable(_adapter, _logger);
        _exporter = new GalaxyAssetExporter(_adapter, _imageEngine, _spriteEngine, _modPrefix, _logger);
        LoadAllStyles();
    }

    // =========================================================================
    // 样式表加载与重载
    // =========================================================================
    public void LoadAllStyles()
    {
        lock (_syncRoot)
        {
            SetTask(EngineTaskType.LoadingStyles);
            try
            {
                _logger.LogInformation("加载星系样式表...");
                _table.LoadFromAdapter();
                RefreshLocalisationCache();
                var names = _table.GetAllNames();
                foreach (string name in names)
                {
                    var def = _table.GetStyle(name);
                    if (def == null) continue;
                    def.LocalisedName = GetLocalisedTextFromCache(name, "english") ?? name;
                    if (!string.IsNullOrEmpty(def.Parameters.DescKey))
                        def.LocalisedDescription = GetLocalisedTextFromCache(def.Parameters.DescKey, "english") ?? string.Empty;
                }
                _logger.LogInformation("加载完成，共 {Count} 个样式", names.Count);
            }
            finally
            {
                SetTask(EngineTaskType.Idle);
            }
        }
    }

    public void RefreshStyles()
    {
        lock (_syncRoot)
        {
            SetTask(EngineTaskType.LoadingStyles);
            try
            {
                _adapter.Rescan();
                _table.LoadFromAdapter();
                RefreshLocalisationCache();
                foreach (string name in _table.GetAllNames())
                {
                    var def = _table.GetStyle(name);
                    if (def == null) continue;
                    def.LocalisedName = GetLocalisedTextFromCache(name, "english") ?? name;
                    if (!string.IsNullOrEmpty(def.Parameters.DescKey))
                        def.LocalisedDescription = GetLocalisedTextFromCache(def.Parameters.DescKey, "english") ?? string.Empty;
                }
                _logger.LogInformation("重载完成，共 {Count} 个样式", _table.GetAllNames().Count);
            }
            finally
            {
                SetTask(EngineTaskType.Idle);
            }
        }
    }

    // =========================================================================
    // 本地化缓存管理（从适配器内存读取）
    // =========================================================================
    public void RefreshLocalisationCache()
    {
        _localisationCache.Clear();

        // 直接使用 adapter 已按根目录优先级合并的本地化数据
        // （支持任意 mod 的本地化文件命名，多根目录由 adapter 保证覆盖顺序）
        var all = _adapter.GetAllLocalisations();
        if (all.Count == 0)
        {
            _localisationCache["english"] = new Dictionary<string, string>(StringComparer.Ordinal);
            _logger.LogDebug("本地化缓存加载: 无本地化数据");
            return;
        }

        foreach (var lang in all)
        {
            _localisationCache[lang.Key] = new Dictionary<string, string>(lang.Value, StringComparer.Ordinal);
            _logger.LogDebug("本地化缓存加载: {Lang} -> {Count} 个条目", lang.Key, lang.Value.Count);
        }

        _logger.LogDebug("本地化缓存已刷新，共 {LangCount} 种语言", _localisationCache.Count);
    }

    /// <summary>
    /// 读取"启用语言"列表（银河类别 galaxy.json 的 global.behavior.enabled_languages）。
    /// 未设置或为空时返回全部已加载语言——即默认全部启用（兼容旧行为）。
    /// 影响：AddStyle 自动生成本地化的范围、本地化编辑区的语种下拉可选类别。
    /// </summary>
    private List<string>? _enabledLanguagesOverride;

    /// <summary>注入启用语种（来自模组偏好 ModPreferences.EnabledLanguages，与 ModPrefix 同级；null = 未设置）。</summary>
    public void SetEnabledLanguages(IEnumerable<string>? langs)
        => _enabledLanguagesOverride = langs?.ToList();

    public List<string> GetEnabledLanguages()
    {
        var all = _localisationCache.Keys.ToList();
        if (_enabledLanguagesOverride is { Count: > 0 })
            return new List<string>(_enabledLanguagesOverride);
        if (_configManager == null)
            return all;
        try
        {
            var v = _configManager.Get("galaxy", "global.behavior.enabled_languages");
            if (v is System.Text.Json.Nodes.JsonArray arr && arr.Count > 0)
            {
                var list = new List<string>();
                foreach (var n in arr)
                {
                    if (n is System.Text.Json.Nodes.JsonValue jv
                        && jv.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                        list.Add(s);
                }
                if (list.Count > 0)
                    return list;
            }
        }
        catch
        {
            // 未设置 → 全部
        }
        return all;
    }

    /// <summary>按指定语言查询本地化文本（lang 缺省 english），供 UI 按当前界面语言显示。</summary>
    public string? GetLocalisedText(string key, string? lang = null)
    {
        lock (_syncRoot)
        {
            return GetLocalisedTextFromCache(key, lang ?? "english");
        }
    }

    /// <summary>获取本地化条目的逻辑值（原文，含 $var$ 占位；未展开）。</summary>
    public string? GetLocalisedLogicalText(string key, string? lang = null)
    {
        lock (_syncRoot)
        {
            return _adapter.GetLocalisedLogicalText(key, lang ?? "english");
        }
    }

    /// <summary>
    /// 读取样式导出开关（银河类别 galaxy.json 的 styles.{name}.{kind}，kind = preview|icon）。
    /// 未设置返回 null（由 SaveAllStyles 回退规则决定）；未注入配置管理器返回 null。
    /// </summary>
    public bool? GetStyleSwitch(string styleName, string kind)
    {
        lock (_syncRoot)
        {
            if (_configManager == null)
                return null;
            try
            {
                string key = $"styles.{styleName}.{kind}";
                return _configManager.Exists("galaxy", key)
                    ? (bool)_configManager.Get("galaxy", key)
                    : (bool?)null;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// 设置样式导出开关（写入银河类别 galaxy.json 的 styles.{name}.{kind}）。
    /// 银河样式相关设置一律存此类别（规范 11.x）。
    /// </summary>
    public void SetStyleSwitch(string styleName, string kind, bool value)
    {
        lock (_syncRoot)
        {
            if (_configManager == null)
            {
                _logger.LogWarning("本地配置管理器未注入，样式开关 {Style}.{Kind} 无法保存", styleName, kind);
                return;
            }
            try
            {
                _configManager.Set("galaxy", $"styles.{styleName}.{kind}", value);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "样式开关写入失败: {Style}.{Kind}", styleName, kind);
            }
        }
    }

    private string? GetLocalisedTextFromCache(string key, string lang)
    {
        if (_localisationCache.TryGetValue(lang, out var dict) && dict.TryGetValue(key, out var val))
            return val;
        return null;
    }

    // =========================================================================
    // 本地化管理公开接口（所有修改通过适配器内存接口）
    // =========================================================================
    public string GetLocalisedTitle(string styleName, string lang)
    {
        lang ??= "english";
        lock (_syncRoot)
        {
            return GetLocalisedTextFromCache(styleName, lang) ?? styleName;
        }
    }

    public string GetLocalisedDescription(string styleName, string lang)
    {
        lang ??= "english";
        lock (_syncRoot)
        {
            var def = _table.GetStyle(styleName);
            if (def == null || string.IsNullOrEmpty(def.Parameters.DescKey))
                return string.Empty;
            return GetLocalisedTextFromCache(def.Parameters.DescKey, lang) ?? string.Empty;
        }
    }

    public IReadOnlyDictionary<string, (string Title, string DescKey, string DescText)> GetAllLocalisationForStyle(string styleName)
    {
        lock (_syncRoot)
        {
            var result = new Dictionary<string, (string, string, string)>();
            var def = _table.GetStyle(styleName);
            if (def == null)
                return result;
            string descKey = def.Parameters.DescKey ?? string.Empty;
            foreach (var lang in _localisationCache.Keys)
            {
                string title = GetLocalisedTextFromCache(styleName, lang) ?? styleName;
                string desc = string.IsNullOrEmpty(descKey) ? string.Empty : GetLocalisedTextFromCache(descKey, lang) ?? string.Empty;
                result[lang] = (title, descKey, desc);
            }
            return result;
        }
    }

    /// <summary>样式本地化（名字/描述键）的合规目标文件：localisation/{lang}/{prefix}_style_l_{lang}.yml。</summary>
    public string StyleLocalisationFile(string lang)
        => $"localisation/{lang}/{_modPrefix}_style_l_{lang}.yml";

    public void UpdateLocalisation(string styleName, string lang, string? newTitle = null,
                                   string? newDescKey = null, string? newDescText = null)
    {
        lang ??= "english";
        lock (_syncRoot)
        {
            if (!_table.GetAllNames().Contains(styleName))
                throw new ArgumentException($"样式 '{styleName}' 不存在", nameof(styleName));

            var def = _table.GetStyle(styleName)!;
            string currentDescKey = def.Parameters.DescKey ?? string.Empty;
            string filePath = StyleLocalisationFile(lang);

            // 1. 更新标题（逻辑值 = 用户输入原文；显示值随后展开）
            if (newTitle != null)
            {
                _adapter.UpdateLocalisationEntry(lang, filePath, styleName, newTitle);
                _adapter.ExpandLocalisationKey(lang, styleName);
                MarkLocalisationDirty(lang, filePath);
            }

            // 2. 处理描述键迁移（旧键可能在任何文件：外部 root / 旧命名文件，跨文件读取与删除）
            if (newDescKey != null && newDescKey != currentDescKey)
            {
                // 获取旧描述文本（跨文件读取）
                string? oldDescText = string.IsNullOrEmpty(currentDescKey)
                    ? null
                    : _adapter.GetLocalisedText(currentDescKey, lang);

                // 删除旧条目（若存在，从其实际所在文件删除）
                if (!string.IsNullOrEmpty(currentDescKey))
                {
                    var keyIndex = _adapter.GetLocalisationKeyFiles(lang);
                    if (keyIndex.TryGetValue(currentDescKey, out var oldFile) && oldFile != null)
                    {
                        _adapter.RemoveLocalisationEntry(lang, oldFile, currentDescKey);
                        MarkLocalisationDirty(lang, oldFile);
                    }
                }

                // 添加新条目（保留旧文本或使用 newDescText）
                string finalDescText = newDescText ?? oldDescText ?? string.Empty;
                _adapter.AddLocalisationEntry(lang, filePath, newDescKey, finalDescText);
                _adapter.ExpandLocalisationKey(lang, newDescKey);
                MarkLocalisationDirty(lang, filePath);

                // 更新样式参数中的 DescKey
                var newParams = def.Parameters.Clone();
                newParams.DescKey = newDescKey;
                newParams.RawInputs.Remove("desc");
                _table.UpdateStyle(styleName, newParams);
            }
            else if (newDescText != null && !string.IsNullOrEmpty(currentDescKey))
            {
                // 仅更新描述文本
                _adapter.UpdateLocalisationEntry(lang, filePath, currentDescKey, newDescText);
                _adapter.ExpandLocalisationKey(lang, currentDescKey);
                MarkLocalisationDirty(lang, filePath);
            }
            else if (newDescText != null && string.IsNullOrEmpty(currentDescKey))
            {
                // descKey 为空但有新描述文本 -> 自动创建描述键
                string autoDescKey = $"{styleName}_desc";
                _adapter.AddLocalisationEntry(lang, filePath, autoDescKey, newDescText);
                _adapter.ExpandLocalisationKey(lang, autoDescKey);
                MarkLocalisationDirty(lang, filePath);
                var newParams = def.Parameters.Clone();
                newParams.DescKey = autoDescKey;
                newParams.RawInputs.Remove("desc");
                _table.UpdateStyle(styleName, newParams);
            }

            // 刷新缓存
            RefreshLocalisationCache();

            // 更新本地化属性
            var updatedDef = _table.GetStyle(styleName)!;
            updatedDef.LocalisedName = GetLocalisedTextFromCache(styleName, lang) ?? styleName;
            if (!string.IsNullOrEmpty(updatedDef.Parameters.DescKey))
                updatedDef.LocalisedDescription = GetLocalisedTextFromCache(updatedDef.Parameters.DescKey, lang) ?? string.Empty;
        }
        // 更新本地化后刷新缓存——否则 GetLocalisedText 返回旧显示值（描述逻辑值编辑不更新）
        RefreshLocalisationCache();
    }

    /// <summary>规整化单个样式（公共入口，刷新本地化缓存）。仅改内存，不落盘（保存由 SaveAllStyles 显式触发）。</summary>
    public void NormalizeKeys(string styleName)
    {
        lock (_syncRoot)
        {
            NormalizeKeysCore(styleName, refreshCache: true);
        }
    }

    /// <summary>
    /// 批量规整化全部样式（只刷新一次本地化缓存，避免逐个全量重建导致卡顿）。
    /// 仅改内存，不落盘。
    /// </summary>
    /// <summary>
    /// 确保精灵表（规整化用）：按每个样式的 preview_icon/button_icon 引用查 gfx——
    /// 缺失的 spriteType 补齐、texturefile 路径不对的修正。只改内存 AST，随保存落盘。
    /// </summary>
    public void EnsureGalaxySpriteTable()
    {
        string gfxPath = $"interface/game_setup/{_modPrefix}_galaxy_shapes.gfx";
        // 按样式生成精灵（icon 是引用：复用同名 → EnsureGalaxySprites 内已存在即跳过，不创建/不覆盖）。
        // 开关过滤（必要）：preview/icon 未勾选（galaxy.json false）→ 不创建对应精灵表；
        // 有 preview → 可能需要创建预览精灵表；无 icon 的样式不生成按钮精灵。
        // 按钮 3 帧横排 → noOfFrames = 3（原版惯例，见 setup.gfx）。
        var sprites = new List<(string Name, string TextureFile, int? NoOfFrames)>();
        foreach (var name in _table.GetAllNames())
        {
            var def = _table.GetStyle(name);
            if (def == null)
                continue;
            bool previewOn = GetStyleSwitch(name, "preview") ?? true;
            bool iconOn = GetStyleSwitch(name, "icon") ?? true;
            if (previewOn && !string.IsNullOrEmpty(def.Parameters.PreviewIcon))
                sprites.Add((def.Parameters.PreviewIcon, $"gfx/interface/game_setup/galaxy_preview/{_modPrefix}_{name}.dds", null));
            if (iconOn && !string.IsNullOrEmpty(def.Parameters.ButtonIcon))
                sprites.Add((def.Parameters.ButtonIcon, $"gfx/interface/game_setup/galaxy_button/{_modPrefix}_{name}.dds", 3));
        }
        // 只改内存 AST + 同步精灵索引；保存时 GetGalaxySpriteFiles 会收集涉及文件一并写回
        _spriteEngine.EnsureGalaxySprites(sprites, gfxPath);
    }

    public void NormalizeAllKeys()
    {
        lock (_syncRoot)
        {
            // 预构建"键 → 当前文件"索引（每语言一次 O(全键)），避免逐样式反复全表扫描导致卡顿
            var keyFiles = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
            foreach (var lang in _localisationCache.Keys)
                keyFiles[lang] = _adapter.GetLocalisationKeyFiles(lang);

            foreach (var name in _table.GetAllNames())
            {
                try
                {
                    NormalizeKeysCore(name, refreshCache: false, keyFiles);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "规整化样式失败（继续其余）: {Style}", name);
                }
            }
            RefreshLocalisationCache();
        }
    }

    /// <summary>
    /// 规整化核心：把样式名/desc 键迁移到合规文件并修正图标字段。
    /// pendingFiles（"lang\0相对路径"）收集待保存文件，供保存流程只写涉及文件。
    /// </summary>
    /// <summary>导出单个样式预览图（静态点集覆盖时用点集渲染）。</summary>
    public OperationStatus ExportSinglePreview(string styleName, PreviewOptions? options = null)
    {
        lock (_syncRoot)
        {
            SetTask(EngineTaskType.ExportingPreview, styleName);
            try
            {
                var def = _table.GetStyle(styleName);
                if (def == null)
                    return SetError(OperationStatus.FileNotFound, $"样式 '{styleName}' 不存在");
                var opts = PreviewOptions.Default.Merge(options);
                _exporter.ExportPreview(styleName, def.Parameters, opts,
                    _staticPointOverrides.GetValueOrDefault(styleName));
                return OperationStatus.Success;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "预览导出失败: {StyleName}", styleName);
                return SetError(OperationStatus.UnknownError, ex.Message);
            }
            finally
            {
                SetTask(EngineTaskType.Idle);
            }
        }
    }

    /// <summary>导出全部样式预览图。</summary>
    public (int Success, int Failed) ExportAllPreviews(PreviewOptions? options = null)
    {
        lock (_syncRoot)
        {
            SetTask(EngineTaskType.ExportingAll, "Previews");
            try
            {
                int s = 0, f = 0;
                foreach (string name in _table.GetAllNames())
                {
                    var st = ExportSinglePreview(name, options);
                    if (st == OperationStatus.Success) s++; else f++;
                }
                return (s, f);
            }
            finally
            {
                SetTask(EngineTaskType.Idle);
            }
        }
    }

    /// <summary>导出单个样式图标（静态点集覆盖时用点集渲染）。</summary>
    public OperationStatus ExportSingleIcon(string styleName, IconOptions? options = null)
    {
        lock (_syncRoot)
        {
            SetTask(EngineTaskType.ExportingIcon, styleName);
            try
            {
                var def = _table.GetStyle(styleName);
                if (def == null)
                    return SetError(OperationStatus.FileNotFound, $"样式 '{styleName}' 不存在");
                var opts = IconOptions.Default.Merge(options);
                _exporter.ExportIcon(styleName, def.Parameters, opts,
                    _staticPointOverrides.GetValueOrDefault(styleName));
                return OperationStatus.Success;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "图标导出失败: {StyleName}", styleName);
                return SetError(OperationStatus.UnknownError, ex.Message);
            }
            finally
            {
                SetTask(EngineTaskType.Idle);
            }
        }
    }

    /// <summary>导出全部样式图标。</summary>
    public (int Success, int Failed) ExportAllIcons(IconOptions? options = null)
    {
        lock (_syncRoot)
        {
            SetTask(EngineTaskType.ExportingAll, "Icons");
            try
            {
                int s = 0, f = 0;
                foreach (string name in _table.GetAllNames())
                {
                    var st = ExportSingleIcon(name, options);
                    if (st == OperationStatus.Success) s++; else f++;
                }
                return (s, f);
            }
            finally
            {
                SetTask(EngineTaskType.Idle);
            }
        }
    }

    /// <summary>获取全部样式名（按当前顺序）。</summary>
    public List<string> GetAllStyleNames()
    {
        lock (_syncRoot)
        {
            return _table.GetAllNames().ToList();
        }
    }

    /// <summary>强制导出全部启用样式的预览图（无视增量；一键检查导出效果）。</summary>
    public (int Ok, int Fail) ExportAllPreviewsEnabled()
    {
        lock (_syncRoot)
        {
            int ok = 0, fail = 0;
            foreach (string name in _table.GetAllNames())
            {
                if (GetStyleSwitch(name, "preview") ?? true)
                {
                    if (ExportSinglePreview(name) == OperationStatus.Success) ok++; else fail++;
                }
            }
            return (ok, fail);
        }
    }

    /// <summary>强制导出全部启用样式的图标（无视增量；一键检查导出效果）。</summary>
    public (int Ok, int Fail) ExportAllIconsEnabled()
    {
        lock (_syncRoot)
        {
            // 按精灵表驱动：gfx 里声明的 GFX_galaxy_button_* spriteType → 导出对应按钮图。
            // （不是遍历样式表——精灵表里没有的按钮不应凭空导出）
            int ok = 0, fail = 0;
            foreach (var spriteName in _spriteEngine.GetGalaxySpriteNamesByPrefix("GFX_galaxy_button_"))
            {
                string styleName = spriteName.Substring("GFX_galaxy_button_".Length);
                var def = _table.GetStyle(styleName);
                if (def == null)
                    continue;
                if (GetStyleSwitch(styleName, "icon") ?? true)
                {
                    if (ExportSingleIcon(styleName) == OperationStatus.Success) ok++; else fail++;
                }
            }
            return (ok, fail);
        }
    }

    /// <summary>
    /// 强制导出全部启用样式的图标与预览（无视"仅修改才导出"的增量逻辑；供一键检查导出效果）。
    /// 按各样式导出开关（galaxy.json styles.{name}.preview|icon，默认 true）执行。
    /// </summary>
    public (int PreviewOk, int PreviewFail, int IconOk, int IconFail) ExportAllEnabled()
    {
        lock (_syncRoot)
        {
            int po = 0, pf = 0, io = 0, ix = 0;
            foreach (string name in _table.GetAllNames())
            {
                bool pv = GetStyleSwitch(name, "preview") ?? true;
                bool ic = GetStyleSwitch(name, "icon") ?? true;
                if (pv)
                {
                    var st = ExportSinglePreview(name);
                    if (st == OperationStatus.Success) po++; else pf++;
                }
                if (ic)
                {
                    var st = ExportSingleIcon(name);
                    if (st == OperationStatus.Success) io++; else ix++;
                }
            }
            return (po, pf, io, ix);
        }
    }

    /// <summary>获取样式定义（参数）的克隆。</summary>
    public GalaxyStyleDefinition? GetStyle(string name)
    {
        lock (_syncRoot)
        {
            return _table.GetStyle(name);
        }
    }

    /// <summary>更新样式的单个参数（raw 输入文本；null/空 = 移除该参数）。</summary>
    public void UpdateStyleParam(string styleName, string paramPath, string? input)
    {
        lock (_syncRoot)
        {
            var def = _table.GetStyle(styleName);
            if (def == null)
                return;
            var p = def.Parameters.Clone();
            if (string.IsNullOrEmpty(input))
                p.RawInputs.Remove(paramPath);
            else
                p.RawInputs[paramPath] = input;
            // 同步更新强类型属性（渲染/计算用）——input 为纯数字时解析；@引用保留 RawInputs 原文
            if (paramPath == "desc")
                ApplyDescKeyChange(p, paramPath, input);
            else
                ApplyRawInputToParam(p, paramPath, input);
            _table.UpdateStyle(styleName, p);
        }
    }

    /// <summary>把参数输入的原始文本求值写回强类型属性（"stars_min_dist" → p.StarsMinDist 等）。</summary>
    /// <summary>descKey 变更：旧自动生成的 descKey 不再被引用 → 删除其本地化条目（各语言，登记 dirty）。</summary>
    private void ApplyDescKeyChange(GalaxyShapeParameters p, string path, string? input)
    {
        string oldDesc = p.DescKey ?? string.Empty;
        if (string.Equals(oldDesc, input ?? string.Empty, StringComparison.Ordinal))
            return;
        if (!string.IsNullOrEmpty(oldDesc))
        {
            foreach (var lang in _localisationCache.Keys)
            {
                var idx = _adapter.GetLocalisationKeyFiles(lang);
                if (idx != null && idx.TryGetValue(oldDesc, out var file) && !string.IsNullOrEmpty(file))
                {
                    _adapter.RemoveLocalisationEntry(lang, file, oldDesc);
                    MarkLocalisationDirty(lang, file);
                }
            }
        }
        p.DescKey = input;
    }

    private static void ApplyRawInputToParam(GalaxyShapeParameters p, string path, string? input)
    {
        if (string.IsNullOrEmpty(input) || input.TrimStart().StartsWith("@"))
            return; // @引用：无法求值，保留 RawInputs 原文，属性不动
        switch (path)
        {
            case "core_radius_perc": if (double.TryParse(input, out var d1)) p.CoreRadiusPerc = d1; break;
            case "num_stars_core_perc": if (double.TryParse(input, out var d2)) p.NumStarsCorePerc = d2; break;
            case "stars_min_dist": if (double.TryParse(input, out var d3)) p.StarsMinDist = d3; break;
            case "num_arms": if (int.TryParse(input, out var i1)) p.NumArms = i1; break;
            case "countries.ideal_sq_dist_between": if (int.TryParse(input, out var i2)) p.CountriesIdealDist = i2; break;
            case "countries.min_sq_dist_between": if (int.TryParse(input, out var i3)) p.CountriesMinDist = i3; break;
            case "fallen_empires.ideal_sq_dist_between": if (int.TryParse(input, out var i4)) p.FallenIdealDist = i4; break;
            case "fallen_empires.min_sq_dist_between": if (int.TryParse(input, out var i5)) p.FallenMinDist = i5; break;
            case "arms.tightness_winding": if (double.TryParse(input, out var d4)) p.Tightness = d4; break;
            case "arms.width": if (double.TryParse(input, out var d5)) p.WidthDeg = d5; break;
            case "arms.fuzz": if (double.TryParse(input, out var d6)) p.Fuzz = d6; break;
            case "arms.seperation": if (double.TryParse(input, out var d7)) p.ArmAngleDeg = d7; break;
            case "ring.width": if (double.TryParse(input, out var d8)) p.RingWidth = d8; break;
            case "ring.offset": if (double.TryParse(input, out var d9)) p.RingOffset = d9; break;
            case "preview_icon": p.PreviewIcon = input; break;
            case "button_icon": p.ButtonIcon = input; break;
            case "desc": p.DescKey = input; break;
        }
    }

    /// <summary>只写"待保存本地化文件集"（"lang\0相对路径"）到 mod 目录；返回是否全部成功与错误列表。</summary>
    private (bool AllSuccess, List<string> Errors) WritePendingLocalisations(HashSet<string> pendingFiles)
    {
        bool allOk = true;
        var errors = new List<string>();
        string modRoot = _adapter.Roots.Count > 0 ? _adapter.Roots[^1] : string.Empty;
        if (string.IsNullOrEmpty(modRoot))
            return (false, new List<string> { "无可用 mod 根目录" });
        foreach (var item in pendingFiles)
        {
            int idx = item.IndexOf('\0');
            if (idx <= 0 || idx >= item.Length - 1)
                continue;
            string lang = item[..idx];
            string relPath = item[(idx + 1)..];
            string fileName = relPath.Substring(relPath.LastIndexOf('/') + 1);
            if (!_adapter.WriteLocalisation(lang, fileName, modRoot, writeIfEmpty: true))
            {
                allOk = false;
                errors.Add($"本地化写入失败: {relPath}");
            }
        }
        return (allOk, errors);
    }

    private void NormalizeKeysCore(string styleName, bool refreshCache,
        Dictionary<string, IReadOnlyDictionary<string, string>>? keyFiles = null)
    {
        var def = _table.GetStyle(styleName);
        if (def == null)
            throw new ArgumentException($"样式 '{styleName}' 不存在", nameof(styleName));

        // 规范化 = 按需修改：先查当前状态，位置/格式正确就不动，只改错误的。
        // preview_icon / button_icon 标准格式是**精灵名**（GFX_galaxy_preview_xxx），
        // 且必须先有 .gfx spriteType 声明（导出时注册）才能被引用。
        var newParams = def.Parameters.Clone();
        bool changed = false;

        if (string.IsNullOrEmpty(newParams.DescKey))
        {
            newParams.DescKey = $"{styleName}_desc";
            changed = true;
        }

        // 描述本地化与样式名本地化：统一处理（键 = 样式名 与 desc 键）。
        // 用"键 → 当前文件"索引 O(1) 判断位置；位置错 → 迁移到合规文件（旧文件删除 + 写入合规文件）。
        // 某语种缺内容时：优先复制英文；英文也没有 → 用任意有该键的语种内容。
        // pendingFiles：收集"待保存文件"（"lang\0相对路径"），供保存时只写涉及文件（O(n) 而非全量）。
        //   规则：键当前所在文件若属于本 mod 目录 → 待保存（写剩余/空头清理）；
        //        目标文件与当前位置不同（或键缺失）→ 待保存（创建/写入合规文件）。
        //        外部 root（游戏本体等）的文件只读、绝不写入，靠本 mod 覆盖兼容。
        string[] keysToMove = { styleName, newParams.DescKey };
        string modRoot = _adapter.Roots.Count > 0 ? _adapter.Roots[^1] : string.Empty;
        foreach (var lang in _localisationCache.Keys)
        {
            // 样式本地化的目标文件：localisation/{lang}/{prefix}_style_l_{lang}.yml
            string targetFile = StyleLocalisationFile(lang);

            // 该语言"键 → 当前文件"索引（预构建，O(1) 判断位置）
            var index = keyFiles != null && keyFiles.TryGetValue(lang, out var idx)
                ? idx
                : _adapter.GetLocalisationKeyFiles(lang);

            foreach (var key in keysToMove)
            {
                if (string.IsNullOrEmpty(key))
                    continue;

                string? currentFile = index.TryGetValue(key, out var f) ? f : null;

                // 键当前所在文件（属于本 mod）→ 待保存（写剩余键或空头清理）
                if (currentFile != null && IsInModRoot(currentFile, modRoot))
                    MarkLocalisationDirty(lang, currentFile);

                // 已在合规位置（且文件在磁盘已存在）→ 不改不写
                if (currentFile != null
                    && string.Equals(currentFile, targetFile, StringComparison.OrdinalIgnoreCase))
                    continue;

                // 需要迁移或新建 → 目标文件待保存
                MarkLocalisationDirty(lang, targetFile);

                // 取文本：该语种有则保留；缺失 → 复制英文 → 再没有 → 任意语种
                string? text = _adapter.GetLocalisedText(key, lang);
                if (text == null)
                    text = ResolveMissingLocalisationText(key, lang);

                // 从旧文件（当前所在文件）删除该键（外部 root 的文件不写盘，仅内存移除，靠覆盖兼容）
                if (currentFile != null)
                    _adapter.RemoveLocalisationEntry(lang, currentFile, key);

                // 写入目标文件（保存时落盘；oldPath 记录旧文件供清理）
                _adapter.AddLocalisationEntry(lang, targetFile, key, text ?? string.Empty, null, currentFile);
                changed = true;
            }
        }

        // 图标：icon 是**精灵名引用**——多个样式可复用同一精灵（copy 复用原样式 icon 是正常行为）。
        // 只校验格式前缀（GFX_galaxy_preview_ / GFX_galaxy_button_ + 非空），不强改"自己的名字"；
        // 精灵表由 EnsureGalaxySpriteTable 按**唯一精灵名**去重生成（复用只生成一个）。
        if (string.IsNullOrEmpty(newParams.PreviewIcon)
            || !newParams.PreviewIcon.StartsWith("GFX_galaxy_preview_", StringComparison.Ordinal))
        {
            newParams.PreviewIcon = $"GFX_galaxy_preview_{styleName}";
            changed = true;
        }
        if (string.IsNullOrEmpty(newParams.ButtonIcon)
            || !newParams.ButtonIcon.StartsWith("GFX_galaxy_button_", StringComparison.Ordinal))
        {
            newParams.ButtonIcon = $"GFX_galaxy_button_{styleName}";
            changed = true;
        }

        // 精灵表文件检查（只报告，不自动重命名——涉及文件系统与 sprite 引用，风险高）
        string gfxFile = $"interface/game_setup/{_modPrefix}_galaxy_shapes.gfx";
        if (!_adapter.GetAllLoadedFiles().Any(kv =>
                kv.Key.Equals(gfxFile, StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogDebug("精灵表文件不存在（导出时由 SpriteManagementEngine 创建）: {File}", gfxFile);
        }

        if (!changed)
            return; // 字段与位置均正确：不产生任何改动，也不刷新

        _table.UpdateStyle(styleName, newParams);

        // 刷新缓存（批量入口可跳过，由 NormalizeAllKeys 统一刷新一次）
        if (refreshCache)
            RefreshLocalisationCache();

        var updatedDef = _table.GetStyle(styleName)!;
        updatedDef.LocalisedName = GetLocalisedTextFromCache(styleName, "english") ?? styleName;
        updatedDef.LocalisedDescription = GetLocalisedTextFromCache(newParams.DescKey, "english") ?? string.Empty;
    }

    /// <summary>
    /// 某语种缺少指定键的本地化内容时取文本：优先英文；英文也没有 → 任意有该键的语种。
    /// </summary>
    private string? ResolveMissingLocalisationText(string key, string currentLang)
    {
        string? text = _adapter.GetLocalisedText(key, "english");
        if (text != null)
            return text;
        foreach (var other in _localisationCache.Keys)
        {
            if (string.Equals(other, currentLang, StringComparison.OrdinalIgnoreCase))
                continue;
            string? t = _adapter.GetLocalisedText(key, other);
            if (t != null)
                return t;
        }
        return null;
    }

    /// <summary>
    /// 判断本地化相对路径是否可写（属于本 mod 目录 Roots[-1]，或为本 mod 新建、尚未扫描的文件）。
    /// 外部 root（游戏本体等）中已存在的文件只读不写，靠本 mod 覆盖兼容。
    /// </summary>
    private bool IsInModRoot(string relPath, string modRoot)
    {
        if (string.IsNullOrEmpty(modRoot))
            return false;
        string? root = _adapter.GetFileRoot(relPath);
        // 未扫描（null）= 本 mod 新建（内存创建）→ 写 mod 目录；
        // 已扫描 → 必须属于 mod 目录才可写（外部 root 文件只读）。
        return root == null || string.Equals(root, modRoot, StringComparison.OrdinalIgnoreCase);
    }

    public void SetStyleIcons(string styleName, string previewIcon, string buttonIcon)
    {
        lock (_syncRoot)
        {
            var def = _table.GetStyle(styleName);
            if (def == null)
                throw new ArgumentException($"样式 '{styleName}' 不存在", nameof(styleName));

            var newParams = def.Parameters.Clone();
            newParams.PreviewIcon = previewIcon;
            newParams.ButtonIcon = buttonIcon;
            newParams.RawInputs.Remove("preview_icon");
            newParams.RawInputs.Remove("button_icon");
            _table.UpdateStyle(styleName, newParams);
        }
    }

    // =========================================================================
    // 样式管理联动
    // =========================================================================
    public void AddStyle(string name, GalaxyShapeParameters parameters,
                         Dictionary<string, (string Title, string DescText)>? localisation = null,
                         int index = -1)
    {
        lock (_syncRoot)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("名称不能为空", nameof(name));
            if (parameters == null) throw new ArgumentNullException(nameof(parameters));

            // 准备本地化数据（自动生成仅覆盖"启用语言"——不想做多语言适配可只启用少量语言）
            bool autoGenerated = false;
            if (localisation == null)
            {
                localisation = new Dictionary<string, (string, string)>();
                var supportedLangs = GetEnabledLanguages();
                if (supportedLangs.Count == 0)
                    supportedLangs = new List<string> { "english", "simp_chinese" };
                foreach (var lang in supportedLangs)
                {
                    localisation[lang] = (name, string.Empty);
                }
                parameters = parameters.Clone();
                parameters.DescKey = $"{name}_desc";
                parameters.RawInputs.Remove("desc");
                autoGenerated = true;
            }

            // 添加样式到样式表（index 指定显示/落盘位置，-1 追加末尾）
            var def = new GalaxyStyleDefinition(name, parameters);
            _table.AddStyle(def, index);

            // 添加本地化条目到适配器内存（统一写入合规文件 style_l_{lang}.yml）
            string descKey = parameters.DescKey ?? $"{name}_desc";
            foreach (var kv in localisation)
            {
                string lang = kv.Key;
                var (title, descText) = kv.Value;
                string filePath = StyleLocalisationFile(lang);

                _adapter.AddLocalisationEntry(lang, filePath, name, title);
                MarkLocalisationDirty(lang, filePath);
                if (!string.IsNullOrEmpty(descKey))
                {
                    _adapter.AddLocalisationEntry(lang, filePath, descKey, descText);
                    MarkLocalisationDirty(lang, filePath);
                }
            }

            // 刷新缓存
            RefreshLocalisationCache();

            // 更新本地化属性
            var addedDef = _table.GetStyle(name)!;
            addedDef.LocalisedName = GetLocalisedTextFromCache(name, "english") ?? name;
            if (!string.IsNullOrEmpty(descKey))
                addedDef.LocalisedDescription = GetLocalisedTextFromCache(descKey, "english") ?? string.Empty;

            _logger.LogInformation("添加样式: {Name}", name);
        }
    }

    public bool DeleteStyle(string name)
    {
        lock (_syncRoot)
        {
            var def = _table.GetStyle(name);
            if (def == null)
                return false;

            string descKey = def.Parameters.DescKey ?? string.Empty;

            // 从样式表删除
            bool ok = _table.DeleteStyle(name);
            if (!ok)
                return false;

            // 从适配器内存删除本地化条目（统一目标文件 style_l_{lang}.yml）——并登记待保存
            // （否则删除只在内存，保存时 _dirtyLocalisationFiles 不含该文件 → 磁盘残留）
            foreach (var lang in _localisationCache.Keys)
            {
                string filePath = StyleLocalisationFile(lang);
                _adapter.RemoveLocalisationEntry(lang, filePath, name);
                if (!string.IsNullOrEmpty(descKey))
                {
                    _adapter.RemoveLocalisationEntry(lang, filePath, descKey);
                }
                _dirtyLocalisationFiles.Add($"{lang} {filePath}");
            }

            // 删除 gfx 精灵（预览/按钮）——**全局值反查询**：该精灵名作为字符串值在
            // 任何文件任何节点出现过（其他样式引用/复用、其他字段值）则保留不删；
            // 只排除被删样式自身的 PreviewIcon/ButtonIcon 引用（它正要被删除）。
            // **必须 try-catch**：反查询/删除任何一步失败都不能阻断删除（否则异常传播到 UI，
            // 列表不刷新 → 看起来"删不掉"）。
            try
            {
                foreach (var spriteName in new[] { "GFX_galaxy_preview_" + name, "GFX_galaxy_button_" + name })
                {
                    int selfRefs = 0;
                    if (string.Equals(def.Parameters.PreviewIcon, spriteName, StringComparison.Ordinal)) selfRefs++;
                    if (string.Equals(def.Parameters.ButtonIcon, spriteName, StringComparison.Ordinal)) selfRefs++;
                    var hits = _adapter.FindStringValues(spriteName);
                    int total = hits.Count > 0 ? (int)hits[0] : 0;   // 第 1 位 = 出现次数
                    if (total > selfRefs)
                    {
                        _logger.LogInformation("精灵 '{Sprite}' 仍被引用（全 AST 出现 {Total} 次，自身 {Self} 次），保留不删",
                            spriteName, total, selfRefs);
                        continue;
                    }
                    var sdef = _spriteEngine.GetSpriteDefinition(spriteName);
                    if (sdef != null)
                        _spriteEngine.RemoveSprite(sdef.SourceFile, spriteName);
                }
            }
            catch (Exception gfxEx)
            {
                // gfx 反查询/删除失败不阻断样式删除（日志记录，样式本体已删）
                _logger.LogWarning(gfxEx, "删除样式 {Name} 时 gfx 清理失败（忽略——样式已删除）", name);
            }

            // 删除 galaxy.json 样式设置（StyleFlags[name]——否则保存后残留"不存在的东西"）
            if (_configManager != null)
            {
                try { _configManager.Delete("galaxy", "style_flags." + name); } catch { }
            }

            // 刷新缓存
            RefreshLocalisationCache();

            _logger.LogInformation("删除样式: {Name}", name);
            return true;
        }
    }

    /// <summary>
    /// 重命名样式：更新样式 key（galaxy_shapes.txt 块名）与本地化键（样式名键、desc 键）。
    /// desc 键若为 {oldName}_desc 则同步改为 {newName}_desc，其余语言本地化值保留。
    /// </summary>
    /// <summary>按新顺序重排样式表（拖拽排序后调用；保存时按此顺序落盘）。</summary>
    public void ReorderStyles(IReadOnlyList<string> order)
    {
        lock (_syncRoot)
        {
            _logger.LogInformation("ReorderStyles 收到顺序 ({Count}): {Order}", order?.Count ?? 0,
                order == null ? "<null>" : string.Join(",", order.Take(12)));
            _table.ReorderStyles(order);
            _logger.LogInformation("ReorderStyles 后 GetAllNames: {Order}",
                string.Join(",", _table.GetAllNames().Take(12)));
        }
    }

    /// <summary>应用 galaxy.json 里保存的样式顺序（style_order——重启/重载后恢复拖拽排序）。
    /// 仅应用已存在的 key；无存储顺序时保持文件顺序。</summary>
    public void ApplyStoredStyleOrder()
    {
        if (_configManager == null)
            return;
        try
        {
            var stored = _configManager.Get("galaxy", "style_order");
            if (stored is not System.Collections.IEnumerable list)
                return;
            var order = new List<string>();
            foreach (var item in list)
            {
                var s = item?.ToString();
                if (!string.IsNullOrEmpty(s) && _table.GetAllNames().Contains(s))
                    order.Add(s);
            }
            // 补上未在存储中的样式（新增样式保持末尾）
            foreach (var name in _table.GetAllNames())
            {
                if (!order.Contains(name))
                    order.Add(name);
            }
            if (order.Count > 0)
                ReorderStyles(order);
        }
        catch (KeyNotFoundException)
        {
            // 尚未保存过 style_order（galaxy.json 无此键）——正常，保持文件顺序
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "应用存储的样式顺序失败");
        }
    }

    public bool RenameStyle(string oldName, string newName)
    {
        lock (_syncRoot)
        {
            if (string.IsNullOrWhiteSpace(newName))
                throw new ArgumentException("新样式名不能为空", nameof(newName));
            if (string.Equals(oldName, newName, StringComparison.Ordinal))
                return true;

            var def = _table.GetStyle(oldName);
            if (def == null)
                return false;
            if (_table.GetAllNames().Contains(newName))
                throw new InvalidOperationException($"样式 '{newName}' 已存在");

            string oldDescKey = def.Parameters.DescKey ?? $"{oldName}_desc";
            bool descAuto = string.Equals(oldDescKey, $"{oldName}_desc", StringComparison.Ordinal);
            string newDescKey = descAuto ? $"{newName}_desc" : oldDescKey;

            // 迁移本地化键（所有语言）：样式名键 oldName → newName；desc 键变化时同样迁移
            foreach (var lang in _localisationCache.Keys)
            {
                MoveLocalisationKey(lang, oldName, newName);
                if (descAuto && !string.Equals(oldDescKey, newDescKey, StringComparison.Ordinal))
                    MoveLocalisationKey(lang, oldDescKey, newDescKey);
            }

            // 更新样式定义（Name + DescKey + PreviewIcon/ButtonIcon 引用同步改名）
            var newParams = def.Parameters.Clone();
            newParams.DescKey = newDescKey;
            if (!string.IsNullOrEmpty(newParams.PreviewIcon)
                && newParams.PreviewIcon.Contains("GFX_galaxy_preview_" + oldName, StringComparison.Ordinal))
                newParams.PreviewIcon = "GFX_galaxy_preview_" + newName;
            if (!string.IsNullOrEmpty(newParams.ButtonIcon)
                && newParams.ButtonIcon.Contains("GFX_galaxy_button_" + oldName, StringComparison.Ordinal))
                newParams.ButtonIcon = "GFX_galaxy_button_" + newName;
            _table.RenameStyle(oldName, newName, newParams);

            // gfx 精灵同步：spriteType 名（GFX_galaxy_preview/button_{old} → {new}）+
            // texturefile 路径（.../{old}.dds → .../{new}.dds）——先建后删（数据安全）
            foreach (var prefix in new[] { "preview", "button" })
            {
                string oldSprite = $"GFX_galaxy_{prefix}_{oldName}";
                string newSprite = $"GFX_galaxy_{prefix}_{newName}";
                try
                {
                    var sdef = _spriteEngine.GetSpriteDefinition(oldSprite);
                    if (sdef == null)
                        continue;
                    string newTex = sdef.TextureFile.Replace(oldName, newName, StringComparison.Ordinal);
                    _spriteEngine.AddSprite(sdef.SourceFile, newSprite, newTex, sdef.NoOfFrames, OperationMode.Overwrite);
                    _spriteEngine.RemoveSprite(sdef.SourceFile, oldSprite);
                    _logger.LogInformation("样式改名同步 gfx 精灵: {Old} -> {New}", oldSprite, newSprite);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "重命名样式 {Old} -> {New} 时 gfx 精灵同步失败（忽略）", oldName, newName);
                }
            }

            RefreshLocalisationCache();
            _logger.LogInformation("重命名样式: {Old} -> {New}", oldName, newName);
            return true;
        }
    }

    /// <summary>把本地化键从 oldKey 迁移到 newKey（保留逻辑值，重算显示值）；先创建再删除（数据安全）。</summary>
    private void MoveLocalisationKey(string lang, string oldKey, string newKey)
    {
        string? file = null;
        var idx = _adapter.GetLocalisationKeyFiles(lang);
        if (idx.TryGetValue(oldKey, out var f) && f != null)
            file = f;
        string? logical = _adapter.GetLocalisedLogicalText(oldKey, lang);
        string? display = _adapter.GetLocalisedText(oldKey, lang);
        if (logical != null || display != null)
        {
            string value = logical ?? display ?? string.Empty;
            // 先创建新 key（原位置或约定文件），后删除旧 key
            string target = file ?? StyleLocalisationFile(lang);
            _adapter.AddLocalisationEntry(lang, target, newKey, value);
            _adapter.ExpandLocalisationKey(lang, newKey);
            MarkLocalisationDirty(lang, target);
            if (file != null)
            {
                _adapter.RemoveLocalisationEntry(lang, file, oldKey);
                MarkLocalisationDirty(lang, file);
            }
        }
    }

    /// <summary>
    /// 注册/更新一个"占位样式"（合法样式条目，仅操作样式表）。
    /// 供 GalaxyMapEngine 注册静态地图的伪样式使用（规范 GalaxyMap 2.6 / 4.5）：
    ///   - 仅新增或更新样式表条目（GalaxyStyleTable），
    ///   - **严禁**触碰本地化条目、本地配置或刷新本地化缓存，
    ///   - 预览/图标/本地化由静态侧（GalaxyMap）全权负责。
    /// </summary>
    public void RegisterPlaceholderStyle(string name, GalaxyShapeParameters parameters)
    {
        lock (_syncRoot)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("样式名不能为空", nameof(name));
            if (parameters == null)
                throw new ArgumentNullException(nameof(parameters));

            // 已存在则先移除，再添加（覆盖式注册）
            _table.DeleteStyle(name);
            _table.AddStyle(new GalaxyStyleDefinition(name, parameters));
            _logger.LogDebug("注册占位样式: {Name}", name);
        }
    }

    /// <summary>
    /// 移除一个占位样式（仅操作样式表，不触碰本地化/配置）。
    /// 供 GalaxyMapEngine 在删除静态地图时移除对应伪样式。
    /// </summary>
    public bool UnregisterPlaceholderStyle(string name)
    {
        lock (_syncRoot)
        {
            return _table.DeleteStyle(name);
        }
    }

    // =========================================================================
    // 几何查询接口
    // =========================================================================
    public IReadOnlyList<IReadOnlyList<Vector2>> GetShapePolygons(
        string styleName,
        float endRadius = 450.0f,
        float step = 5.0f,
        int dirSign = 1)
    {
        lock (_syncRoot)
        {
            var def = _table.GetStyle(styleName);
            if (def == null)
            {
                _logger.LogWarning("样式 '{StyleName}' 不存在，无法获取多边形", styleName);
                return new List<IReadOnlyList<Vector2>>();
            }
            return GetShapePolygonsWithParameters(def.Parameters, endRadius, step, dirSign);
        }
    }

    public IReadOnlyList<IReadOnlyList<Vector2>> GetShapePolygonsWithParameters(
        GalaxyShapeParameters parameters,
        float endRadius = 450.0f,
        float step = 5.0f,
        int dirSign = 1)
    {
        var result = new List<IReadOnlyList<Vector2>>();

        if (parameters.NumArms > 0)
        {
            float r0 = (float)(parameters.CoreRadiusPerc * endRadius);
            var armPolys = GalaxyPointGenerator.GetArmPolygons(
                parameters.NumArms,
                r0,
                endRadius,
                (float)parameters.Tightness,
                dirSign,
                (float)parameters.WidthDeg,
                (float)parameters.ArmAngleDeg,
                step
            );
            foreach (var poly in armPolys)
                result.Add(poly);
        }

        if (parameters.HasRing)
        {
            var ringPoly = GalaxyPointGenerator.GetRingPolygon(
                (float)parameters.RingWidth,
                (float)parameters.RingOffset,
                endRadius,
                step
            );
            if (ringPoly.Count > 0)
                result.Add(ringPoly);
        }

        if (parameters.NumArms == 0 && !parameters.HasRing)
        {
            float r0 = (float)(parameters.CoreRadiusPerc * endRadius);
            if (r0 < endRadius)
            {
                var diskPoly = GalaxyPointGenerator.GetDiskPolygon(
                    r0,
                    endRadius,
                    step
                );
                if (diskPoly.Count > 0)
                    result.Add(diskPoly);
            }
        }

        return result;
    }

    // =========================================================================
    // 本地配置集成（规范第十一章）
    // =========================================================================

    /// <summary>
    /// 样式独立开关（规范 11.2）：preview / icon / normalize。
    /// </summary>
    public readonly record struct StyleSwitches(bool Preview, bool Icon, bool Normalize);

    /// <summary>
    /// 保存流程中解析出的本地配置快照（规范 11.4 步骤 6 的临时变量）。
    /// Available 为 false 时全部回退硬编码默认值。
    /// </summary>
    private sealed class LocalConfigSnapshot
    {
        public bool Available;
        public PreviewOptions PreviewOptions = PreviewOptions.Default;
        public IconOptions IconOptions = IconOptions.Default;
        public bool FallbackPreview;
        public bool FallbackIcon;
        public bool FallbackNormalize;
        public bool SyncOnSave;
        public Dictionary<string, StyleSwitches> StyleSwitches = new(StringComparer.Ordinal);

        /// <summary>
        /// 解析样式的有效开关：styles.{name} 优先，缺失用 fallback 值（规范 4.3 步骤 3）。
        /// </summary>
        public StyleSwitches GetEffectiveSwitches(string styleName)
        {
            if (StyleSwitches.TryGetValue(styleName, out var sw))
                return sw;
            return new StyleSwitches(FallbackPreview, FallbackIcon, FallbackNormalize);
        }
    }

    /// <summary>
    /// 读取并解析本地配置 galaxy.json（规范 11.4）。
    /// 读取或解析失败时返回 Available=false 的快照（硬编码降级），记录 Error 日志。
    /// </summary>
    private LocalConfigSnapshot LoadLocalConfigSnapshot()
    {
        var snapshot = new LocalConfigSnapshot { Available = false };
        if (_configManager == null)
            return snapshot;

        IReadOnlyDictionary<string, object>? all;
        try
        {
            all = _configManager.GetAll("galaxy");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取本地配置 galaxy.json 失败，降级为硬编码默认值");
            return snapshot;
        }

        if (all == null)
        {
            _logger.LogDebug("本地配置 galaxy.json 不存在，使用硬编码默认值");
            return snapshot;
        }

        try
        {
            // global 节点
            var global = GetObject(all, "global");
            if (global != null)
            {
                var previewObj = GetObject(global, "preview");
                if (previewObj != null)
                    snapshot.PreviewOptions = ApplyPreviewConfig(PreviewOptions.Default, previewObj);

                var iconObj = GetObject(global, "icon");
                if (iconObj != null)
                    snapshot.IconOptions = ApplyIconConfig(IconOptions.Default, iconObj);

                var behaviorObj = GetObject(global, "behavior");
                if (behaviorObj != null)
                {
                    // 默认导出开关（未设置时）为 true（用户要求"默认导出全部 True"），
                    // normalize 默认 false；可经 global.behavior.fallback_* 显式覆盖。
                    snapshot.FallbackPreview = GetBool(behaviorObj, "fallback_preview", true);
                    snapshot.FallbackIcon = GetBool(behaviorObj, "fallback_icon", true);
                    snapshot.FallbackNormalize = GetBool(behaviorObj, "fallback_normalize", true); // 规整化默认支持
                    snapshot.SyncOnSave = GetBool(behaviorObj, "sync_on_save", false);
                }
            }

            // styles 节点
            var styles = GetObject(all, "styles");
            if (styles != null)
            {
                foreach (var kv in styles)
                {
                    if (kv.Value is not JsonObject styleObj)
                        continue;
                    bool preview = GetBool(styleObj, "preview", snapshot.FallbackPreview);
                    bool icon = GetBool(styleObj, "icon", snapshot.FallbackIcon);
                    bool normalize = GetBool(styleObj, "normalize", snapshot.FallbackNormalize);
                    snapshot.StyleSwitches[kv.Key] = new StyleSwitches(preview, icon, normalize);
                }
            }

            snapshot.Available = true;
            _logger.LogDebug("本地配置解析完成：{StyleCount} 个样式开关、sync_on_save={Sync}",
                snapshot.StyleSwitches.Count, snapshot.SyncOnSave);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "解析本地配置 galaxy.json 失败，降级为硬编码默认值");
            return new LocalConfigSnapshot { Available = false };
        }

        return snapshot;
    }

    // ===== 本地配置解析辅助 =====

    private static JsonObject? GetObject(IReadOnlyDictionary<string, object> dict, string key)
    {
        if (!dict.TryGetValue(key, out var val))
            return null;
        return val as JsonObject;
    }

    private static JsonObject? GetObject(JsonObject obj, string key)
    {
        if (!obj.TryGetPropertyValue(key, out var val) || val is not JsonObject child)
            return null;
        return child;
    }

    private static bool GetBool(JsonObject obj, string key, bool fallback)
    {
        if (obj.TryGetPropertyValue(key, out var node) && node is JsonValue v && v.TryGetValue<bool>(out bool b))
            return b;
        return fallback;
    }

    private static int? GetInt(JsonObject obj, string key)
    {
        if (obj.TryGetPropertyValue(key, out var node) && node is JsonValue v && v.TryGetValue<int>(out int val))
            return val;
        return null;
    }

    private static double? GetDouble(JsonObject obj, string key)
    {
        if (!obj.TryGetPropertyValue(key, out var node) || node is not JsonValue v)
            return null;
        if (v.TryGetValue<double>(out double d)) return d;
        if (v.TryGetValue<int>(out int i)) return i;
        return null;
    }

    /// <summary>解析 RGBA 颜色数组 [r, g, b, a]；无效时返回 null。</summary>
    private static byte[]? GetRgba(JsonObject obj, string key)
    {
        if (!obj.TryGetPropertyValue(key, out var node) || node is not JsonArray arr || arr.Count < 4)
            return null;
        var bytes = new byte[4];
        for (int i = 0; i < 4; i++)
        {
            if (arr[i] is not JsonValue v || !v.TryGetValue<int>(out int val))
                return null;
            bytes[i] = (byte)Math.Clamp(val, 0, 255);
        }
        return bytes;
    }

    /// <summary>
    /// 解析恒星预设（规范 2.3 / 用户确认格式）：
    /// "star_presets": { "name": { "color": [r,g,b,a], "glow_color": [r,g,b,a], "weight": n } }
    /// </summary>
    private static Dictionary<string, StarPreset>? GetStarPresets(JsonObject obj, string key)
    {
        if (!obj.TryGetPropertyValue(key, out var node) || node is not JsonObject presets)
            return null;

        var result = new Dictionary<string, StarPreset>(StringComparer.Ordinal);
        foreach (var kv in presets)
        {
            if (kv.Value is not JsonObject presetObj)
                continue;
            byte[]? color = GetRgba(presetObj, "color");
            if (color == null)
                continue;
            byte[]? glow = GetRgba(presetObj, "glow_color");
            int weight = GetInt(presetObj, "weight") ?? 0;
            result[kv.Key] = new StarPreset(
                color[0], color[1], color[2], color[3],
                glow?[0] ?? color[0], glow?[1] ?? color[1], glow?[2] ?? color[2], glow?[3] ?? color[3],
                weight);
        }
        return result.Count > 0 ? result : null;
    }

    /// <summary>将 global.preview 节点覆盖到硬编码默认值上（规范 4.3）。</summary>
    private static PreviewOptions ApplyPreviewConfig(PreviewOptions baseOpts, JsonObject obj)
    {
        var result = baseOpts.Merge(null); // 浅拷贝副本，避免污染默认实例
        if (GetInt(obj, "outer_width") is int ow) result.OuterWidth = ow;
        if (GetInt(obj, "outer_height") is int oh) result.OuterHeight = oh;
        if (GetInt(obj, "inner_width") is int iw) result.InnerWidth = iw;
        if (GetInt(obj, "inner_height") is int ih) result.InnerHeight = ih;
        if (GetRgba(obj, "background_color") is byte[] bg) result.BackgroundColor = bg;
        if (GetBool(obj, "glow_arms", result.GlowArms ?? false) is bool ga) result.GlowArms = ga;
        if (GetBool(obj, "glow_core", result.GlowCore ?? false) is bool gc) result.GlowCore = gc;
        if (GetRgba(obj, "core_color") is byte[] cc) result.CoreColor = cc;
        if (GetStarPresets(obj, "star_presets") is { } presets) result.StarPresets = presets;
        if (GetDouble(obj, "bg_star_density") is double bsd) result.BgStarDensity = bsd;
        if (GetDouble(obj, "fill_density") is double fd) result.FillDensity = fd;
        return result;
    }

    /// <summary>将 global.icon 节点覆盖到硬编码默认值上（规范 4.3）。</summary>
    private static IconOptions ApplyIconConfig(IconOptions baseOpts, JsonObject obj)
    {
        var result = baseOpts.Merge(null);
        if (GetInt(obj, "frame_width") is int fw) result.FrameWidth = fw;
        if (GetInt(obj, "frame_height") is int fh) result.FrameHeight = fh;
        if (GetInt(obj, "inner_width") is int iw) result.InnerWidth = iw;
        if (GetInt(obj, "inner_height") is int ih) result.InnerHeight = ih;
        if (GetInt(obj, "glow_radius") is int gr) result.GlowRadius = gr;
        if (GetRgba(obj, "normal_color") is byte[] nc) result.NormalColor = nc;
        if (GetRgba(obj, "highlight_color") is byte[] hc) result.HighlightColor = hc;
        if (GetRgba(obj, "pressed_color") is byte[] pc) result.PressedColor = pc;
        return result;
    }

    // =========================================================================
    // 样式表保存与导出（含本地化写入）
    // =========================================================================
    /// <summary>
    /// 保存全部样式（规范第十二章，签名 12.1）。
    /// </summary>
    /// <param name="useLocalConfig">是否启用本地配置（galaxy.json）驱动导出参数与样式独立开关。
    /// 为 false 或配置不可用时全部回退硬编码默认值（行为与旧版一致）。</param>
    /// <param name="autoBuildIcons">图标总开关。true=开启、false=关闭；
    /// null=跟随配置（useLocalConfig 生效时逐样式由 icon 开关决定，否则按硬编码默认 false，与旧版一致）。</param>
    /// <param name="autoBuildPreviews">预览总开关，语义同 autoBuildIcons。</param>
    public SaveResult SaveAllStyles(bool useLocalConfig = false, bool? autoBuildIcons = null, bool? autoBuildPreviews = null)
    {
        var result = new SaveResult();
        lock (_syncRoot)
        {
            SetTask(EngineTaskType.SavingStyles);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                _logger.LogInformation("保存样式表 (useLocalConfig: {UseLocalConfig}, autoBuildIcons: {Icons}, autoBuildPreviews: {Previews})",
                    useLocalConfig, autoBuildIcons, autoBuildPreviews);

                // 步骤 1：本地配置读取（规范 11.4）
                LocalConfigSnapshot snapshot = useLocalConfig && _configManager != null
                    ? LoadLocalConfigSnapshot()
                    : new LocalConfigSnapshot { Available = false };

                // 步骤 2：规整化（仅内存，不落盘——保存由用户显式触发）
                _logger.LogInformation("[保存计时] 步骤1 配置读取: {Ms}ms", sw.ElapsedMilliseconds);
                //   待保存文件集 = 修改登记的文件（dirty，引擎本地化 API 修改时登记）+ 规整化迁移涉及文件。
                //   没修改的本地化文件不写（用户要求：保存只写被修改过的）。
                var keyFiles = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
                foreach (var lang in _localisationCache.Keys)
                    keyFiles[lang] = _adapter.GetLocalisationKeyFiles(lang);

                // 按样式 normalize 开关执行迁移（配置不可用时保持旧行为：不自动规整化）
                foreach (string name in _table.GetAllNames())
                {
                    if (snapshot.Available && !snapshot.GetEffectiveSwitches(name).Normalize)
                        continue;
                    try
                    {
                        NormalizeKeysCore(name, refreshCache: false, keyFiles);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "样式规范化失败: {Style}", name);
                        result.Errors.Add($"规范化失败: {name}");
                    }
                }
                RefreshLocalisationCache();

                // 步骤 2b：gfx 精灵表**写回涉及文件**收集（不做位置规整化——规整化归"全部规整化"按钮，
                _logger.LogInformation("[保存计时] 步骤2 规整化: {Ms}ms", sw.ElapsedMilliseconds);
                // 保存默认只保存自身配置 + 图像；gfx 位置迁移由 NormalizeSpriteFiles 单独执行，内存迁移后
                // 保存时此处收集迁移后的新位置文件一并写回）。
                var gfxPending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    gfxPending = _spriteEngine.GetGalaxySpriteFiles();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "gfx 涉及文件收集失败（继续保存）");
                }

                // 步骤 3：计算本次将写入的内容哈希（含规整化修正），并与写盘前的样式块基线比较
                _logger.LogInformation("[保存计时] 步骤2b gfx 收集: {Ms}ms", sw.ElapsedMilliseconds);
                var entries = _table.BuildAllStyleBlocks();
                string hash1Content = _adapter.SerializeNodes(entries);
                byte[] bytes = Encoding.UTF8.GetBytes(hash1Content);
                string hash1 = Convert.ToHexString(SHA256.HashData(bytes));

                var beforeResult = _adapter.GetConfig(ConfigPath);
                // 逐样式比较：内存块 vs 磁盘该样式块 → 只导出参数被修改的样式（增量渲染）
                var diskBlocks = new Dictionary<string, string>(StringComparer.Ordinal);
                if (beforeResult != null)
                {
                    foreach (var node in beforeResult.RootNodes)
                        if (node.Type == NodeType.Block && node.Key != null)
                            diskBlocks[node.Key] = _adapter.SerializeNodes(new List<AstNode> { node });
                }
                var changedStyles = new List<string>();
                foreach (var name in _table.GetAllNames())
                {
                    var def = _table.GetStyle(name);
                    if (def == null)
                        continue;
                    string mem = _adapter.SerializeNodes(new List<AstNode> { _table.BuildStyleBlock(name, def.Parameters) });
                    if (!string.Equals(mem, diskBlocks.GetValueOrDefault(name), StringComparison.Ordinal))
                        changedStyles.Add(name);
                }
                bool contentChanged = changedStyles.Count > 0;

                // 步骤 4：将内存样式表（含规整化修正的图标/descKey）写回 galaxy_shapes.txt（原子写入）
                _logger.LogInformation("[保存计时] 步骤3 哈希: {Ms}ms", sw.ElapsedMilliseconds);
                try
                {
                    _table.SaveToAdapter();
                }
                catch (Exception ex)
                {
                    result.WriteSuccess = false;
                    result.Errors.Add($"写入 galaxy_shapes.txt 失败: {ex.Message}");
                    return result;
                }
                result.WriteSuccess = true;

                if (!contentChanged)
                {
                    _logger.LogInformation("文件内容无变化，跳过导出（步骤 3）");
                }
                else
                {
                    _logger.LogInformation("文件已变化，开始按样式开关导出...");

                    // 步骤 4：按全局总开关与样式独立开关导出（规范 11.5）
                    bool previewMaster = autoBuildPreviews ?? snapshot.Available;
                    bool iconMaster = autoBuildIcons ?? snapshot.Available;

                    if (previewMaster && changedStyles.Count > 0)
                    {
                        SetTask(EngineTaskType.ExportingAll, "Previews");
                        foreach (string name in changedStyles)
                        {
                            // 样式独立开关仅在「未显式指定总开关 + 配置可用」时生效（规范 11.5）；
                            // 显式 true/false 为强制覆盖（旧行为：true 导出所有）
                            if (autoBuildPreviews == null && snapshot.Available && !snapshot.GetEffectiveSwitches(name).Preview)
                                continue;
                            var st = ExportSinglePreview(name, snapshot.PreviewOptions);
                            if (st == OperationStatus.Success)
                                result.PreviewSuccessCount++;
                            else
                            {
                                result.PreviewFailedCount++;
                                result.Errors.Add($"预览导出失败: {name}");
                            }
                        }
                    }

                    if (iconMaster && changedStyles.Count > 0)
                    {
                        SetTask(EngineTaskType.ExportingAll, "Icons");
                        foreach (string name in changedStyles)
                        {
                            if (autoBuildIcons == null && snapshot.Available && !snapshot.GetEffectiveSwitches(name).Icon)
                                continue;
                            var st = ExportSingleIcon(name, snapshot.IconOptions);
                            if (st == OperationStatus.Success)
                                result.IconSuccessCount++;
                            else
                            {
                                result.IconFailedCount++;
                                result.Errors.Add($"图标导出失败: {name}");
                            }
                        }
                    }

                }

                // 步骤 6：配置回写——保存时总是把内存中的相关设置（样式开关 + 导出参数）
                _logger.LogInformation("[保存计时] 步骤5 导出: {Ms}ms", sw.ElapsedMilliseconds);
                // 同步到银河类别 galaxy.json（用户要求：设置归位银河类别，保存即同步）。
                SyncToLocalConfigInternal(snapshot);

                // 步骤 7：本地化写入——只写"待保存文件集"（O(涉及文件)，不重写全部文件，
                _logger.LogInformation("[保存计时] 步骤6 配置回写: {Ms}ms", sw.ElapsedMilliseconds);
                //   不触碰游戏本体等外部 root 的文件）。
                // 步骤 7：本地化写入——只写启用语言的涉及文件（GetEnabledLanguages；未设置 = 全部）
                var enabledLangs7 = GetEnabledLanguages();
                System.Collections.Generic.IEnumerable<string> pendingLoc = _dirtyLocalisationFiles;
                if (enabledLangs7.Count > 0)
                {
                    var langSet = new HashSet<string>(enabledLangs7, StringComparer.Ordinal);
                    pendingLoc = _dirtyLocalisationFiles.Where(f =>
                    {
                        int sep = f.IndexOf('\0');
                        return sep > 0 && langSet.Contains(f[..sep]);
                    });
                }
                var pendingLocList = pendingLoc.ToList();
                var (locAllSuccess, locErrors) = WritePendingLocalisations(new HashSet<string>(pendingLocList, StringComparer.Ordinal));
                result.LocalisationSuccess = locAllSuccess;
                result.LocalisationErrors = locErrors;
                if (!locAllSuccess) result.Errors.AddRange(locErrors);
                // 已写盘：清空"修改登记"（下次保存只写新修改的）
                _dirtyLocalisationFiles.Clear();

                // 步骤 8：gfx 精灵表写回（只写本 mod 涉及文件，含规整化迁移后的待清理文件）
                _logger.LogInformation("[保存计时] 步骤7 本地化写入: {Ms}ms", sw.ElapsedMilliseconds);
                try
                {
                    if (!_spriteEngine.WriteAllSpriteDefinitions(gfxPending))
                        result.Errors.Add("gfx 精灵表写入失败");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "gfx 精灵表写回失败");
                    result.Errors.Add($"gfx 精灵表写回失败: {ex.Message}");
                }

                _logger.LogInformation("[保存计时] 步骤8 gfx 写回 + 总耗时: {Ms}ms", sw.ElapsedMilliseconds);
                return result;
            }
            catch (Exception ex)
            {
                result.Errors.Add(ex.Message);
                return result;
            }
            finally
            {
                SetTask(EngineTaskType.Idle);
            }
        }
    }

    /// <summary>
    /// 手动将当前所有样式的有效开关状态同步回本地配置（规范 11.6）。
    /// 不依赖 sync_on_save 开关；仅更新 styles 节点，保留 global 节点不变。
    /// 回写失败仅记录 Error 日志，不影响调用方。
    /// </summary>
    public void SyncToLocalConfig()
    {
        lock (_syncRoot)
        {
            var snapshot = LoadLocalConfigSnapshot();
            SyncToLocalConfigInternal(snapshot);
        }
    }

    /// <summary>
    /// 配置回写实现：以扁平键 SetBatch("galaxy", ...) 写入。
    /// 同步内容：每个样式的 preview/icon/normalize 开关 + 全局导出参数
    /// （global.preview.* / global.icon.*）。保存时由 SaveAllStyles 无条件调用，
    /// 保证内存中的相关设置归位到银河类别（galaxy.json）。
    /// 前置条件：调用方已持有 _syncRoot（规范 11.6）。
    /// </summary>
    private void SyncToLocalConfigInternal(LocalConfigSnapshot snapshot)
    {
        if (_configManager == null)
        {
            _logger.LogWarning("本地配置管理器未注入，无法同步配置");
            return;
        }

        var syncData = new Dictionary<string, object>();
        // 样式顺序（拖拽排序——持久化，重启/重载后恢复）
        syncData["style_order"] = _table.GetAllNames().ToList();
        foreach (string name in _table.GetAllNames())
        {
            // 实际开关优先（用户经 UI/SetStyleSwitch 写入 galaxy.json 的值）；
            // 未设置时用 snapshot 的 fallback（配置可用时）或默认 false。
            // 不能依赖 snapshot.GetEffectiveSwitches——useLocalConfig=false 时
            // snapshot.Available=false，其返回值恒 false，会把用户勾选的导出覆盖。
            syncData[$"styles.{name}.preview"] = ReadEffectiveSwitch(name, "preview", snapshot);
            syncData[$"styles.{name}.icon"] = ReadEffectiveSwitch(name, "icon", snapshot);
            syncData[$"styles.{name}.normalize"] = ReadEffectiveSwitch(name, "normalize", snapshot);
        }

        // 全局导出参数（内存实际值 → 银河类别 global.preview / global.icon）
        SerializePreviewOptions(snapshot.PreviewOptions, "global.preview.", syncData);
        SerializeIconOptions(snapshot.IconOptions, "global.icon.", syncData);

        if (syncData.Count == 0)
            return;

        try
        {
            _configManager.SetBatch("galaxy", syncData);
            _logger.LogInformation("本地配置已同步：{Count} 个样式开关 + 导出参数", _table.GetAllNames().Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "本地配置回写失败（不影响导出结果）");
        }
    }

    /// <summary>
    /// 读取样式的有效开关：优先 galaxy.json 中的实际值（GetStyleSwitch，用户勾选写入），
    /// 未设置时用 snapshot 的 fallback（配置可用时，preview/icon 默认 true），
    /// 配置不可用时：preview/icon 默认 true（用户要求"默认导出全部 True"）、normalize 默认 false。
    /// </summary>
    private bool ReadEffectiveSwitch(string styleName, string kind, LocalConfigSnapshot snapshot)
    {
        bool? actual = GetStyleSwitch(styleName, kind);
        if (actual.HasValue)
            return actual.Value;
        if (snapshot.Available)
        {
            var sw = snapshot.GetEffectiveSwitches(styleName);
            return kind switch
            {
                "preview" => sw.Preview,
                "icon" => sw.Icon,
                _ => sw.Normalize
            };
        }
        return true; // preview/icon/normalize 均默认 true（规整化默认支持）
    }

    /// <summary>把 PreviewOptions 序列化为扁平键（前缀 + snake_case 字段），star_presets 跳过（保留已有）。</summary>
    private static void SerializePreviewOptions(PreviewOptions opt, string prefix, Dictionary<string, object> syncData)
    {
        if (opt.OuterWidth.HasValue) syncData[prefix + "outer_width"] = opt.OuterWidth.Value;
        if (opt.OuterHeight.HasValue) syncData[prefix + "outer_height"] = opt.OuterHeight.Value;
        if (opt.InnerWidth.HasValue) syncData[prefix + "inner_width"] = opt.InnerWidth.Value;
        if (opt.InnerHeight.HasValue) syncData[prefix + "inner_height"] = opt.InnerHeight.Value;
        if (opt.BackgroundColor != null) syncData[prefix + "background_color"] = ToIntArray(opt.BackgroundColor);
        if (opt.GlowArms.HasValue) syncData[prefix + "glow_arms"] = opt.GlowArms.Value;
        if (opt.GlowCore.HasValue) syncData[prefix + "glow_core"] = opt.GlowCore.Value;
        if (opt.CoreColor != null) syncData[prefix + "core_color"] = ToIntArray(opt.CoreColor);
        if (opt.BgStarDensity.HasValue) syncData[prefix + "bg_star_density"] = opt.BgStarDensity.Value;
        if (opt.FillDensity.HasValue) syncData[prefix + "fill_density"] = opt.FillDensity.Value;
    }

    /// <summary>把 IconOptions 序列化为扁平键（前缀 + snake_case 字段）。</summary>
    private static void SerializeIconOptions(IconOptions opt, string prefix, Dictionary<string, object> syncData)
    {
        if (opt.FrameWidth.HasValue) syncData[prefix + "frame_width"] = opt.FrameWidth.Value;
        if (opt.FrameHeight.HasValue) syncData[prefix + "frame_height"] = opt.FrameHeight.Value;
        if (opt.InnerWidth.HasValue) syncData[prefix + "inner_width"] = opt.InnerWidth.Value;
        if (opt.InnerHeight.HasValue) syncData[prefix + "inner_height"] = opt.InnerHeight.Value;
        if (opt.GlowRadius.HasValue) syncData[prefix + "glow_radius"] = opt.GlowRadius.Value;
        if (opt.NormalColor != null) syncData[prefix + "normal_color"] = ToIntArray(opt.NormalColor);
        if (opt.HighlightColor != null) syncData[prefix + "highlight_color"] = ToIntArray(opt.HighlightColor);
        if (opt.PressedColor != null) syncData[prefix + "pressed_color"] = ToIntArray(opt.PressedColor);
    }

    private static int[] ToIntArray(byte[] bytes)
    {
        var arr = new int[bytes.Length];
        for (int i = 0; i < bytes.Length; i++) arr[i] = bytes[i];
        return arr;
    }

    /// <summary>
    /// 仅序列化指定样式块（用于哈希比较，排除 spriteTypes 等保留块）。
    /// </summary>
    private static string SerializeStyleBlocksOnly(List<AstNode> rootNodes, List<string> styleKeys)
    {
        var keys = new HashSet<string>(styleKeys, StringComparer.Ordinal);
        var filtered = rootNodes
            .Where(n => n.Type == NodeType.Block && n.Key != null && keys.Contains(n.Key))
            .ToList();
        return SerializationHelper.Serialize(filtered);
    }

    private (bool AllSuccess, List<string> Errors) WriteAllLocalisations()
    {
        // 本地化写入统一走底层 SA 的标准保存流程（收集新旧文件 → 逐个写 CurrentPath 键值对）。
        // 顶层引擎不直接收集/写文件，只告知 SA 执行。
        var (allSuccess, errors) = _adapter.WriteAllLocalisations();
        if (!allSuccess)
        {
            foreach (var err in errors)
                _logger.LogError(err);
        }
        return (allSuccess, errors);
    }

    /// <summary>
    /// 收集"待保存本地化文件集"：所有样式相关键（样式名 + desc 键）的
    ///   - 当前所在文件（CurrentPath，属于本 mod）——保证编辑内容落盘；
    ///   - 迁移来源文件（OldPath，属于本 mod）——保证磁盘旧文件被清理（写剩余/空头）。
    /// 绝不触碰游戏本体等外部 root 的文件（"lang\0相对路径"）。
    /// </summary>

    private OperationStatus SetError(OperationStatus status, string msg)
    {
        _logger.LogError(msg);
        return status;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}