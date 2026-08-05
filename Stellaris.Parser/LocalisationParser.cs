using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Stellaris.Parser;

/// <summary>
/// 本地化 YML 解析器，严格遵循规范 1.6。
/// 支持解析和序列化。
/// </summary>
public static class LocalisationParser
{
    /// <summary>
    /// 仅解析文件内容为字典，不进行常量替换。
    /// 返回 Dictionary&lt;key, rawValue&gt;
    /// </summary>
    public static Dictionary<string, string> ParseRaw(string filePath)
    {
        var result = new Dictionary<string, string>();
        if (!File.Exists(filePath))
            return result;

        var lines = File.ReadAllLines(filePath, Encoding.UTF8);
        foreach (var rawLine in lines)
        {
            string line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#') || line.StartsWith("l_"))
                continue;

            int colonIdx = line.IndexOf(':');
            if (colonIdx == -1)
                continue;

            string key = line[..colonIdx].Trim();
            string rest = line[(colonIdx + 1)..].Trim();

            // 跳过可选数字部分
            int valueStart = 0;
            while (valueStart < rest.Length && char.IsDigit(rest[valueStart]))
                valueStart++;
            while (valueStart < rest.Length && char.IsWhiteSpace(rest[valueStart]))
                valueStart++;

            if (valueStart >= rest.Length || rest[valueStart] != '"')
                continue;

            int firstQuote = rest.IndexOf('"', valueStart);
            int lastQuote = rest.LastIndexOf('"');
            if (firstQuote == -1 || lastQuote == -1 || firstQuote == lastQuote)
                continue;

            string value = rest.Substring(firstQuote + 1, lastQuote - firstQuote - 1);
            result[key] = value;
        }

        return result;
    }

    /// <summary>
    /// 解析文件并进行迭代 $var$ 替换。
    /// 使用 TextReplacer 进行替换，检测自引用。
    /// </summary>
    public static Dictionary<string, string> ParseAndReplace(string filePath, TextReplacer replacer)
    {
        var raw = ParseRaw(filePath);
        if (raw.Count == 0 || replacer == null)
            return raw;

        var result = new Dictionary<string, string>(raw);
        var previousValues = new Dictionary<string, string>(result);

        int iteration = 0;
        bool changed;
        do
        {
            changed = false;
            iteration++;
            var keys = result.Keys.ToList();
            foreach (var key in keys)
            {
                string oldVal = result[key];
                var (newVal, changedSingle) = replacer.ReplaceWithStabilityCheck(oldVal, key);
                if (changedSingle)
                {
                    if (newVal == previousValues[key])
                        continue;
                    result[key] = newVal;
                    previousValues[key] = newVal;
                    changed = true;
                }
            }
        } while (changed && iteration < Config.MaxIterationDepth);

        return result;
    }

    /// <summary>
    /// 对已解析的字典执行一次迭代替换（用于阶段3二次替换）
    /// 使用全局常量字典，不进行自引用检测（因为已稳定）
    /// </summary>
    public static void ApplyReplacement(Dictionary<string, string> dict, TextReplacer replacer)
    {
        if (dict == null || dict.Count == 0 || replacer == null)
            return;

        var keys = dict.Keys.ToList();
        foreach (var key in keys)
        {
            string oldVal = dict[key];
            string newVal = replacer.Replace(oldVal);
            if (newVal != oldVal)
                dict[key] = newVal;
        }
    }

    /// <summary>
    /// 将本地化字典序列化为标准的 YML 文件。
    /// 格式：l_{lang}: 开头，每行缩进一个制表符， key: "value"。
    /// </summary>
    /// <param name="filePath">输出文件路径</param>
    /// <param name="lang">语言标识（如 "english"）</param>
    /// <param name="dict">本地化字典</param>
    public static void Serialize(string filePath, string lang, Dictionary<string, string> dict)
    {
        if (string.IsNullOrEmpty(filePath))
            throw new ArgumentNullException(nameof(filePath));
        if (string.IsNullOrEmpty(lang))
            throw new ArgumentNullException(nameof(lang));
        if (dict == null)
            throw new ArgumentNullException(nameof(dict));

        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var sb = new StringBuilder();
        sb.AppendLine($"l_{lang}:");
        foreach (var kv in dict)
        {
            // 值中可能包含双引号，但规范不要求转义，直接输出
            sb.AppendLine($" {kv.Key}: \"{kv.Value}\"");
        }

        // 使用原子写入（.temp 重命名）
        SerializationHelper.WriteFile(filePath, sb.ToString());
    }
}