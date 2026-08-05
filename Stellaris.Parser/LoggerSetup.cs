using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Stellaris.Parser
{
    /// <summary>
    /// 日志系统配置，支持：
    /// - 初始化（清空日志文件）
    /// - 日志队列（避免阻塞主线程）
    /// - 重试机制（失败重试次数和间隔）
    /// 日志文件：程序运行目录下的 editor_debug.log（可自定义路径）
    /// </summary>
    public static class LoggerSetup
    {
        private static ILoggerFactory? _factory;
        private static readonly object _lock = new();
        private static FileLogger? _fileLogger;

        public static ILoggerFactory GetFactory()
        {
            if (_factory is not null)
                return _factory;

            lock (_lock)
            {
                if (_factory is not null)
                    return _factory;

                // 默认日志路径
                string logPath = Path.Combine(AppContext.BaseDirectory, "editor_debug.log");
                _fileLogger = new FileLogger(logPath);
                _factory = LoggerFactory.Create(builder =>
                {
                    builder
                        .AddSimpleConsole(options =>
                        {
                            options.TimestampFormat = "[yyyy-MM-dd HH:mm:ss] ";
                            options.SingleLine = true;
                        })
                        .AddProvider(new FileLoggerProvider(_fileLogger))
                        .SetMinimumLevel(LogLevel.Information);
                });
                return _factory;
            }
        }

        /// <summary>
        /// 初始化日志系统：指定日志文件路径，并清空该文件。
        /// 如果未指定路径，则使用默认路径。
        /// </summary>
        /// <param name="logFilePath">日志文件路径（可选）</param>
        /// <param name="retryCount">失败后的额外重试次数（默认0）</param>
        /// <param name="retryDelayMs">重试间隔毫秒（默认0）</param>
        public static void Initialize(string? logFilePath = null, int retryCount = 0, int retryDelayMs = 0)
        {
            lock (_lock)
            {
                if (_factory is not null)
                {
                    _factory.Dispose();
                    _factory = null;
                }

                string path = logFilePath ?? Path.Combine(AppContext.BaseDirectory, "editor_debug.log");
                _fileLogger = new FileLogger(path);
                _fileLogger.Clear();
                _fileLogger.SetRetryPolicy(retryCount, retryDelayMs);

                _factory = LoggerFactory.Create(builder =>
                {
                    builder
                        .AddSimpleConsole(options =>
                        {
                            options.TimestampFormat = "[yyyy-MM-dd HH:mm:ss] ";
                            options.SingleLine = true;
                        })
                        .AddProvider(new FileLoggerProvider(_fileLogger))
                        .SetMinimumLevel(LogLevel.Information);
                });
            }
        }

        public static ILogger CreateLogger<T>() => GetFactory().CreateLogger<T>();
        public static ILogger CreateLogger(string categoryName) => GetFactory().CreateLogger(categoryName);
    }

    /// <summary>
    /// 文件日志提供程序
    /// </summary>
    public class FileLoggerProvider : ILoggerProvider
    {
        private readonly FileLogger _fileLogger;

        public FileLoggerProvider(FileLogger fileLogger)
        {
            _fileLogger = fileLogger;
        }

        public ILogger CreateLogger(string categoryName) => _fileLogger;

        public void Dispose() { }
    }

    /// <summary>
    /// 文件日志实现，支持队列和重试。
    /// </summary>
    public class FileLogger : ILogger
    {
        private readonly string _filePath;
        private readonly object _queueLock = new();
        private readonly ConcurrentQueue<LogEntry> _queue = new();
        private readonly CancellationTokenSource _cts = new();
        private Task _consumerTask;
        private bool _disposed;

        // 重试配置
        private int _retryCount = 0;
        private int _retryDelayMs = 0;

        public FileLogger(string filePath)
        {
            _filePath = filePath;
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            _consumerTask = Task.Run(ConsumerLoop);
        }

        /// <summary>
        /// 清空日志文件
        /// </summary>
        public void Clear()
        {
            lock (_queueLock)
            {
                _queue.Clear();
            }
            try
            {
                File.WriteAllText(_filePath, string.Empty);
            }
            catch { /* 忽略 */ }
        }

        /// <summary>
        /// 设置重试策略
        /// </summary>
        /// <param name="retryCount">重试次数（失败后额外尝试次数）</param>
        /// <param name="retryDelayMs">重试间隔（毫秒）</param>
        public void SetRetryPolicy(int retryCount, int retryDelayMs)
        {
            _retryCount = Math.Max(0, retryCount);
            _retryDelayMs = Math.Max(0, retryDelayMs);
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            string message = formatter(state, exception);
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            string fullMessage = $"[{timestamp}] {logLevel}: {message}";
            if (exception != null)
                fullMessage += $"\n{exception}";

            _queue.Enqueue(new LogEntry(fullMessage));
        }

        private async Task ConsumerLoop()
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                if (_queue.TryDequeue(out var entry))
                {
                    await WriteWithRetry(entry.Message);
                }
                else
                {
                    await Task.Delay(10, _cts.Token);
                }
            }

            while (_queue.TryDequeue(out var remaining))
            {
                WriteWithRetry(remaining.Message).Wait();
            }
        }

        private async Task WriteWithRetry(string message)
        {
            int attempt = 0;
            int maxAttempts = _retryCount + 1;
            do
            {
                try
                {
                    await File.AppendAllTextAsync(_filePath, message + "\n");
                    return;
                }
                catch
                {
                    attempt++;
                    if (attempt >= maxAttempts)
                    {
                        try
                        {
                            string errorMsg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ERROR: Failed to write log after {attempt} attempts: {message}";
                            await File.AppendAllTextAsync(_filePath, errorMsg + "\n");
                        }
                        catch { /* 彻底失败 */ }
                        return;
                    }
                    if (_retryDelayMs > 0)
                        await Task.Delay(_retryDelayMs);
                }
            } while (true);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cts.Cancel();
            try
            {
                _consumerTask.Wait(5000);
            }
            catch { }
            _cts.Dispose();
        }

        private record LogEntry(string Message);
    }
}