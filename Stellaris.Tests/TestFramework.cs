// 文件: Stellaris.Tests/TestFramework.cs
// 零依赖微型测试框架：TestAttribute 标记测试方法，Assert 提供断言。
// 运行器（Program.cs）通过反射自动发现并执行所有 [Test] 方法。

using System;

namespace Stellaris.Tests;

/// <summary>
/// 标记一个测试方法。方法必须是无参的 public 实例方法，
/// 所在类必须有 public 无参构造函数。
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class TestAttribute : Attribute
{
}

/// <summary>
/// 断言工具。断言失败抛出 AssertionException，由运行器捕获并计为失败。
/// </summary>
public static class Assert
{
    public sealed class AssertionException : Exception
    {
        public AssertionException(string message) : base(message) { }
    }

    public static void True(bool condition, string message)
    {
        if (!condition)
            throw new AssertionException(message);
    }

    public static void False(bool condition, string message)
        => True(!condition, message);

    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertionException($"{message}（期望 {expected}，实际 {actual}）");
    }

    public static void NotNull(object? value, string message)
    {
        if (value == null)
            throw new AssertionException(message);
    }

    public static void Null(object? value, string message)
    {
        if (value != null)
            throw new AssertionException(message);
    }

    public static void Contains(string haystack, string needle, string message)
    {
        if (!haystack.Contains(needle, StringComparison.Ordinal))
            throw new AssertionException($"{message}（未找到片段 '{needle}'）");
    }
}
