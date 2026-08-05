// 文件: Stellaris.Engine/ImageAsset/ImageAssetEngine.cs

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Stellaris.Engine.ImageAsset;

/// <summary>
/// 图像素材引擎（外观），严格遵循 ImageAssetSpecification 规范。
/// 所有公开方法线程安全。
/// </summary>
public sealed class ImageAssetEngine : IDisposable
{
    private readonly IReadOnlyList<string> _roots;
    private readonly ILogger _logger;
    private readonly object _syncRoot = new();

    // 内部组件
    private readonly ImageLoader _loader;
    private readonly ImageProcessor _processor;
    private readonly ImageExporter _exporter;

    // 运行时可变的内存检查开关
    private bool _currentMemoryCheck;

    // ===== 公开属性 =====
    public OperationStatus Status { get; private set; } = OperationStatus.Success;
    public PixelSet? Result { get; private set; }
    public (int X, int Y) RotatedCenter { get; private set; }

    // 公开只读属性（查询当前内存检查状态）
    public bool EnableMemoryCheck
    {
        get { lock (_syncRoot) return _currentMemoryCheck; }
    }

    public ImageAssetEngine(IReadOnlyList<string> roots, ILogger? logger = null, bool enableMemoryCheck = true)
    {
        _roots = roots ?? throw new ArgumentNullException(nameof(roots));
        _logger = logger ?? NullLogger.Instance;
        _currentMemoryCheck = enableMemoryCheck;

        _loader = new ImageLoader(_roots, _logger, enableMemoryCheck);
        _processor = new ImageProcessor(_logger, enableMemoryCheck);
        _exporter = new ImageExporter(_roots, _logger);
    }

    // ==================== 内存检查覆盖机制 ====================

    /// <summary>
    /// 临时覆盖内存检查开关。返回的 IDisposable 对象在 using 块结束时自动恢复原值。
    /// 线程安全，支持嵌套。
    /// </summary>
    /// <param name="enabled">临时启用的内存检查状态</param>
    /// <returns>用于恢复的 IDisposable 对象</returns>
    public IDisposable OverrideMemoryCheck(bool enabled)
    {
        lock (_syncRoot)
        {
            var oldValue = _currentMemoryCheck;
            _currentMemoryCheck = enabled;
            _loader.SetMemoryCheck(enabled);
            _processor.SetMemoryCheck(enabled);
            return new MemoryCheckOverride(this, oldValue);
        }
    }

    private sealed class MemoryCheckOverride : IDisposable
    {
        private readonly ImageAssetEngine _engine;
        private readonly bool _oldValue;
        private bool _disposed;

        public MemoryCheckOverride(ImageAssetEngine engine, bool oldValue)
        {
            _engine = engine;
            _oldValue = oldValue;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            lock (_engine._syncRoot)
            {
                _engine._currentMemoryCheck = _oldValue;
                _engine._loader.SetMemoryCheck(_oldValue);
                _engine._processor.SetMemoryCheck(_oldValue);
            }
        }
    }

    // ==================== 3.1 LoadImage ====================

    public async Task LoadImageAsync(string relativePath, ImageSize? outputSize = null,
        byte[]? backgroundColor = null, bool forceReload = false,
        CancellationToken cancellationToken = default)
    {
        await Task.Run(() => LoadImage(relativePath, outputSize, backgroundColor, forceReload), cancellationToken);
    }

    public void LoadImage(string relativePath, ImageSize? outputSize = null, byte[]? backgroundColor = null, bool forceReload = false)
    {
        lock (_syncRoot)
        {
            Status = OperationStatus.Success;
            Result = null;

            try
            {
                var pixelSet = _loader.LoadImage(relativePath, outputSize, backgroundColor, forceReload);
                Result = pixelSet;
                Status = OperationStatus.Success;
            }
            catch (NotSupportedException ex)
            {
                Status = OperationStatus.UnsupportedFormat;
                _logger.LogError(ex, "LoadImage 不支持的格式: {Path}", relativePath);
            }
            catch (OutOfMemoryException)
            {
                Status = OperationStatus.OutOfMemory;
                _logger.LogError("LoadImage 内存不足: {Path}", relativePath);
            }
            catch (Exception ex)
            {
                Status = OperationStatus.UnknownError;
                _logger.LogError(ex, "LoadImage 异常: {Path}", relativePath);
            }
        }
    }

    // ==================== 3.2 TransformImage ====================

    public void TransformImage(PixelSet pixelSet, List<TransformOperation> operations, ImageSize? outputSize = null)
    {
        lock (_syncRoot)
        {
            Status = OperationStatus.Success;
            Result = null;

            try
            {
                var result = _processor.Transform(pixelSet, operations, outputSize);
                Result = result;
                Status = OperationStatus.Success;
            }
            catch (ArgumentException)
            {
                Status = OperationStatus.InvalidParameter;
            }
            catch (OutOfMemoryException)
            {
                Status = OperationStatus.OutOfMemory;
            }
            catch (Exception ex)
            {
                Status = OperationStatus.UnknownError;
                _logger.LogError(ex, "TransformImage 异常");
            }
        }
    }

    // ==================== 3.3 RotateImage ====================

    public void RotateImage(PixelSet pixelSet, double angle, (int X, int Y)? pivot = null,
        bool autoExpand = true, byte[]? backgroundColor = null)
    {
        lock (_syncRoot)
        {
            Status = OperationStatus.Success;
            Result = null;
            RotatedCenter = (0, 0);

            try
            {
                var (result, center) = _processor.Rotate(pixelSet, angle, pivot, autoExpand, backgroundColor);
                Result = result;
                RotatedCenter = center;
                Status = OperationStatus.Success;
            }
            catch (ArgumentException)
            {
                Status = OperationStatus.InvalidParameter;
            }
            catch (OutOfMemoryException)
            {
                Status = OperationStatus.OutOfMemory;
            }
            catch (Exception ex)
            {
                Status = OperationStatus.UnknownError;
                _logger.LogError(ex, "RotateImage 异常");
            }
        }
    }

    // ==================== 3.4 StitchImages ====================

    public void StitchImages(List<PixelSet> pixelSets, int[][] grid, ImageSize cellSize,
        byte[]? backgroundColor = null, ImageSize? outputSize = null)
    {
        lock (_syncRoot)
        {
            Status = OperationStatus.Success;
            Result = null;

            try
            {
                var result = _processor.Stitch(pixelSets, grid, cellSize, backgroundColor, outputSize);
                Result = result;
                Status = OperationStatus.Success;
            }
            catch (ArgumentException)
            {
                Status = OperationStatus.InvalidParameter;
            }
            catch (OutOfMemoryException)
            {
                Status = OperationStatus.OutOfMemory;
            }
            catch (Exception ex)
            {
                Status = OperationStatus.UnknownError;
                _logger.LogError(ex, "StitchImages 异常");
            }
        }
    }

    // ==================== 3.5 CompositeImages ====================

    public void CompositeImages(List<PixelSet> pixelSets, List<Placement> placements,
        byte[]? backgroundColor = null, ImageSize? outputSize = null)
    {
        lock (_syncRoot)
        {
            Status = OperationStatus.Success;
            Result = null;

            try
            {
                var result = _processor.Composite(pixelSets, placements, backgroundColor, outputSize);
                Result = result;
                Status = OperationStatus.Success;
            }
            catch (ArgumentException)
            {
                Status = OperationStatus.InvalidParameter;
            }
            catch (OutOfMemoryException)
            {
                Status = OperationStatus.OutOfMemory;
            }
            catch (Exception ex)
            {
                Status = OperationStatus.UnknownError;
                _logger.LogError(ex, "CompositeImages 异常");
            }
        }
    }

    // ==================== 3.6 ExportImage ====================

    public void ExportImage(string relativePath, PixelSet pixelSet, ImageFormat format,
        ExportMode mode = ExportMode.DdsOnly,
        ImageSize? outputSize = null, byte[]? backgroundColor = null)
    {
        lock (_syncRoot)
        {
            Status = OperationStatus.Success;

            try
            {
                _exporter.Export(relativePath, pixelSet, format, mode, outputSize, backgroundColor);
                Status = OperationStatus.Success;
            }
            catch (ArgumentException)
            {
                Status = OperationStatus.InvalidParameter;
            }
            catch (OutOfMemoryException)
            {
                Status = OperationStatus.OutOfMemory;
            }
            catch (IOException ex)
            {
                Status = OperationStatus.IoError;
                _logger.LogError(ex, "ExportImage IO错误");
            }
            catch (Exception ex)
            {
                Status = OperationStatus.UnknownError;
                _logger.LogError(ex, "ExportImage 异常");
            }
        }
    }

    // ==================== 3.7 DeleteImage ====================

    public void DeleteImage(string relativePath)
    {
        lock (_syncRoot)
        {
            Status = OperationStatus.Success;

            try
            {
                _exporter.Delete(relativePath);
                Status = OperationStatus.Success;
            }
            catch (FileNotFoundException)
            {
                Status = OperationStatus.FileNotFound;
            }
            catch (IOException ex)
            {
                Status = OperationStatus.IoError;
                _logger.LogError(ex, "DeleteImage IO错误");
            }
            catch (Exception ex)
            {
                Status = OperationStatus.UnknownError;
                _logger.LogError(ex, "DeleteImage 异常");
            }
        }
    }

    // ==================== 缓存管理 ====================

    public void ClearCache()
    {
        lock (_syncRoot)
        {
            _loader.ClearCache();
            _logger.LogDebug("缓存已清空");
        }
    }

    // ==================== IDisposable ====================

    public void Dispose()
    {
        _loader.Dispose();
        GC.SuppressFinalize(this);
    }
}