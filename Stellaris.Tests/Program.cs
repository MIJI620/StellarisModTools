// 文件: Stellaris.Tests/Program.cs
// 测试运行器：反射发现所有 [Test] 方法并执行，输出 PASS/FAIL 统计，
// 任一失败则进程返回非零退出码（便于 CI / 脚本调用）。

using System.Reflection;
using Stellaris.Tests;

var assembly = Assembly.GetExecutingAssembly();

var testMethods = assembly.GetTypes()
    .Where(t => t.IsClass && !t.IsAbstract && t.IsPublic)
    .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
        .Where(m => m.GetCustomAttribute<TestAttribute>() != null)
        .Select(m => (Type: t, Method: m)))
    .OrderBy(x => x.Type.Name)
    .ThenBy(x => x.Method.Name)
    .ToList();

if (testMethods.Count == 0)
{
    Console.WriteLine("未发现任何 [Test] 方法");
    return 0;
}

int pass = 0, fail = 0;
string? currentClass = null;

foreach (var (type, method) in testMethods)
{
    if (!string.Equals(currentClass, type.Name, StringComparison.Ordinal))
    {
        currentClass = type.Name;
        Console.WriteLine();
        Console.WriteLine($"== {type.Name} ==");
    }

    try
    {
        object? instance = Activator.CreateInstance(type);
        method.Invoke(instance, null);
        pass++;
        Console.WriteLine($"  PASS  {method.Name}");
    }
    catch (TargetInvocationException tie)
    {
        fail++;
        Console.WriteLine($"  FAIL  {method.Name}  ->  {tie.InnerException?.Message ?? tie.Message}");
    }
    catch (Exception ex)
    {
        fail++;
        Console.WriteLine($"  FAIL  {method.Name}  ->  {ex.Message}");
    }
}

Console.WriteLine();
Console.WriteLine($"共 {pass + fail} 项测试：通过 {pass}，失败 {fail}");
return fail == 0 ? 0 : 1;
