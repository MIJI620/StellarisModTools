// 文件: Stellaris.Parser/StellarisAdapter.cs
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;

namespace Stellaris.Parser
{
    /// <summary>
    /// 文件类别，用于区分适配器内部两种不同的内存缓存。
    /// </summary>
    public enum FileCategory
    {
        /// <summary>配置文件（.txt/.gfx/.gui/.asset），对应 _configResults</summary>
        Config,
        /// <summary>本地化文件（.yml），对应 _localisationTable</summary>
        Localisation
    }

    /// <summary>
    /// 本地化条目，存储单个本地化键值对的完整元数据。
    /// 符合规范 1.6-d。
    /// </summary>
    public class LocalisationEntry
    {
        /// <summary>本地化文本（已展开 / 显示值）</summary>
        public string Value { get; set; } = string.Empty;

        /// <summary>逻辑值（原文，可能含 $var$ 替换占位；展开前的原文）</summary>
        public string LogicalValue { get; set; } = string.Empty;

        /// <summary>该条目当前所在的文件相对路径</summary>
        public string CurrentPath { get; set; } = string.Empty;

        /// <summary>该条目的来源文件相对路径（扫描时初始等于 CurrentPath）</summary>
        public string OldPath { get; set; } = string.Empty;

        /// <summary>来源文件所在的根目录</summary>
        public string Root { get; set; } = string.Empty;
    }

    public enum ParserTaskType
    {
        Idle,
        ParsingFile,
        CsvMerge,
        LocalizationFormat,
        InlineScriptExpand,
        ConstantEvaluation,
        WritingFile,
        LoadingGlobals,
        CollectingLocals
    }

    public class TaskChangedEventArgs : EventArgs
    {
        public ParserTaskType TaskType { get; }
        public string? Argument { get; }
        public TaskChangedEventArgs(ParserTaskType type, string? arg)
        {
            TaskType = type;
            Argument = arg;
        }
    }

    public partial class StellarisAdapter
    {
        private readonly List<string> _roots = new();
        private readonly Dictionary<string, ParserResult> _configResults = new();
        private readonly Dictionary<string, string> _configRoots = new();
        /// <summary>相对路径撞击表：relPath → 各 root 的 (root, 绝对路径)。
        /// 仅记录**多个 root 撞同一个相对路径**的情况；常规扫描仍按覆盖规则合并（本表与常规隔离）。</summary>
        private readonly Dictionary<string, List<(string Root, string FullPath)>> _pathCollisions = new(StringComparer.OrdinalIgnoreCase);

        // 本地化存储：按语言分块，每个条目包含 Value、CurrentPath、OldPath、Root
        // 符合规范 1.6-d 和 16.5
        private readonly Dictionary<string, Dictionary<string, LocalisationEntry>> _localisationTable = new(StringComparer.Ordinal);

        // 本地化文件登记（{lang}\u0000{path}）：键被转移/删除后仍保留文件记录，用于落盘清理旧文件
        private readonly HashSet<string> _localisationFiles = new(StringComparer.Ordinal);

        private readonly ConstantResolver _globalResolver = new();
        private readonly ILogger _logger;
        private ScriptExpander? _expander;
        private readonly object _stateLock = new();

        // 常量引用索引：全局常量名 -> 引用该常量的 AST 节点弱引用集合（规范 4.1.3）
        private readonly Dictionary<string, HashSet<WeakReference<AstNode>>> _constantReferenceIndex =
            new(StringComparer.Ordinal);

        // 目录检索内部索引
        private readonly Dictionary<string, string> _fileIndex = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CsvData> _csvCache = new();

        // ===== 配置属性 =====
        private List<string> _fileExtensions = new() { ".txt", ".gui", ".gfx", ".asset" };
        private bool _enableInlineScript = true;
        private bool _enableCsvMerge = true;

        public List<string> FileExtensions
        {
            get => _fileExtensions;
            set => _fileExtensions = value ?? new List<string>();
        }

        public bool EnableInlineScript
        {
            get => _enableInlineScript;
            set => _enableInlineScript = value;
        }

        public bool EnableCsvMerge
        {
            get => _enableCsvMerge;
            set => _enableCsvMerge = value;
        }

        // ===== 任务状态 =====
        private ParserTaskType _currentTaskType = ParserTaskType.Idle;
        private string? _taskArgument;
        public ParserTaskType CurrentTaskType => _currentTaskType;
        public string? TaskArgument => _taskArgument;
        public event EventHandler<TaskChangedEventArgs>? TaskChanged;

        protected void SetTask(ParserTaskType type, string? argument = null)
        {
            if (_currentTaskType != type || _taskArgument != argument)
            {
                _currentTaskType = type;
                _taskArgument = argument;
                TaskChanged?.Invoke(this, new TaskChangedEventArgs(type, argument));
                _logger.LogDebug("任务切换: {TaskType} {Arg}", type, argument);
            }
        }

        public StellarisAdapter(ILogger? logger = null)
        {
            _logger = logger ?? NullLogger.Instance;
        }

        /// <summary>添加 root（抗爆炸）：路径不存在 → 日志警告并跳过（不抛异常、不加入——避免扫描时爆炸）。</summary>
        public void AddRoot(string root)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                _logger.LogWarning("跳过不存在的 root 路径: {Root}", root);
                return;
            }
            _roots.Add(root);
        }

        public IReadOnlyList<string> Roots => _roots.AsReadOnly();

        /// <summary>覆盖规则读取器（解析层应用：只读一次 → 最早 root 生效）。null = 不应用规则。</summary>
        public Rules.RulesReader? Rules { get; set; }

        /// <summary>标记为"游戏"的 root（绝对路径——全局唯一）。只读一次规则跳过它（不算最早，
        /// 优先读它之后的 root；若之后无其他 root 的同名文件则回退到它）。null = 未标记。</summary>
        public string? GameRoot { get; set; }

        /// <summary>返回文件（相对路径）所属的根目录；未收录返回 null。</summary>
        public string? GetFileRoot(string relPath)
        {
            string norm = NormalizePath(relPath);
            return _fileIndex.TryGetValue(norm, out var root) ? root : null;
        }

        // ========== 清空所有内部状态 ==========
        private void ClearAllData()
        {
            _configResults.Clear();
            _configRoots.Clear();
            _pathCollisions.Clear();
            _localisationTable.Clear();
            _localisationFiles.Clear();
            _csvCache.Clear();
            _globalResolver.ClearGlobal();
            _constantReferenceIndex.Clear();
            _expander = null;
            _fileIndex.Clear();
        }

        // ========== 内部辅助 ==========
        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;
            string normalized = path.Replace('\\', '/');
            if (normalized.StartsWith('/'))
                normalized = normalized[1..];
            if (normalized.EndsWith('/'))
                normalized = normalized[..^1];
            return normalized;
        }

        private void BuildFileIndex()
        {
            _fileIndex.Clear();
            foreach (var kv in _configResults)
            {
                string relPath = NormalizePath(kv.Key);
                if (_configRoots.TryGetValue(kv.Key, out var root))
                {
                    _fileIndex[relPath] = root;
                }
                else
                {
                    _fileIndex[relPath] = _roots.Count > 0 ? _roots[^1] : string.Empty;
                }
            }
            _logger.LogDebug("文件索引构建完成，共 {Count} 个条目", _fileIndex.Count);
        }

        private static bool MatchPattern(string fileName, string pattern)
        {
            if (pattern == "*" || string.IsNullOrEmpty(pattern))
                return true;

            // 纯字符串通配符匹配（* = 任意串，? = 任意单字符，不区分大小写），不使用正则
            return WildcardMatch(fileName, pattern, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>通配符匹配（* / ?），标准单次回溯算法，无正则。</summary>
        private static bool WildcardMatch(string text, string pattern, StringComparison cmp)
        {
            int ti = 0, pi = 0, star = -1, mark = 0;
            while (ti < text.Length)
            {
                if (pi < pattern.Length
                    && (pattern[pi] == '?'
                        || string.Equals(text[ti].ToString(), pattern[pi].ToString(), cmp)))
                {
                    ti++;
                    pi++;
                }
                else if (pi < pattern.Length && pattern[pi] == '*')
                {
                    star = pi++;
                    mark = ti;
                }
                else if (star != -1)
                {
                    pi = star + 1;
                    ti = ++mark;
                }
                else
                {
                    return false;
                }
            }
            while (pi < pattern.Length && pattern[pi] == '*')
                pi++;
            return pi == pattern.Length;
        }

        // ========== 目录检索接口（第十五章） ==========
        public IReadOnlyDictionary<string, string> GetAllLoadedFiles()
        {
            return new ReadOnlyDictionary<string, string>(_fileIndex);
        }

        public bool FileExists(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath))
                return false;
            string norm = NormalizePath(relativePath);
            return _fileIndex.ContainsKey(norm);
        }

        public IReadOnlyList<string> GetFilesInDirectory(string relativeDirectory, string? searchPattern = null)
        {
            if (string.IsNullOrEmpty(relativeDirectory))
                relativeDirectory = "";
            string normDir = NormalizePath(relativeDirectory);
            if (normDir.Length > 0 && !normDir.EndsWith('/'))
                normDir += "/";

            var result = new List<string>();
            foreach (var path in _fileIndex.Keys)
            {
                string dir = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "";
                if (string.IsNullOrEmpty(dir))
                    dir = "";
                if (!dir.EndsWith('/') && dir.Length > 0)
                    dir += "/";

                if (!string.Equals(dir, normDir, StringComparison.OrdinalIgnoreCase))
                    continue;

                string fileName = Path.GetFileName(path);
                if (searchPattern != null && !MatchPattern(fileName, searchPattern))
                    continue;
                result.Add(path);
            }
            return result.AsReadOnly();
        }

        public IReadOnlyList<string> GetFilesRecursive(string relativeDirectory, string? searchPattern = null)
        {
            if (string.IsNullOrEmpty(relativeDirectory))
                relativeDirectory = "";
            string normDir = NormalizePath(relativeDirectory);
            if (normDir.Length > 0 && !normDir.EndsWith('/'))
                normDir += "/";

            var result = new List<string>();
            foreach (var path in _fileIndex.Keys)
            {
                if (normDir.Length == 0 || path.StartsWith(normDir, StringComparison.OrdinalIgnoreCase))
                {
                    string fileName = Path.GetFileName(path);
                    if (searchPattern != null && !MatchPattern(fileName, searchPattern))
                        continue;
                    result.Add(path);
                }
            }
            return result.AsReadOnly();
        }

        /// <summary>
        /// 直接从磁盘递归扫描指定相对目录下的二进制/未被解析文件（如 gfx/ 下的 .dds 贴图——不在 _fileIndex）。
        /// 遍历全部 roots，相对路径按覆盖规则去重；"只需要一个目录"（如 gfx/）。供图形可视化索引（用户 2026-08）。
        /// </summary>
        public IReadOnlyList<string> GetBinaryFilesRecursive(string relativeDirectory, string searchPattern)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string normDir = NormalizePath(relativeDirectory);
            foreach (var root in _roots)
            {
                if (string.IsNullOrEmpty(root))
                    continue;
                string dir = Path.Combine(root, normDir.Replace('/', Path.DirectorySeparatorChar));
                if (!Directory.Exists(dir))
                    continue;
                try
                {
                    foreach (var f in Directory.GetFiles(dir, searchPattern, SearchOption.AllDirectories))
                    {
                        string rel = Path.GetRelativePath(root, f).Replace('\\', '/');
                        if (rel.Length > 0 && !rel.StartsWith("../", StringComparison.Ordinal))
                            set.Add(rel);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "磁盘扫描失败: {Dir}", dir);
                }
            }
            return set.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        }
        /// <summary>
        /// 获取指定语言的本地化文本。
        /// 从 _localisationTable 中按 key 查询。
        /// </summary>
        public string? GetLocalisedText(string key, string lang = "english")
        {
            if (_localisationTable.TryGetValue(lang, out var dict) && dict.TryGetValue(key, out var entry))
                return entry.Value;
            return null;
        }

        /// <summary>获取本地化条目的逻辑值（原文，可能含 $var$ 替换占位；未展开）。</summary>
        public string? GetLocalisedLogicalText(string key, string lang = "english")
        {
            if (_localisationTable.TryGetValue(lang, out var dict) && dict.TryGetValue(key, out var entry))
                return entry.LogicalValue;
            return null;
        }

        /// <summary>
        /// 获取全部本地化数据的只读快照（语言 → 键 → 文本）。
        /// 数据已按根目录优先级合并（高优先级覆盖低优先级），供引擎/UI 使用。
        /// </summary>
        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> GetAllLocalisations()
        {
            var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
            foreach (var lang in _localisationTable)
            {
                var dict = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var kv in lang.Value)
                    dict[kv.Key] = kv.Value.Value;
                result[lang.Key] = dict;
            }
            return result;
        }

        /// <summary>
        /// 指定语言下全部本地化条目（键 → LocalisationEntry，含逻辑值/当前文件相对路径/来源根目录）。
        /// 供语言字典引擎只读查询。
        /// </summary>
        public IReadOnlyDictionary<string, LocalisationEntry> GetLocalisationEntriesDetailed(string lang)
        {
            lock (_stateLock)
            {
                if (_localisationTable.TryGetValue(lang, out var dict))
                    return dict;
                return new Dictionary<string, LocalisationEntry>(StringComparer.Ordinal);
            }
        }

        /// <summary>
        /// 获取指定语言下全部本地化文件的相对路径（去重）。
        /// 供引擎做"键从旧文件转移到规范文件"等操作（规整化）。
        /// </summary>
        public IReadOnlyList<string> GetLocalisationFiles(string lang)
        {
            string prefix = lang + "\u0000";
            return _localisationFiles
                .Where(f => f.StartsWith(prefix, StringComparison.Ordinal))
                .Select(f => f.Substring(prefix.Length))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>已加载本地化的全部语种（从 lang\0path 前缀提取，去重排序）。</summary>
        public IReadOnlyList<string> GetLocalisationLanguages()
            => _localisationFiles
                .Select(f => f.Substring(0, f.IndexOf('\0')))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();

        /// <summary>
        /// 指定语言下所有本地化键 → 当前所在文件路径（CurrentPath）的一次性索引。
        /// 供规整化 O(1) 判断键位置，避免反复全表扫描。
        /// </summary>
        public IReadOnlyDictionary<string, string> GetLocalisationKeyFiles(string lang)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            lock (_stateLock)
            {
                if (_localisationTable.TryGetValue(lang, out var dict))
                {
                    foreach (var kv in dict)
                    {
                        if (!string.IsNullOrEmpty(kv.Value.CurrentPath))
                            result[kv.Key] = kv.Value.CurrentPath;
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// 指定语言下"键 → 迁移来源文件（OldPath）"索引。
        /// OldPath 非空且与 CurrentPath 不同（键曾被迁移）时记录，供保存时清理磁盘旧文件。
        /// </summary>
        public IReadOnlyDictionary<string, string> GetLocalisationOldPathIndex(string lang)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            lock (_stateLock)
            {
                if (_localisationTable.TryGetValue(lang, out var dict))
                {
                    foreach (var kv in dict)
                    {
                        if (!string.IsNullOrEmpty(kv.Value.OldPath)
                            && !string.Equals(kv.Value.OldPath, kv.Value.CurrentPath, StringComparison.OrdinalIgnoreCase))
                            result[kv.Key] = kv.Value.OldPath;
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// 指定语言下**涉及写入**的全部文件（键的 CurrentPath 与 OldPath 并集）。
        /// 供保存时"统计目前/过去文件名 → 逐个写 CurrentPath 键值对"使用。
        /// </summary>
        public IReadOnlyList<string> GetLocalisationFilePaths(string lang)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            lock (_stateLock)
            {
                if (_localisationTable.TryGetValue(lang, out var dict))
                {
                    foreach (var kv in dict)
                    {
                        if (!string.IsNullOrEmpty(kv.Value.CurrentPath))
                            set.Add(kv.Value.CurrentPath);
                        if (!string.IsNullOrEmpty(kv.Value.OldPath)
                            && !string.Equals(kv.Value.OldPath, kv.Value.CurrentPath, StringComparison.OrdinalIgnoreCase))
                            set.Add(kv.Value.OldPath);
                    }
                }
            }
            return set.OrderBy(p => p, StringComparer.Ordinal).ToList();
        }

        /// <summary>指定语言下，该文件是否存在 CurrentPath 匹配的键（用于判断"纯旧文件"）。</summary>
        public bool HasLocalisationKeysInPath(string lang, string path)
        {
            lock (_stateLock)
            {
                if (!_localisationTable.TryGetValue(lang, out var dict))
                    return false;
                foreach (var kv in dict)
                {
                    if (string.Equals(kv.Value.CurrentPath, path, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }
        }

        /// <summary>
        /// 底层标准本地化保存流程（所有本地化写入必须经此）：
        /// 1. 根据所有键提取文件：统计目前文件名（CurrentPath）与过去文件名（OldPath）；
        /// 2. 将提取的文件名全部加入需要写入的列表；
        /// 3. 逐个提取文件名，写入"目前文件名（CurrentPath）中有这个文件名的键值对"；
        ///    纯旧文件（无当前键、但有 OldPath 转移记录 → 可追溯）→ 写空头清理磁盘被转移内容。
        /// </summary>
        public (bool AllSuccess, List<string> Errors) WriteAllLocalisations()
        {
            bool allSuccess = true;
            var errors = new List<string>();

            string[] languages;
            lock (_stateLock)
            {
                languages = _localisationTable.Keys.ToArray();
            }

            foreach (var lang in languages)
            {
                // 1+2. 收集该语言所有涉及文件（键 CurrentPath + OldPath 并集）
                var files = GetLocalisationFilePaths(lang);

                // 3. 逐个写 CurrentPath 键值对；纯旧文件（可追溯转移）写空头清理
                foreach (var file in files)
                {
                    string fileName = Path.GetFileName(file);
                    bool hasCurrent = HasLocalisationKeysInPath(lang, file);
                    if (!WriteLocalisation(lang, fileName, writeIfEmpty: !hasCurrent))
                    {
                        allSuccess = false;
                        errors.Add($"本地化写入失败: {lang}/{fileName}");
                    }
                }
            }

            return (allSuccess, errors);
        }

        public ParserResult? GetConfig(string relPath)
        {
            return _configResults.GetValueOrDefault(relPath);
        }

        public IReadOnlyDictionary<string, ParserResult> GetAllConfigs() => _configResults;

        /// <summary>相对路径撞击表（只读）：relPath → 所有 root 的 (root, 绝对路径)（>1 个 root 才记录）。</summary>
        public IReadOnlyDictionary<string, List<(string Root, string FullPath)>> PathCollisions => _pathCollisions;

        /// <summary>
        /// 撞击扫描（特殊接口，与常规隔离）：指定相对路径 → 每个 root 各自**独立解析**的 AST（不合并）。
        /// 常规 _configResults 只保留一个 root（按覆盖规则）；本接口每个 root 都解析、都返回，
        /// 且不写入任何内部状态——供资源/本地化等"需要看全部同名文件"的适配使用。
        /// </summary>
        public IReadOnlyList<(string Root, string FullPath, ParserResult Ast)> GetCollisionAsts(string relativePath)
        {
            var norm = NormalizePath(relativePath);
            var result = new List<(string, string, ParserResult)>();
            if (!_pathCollisions.TryGetValue(norm, out var entries))
                return result;
            foreach (var (root, fullPath) in entries)
            {
                try
                {
                    result.Add((root, fullPath, ParseConfigFile(fullPath)));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "撞击扫描解析失败: {FullPath}", fullPath);
                }
            }
            return result;
        }

        /// <summary>解析单个配置文件（Lexer + Parser——供常规扫描与撞击扫描共用）。</summary>
        private ParserResult ParseConfigFile(string fullPath)
        {
            var content = File.ReadAllText(fullPath);
            var lines = File.ReadAllLines(fullPath);
            var lexer = new Lexer(content);
            var tokens = new List<Token>();
            Token tok;
            while ((tok = lexer.NextToken()).Type != TokenType.Eof)
                tokens.Add(tok);
            var parser = new Parser(tokens, lines, fullPath, content);
            return parser.Parse();
        }

        // ================================================================
        // SA 基础服务：文本 ↔ AST 节点统一转换（各引擎统一调用，禁止自行 new Lexer/Parser 或自行拼文本）
        // ================================================================

        /// <summary>解析单条 "key = 值" / "key = { ... }" 文本 → 单节点（Simple/Block/List）。
        /// 各引擎"字段原文 → AST 节点"统一经此（法令/科技/战略资源共用——2026-08 曾各自重复实现）；
        /// 调用方自行校验节点 Key。解析失败返回 null。</summary>
        public AstNode? ParseSingleNode(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;
            try
            {
                var lexer = new Lexer(text);
                var tokens = new List<Token>();
                Token tok;
                while ((tok = lexer.NextToken()).Type != TokenType.Eof)
                    tokens.Add(tok);
                var parser = new Parser(tokens, new[] { text }, "adapter_text", text);
                var nodes = parser.Parse().RootNodes;
                return nodes.Count == 1 ? nodes[0] : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>节点列表 → 文本（**完整递归序列化**：嵌套块/注释/格式全保留）。
        /// 各引擎"块 → 文本"统一经此（禁止自行拼文本/简写嵌套块——科技 BlockToText 曾因简写丢内容，2026-08）。</summary>
        public string SerializeNodes(IReadOnlyList<AstNode> nodes)
        {
            if (nodes == null || nodes.Count == 0)
                return "";
            return SerializationHelper.Serialize(nodes.ToList());
        }

        /// <summary>【反向搜索】全 AST 字符串值反查询：value 作为字符串值在任一已加载文件（含递归子节点）中出现的所有位置。
        /// 返回列表：**第 1 位是出现次数（int）**，后续每位是位置 (string 文件相对路径, List&lt;object&gt; path)。
        /// **path 为标准选择路径（新标准格式）**——每层 = 字典 { "mode": 节点类型, "index": 该类型在当前层的序次 }，
        /// 可直接喂 SelectorResolver / SelectNodes / CRUD 增删改查（逐层语义，无歧义）。
        /// 调用方多数时候只需读第 1 位（次数）；需要基于 AST 位置操作时再用 path。
        /// 正向（条件 → 节点）查询用 SelectNodes。</summary>
        public List<object> FindStringValues(string value)
        {
            var result = new List<object> { 0 };
            foreach (var file in _configResults)
            {
                WalkChildren(file.Value.RootNodes, new List<object>(), file.Key);
            }
            return result;

            void WalkChildren(List<AstNode> nodes, List<object> path, string filePath)
            {
                // 每层按**类型**计数（该类型第几个）——与标准选择器 {mode, index} 对齐：
                // 逐层语义下 {mode, index} 唯一化定位（过滤当前空间 + 抽第几个），可精确到任意节点。
                var typeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int i = 0; i < nodes.Count; i++)
                {
                    var n = nodes[i];
                    string typeName = n.Type switch
                    {
                        NodeType.Block => "Block",
                        NodeType.List => "List",
                        _ => "Simple"
                    };
                    typeCounts.TryGetValue(typeName, out int c);
                    int kth = c;
                    typeCounts[typeName] = c + 1;
                    var selector = new Dictionary<string, object>
                    {
                        ["mode"] = typeName,
                        ["index"] = kth + 1   // 1 起（标准规范统一）
                    };

                    if (n.Value is string sv && string.Equals(sv, value, StringComparison.Ordinal))
                    {
                        result[0] = (int)result[0] + 1;
                        var target = new List<object>(path) { selector };
                        result.Add((filePath, target));
                    }

                    if (n.Children.Count > 0)
                    {
                        var sub = new List<object>(path) { selector };
                        WalkChildren(n.Children, sub, filePath);
                    }
                }
            }
        }

        // ========== 写入接口 ==========
        /// <summary>
        /// 撞击保存：把指定 root 的 relPath 文件的 AST（nodes）序列化写盘。
        /// 供撞击扫描/合并保存使用（常规 _configResults 不涉及该 root 时也能落盘）。
        /// 引擎层不直接操作底层——统一经此接口。
        /// </summary>
        public bool WriteCollisionFile(string relPath, string root, IReadOnlyList<AstNode> nodes)
        {
            if (string.IsNullOrEmpty(relPath) || string.IsNullOrEmpty(root) || nodes == null)
                return false;
            try
            {
                string fullPath = Path.Combine(root, relPath);
                SetTask(ParserTaskType.WritingFile, fullPath);
                string? dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                string content = SerializationHelper.Serialize(nodes as List<AstNode> ?? nodes.ToList());
                SerializationHelper.WriteFile(fullPath, content);
                _logger.LogInformation("撞击写回文件: {Path}", fullPath);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "撞击写回文件失败: {Path}", relPath);
                return false;
            }
        }

        /// <summary>
        /// 通用文本写盘：把 content 原样写入**指定 root** 的 relPath（自动建目录）。
        /// 供外部写盘根（与 Roots 无关的目标目录）生成文件使用（如 Extension 工具的部署输出）。
        /// encoding 可选："utf-8"（无 BOM）/ "utf-8-bom"（带 BOM）；缺省按扩展名规则
        /// （.yml 带 BOM，其他无 BOM）。引擎层不直接操作底层——统一经此接口。
        /// </summary>
        public bool WriteTextFile(string relPath, string root, string content, string? encoding = null, bool append = false)
        {
            if (string.IsNullOrEmpty(relPath) || string.IsNullOrEmpty(root) || content == null)
                return false;
            try
            {
                string fullPath = Path.Combine(root, relPath);
                SetTask(ParserTaskType.WritingFile, fullPath);
                string? dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                // append：读已有内容拼接（底层直接 File 操作——SA 是磁盘读写层）
                if (append && File.Exists(fullPath))
                {
                    var existing = File.ReadAllText(fullPath);
                    if (!string.IsNullOrEmpty(existing) && !string.IsNullOrEmpty(content))
                    {
                        if (!existing.EndsWith("\n", StringComparison.Ordinal))
                            existing += "\n";
                        content = existing + content;
                    }
                }
                SerializationHelper.WriteFile(fullPath, content, encoding);
                _logger.LogInformation("文本写盘{Append}: {Path}", append ? "(追加)" : "", fullPath);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "文本写盘失败: {Path}", relPath);
                return false;
            }
        }

        public bool WriteFile(string relPath, string? targetRoot = null)
        {
            if (string.IsNullOrEmpty(relPath) || !_configResults.TryGetValue(relPath, out var result))
                return false;

            string root;
            if (!string.IsNullOrEmpty(targetRoot) && Directory.Exists(targetRoot))
                root = targetRoot;
            else if (_roots.Count > 0)
                root = _roots[^1];
            else
            {
                _logger.LogError("没有可用的根目录，无法写回文件: {Path}", relPath);
                return false;
            }

            string fullPath = Path.Combine(root, relPath);
            try
            {
                SetTask(ParserTaskType.WritingFile, fullPath);

                string? dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var nodes = result.RootNodes;
                string content = SerializationHelper.Serialize(nodes);
                SerializationHelper.WriteFile(fullPath, content);
                _logger.LogInformation("写回文件: {Path} (目标根目录: {Root})", fullPath, root);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "写回文件失败: {Path}", fullPath);
                return false;
            }
            finally
            {
                SetTask(ParserTaskType.Idle);
            }
        }

        /// <summary>读取任意文本文件（配置/导出专用——如 key_extract_config.json）。
        /// 相对路径 → 按覆盖规则取生效 root 的文件；找不到返回 null。不写入内部状态。</summary>
        public string? ReadTextFile(string relPath)
        {
            var norm = NormalizePath(relPath);
            if (_configRoots.TryGetValue(norm, out var root) && !string.IsNullOrEmpty(root))
            {
                var full = Path.Combine(root, norm);
                if (File.Exists(full))
                    return File.ReadAllText(full);
            }
            // 无登记 root（该文件可能不在扫描扩展里）——按覆盖规则找 roots 最后一位存在者
            for (int i = _roots.Count - 1; i >= 0; i--)
            {
                var full = Path.Combine(_roots[i], norm);
                if (File.Exists(full))
                    return File.ReadAllText(full);
            }
            return null;
        }

        /// <summary>导出文档专用写盘（如 .md）：写 Roots 最后一位的 relPath。
        /// 独立方法——不占用现有 FileCategory（Config/Localisation），不经过类别表。</summary>
        public bool WriteExportFile(string relPath, string content)
        {
            if (string.IsNullOrEmpty(relPath) || content == null || _roots.Count == 0)
                return false;
            try
            {
                var root = _roots[^1];
                var full = Path.Combine(root, relPath);
                SetTask(ParserTaskType.WritingFile, full);
                var dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                SerializationHelper.WriteFile(full, content);
                _logger.LogInformation("导出文档写回: {Path}", full);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "导出文档写回失败: {Path}", relPath);
                return false;
            }
        }

        public void WriteAllFiles()
        {
            foreach (var key in _configResults.Keys.ToList())
                WriteFile(key);
        }

        // ================================================================
        // 1.7 本地化数据写回
        // ================================================================

        /// <summary>
        /// 将内存中指定本地化文件的数据写入 YML 文件。
        /// 从 _localisationTable[lang] 中筛选 CurrentPath == targetPath 的条目。
        /// 写入成功后，将该文件所有条目的 OldPath 更新为 targetPath。
        /// </summary>
        /// <param name="lang">语言标识（如 "english"）</param>
        /// <param name="fileName">YML 文件名（如 "mod_galaxy_shapes.yml"）</param>
        /// <param name="targetRoot">可选目标根目录，未指定则使用 Roots[-1]</param>
        /// <returns>写入是否成功（若无数据可写入则返回 false）</returns>
        public bool WriteLocalisation(string lang, string fileName, string? targetRoot = null, bool writeIfEmpty = false)
        {
            if (string.IsNullOrEmpty(lang))
            {
                _logger.LogError("WriteLocalisation: lang 不能为空");
                return false;
            }
            if (string.IsNullOrEmpty(fileName))
            {
                _logger.LogError("WriteLocalisation: fileName 不能为空");
                return false;
            }

            string targetPath = $"localisation/{lang}/{fileName}";

            // 1. 获取该文件的所有条目
            var fileEntries = GetLocalisationFileInternal(lang, targetPath);
            if (fileEntries == null || fileEntries.Count == 0)
            {
                if (writeIfEmpty)
                {
                    // 调用方已确认**可追溯**（如该文件全部键均已通过转移记录移走）→ 写空头清理磁盘
                    _logger.LogInformation("WriteLocalisation: 空文件清理（调用方确认可追溯） {Path}", targetPath);
                    fileEntries = new Dictionary<string, string>();
                }
                else
                {
                    // 数据安全：未确认可追溯时**不写空文件**（防止假空误删必要内容），跳过并记录
                    _logger.LogWarning("WriteLocalisation: 路径 '{Path}' 在内存中无数据且未确认可追溯，跳过写入（不写空文件）", targetPath);
                    return false;
                }
            }

            // 2. 确定目标根目录
            string root;
            if (!string.IsNullOrEmpty(targetRoot) && Directory.Exists(targetRoot))
                root = targetRoot;
            else if (_roots.Count > 0)
                root = _roots[^1];
            else
            {
                _logger.LogError("WriteLocalisation: 无可用根目录");
                return false;
            }

            // 3. 构造完整路径
            string fullPath = Path.Combine(root, targetPath);

            // 4. 确保目录存在
            string? dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            try
            {
                SetTask(ParserTaskType.WritingFile, fullPath);

                // 5. 序列化为 YML（缩进为 1 个空格）
                var sb = new StringBuilder();
                sb.AppendLine($"l_{lang}:");
                foreach (var kv in fileEntries.OrderBy(k => k.Key))
                {
                    string escapedValue = kv.Value?.Replace("\"", "\\\"") ?? string.Empty;
                    sb.AppendLine($" {kv.Key}: \"{escapedValue}\"");
                }
                string content = sb.ToString();

                // 6. 原子写入
                SerializationHelper.WriteFile(fullPath, content);

                // 7. 写入成功后，将该文件所有条目的 OldPath 更新为 targetPath（转移完成标记）。
                //    旧文件的重写（键转移后保留剩余/写空头）由 WriteAllLocalisations 统一按
                //    "收集新旧文件 → 逐个写 CurrentPath 键值对"处理。
                lock (_stateLock)
                {
                    if (_localisationTable.TryGetValue(lang, out var dict))
                    {
                        foreach (var kv in dict)
                        {
                            if (kv.Value.CurrentPath == targetPath)
                            {
                                kv.Value.OldPath = targetPath;
                            }
                        }
                    }
                }

                _logger.LogInformation("WriteLocalisation: 写入成功 {Path}，共 {Count} 个条目", fullPath, fileEntries.Count);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WriteLocalisation: 写入失败 {Path}", fullPath);
                return false;
            }
            finally
            {
                SetTask(ParserTaskType.Idle);
            }
        }

        // ================================================================
        // 内部辅助方法（供 CRUD 和 Scan 使用）
        // ================================================================

        /// <summary>
        /// 内部获取指定语言中指定路径的所有条目（键 -> 逻辑值/原文）。
        /// 用于 WriteLocalisation——落盘写**逻辑值**（含 $var$ 占位的原文），
        /// 显示值（Value，已展开）仅供 UI 展示，不写入磁盘。
        /// </summary>
        internal Dictionary<string, string>? GetLocalisationFileInternal(string lang, string path)
        {
            if (string.IsNullOrEmpty(lang) || string.IsNullOrEmpty(path))
                return null;

            lock (_stateLock)
            {
                if (!_localisationTable.TryGetValue(lang, out var dict))
                    return null;

                var result = new Dictionary<string, string>();
                foreach (var kv in dict)
                {
                    if (kv.Value.CurrentPath == path)
                    {
                        result[kv.Key] = string.IsNullOrEmpty(kv.Value.LogicalValue)
                            ? kv.Value.Value
                            : kv.Value.LogicalValue;
                    }
                }
                return result.Count > 0 ? result : null;
            }
        }
    }
}