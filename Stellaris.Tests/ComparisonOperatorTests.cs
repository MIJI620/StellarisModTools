using System;
using System.IO;
using System.Linq;
using Stellaris.Parser;

namespace Stellaris.Tests;

public sealed class ComparisonOperatorTests
{
    [Test]
    public void Run()
    {
        var content = "a = 1\nb >= 2\nc <= 3\nd > 4\ne < 5\nf = { g >= 6 }\n";
        var adapter = new StellarisAdapter();
        var root = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "op_probe_" + Guid.NewGuid().ToString("N")));
        root.Create();
        try
        {
            var file = Path.Combine(root.FullName, "probe.txt");
            File.WriteAllText(file, content);
            adapter.AddRoot(root.FullName);
            adapter.ScanAll();
            var result = adapter.GetConfig("probe.txt");
            Assert.True(result != null, "解析失败");
            var f = result.RootNodes.FirstOrDefault(n => n.Key == "f");
            if (f != null)
            {
                foreach (var c in f.Children)
                    Console.WriteLine("CHILD: key=" + c.Key + " sep=" + c.SeparatorType + " raw=" + (c.RawText ?? "null") + " val=" + (c.Value?.ToString() ?? "null"));
            }
            var ser = SerializationHelper.Serialize(result.RootNodes);
            Console.WriteLine("SER:\n" + ser.Replace("\t", "    "));
            Assert.True(ser.Contains("b >= 2", StringComparison.Ordinal), ">= 写回丢失");
            Assert.True(ser.Contains("c <= 3", StringComparison.Ordinal), "<= 写回丢失");
            Assert.True(ser.Contains("d > 4", StringComparison.Ordinal), "> 写回丢失");
            Assert.True(ser.Contains("e < 5", StringComparison.Ordinal), "< 写回丢失");
            Assert.True(ser.Contains("g >= 6", StringComparison.Ordinal), "块内 >= 写回丢失");
        }
        finally
        {
            try { root.Delete(true); } catch { }
        }
    }
}
