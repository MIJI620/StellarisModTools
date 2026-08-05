using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Stellaris.Parser;

/// <summary>
/// CSV 解析结果，包含表头列顺序和行数据。
/// </summary>
public class CsvData
{
    public List<string> HeaderColumns { get; set; } = new();
    public Dictionary<string, Dictionary<string, object?>> Rows { get; set; } = new();
    // Rows 的 key 为索引列的值，内层字典为列名->值
}

public static class CsvParser
{
    public static CsvData Parse(string filePath)
    {
        var result = new CsvData();
        if (!File.Exists(filePath))
            return result;

        var lines = File.ReadAllLines(filePath);
        if (lines.Length == 0)
            return result;

        // 1. 查找表头行
        string? headerLine = null;
        int headerIndex = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                continue;
            headerLine = lines[i];
            headerIndex = i;
            break;
        }

        if (string.IsNullOrEmpty(headerLine))
            return result;

        // 2. 解析表头
        var headerParts = headerLine.Split(';', StringSplitOptions.None)
            .Select(h => h.Trim()).ToList();

        // 查找 "end" 列并截断
        int endIdx = headerParts.IndexOf("end");
        if (endIdx != -1)
            headerParts = headerParts.Take(endIdx).ToList();

        if (headerParts.Count == 0)
            return result;

        string indexColumn = headerParts[0];
        if (string.IsNullOrEmpty(indexColumn))
            return result;

        result.HeaderColumns = headerParts;

        // 3. 解析数据行
        for (int i = headerIndex + 1; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#'))
                continue;

            var parts = line.Split(';', StringSplitOptions.None)
                .Select(p => p.Trim()).ToList();

            while (parts.Count < headerParts.Count)
                parts.Add("");
            if (parts.Count > headerParts.Count)
                parts = parts.Take(headerParts.Count).ToList();

            string indexValue = parts[0];
            if (string.IsNullOrEmpty(indexValue))
                continue;

            var row = new Dictionary<string, object?>();
            for (int j = 0; j < headerParts.Count; j++)
            {
                string val = parts[j];
                if (string.IsNullOrEmpty(val))
                {
                    row[headerParts[j]] = null;
                    continue;
                }

                if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int intVal))
                    row[headerParts[j]] = intVal;
                else if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out double dblVal))
                    row[headerParts[j]] = dblVal;
                else
                    row[headerParts[j]] = val;
            }

            // 以索引列值为内层键
            if (!result.Rows.ContainsKey(indexValue))
                result.Rows[indexValue] = row;
        }

        return result;
    }
}