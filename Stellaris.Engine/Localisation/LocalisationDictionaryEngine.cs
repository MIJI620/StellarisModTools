using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Stellaris.Parser;

namespace Stellaris.Engine.Localisation
{
    /// <summary>语言字典查询结果行（只读视图）。</summary>
    public sealed class LocalisationEntryView
    {
        public required string Language { get; init; }
        public required string Key { get; init; }
        public required string DisplayValue { get; init; }
        public required string LogicalValue { get; init; }
        public required string RelativePath { get; init; }
        public required string AbsolutePath { get; init; }
    }

    /// <summary>
    /// 语言字典引擎（只读）：按语种 + 正则匹配 key / 显示值查询全部本地化条目。
    /// 本引擎是**唯一**允许使用正则的位置（用户授权：仅此处、仅读取、无写入无修改）。
    /// </summary>
    public sealed class LocalisationDictionaryEngine
    {
        private readonly StellarisAdapter _adapter;
        private readonly ILogger _logger;

        public LocalisationDictionaryEngine(StellarisAdapter adapter, ILogger logger)
        {
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
            _logger = logger;
        }

        /// <summary>可用语种列表（扫描到的全部语言）。</summary>
        public IReadOnlyList<string> GetLanguages()
            => _adapter.GetAllLocalisations().Keys
                .OrderBy(l => l, StringComparer.Ordinal)
                .ToList();

        /// <summary>
        /// 查询（只读）。language 为空或 "*" = 全部语种；
        /// keyPattern / valuePattern 为正则（可为空 = 不过滤；无效正则抛异常由 UI 提示）。
        /// </summary>
        public List<LocalisationEntryView> Query(string? language, string? keyPattern, string? valuePattern,
            bool ignoreCase = false)
        {
            Regex? keyRx = Compile(keyPattern, nameof(keyPattern), ignoreCase);
            Regex? valueRx = Compile(valuePattern, nameof(valuePattern), ignoreCase);

            var languages = string.IsNullOrEmpty(language) || language == "*"
                ? GetLanguages()
                : new[] { language };

            var result = new List<LocalisationEntryView>();
            foreach (var lang in languages)
            {
                foreach (var (key, entry) in _adapter.GetLocalisationEntriesDetailed(lang))
                {
                    if (keyRx != null && !keyRx.IsMatch(key))
                        continue;
                    if (valueRx != null && !valueRx.IsMatch(entry.Value))
                        continue;
                    result.Add(new LocalisationEntryView
                    {
                        Language = lang,
                        Key = key,
                        DisplayValue = entry.Value,
                        LogicalValue = string.IsNullOrEmpty(entry.LogicalValue) ? entry.Value : entry.LogicalValue,
                        RelativePath = entry.CurrentPath,
                        AbsolutePath = BuildAbsolutePath(entry.Root, entry.CurrentPath)
                    });
                }
            }
            return result;
        }

        private static Regex? Compile(string? pattern, string paramName, bool ignoreCase)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                return null;
            try
            {
                // 仅匹配用（非解析群星文件）；默认区分大小写，可勾选忽略
                var opts = RegexOptions.CultureInvariant;
                if (ignoreCase) opts |= RegexOptions.IgnoreCase;
                return new Regex(pattern, opts, TimeSpan.FromSeconds(2));
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException($"无效正则（{paramName}）：{ex.Message}", ex);
            }
        }

        private static string BuildAbsolutePath(string root, string relPath)
        {
            try
            {
                if (string.IsNullOrEmpty(root))
                    return relPath;
                return Path.GetFullPath(Path.Combine(root, relPath));
            }
            catch
            {
                return relPath;
            }
        }
    }
}
