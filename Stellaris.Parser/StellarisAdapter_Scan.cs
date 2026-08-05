// 文件: Stellaris.Parser/StellarisAdapter_Scan.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Stellaris.Parser
{
    public partial class StellarisAdapter
    {
        // ========== 首次扫描 ==========
        public void ScanAll()
        {
            lock (_stateLock)
            {
                LoggerSetup.Initialize();
                PerformScan();
            }
        }

        // ========== 重扫描 ==========
        public void Rescan()
        {
            lock (_stateLock)
            {
                ClearAllData();
                PerformScan();
            }
        }

        // ========== 核心扫描逻辑 ==========
        private void PerformScan()
        {
            if (_roots.Count == 0)
                return;

            _logger.LogInformation("开始扫描，根目录数量: {Count}", _roots.Count);

            try
            {
                ParseAllFiles();
                RunScanStage(ExpandAllInlineScripts, "内联脚本展开");
                RunScanStage(RecollectLocalConstants, "局部常量收集");
                RunScanStage(EvaluateAllConstants, "常量与表达式求值");
            }
            finally
            {
                SetTask(ParserTaskType.Idle);
            }

            _logger.LogInformation("扫描完成，解析文件数: {Count}", _configResults.Count);
        }

        /// <summary>
        /// 防御性：单个扫描阶段失败只记录 Error 日志并跳过，不得中断整个扫描
        /// （默认输入不可信，尽量从可用数据中提取结果）。
        /// </summary>
        private void RunScanStage(Action stage, string stageName)
        {
            try
            {
                stage();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "扫描阶段失败（已跳过，继续后续阶段）: {Stage}", stageName);
            }
        }

        // ========== 阶段1：初始解析 ==========
        private void ParseAllFiles()
        {
            try
            {
                SetTask(ParserTaskType.LoadingGlobals);
                LoadGlobalConstants();

                var allFiles = new Dictionary<string, (string root, string fullPath)>();
                foreach (var root in _roots)
                {
                    foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
                    {
                        if (!_fileExtensions.Any(ext => file.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                            continue;

                        string relPath = NormalizePath(Path.GetRelativePath(root, file));
                        allFiles[relPath] = (root, file);
                    }
                }

                _csvCache.Clear();

                foreach (var kv in allFiles)
                {
                    string relPath = kv.Key;
                    string root = kv.Value.root;
                    string fullPath = kv.Value.fullPath;

                    _fileIndex[relPath] = root;

                    SetTask(ParserTaskType.ParsingFile, fullPath);
                    _logger.LogDebug("解析文件: {RelPath} (来自 {Root})", relPath, root);

                    // 防御性：单个文件读取/解析失败不得中断整个扫描，
                    // 记录 Error 日志后继续处理其余文件（尽力提取可用数据）。
                    try
                    {
                        var content = File.ReadAllText(fullPath);
                        var lines = File.ReadAllLines(fullPath);
                        var lexer = new Lexer(content);
                        var tokens = new List<Token>();
                        Token tok;
                        while ((tok = lexer.NextToken()).Type != TokenType.Eof)
                            tokens.Add(tok);

                        var parser = new Parser(tokens, lines, fullPath, content);
                        var result = parser.Parse();

                        var tempResolver = new ConstantResolver(_globalResolver);
                        CollectLocalConstants(result, tempResolver);
                        string dir = Path.GetDirectoryName(relPath) ?? "";

                        if (_enableCsvMerge)
                            ApplyCsvMerging(result, dir);

                        _configResults[relPath] = result;
                        _configRoots[relPath] = root;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "解析文件失败（已跳过，不影响其余文件）: {Path}", fullPath);
                    }
                }

                SetTask(ParserTaskType.LocalizationFormat);
                LoadLocalisation();
            }
            finally
            {
                SetTask(ParserTaskType.Idle);
            }
        }

        private void LoadGlobalConstants()
        {
            _globalResolver.ClearGlobal();
            foreach (var root in _roots)
            {
                string varDir = Path.Combine(root, "common", "scripted_variables");
                if (!Directory.Exists(varDir))
                    continue;

                var files = Directory.GetFiles(varDir, "*.txt")
                    .OrderBy(Path.GetFileName)
                    .ToList();

                foreach (var fullPath in files)
                {
                    _logger.LogDebug("加载全局常量: {Path}", fullPath);
                    try
                    {
                        var content = File.ReadAllText(fullPath);
                        var lines = File.ReadAllLines(fullPath);
                        var lexer = new Lexer(content);
                        var tokens = new List<Token>();
                        Token tok;
                        while ((tok = lexer.NextToken()).Type != TokenType.Eof)
                            tokens.Add(tok);

                        var parser = new Parser(tokens, lines, fullPath, content);
                        var result = parser.Parse();

                        CollectGlobalConstantsRecursive(result, fullPath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "加载全局常量文件失败: {Path}", fullPath);
                    }
                }
            }
        }

        private void CollectGlobalConstantsRecursive(ParserResult result, string filePath)
        {
            foreach (var node in result.RootNodes)
                CollectGlobalConstantsRecursive(node, filePath);
        }

        private void CollectGlobalConstantsRecursive(AstNode node, string filePath)
        {
            if (node == null) return;

            if (node.Type == NodeType.Simple && node.Key != null && node.Key.StartsWith('@'))
            {
                string constName = node.Key[1..];
                var val = node.Value;
                if (val is int || val is long || val is float || val is double)
                {
                    _globalResolver.SetGlobal(constName, val);
                }
                else
                {
                    _logger.LogError("全局常量 {Name} 的值不是数字字面量，已跳过 (文件: {Path}): {Value}",
                        constName, filePath, val);
                }
            }

            if (node.Type == NodeType.Block || node.Type == NodeType.List || node.Type == NodeType.InlineScript)
            {
                foreach (var child in node.Children)
                    CollectGlobalConstantsRecursive(child, filePath);
            }
        }

        private void CollectLocalConstants(ParserResult result, ConstantResolver resolver)
        {
            foreach (var node in result.RootNodes)
                CollectLocalConstantsRecursive(node, resolver);
        }

        private void CollectLocalConstantsRecursive(AstNode node, ConstantResolver resolver)
        {
            if (node == null) return;

            if (node.Type == NodeType.Simple && node.Key != null && node.Key.StartsWith('@'))
            {
                string constName = node.Key[1..];
                if (node.Value != null)
                    resolver.SetLocal(constName, node.Value);
            }

            if (node.Type == NodeType.Block || node.Type == NodeType.List || node.Type == NodeType.InlineScript)
            {
                foreach (var child in node.Children)
                    CollectLocalConstantsRecursive(child, resolver);
            }
        }

        private void RecollectLocalConstants()
        {
            SetTask(ParserTaskType.CollectingLocals);
            // 实际收集已在 EvaluateAllConstants 中通过 ConstantResolver 完成，此处仅设置任务状态
        }

        private void ApplyCsvMerging(ParserResult result, string directory)
        {
            if (string.IsNullOrEmpty(directory)) return;

            SetTask(ParserTaskType.CsvMerge, directory);

            if (_csvCache.TryGetValue(directory, out var mergedCsvData))
            {
                if (mergedCsvData.Rows.Count > 0)
                {
                    var merger = new CsvMerger(mergedCsvData);
                    foreach (var node in result.RootNodes)
                        merger.MergeNode(node);
                }
                return;
            }

            var headerColumns = new List<string>();
            var rows = new Dictionary<string, Dictionary<string, object?>>();

            foreach (var root in _roots)
            {
                string dirPath = Path.Combine(root, directory);
                if (!Directory.Exists(dirPath)) continue;

                var csvFiles = Directory.GetFiles(dirPath, "*.csv")
                                        .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                                        .ToList();

                foreach (var csvFile in csvFiles)
                {
                    try
                    {
                        var parsed = CsvParser.Parse(csvFile);
                        if (parsed.HeaderColumns.Count == 0 || parsed.Rows.Count == 0)
                            continue;

                        foreach (var col in parsed.HeaderColumns)
                            if (!headerColumns.Contains(col))
                                headerColumns.Add(col);

                        foreach (var row in parsed.Rows)
                        {
                            string indexValue = row.Key;
                            var rowData = row.Value;

                            if (!rows.ContainsKey(indexValue))
                                rows[indexValue] = new Dictionary<string, object?>();

                            foreach (var col in rowData)
                                rows[indexValue][col.Key] = col.Value;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "解析CSV文件失败: {Path}", csvFile);
                    }
                }
            }

            mergedCsvData = new CsvData
            {
                HeaderColumns = headerColumns,
                Rows = rows
            };
            _csvCache[directory] = mergedCsvData;

            if (mergedCsvData.Rows.Count > 0)
            {
                var merger = new CsvMerger(mergedCsvData);
                foreach (var node in result.RootNodes)
                    merger.MergeNode(node);
            }
        }

        // ================================================================
        // 1.6 本地化文件处理（使用 _localisationTable）
        // ================================================================

        /// <summary>
        /// 加载所有本地化文件，使用 _localisationTable 存储。
        /// 符合规范 1.6-a、1.6-b、1.6-c。
        /// </summary>
        private void LoadLocalisation()
        {
            // a) 收集阶段：遍历所有根目录，收集所有 yml 文件
            var allYmlFiles = new List<(string Root, string RelPath, string FullPath)>();

            foreach (var root in _roots)
            {
                string locDir = Path.Combine(root, "localisation");
                if (!Directory.Exists(locDir)) continue;

                foreach (var langDir in Directory.GetDirectories(locDir))
                {
                    string lang = Path.GetFileName(langDir);

                    var ymlFiles = Directory.GetFiles(langDir, "*.yml", SearchOption.AllDirectories);
                    foreach (var ymlFile in ymlFiles)
                    {
                        string relPath = NormalizePath(Path.GetRelativePath(root, ymlFile));
                        allYmlFiles.Add((root, relPath, ymlFile));
                    }
                }
            }

            if (allYmlFiles.Count == 0)
            {
                _logger.LogDebug("未找到任何本地化文件");
                return;
            }

            // b) 合并阶段：按根目录优先级（后添加的优先级高）遍历
            // 先按 root 索引降序排列，使高优先级先处理
            var sortedFiles = allYmlFiles
                .OrderByDescending(f => _roots.IndexOf(f.Root))
                .ThenBy(f => f.RelPath)
                .ToList();

            foreach (var (root, relPath, ymlFile) in sortedFiles)
            {
                // 语言优先从文件名 _l_{lang}.yml 提取：
                // localisation/english/name_lists/xxx.yml 这类子目录不是语言目录，
                // 直接用目录名会把 name_lists/random_names 误当成语言。
                string lang = ExtractLocalisationLang(relPath)
                              ?? Path.GetFileName(Path.GetDirectoryName(relPath))
                              ?? "english";

                try
                {
                    SetTask(ParserTaskType.LocalizationFormat, $"{lang}: {Path.GetFileName(ymlFile)}");
                    var parsed = LocalisationParser.ParseRaw(ymlFile);

                    if (parsed.Count > 0)
                    {
                        _localisationFiles.Add(lang + " " + relPath); // 文件登记（供落盘清理）
                        // 使用批量添加接口，内部自动处理优先级覆盖
                        AddLocalisationEntries(lang, relPath, root, parsed);
                        _logger.LogDebug("加载本地化文件: {RelPath} ({Lang}, {Count} 个条目)", relPath, lang, parsed.Count);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "解析本地化文件失败: {Path}", ymlFile);
                }
            }

            // c) 替换阶段：对每个语言的所有条目进行 $var$ 展开
            ExpandLocalisationValues();
            _logger.LogInformation("本地化加载完成，共 {LangCount} 种语言", _localisationTable.Count);
        }

        /// <summary>
        /// 从本地化文件相对路径提取语言代码（群星命名约定 xxx_l_{lang}.yml，
        /// 如 localisation/english/name_lists/foo_l_english.yml → "english"）。
        /// 文件名不含 _l_ 约定时返回 null，由调用方回退到目录名。
        /// </summary>
        private static string? ExtractLocalisationLang(string relPath)
        {
            string fileName = Path.GetFileName(relPath);
            if (!fileName.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
                return null;
            string baseName = fileName[..^4];
            int idx = baseName.LastIndexOf("_l_", StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return null;
            string lang = baseName[(idx + 3)..];
            return lang.Length > 0 ? lang : null;
        }

        /// <summary>
        /// 对 _localisationTable 中所有条目的 Value 进行 $var$ 展开。
        /// 符合规范 1.6-c。
        /// </summary>
        private void ExpandLocalisationValues()
        {
            int totalExpanded = 0;

            foreach (var langPair in _localisationTable)
            {
                string lang = langPair.Key;
                var dict = langPair.Value;

                if (dict.Count == 0) continue;

                SetTask(ParserTaskType.LocalizationFormat, lang);

                // 构建原始字典（key -> 逻辑值/未展开原文）
                var rawDict = dict.ToDictionary(kv => kv.Key,
                    kv => string.IsNullOrEmpty(kv.Value.LogicalValue) ? kv.Value.Value : kv.Value.LogicalValue);
                var resolvedCache = new Dictionary<string, string>();

                foreach (var key in dict.Keys.ToList())
                {
                    var visiting = new HashSet<string>();
                    string source = string.IsNullOrEmpty(dict[key].LogicalValue) ? dict[key].Value : dict[key].LogicalValue;
                    string expanded = TextReplacer.Expand(source, rawDict, resolvedCache, visiting, 0, _logger);
                    if (expanded != dict[key].Value)
                    {
                        dict[key].Value = expanded;
                        totalExpanded++;
                    }
                    resolvedCache[key] = expanded;
                }
            }

            _logger.LogDebug("本地化展开完成，共 {Count} 个条目被展开", totalExpanded);
        }

        /// <summary>
        /// 单键展开显示值：编辑逻辑值（原文）后调用，用该语言全部逻辑值作为原始字典，
        /// 把指定 key 的显示值（Value）重新展开（LogicalValue 保持不变）。
        /// </summary>
        public void ExpandLocalisationKey(string lang, string key)
        {
            lock (_stateLock)
            {
                if (!_localisationTable.TryGetValue(lang, out var dict) || !dict.TryGetValue(key, out var entry))
                    return;
                var rawDict = dict.ToDictionary(kv => kv.Key,
                    kv => string.IsNullOrEmpty(kv.Value.LogicalValue) ? kv.Value.Value : kv.Value.LogicalValue);
                var resolvedCache = new Dictionary<string, string>();
                var visiting = new HashSet<string>();
                string source = string.IsNullOrEmpty(entry.LogicalValue) ? entry.Value : entry.LogicalValue;
                entry.Value = TextReplacer.Expand(source, rawDict, resolvedCache, visiting, 0, _logger);
            }
        }

        // ========== 阶段2：内联脚本展开 ==========
        private void ExpandAllInlineScripts()
        {
            if (!_enableInlineScript) return;

            try
            {
                SetTask(ParserTaskType.InlineScriptExpand);
                _expander = new ScriptExpander(_roots, _logger);

                foreach (var kv in _configResults)
                {
                    var result = kv.Value;
                    var newRootNodes = new List<AstNode>();
                    foreach (var node in result.RootNodes)
                    {
                        var expanded = _expander.Expand(node);
                        newRootNodes.AddRange(expanded);
                    }
                    result.RootNodes = newRootNodes;
                }
            }
            finally
            {
                SetTask(ParserTaskType.Idle);
            }
        }

        // ========== 阶段3：常量与表达式求值 ==========
        private void EvaluateAllConstants()
        {
            try
            {
                SetTask(ParserTaskType.ConstantEvaluation);

                foreach (var kv in _configResults)
                {
                    var result = kv.Value;

                    var resolver = new ConstantResolver(_globalResolver);
                    CollectLocalConstants(result, resolver);

                    var evaluator = new ExpressionEvaluator(resolver, _logger);

                    foreach (var node in result.RootNodes)
                        evaluator.EvaluateNode(node);
                }
            }
            finally
            {
                // 阶段 3 完成（无论求值是否部分失败）：全量构建常量引用索引（规范 6.1 步骤 4）
                try
                {
                    BuildConstantReferenceIndex();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "常量引用索引构建失败（已跳过）");
                }
                SetTask(ParserTaskType.Idle);
            }
        }
    }
}