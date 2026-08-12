using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Stellaris.Tests;

/// <summary>启动器数据库（launcher-v2.sqlite）解析测试：用沙盒 sqlite 文件验证播放集/mod 顺序解析。</summary>
public sealed class LauncherSqliteImportTests
{
    [Test]
    public void ImportsPlaysetWithOrderedEnabledModDirs()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "launcher_test_" + Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            using (var con = new SqliteConnection($"Data Source={dbPath};Pooling=false"))
            {
                con.Open();
                using var cmd = con.CreateCommand();
                cmd.CommandText = @"
CREATE TABLE mods (id TEXT PRIMARY KEY, dirPath TEXT, displayName TEXT);
CREATE TABLE playsets (id TEXT PRIMARY KEY, name TEXT, offDisk INTEGER, isRemoved INTEGER);
CREATE TABLE playsets_mods (playsetId TEXT, modId TEXT, enabled INTEGER, position INTEGER);
INSERT INTO mods VALUES ('m1','C:/mods/alpha','Alpha');
INSERT INTO mods VALUES ('m2','C:/mods/beta','Beta');
INSERT INTO mods VALUES ('m3','C:/mods/gamma','Gamma');
INSERT INTO playsets VALUES ('p1','我的集合',0,0);
INSERT INTO playsets VALUES ('p2','空集合',0,0);
INSERT INTO playsets_mods VALUES ('p1','m3',1,2);
INSERT INTO playsets_mods VALUES ('p1','m1',1,0);
INSERT INTO playsets_mods VALUES ('p1','m2',0,1);
";
                cmd.ExecuteNonQuery();
            }

            var sets = Stellaris.Parser.LauncherSqliteImporter.Import(dbPath);
            Assert.Equal(2, sets.Count, "两个播放集都应导入（空集合也导入，目录为空）");
            var set = sets[0];
            Assert.Equal("我的集合", set.Name, "播放集名");
            Assert.Equal(2, set.ModDirs.Count, "enabled 的 mod 只有 2 个（m2 disabled 跳过）");
            Assert.Equal("C:/mods/alpha", set.ModDirs[0], "按 position 排序第一（m1 pos0）");
            Assert.Equal("C:/mods/gamma", set.ModDirs[1], "按 position 排序第二（m3 pos2）");
            Assert.Equal(0, sets[1].ModDirs.Count, "空播放集目录为空");
        }
        finally
        {
            try { File.Delete(dbPath); } catch { }
        }
    }

    [Test]
    public void ImportMissingFileThrows()
    {
        var missing = Path.Combine(Path.GetTempPath(), "launcher_nonexist_" + Guid.NewGuid().ToString("N") + ".sqlite");
        var threw = false;
        try { Stellaris.Parser.LauncherSqliteImporter.Import(missing); }
        catch { threw = true; }
        Assert.True(threw, "文件不存在应抛异常（由调用方弹窗提示）");
    }
}
