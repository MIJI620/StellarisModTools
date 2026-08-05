# Stellaris Mod Tools — 功能索引（公开 API）

本文件是工具的功能索引：每个公开方法/函数 → 所在文件、输入参数、功能。
开发新功能时**优先复用表中已有模块**（用户要求：被验证通过的模块往死里用，不另起炉灶）。
所有写盘必须经引擎/适配器核心组件，禁止上层直接写文件；引擎层不直接操作底层。

> 生成日期：2026-08。以代码为准；修改 API 后请同步更新本文件。

## 目录
- [Stellaris.Engine](#stellarisengine)
- [Stellaris.Parser](#stellarisparser)
- [Stellaris.Editor](#stellariseditor)

## Stellaris.Engine

### `Stellaris.Engine/GalaxyMap/GalaxyMapEngine.cs`

- **SetCoordinatePrecision**(int digits) — 设置坐标精度（0~6，超出裁剪；内部始终 double，仅序列化格式化，规范 2.3）。
- **SetIdPadding**(int minDigits) — 设置 ID 最小补零位数（0 = 自动按总点数计算，规范 4.2）。
- **ScanAll**() — 从 adapter 加载所有 map/setup_scenarios/ 下的场景文件。
- **GetDynamicScenario**(string name) — 按名获取动态场景
- **GetStaticScenario**(string name) — 按名获取静态场景
- **GetAllScenarioNames**() — 获取全部场景名（动态+静态）
- **SaveDynamicScenario**(string name) — 保存指定动态场景到文件（原子写入）。静态场景请使用 SaveStaticScenario。
- **SaveStaticScenario**(string name) — 保存指定静态场景到文件。保存前执行 ID 重编号（4.2）与坐标固化（4.4）。
- **WritePendingFiles**() — 一次性落盘全部待保存地图文件（统一保存功能——用户显式触发——时调用）。 先写地图本地化文件，再写场景文件；成功后清空两张文件表。
- **SaveAllScenarios**() — 统一保存全部地图（动态 + 静态）：登记全部场景文件与地图相关本地化文件到待保存表，然后一次性落盘。 必须由用户显式触发；文件名 = 地图 key（{ScenarioDir}/{name}.txt）。
- **NormalizeLocalisation**() — 地图本地化规整化（仅内存，不落盘；保存时随统一保存写盘）： - 静态地图绑定样式键（{mapKey} / {mapKey}_desc，与占位/绑定样式同 key）→ style 文件（{prefix}_style_l_{lang}.yml）； - 动态地图地图名（自建名字，无 desc）→ map 文件（{prefix}_map_l_{lang}.yml）； - 静态地图恒星点名（自建）→ …
- **AddDynamicScenario**(DynamicScenario scenario) — 新增或覆盖一个动态场景（内存），随后调用 SaveDynamicScenario 落盘。
- **AddStaticScenario**(StaticScenario scenario) — 新增或覆盖一个静态场景（内存）。加载时同步注册伪样式（4.5）。
- **DeleteScenario**(string name) — 删除场景（内存 + 伪样式移除）。返回是否删除成功。
- **GetShapeOrder**(string mapName) — 获取形状顺序（勾选集）
- **SetShapeTableOrder**(string mapName, List<string> order) — 记录形状总表顺序（全部样式，含未勾选；拖拽排序后调用，重建形状页时使用）。
- **GetShapeTableOrder**(string mapName) — 形状总表顺序；无记录（未拖过）返回 null（调用方回退样式表顺序）。
- **SetShapeOrder**(string mapName, List<string> order) — 设置形状顺序（内存顺序，保存时按此顺序落盘；动态/静态地图均支持）。
- **ApplyPreferredOrder**(string mapName, List<string> preferredOrder) — 应用用户偏好顺序：与 SupportedShapes 取交集（按偏好顺序过滤），覆盖内存顺序。 严禁写入文件（除非 SyncOrderWithPreferred）。
- **SyncOrderWithPreferred**(string mapName, List<string> preferredOrder) — 一键排序：交集替换内存顺序并写入文件（覆盖文件顺序，规范 3.2c）。
- **GetEstimatedShape**(string mapName) — 静态地图大致形状：基于伪样式参数调用 GalaxyStyleEngine.GetShapePolygonsWithParameters 生成边界多边形（双向联动，规范 4.7）。
- **ApplyDefaultLockLocalisation**(IEnumerable<string>? presetNames) — 按"默认锁定本地化"预设名列表（galaxy.json 写死 huge/large/medium/small/tiny 等原版预设， 由上层注入）设置对应 key 的地图为锁定本地化。
- **RestoreMapFlags**(IReadOnlyDictionary<string, (bool Lock, bool Clear) — 恢复地图的"锁定本地化 / 清空文件"标志（从银河类别 galaxy.json 的 maps 节点读取，由上层注入）。
- **RestoreStaticStyleMapping**(IReadOnlyDictionary<string, string>? mapping) — 恢复静态地图 → 样式映射（从银河类别 galaxy.json 的 maps 节点读取，由上层注入； 绑定样式必须真实存在，否则忽略）。core_radius 是样式参数，从样式文件读取——恢复绑定后 以绑定样式的 core_radius_perc 刷新 StaticScenario.CoreRadiusPerc。
- **SetBoundStyle**(string mapName, string? styleName) — 绑定静态地图到已有样式：保存时该样式的图标/预览使用本图点集渲染。 styleName 为空 = 解绑；映射（mapName → styleName）供统一保存写入银河类别 galaxy.json。
- **GetBoundStyle**(string mapName) — 获取静态地图绑定的样式名（无绑定返回 null）。
- **GenerateShapePlaceholder**(string mapName) — 生成形状占位符（历史保留）：自动生成与地图同名的占位样式并记录映射。 新流程优先使用 SetBoundStyle（绑定已有样式）。
- **RenameDynamicScenario**(string oldName, string newName) — 重命名动态地图：更新内存字典 key + 本地化键迁移（{old} → {new}，各启用语言）。 列表位置不变（priority 不变）；地图键输入框失焦即改内存。
- **RenameStaticScenario**(string oldName, string newName) — 重命名静态地图：更新静态字典 + 同步改占位样式 key（同名）+ 更新内存映射。 映射（静态地图名 → 占位样式名）供保存时写入银河类配置。
- **Dispose**() — 释放资源

### `Stellaris.Engine/GalaxyMap/GalaxyMapEngine_Image.cs`

- **GeneratePointsFromImage**(string mapName, string imagePath, ImageGenerationOptions options) — 将 PNG 图像转换为静态地图星系坐标点集（规范 5.1 / 5.5）。 创建模式：生成点**追加**到当前 Systems（不覆盖、不合并），随后触发 ID 重编号与边界校验。 静态地图名 PNG 图像路径（绝对路径或相对当前工作目录） 转点阵参数
- **Add**(Vector2 p) — 新增条目（见代码）
- **HasNearby**(Vector2 p, double minDist) — 检查给定点附近（最小距离内）是否已有点（去重/碰撞检测）

### `Stellaris.Engine/GalaxyMap/GalaxyMapEngine_Lattice.cs`

- **PreviewLattice**(LatticeGenerationOptions options) — 预计算网格点集与航道（不写入内存，规范 4.8.4）。
- **ApplyLattice**(string mapName, LatticeGenerationOptions options) — 正式将网格点与航道写入静态地图（规范 4.8.4 / 4.8.3 步骤 5-7）。
- **GenerateLattice**(string mapName, LatticeGenerationOptions options) — 生成并写入（一步式，规范 4.8.2）。

### `Stellaris.Engine/GalaxyMap/GalaxyMapEngine_Static.cs`

- **ConnectToThreeNeighbors**(string mapName, string systemId) — 为指定系统创建到最近 3 个邻居的超空间航道。 点数不足 4 时抛 InvalidOperationException；系统不存在抛 KeyNotFoundException。
- **SetSystemCategory**(string mapName, string systemId, string category) — 设置系统类别（category 子节点）。类别为 null/空白时重置为 "normal"（规范 6.8）。
- **ExportAssets**(string mapName, bool forceRebuild = false) — 导出静态地图伪样式的预览与图标（伪样式已在 GalaxyStyleEngine 注册）。 使用默认 PreviewOptions / IconOptions。失败返回对应 OperationStatus。

### `Stellaris.Engine/GalaxyMap/GalaxyMapTypes.cs`

- **Clone**() — 深拷贝对象
- **Clone**() — 深拷贝对象
- **GetX**() — 取 X：固定值或随机范围中随机取（规范 2.2：保留范围块，使用时随机取）。
- **GetY**() — 获取Y（见代码）
- **GetZ**() — 获取Z（见代码）
- **Clone**() — 深拷贝对象
- **Clone**() — 深拷贝对象
- **Clone**() — 深拷贝对象
- **Apply**(double v) — 应用数值（按固定值/随机范围取实际值）
- **Clone**() — 深拷贝对象
- **Clone**() — 深拷贝对象
- **Clone**() — 深拷贝对象
- **Clone**() — 深拷贝对象
- **Clone**() — 深拷贝对象
- **Clone**() — 深拷贝对象
- **Clone**() — 深拷贝对象
- **Clone**() — 深拷贝对象

### `Stellaris.Engine/GalaxyMap/ScenarioParser.cs`

- **ParseDynamic**(AstNode block, string fileName) — 解析动态场景 AST 块 → DynamicScenario
- **ParseStatic**(AstNode block, string fileName) — 解析静态场景 AST 块 → StaticScenario

### `Stellaris.Engine/GalaxyMap/ScenarioSerializer.cs`

- **BuildDynamicRoot**(DynamicScenario s) — 把动态场景序列化为 AST 根节点（按精度格式化坐标）
- **BuildStaticRoot**(StaticScenario s, int precision) — 把静态场景序列化为 AST 根节点（按精度格式化坐标）

### `Stellaris.Engine/GalaxyStyle/GalaxyAssetExporter.cs`

- **ExportPreview**(string styleName, GalaxyShapeParameters parameters, PreviewOptions opts, List<Vector2>? staticPoints = null) — 导出单个样式预览图（可选静态点集渲染；失败返回状态）
- **ExportIcon**(string styleName, GalaxyShapeParameters parameters, IconOptions opts, List<Vector2>? staticPoints = null) — 导出单个样式按钮图标（3 帧横排；可选静态点集）

### `Stellaris.Engine/GalaxyStyle/GalaxyPointGenerator.cs`

- **GeneratePoints**(GalaxyShapeParameters param, float endRadius, int direction = 1) — 按样式参数生成恒星点集（方向 ±1）
- **PolygonArea**(List<Vector2> poly) — 计算多边形面积（鞋带公式）
- **Add**(Vector2 p) — 新增条目（见代码）
- **HasNearby**(Vector2 p, float minDist) — 检查给定点附近（最小距离内）是否已有点（去重/碰撞检测）

### `Stellaris.Engine/GalaxyStyle/GalaxyStyleEngine.cs`

- **SetStaticPointOverride**(string styleName, List<Vector2> points) — 设置样式导出时的静态点集覆盖（绑定样式由统一保存设置，导出后由上层清除）。
- **ClearStaticPointOverrides**() — 清除全部静态点集覆盖。
- **WriteStyleTableToDisk**() — 将内存样式表（含静态地图占位/绑定样式）写回 galaxy_shapes.txt（原子写入）。 供统一保存把静态地图同步创建的样式一起落盘——与 SaveAllStyles 使用同一写回机制，不设特例。
- **LoadAllStyles**() — 从适配器加载全部样式表
- **RefreshStyles**() — 重新加载样式表（清缓存重扫）
- **RefreshLocalisationCache**() — 刷新本地化缓存（修改后显示值同步）
- **SetEnabledLanguages**(IEnumerable<string>? langs) — 注入启用语种（来自模组偏好 ModPreferences.EnabledLanguages，与 ModPrefix 同级；null = 未设置）。
- **GetEnabledLanguages**() — 获取启用语言列表
- **GetLocalisedText**(string key, string? lang = null) — 按指定语言查询本地化文本（lang 缺省 english），供 UI 按当前界面语言显示。
- **GetLocalisedLogicalText**(string key, string? lang = null) — 获取本地化条目的逻辑值（原文，含 $var$ 占位；未展开）。
- **GetStyleSwitch**(string styleName, string kind) — 读取样式导出开关（银河类别 galaxy.json 的 styles.{name}.{kind}，kind = preview|icon）。 未设置返回 null（由 SaveAllStyles 回退规则决定）；未注入配置管理器返回 null。
- **SetStyleSwitch**(string styleName, string kind, bool value) — 设置样式导出开关（写入银河类别 galaxy.json 的 styles.{name}.{kind}）。 银河样式相关设置一律存此类别（规范 11.x）。
- **GetLocalisedTitle**(string styleName, string lang) — 获取LocalisedTitle（见代码）
- **GetLocalisedDescription**(string styleName, string lang) — 获取LocalisedDescription（见代码）
- **StyleLocalisationFile**(string lang) — 样式本地化（名字/描述键）的合规目标文件：localisation/{lang}/{prefix}_style_l_{lang}.yml。
- **NormalizeKeys**(string styleName) — 规整化单个样式（公共入口，刷新本地化缓存）。仅改内存，不落盘（保存由 SaveAllStyles 显式触发）。
- **EnsureGalaxySpriteTable**() — 批量规整化全部样式（只刷新一次本地化缓存，避免逐个全量重建导致卡顿）。 仅改内存，不落盘。 确保精灵表（规整化用）：按每个样式的 preview_icon/button_icon 引用查 gfx—— 缺失的 spriteType 补齐、texturefile 路径不对的修正。只改内存 AST，随保存落盘。
- **NormalizeAllKeys**() — 规整化全部样式本地化键（仅内存，不落盘）
- **ExportSinglePreview**(string styleName, PreviewOptions? options = null) — 规整化核心：把样式名/desc 键迁移到合规文件并修正图标字段。 pendingFiles（"lang\0相对路径"）收集待保存文件，供保存流程只写涉及文件。 导出单个样式预览图（静态点集覆盖时用点集渲染）。
- **ExportSingleIcon**(string styleName, IconOptions? options = null) — 导出单个样式图标（静态点集覆盖时用点集渲染）。
- **GetAllStyleNames**() — 获取全部样式名（按当前顺序）。
- **GetStyle**(string name) — 获取样式定义（参数）的克隆。
- **UpdateStyleParam**(string styleName, string paramPath, string? input) — 更新样式的单个参数（raw 输入文本；null/空 = 移除该参数）。
- **SetStyleIcons**(string styleName, string previewIcon, string buttonIcon) — 设置/更新（见代码）
- **DeleteStyle**(string name) — 删除条目（见代码）
- **ReorderStyles**(IReadOnlyList<string> order) — 重命名样式：更新样式 key（galaxy_shapes.txt 块名）与本地化键（样式名键、desc 键）。 desc 键若为 {oldName}_desc 则同步改为 {newName}_desc，其余语言本地化值保留。 按新顺序重排样式表（拖拽排序后调用；保存时按此顺序落盘）。
- **RenameStyle**(string oldName, string newName) — 重命名样式（内存 + 同步改名 gfx 精灵名等）
- **RegisterPlaceholderStyle**(string name, GalaxyShapeParameters parameters) — 注册/更新一个"占位样式"（合法样式条目，仅操作样式表）。 供 GalaxyMapEngine 注册静态地图的伪样式使用（规范 GalaxyMap 2.6 / 4.5）： - 仅新增或更新样式表条目（GalaxyStyleTable）， - **严禁**触碰本地化条目、本地配置或刷新本地化缓存， - 预览/图标/本地化由静态侧（GalaxyMap）全权负责。
- **UnregisterPlaceholderStyle**(string name) — 移除一个占位样式（仅操作样式表，不触碰本地化/配置）。 供 GalaxyMapEngine 在删除静态地图时移除对应伪样式。
- **GetEffectiveSwitches**(string styleName) — 解析样式的有效开关：styles.{name} 优先，缺失用 fallback 值（规范 4.3 步骤 3）。
- **SaveAllStyles**(bool useLocalConfig = false, bool? autoBuildIcons = null, bool? autoBuildPreviews = null) — 保存全部样式（规范第十二章，签名 12.1）。 是否启用本地配置（galaxy.json）驱动导出参数与样式独立开关。 为 false 或配置不可用时全部回退硬编码默认值（行为与旧版一致）。 图标总开关。true=开启、false=关闭； null=跟随配置（useLocalConfig 生效时逐样式由 icon 开关决定，否则按硬编码默认 false，与旧版一致）。 预览总开关，语义同 a…
- **SyncToLocalConfig**() — 手动将当前所有样式的有效开关状态同步回本地配置（规范 11.6）。 不依赖 sync_on_save 开关；仅更新 styles 节点，保留 global 节点不变。 回写失败仅记录 Error 日志，不影响调用方。
- **Dispose**() — 释放资源

### `Stellaris.Engine/GalaxyStyle/GalaxyStyleOptions.cs`

- **Merge**(PreviewOptions? external) — 合并配置（外部值覆盖当前值，返回新实例）
- **Merge**(IconOptions? external) — 合并配置（外部值覆盖当前值，返回新实例）

### `Stellaris.Engine/GalaxyStyle/GalaxyStyleTable.cs`

- **LoadFromAdapter**() — 从 SA 加载 galaxy_shapes.txt 到内存表
- **SaveToAdapter**() — 将内存表完整写回 galaxy_shapes.txt（原子写入）
- **GetStyle**(string name) — 获取样式定义克隆
- **AddStyle**(GalaxyStyleDefinition def, int index = -1) — 添加样式；index 为显示/落盘顺序的插入位置（-1 = 追加末尾）。
- **UpdateStyle**(string name, GalaxyShapeParameters newParams) — 更新样式参数（内存，保存时落盘）
- **DeleteStyle**(string name) — 删除条目（见代码）
- **RenameStyle**(string oldName, string newName, GalaxyShapeParameters newParams) — 重命名样式：更新字典 key、顺序列表与定义（Name 只读，重建定义）。
- **GetAllNames**() — 获取全部样式名（按 _styleOrder 顺序）
- **ReorderStyles**(IReadOnlyList<string> order) — 按新顺序重排样式（拖拽排序后调用）：只重排已存在的样式，缺失项保留在末尾。
- **BuildAllStyleBlocks**() — 生成全部样式块（按 _styleOrder 顺序，用于序列化）
- **BuildStyleBlock**(string name, GalaxyShapeParameters param) — 生成单个样式块（AST，序列化用）
- **SetStyleParam**(string styleName, string paramPath, string? input) — 按参数路径设置单个参数的原始输入。 输入 "@foo" / "@[foo + 1]"：识别为常量引用，内部经 adapter 自动解析求值， 运行时值写入强类型属性，原文保留在 RawInputs 中（写回时原样填回 "@"）。 输入普通文本：自动去除头尾多余双引号后按参数类型转换。

### `Stellaris.Engine/GalaxyStyle/GalaxyStyleTypes.cs`

- **Clone**() — 深拷贝（含 RawInputs 字典——MemberwiseClone 会共享同一字典实例，必须换新字典）

### `Stellaris.Engine/ImageAsset/ImageAssetEngine.cs`

- **OverrideMemoryCheck**(bool enabled) — 临时覆盖内存检查开关。返回的 IDisposable 对象在 using 块结束时自动恢复原值。 线程安全，支持嵌套。 临时启用的内存检查状态 用于恢复的 IDisposable 对象
- **Dispose**() — 释放资源
- **LoadImage**(string relativePath, ImageSize? outputSize = null, byte[]? backgroundColor = null, bool forceReload = false) — 加载图像（可缩放/背景色，带缓存）
- **TransformImage**(PixelSet pixelSet, List<TransformOperation> operations, ImageSize? outputSize = null) — 按操作列表变换图像
- **RotateImage**(PixelSet pixelSet, double angle, (int X, int Y) — 旋转图像
- **DeleteImage**(string relativePath) — 删除条目（见代码）
- **ClearCache**() — 清空缓存
- **Dispose**() — 释放资源

### `Stellaris.Engine/ImageAsset/ImageAssetRenderer.cs`

- **BitmapToPixelSet**(SKBitmap bitmap) — SKBitmap → PixelSet 转换
- **PixelSetToBitmap**(PixelSet pixelSet) — PixelSet → SKBitmap 转换
- **ResizeBitmap**(SKBitmap src, int newWidth, int newHeight) — 缩放 SKBitmap
- **ResizePixelSet**(PixelSet src, int newWidth, int newHeight) — 缩放 PixelSet
- **ApplyBackground**(PixelSet pixelSet, byte[] bgColor) — 标准 Alpha Over 合成：将 pixelSet 绘制到背景色上。 符合规范 2.3 和 3.1。
- **ApplyTransform**(PixelSet src, TransformOperation op, ImageSize? outputSize) — 应用单个变换操作
- **EncodeDds**(SKBitmap bitmap, ImageFormat format) — 编码为 DDS 图像

### `Stellaris.Engine/ImageAsset/ImageExporter.cs`

- **Delete**(string relativePath) — 删除条目（见代码）

### `Stellaris.Engine/ImageAsset/ImageLoader.cs`

- **ClearCache**() — 清空缓存
- **Dispose**() — 释放资源

### `Stellaris.Engine/ImageAsset/ImageProcessor.cs`

- **Transform**(PixelSet pixelSet, List<TransformOperation> operations, ImageSize? outputSize) — 变换图像（操作列表）

### `Stellaris.Engine/ImageAsset/ImageSize.cs`

- **Equals**(ImageSize other) — 尺寸相等比较
- **Deconstruct**(out int width, out int height) — 解构为 (width, height)

### `Stellaris.Engine/ImageAsset/PixelSet.cs`

- **Dispose**() — 释放资源
- **Clone**() — 深拷贝对象

### `Stellaris.Engine/LocalConfigManager/LocalConfigManagerEngine.cs`

- **Set**(string category, string key, object value) — 设置/更新（见代码）
- **Get**(string category, string key) — 获取（见代码）
- **Delete**(string category, string key) — 删除条目（见代码）
- **Exists**(string category, string key) — 检查配置项是否存在
- **Reload**(string category) — 重载指定类别配置
- **ReloadAll**() — 重载全部配置
- **SetBatch**(string category, IDictionary<string, object> values) — 设置/更新（见代码）

### `Stellaris.Engine/Localisation/LocalisationDictionaryEngine.cs`

- **GetLanguages**() — 可用语种列表（扫描到的全部语言）。
- **Query**(string? language, string? keyPattern, string? valuePattern) — 查询（只读）。language 为空或 "*" = 全部语种； keyPattern / valuePattern 为正则（可为空 = 不过滤；无效正则抛异常由 UI 提示）。

### `Stellaris.Engine/SpriteManagement/SpriteDefinition.cs`

- **GetEffectiveFrameCount**() — 获取有效帧数（若 NoOfFrames 有值则返回，否则返回 1）
- **Dispose**() — 释放资源
- **NotFound**(string name) — 创建未找到的查询结果
- **Dispose**() — 释放资源

### `Stellaris.Engine/SpriteManagement/SpriteFrameCache.cs`

- **TryGet**(string textureFile, int frameIndex, out SpriteFrame? frame) — 尝试从缓存中获取指定帧。 纹理文件路径 帧索引 若命中则返回对应的 SpriteFrame，否则为 null 是否命中缓存
- **Add**(string textureFile, int frameIndex, SpriteFrame frame) — 将指定帧加入缓存。若缓存已满，则淘汰最久未使用的条目。 纹理文件路径 帧索引 SpriteFrame 实例
- **Clear**() — 清空缓存，释放所有帧资源。
- **Dispose**() — 释放资源

### `Stellaris.Engine/SpriteManagement/SpriteManagementEngine.cs`

- **RebuildIndex**() — 重建索引
- **ClearFrameCache**() — 清空帧缓存（按钮/预览帧）
- **GetSpriteDefinition**(string name) — 按名获取精灵定义（texturefile 等）
- **GetGalaxySpriteFiles**() — 收集本 mod 内全部星系样式精灵（GFX_galaxy_*）所在的 gfx 文件（写回涉及文件，不做位置迁移）。 供保存时只写涉及文件；位置规整化（迁移）由 NormalizeSpriteFiles 单独执行。
- **RemoveSprite**(string gfxPath, string name) — 删除条目（见代码）
- **QuerySprite**(string name) — 按名查询精灵（含帧信息）
- **WriteAllSpriteDefinitions**(HashSet<string>? extraFiles = null) — 保存全部精灵定义表（.gfx）——只写**本 mod 目录**内涉及的文件： 索引记录了每个精灵所在实体文件的相对路径（SourceFile）与所属目录（SourceRoot）。 规则（与本地化写回一致）： - SourceFile 属于本 mod 目录（或为本 mod 新建、尚未扫描）→ 写 mod 目录； - SourceFile 属于外部 root（游戏本体等）→ 只读不写、绝不复制，…
- **NormalizeSpriteFiles**(string targetGfxPath) — 规整化精灵位置（仅内存，随保存落盘）： 本 mod 内所有星系样式精灵（名字以 GFX_galaxy_ 开头）应位于 targetGfxPath （interface/game_setup/{modPrefix}_galaxy_shapes.gfx，规范 14.5）。 - 精灵已在正确文件（mod 内）→ 待写； - 精灵在 mod 内其他 .gfx（错误文件名，如历史遗留 setup.gf…
- **Dispose**() — 释放资源

### `Stellaris.Engine/SystemInitializer/SystemInitializerEngine.cs`

- **GetAvailableInitializers**() — 扫描全部已加载的 common/solar_system_initializers/*.txt，收集星系预设（initializer）名。 经 StellarisAdapter 解析 AST（不直接读磁盘），取每个文件顶级 Block 的 key；返回去重排序列表。

## Stellaris.Parser

### `Stellaris.Parser/AstNodes.cs`

- **ToString**() — 节点/对象转字符串表示

### `Stellaris.Parser/ConstantResolver.cs`

- **SetLocal**(string name, object value) — 设置/更新（见代码）
- **SetGlobal**(string name, object value) — 设置/更新（见代码）
- **Resolve**(string name) — 解析常量（局部优先，回退全局）
- **ClearLocal**() — 清空局部常量
- **ClearGlobal**() — 清空全局常量

### `Stellaris.Parser/CsvMerger.cs`

- **MergeNode**(AstNode node) — CSV 合并节点（合并同名块）

### `Stellaris.Parser/CsvParser.cs`

- **Parse**(string filePath) — 解析源文本为 AST

### `Stellaris.Parser/ErrorEntry.cs`

- **ToString**() — 节点/对象转字符串表示

### `Stellaris.Parser/ExpressionEvaluator.cs`

- **EvaluateNode**(AstNode node) — 求值 AST 节点（表达式）
- **EvaluateValue**(object? value) — 求值裸值（常量/数字）

### `Stellaris.Parser/Lexer.cs`

- **NextToken**() — 取下一个 Token（词法）

### `Stellaris.Parser/LocalisationParser.cs`

- **ApplyReplacement**(Dictionary<string, string> dict, TextReplacer replacer) — 对已解析的字典执行一次迭代替换（用于阶段3二次替换） 使用全局常量字典，不进行自引用检测（因为已稳定）
- **Serialize**(string filePath, string lang, Dictionary<string, string> dict) — 将本地化字典序列化为标准的 YML 文件。 格式：l_{lang}: 开头，每行缩进一个制表符， key: "value"。 输出文件路径 语言标识（如 "english"） 本地化字典

### `Stellaris.Parser/LoggerSetup.cs`

- **GetFactory**() — 获取Factory（见代码）
- **Initialize**(string? logFilePath = null, int retryCount = 0, int retryDelayMs = 0) — 初始化日志系统：指定日志文件路径，并清空该文件。 如果未指定路径，则使用默认路径。 日志文件路径（可选） 失败后的额外重试次数（默认0） 重试间隔毫秒（默认0）
- **CreateLogger**(string categoryName) — 创建日志器（按类别）
- **CreateLogger**(string categoryName) — 创建日志器（按类别）
- **Dispose**() — 释放资源
- **Clear**() — 清空日志文件
- **SetRetryPolicy**(int retryCount, int retryDelayMs) — 设置重试策略 重试次数（失败后额外尝试次数） 重试间隔（毫秒）
- **IsEnabled**(LogLevel logLevel) — 检查日志级别是否启用
- **Dispose**() — 释放资源

### `Stellaris.Parser/Parser.cs`

- **Parse**() — 解析源文本为 AST

### `Stellaris.Parser/ScriptExpander.cs`

- **Expand**(AstNode node) — 展开脚本/常量引用

### `Stellaris.Parser/SerializationHelper.cs`

- **Serialize**(List<AstNode> nodes) — AST 节点列表序列化为文本
- **WriteFile**(string filePath, string content) — 写回指定文件（引擎经此落盘）
- **SerializeToFile**(string filePath, List<AstNode> nodes) — AST 节点列表序列化并写入文件

### `Stellaris.Parser/StellarisAdapter.cs`

- **AddRoot**(string root) — 新增条目（见代码）
- **GetFileRoot**(string relPath) — 返回文件（相对路径）所属的根目录；未收录返回 null。
- **FileExists**(string relativePath) — 检查相对路径文件是否存在
- **GetFilesInDirectory**(string relativeDirectory, string? searchPattern = null) — 获取FilesInDirectory（见代码）
- **GetFilesRecursive**(string relativeDirectory, string? searchPattern = null) — 获取FilesRecursive（见代码）
- **GetLocalisedText**(string key, string lang = "english") — 获取指定语言的本地化文本。 从 _localisationTable 中按 key 查询。
- **GetLocalisedLogicalText**(string key, string lang = "english") — 获取本地化条目的逻辑值（原文，可能含 $var$ 替换占位；未展开）。
- **GetLocalisationFiles**(string lang) — 获取指定语言下全部本地化文件的相对路径（去重）。 供引擎做"键从旧文件转移到规范文件"等操作（规整化）。
- **GetLocalisationFilePaths**(string lang) — 指定语言下**涉及写入**的全部文件（键的 CurrentPath 与 OldPath 并集）。 供保存时"统计目前/过去文件名 → 逐个写 CurrentPath 键值对"使用。
- **HasLocalisationKeysInPath**(string lang, string path) — 指定语言下，该文件是否存在 CurrentPath 匹配的键（用于判断"纯旧文件"）。
- **GetConfig**(string relPath) — 获取指定相对路径的 AST（ParserResult）
- **WriteFile**(string relPath, string? targetRoot = null) — 写回指定文件（引擎经此落盘）
- **WriteAllFiles**() — 一次性落盘全部待保存文件（文件表）
- **WriteLocalisation**(string lang, string fileName, string? targetRoot = null, bool writeIfEmpty = false) — 将内存中指定本地化文件的数据写入 YML 文件。 从 _localisationTable[lang] 中筛选 CurrentPath == targetPath 的条目。 写入成功后，将该文件所有条目的 OldPath 更新为 targetPath。 语言标识（如 "english"） YML 文件名（如 "mod_galaxy_shapes.yml"） 可选目标根目录，未指定则使用 Ro…

### `Stellaris.Parser/StellarisAdapter_CRUD.cs`

- **CreateEmptyFileInMemory**(string relativePath, FileCategory category) — 在指定的内存缓存中创建一个空的容器条目。 对于 Localisation 类型，此方法确保 _localisationTable 中存在该语言的字典。
- **RemoveConfigNode**(string relativePath, List<object> targetPath) — 删除指定文件 AST 中由路径定位到的节点。 若定位到多个节点，抛出 InvalidOperationException。
- **AddLocalisationEntry**(string lang, string path, string key, string value, string? root = null, string? oldPath = null) — 16.5.1 添加本地化条目。 若 key 已存在且新 root 优先级更高，则覆盖；否则抛出异常。
- **RemoveLocalisationEntry**(string lang, string path, string key) — 16.5.2 删除本地化条目。 仅当条目的 CurrentPath == path 时才删除，否则静默返回。
- **UpdateLocalisationEntry**(string lang, string path, string key, string newValue, string? root = null) — 16.5.3 更新本地化条目。 若 key 不存在，则自动转为添加。 若 key 存在，更新 Value，若 CurrentPath != path 则更新 CurrentPath。
- **AddLocalisationEntries**(string lang, string path, string root, Dictionary<string, string> entries) — 16.5.5 批量添加本地化条目（用于扫描阶段）。
- **RemoveLocalisationFile**(string lang, string path) — 16.5.6 删除指定路径对应的文件中的所有条目。

### `Stellaris.Parser/StellarisAdapter_Constants.cs`

- **GetGlobalConstant**(string name) — 查询全局常量（规范 10.1）。 name 为空抛 ArgumentException；不存在返回 null；线程安全（加锁）。
- **SetGlobalConstant**(string name, object value) — 修改全局常量并触发常量传播（规范 10.2）。 name 为空或 value 为 null / 非数字类型时抛 ArgumentException；线程安全（加锁）。
- **GetConstantReferences**(string name) — 查询引用指定全局常量的节点（规范 10.3）。 name 为空抛 ArgumentException；不存在返回 null；遍历时惰性清理失效弱引用；线程安全（加锁）。
- **CleanConstantIndex**() — 手动触发常量引用索引全量惰性清理（规范 4.5）。 移除所有 Target 为 null 或节点已不在任何 AST 中的弱引用。
- **ResolveConstantInput**(string? input) — 解析用户输入的常量文本（如 "@foo"、"@[foo + 1]"、"42"、"text"）， 返回求值后的逻辑值；无法解析的常量引用返回 null（调用方可保留原文写回，交由游戏端解析）。 供上层引擎（如 GalaxyStyleEngine）支持常量引用输入时调用；线程安全（加锁）。
- **Equals**(WeakReference<AstNode>? x, WeakReference<AstNode>? y) — 尺寸相等比较
- **GetHashCode**(WeakReference<AstNode> obj) — 获取HashCode（见代码）

### `Stellaris.Parser/StellarisAdapter_Scan.cs`

- **ScanAll**() — 扫描加载全部地图场景
- **Rescan**() — 重新扫描（加载配置/文件）
- **ExpandLocalisationKey**(string lang, string key) — 单键展开显示值：编辑逻辑值（原文）后调用，用该语言全部逻辑值作为原始字典， 把指定 key 的显示值（Value）重新展开（LogicalValue 保持不变）。

### `Stellaris.Parser/TextReplacer.cs`

- **Replace**(string text) — 替换文本中的所有 $var$ 占位符
- **SetVariable**(string key, object? value) — 添加或更新常量
- **ResetStableKeys**() — 重置稳定标记（用于新一轮迭代）

### `Stellaris.Parser/Token.cs`

- **ToString**() — 节点/对象转字符串表示

## Stellaris.Editor

### `Stellaris.Editor/Controls/ColorPickerControl.xaml.cs`

- **ApplyLocalisation**(UILocalisationManager loc) — 应用本地化文本（R/G/B/A 标签、选项卡、按钮等）；由调用方传入。

### `Stellaris.Editor/MainWindow.xaml.cs`

- **RefreshUIAfterLanguageChange**() — 界面语言切换后刷新整个界面（设置页"重载"按钮调用）： 更新窗口标题、重建导航与当前页面（保持选中项）。
- **ApplyUserFont**() — 应用偏好中的界面字体与字号（设置页切换后亦调用）。

### `Stellaris.Editor/ModPreferences.cs`

- **GetDefaultPath**(string modRoot) — 模组偏好文件路径：{modRoot}/.smt/user_prefs.json（点目录，游戏忽略）。
- **Load**(string modRoot) — 载入模组偏好；缺失/损坏回退默认（绝不抛异常）。
- **Save**(string modRoot) — 保存模组偏好（原子写入）。失败返回 false 并记录。
- **InferPrefixFromDescriptor**(string modRoot) — 从 descriptor.mod 的 name 推断模组前缀（如 "More Galaxy(Standard Edition)" → more_galaxy_standard_edition）。

### `Stellaris.Editor/Pages/DynamicMapPage.xaml.cs`

- **Refresh**() — 导航切到本页时刷新（重建当前地图表单，含理论上限——星系样式参数更新后同步）。
- **SetMap**(string mapName) — 静态地图页切换过来时选中指定动态地图（双向切换）。

### `Stellaris.Editor/Pages/GalaxyStylePage.xaml.cs`

- **ReloadStyles**() — 刷新样式列表（本地化名显示）与参数表单（重扫后调用）。

### `Stellaris.Editor/Pages/StaticMapPage.xaml.cs`

- **SetMap**(string mapName) — 动态地图页切换过来时选中指定静态地图（输出接口）。
- **Refresh**() — 导航切到本页时刷新（重读点精度设置等）。

### `Stellaris.Editor/StatusOverlay.xaml.cs`

- **SetStatus**(string mainText, string? subText = null) — 设置浮层文本（线程安全：自动切回 UI 线程）。
- **SetMain**(string mainText) — 设置主状态行（线程安全）。
- **ShowError**(string message) — 初始化失败：红色错误文本 + 显示退出按钮（用户确认后才退出）。

### `Stellaris.Editor/UILocalisationManager.cs`

- **Load**(string? directory = null) — 载入全部语言文件。目录缺失或为空时回退到默认语言（空表），记录调试信息； 单个文件损坏时跳过该语言并记录警告，不影响其他语言（规范 2.4 / 6.1 防御性）。 本地化目录；缺省为 exe 所在目录下的 localisation/。
- **Get**(string key, string? lang = null) — 取指定键的文本（规范 2.2）。回退链：当前语言 → 默认语言 → 键名。
- **Format**(string key, params object?[] args) — 取文本并格式化 {0}/{1} 占位符。
- **GetLanguageDisplayName**(string lang) — 语言标识（UI 语言如 zh-CN，或 mod 本地化语言如 simp_chinese）→ 该语言的自称 （endonym）：zh-CN → "简体中文"、en-US → "English"、french → "Français"。 优先 languages.json 声明；其次 lang.name.{code 小写} 键；未定义回退原标识。 用于**界面语言下拉**（设置页）。
- **GetLanguageDisplayNameLocalized**(string lang) — 语言标识 → **当前界面语言下的译名**（统一语种）：中文界面下 english → "英语"、simp_chinese → "简体中文"；英文界面下 → "English"/"Simplified Chinese"。 查 lang.name.{code 小写} 键（各语言文件按自身界面翻译）；未定义回退原标识。 用于**本地化编辑区的语种下拉**（星系样式"其他"选项卡）。
- **SetLanguage**(string lang) — 切换当前语言并触发 LanguageChanged（规范 2.2）。

### `Stellaris.Editor/UserPreferences.cs`

- **Load**(string? path = null) — 载入偏好设置（规范 3.3 防御性）：文件缺失 / 损坏 / 反序列化失败时 整体回退空偏好并记录警告，绝不抛异常。
- **Save**(string? path = null) — 保存偏好设置（规范 3.1 / 3.3）：原子写入（临时文件 + 重命名）。 保存失败仅记录警告，不影响当前会话。

### `Stellaris.Editor/ViewModels/MainViewModel.cs`

- **RefreshTitles**(EngineServices services) — 语言切换后刷新全部导航标题（页面内部文本刷新由各页自行处理）。
