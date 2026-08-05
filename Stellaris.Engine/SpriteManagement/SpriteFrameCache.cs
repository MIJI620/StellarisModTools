// 文件: Stellaris.Engine/SpriteManagement/SpriteFrameCache.cs

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Stellaris.Engine.ImageAsset;

namespace Stellaris.Engine.SpriteManagement;

/// <summary>
/// LRU 帧缓存，用于缓存已切分的 SpriteFrame 数据。
/// 线程安全。
/// </summary>
internal sealed class SpriteFrameCache : IDisposable
{
    private readonly int _capacity;
    private readonly ConcurrentDictionary<(string TextureFile, int FrameIndex), SpriteFrame> _cache;
    private readonly List<(string TextureFile, int FrameIndex)> _accessOrder;
    private readonly ReaderWriterLockSlim _lock;
    private bool _disposed;

    /// <summary>
    /// 创建新的帧缓存实例。
    /// </summary>
    /// <param name="capacity">最大缓存条目数，必须大于 0。默认 100。</param>
    public SpriteFrameCache(int capacity = 100)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "容量必须大于 0");

        _capacity = capacity;
        _cache = new ConcurrentDictionary<(string, int), SpriteFrame>();
        _accessOrder = new List<(string, int)>(capacity);
        _lock = new ReaderWriterLockSlim();
    }

    /// <summary>
    /// 尝试从缓存中获取指定帧。
    /// </summary>
    /// <param name="textureFile">纹理文件路径</param>
    /// <param name="frameIndex">帧索引</param>
    /// <param name="frame">若命中则返回对应的 SpriteFrame，否则为 null</param>
    /// <returns>是否命中缓存</returns>
    public bool TryGet(string textureFile, int frameIndex, out SpriteFrame? frame)
    {
        if (string.IsNullOrEmpty(textureFile))
            throw new ArgumentNullException(nameof(textureFile));
        if (frameIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(frameIndex));

        var key = (textureFile, frameIndex);
        if (_cache.TryGetValue(key, out var cachedFrame))
        {
            // 更新访问顺序
            _lock.EnterWriteLock();
            try
            {
                _accessOrder.Remove(key);
                _accessOrder.Add(key);
            }
            finally
            {
                _lock.ExitWriteLock();
            }
            frame = cachedFrame;
            return true;
        }

        frame = null;
        return false;
    }

    /// <summary>
    /// 将指定帧加入缓存。若缓存已满，则淘汰最久未使用的条目。
    /// </summary>
    /// <param name="textureFile">纹理文件路径</param>
    /// <param name="frameIndex">帧索引</param>
    /// <param name="frame">SpriteFrame 实例</param>
    public void Add(string textureFile, int frameIndex, SpriteFrame frame)
    {
        if (string.IsNullOrEmpty(textureFile))
            throw new ArgumentNullException(nameof(textureFile));
        if (frameIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        if (frame == null)
            throw new ArgumentNullException(nameof(frame));

        var key = (textureFile, frameIndex);

        _lock.EnterWriteLock();
        try
        {
            // 若已存在，更新顺序并替换（理论上不会发生，但以防万一）
            if (_cache.TryGetValue(key, out var old))
            {
                _accessOrder.Remove(key);
                old?.Dispose(); // 释放旧帧
                _cache[key] = frame;
                _accessOrder.Add(key);
                return;
            }

            // 若缓存已满，淘汰最久未使用的条目
            if (_cache.Count >= _capacity && _accessOrder.Count > 0)
            {
                var oldestKey = _accessOrder[0];
                _accessOrder.RemoveAt(0);
                if (_cache.TryRemove(oldestKey, out var evicted))
                {
                    evicted?.Dispose(); // 释放被淘汰的帧资源
                }
            }

            // 添加新条目
            if (_cache.TryAdd(key, frame))
            {
                _accessOrder.Add(key);
            }
            // 若添加失败（例如并发添加），则不重复处理
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// 清空缓存，释放所有帧资源。
    /// </summary>
    public void Clear()
    {
        _lock.EnterWriteLock();
        try
        {
            // 释放所有帧
            foreach (var frame in _cache.Values)
                frame?.Dispose();
            _cache.Clear();
            _accessOrder.Clear();
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// 获取当前缓存条目数（仅用于调试）。
    /// </summary>
    public int Count => _cache.Count;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Clear();
        _lock.Dispose();
    }
}