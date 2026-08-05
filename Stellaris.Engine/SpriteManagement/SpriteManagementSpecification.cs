/*
 * ============================================================================
 * STELLARIS SPRITE MANAGEMENT ENGINE STANDARD SPECIFICATION (REVISION 3.0)
 * ============================================================================
 * 本规范为子图形管理引擎（SpriteManagementEngine）实现的唯一权威依据。
 * 所有实现必须严格遵循本规范定义的接口签名、数据结构、算法与边界条件。
 * 本规范在逻辑上优先于任何现有代码实现，所有实现偏差均视为缺陷。
 * ============================================================================
 *
 *
 * 术语定义
 * --------
 * - 子图形（Sprite）：.gfx 文件中 `spriteTypes > spriteType` 块定义的一条
 *   精灵声明，以 name 字段为唯一键。
 * - 纹理（Texture）：spriteType 的 texturefile 字段，指向一个 .dds 文件
 *   （相对路径），帧数据按水平方向等宽切分。
 * - 额外子节点（AdditionalChildren）：spriteType 块中除 name、texturefile、
 *   noOfFrames 之外的其余子节点（其他 Simple 字段、Block、List 等），
 *   原样保留并随 CRUD 写回。
 * - .gfx 文件路径（gfxPath）：相对路径，可省略 .gfx 后缀（引擎自动补全）。
 *
 *
 * 总体执行原则
 * ------------
 * 1. 所有公开方法通过 Status（SpriteOperationStatus）与 LastErrorMessage
 *    （string?）报告结果；布尔返回方法在失败时返回 false。
 * 2. 所有公开方法线程安全（内部 lock (_syncLock)）。
 * 3. 所有 .gfx 读写必须通过 StellarisAdapter 的内存 CRUD 接口
 *    （AddConfigNode / UpdateConfigNode / RemoveConfigNode），禁止直接
 *    操作磁盘文件；索引构建通过 adapter.GetConfig / GetFilesRecursive。
 * 4. 构造函数完成时立即构建内存索引（BuildIndex）。
 *
 *
 * 第一章：引擎概述与架构
 * ======================
 *
 * 1.1 引擎定位
 *     子图形管理引擎负责管理模组中 interface 目录（含子目录）下所有
 *     *.gfx 文件内的 spriteType 声明，提供索引构建、增删改查（CRUD）、
 *     帧查询与缓存。
 *     查询结果返回切分后的帧像素集合（PixelSet，来自 ImageAssetEngine）。
 *
 * 1.2 依赖关系
 *     - Stellaris.Parser.StellarisAdapter    ：.gfx 文件读取与内存 CRUD。
 *     - Stellaris.Engine.ImageAsset.ImageAssetEngine ：纹理加载（LoadImage）。
 *
 * 1.3 公开状态属性
 *     - SpriteOperationStatus Status { get; }：最近一次操作状态。
 *     - string? LastErrorMessage { get; }：最近一次操作的错误描述（成功时为 null）。
 *     每次公开方法调用开始先将 Status 置为 Success、LastErrorMessage 置 null。
 *
 * 1.4 构造函数
 *     SpriteManagementEngine(StellarisAdapter adapter, ImageAssetEngine imageEngine,
 *                            ILogger? logger = null, int cacheCapacity = 100)
 *     - cacheCapacity：帧缓存容量（必须 > 0，否则抛 ArgumentOutOfRangeException）。
 *     - 构造完成后立即调用 BuildIndex()。
 *
 *
 * 第二章：核心数据结构
 * ====================
 *
 * 2.1 精灵定义（SpriteDefinition）
 *     类型：public class SpriteDefinition（不可变）
 *       - string Name { get; }：name 字段值（唯一键）。
 *       - string TextureFile { get; }：texturefile 字段值（.dds 相对路径）。
 *       - int? NoOfFrames { get; }：noOfFrames 字段值；null 表示未声明。
 *       - string SourceFile { get; }：所在 .gfx 文件相对路径。
 *       - IReadOnlyList<AstNode>? AdditionalChildren { get; }：额外子节点。
 *       - int GetEffectiveFrameCount()：返回 NoOfFrames ?? 1。
 *     构造：Name / TextureFile / SourceFile 为 null 时抛 ArgumentNullException。
 *
 * 2.2 帧（SpriteFrame）
 *     类型：public sealed class SpriteFrame : IDisposable
 *       - int Index { get; }：帧索引（从 0 开始）。
 *       - PixelSet PixelData { get; }：该帧像素集合（RGBA）。
 *       - int Width / int Height：帧尺寸（= PixelData 尺寸）。
 *       - void Dispose()：释放 PixelData（幂等）。
 *     构造：pixelData 为 null 抛 ArgumentNullException。
 *
 * 2.3 查询结果（SpriteQueryResult）
 *     类型：public sealed class SpriteQueryResult : IDisposable
 *       - bool Found { get; }：是否找到。
 *       - string Name { get; }：查询键。
 *       - string? SourceFile / TextureFile：Found 时有效。
 *       - int FrameCount：实际帧数（成功时 ≥ 1，未找到时为 0）。
 *       - IReadOnlyList<SpriteFrame> Frames：按索引升序排列。
 *       - IReadOnlyList<AstNode>? AdditionalChildren：与定义同一引用。
 *       - static SpriteQueryResult NotFound(string name)：未找到结果。
 *       - static SpriteQueryResult Success(name, sourceFile, textureFile,
 *         frames, additionalChildren = null)：frames 为空或 sourceFile/
 *         textureFile 为空时抛 ArgumentException。
 *       - void Dispose()：释放全部帧（幂等）。
 *     注意：查询返回的帧是缓存副本的克隆；调用方持有结果的所有权，
 *     使用完毕后必须 Dispose 以释放 PixelSet。
 *
 * 2.4 操作状态（SpriteOperationStatus）
 *     枚举值：Success、FileNotFound、SpriteNotFound、SpriteAlreadyExists、
 *     InvalidParameter、IoError、ParseError、ImageLoadError、OutOfMemory、
 *     UnknownError。
 *     触发条件：
 *       - FileNotFound：.gfx 文件不存在（当前实现中索引构建跳过缺失文件）。
 *       - SpriteNotFound：查询/删除时名称不在索引中。
 *       - SpriteAlreadyExists：AddSprite 时名称已存在且 OperationMode.Error。
 *       - InvalidParameter：路径/名称为空、texturefile 非 .dds、帧数
 *         不能整除图像宽度等。
 *       - ImageLoadError：QuerySprite 时纹理加载失败。
 *       - UnknownError：其他未分类异常（含 CRUD 抛出的异常）。
 *
 * 2.5 操作模式（OperationMode）
 *     枚举值：Overwrite（同名已存在则完全覆盖）、Skip（同名已存在则跳过，
 *     视为成功）、Error（同名已存在则返回失败并置 SpriteAlreadyExists）。
 *
 *
 * 第三章：索引构建
 * ================
 *
 * 3.1 BuildIndex（构造时自动执行，RebuildIndex 时重新执行）
 *     流程：
 *       1) 清空 _spriteIndex。
 *       2) adapter.GetFilesRecursive("", "*.gfx") 获取全部 .gfx 文件。
 *       3) 对每个文件：adapter.GetConfig(gfxPath) 获取解析结果；
 *          定位所有顶层 Key == "spriteTypes" 的 Block。
 *       4) 对每个 spriteTypes 块中的 Key == "spriteType" 的 Block 子节点，
 *          调用 ParseSpriteDefinition 解析；成功则以 Name 为键存入索引。
 *       5) 单个文件解析失败记录 Error 日志并继续（不影响其他文件）。
 *       6) 解析完成置 _indexBuilt = true。
 *
 * 3.2 ParseSpriteDefinition（AST → SpriteDefinition）
 *     遍历 spriteType 块的直接子节点：
 *       - Simple 且 Key == "name"：Name = Value.ToString()。
 *       - Simple 且 Key == "texturefile"：TextureFile = Value.ToString()。
 *       - Simple 且 Key == "noOfFrames"：int.TryParse 成功则记录，失败忽略。
 *       - 其他 Simple 子节点及全部非 Simple 子节点：加入 AdditionalChildren。
 *     若 Name 或 TextureFile 为空 → 返回 null（该条目不进入索引）。
 *
 * 3.3 路径规范化（NormalizeGfxPath）
 *     若路径不以 .gfx 结尾（忽略大小写），自动追加 .gfx。
 *
 *
 * 第四章：公开接口
 * ================
 *
 * 4.1 索引重建（RebuildIndex）
 *     void RebuildIndex()：清空索引与帧缓存后重新 BuildIndex。
 *
 * 4.2 帧缓存清理（ClearFrameCache）
 *     void ClearFrameCache()：清空帧缓存并释放全部帧资源。
 *
 * 4.3 查询定义（GetSpriteDefinition）
 *     SpriteDefinition? GetSpriteDefinition(string name)
 *       - name 为空 → InvalidParameter，返回 null。
 *       - 索引中存在 → Success，返回定义引用。
 *       - 不存在 → SpriteNotFound，返回 null。
 *
 * 4.4 查询全部名称（GetAllSpriteNames）
 *     IReadOnlyDictionary<string, string> GetAllSpriteNames()
 *       返回 名称 → SourceFile 的只读副本；异常时返回空字典并置 UnknownError。
 *
 * 4.5 添加精灵（AddSprite）
 *     bool AddSprite(string gfxPath, string name, string textureFile,
 *                    int? noOfFrames = null, OperationMode mode = OperationMode.Overwrite,
 *                    List<AstNode>? additionalChildren = null)
 *     参数校验：
 *       - gfxPath / name / textureFile 为空 → InvalidParameter。
 *       - textureFile 必须以 .dds 结尾（忽略大小写）→ InvalidParameter。
 *     流程：
 *       1) normalizedPath = NormalizeGfxPath(gfxPath)。
 *       2) 若 name 已存在：按 mode 处理——Skip 返回 true；Error 置
 *          SpriteAlreadyExists 返回 false；Overwrite 转调
 *          UpdateSprite(gfxPath, name, textureFile, noOfFrames, true, additionalChildren)
 *          并返回其结果。
 *       3) 新增：FilterAdditionalChildren 过滤保留键（见 4.5-a）；
 *          通过 adapter 依次执行（spriteType 块的"已存在判定"统一按第一层
 *          name 字段：existingPredicate = Block 且第一层有 name={name} 的 Simple；
 *          目标文件已有同名精灵则替换，否则添加——不同 name 的 spriteType 互不覆盖）：
 *          a) AddConfigNode(path, ["spriteTypes"], 带 name 的 spriteType Block,
 *             existingPredicate: 按 name)；
 *          b) AddConfigNode(path, ["spriteTypes", ("name", name)], name)；
 *          c) AddConfigNode(..., texturefile)；
 *          d) noOfFrames 有值时 AddConfigNode(..., noOfFrames)；
 *          e) 每个过滤后的额外子节点：Simple 且有键有值 →
 *             AddConfigNode(path, ["spriteTypes", ("name",name), key], node)；
 *             Block/List → UpdateConfigNode(path, targetPath, node,
 *             fullReplace: false)。
 *       4) 从 adapter 重新解析该精灵定义更新索引；解析失败时用参数
 *          手动构造 SpriteDefinition 兜底。
 *
 * 4.5-b 底层 CRUD 条件化（StellarisAdapter）
 *     AddConfigNode / UpdateConfigNode 支持"限定条件"：
 *       - AddConfigNode(path, parentPath, newNode, existingPredicate = null)：
 *         existingPredicate 自定义"已存在判定"（默认 null = 按 Key 同名）；
 *         父节点下第一个满足谓词的节点视为已存在（转替换），无则添加。
 *       - UpdateConfigNode(path, targetPath, newNode, fullReplace, targetPredicate = null)：
 *         从定位结果中取第一个满足 targetPredicate 的节点为目标；
 *         找不到（或都不满足）→ 视为需要 Add（upsert，谓词作为 Add 的已存在判定）。
 *     语义（用户约定）：谓词按"Block 第一层子节点（Simple/List）符合检测标准"
 *     判定目标 Block（如 spriteType 按 name 字段），与 find 的块定位一致。
 *
 * 4.5-a 额外子节点过滤（FilterAdditionalChildren）
 *     遍历 additionalChildren，剔除 Key ∈ { "name", "texturefile",
 *     "noOfFrames" } 的 Simple 节点（记 Warning）；结果为 null/空时返回 null。
 *
 * 4.6 更新精灵（UpdateSprite）
 *     bool UpdateSprite(string gfxPath, string name,
 *                       string? newTextureFile = null, int? newNoOfFrames = null,
 *                       bool fullOverwrite = false,
 *                       List<AstNode>? additionalChildren = null)
 *     校验：同 AddSprite（newTextureFile 非空时必须以 .dds 结尾）。
 *     流程：
 *       1) 若 name 不在索引：
 *          a) newTextureFile 非空 → 自动转为 AddSprite（Overwrite）；
 *          b) newTextureFile 为空 → 记 Debug 日志，返回 true（无变更）。
 *       2) 若 fullOverwrite 为 true（完全覆盖）：
 *          a) RemoveConfigNode 删除整个 spriteType 块；
 *          b) 重新 AddConfigNode 空块 + name + texturefile（优先
 *             newTextureFile，否则沿用现有 TextureFile）+ noOfFrames
 *             （仅当 newNoOfFrames 有值）+ 过滤后的额外子节点
 *             （Simple 用 AddConfigNode 按键追加；Block/List 用
 *             UpdateConfigNode 合并）。
 *       3) 若 fullOverwrite 为 false（增量更新）：
 *          a) texturefile / noOfFrames 字段有值时用 UpdateConfigNode
 *             （fullReplace: false）就地更新；
 *          b) 额外子节点：Simple 且有键有值 → 按键更新；Block/List →
 *             UpdateConfigNode 合并（未提供的额外子节点保留）。
 *       4) 从 adapter 重新解析更新索引；失败时手动构造兜底。
 *
 * 4.7 删除精灵（RemoveSprite）
 *     bool RemoveSprite(string gfxPath, string name)
 *       - name 不在索引 → 记 Warning，返回 true（视为成功）。
 *       - 否则 RemoveConfigNode(path, ["spriteTypes", ("name", name)])
 *         删除该 spriteType 块，并从索引移除。
 *
 * 4.8 帧查询（QuerySprite）
 *     SpriteQueryResult QuerySprite(string name)
 *     流程：
 *       1) name 为空 → InvalidParameter，返回 NotFound(name)。
 *       2) 索引无此名称 → SpriteNotFound，返回 NotFound(name)。
 *       3) frameCount = GetEffectiveFrameCount()；≤ 0 时按 1 处理。
 *       4) 尝试从帧缓存取全部帧（键 = (TextureFile, FrameIndex)）；
 *          全部命中 → 克隆帧后返回 Success（缓存存原始帧，返回克隆，
 *          防止外部修改/释放污染缓存）。
 *       5) 缓存未命中：imageEngine.LoadImage(def.TextureFile, null, null)
 *          加载纹理；失败（Status ≠ Success 或 Result 为 null）→
 *          ImageLoadError，返回 NotFound(name)。
 *       6) 确保 RGBA（EnsureRGBA：3 通道补 Alpha=255；其他通道数抛异常）。
 *       7) 校验 workingSet.Width % frameCount == 0，否则 →
 *          InvalidParameter，返回 NotFound(name)。
 *       8) 水平切分：frameWidth = Width / frameCount；对 i in [0, frameCount)
 *          ExtractFrame(workingSet, i*frameWidth, 0, frameWidth, Height)。
 *       9) 全部帧存入缓存（原始帧），返回克隆帧列表。
 *     异步重载：Task<SpriteQueryResult> QuerySpriteAsync(name,
 *     CancellationToken cancellationToken = default)。
 *
 * 4.8-a EnsureRGBA（像素集合 RGBA 化）
 *     4 通道 → 直接克隆；3 通道 → 逐像素补 Alpha=255；其他 → 抛异常。
 *
 * 4.8-b ExtractFrame（帧提取）
 *     从源像素集合复制 (x, y, width, height) 矩形区域为新的 PixelSet。
 *
 *
 * 第五章：帧缓存（SpriteFrameCache）
 * =================================
 * 内部类，LRU 策略，线程安全（ReaderWriterLockSlim 写锁串行化修改）。
 *   - 键：(string TextureFile, int FrameIndex)。
 *   - bool TryGet(textureFile, frameIndex, out SpriteFrame? frame)：
 *     命中时更新访问顺序并返回 true；textureFile 为空或 frameIndex < 0
 *     抛异常。
 *   - void Add(textureFile, frameIndex, frame)：已存在则替换并释放旧帧；
 *     缓存满（Count ≥ capacity）则淘汰最久未使用条目（释放其资源）；
 *     新增成功后加入访问顺序尾。
 *   - void Clear()：释放全部帧资源并清空。
 *   - int Count：当前条目数（调试用）。
 *   - void Dispose()：Clear + 释放锁。
 *
 *
 * 第六章：错误处理与日志
 * ======================
 * 6.1 所有异常捕获后置 UnknownError 并记录 Error 日志（含方法名与消息），
 *     不向上层抛出（除构造参数校验）。
 * 6.2 同名跳过、索引外删除等可恢复情形记录 Information/Warning 级别。
 * 6.3 日志使用 Microsoft.Extensions.Logging 标准接口。
 *
 * ============================================================================
 * 规范结束
 * ============================================================================
 */

namespace Stellaris.Engine.SpriteManagement;
