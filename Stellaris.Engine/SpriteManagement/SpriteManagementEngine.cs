// 文件: Stellaris.Engine/SpriteManagement/SpriteManagementEngine.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Stellaris.Engine.ImageAsset;
using Stellaris.Parser;

namespace Stellaris.Engine.SpriteManagement;

public sealed class SpriteManagementEngine : IDisposable
{
    private readonly StellarisAdapter _adapter;
    private readonly ImageAssetEngine _imageEngine;
    private readonly ILogger _logger;
    private readonly object _syncLock = new();

    private readonly ConcurrentDictionary<string, SpriteDefinition> _spriteIndex = new();

    /// <summary>gfx 规整化迁移的源文件（待清理：保存时写空头）；保存写回成功后清空。</summary>
    private readonly HashSet<string> _pendingCleanupGfx = new(StringComparer.OrdinalIgnoreCase);
    private readonly SpriteFrameCache _frameCache;
    private bool _indexBuilt;

    private SpriteOperationStatus _status = SpriteOperationStatus.Success;
    private string? _lastErrorMessage;

    public SpriteOperationStatus Status => _status;
    public string? LastErrorMessage => _lastErrorMessage;

    // ===== 构造函数：立即构建索引 =====
    public SpriteManagementEngine(StellarisAdapter adapter, ImageAssetEngine imageEngine,
                                  ILogger? logger = null, int cacheCapacity = 100)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _imageEngine = imageEngine ?? throw new ArgumentNullException(nameof(imageEngine));
        _logger = logger ?? NullLogger.Instance;
        _frameCache = new SpriteFrameCache(cacheCapacity);

        BuildIndex();
    }

    // ===== 索引构建 =====
    private void BuildIndex()
    {
        _spriteIndex.Clear();
        _logger.LogInformation("开始构建子图形内存索引...");

        var gfxFiles = _adapter.GetFilesRecursive("", "*.gfx");
        int parsedCount = 0;

        foreach (var gfxPath in gfxFiles)
        {
            try
            {
                var result = _adapter.GetConfig(gfxPath);
                if (result == null)
                {
                    _logger.LogWarning("无法获取 .gfx 文件内容: {Path}", gfxPath);
                    continue;
                }

                // 定位所有 spriteTypes 块
                var spriteTypesBlocks = result.RootNodes
                    .Where(n => n.Type == NodeType.Block && n.Key == "spriteTypes")
                    .ToList();

                foreach (var block in spriteTypesBlocks)
                {
                    foreach (var child in block.Children)
                    {
                        if (child.Type == NodeType.Block && child.Key == "spriteType")
                        {
                            string? sourceRoot = _adapter.GetFileRoot(gfxPath);
                            var def = ParseSpriteDefinition(child, gfxPath, sourceRoot);
                            if (def != null)
                            {
                                _spriteIndex[def.Name] = def;
                                parsedCount++;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "解析 .gfx 文件失败: {Path}", gfxPath);
            }
        }

        _logger.LogInformation("子图形内存索引构建完成，共 {Count} 个条目", parsedCount);
        _indexBuilt = true;
    }

    public void RebuildIndex()
    {
        lock (_syncLock)
        {
            _spriteIndex.Clear();
            _frameCache.Clear();
            BuildIndex();
            _logger.LogInformation("索引重建完成");
        }
    }

    // ===== 缓存管理 =====
    public void ClearFrameCache()
    {
        lock (_syncLock)
        {
            _frameCache.Clear();
            _logger.LogInformation("帧缓存已清空");
        }
    }

    // ===== 查询接口 =====
    public SpriteDefinition? GetSpriteDefinition(string name)
    {
        lock (_syncLock)
        {
            _status = SpriteOperationStatus.Success;
            _lastErrorMessage = null;

            try
            {
                if (string.IsNullOrEmpty(name))
                {
                    _status = SpriteOperationStatus.InvalidParameter;
                    _lastErrorMessage = "name 不能为空";
                    return null;
                }

                if (_spriteIndex.TryGetValue(name, out var def))
                    return def;

                _status = SpriteOperationStatus.SpriteNotFound;
                _lastErrorMessage = $"未找到子图形 '{name}'";
                return null;
            }
            catch (Exception ex)
            {
                _status = SpriteOperationStatus.UnknownError;
                _lastErrorMessage = $"GetSpriteDefinition 异常: {ex.Message}";
                _logger.LogError(ex, "GetSpriteDefinition 失败");
                return null;
            }
        }
    }

    /// <summary>
    /// 收集本 mod 内全部星系样式精灵（GFX_galaxy_*）所在的 gfx 文件（写回涉及文件，不做位置迁移）。
    /// 供保存时只写涉及文件；位置规整化（迁移）由 NormalizeSpriteFiles 单独执行。
    /// </summary>
    public HashSet<string> GetGalaxySpriteFiles()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string modRoot = _adapter.Roots.Count > 0 ? _adapter.Roots[^1] : string.Empty;
        foreach (var kv in _spriteIndex)
        {
            var def = kv.Value;
            if (def == null || string.IsNullOrEmpty(def.Name)
                || !def.Name.StartsWith("GFX_galaxy_", StringComparison.Ordinal))
                continue;
            if (def.SourceRoot != null
                && !string.Equals(def.SourceRoot, modRoot, StringComparison.OrdinalIgnoreCase))
                continue; // 外部 root 只读
            if (!string.IsNullOrEmpty(def.SourceFile))
                files.Add(def.SourceFile);
        }
        // 迁移源文件（待清理，保存时写空头）
        foreach (var f in _pendingCleanupGfx)
            files.Add(f);
        return files;
    }

    /// <summary>本 mod 星系精灵名中按前缀过滤（如 "GFX_galaxy_button_" → 按钮精灵名列表）。</summary>
    public List<string> GetGalaxySpriteNamesByPrefix(string prefix)
    {
        lock (_syncLock)
        {
            return _spriteIndex.Keys
                .Where(n => n.StartsWith(prefix, StringComparison.Ordinal))
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
        }
    }

    public IReadOnlyDictionary<string, string> GetAllSpriteNames()
    {
        lock (_syncLock)
        {
            _status = SpriteOperationStatus.Success;
            _lastErrorMessage = null;

            try
            {
                var result = new Dictionary<string, string>();
                foreach (var kv in _spriteIndex)
                    result[kv.Key] = kv.Value.SourceFile;
                return result;
            }
            catch (Exception ex)
            {
                _status = SpriteOperationStatus.UnknownError;
                _lastErrorMessage = $"GetAllSpriteNames 异常: {ex.Message}";
                _logger.LogError(ex, "GetAllSpriteNames 失败");
                return new Dictionary<string, string>();
            }
        }
    }

    // ===== CRUD 操作 =====

    public bool AddSprite(string gfxPath, string name, string textureFile,
              int? noOfFrames = null, OperationMode mode = OperationMode.Overwrite,
              List<AstNode>? additionalChildren = null)
    {
        lock (_syncLock)
        {
            _status = SpriteOperationStatus.Success;
            _lastErrorMessage = null;

            try
            {
                // 1. 参数校验
                if (string.IsNullOrEmpty(gfxPath))
                    throw new ArgumentException("gfxPath 不能为空", nameof(gfxPath));
                if (string.IsNullOrEmpty(name))
                    throw new ArgumentException("name 不能为空", nameof(name));
                if (string.IsNullOrEmpty(textureFile))
                    throw new ArgumentException("textureFile 不能为空", nameof(textureFile));
                if (!textureFile.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("textureFile 必须以 .dds 结尾", nameof(textureFile));

                // 2. 检查是否已存在
                string normalizedPath = NormalizeGfxPath(gfxPath);
                bool exists = _spriteIndex.ContainsKey(name);

                if (exists)
                {
                    switch (mode)
                    {
                        case OperationMode.Skip:
                            _logger.LogInformation("同名子图形 '{Name}' 已存在，跳过", name);
                            return true;
                        case OperationMode.Error:
                            _status = SpriteOperationStatus.SpriteAlreadyExists;
                            _lastErrorMessage = $"同名子图形 '{name}' 已存在";
                            return false;
                        case OperationMode.Overwrite:
                            _logger.LogInformation("同名子图形 '{Name}' 已存在，自动切换为完全覆盖更新", name);
                            return UpdateSprite(gfxPath, name, textureFile, noOfFrames, true, additionalChildren);
                        default:
                            _status = SpriteOperationStatus.InvalidParameter;
                            _lastErrorMessage = $"未知的 OperationMode: {mode}";
                            return false;
                    }
                }

                // 3. 新增流程
                var filteredChildren = FilterAdditionalChildren(additionalChildren);
                var parentPath = new List<object> { "spriteTypes" };
                var targetPath = new List<object> { "spriteTypes", ("name", name) };

                // 3a. 创建带 name 标识的 spriteType Block——3b 用 ("name", name) 定位
                //     时才能命中（空块无 name 会定位失败，导致新建文件无法添加精灵）。
                //     existingPredicate 按 name 判同：目标文件已有同名精灵时替换，否则添加。
                var newBlock = BuildBlockNode("spriteType", new List<AstNode>());
                newBlock.Children.Add(BuildSimpleNode("name", name, isQuoted: true));
                _adapter.AddConfigNode(normalizedPath, parentPath, newBlock, existingPredicate: SpriteByName(name));

                // 3b. 添加标准字段（name 已存在则更新为相同值，其余字段追加）
                _adapter.AddConfigNode(normalizedPath, targetPath, BuildSimpleNode("name", name, isQuoted: true));
                _adapter.AddConfigNode(normalizedPath, targetPath, BuildSimpleNode("texturefile", textureFile, isQuoted: true));
                if (noOfFrames.HasValue)
                    _adapter.AddConfigNode(normalizedPath, targetPath, BuildSimpleNode("noOfFrames", noOfFrames.Value));

                // 3c. 添加额外子节点
                if (filteredChildren != null)
                {
                    foreach (var node in filteredChildren)
                    {
                        if (node.Type == NodeType.Simple && !string.IsNullOrEmpty(node.Key) && node.Value != null)
                        {
                            // 追加到目标路径下的该 Key
                            var fieldPath = targetPath.Concat(new object[] { node.Key }).ToList();
                            _adapter.AddConfigNode(normalizedPath, fieldPath, node);
                        }
                        else if (node.Type == NodeType.Block || node.Type == NodeType.List)
                        {
                            // 将 Block/List 中的子节点合并到目标 Block（fullReplace=false）
                            _adapter.UpdateConfigNode(normalizedPath, targetPath, node, fullReplace: false);
                        }
                    }
                }

                // 4. 更新索引
                _spriteIndex.TryRemove(name, out _);
                var newDef = ParseSpriteDefinitionFromAdapter(normalizedPath, name);
                if (newDef != null)
                    _spriteIndex[name] = newDef;
                else
                {
                    // 防御性：如果读取失败，手动构造
                    newDef = new SpriteDefinition(name, textureFile, noOfFrames, normalizedPath,
                        filteredChildren?.AsReadOnly());
                    _spriteIndex[name] = newDef;
                }

                _logger.LogInformation("添加子图形 '{Name}' 到 {Path}", name, normalizedPath);
                return true;
            }
            catch (Exception ex)
            {
                _status = SpriteOperationStatus.UnknownError;
                _lastErrorMessage = $"AddSprite 异常: {ex.Message}";
                _logger.LogError(ex, "AddSprite 失败");
                return false;
            }
        }
    }

    public bool UpdateSprite(string gfxPath, string name,
                 string? newTextureFile = null, int? newNoOfFrames = null,
                 bool fullOverwrite = false, List<AstNode>? additionalChildren = null)
    {
        lock (_syncLock)
        {
            _status = SpriteOperationStatus.Success;
            _lastErrorMessage = null;

            try
            {
                if (string.IsNullOrEmpty(gfxPath))
                    throw new ArgumentException("gfxPath 不能为空", nameof(gfxPath));
                if (string.IsNullOrEmpty(name))
                    throw new ArgumentException("name 不能为空", nameof(name));
                if (newTextureFile != null && !newTextureFile.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("newTextureFile 必须以 .dds 结尾", nameof(newTextureFile));

                string normalizedPath = NormalizeGfxPath(gfxPath);
                bool exists = _spriteIndex.ContainsKey(name);

                if (!exists)
                {
                    if (!string.IsNullOrEmpty(newTextureFile))
                    {
                        _logger.LogInformation("子图形 '{Name}' 不在索引中，自动切换为添加", name);
                        return AddSprite(gfxPath, name, newTextureFile, newNoOfFrames,
                            OperationMode.Overwrite, additionalChildren);
                    }
                    else
                    {
                        _logger.LogDebug("子图形 '{Name}' 不在索引中且 newTextureFile 为空，无变更", name);
                        return true;
                    }
                }

                var existingDef = _spriteIndex[name];
                var targetPath = new List<object> { "spriteTypes", ("name", name) };
                var filteredChildren = FilterAdditionalChildren(additionalChildren);

                if (fullOverwrite)
                {
                    // 完全覆盖：清空所有子节点，重新构造
                    // 先删除整个 spriteType Block，再重新添加
                    _adapter.RemoveConfigNode(normalizedPath, targetPath);

                    // 重新创建 Block（先带 name 子节点——后续 ("name", name) 定位才能命中；
                    // 之前空块无 name 导致"父路径定位失败"：旧块已删、新块残缺 → 保存写回丢精灵）
                    var newBlock = BuildBlockNode("spriteType", new List<AstNode>());
                    newBlock.Children.Add(BuildSimpleNode("name", name, isQuoted: true));
                    _adapter.AddConfigNode(normalizedPath, new List<object> { "spriteTypes" }, newBlock,
                        existingPredicate: SpriteByName(name));

                    // 重新添加标准字段
                    string finalTexture = newTextureFile ?? existingDef.TextureFile;
                    int? finalNoOfFrames = newNoOfFrames;

                    _adapter.AddConfigNode(normalizedPath, targetPath, BuildSimpleNode("name", name, isQuoted: true));
                    _adapter.AddConfigNode(normalizedPath, targetPath, BuildSimpleNode("texturefile", finalTexture, isQuoted: true));
                    if (finalNoOfFrames.HasValue)
                        _adapter.AddConfigNode(normalizedPath, targetPath, BuildSimpleNode("noOfFrames", finalNoOfFrames.Value));

                    // 重新添加额外子节点
                    if (filteredChildren != null)
                    {
                        foreach (var node in filteredChildren)
                        {
                            if (node.Type == NodeType.Simple && !string.IsNullOrEmpty(node.Key) && node.Value != null)
                            {
                                var fieldPath = targetPath.Concat(new object[] { node.Key }).ToList();
                                _adapter.AddConfigNode(normalizedPath, fieldPath, node);
                            }
                            else if (node.Type == NodeType.Block || node.Type == NodeType.List)
                            {
                                _adapter.UpdateConfigNode(normalizedPath, targetPath, node, fullReplace: false);
                            }
                        }
                    }
                }
                else
                {
                    // 增量合并
                    if (newTextureFile != null)
                    {
                        var texPath = targetPath.Concat(new object[] { "texturefile" }).ToList();
                        _adapter.UpdateConfigNode(normalizedPath, texPath,
                            BuildSimpleNode("texturefile", newTextureFile, isQuoted: true), fullReplace: false);
                    }

                    if (newNoOfFrames.HasValue)
                    {
                        var framesPath = targetPath.Concat(new object[] { "noOfFrames" }).ToList();
                        _adapter.UpdateConfigNode(normalizedPath, framesPath,
                            BuildSimpleNode("noOfFrames", newNoOfFrames.Value), fullReplace: false);
                    }
                    else if (newNoOfFrames == null)
                    {
                        var framesPath = targetPath.Concat(new object[] { "noOfFrames" }).ToList();
                        _adapter.RemoveConfigNode(normalizedPath, framesPath);
                    }

                    // 处理 additionalChildren（增量合并）
                    if (filteredChildren != null)
                    {
                        foreach (var node in filteredChildren)
                        {
                            if (node.Type == NodeType.Simple && !string.IsNullOrEmpty(node.Key))
                            {
                                if (node.Value != null)
                                {
                                    var fieldPath = targetPath.Concat(new object[] { node.Key }).ToList();
                                    _adapter.UpdateConfigNode(normalizedPath, fieldPath, node, fullReplace: false);
                                }
                            }
                            else if (node.Type == NodeType.Block || node.Type == NodeType.List)
                            {
                                _adapter.UpdateConfigNode(normalizedPath, targetPath, node, fullReplace: false);
                            }
                        }
                    }
                }

                // 更新索引
                _spriteIndex.TryRemove(name, out _);
                var updatedDef = ParseSpriteDefinitionFromAdapter(normalizedPath, name);
                if (updatedDef != null)
                    _spriteIndex[name] = updatedDef;
                else
                {
                    string finalTex = newTextureFile ?? existingDef.TextureFile;
                    updatedDef = new SpriteDefinition(name, finalTex, newNoOfFrames, normalizedPath,
                        filteredChildren?.AsReadOnly());
                    _spriteIndex[name] = updatedDef;
                }

                _logger.LogInformation("更新子图形 '{Name}' 在 {Path}", name, normalizedPath);
                return true;
            }
            catch (Exception ex)
            {
                _status = SpriteOperationStatus.UnknownError;
                _lastErrorMessage = $"UpdateSprite 异常: {ex.Message}";
                _logger.LogError(ex, "UpdateSprite 失败");
                return false;
            }
        }
    }

    public bool RemoveSprite(string gfxPath, string name)
    {
        lock (_syncLock)
        {
            _status = SpriteOperationStatus.Success;
            _lastErrorMessage = null;

            try
            {
                if (string.IsNullOrEmpty(gfxPath))
                    throw new ArgumentException("gfxPath 不能为空", nameof(gfxPath));
                if (string.IsNullOrEmpty(name))
                    throw new ArgumentException("name 不能为空", nameof(name));

                if (!_spriteIndex.ContainsKey(name))
                {
                    _logger.LogWarning("子图形 '{Name}' 不在索引中，视为成功", name);
                    return true;
                }

                string normalizedPath = NormalizeGfxPath(gfxPath);
                var targetPath = new List<object> { "spriteTypes", ("name", name) };

                _adapter.RemoveConfigNode(normalizedPath, targetPath);

                _spriteIndex.TryRemove(name, out _);
                _logger.LogInformation("删除子图形 '{Name}' 从 {Path}", name, normalizedPath);
                return true;
            }
            catch (Exception ex)
            {
                _status = SpriteOperationStatus.UnknownError;
                _lastErrorMessage = $"RemoveSprite 异常: {ex.Message}";
                _logger.LogError(ex, "RemoveSprite 失败");
                return false;
            }
        }
    }

    public SpriteQueryResult QuerySprite(string name)
    {
        lock (_syncLock)
        {
            _status = SpriteOperationStatus.Success;
            _lastErrorMessage = null;

            try
            {
                if (string.IsNullOrEmpty(name))
                {
                    _status = SpriteOperationStatus.InvalidParameter;
                    _lastErrorMessage = "name 不能为空";
                    return SpriteQueryResult.NotFound(name);
                }

                if (!_spriteIndex.TryGetValue(name, out var def))
                {
                    _status = SpriteOperationStatus.SpriteNotFound;
                    _lastErrorMessage = $"未找到子图形 '{name}'";
                    return SpriteQueryResult.NotFound(name);
                }

                int frameCount = def.GetEffectiveFrameCount();
                if (frameCount <= 0) frameCount = 1;

                // 尝试从缓存获取所有帧
                var cachedFrames = new List<SpriteFrame>();
                bool allCached = true;
                for (int i = 0; i < frameCount; i++)
                {
                    if (_frameCache.TryGet(def.TextureFile, i, out var cachedFrame) && cachedFrame != null)
                        cachedFrames.Add(cachedFrame);
                    else
                    {
                        allCached = false;
                        break;
                    }
                }

                if (allCached && cachedFrames.Count == frameCount)
                {
                    var clonedFrames = new List<SpriteFrame>();
                    foreach (var frame in cachedFrames)
                    {
                        var clonedPixelSet = frame.PixelData.Clone();
                        var clonedFrame = new SpriteFrame(frame.Index, clonedPixelSet);
                        clonedFrames.Add(clonedFrame);
                    }
                    _logger.LogInformation("查询子图形 '{Name}' 缓存命中，帧数: {Count}", name, frameCount);
                    return SpriteQueryResult.Success(name, def.SourceFile, def.TextureFile,
                        clonedFrames, def.AdditionalChildren);
                }

                // 缓存未命中：加载图像并切分
                _imageEngine.LoadImage(def.TextureFile, null, null);
                if (_imageEngine.Status != OperationStatus.Success || _imageEngine.Result == null)
                {
                    _status = SpriteOperationStatus.ImageLoadError;
                    _lastErrorMessage = $"加载纹理失败: {def.TextureFile}，状态: {_imageEngine.Status}";
                    return SpriteQueryResult.NotFound(name);
                }

                var pixelSet = _imageEngine.Result.Clone();
                var workingSet = EnsureRGBA(pixelSet);
                if (!ReferenceEquals(workingSet, pixelSet))
                    pixelSet.Dispose();

                if (workingSet.Width % frameCount != 0)
                {
                    _status = SpriteOperationStatus.InvalidParameter;
                    _lastErrorMessage = $"图像宽度 {workingSet.Width} 不能被帧数 {frameCount} 整除";
                    workingSet.Dispose();
                    return SpriteQueryResult.NotFound(name);
                }

                int frameWidth = workingSet.Width / frameCount;
                var frames = new List<SpriteFrame>();
                var cachedFramesToStore = new List<SpriteFrame>();

                for (int i = 0; i < frameCount; i++)
                {
                    int startX = i * frameWidth;
                    var framePixelSet = ExtractFrame(workingSet, startX, 0, frameWidth, workingSet.Height);
                    var cacheFrame = new SpriteFrame(i, framePixelSet);
                    cachedFramesToStore.Add(cacheFrame);
                    var clonedPixelSet = framePixelSet.Clone();
                    var returnFrame = new SpriteFrame(i, clonedPixelSet);
                    frames.Add(returnFrame);
                }

                foreach (var cf in cachedFramesToStore)
                    _frameCache.Add(def.TextureFile, cf.Index, cf);

                workingSet.Dispose();

                _logger.LogInformation("查询子图形 '{Name}' 缓存未命中，重新加载并切分，帧数: {Count}", name, frameCount);
                return SpriteQueryResult.Success(name, def.SourceFile, def.TextureFile,
                    frames, def.AdditionalChildren);
            }
            catch (Exception ex)
            {
                _status = SpriteOperationStatus.UnknownError;
                _lastErrorMessage = $"QuerySprite 异常: {ex.Message}";
                _logger.LogError(ex, "QuerySprite 失败");
                return SpriteQueryResult.NotFound(name);
            }
        }
    }

    public async Task<SpriteQueryResult> QuerySpriteAsync(string name,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return QuerySprite(name);
        }, cancellationToken);
    }

    // ===== 内部辅助方法 =====

    private static string NormalizeGfxPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;
        if (!path.EndsWith(".gfx", StringComparison.OrdinalIgnoreCase))
            return path + ".gfx";
        return path;
    }

    private List<AstNode>? FilterAdditionalChildren(List<AstNode>? additionalChildren)
    {
        if (additionalChildren == null || additionalChildren.Count == 0)
            return null;

        var filtered = new List<AstNode>();
        foreach (var node in additionalChildren)
        {
            if (node.Type == NodeType.Simple && (node.Key == "name" || node.Key == "texturefile" || node.Key == "noOfFrames"))
            {
                _logger.LogWarning("忽略保留键 '{Key}' 在 additionalChildren 中", node.Key);
                continue;
            }
            filtered.Add(node);
        }
        return filtered.Count > 0 ? filtered : null;
    }

    private AstNode BuildSimpleNode(string key, object value, bool isQuoted = false)
    {
        return new AstNode
        {
            Type = NodeType.Simple,
            Key = key,
            Value = value,
            IsQuoted = isQuoted,
            OriginalLayout = OriginalLayout.SingleLine
        };
    }

    private AstNode BuildBlockNode(string key, List<AstNode> children)
    {
        return new AstNode
        {
            Type = NodeType.Block,
            Key = key,
            Children = children,
            OriginalLayout = OriginalLayout.MultiLine
        };
    }

    private SpriteDefinition? ParseSpriteDefinition(AstNode node, string sourceFile, string? sourceRoot = null)
    {
        if (node == null || node.Type != NodeType.Block || node.Key != "spriteType")
            return null;

        string? name = null;
        string? textureFile = null;
        int? noOfFrames = null;
        var additionalChildren = new List<AstNode>();

        foreach (var child in node.Children)
        {
            if (child.Type == NodeType.Simple)
            {
                switch (child.Key)
                {
                    case "name":
                        name = child.Value?.ToString();
                        break;
                    case "texturefile":
                        textureFile = child.Value?.ToString();
                        break;
                    case "noOfFrames":
                        if (int.TryParse(child.Value?.ToString(), out int frames))
                            noOfFrames = frames;
                        break;
                    default:
                        additionalChildren.Add(child);
                        break;
                }
            }
            else
            {
                additionalChildren.Add(child);
            }
        }

        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(textureFile))
            return null;

        return new SpriteDefinition(name, textureFile, noOfFrames, sourceFile,
            additionalChildren.Count > 0 ? additionalChildren.AsReadOnly() : null,
            sourceRoot);
    }

    private SpriteDefinition? ParseSpriteDefinitionFromAdapter(string gfxPath, string name)
    {
        var result = _adapter.GetConfig(gfxPath);
        if (result == null)
            return null;

        var spriteTypesBlocks = result.RootNodes
            .Where(n => n.Type == NodeType.Block && n.Key == "spriteTypes")
            .ToList();

        foreach (var block in spriteTypesBlocks)
        {
            foreach (var child in block.Children)
            {
                if (child.Type == NodeType.Block && child.Key == "spriteType")
                {
                    var def = ParseSpriteDefinition(child, gfxPath);
                    if (def != null && def.Name == name)
                        return def;
                }
            }
        }
        return null;
    }

    private PixelSet EnsureRGBA(PixelSet pixelSet)
    {
        if (pixelSet.Channels == 4)
            return pixelSet.Clone();

        if (pixelSet.Channels != 3)
            throw new InvalidOperationException($"不支持的通道数: {pixelSet.Channels}");

        int w = pixelSet.Width, h = pixelSet.Height;
        var newData = new byte[h][][];
        for (int y = 0; y < h; y++)
        {
            newData[y] = new byte[w][];
            for (int x = 0; x < w; x++)
            {
                var src = pixelSet.Data[y][x];
                newData[y][x] = new byte[4];
                newData[y][x][0] = src[0];
                newData[y][x][1] = src[1];
                newData[y][x][2] = src[2];
                newData[y][x][3] = 255;
            }
        }
        return new PixelSet(newData);
    }

    private PixelSet ExtractFrame(PixelSet source, int x, int y, int width, int height)
    {
        var newData = new byte[height][][];
        for (int row = 0; row < height; row++)
        {
            newData[row] = new byte[width][];
            for (int col = 0; col < width; col++)
            {
                newData[row][col] = (byte[])source.Data[y + row][x + col].Clone();
            }
        }
        return new PixelSet(newData);
    }

    // ===== IDisposable =====
    private bool _disposed;

    /// <summary>
    /// 保存全部精灵定义表（.gfx）——只写**本 mod 目录**内涉及的文件：
    /// 索引记录了每个精灵所在实体文件的相对路径（SourceFile）与所属目录（SourceRoot）。
    /// 规则（与本地化写回一致）：
    ///   - SourceFile 属于本 mod 目录（或为本 mod 新建、尚未扫描）→ 写 mod 目录；
    ///   - SourceFile 属于外部 root（游戏本体等）→ 只读不写、绝不复制，
    ///     覆盖性兼容由本 mod 自己的 .gfx（{modPrefix}_galaxy_shapes.gfx）声明达成。
    /// extraFiles：额外待写/待清理文件（如精灵迁移后的旧文件，写空头 spriteTypes）。
    /// </summary>
    public bool WriteAllSpriteDefinitions(HashSet<string>? extraFiles = null)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in _spriteIndex.Values)
        {
            if (!string.IsNullOrEmpty(def.SourceFile))
                files.Add(def.SourceFile);
        }
        if (extraFiles != null)
        {
            foreach (var f in extraFiles)
                files.Add(f);
        }

        string currentRoot = _adapter.Roots.Count > 0 ? _adapter.Roots[^1] : string.Empty;
        bool allOk = true;

        foreach (var file in files)
        {
            string? sourceRoot = _adapter.GetFileRoot(file);

            // 外部 root 的 .gfx：只读不写（本 mod 覆盖兼容，见方法注释）
            if (sourceRoot != null
                && !string.Equals(sourceRoot, currentRoot, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("跳过外部 root 的精灵表（只读，靠本 mod 覆盖）: {File}", file);
                continue;
            }

            // 本 mod 内文件（或新建文件）→ 写 mod 目录（无剩余精灵时为空 spriteTypes 头）
            if (!_adapter.WriteFile(file, currentRoot))
            {
                _logger.LogError("精灵定义表写入失败: {File} -> {Root}", file, currentRoot);
                allOk = false;
            }
        }
        // 写回完成：清空待清理标记（旧源文件已写空头）
        _pendingCleanupGfx.Clear();

        _logger.LogInformation("精灵定义表保存完成：{Count} 个 .gfx 文件（仅本 mod）", files.Count);
        return allOk;
    }

    /// <summary>
    /// 规整化精灵位置（仅内存，随保存落盘）：
    /// 本 mod 内所有星系样式精灵（名字以 GFX_galaxy_ 开头）应位于 targetGfxPath
    /// （interface/game_setup/{modPrefix}_galaxy_shapes.gfx，规范 14.5）。
    ///   - 精灵已在正确文件（mod 内）→ 待写；
    ///   - 精灵在 mod 内其他 .gfx（错误文件名，如历史遗留 setup.gfx / *_xxc.gfx）→
    ///     批量迁移到正确文件（合并进目标 spriteTypes，逐个从源删除），源文件待清理；
    ///   - 精灵在外部 root → 只读不迁移，由本 mod 在正确文件新建覆盖（覆盖性兼容）。
    /// 返回"需要写盘/清理的 gfx 文件集"，供 WriteAllSpriteDefinitions 落盘。
    /// 注意：必须**批量合并** spriteType（不能逐块 AddConfigNode——所有 spriteType 块
    /// Key 相同，AddConfigNode 会把后续块误判为同名 existing 而覆盖）。
    /// </summary>
    public HashSet<string> NormalizeSpriteFiles(string targetGfxPath)
    {
        var pendingGfx = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string modRoot = _adapter.Roots.Count > 0 ? _adapter.Roots[^1] : string.Empty;
        if (string.IsNullOrEmpty(modRoot))
            return pendingGfx;

        var toMigrate = new List<SpriteDefinition>();
        foreach (var kv in _spriteIndex)
        {
            var def = kv.Value;
            if (def == null || string.IsNullOrEmpty(def.Name)
                || !def.Name.StartsWith("GFX_galaxy_", StringComparison.Ordinal))
                continue; // 非星系样式精灵（如游戏 UI 精灵）不处理

            // 外部 root 的精灵：只读不迁移（靠本 mod 覆盖兼容）
            if (def.SourceRoot != null
                && !string.Equals(def.SourceRoot, modRoot, StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.Equals(def.SourceFile, targetGfxPath, StringComparison.OrdinalIgnoreCase))
            {
                // 已在正确文件 → 待写（mod 内）
                if (IsModFile(def.SourceFile, modRoot))
                    pendingGfx.Add(def.SourceFile);
                continue;
            }

            toMigrate.Add(def);
            pendingGfx.Add(def.SourceFile); // 源文件待清理（写空头）
            _pendingCleanupGfx.Add(def.SourceFile);
        }

        if (toMigrate.Count == 0)
            return pendingGfx;

        // 条件化迁移：逐个 AddConfigNode（existingPredicate 按 name 判同——
        // 目标文件已有同名 name 的 spriteType 则替换，否则添加），
        // 从源删除并更新索引。底层 AddConfigNode 现支持"已存在判定谓词"，
        // 不同 name 的 spriteType 不再被误判为同名 existing 而覆盖。
        try
        {
            foreach (var def in toMigrate)
            {
                var block = BuildBlockNode("spriteType", new List<AstNode>());
                block.Children.Add(BuildSimpleNode("name", def.Name, isQuoted: true));
                block.Children.Add(BuildSimpleNode("texturefile", def.TextureFile, isQuoted: true));
                if (def.NoOfFrames.HasValue)
                    block.Children.Add(BuildSimpleNode("noOfFrames", def.NoOfFrames.Value));
                if (def.AdditionalChildren != null)
                {
                    foreach (var c in def.AdditionalChildren)
                        block.Children.Add(c);
                }

                _adapter.AddConfigNode(targetGfxPath, new List<object> { "spriteTypes" }, block,
                    existingPredicate: SpriteByName(def.Name));
                _adapter.RemoveConfigNode(def.SourceFile, new List<object> { "spriteTypes", ("name", def.Name) });
                _spriteIndex[def.Name] = new SpriteDefinition(
                    def.Name, def.TextureFile, def.NoOfFrames, targetGfxPath, def.AdditionalChildren, modRoot);
                _logger.LogInformation("精灵迁移: {Name} {From} -> {To}", def.Name, def.SourceFile, targetGfxPath);
            }
            pendingGfx.Add(targetGfxPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "精灵批量迁移失败（继续保存）");
        }

        return pendingGfx;
    }

    /// <summary>
    /// "已存在判定"谓词：Block 第一层含 name = {name} 的 Simple 即视为目标 spriteType
    /// （供 AddConfigNode/UpdateConfigNode 的 existingPredicate/targetPredicate 使用）。
    /// </summary>
    /// <summary>
    /// 确保精灵表存在（规整化用）：按 (name, texturefile) 遍历——gfx 文件中缺失的 spriteType 补齐、
    /// texturefile 路径不对的修正。文件不存在时先创建空文件（内存 AST）。返回涉及（需写回）的 gfx 文件。
    /// </summary>
    /// <summary>
    /// 确保精灵表存在（规整化用）：按 (name, texturefile, noOfFrames) 遍历，逐个走 AddSprite（Overwrite）——
    /// 复用原有精灵写盘机制（含 noOfFrames 切分参数）：缺失的 spriteType 补齐、texturefile 不对的修正。
    /// 文件不存在时先创建空文件（内存 AST）。返回涉及（需写回）的 gfx 文件。
    /// </summary>
    public HashSet<string> EnsureGalaxySprites(
        IReadOnlyList<(string Name, string TextureFile, int? NoOfFrames)> sprites,
        string targetGfxPath)
    {
        var pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (sprites.Count == 0)
            return pending;

        // 文件不存在先创建空文件（内存 AST），AddSprite 才能定位 spriteTypes
        if (_adapter.GetConfig(targetGfxPath) == null)
            _adapter.CreateEmptyFileInMemory(targetGfxPath, FileCategory.Config);

        foreach (var (name, tex, noOfFrames) in sprites)
        {
            // 复用（样式 Y 引用与样式 X 相同的精灵名）→ 已存在则**跳过**：
            // 不创建新精灵、不覆盖 texturefile、不清空已有块（"第二个不能覆盖第一个"）。
            var existing = GetSpriteDefinition(name);
            if (existing != null)
            {
                _logger.LogDebug("精灵 '{Name}' 已存在（可能被复用），跳过——不创建、不覆盖", name);
                continue;
            }
            try
            {
                // 缺失才创建（AddSprite；同名已存在被上面跳过，不会走到覆盖更新）
                AddSprite(targetGfxPath, name, tex, noOfFrames, OperationMode.Overwrite);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "EnsureGalaxySprites 失败: {Name} ({Path})", name, targetGfxPath);
            }
        }

        pending.Add(targetGfxPath);
        return pending;
    }


    private bool AddNoOfFramesNode(AstNode block, int? noOfFrames)
    {
        if (!noOfFrames.HasValue || noOfFrames.Value <= 0)
            return false;
        bool exists = block.Children.Any(c => c.Type == NodeType.Simple && c.Key == "noOfFrames");
        if (exists)
            return false;
        block.Children.Add(BuildSimpleNode("noOfFrames", noOfFrames.Value.ToString(), isQuoted: false));
        return true;
    }

    private static Func<AstNode, bool> SpriteByName(string name)
        => node => node.Type == NodeType.Block
            && node.Children.Any(c => c.Type == NodeType.Simple
                && c.Key == "name" && Equals(c.Value, name));

    /// <summary>判断 .gfx 相对路径是否属于本 mod 目录（或为本 mod 新建、尚未扫描）。</summary>
    private bool IsModFile(string relPath, string modRoot)
    {
        if (string.IsNullOrEmpty(modRoot))
            return false;
        string? root = _adapter.GetFileRoot(relPath);
        return root == null || string.Equals(root, modRoot, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _frameCache.Dispose();
        _spriteIndex.Clear();
    }
}