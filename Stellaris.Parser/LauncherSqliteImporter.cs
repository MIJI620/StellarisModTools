using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace Stellaris.Parser;

/// <summary>启动器数据库（launcher-v2.sqlite）只读解析：读取所有播放集及其启用的 mod 目录（按加载顺序）。
/// 仅供"加载集合"右键导入使用——不写、不修改启动器数据库。</summary>
public static class LauncherSqliteImporter
{
    /// <summary>一个播放集 = 名称 + 按加载顺序排列的 mod 绝对目录（仅 enabled）。</summary>
    public sealed record PlaysetImport(string Name, List<string> ModDirs);

    /// <summary>只读打开启动器数据库，解析所有播放集。数据库无播放集时返回空列表。</summary>
    public static List<PlaysetImport> Import(string sqlitePath)
    {
        var result = new List<PlaysetImport>();
        var connString = new SqliteConnectionStringBuilder
        {
            DataSource = sqlitePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();

        using var con = new SqliteConnection(connString);
        con.Open();

        // mods：id → dirPath（同一 id 可能多行，取最后一行）
        var dirByMod = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = "SELECT id, dirPath FROM mods";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var id = r.GetString(0);
                if (r.IsDBNull(1))
                    continue;
                var dir = r.GetString(1);
                if (string.IsNullOrWhiteSpace(dir))
                    continue;
                dirByMod[id] = dir.Trim();
            }
        }

        // playsets：id → name（跳过 offDisk 或 isRemoved 的）
        var nameByPlay = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = "SELECT id, name, offDisk, isRemoved FROM playsets";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var id = r.GetString(0);
                if (r.IsDBNull(1) || string.IsNullOrWhiteSpace(r.GetString(1)))
                    continue;
                bool offDisk = !r.IsDBNull(2) && r.GetBoolean(2);
                bool removed = !r.IsDBNull(3) && r.GetBoolean(3);
                if (offDisk || removed)
                    continue;
                nameByPlay[id] = r.GetString(1).Trim();
            }
        }

        // playsets_mods：playsetId → (position, modId, enabled) 排序
        var modsByPlay = new Dictionary<string, List<(int Pos, string ModId, bool Enabled)>>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = "SELECT playsetId, modId, enabled, position FROM playsets_mods";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var pid = r.GetString(0);
                var modId = r.GetString(1);
                bool enabled = !r.IsDBNull(2) && r.GetBoolean(2);
                int pos = r.IsDBNull(3) ? 0 : r.GetInt32(3);
                if (!modsByPlay.TryGetValue(pid, out var list))
                    modsByPlay[pid] = list = new List<(int, string, bool)>();
                list.Add((pos, modId, enabled));
            }
        }

        foreach (var (pid, pname) in nameByPlay)
        {
            var dirs = new List<string>();
            if (modsByPlay.TryGetValue(pid, out var rows))
                dirs = rows
                    .Where(x => x.Enabled && dirByMod.ContainsKey(x.ModId))
                    .OrderBy(x => x.Pos)
                    .Select(x => dirByMod[x.ModId])
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            result.Add(new PlaysetImport(pname, dirs));
        }

        // 按创建顺序稳定排序（数据库返回顺序）——不保证，但结果列表稳定即可
        return result;
    }
}
