using System.Text.Json;
using Microsoft.Extensions.Logging;
using Stellaris.Extension;
using Stellaris.Parser;

// 半隐藏拓展工具入口：读 extension_config.json（exe 同位置）→ modRoot →
// 读 {modRoot}/.smt/_extension.json → 建 SA（roots 读取源）→ 顺序执行全部部署。
// 两个配置文件属 App 层启动配置，直接 File 读写（同 Editor config/user_prefs.json 惯例）。

var jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
string baseDir = AppContext.BaseDirectory;
bool noPause = args.Contains("--no-pause", StringComparer.Ordinal);

try
{
    // ---- 第 1 层：extension_config.json ----
    string configPath = Path.Combine(baseDir, "extension_config.json");
    if (!File.Exists(configPath))
    {
        Console.WriteLine("未找到 extension_config.json（应位于 exe 同位置）。");
        Console.WriteLine("示例内容：{ \"modRoot\": \"F:/path/to/mod\" }");
        return Fail(noPause);
    }
    ExtensionConfig config;
    try
    {
        config = JsonSerializer.Deserialize<ExtensionConfig>(File.ReadAllText(configPath), jsonOpts) ?? new ExtensionConfig();
    }
    catch (Exception ex)
    {
        Console.WriteLine("extension_config.json 解析失败：" + ex.Message);
        return Fail(noPause);
    }
    if (string.IsNullOrWhiteSpace(config.ModRoot) || !Directory.Exists(config.ModRoot))
    {
        Console.WriteLine("extension_config.json 的 modRoot 无效或目录不存在：" + config.ModRoot);
        return Fail(noPause);
    }

    // ---- 第 2 层：_extension.json（模组 .smt/ 下，半隐藏）----
    string planPath = Path.Combine(config.ModRoot, ".smt", "_extension.json");
    if (!File.Exists(planPath))
    {
        Console.WriteLine("未找到 _extension.json（应位于 " + config.ModRoot + "/.smt/ 下）。");
        return Fail(noPause);
    }
    ExtensionPlan plan;
    try
    {
        plan = JsonSerializer.Deserialize<ExtensionPlan>(File.ReadAllText(planPath), jsonOpts) ?? new ExtensionPlan();
    }
    catch (Exception ex)
    {
        Console.WriteLine("_extension.json 解析失败：" + ex.Message);
        return Fail(noPause);
    }
    if (plan.Deployments.Count == 0)
    {
        Console.WriteLine("_extension.json 没有 deployments——无事可做。");
        return Fail(noPause);
    }

    // ---- 日志（exe 同位置 extension_debug.log）----
    LoggerSetup.Initialize(Path.Combine(baseDir, "extension_debug.log"));
    var logger = LoggerSetup.GetFactory().CreateLogger("Extension");

    // ---- 建 SA：roots 是读取源（不含写盘根也能工作）----
    var adapter = new StellarisAdapter();
    foreach (var root in plan.Roots)
    {
        if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
            adapter.AddRoot(root);
        else
            logger.LogWarning("跳过无效读取源: {Root}", root);
    }
    adapter.ScanAll();

    // ---- 执行 ----
    Console.WriteLine($"写盘根：{config.ModRoot}");
    Console.WriteLine($"读取源：{plan.Roots.Count} 个，部署：{plan.Deployments.Count} 轮");
    var runner = new ExtensionRunner(adapter, config.ModRoot, logger);
    var report = runner.Run(plan);

    Console.WriteLine();
    if (report.Failed > 0)
    {
        Console.WriteLine($"失败 {report.Failed}/{report.Total} 轮：");
        foreach (var err in report.Errors)
            Console.WriteLine("  ✗ " + err);
    }
    else
    {
        Console.WriteLine($"全部 {report.Total} 轮部署完成，无失败。");
    }

    Console.WriteLine();
    Console.WriteLine("全部完成。按任意键退出...");
    if (!noPause)
        Console.ReadKey();
    return 0;
}
catch (Exception ex)
{
    // 未捕获异常 → exe 旁 error.log（同 Editor 惯例）
    try
    {
        File.WriteAllText(Path.Combine(baseDir, "error.log"),
            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n{ex.StackTrace}");
    }
    catch { /* 日志写入失败不阻塞 */ }
    Console.WriteLine("发生未捕获异常：" + ex.Message);
    return Fail(noPause);
}

static int Fail(bool noPause)
{
    Console.WriteLine("按任意键退出...");
    if (!noPause)
        Console.ReadKey();
    return 1;
}
