// 文件: Stellaris.Engine/GalaxyMap/GalaxyMapEngine.cs
// 银河地图引擎：管理 map/setup_scenarios/ 下的动态与静态地图文件
// （规范 GalaxyMapSpecification REVISION 1.0）。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Stellaris.Engine.GalaxyStyle;
using Stellaris.Engine.ImageAsset;
using Stellaris.Engine.SpriteManagement;
using Stellaris.Parser;

namespace Stellaris.Engine.GalaxyMap;

/// <summary>地图引擎任务类型（供上层进度显示）。</summary>
public enum GalaxyMapTaskType
{
    Idle,
    LoadingScenarios,
    SavingScenarios,
    GeneratingLattice,
    GeneratingPointsFromImage,
    ExportingAssets
}

/// <summary>地图引擎任务变更事件参数（规范 Editor 4.4 进度汇报协议）。</summary>
public class GalaxyMapTaskChangedEventArgs : EventArgs
{
    public GalaxyMapTaskType TaskType { get; }
    public string? Argument { get; }

    public GalaxyMapTaskChangedEventArgs(GalaxyMapTaskType taskType, string? argument = null)
    {
        TaskType = taskType;
        Argument = argument;
    }
}

public sealed partial class GalaxyMapEngine : IDisposable
{
    private readonly StellarisAdapter _adapter;
    private readonly Stellaris.Engine.LocalConfigManager.IConfigManager? _configManager;

    /// <summary>
    /// 待保存文件表：所有地图落盘一律登记到本表（相对路径），不立即写盘；
    /// 统一保存功能（用户显式触发）时经 WritePendingFiles 一次性落盘后清空。
    /// </summary>
    private readonly HashSet<string> _pendingWriteFiles = new(StringComparer.Ordinal);

    /// <summary>地图相关本地化待保存文件表（语言 + 相对路径；统一保存时写盘）。</summary>
    private readonly HashSet<(string Lang, string RelPath)> _pendingLocalisations = new();

    /// <summary>待保存文件表（只读视图，统一保存时读取）。</summary>
    public IReadOnlyCollection<string> PendingWriteFiles => _pendingWriteFiles;

    /// <summary>是否有待保存的地图文件。</summary>
    public bool HasPendingWrites => _pendingWriteFiles.Count > 0 || _pendingLocalisations.Count > 0;

    /// <summary>静态地图名 → 占位样式名 映射（统一保存时写入银河类别 galaxy.json）。</summary>
    public IReadOnlyDictionary<string, string> StaticStyleMapping => _staticStyleMapping;
    private readonly GalaxyStyleEngine _styleEngine;
    private readonly ImageAssetEngine _imageEngine;
    private readonly SpriteManagementEngine _spriteEngine;
    private readonly ILogger _logger;
    private readonly object _syncRoot = new();
    private readonly string _modPrefix;

    // ---- 任务状态（进度汇报，规范 Editor 4.4）----
    private GalaxyMapTaskType _currentTask = GalaxyMapTaskType.Idle;
    private string? _taskArgument;

    public GalaxyMapTaskType CurrentTask => _currentTask;
    public string? TaskArgument => _taskArgument;

    /// <summary>任务变更事件：上层（如 UI）据此显示当前正在做什么。</summary>
    public event EventHandler<GalaxyMapTaskChangedEventArgs>? TaskChanged;

    private void SetTask(GalaxyMapTaskType task, string? argument = null)
    {
        if (_currentTask != task || !string.Equals(_taskArgument, argument, StringComparison.Ordinal))
        {
            _currentTask = task;
            _taskArgument = argument;
            TaskChanged?.Invoke(this, new GalaxyMapTaskChangedEventArgs(task, argument));
        }
    }

    /// <summary>地图文件根目录（相对路径，规范 1.3）。</summary>
    internal const string ScenarioDir = "map/setup_scenarios";

    // 内存场景表（Key = 文件名，不含扩展名）
    private readonly Dictionary<string, DynamicScenario> _dynamicScenarios = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StaticScenario> _staticScenarios = new(StringComparer.Ordinal);
    // 静态地图名 → 占位样式名 映射（保存时写银河类配置）
    private readonly Dictionary<string, string> _staticStyleMapping = new(StringComparer.Ordinal);
    /// <summary>形状总表顺序（全部样式，拖拽排序；未记录则回退样式表顺序）。</summary>
    private readonly Dictionary<string, List<string>> _shapeTableOrder = new(StringComparer.Ordinal);

    // 坐标精度（规范 2.3）
    private int _coordinatePrecision = 2;
    // ID 最小补零位数，0 表示按总点数自动计算（规范 4.2）
    private int _minIdPadding;

    private bool _disposed;

    public GalaxyMapEngine(StellarisAdapter adapter, GalaxyStyleEngine styleEngine,
        ImageAssetEngine imageEngine, SpriteManagementEngine spriteEngine,
        string modPrefix, ILogger? logger = null,
        Stellaris.Engine.LocalConfigManager.IConfigManager? configManager = null)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _styleEngine = styleEngine ?? throw new ArgumentNullException(nameof(styleEngine));
        _imageEngine = imageEngine ?? throw new ArgumentNullException(nameof(imageEngine));
        _spriteEngine = spriteEngine ?? throw new ArgumentNullException(nameof(spriteEngine));
        _modPrefix = modPrefix ?? throw new ArgumentNullException(nameof(modPrefix));
        _configManager = configManager;
        _logger = logger ?? NullLogger.Instance;
    }

    // ===== 配置 =====

    /// <summary>设置坐标精度（0~6，超出裁剪；内部始终 double，仅序列化格式化，规范 2.3）。</summary>
    public void SetCoordinatePrecision(int digits)
        => _coordinatePrecision = Math.Clamp(digits, 0, 6);

    public int CoordinatePrecision => _coordinatePrecision;

    /// <summary>设置 ID 最小补零位数（0 = 自动按总点数计算，规范 4.2）。</summary>
    public void SetIdPadding(int minDigits)
        => _minIdPadding = Math.Max(0, minDigits);

    public int MinIdPadding => _minIdPadding;

    // ===== 加载 / 重载 =====

    /// <summary>从 adapter 加载所有 map/setup_scenarios/ 下的场景文件。</summary>
    public void ScanAll()
    {
        lock (_syncRoot)
        {
            SetTask(GalaxyMapTaskType.LoadingScenarios);
            try
            {
                _dynamicScenarios.Clear();
                _staticScenarios.Clear();

                var files = _adapter.GetAllLoadedFiles()
                    .Where(kv => kv.Key.StartsWith(ScenarioDir + "/", StringComparison.OrdinalIgnoreCase)
                                 && kv.Key.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                    .Select(kv => kv.Key)
                    .ToList();

                foreach (var relPath in files)
                {
                    SetTask(GalaxyMapTaskType.LoadingScenarios, relPath);

                    var result = _adapter.GetConfig(relPath);
                    if (result == null || result.RootNodes == null)
                        continue;

                    foreach (var node in result.RootNodes)
                    {
                        if (node.Type != NodeType.Block || node.Key == null)
                            continue;

                        string fileName = Path.GetFileNameWithoutExtension(relPath);

                        if (node.Key == "setup_scenario")
                        {
                            var scenario = ScenarioParser.ParseDynamic(node, fileName);
                            if (scenario != null)
                                _dynamicScenarios[scenario.Name] = scenario;
                        }
                        else if (node.Key == "static_galaxy_scenario")
                        {
                            var scenario = ScenarioParser.ParseStatic(node, fileName);
                            if (scenario != null)
                            {
                                // 场景标识 = name 字段（规范 2.1 唯一标识）
                                // 注意：加载时【不】注册样式——样式必须来自磁盘真实文件（galaxy_shapes.txt），
                                // 不得凭空创建；静态地图的样式仅在创建时（AddStaticScenario）注册并持久化。
                                _staticScenarios[scenario.Name] = scenario;
                                _logger.LogInformation("静态场景加载: {Name}（系统 {Systems} 个、航道 {Lanes} 条、prevented {Prev}）",
                                    scenario.Name, scenario.Systems.Count, scenario.Hyperlanes.Count,
                                    scenario.PreventedHyperlanes.Count);
                            }
                        }
                    }
                }

                // 加载完成后重建"静态地图 → 样式"映射（同名样式已从磁盘加载，或绑定样式）
                RebuildStaticStyleMapping();

                _logger.LogInformation("加载地图场景完成：动态 {DynamicCount} 个、静态 {StaticCount} 个",
                    _dynamicScenarios.Count, _staticScenarios.Count);
            }
            finally
            {
                SetTask(GalaxyMapTaskType.Idle);
            }
        }
    }

    // ===== 查询 =====

    public IReadOnlyDictionary<string, DynamicScenario> DynamicScenarios => _dynamicScenarios;
    public IReadOnlyDictionary<string, StaticScenario> StaticScenarios => _staticScenarios;

    public DynamicScenario? GetDynamicScenario(string name)
        => _dynamicScenarios.GetValueOrDefault(name);

    public StaticScenario? GetStaticScenario(string name)
        => _staticScenarios.GetValueOrDefault(name);

    public List<string> GetAllScenarioNames()
        => _dynamicScenarios.Keys.Concat(_staticScenarios.Keys).Distinct().OrderBy(n => n).ToList();

    // ===== 保存 =====

    /// <summary>
    /// 保存指定动态场景到文件（原子写入）。静态场景请使用 SaveStaticScenario。
    /// </summary>
    public bool SaveDynamicScenario(string name)
    {
        lock (_syncRoot)
        {
            SetTask(GalaxyMapTaskType.SavingScenarios, name);
            try
            {
                if (!_dynamicScenarios.TryGetValue(name, out var scenario))
                    return false;

                // 按 UI 形状排序（标准排序）写 supports_shape：总表顺序（拖拽后记忆，未拖过用样式表顺序）
                // 过滤勾选集——否则保存用 SupportedShapes 的旧文件顺序，与 UI 显示不一致（乱序）。
                EnsureShapeOrderMatchesUi(name, scenario);

                string relPath = $"{ScenarioDir}/{name}.txt";
                var root = ScenarioSerializer.BuildDynamicRoot(scenario);

                _adapter.CreateEmptyFileInMemory(relPath, FileCategory.Config);
                var result = _adapter.GetConfig(relPath)!;
                result.RootNodes = new List<AstNode> { root };
                // 登记待保存文件表（不立即落盘；统一保存时经 WritePendingFiles 一次性写盘）
                _pendingWriteFiles.Add(relPath);
                _logger.LogInformation("动态场景已登记待保存: {Name}", name);
                return true;
            }
            finally
            {
                SetTask(GalaxyMapTaskType.Idle);
            }
        }
    }

    /// <summary>
    /// 保存指定静态场景到文件。保存前执行 ID 重编号（4.2）与坐标固化（4.4）。
    /// </summary>
    public bool SaveStaticScenario(string name)
    {
        lock (_syncRoot)
        {
            SetTask(GalaxyMapTaskType.SavingScenarios, name);
            try
            {
                if (!_staticScenarios.TryGetValue(name, out var scenario))
                    return false;

                // 4.2：ID 从 0 连续重编号，并同步更新航道引用
                RenumberIds(scenario);

                // **静态地图必须带 supports_shape**（游戏据此在新建游戏列表显示地图）：
                // 形状勾选为空时用绑定样式兜底（绑定样式 = 占位/用户绑定，galaxy_shapes 里必有）
                if (scenario.SupportedShapes.Count == 0)
                {
                    string? boundFallback = GetBoundStyle(name);
                    if (!string.IsNullOrEmpty(boundFallback))
                        scenario.SupportedShapes.Add(boundFallback);
                    else if (_styleEngine.GetStyle(name) != null)
                        scenario.SupportedShapes.Add(name);
                }

                string relPath = $"{ScenarioDir}/{name}.txt";
                var root = ScenarioSerializer.BuildStaticRoot(scenario, _coordinatePrecision);

                _adapter.CreateEmptyFileInMemory(relPath, FileCategory.Config);
                var result = _adapter.GetConfig(relPath)!;
                result.RootNodes = new List<AstNode> { root };
                // 登记待保存文件表（不立即落盘；统一保存时经 WritePendingFiles 一次性写盘）
                _pendingWriteFiles.Add(relPath);
                _logger.LogInformation("静态场景已登记待保存: {Name}（系统 {SystemCount} 个）", name, scenario.Systems.Count);
                return true;
            }
            finally
            {
                SetTask(GalaxyMapTaskType.Idle);
            }
        }
    }

    /// <summary>
    /// 一次性落盘全部待保存地图文件（统一保存功能——用户显式触发——时调用）。
    /// 先写地图本地化文件，再写场景文件；成功后清空两张文件表。
    /// </summary>
    public bool WritePendingFiles()
    {
        lock (_syncRoot)
        {
            bool allOk = true;
            string modRoot = _adapter.Roots.Count > 0 ? _adapter.Roots[^1] : null;
            // 地图本地化文件
            foreach (var (lang, relPath) in _pendingLocalisations.ToList())
            {
                SetTask(GalaxyMapTaskType.SavingScenarios, relPath);
                int idx = relPath.LastIndexOf('/');
                string fileName = idx >= 0 ? relPath[(idx + 1)..] : relPath;
                if (!_adapter.WriteLocalisation(lang, fileName, modRoot, writeIfEmpty: true))
                {
                    _logger.LogError("地图本地化待保存文件落盘失败: {RelPath}", relPath);
                    allOk = false;
                }
            }
            // 场景文件
            foreach (var relPath in _pendingWriteFiles.ToList())
            {
                SetTask(GalaxyMapTaskType.SavingScenarios, relPath);
                if (!_adapter.WriteFile(relPath))
                {
                    _logger.LogError("地图待保存文件落盘失败: {RelPath}", relPath);
                    allOk = false;
                }
            }
            SetTask(GalaxyMapTaskType.Idle);
            _pendingWriteFiles.Clear();
            _pendingLocalisations.Clear();
            return allOk;
        }
    }

    /// <summary>
    /// 统一保存全部地图（动态 + 静态）：登记全部场景文件与地图相关本地化文件到待保存表，然后一次性落盘。
    /// 必须由用户显式触发；文件名 = 地图 key（{ScenarioDir}/{name}.txt）。
    /// </summary>
    public bool SaveAllScenarios()
    {
        lock (_syncRoot)
        {
            // 清理重命名残留的旧场景文件（保存时处理旧文件）
            CleanupStaleScenarioFiles();

            // 清空文件：勾选该选项的地图，从场景文件中移除其 AST 节点（保存时写回即消失）
            RemoveClearedScenarioNodes();

            foreach (var name in _dynamicScenarios.Keys.ToList())
                SaveDynamicScenario(name);
            foreach (var name in _staticScenarios.Keys.ToList())
                SaveStaticScenario(name);
            // 静态地图同步创建的占位/绑定样式写回 galaxy_shapes.txt（与 SaveAllStyles 同一机制，不设特例）
            try
            {
                _styleEngine.WriteStyleTableToDisk();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "统一保存：样式表写回失败");
            }
            RegisterMapLocalisationPending();
            // 形状总表顺序写**模组**用户配置（galaxy.json maps.{name}.shape_order）——重启后恢复拖拽排序
            try
            {
                if (_configManager != null)
                {
                    foreach (var (mapName, order) in GetAllShapeTableOrders())
                        _configManager.Set("galaxy", $"maps.{mapName}.shape_order", order.ToArray());
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "统一保存：形状顺序写模组配置失败");
            }
            return WritePendingFiles();
        }
    }

    /// <summary>
    /// 地图本地化规整化（仅内存，不落盘；保存时随统一保存写盘）：
    ///   - 静态地图绑定样式键（{mapKey} / {mapKey}_desc，与占位/绑定样式同 key）→ style 文件（{prefix}_style_l_{lang}.yml）；
    ///   - 动态地图地图名（自建名字，无 desc）→ map 文件（{prefix}_map_l_{lang}.yml）；
    ///   - 静态地图恒星点名（自建）→ map 文件；
    ///   - 只处理本工具创建/引用的键，外部（游戏预设）键不动；迁移记录 OldPath → 保存时清理旧文件。
    /// </summary>
    public void NormalizeLocalisation()
    {
        lock (_syncRoot)
        {
            string modRoot = _adapter.Roots.Count > 0 ? _adapter.Roots[^1] : string.Empty;
            if (string.IsNullOrEmpty(modRoot))
                return;
            // 遵守启用语言设置（galaxy.json global.behavior.enabled_languages；未设置 = 全部）
            var enabledLangs = _styleEngine.GetEnabledLanguages();
            foreach (var lang in _adapter.GetAllLocalisations().Keys)
            {
                if (enabledLangs.Count > 0 && !enabledLangs.Contains(lang, StringComparer.Ordinal))
                    continue;
                string styleFile = _styleEngine.StyleLocalisationFile(lang);
                string mapFile = $"localisation/{lang}/{_modPrefix}_map_l_{lang}.yml";
                var index = _adapter.GetLocalisationKeyFiles(lang);
                if (index == null)
                    continue;
                // 1. 静态地图：绑定样式键（{mapKey}/{mapKey}_desc，与占位/绑定样式同 key）→ style 文件
                //    （锁定本地化的地图跳过——不动其本地化）
                foreach (var (name, s) in _staticScenarios)
                {
                    if (s.LockLocalisation)
                        continue;
                    foreach (var key in new[] { name, name + "_desc" })
                    {
                        if (index.TryGetValue(key, out var path) && !string.Equals(path, styleFile, StringComparison.Ordinal))
                        {
                            string? logical = _adapter.GetLocalisedLogicalText(key, lang);
                            if (logical != null)
                                MoveKey(lang, key, path, styleFile, logical, modRoot);
                        }
                    }
                }
                // 2. 动态地图：地图名（自建名字，无 desc）→ map 文件（锁定本地化的地图跳过）
                foreach (var (name, d) in _dynamicScenarios)
                {
                    if (d.LockLocalisation)
                        continue;
                    if (index.TryGetValue(name, out var path) && !string.Equals(path, mapFile, StringComparison.Ordinal))
                    {
                        string? logical = _adapter.GetLocalisedLogicalText(name, lang);
                        if (logical != null)
                            MoveKey(lang, name, path, mapFile, logical, modRoot);
                    }
                }
                // 3. 自建恒星点名 → map 文件（锁定本地化的静态地图跳过）
                foreach (var stat in _staticScenarios.Values)
                {
                    if (stat.LockLocalisation)
                        continue;
                    foreach (var sys in stat.Systems)
                    {
                        string key = sys.Name;
                        if (string.IsNullOrEmpty(key))
                            continue;
                        if (index.TryGetValue(key, out var path) && !string.Equals(path, mapFile, StringComparison.Ordinal))
                        {
                            string? logical = _adapter.GetLocalisedLogicalText(key, lang);
                            if (logical != null)
                                MoveKey(lang, key, path, mapFile, logical, modRoot);
                        }
                    }
                }
            }
            // 登记相关本地化文件到待保存表（保存时落盘）
            RegisterMapLocalisationPending();
        }
    }

    /// <summary>迁移本地化键：只写入新位置（记录 OldPath → 保存时清理旧文件），不先删旧键（数据安全：先创建再删除，旧键删除延迟到保存）。</summary>
    private void MoveKey(string lang, string key, string oldPath, string newPath, string logical, string modRoot)
    {
        _adapter.AddLocalisationEntry(lang, newPath, key, logical, modRoot, oldPath);
        // 旧文件登记待保存：键已迁走 → 保存时写回为空头（清理磁盘残留）
        _pendingLocalisations.Add((lang, oldPath));
    }

    /// <summary>登记地图相关本地化文件（地图名 / 地图名_desc / 静态恒星点名 键当前所在文件）到待保存表。</summary>
    private void RegisterMapLocalisationPending()
    {
        try
        {
            var allLangs = _adapter.GetAllLocalisations().Keys;
            // 遵守启用语言设置（未设置 = 全部）
            var enabledLangs = _styleEngine.GetEnabledLanguages();
            foreach (var name in _dynamicScenarios.Keys.Concat(_staticScenarios.Keys).Distinct(StringComparer.Ordinal))
            {
                // 清空文件的地图：AST 节点移除由 SaveAllScenarios 处理（不涉及本地化键）
                var dyn = _dynamicScenarios.GetValueOrDefault(name);
                var stat = _staticScenarios.GetValueOrDefault(name);
                bool clear = (dyn?.ClearFile ?? false) || (stat?.ClearFile ?? false);
                foreach (var lang in allLangs)
                {
                    if (enabledLangs.Count > 0 && !enabledLangs.Contains(lang, StringComparer.Ordinal))
                        continue;
                    var index = _adapter.GetLocalisationKeyFiles(lang);
                    if (index == null)
                        continue;
                    // 锁定本地化的地图：不登记其本地化文件（保存不动）
                    if ((dyn?.LockLocalisation ?? false) || (stat?.LockLocalisation ?? false))
                        continue;
                    // 动态地图无 desc：只登记地图名
                    if (index.TryGetValue(name, out var path) && !string.IsNullOrEmpty(path))
                        _pendingLocalisations.Add((lang, path));
                    string descKey = name + "_desc";
                    if (_staticScenarios.ContainsKey(name) && index.TryGetValue(descKey, out var dpath) && !string.IsNullOrEmpty(dpath))
                        _pendingLocalisations.Add((lang, dpath));
                    // 历史迁移残留：地图键的 OldPath 文件也登记（键已迁走 → 保存写空头清理）
                    var oldIdx = _adapter.GetLocalisationOldPathIndex(lang);
                    if (oldIdx != null)
                    {
                        foreach (var k in new[] { name, name + "_desc" })
                        {
                            if (oldIdx.TryGetValue(k, out var opath) && !string.IsNullOrEmpty(opath))
                                _pendingLocalisations.Add((lang, opath));
                        }
                    }
                }
            }
            // 静态地图恒星点名（自建键）所在文件（锁定本地化的跳过）
            foreach (var stat2 in _staticScenarios.Values)
            {
                if (stat2.LockLocalisation)
                    continue;
                foreach (var lang in allLangs)
                {
                    if (enabledLangs.Count > 0 && !enabledLangs.Contains(lang, StringComparer.Ordinal))
                        continue;
                    var index = _adapter.GetLocalisationKeyFiles(lang);
                    if (index == null)
                        continue;
                    foreach (var sys in stat2.Systems)
                    {
                        if (string.IsNullOrEmpty(sys.Name))
                            continue;
                        if (index.TryGetValue(sys.Name, out var path) && !string.IsNullOrEmpty(path))
                            _pendingLocalisations.Add((lang, path));
                    }
                }
            }
        }
        catch
        {
            // 登记失败不阻断场景文件保存
        }
    }

    // ===== CRUD =====

    /// <summary>
    /// 按 UI 形状总表顺序重排 SupportedShapes（勾选集）：总表顺序 = 拖拽记忆的顺序，
    /// 未拖过则回退样式表顺序；勾选集只保留仍然勾选的项，顺序与 UI 显示一致。
    /// </summary>
    private void EnsureShapeOrderMatchesUi(string mapName, DynamicScenario scenario)
    {
        var supportedSet = new HashSet<string>(scenario.SupportedShapes, StringComparer.Ordinal);
        var uiOrder = _shapeTableOrder.TryGetValue(mapName, out var o) && o.Count > 0
            ? o
            : (_styleEngine?.GetAllStyleNames() ?? new List<string>());
        scenario.SupportedShapes = uiOrder.Where(supportedSet.Contains).ToList();
    }

    /// <summary>新增或覆盖一个动态场景（内存），随后调用 SaveDynamicScenario 落盘。</summary>
    public void AddDynamicScenario(DynamicScenario scenario)
    {
        lock (_syncRoot)
        {
            if (scenario == null) throw new ArgumentNullException(nameof(scenario));
            if (string.IsNullOrEmpty(scenario.Name)) throw new ArgumentException("场景名不能为空", nameof(scenario));
            _dynamicScenarios[scenario.Name] = scenario.Clone();
        }
    }

    /// <summary>新增或覆盖一个静态场景（内存）。加载时同步注册伪样式（4.5）。</summary>
    public void AddStaticScenario(StaticScenario scenario)
    {
        lock (_syncRoot)
        {
            if (scenario == null) throw new ArgumentNullException(nameof(scenario));
            if (string.IsNullOrEmpty(scenario.Name)) throw new ArgumentException("场景名不能为空", nameof(scenario));

            _staticScenarios[scenario.Name] = scenario.Clone();
            RegisterPseudoStyleInternal(scenario.Name);
            // 新建静态地图时自动绑定与其同名的占位样式（BoundStyleName + 映射）
            SetBoundStyle(scenario.Name, scenario.Name);
        }
    }

    /// <summary>删除场景（内存 + 伪样式移除）。返回是否删除成功。</summary>
    public bool DeleteScenario(string name)
    {
        lock (_syncRoot)
        {
            bool removed = _dynamicScenarios.Remove(name) | _staticScenarios.Remove(name);
            if (removed)
                UnregisterPseudoStyleInternal(name);

            // 场景文件节点移除：从 setup_scenarios/{name}.txt 删除该地图的根节点块
            // （文件可能含多个根节点，只删对应块，不物理删文件）；文件不在任何 root → 跳过。
            string relPath = $"{ScenarioDir}/{name}.txt";
            var result = _adapter.GetConfig(relPath);
            if (result != null)
            {
                bool nodeRemoved = result.RootNodes.RemoveAll(n => IsScenarioBlock(n, name)) > 0;
                if (nodeRemoved)
                    _pendingWriteFiles.Add(relPath);
            }

            // 本地化键节点移除（各语言：地图名 + desc）；涉及文件登记待保存（写回清理）
            foreach (var lang in _adapter.GetAllLocalisations().Keys)
            {
                var idx = _adapter.GetLocalisationKeyFiles(lang);
                if (idx == null)
                    continue;
                foreach (var key in new[] { name, name + "_desc" })
                {
                    if (idx.TryGetValue(key, out var path) && !string.IsNullOrEmpty(path))
                    {
                        _adapter.RemoveLocalisationEntry(lang, path, key);
                        _pendingLocalisations.Add((lang, path));
                    }
                }
            }

            _staticStyleMapping.Remove(name);
            _shapeTableOrder.Remove(name);
            return removed;
        }
    }

    /// <summary>清空文件：把 ClearFile 标记的地图从场景文件（setup_scenarios/{key}.txt）移除其 AST 节点。</summary>
    private void RemoveClearedScenarioNodes()
    {
        try
        {
            foreach (var (name, d) in _dynamicScenarios)
            {
                if (d.ClearFile)
                    RemoveScenarioNodeFromFile(name);
            }
            foreach (var (name, s) in _staticScenarios)
            {
                if (s.ClearFile)
                    RemoveScenarioNodeFromFile(name);
            }
        }
        catch
        {
            // 失败不阻断保存
        }
    }

    /// <summary>从 setup_scenarios/{name}.txt 移除指定地图的场景块节点并登记写回（文件不在任何 root → 跳过）。</summary>
    private void RemoveScenarioNodeFromFile(string name)
    {
        string relPath = $"{ScenarioDir}/{name}.txt";
        var result = _adapter.GetConfig(relPath);
        if (result != null)
        {
            bool removed = result.RootNodes.RemoveAll(n => IsScenarioBlock(n, name)) > 0;
            if (removed)
                _pendingWriteFiles.Add(relPath);
        }
    }

    /// <summary>判断 AST 节点是否属于指定地图的场景块（static_galaxy_scenario / setup_scenario 且 name 字段匹配）。</summary>
    private static bool IsScenarioBlock(AstNode n, string mapName)
    {
        if (n.Type != NodeType.Block)
            return false;
        if (n.Key != "static_galaxy_scenario" && n.Key != "setup_scenario")
            return false;
        return n.Children != null && n.Children.Any(c =>
            c.Type == NodeType.Simple && c.Key == "name"
            && c.Value is string sv && sv == mapName);
    }

    /// <summary>
    /// 清理重命名残留的旧场景文件：mod 目录中，文件内场景块的 name 均不等于文件名
    /// （例如旧名 01.txt 里已是 name="01_fast"）→ 该文件是残留，移除全部场景块并登记写回
    /// （保存后残留消失）；外部 root 文件只读不处理。
    /// </summary>
    private void CleanupStaleScenarioFiles()
    {
        try
        {
            string modRoot = _adapter.Roots.Count > 0 ? _adapter.Roots[^1] : string.Empty;
            if (string.IsNullOrEmpty(modRoot))
                return;
            foreach (var relPath in _adapter.GetAllLoadedFiles().Keys)
            {
                if (!relPath.StartsWith(ScenarioDir + "/", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!relPath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!_adapter.GetFileRoot(relPath).Equals(modRoot, StringComparison.OrdinalIgnoreCase))
                    continue; // 外部 root 只读
                var result = _adapter.GetConfig(relPath);
                if (result == null)
                    continue;
                bool hasScenario = result.RootNodes.Any(n => n.Type == NodeType.Block
                    && (n.Key == "static_galaxy_scenario" || n.Key == "setup_scenario"));
                if (!hasScenario)
                    continue;
                string fileName = Path.GetFileNameWithoutExtension(relPath);
                bool hasMatching = result.RootNodes.Any(n => IsScenarioBlock(n, fileName));
                if (!hasMatching)
                {
                    // 残留：移除全部场景块（写回后只剩空头/注释）
                    result.RootNodes.RemoveAll(n => n.Type == NodeType.Block
                        && (n.Key == "static_galaxy_scenario" || n.Key == "setup_scenario"));
                    _pendingWriteFiles.Add(relPath);
                    _logger.LogInformation("清理重命名残留场景文件: {RelPath}", relPath);
                }
            }
        }
        catch
        {
            // 清理失败不阻断保存
        }
    }

    // ===== 样式排序（规范 3.2）=====

    /// <summary>获取动态场景的当前形状顺序（内存顺序）。</summary>

    public List<string> GetShapeOrder(string mapName)
    {
        lock (_syncRoot)
        {
            if (_dynamicScenarios.TryGetValue(mapName, out var d))
                return new List<string>(d.SupportedShapes);
            if (_staticScenarios.TryGetValue(mapName, out var s))
                return new List<string>(s.SupportedShapes);
            return new List<string>();
        }
    }

    /// <summary>记录形状总表顺序（全部样式，含未勾选；拖拽排序后调用，重建形状页时使用）。</summary>
    public void SetShapeTableOrder(string mapName, List<string> order)
    {
        lock (_syncRoot)
        {
            if (order == null)
                _shapeTableOrder.Remove(mapName);
            else
                _shapeTableOrder[mapName] = new List<string>(order);
        }
    }

    /// <summary>全部地图的形状总表顺序（供保存写入用户配置 galaxy.json maps.{name}.shape_order）。</summary>
    public IReadOnlyDictionary<string, List<string>> GetAllShapeTableOrders()
    {
        lock (_syncRoot)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var (name, order) in _shapeTableOrder)
                result[name] = new List<string>(order);
            return result;
        }
    }

    /// <summary>恢复地图形状总表顺序（启动时从 galaxy.json maps.{name}.shape_order 读取，由上层注入）。</summary>
    public void RestoreShapeTableOrder(IReadOnlyDictionary<string, List<string>>? orders)
    {
        lock (_syncRoot)
        {
            if (orders == null)
                return;
            foreach (var (name, order) in orders)
                if (order != null && order.Count > 0)
                    _shapeTableOrder[name] = new List<string>(order);
        }
    }

    /// <summary>形状总表顺序；无记录（未拖过）返回 null（调用方回退样式表顺序）。</summary>
    public List<string>? GetShapeTableOrder(string mapName)
    {
        lock (_syncRoot)
            return _shapeTableOrder.TryGetValue(mapName, out var o) ? new List<string>(o) : null;
    }

    /// <summary>设置形状顺序（内存顺序，保存时按此顺序落盘；动态/静态地图均支持）。</summary>
    public void SetShapeOrder(string mapName, List<string> order)
    {
        lock (_syncRoot)
        {
            var list = order == null ? new List<string>() : new List<string>(order);
            if (_dynamicScenarios.TryGetValue(mapName, out var d))
                d.SupportedShapes = list;
            else if (_staticScenarios.TryGetValue(mapName, out var s))
                s.SupportedShapes = list;
            else
                throw new KeyNotFoundException($"场景 '{mapName}' 不存在");
        }
    }

    /// <summary>
    /// 应用用户偏好顺序：与 SupportedShapes 取交集（按偏好顺序过滤），覆盖内存顺序。
    /// 严禁写入文件（除非 SyncOrderWithPreferred）。
    /// </summary>
    public void ApplyPreferredOrder(string mapName, List<string> preferredOrder)
    {
        lock (_syncRoot)
        {
            var s = _dynamicScenarios.GetValueOrDefault(mapName)
                    ?? throw new KeyNotFoundException($"动态地图 '{mapName}' 不存在");
            if (preferredOrder == null) return;

            var supported = new HashSet<string>(s.SupportedShapes, StringComparer.Ordinal);
            s.SupportedShapes = preferredOrder.Where(supported.Contains).ToList();
        }
    }

    /// <summary>一键排序：交集替换内存顺序并写入文件（覆盖文件顺序，规范 3.2c）。</summary>
    public void SyncOrderWithPreferred(string mapName, List<string> preferredOrder)
    {
        lock (_syncRoot)
        {
            ApplyPreferredOrder(mapName, preferredOrder);
            SaveDynamicScenario(mapName);
        }
    }

    // ===== 大致样式接口（规范 3.3 / 4.7）=====

    /// <summary>
    /// 估算动态地图容量：对每个支持的形状调用 GalaxyPointGenerator.ComputeAreas，
    /// 估算该地图半径下的最大恒星数（×0.8 安全系数）。
    /// </summary>
    public (double Radius, List<string> SupportedShapes, Dictionary<string, int> MaxStarsPerShape)
        GetEstimatedCapacity(string mapName)
    {
        lock (_syncRoot)
        {
            var s = _dynamicScenarios.GetValueOrDefault(mapName)
                    ?? throw new KeyNotFoundException($"动态地图 '{mapName}' 不存在");

            // 对全部样式算预估（不只 SupportedShapes）——形状总表未打钩的样式也要显示预估数
            var maxStars = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var shapeName in _styleEngine.GetAllStyleNames())
            {
                var def = _styleEngine.GetStyle(shapeName);
                if (def == null)
                    continue;

                var (total, _, _, _) = GalaxyStyle.GalaxyPointGenerator.ComputeAreas(def.Parameters, s.Radius, 5.0f);
                // 恒星容量按每个恒星占用 0.785×间距² 的面积计算（圆：π(d/2)² ≈ 0.785·d²，接近 πr²）
                double d = def.Parameters.StarsMinDist <= 0 ? 8.0 : def.Parameters.StarsMinDist;
                double cellArea = 0.785 * d * d;
                int stars = cellArea > 0 ? (int)(total / cellArea) : 0;
                maxStars[shapeName] = Math.Max(0, stars);
            }

            return (s.Radius, new List<string>(s.SupportedShapes), maxStars);
        }
    }

    /// <summary>
    /// 静态地图大致形状：基于伪样式参数调用 GalaxyStyleEngine.GetShapePolygonsWithParameters
    /// 生成边界多边形（双向联动，规范 4.7）。
    /// </summary>
    public IReadOnlyList<IReadOnlyList<System.Numerics.Vector2>> GetEstimatedShape(string mapName)
    {
        lock (_syncRoot)
        {
            var s = _staticScenarios.GetValueOrDefault(mapName)
                    ?? throw new KeyNotFoundException($"静态地图 '{mapName}' 不存在");

            var pseudo = BuildPseudoStyle(s);
            var shapeParams = PseudoStyleToGalaxyShapeParameters(pseudo, s);
            return _styleEngine.GetShapePolygonsWithParameters(shapeParams);
        }
    }

    // ===== 伪样式占位注册（规范 4.5）=====

    /// <summary>
    /// 重建"静态地图 → 样式"映射（加载后调用）：样式必须来自磁盘真实文件。
    /// 优先用场景 BoundStyleName（若有且样式存在），否则同名样式存在则映射到同名；
    /// 磁盘上没有的样式一律不建映射（不凭空创建）。
    /// </summary>
    private void RebuildStaticStyleMapping()
    {
        _staticStyleMapping.Clear();
        foreach (var (name, s) in _staticScenarios)
        {
            string? style = s.BoundStyleName;
            if (string.IsNullOrEmpty(style) && _styleEngine.GetStyle(name) != null)
                style = name; // 创建时注册的同名样式（已持久化到磁盘）
            if (!string.IsNullOrEmpty(style))
            {
                var def = _styleEngine.GetStyle(style);
                if (def != null)
                {
                    _staticStyleMapping[name] = style;
                    // **同步 BoundStyleName**（GetBoundStyle 读它——否则映射建了但工具仍显示"未绑定"）
                    s.BoundStyleName = style;
                    // core_radius 从样式文件读取（真实样式参数）
                    s.CoreRadiusPerc = def.Parameters.CoreRadiusPerc;
                }
            }
        }
    }

    /// <summary>
    /// 按"默认锁定本地化"预设名列表（galaxy.json 写死 huge/large/medium/small/tiny 等原版预设，
    /// 由上层注入）设置对应 key 的地图为锁定本地化。
    /// </summary>
    public void ApplyDefaultLockLocalisation(IEnumerable<string>? presetNames)
    {
        lock (_syncRoot)
        {
            if (presetNames == null)
                return;
            var set = new HashSet<string>(presetNames, StringComparer.Ordinal);
            foreach (var (name, d) in _dynamicScenarios)
            {
                if (set.Contains(name))
                    d.LockLocalisation = true;
            }
            foreach (var (name, s) in _staticScenarios)
            {
                if (set.Contains(name))
                    s.LockLocalisation = true;
            }
        }
    }

    /// <summary>恢复地图的"锁定本地化 / 清空文件"标志（从银河类别 galaxy.json 的 maps 节点读取，由上层注入）。</summary>
    public void RestoreMapFlags(IReadOnlyDictionary<string, (bool Lock, bool Clear)>? flags)
    {
        lock (_syncRoot)
        {
            if (flags == null)
                return;
            foreach (var (name, f) in flags)
            {
                if (_dynamicScenarios.TryGetValue(name, out var d))
                {
                    d.LockLocalisation = f.Lock;
                    d.ClearFile = f.Clear;
                }
                if (_staticScenarios.TryGetValue(name, out var s))
                {
                    s.LockLocalisation = f.Lock;
                    s.ClearFile = f.Clear;
                }
            }
        }
    }

    /// <summary>
    /// 恢复静态地图 → 样式映射（从银河类别 galaxy.json 的 maps 节点读取，由上层注入；
    /// 绑定样式必须真实存在，否则忽略）。core_radius 是样式参数，从样式文件读取——恢复绑定后
    /// 以绑定样式的 core_radius_perc 刷新 StaticScenario.CoreRadiusPerc。
    /// </summary>
    public void RestoreStaticStyleMapping(IReadOnlyDictionary<string, string>? mapping)
    {
        lock (_syncRoot)
        {
            if (mapping == null)
                return;
            foreach (var (mapName, styleName) in mapping)
            {
                if (_staticScenarios.ContainsKey(mapName))
                {
                    var def = _styleEngine.GetStyle(styleName);
                    if (def != null)
                    {
                        _staticScenarios[mapName].BoundStyleName = styleName;
                        _staticStyleMapping[mapName] = styleName;
                        // core_radius 从样式文件读取（真实样式参数）
                        _staticScenarios[mapName].CoreRadiusPerc = def.Parameters.CoreRadiusPerc;
                    }
                }
            }
        }
    }

    /// <summary>
    /// 绑定静态地图到已有样式：保存时该样式的图标/预览使用本图点集渲染。
    /// styleName 为空 = 解绑；映射（mapName → styleName）供统一保存写入银河类别 galaxy.json。
    /// </summary>
    public void SetBoundStyle(string mapName, string? styleName)
    {
        lock (_syncRoot)
        {
            if (!_staticScenarios.TryGetValue(mapName, out var scenario))
                return;
            scenario.BoundStyleName = string.IsNullOrWhiteSpace(styleName) ? null : styleName;
            if (string.IsNullOrWhiteSpace(styleName))
                _staticStyleMapping.Remove(mapName);
            else
                _staticStyleMapping[mapName] = styleName.Trim();
            _logger.LogDebug("静态地图绑定样式: {Map} → {Style}", mapName, styleName ?? "(无)");
        }
    }

    /// <summary>获取静态地图绑定的样式名（无绑定返回 null）。</summary>
    public string? GetBoundStyle(string mapName)
    {
        lock (_syncRoot)
        {
            return _staticScenarios.TryGetValue(mapName, out var s) ? s.BoundStyleName : null;
        }
    }

    /// <summary>
    /// 生成形状占位符（历史保留）：自动生成与地图同名的占位样式并记录映射。
    /// 新流程优先使用 SetBoundStyle（绑定已有样式）。
    /// </summary>
    public void GenerateShapePlaceholder(string mapName)
    {
        lock (_syncRoot)
        {
            RegisterPseudoStyleInternal(mapName);
        }
    }

    /// <summary>
    /// 重命名动态地图：更新内存字典 key + 本地化键迁移（{old} → {new}，各启用语言）。
    /// 列表位置不变（priority 不变）；地图键输入框失焦即改内存。
    /// </summary>
    public bool RenameDynamicScenario(string oldName, string newName)
    {
        lock (_syncRoot)
        {
            if (string.IsNullOrWhiteSpace(newName) || oldName == newName)
                return false;
            if (!_dynamicScenarios.TryGetValue(oldName, out var s) || _dynamicScenarios.ContainsKey(newName))
                return false;
            _dynamicScenarios.Remove(oldName);
            s.Name = newName;
            _dynamicScenarios[newName] = s;
            // 旧场景文件节点移除（保存时写回清理；文件不在任何 root → 跳过）
            string oldRelPath = $"{ScenarioDir}/{oldName}.txt";
            var oldResult = _adapter.GetConfig(oldRelPath);
            if (oldResult != null)
            {
                bool removed = oldResult.RootNodes.RemoveAll(n => IsScenarioBlock(n, oldName)) > 0;
                if (removed)
                    _pendingWriteFiles.Add(oldRelPath);
            }
            // 本地化键迁移：{old} → {new}（动态地图无 desc；各启用语言）——先创建再删除（数据安全）
            foreach (var lang in _adapter.GetAllLocalisations().Keys)
            {
                var idx = _adapter.GetLocalisationKeyFiles(lang);
                if (idx == null)
                    continue;
                if (idx.TryGetValue(oldName, out var path) && !string.IsNullOrEmpty(path))
                {
                    string? logical = _adapter.GetLocalisedLogicalText(oldName, lang);
                    if (logical != null)
                    {
                        // 新 key 写在旧 key 的【原位置】（相对路径不变；未规整化时 key 不迁移文件）
                        _adapter.AddLocalisationEntry(lang, path, newName, logical, _adapter.Roots[^1], path);
                        _pendingLocalisations.Add((lang, path));
                    }
                    // 先建后删：新 key 已创建，再移除旧 key
                    _adapter.RemoveLocalisationEntry(lang, path, oldName);
                }
            }
            return true;
        }
    }

    /// <summary>
    /// 重命名静态地图：更新静态字典 + 同步改占位样式 key（同名）+ 更新内存映射。
    /// 映射（静态地图名 → 占位样式名）供保存时写入银河类配置。
    /// </summary>
    public bool RenameStaticScenario(string oldName, string newName)
    {
        lock (_syncRoot)
        {
            if (string.IsNullOrWhiteSpace(newName) || oldName == newName)
                return false;
            if (!_staticScenarios.TryGetValue(oldName, out var s) || _staticScenarios.ContainsKey(newName))
                return false;
            _staticScenarios.Remove(oldName);
            s.Name = newName;
            _staticScenarios[newName] = s;
            // 旧场景文件节点移除（保存时写回清理；文件不在任何 root → 跳过）
            string oldRelPath = $"{ScenarioDir}/{oldName}.txt";
            var oldResult = _adapter.GetConfig(oldRelPath);
            if (oldResult != null)
            {
                bool removed = oldResult.RootNodes.RemoveAll(n => IsScenarioBlock(n, oldName)) > 0;
                if (removed)
                    _pendingWriteFiles.Add(oldRelPath);
            }
            // 占位样式同步改名（同名；未生成则 RenameStyle 返回 false 无害）
            try { _styleEngine.RenameStyle(oldName, newName); } catch { }
            // 绑定样式同步：若绑定的是同名（静态地图自己的样式），改名时跟随
            if (s.BoundStyleName == oldName)
                s.BoundStyleName = newName;
            // 内存映射（静态地图名 → 样式名，保存时写银河类配置）
            _staticStyleMapping.Remove(oldName);
            _staticStyleMapping[newName] = s.BoundStyleName ?? newName;
            return true;
        }
    }

    private void RegisterPseudoStyleInternal(string mapName)
    {
        if (!_staticScenarios.TryGetValue(mapName, out var scenario))
            return;

        var pseudo = BuildPseudoStyle(scenario);
        var shapeParams = PseudoStyleToGalaxyShapeParameters(pseudo, scenario);
        _styleEngine.RegisterPlaceholderStyle(mapName, shapeParams);
        // 静态地图生成的特色版本：默认三个开关都 false（不导出预览/按钮、不接受规整化）
        _styleEngine.SetStyleSwitch(mapName, "preview", false);
        _styleEngine.SetStyleSwitch(mapName, "icon", false);
        _styleEngine.SetStyleSwitch(mapName, "normalize", false);
        // 记录静态地图名 → 占位样式名 映射
        _staticStyleMapping[mapName] = mapName;
        _logger.LogDebug("注册伪样式占位: {Name}", mapName);
    }

    private void UnregisterPseudoStyleInternal(string mapName)
    {
        if (_styleEngine.UnregisterPlaceholderStyle(mapName))
            _logger.LogDebug("移除伪样式占位: {Name}", mapName);
    }

    /// <summary>构造伪样式对象（自动生成 PreviewIcon/ButtonIcon/DescKey）。</summary>
    internal PseudoStyle BuildPseudoStyle(StaticScenario scenario)
    {
        var pseudo = new PseudoStyle
        {
            Name = scenario.Name,
            CoreRadiusPerc = scenario.CoreRadiusPerc,
            // preview_icon / button_icon 标准格式为**精灵名**（须先在 .gfx 声明）
            PreviewIcon = $"GFX_galaxy_preview_{scenario.Name}", // 精灵名不带 modPrefix
            ButtonIcon = $"GFX_galaxy_button_{scenario.Name}",
            DescKey = $"{scenario.Name}_desc"
        };

        // 根据散点分布自动计算 stars_min_dist 与 num_stars_core_perc
        if (scenario.Systems.Count > 1)
        {
            var pts = scenario.Systems.Select(SamplePosition).ToList();
            // stars_min_dist：最近邻平均距离的保守估计
            double minDistSum = 0;
            int count = 0;
            foreach (var p in pts)
            {
                double best = double.MaxValue;
                foreach (var q in pts)
                {
                    if (ReferenceEquals(p, q)) continue;
                    double dx = p.X - q.X, dy = p.Y - q.Y;
                    double d = Math.Sqrt(dx * dx + dy * dy);
                    if (d < best) best = d;
                }
                if (best < double.MaxValue) { minDistSum += best; count++; }
            }
            pseudo.StarsMinDist = count > 0 ? Math.Max(1.0, minDistSum / count * 0.5) : 10.0;

            // num_stars_core_perc：核心半径（CoreRadiusPerc × 500）内点数占比
            double coreR = pseudo.CoreRadiusPerc * 500.0;
            int inCore = pts.Count(p => Math.Sqrt(p.X * p.X + p.Y * p.Y) <= coreR);
            pseudo.NumStarsCorePerc = (double)inCore / pts.Count;
        }
        else
        {
            pseudo.StarsMinDist = 10.0;
            pseudo.NumStarsCorePerc = 0.0;
        }

        return pseudo;
    }

    /// <summary>伪样式 → GalaxyShapeParameters（供 GalaxyStyleEngine 渲染/导出）。</summary>
    internal static GalaxyStyle.GalaxyShapeParameters PseudoStyleToGalaxyShapeParameters(
        PseudoStyle pseudo, StaticScenario scenario)
    {
        return new GalaxyStyle.GalaxyShapeParameters
        {
            CoreRadiusPerc = pseudo.CoreRadiusPerc,
            NumStarsCorePerc = pseudo.NumStarsCorePerc,
            StarsMinDist = pseudo.StarsMinDist,
            NumArms = 0,
            HasRing = false,
            PreviewIcon = pseudo.PreviewIcon,
            ButtonIcon = pseudo.ButtonIcon,
            DescKey = pseudo.DescKey
        };
    }

    /// <summary>取系统坐标（随机范围块随机取值），返回 (X, Y)。</summary>
    internal static (double X, double Y) SamplePosition(SystemEntry entry)
        => (entry.Position.GetX(), entry.Position.GetY());

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
