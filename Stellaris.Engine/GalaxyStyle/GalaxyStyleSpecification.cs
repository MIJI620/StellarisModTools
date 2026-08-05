/*
 * ============================================================================
 * STELLARIS GALAXY STYLE ENGINE STANDARD SPECIFICATION (REVISION 3.0)
 * ============================================================================
 * 本规范为银河样式引擎（GalaxyStyleEngine）实现的唯一权威依据。
 * 所有实现必须严格遵循本规范中的数值定义、边界条件和业务流程。
 * 本规范包含完整的坐标映射、空间哈希、面积计算、渲染合成等全部算法，
 * 以及引擎的架构设计、职责边界、依赖关系、线程安全、错误处理等所有设计内容，
 * 并涵盖本地配置集成、样式独立开关等运行时覆盖机制。
 * 本规范在逻辑上优先于任何现有代码实现，所有实现偏差均视为缺陷。
 * ============================================================================
 *
 *
 * 术语定义
 * --------
 * 规范中使用的术语定义如下，贯穿全文：
 *
 * - 根目录（Root Directory）：模组文件系统的顶层目录路径。引擎按添加顺序维护
 *   一个根目录列表，列表末尾（索引最大）的目录优先级最高。所有文件读写均
 *   通过 StellarisAdapter 进行，引擎不直接操作磁盘。
 *
 * - 相对路径（Relative Path）：相对于某个根目录的路径，使用正斜杠 '/' 作为
 *   目录分隔符，不以 '/' 开头。引擎内部所有路径均为相对路径。
 *
 * - 优先级（Priority）：根目录在列表中的索引值，索引越大优先级越高。
 *   在文件查找、常量合并、本地化覆盖等场景中，高优先级条目覆盖低优先级条目。
 *
 * - 逻辑坐标（Logical Coordinates）：以星系中心为原点、范围为 [-500, 500] 的
 *   二维浮点数坐标系，单位与游戏内单位一致。所有点阵生成和几何计算均使用此坐标系。
 *
 * - 像素坐标（Pixel Coordinates）：以图像左上角为原点、整数表示的二维坐标系。
 *   用于所有渲染和图像操作。
 *
 * - 逻辑绘制画布（Logical Canvas）：所有点阵、光晕、星云计算的像素网格。
 *   固定为 500×500 像素，不可配置。
 *
 * - 有效内容视口（Content Viewport）：最终输出图片中，用于展示缩放后星系内容的
 *   矩形区域。它是逻辑画布等比例缩放后填入的区域。
 *
 * - 外部输出尺寸（Output Size）：最终保存到磁盘的完整图片尺寸（包含背景、边框等）。
 *
 * - 本地配置（Local Config）：通过 LocalConfigManager 存储的用户偏好和运行时覆盖
 *   设置，独立于游戏数据，保存在 `galaxy.json` 文件中。引擎可在保存流程中
 *   选择是否读取和回写这些配置。
 *
 * - 样式独立开关（Style-Specific Switch）：针对每个样式单独控制的布尔值设置，
 *   包括是否生成预览图、是否生成图标、是否规范化键名。这些开关存储在本地配置
 *   的 `styles.{name}` 节点下，优先级高于全局回退值。
 *
 *
 * 总体执行顺序（强制，不可调整）
 * ------------------------------
 * 整个引擎生命周期分为七个核心阶段，执行顺序严格如下。
 * 任何实现若调整阶段顺序，均视为对本规范的违反。
 *
 * 阶段 0（引擎初始化）：
 *   接收 StellarisAdapter、ImageAssetEngine、SpriteManagementEngine 和模组前缀
 *   （可选接收 IConfigManager，见 11.1），
 *   初始化样式表（GalaxyStyleTable）和资产导出器（GalaxyAssetExporter），
 *   立即执行阶段 1。
 *
 * 阶段 1（样式表加载）：
 *   通过 StellarisAdapter 读取 `map/galaxy/galaxy_shapes.txt`，解析所有样式块，
 *   填充内存样式表字典。同时通过适配器的本地化接口加载所有语言的本地化数据，
 *   填充本地化缓存。此阶段在构造函数中自动完成。
 *
 * 阶段 2（样式表重载）：
 *   当调用 RefreshStyles() 时，清空所有内存状态（样式表、本地化缓存），
 *   重新执行阶段 1 的全部流程，确保与磁盘完全同步。
 *
 * 阶段 3（样式表保存与导出准备）：
 *   当调用 SaveAllStyles() 时，先将内存样式表序列化并通过适配器写回
 *   `galaxy_shapes.txt`（原子写入），然后根据参数和本地配置决定是否
 *   执行后续导出。若启用本地配置，则写盘前先执行阶段 4 的配置读取
 *   （与第十二章步骤 1/2 顺序一致）。
 *
 * 阶段 4（本地配置读取）：
 *   若 SaveAllStyles 的 useLocalConfig 参数为 true，则从 LocalConfigManager
 *   读取 `galaxy.json` 中的全局公共参数和样式独立开关。若读取失败或文件
 *   不存在，则静默降级，所有参数使用硬编码默认值。
 *   实际执行时机：阶段 3 的写盘动作之前（见第十二章步骤 1）。
 *
 * 阶段 5（资产导出）：
 *   根据全局开关（autoBuildIcons / autoBuildPreviews）和样式独立开关
 *   （preview / icon）决定是否对每个样式执行预览图生成和图标生成。
 *   生成时使用阶段 4 解析出的公共渲染参数（尺寸、颜色、密度等）。
 *
 * 阶段 6（规范化与同步）：
 *   对每个样式，若其 normalize 开关为 true，则执行 NormalizeKeys 操作
 *   （统一 desc 键、图标路径）。若全局行为 sync_on_save 为 true，
 *   则将当前所有样式的实际开关状态写回本地配置，实现配置同步。
 *
 * 阶段 7（结束）：
 *   返回 SaveResult 统计信息（成功/失败数量、错误列表）。
 *
 *
 * 第一章：引擎概述与架构设计
 * ==========================
 *
 * 1.1 引擎定位
 *     银河样式引擎是 Stellaris.Engine 层的业务组件，负责管理群星模组中
 *     `map/galaxy/galaxy_shapes.txt` 文件内的所有星系样式定义，并提供样式表的
 *     增删改查、点阵生成、预览渲染、图标生成、资产导出、几何查询等完整功能。
 *     引擎还集成了本地配置管理，允许用户按样式独立控制导出行为，并自定义
 *     全局渲染参数。
 *
 * 1.2 负责范围
 *     - 样式表管理：加载、保存（完整文件写入）、增删改查。
 *     - 点阵生成：根据样式参数（旋臂、环、半径等）计算恒星坐标。
 *     - 预览渲染：生成星系预览图（尺寸可自定义，默认 562×236，内部渲染 200×200）。
 *     - 按钮图标生成：生成三态按钮图标（尺寸可自定义，默认 110×59，内部掩码 35×35）。
 *     - 资产导出：调用 ImageAssetEngine 导出 .dds/.png，调用 SpriteManagementEngine
 *       注册精灵，调用 StellarisAdapter 写入本地化。
 *     - 几何查询：对外提供样式生成区域的多边形表示，用于 UI 可视化或其他外部用途。
 *     - 本地配置集成：在保存流程中读取用户预设的全局参数和样式独立开关，
 *       并可选地将当前状态同步回本地配置。
 *
 * 1.3 依赖关系（强制，不可绕过）
 *     - Stellaris.Parser.StellarisAdapter         ：所有文件读写（.txt / .yml）的唯一入口。
 *     - Stellaris.Engine.ImageAsset.ImageAssetEngine ：所有图像解码、缩放、合成、导出的唯一入口。
 *     - Stellaris.Engine.SpriteManagement.SpriteManagementEngine ：所有 .gfx 精灵声明增删改查的唯一入口。
 *     - Stellaris.Engine.LocalConfigManager.IConfigManager ：本地配置读取与写入的唯一入口（可选，若未提供则禁用本地配置功能）。
 *
 * 1.4 线程安全
 *     引擎实例应为单例或由上层确保线程安全。所有公开方法必须使用锁
 *     （如 `lock (_syncRoot)`）保护内部状态。内部状态包括：样式表字典、
 *     本地化缓存、任务状态等。
 *
 * 1.5 任务状态追踪
 *     引擎在执行耗时操作（如导出、保存）时应通过事件或属性通知上层当前任务类型。
 *     任务类型包括：
 *         Idle, LoadingStyles, SavingStyles, ExportingPreview, ExportingIcon,
 *         ExportingAll, ComputingAreas（预留）
 *     上层可根据任务类型显示进度或状态信息。
 *
 * 1.6 加载与重载行为（Load & Reload）
 *     a) 初始化加载（LoadAllStyles）：
 *        引擎构造或首次调用 LoadAllStyles() 时，必须执行以下操作：
 *          1) 通过 StellarisAdapter 读取 `map/galaxy/galaxy_shapes.txt` 内容，
 *             解析所有样式块，填充内部样式表字典。
 *          2) 通过 StellarisAdapter 的本地化接口获取所有语言的本地化数据
 *            （来自 localisation/ 目录），填充本地化缓存。
 *          3) 将每个样式的本地化名称和描述（从缓存中读取）赋值给对应的
 *             样式定义属性。
 *     b) 重载（RefreshStyles）：
 *        当调用 RefreshStyles() 时，引擎必须清空样式表字典和本地化缓存，
 *        然后重新执行与初始化加载完全相同的流程，以确保内存数据与磁盘文件一致。
 *
 *
 * 第二章：核心数据结构
 * ====================
 *
 * 2.1 样式定义（GalaxyStyleDefinition）
 *     表示内存样式表中的一条记录。
 *     - Name (string)                     ：样式唯一标识符（如 "spiral_2"）。
 *     - Parameters (GalaxyShapeParameters)：几何与渲染参数（见 2.2）。
 *     - LocalisedName (string?)           ：从 SA 获取的当前语言本地化名称（只读）。
 *     - LocalisedDescription (string?)    ：从 SA 获取的当前语言本地化描述（只读）。
 *
 * 2.2 形状参数（GalaxyShapeParameters）
 *     直接对应 `galaxy_shapes.txt` 中样式块内的字段。
 *     所有标量参数均为值类型；另含 RawInputs（Dictionary&lt;string,string&gt;，
 *     引用类型）用于保留常量引用原文。提供 Clone() 方法进行深拷贝
 *     （含 RawInputs 字典）。范围标注（如 [0,1]）仅为常规取值描述，
 *     解析与设置不强制校验，渲染/计算时按需收敛（如 core_ratio =
 *     min(CoreRadiusPerc, 0.5)）。
 *     - CoreRadiusPerc (double)           ：核心半径比例，范围 [0,1]。
 *     - StarsMinDist (double)             ：恒星最小间距（用于密度计算）。
 *     - NumStarsCorePerc (double)         ：核心恒星比例，范围 [0,1]（保留兼容）。
 *     - CountriesIdealDist (int)          ：普通帝国理想距离平方。
 *     - CountriesMinDist (int)            ：普通帝国最小距离平方。
 *     - FallenIdealDist (int)             ：堕落帝国理想距离平方。
 *     - FallenMinDist (int)               ：堕落帝国最小距离平方。
 *     - NumArms (int)                     ：旋臂数量（0 表示无旋臂）。
 *     - Tightness (double)                ：缠绕度（从原点到终止半径的总圈数）。
 *     - WidthDeg (double)                 ：臂宽度（度）。
 *     - Fuzz (double)                     ：散乱度（保留兼容，实际渲染中已弃用）。
 *     - ArmAngleDeg (double)              ：臂间夹角（度）。
 *     - HasRing (bool)                    ：是否包含环。
 *     - RingWidth (double)                ：环宽度比例（相对于 EndRadius）。
 *     - RingOffset (double)               ：环偏移比例（环内径位置）。
 *     - PreviewIcon (string)              ：预览图标键（旧版兼容）。
 *     - ButtonIcon (string)               ：按钮图标键（旧版兼容）。
 *     - DescKey (string)                  ：描述本地化键。
 *     - RawInputs (Dictionary<string, string>)：原始输入映射，用于保留常量引用
 *       原文，序列化时优先使用。
 *
 * 2.2-a 常量引用参数输入规则（UpdateStyleParam）
 *     引擎提供按参数路径更新单个参数的公开接口：
 *     void UpdateStyleParam(string styleName, string paramPath, string? input)
 *     参数路径（paramPath）采用点分形式，与 galaxy_shapes.txt 结构对应：
 *       - 顶层字段：core_radius_perc、num_stars_core_perc、stars_min_dist、
 *         num_arms、preview_icon、button_icon、desc。
 *       - 嵌套块字段：countries.ideal_sq_dist_between、
 *         countries.min_sq_dist_between、fallen_empires.ideal_sq_dist_between、
 *         fallen_empires.min_sq_dist_between、arms.tightness_winding、
 *         arms.width、arms.fuzz、arms.seperation、ring.width、ring.offset。
 *     输入处理规则（按顺序）：
 *       1. Trim 输入；为空则抛 ArgumentException。
 *       2. 若输入以 '@' 开头（常量引用）：
 *          a) 调用 StellarisAdapter.ResolveConstantInput(input) 求值。
 *          b) 无论求值是否成功，均将 Trim 后的原文存入
 *             RawInputs[paramPath]（写回时原样填回 '@' 引用）。
 *          c) 求值成功 → 将结果按目标类型写入对应强类型属性
 *             （数值用 Convert.ToDouble/ToInt32，文本用 ToString）。
 *          d) 求值失败（返回 null）或类型转换失败 → 记 Warning 日志，
 *             强类型属性保持当前值不变，原文保留用于写回。
 *       3. 否则（普通输入）：
 *          a) 去除头尾成对双引号（StripSurroundingQuotes）：
 *             仅当首尾字符均为 '"' 时去除，其余情况原样保留。
 *             目的：无论用户是否记得加引号，均不会产生双重引号。
 *          b) 清除 RawInputs[paramPath]（不再保留原文）。
 *          c) 按参数路径对应的目标类型转换并写入强类型属性；
 *             数值解析失败（非数字输入）记 Warning 并保留原值。
 *     禁止事项：
 *       - 本方法严禁读取或写入本地配置（规范 11.7 写入隔离）。
 *       - 不允许通过本方法修改 bool 参数（HasRing 由块存在性决定，
 *         不接收字符串输入）。
 *     其余公开方法（UpdateStyle、AddStyle、SetStyleIcons、NormalizeKeys 等）
 *     直接以强类型对象赋值时，必须同步清除受影响参数的 RawInputs 条目，
 *     避免残留过期 '@' 原文（例如程序化改写 DescKey 时移除 RawInputs["desc"]）。
 *
 * 2.2-b 参数解析（ParseStyleBlock）的 RawInputs 填充
 *     从 adapter 读取样式块时，对每个 Simple 子节点：
 *       - 若子节点 RawText 以 '@' 开头，将 RawText 原文存入
 *         RawInputs[对应参数路径]。
 *       - 强类型属性取子节点 Value（adapter 已完成常量求值；
 *         若 Value 为 ConstantValue 表示常量未解析，数值回退默认值，
 *         文本参数退回 RawText 或空串）。
 *       - 文本参数（preview_icon / button_icon / desc）的值取
 *         Value.ToString()（带引号字符串的 Value 已去除引号）。
 *     序列化（BuildAllStyleBlocks / CreateSimple）时：
 *       - 若 RawInputs 中存在该参数路径，将原文写入 Simple 节点的
 *         RawText（序列化器按 RawText 优先规则输出，实现 '@' 填回）。
 *       - 否则 RawText 保持 null，值由 FormatValue 按内容自动决定引号。
 *
 * 2.3 导出选项（Export Options）
 *     引擎对外提供两组独立的可选参数，分别用于预览图生成和图标生成。
 *     这些参数可由调用方在导出方法中传入；在 SaveAllStyles 流程内部，
 *     还可由本地配置的全局节点覆盖（见 11.4，仅流程内生效）。
 *     预览参数（PreviewOptions）：
 *         OuterWidth, OuterHeight, InnerWidth, InnerHeight,
 *         BackgroundColor (RGBA), GlowArms, GlowCore, CoreColor (RGBA),
 *         StarPresets (Dictionary<string, StarPreset>), BgStarDensity, FillDensity
 *     图标参数（IconOptions）：
 *         FrameWidth, FrameHeight, InnerWidth, InnerHeight,
 *         GlowRadius, NormalColor (RGBA), HighlightColor (RGBA), PressedColor (RGBA)
 *     其中 StarPreset 结构定义：
 *         Color (RGBA)      —— 恒星核心颜色
 *         GlowColor (RGBA)  —— 恒星光晕颜色
 *         Weight (int)      —— 出现占比（≥0，0表示不出现）
 *     恒星预设的键为任意字符串标识符，由调用方自由配置。
 *
 * 2.3-a StarPreset 的 JSON 表示（本地配置 global.preview.star_presets）
 *     采用“对象 + 对象”形式，字段名与 StarPreset 成员对应：
 *     {
 *       "star_presets": {
 *         "wolf_rayet": {
 *           "color": [59, 40, 204, 255],
 *           "glow_color": [59, 40, 204, 255],
 *           "weight": 1
 *         }
 *       }
 *     }
 *     解析规则：
 *       - "color" 必须为长度 ≥ 4 的整数数组，缺失或无效时跳过该预设。
 *       - "glow_color" 可选；缺失时使用 "color" 的值（光晕色 = 核心色）。
 *       - "weight" 可选；缺失时为 0。
 *       - RGBA 各通道取值范围 [0, 255]，越界值按 Math.Clamp 收敛。
 *       - 未知字段被忽略；整个 star_presets 缺失或解析结果为空时，
 *         回退到 PreviewOptions.Default 的硬编码预设。
 *
 * 2.4 操作状态（OperationStatus）
 *     导出类方法返回 OperationStatus；保存类方法（SaveAllStyles）返回 SaveResult。
 *     引擎不设公开 Status 属性。
 *     枚举值：
 *         Success, FileNotFound, SpriteNotFound, SpriteAlreadyExists,
 *         InvalidParameter, IoError, ParseError, ImageLoadError,
 *         OutOfMemory, UnknownError
 *
 * 2.5 本地化缓存（Localisation Cache）
 *     引擎内部维护本地化缓存，结构为：
 *     Dictionary<string, Dictionary<string, string>> _localisationCache
 *     外层 Key：语言标识符（如 "english"、"simp_chinese"）。
 *     内层 Key：本地化条目名，Value：对应的文本。
 *     每个样式至少贡献两个条目：标题（键 = 样式名）和描述（键 = desc 字段值）。
 *     缓存的生命周期与样式表同步，在加载和重载时填充。
 *
 * 2.6 本地配置结构（Local Config Structure）
 *     本地配置文件 `galaxy.json` 的 JSON 结构必须遵循以下格式：
 *     {
 *       "global": {
 *         "preview": { ... },   // 全局预览参数，字段与 PreviewOptions 对应
 *         "icon": { ... },      // 全局图标参数，字段与 IconOptions 对应
 *         "behavior": {
 *           "fallback_preview": false,   // 样式未设置 preview 时的回退值
 *           "fallback_icon": false,      // 样式未设置 icon 时的回退值
 *           "fallback_normalize": false, // 样式未设置 normalize 时的回退值
 *           "sync_on_save": false        // 保存时是否自动回写配置
 *         }
 *       },
 *       "styles": {
 *         "spiral_2": {
 *           "preview": true,
 *           "icon": true,
 *           "normalize": true
 *         },
 *         "ring_4": {
 *           "preview": false,
 *           "icon": true
 *         }
 *       }
 *     }
 *     引擎仅读取 `global` 和 `styles` 节点，其他节点被忽略。
 *     若某个样式在 `styles` 中缺失，则该样式的所有独立开关使用 `fallback_*` 值。
 *
 *
 * 第三章：核心常量及其理由（精确数值）
 * ====================================
 *
 * 3.1 逻辑坐标系常量
 *     游戏最大安全生成半径：500.0（浮点数）
 *         理由：游戏引擎的安全生成边界，超过该值恒星可能因浮点误差偏移至屏幕外。
 *     终止半径：450.0（浮点数）
 *         理由：最大半径乘以 0.9，留出边缘余量，确保所有恒星完全可见。
 *
 *
 * 第四章：默认导出配置与本地配置覆盖规则
 * ======================================
 *
 * 4.1 默认预览参数（硬编码）
 *     逻辑绘制画布尺寸：500 × 500 像素（固定，不可配置）
 *     有效内容视口尺寸：200 × 200 像素
 *     外部输出尺寸：562 × 236 像素
 *     背景颜色：(0, 0, 0, 255) 黑色不透明
 *     旋臂光晕：启用
 *     核心光晕：启用
 *     核心光晕颜色：(255, 255, 255, 255)
 *     恒星预设：见规范 2.3-a（引擎不限制类型名称或数量）
 *     背景星光密度：0.20
 *     填充密度：0.25
 *     图像格式：Rgba8888（强制，不可配置）
 *
 * 4.2 默认图标参数（硬编码）
 *     灰度掩码视口尺寸：35 × 35 像素
 *     单帧尺寸：110 × 59 像素
 *     外部输出尺寸：330 × 59 像素（三帧水平拼接）
 *     光晕半径：9 像素
 *     正常颜色：(13, 200, 167, 255)
 *     按下颜色：(108, 255, 224, 255)
 *     高亮颜色：(249, 161, 50, 255)
 *     图像格式：Rgba8888（强制，不可配置）
 *
 * 4.3 本地配置覆盖规则
 *     当 `useLocalConfig` 为 true 时，引擎必须按以下优先级读取参数：
 *       1) 若 `global.preview` 或 `global.icon` 存在且字段完整，
 *          则使用这些值覆盖对应的硬编码默认值。
 *       2) 若某字段在配置中缺失或类型错误，则回退到硬编码默认值。
 *       3) 样式独立开关的解析顺序：
 *          a) 若 `styles.{name}.preview` 存在且为 bool，则使用该值。
 *          b) 否则使用 `global.behavior.fallback_preview`（若存在且为 bool）。
 *          c) 否则使用硬编码默认值 false。
 *          d) `icon` 和 `normalize` 同理。
 *       4) 若配置文件不存在或解析失败，则整个 `useLocalConfig` 视作 false，
 *          所有参数使用硬编码默认值，并记录 Error 日志。
 *
 *
 * 第五章：坐标映射（逻辑坐标系 ↔ 像素坐标系）
 * ============================================
 *
 * 5.1 正向映射（逻辑坐标 → 像素坐标）
 *     输入：逻辑坐标 (lx, ly)，其中 lx, ly ∈ [-500.0, 500.0]（浮点数）
 *           目标画布尺寸 W×H（通常 W=H=500）
 *     输出：像素坐标 (px, py)，其中 px ∈ [0, W-1]，py ∈ [0, H-1]（整数）
 *     公式：
 *         px = (int)( (lx + 500.0) / 1000.0 * (W - 1) )
 *         py = (int)( (H - 1) - (ly + 500.0) / 1000.0 * (H - 1) )
 *     说明：使用截断（直接 (int) 转换，不四舍五入）。
 *     此行为与全部现有渲染实现一致（审查 D1 决策）。
 *
 * 5.2 反向映射（像素坐标 → 逻辑坐标）
 *     输入：像素坐标 (px, py)（整数），画布尺寸 (W, H)
 *     输出：逻辑坐标 (lx, ly)（浮点数）
 *     公式：
 *         lx = (px / (W - 1)) * 1000.0 - 500.0
 *         ly = 500.0 - (py / (H - 1)) * 1000.0
 *
 *
 * 第六章：点阵生成算法（精确数学定义）
 * ====================================
 *
 * 6.1 通用半径采样序列
 *     输入：起始半径 start、终止半径 end、步长 step
 *     算法：
 *       1. 初始化空列表 radii。
 *       2. 令 r = start。
 *       3. 循环直到 r > end + step * 0.5：
 *          若 r > end，则跳出。
 *          将 r 加入 radii，r += step。
 *       4. 若 radii 非空且 |radii[-1] - end| > step * 0.5，则将 end 追加。
 *     返回 radii。
 *
 * 6.2 阿基米德螺旋臂点阵
 *     输入：旋臂数量 N、起始半径 r0、终止半径 R、缠绕度 t、方向 dir（+1/-1）、
 *           臂宽角度 w_deg、臂间夹角 a_deg、步长 step。
 *     前提：N > 0，r0 < R，step > 0。
 *     步骤：
 *       1. radii = SampleRadii(r0, R, step)。
 *       2. 总扭转角 Φ = t * 2π。
 *       3. 自动偏移角（弧度）：offset_rad = (-dir * t * 360.0 * (r0 / R)) * π/180。
 *       4. 半臂宽 δ = (w_deg / 2.0) * π/180。
 *       5. 对每条臂 i（0 ≤ i < N）：
 *          base_angle = offset_rad + i * a_deg * π/180。
 *          对每个半径 r in radii：
 *             center_phi = base_angle + dir * Φ * (r / R)。
 *             arc_len = r * 2 * δ。
 *             若 arc_len ≤ 0，m = 1，否则 m = max(1, floor(arc_len / step) + 1)。
 *             若 m == 1，点 = (r*cos(center_phi), r*sin(center_phi))。
 *             否则对 k = 0..m-1：
 *                 phi = center_phi - δ + k * (2δ / (m-1))。
 *                 点 = (r*cos(phi), r*sin(phi))。
 *
 * 6.3 环点阵
 *     输入：环宽度 w、环偏移 o、步长 step、最大半径 R。
 *     内径 R_in = R * o，外径 R_out = R * (o + w)。
 *     若 R_in >= R_out，返回空列表。
 *     radii = SampleRadii(R_in, R_out, step)。
 *     对每个 r in radii：
 *         n = max(1, floor(2πr / step))。
 *         对 k = 0..n-1：
 *             θ = 2πk / n。
 *             点 = (r*cos(θ), r*sin(θ))。
 *
 * 6.4 圆盘点阵
 *     输入：起始半径 r0、终止半径 R、步长 step。
 *     radii = SampleRadii(r0, R, step)。
 *     对每个 r，角度采样同 6.3。
 *
 *
 * 第七章：面积计算与重叠剔除
 * ==========================
 *
 * 7.1 旋臂多边形构建
 *     对每条臂，构建一个闭合多边形，顶点顺序为：
 *       1) 起始弧：以 radii[0] 为半径，从 (centerStart − halfWidthRad) 到
 *          (centerStart + halfWidthRad) 扫过的弧段点集（含两端）。
 *       2) 右边界：对 radii[1 .. n−2] 每个半径取 center + halfWidthRad 的点。
 *       3) 终止弧（反向）：以 radii[n−1] 为半径，从 (centerEnd + halfWidthRad)
 *          到 (centerEnd − halfWidthRad) 反向扫过的弧段点集。
 *       4) 左边界（反向）：对 radii[n−2 .. 1] 每个半径取 center − halfWidthRad 的点。
 *     其中 center = baseAngle + dirSign * totalTheta * (r / endR)；
 *     弧段采样数 = max(2, floor(r * 2 * halfWidthRad / step) + 1)。
 *     此算法与 GalaxyPointGenerator.GetArmPolygonsInRange 实现一致。
 *
 * 7.2 鞋带公式面积
 *     面积 = 0.5 * |Σ (x_i * y_{i+1} - x_{i+1} * y_i)|。
 *
 * 7.3 重叠剔除策略
 *     若同时有环和旋臂，环面积与旋臂外面积之和为总面积。
 *     若只有旋臂，总面积为所有旋臂多边形面积之和。
 *     若只有环，总面积为环面积。
 *     若两者都无，总面积为 π * (R² - r0²)。
 *
 *
 * 第八章：预览渲染算法（尺寸可自定义）
 * ====================================
 *
 * 8.1 渲染流程总览
 *     a) 根据样式参数生成候选点集。
 *     b) 计算面积并分配采样配额（强制限制 10~20000）。
 *     c) 空间网格采样确保最小间距。
 *     d) 合并采样点，按权重分配恒星类型。
 *     e) 创建外部输出画布，填充背景色，绘制背景星光。
 *     f) 创建逻辑画布（500×500），绘制核心辉光（8.3）。
 *     g) 扩展逻辑画布（扩展量 120，得到 620×620）。
 *     h) 在扩展画布上生成旋臂星云（8.4）。
 *     i) 在扩展画布上绘制恒星点阵（8.5）。
 *     j) 独立方向缩放至内视口尺寸：scaleX = InnerWidth / 500、
 *        scaleY = InnerHeight / 500；scaledW/H = Math.Round(extW×scaleX /
 *        extH×scaleY)（审查 D4 决策：非等比；默认 InnerWidth=InnerHeight
 *        时与等比等价）。
 *     k) 裁切多余部分（独立判断宽高方向）：若 scaledW > InnerWidth 则
 *        居中裁切宽，scaledH > InnerHeight 同理。
 *     l) 合成到外部输出画布。
 *
 * 8.2 空间网格采样
 *     网格单元大小 = stars_min_dist。
 *     使用哈希表，检查 3×3 邻域。
 *
 * 8.3 核心辉光绘制
 *     核心像素半径 core_px = core_ratio * R * 0.5（core_ratio = min(CoreRadiusPerc, 0.5)）。
 *     衰减半径阈值 r1, r2, r3, r4 分别为 0.25, 0.50, 1.00, 2.00 倍 core_px。
 *     根据距离 d 计算 alpha，高斯模糊（半径 12.5px），与 core_color 合成。
 *
 * 8.4 旋臂星云层生成
 *     密度网格、随机散点、邻近过滤、绘制半透明圆和径向渐变、高斯模糊（半径 15px）。
 *
 * 8.5 恒星点绘制
 *     光晕层半径 8px，核心点半径 2px（基于 500×500 逻辑画布定义）。
 *
 * 8.6 背景星光
 *     num_stars = clamp(floor(OuterWidth * OuterHeight * bg_star_density * 0.02), 10, 5000)。
 *     随机生成，亮度 [63,127]，1×1 点，0.5px 高斯模糊。
 *
 *
 * 第九章：按钮图标生成算法（三态）
 * ================================
 *
 * 9.1 灰度掩码生成
 *     对每个逻辑点，映射到灰度掩码视口尺寸，绘制半径 2 实心圆。
 *     若需填充核心（触发条件：NumArms &gt; 0，或 HasRing 且
 *     |RingOffset×EndRadius − CoreRadiusPerc×EndRadius| &gt; 0.001），
 *     绘制半径 floor(min(core_ratio,0.5)*视口宽/2) 的实心圆，累加 128。
 *     对比度映射：v≤128 -> v' = v*191/128；否则 v' = 191 + (v-128)*64/127。
 *
 * 9.2 三态着色
 *     对正常、高亮、按下分别创建帧，颜色 = 状态颜色 * (v'/255)。
 *     绘制 1px 外边框，高亮/按下状态叠加高斯模糊光晕（半径 9px）。
 *     水平拼接三帧。
 *
 *
 * 第十章：对外接口与本地化管理及几何查询
 * ======================================
 *
 * 10.1 导出接口
 *     10.1.1 ExportSinglePreview(styleName, previewOptions)
 *     10.1.2 ExportAllPreviews(previewOptions)
 *     10.1.3 ExportSingleIcon(styleName, iconOptions)
 *     10.1.4 ExportAllIcons(iconOptions)
 *     行为同旧规范；单独调用时参数仅来自调用方传入的 options
 *     （未传入使用硬编码默认值），不受本地配置影响（见 10.1-a）。
 *
 * 10.1-a 单导出与本地配置的隔离（强制）
 *     单独调用的 ExportSinglePreview / ExportSingleIcon / ExportAllPreviews /
 *     ExportAllIcons **不受本地配置影响**：
 *       - 参数仅来自调用方传入的 options；未传入时使用硬编码默认值
 *         （PreviewOptions.Default / IconOptions.Default）。
 *       - 样式独立开关（preview / icon / normalize）不作用于这些方法，
 *         它们始终执行（规范 11.2）。
 *       - 本地配置解析结果仅在 SaveAllStyles 流程内部使用（规范 11.7）。
 *
 * 10.2 本地化管理接口
 *     10.2.1 GetLocalisedTitle(styleName, lang)
 *     10.2.2 GetLocalisedDescription(styleName, lang)
 *     10.2.3 GetAllLocalisationForStyle(styleName)
 *     10.2.4 UpdateLocalisation(styleName, lang, newTitle, newDescKey, newDescText)
 *           - newTitle / newDescText 为**逻辑值（原文，可含 $var$）**；写入后引擎
 *             调用 adapter.ExpandLocalisationKey 重算显示值。
 *     10.2.5 NormalizeKeys(styleName)
 *     10.2.6 SetStyleIcons(styleName, previewIcon, buttonIcon)
 *     10.2.7 AddStyle(name, parameters, localisation, index)
 *           - index：显示/落盘顺序插入位置（-1 = 追加末尾）。
 *     10.2.8 DeleteStyle(name)
 *     10.2.9 RenameStyle(oldName, newName)
 *           - 重命名样式 key：更新 galaxy_shapes.txt 块名 + 本地化键
 *             （样式名键、自动 desc 键 {old}_desc → {new}_desc），各语言值保留。
 *     10.2.10 RefreshLocalisationCache()
 *     10.2.11 GetLocalisedLogicalText(key, lang)
 *           - 取本地化条目的**逻辑值**（原文，含 $var$ 占位，未展开）。
 *     10.2.12 GetStyleSwitch / SetStyleSwitch(styleName, kind, value)
 *           - 读写银河类别 galaxy.json 的 styles.{name}.preview|icon 开关。
 *
 * 10.2-a 逻辑值与显示值（强制）
 *     每个本地化条目同时持有：
 *       - 逻辑值 LogicalValue：原文（可能含 $var$ 替换占位），加载/编辑时写入；
 *       - 显示值 Value：$var$ 展开后的文本，仅供 UI 展示。
 *     落盘（WriteLocalisation）一律写**逻辑值**，磁盘保留原文；展开仅发生在内存。
 *
 * 10.3 几何查询接口
 *     10.3.1 GetShapePolygons(styleName, endRadius, step, dirSign)
 *     10.3.2 GetShapePolygonsWithParameters(parameters, ...)
 *     返回多边形顶点列表（逻辑坐标系）。
 *
 *
 * 第十一章：本地配置集成（新增）
 * ==============================
 *
 * 11.1 本地配置管理器注入
 *     引擎构造函数允许传入 IConfigManager 实例。若未提供，引擎内部 _configManager
 *     为 null，所有本地配置功能静默禁用（即 useLocalConfig 强制为 false）。
 *
 * 11.2 样式独立开关定义
 *     每个样式在本地配置中最多拥有三个独立开关：
 *       - preview (bool)：是否在 SaveAllStyles 中为该样式生成预览图。
 *       - icon (bool)   ：是否在 SaveAllStyles 中为该样式生成图标。
 *       - normalize (bool)：是否在保存时对该样式执行 NormalizeKeys 操作。
 *     这些开关仅影响 SaveAllStyles 的自动化流程，不影响单独调用的 ExportSinglePreview
 *     等方法（后者始终执行）。
 *
 * 11.3 全局行为回退值
 *     在 `global.behavior` 中定义三个回退值：
 *       - fallback_preview : 样式未设置 preview 时的默认值（默认 false）。
 *       - fallback_icon    : 样式未设置 icon 时的默认值（默认 false）。
 *       - fallback_normalize: 样式未设置 normalize 时的默认值（默认 false）。
 *       - sync_on_save     : 是否在 SaveAllStyles 完成后将当前样式开关状态
 *                            写回本地配置（默认 false）。
 *
 * 11.4 保存流程中的本地配置读取步骤
 *     当 SaveAllStyles(useLocalConfig: true) 被调用时，引擎必须执行以下顺序：
 *       1) 尝试调用 _configManager.GetAll("galaxy") 获取完整配置对象。
 *          配置文件（galaxy.json）结构遵循 2.6；LocalConfigManager 支持
 *          点路径键（"styles.spiral_2.preview"）自动创建/访问嵌套节点。
 *       2) 若获取失败（文件不存在、解析错误、_configManager 为 null），
 *          则内部快照的 Available 标志置为 false（LocalConfigSnapshot.Available），
 *          后续步骤全部回退到硬编码默认值，并记录 Error 日志。
 *       3) 若获取成功，解析 `global.preview` 和 `global.icon` 节点，
 *          将字段映射到 PreviewOptions 和 IconOptions 对象。
 *          不支持的字段或类型错误字段被忽略（回退默认值）。
 *          字段映射（snake_case）：outer_width、outer_height、inner_width、
 *          inner_height、background_color（RGBA 数组）、glow_arms、glow_core、
 *          core_color（RGBA 数组）、star_presets（见 2.3-a）、bg_star_density、
 *          fill_density；图标：frame_width、frame_height、inner_width、
 *          inner_height、glow_radius、normal_color、highlight_color、
 *          pressed_color（均为 RGBA 数组）。
 *       4) 解析 `global.behavior` 中的回退值（fallback_preview、
 *          fallback_icon、fallback_normalize）和 sync_on_save。
 *       5) 遍历所有样式，对每个样式：
 *          a) 从 `styles.{name}` 读取 preview、icon、normalize。
 *          b) 若某个字段缺失，使用对应的 fallback 值。
 *          c) 若 fallback 也缺失，使用硬编码默认值 false。
 *       6) 将解析后的所有参数保存在内部快照对象（LocalConfigSnapshot）中，
 *          供导出步骤使用。快照包含：全局 PreviewOptions / IconOptions、
 *          三个回退值、sync_on_save、以及样式名 → StyleSwitches 映射；
 *          快照提供 GetEffectiveSwitches(name) 解析每个样式的有效开关。
 *
 * 11.5 导出步骤中的使用（总开关 + 样式开关的精确语义）
 *     设 snapshot.Available 表示本地配置解析成功可用。
 *     预览总开关：previewMaster = autoBuildPreviews ?? snapshot.Available。
 *     图标总开关：iconMaster = autoBuildIcons ?? snapshot.Available。
 *     （即 null 表示“跟随配置”：配置可用时总开关为 true，否则为 false，
 *      与旧版默认行为一致。）
 *     对每个样式依次判断：
 *       - 预览导出：仅当 previewMaster 为 true 且（autoBuildPreviews 已显式
 *         指定，或 !snapshot.Available，或该样式 preview 开关为 true）时执行。
 *         即：显式 true/false 为强制覆盖（true 导出所有、false 全部不导出，
 *         样式开关不生效）；null 且配置可用时按样式 preview 开关逐样式过滤。
 *       - 图标导出：规则同预览（使用 iconMaster 与样式 icon 开关）。
 *       - 规范化：仅当该样式的 normalize 为 true 时执行 NormalizeKeysCore
 *         （normalize 开关仅来自配置：styles.{name}.normalize → fallback_normalize
 *         → false；配置不可用时恒为 false，与旧版一致）。
 *         规整化只改内存（键迁移 + 图标字段修正），由 SaveAllStyles 步骤 4/7
 *         显式落盘——“全部规整化”（NormalizeAllKeys）绝不写盘，
 *         保存必须由用户显式触发。
 *     - 所有导出的渲染参数（尺寸、颜色、密度等）使用 11.4 解析出的全局参数；
 *       配置不可用时使用硬编码默认值。
 *
 * 11.6 配置回写（Sync）
 *     - SaveAllStyles 保存时**无条件**执行配置回写：把内存中的相关设置
 *       （每个样式的 preview/icon/normalize 开关 + 全局导出参数
 *       global.preview.* / global.icon.*）同步写入银河类别 galaxy.json，
 *       保证设置归位银河类别、下次保存可读（不再依赖 sync_on_save 开关）。
 *     - 回写通过调用 _configManager.SetBatch("galaxy", syncData) 实现，
 *       其中 syncData 为扁平字典，键为 "styles.{name}.preview"、
 *       "global.preview.outer_width" 等形式；LocalConfigManager 将点路径键
 *       自动映射为嵌套节点（2.6 结构）。
 *     - 若回写失败，记录 Error 日志，但不影响 SaveResult 的整体成功状态
 *       （即导出仍视为成功，但配置同步失败）。
 *     - 引擎还提供一个独立公开方法 SyncToLocalConfig()，供上层手动调用，
 *       该方法执行与回写相同的操作；即使配置不存在也会写入当前全部样式
 *       的开关状态与导出参数。
 *
 * 11.7 写入隔离原则（强制）
 *     - 所有修改样式参数或本地化的方法（UpdateStyleParam、UpdateLocalisation、
 *       UpdateStyle、AddStyle、DeleteStyle 等）**严禁**读取或写入本地配置。
 *     - 本地配置的唯一写入途径是 SaveAllStyles（步骤 6）和 SyncToLocalConfig()。
 *     - 本地配置的唯一读取途径是 SaveAllStyles（当 useLocalConfig 为 true）。
 *       其他方法不得调用 _configManager。
 *       （例外：SyncToLocalConfig() 手动回写前需读取配置快照以解析回退值，
 *       属 11.6 回写的必要前置。）
 *     - 导出设置（预览/图标尺寸等）归位银河类别 galaxy.json 的
 *       global.preview.* / global.icon.*（不再存用户配置类别 ModPreferences）；
 *       历史遗留于 ModPreferences 的 StyleFlags 在启动时自动迁移到 galaxy.json。
 *     - 文件写回范围（强制）：保存/规整化的文件落盘只写本 mod 目录
 *       （Roots[-1] 或新建文件）；游戏本体等外部 root 的文件只读不写，
 *       覆盖性兼容由本 mod 生成同名文件达成。被迁移的键若原位于外部文件，
 *       仅在本 mod 文件写入覆盖，绝不修改外部文件。
 *
 *
 * 第十二章：“保存全部样式”流程（集成导出与校验）
 * ================================================
 *
 * 12.1 方法签名
 *     SaveResult SaveAllStyles(bool useLocalConfig = false,
 *                              bool? autoBuildIcons = null,
 *                              bool? autoBuildPreviews = null)
 *
 *     参数语义（null 表示“跟随配置”，见 11.5）：
 *       - useLocalConfig：是否启用本地配置驱动导出。false 或 _configManager
 *         为 null 时，全部回退硬编码默认值，行为与旧版完全一致。
 *       - autoBuildIcons / autoBuildPreviews：
 *           true  → 强制开启该类导出（配合样式开关；配置不可用时导出所有，
 *                    与旧版 SaveAllStyles(autoBuildXxx: true) 一致）；
 *           false → 强制关闭，不导出；
 *           null  → 跟随配置（useLocalConfig 生效时逐样式按开关，否则 false）。
 *
 * 12.2 执行流程（完整步骤）
 *     步骤 1：若 useLocalConfig 为 true 且 _configManager 不为 null，
 *             执行 11.4 的本地配置读取，得到 LocalConfigSnapshot。
 *     步骤 2：规整化（仅内存，不落盘）——对每个样式（配置可用时按样式
 *             normalize 开关过滤）执行 NormalizeKeysCore：把样式名/desc 键
 *             迁移到合规文件 localisation/{lang}/{prefix}_style_l_{lang}.yml、
 *             修正图标字段；同时收集“待保存文件集”（“lang\0相对路径”，
 *             HashSet 去重，整体 O(n)）。所有样式相关键当前所在、且属于
 *             本 mod 目录的文件也加入待保存集（保证未开启 normalize 的样式
 *             其编辑内容也能落盘）。外部 root 的文件只读不写。
 *     步骤 2b：gfx 精灵表位置规整化（仅内存，随保存落盘）——本 mod 内
 *             GFX_galaxy_* 精灵应位于 interface/game_setup/{prefix}_galaxy_shapes.gfx
 *             （规范 14.5）；错误文件（历史遗留 setup.gfx / *_xxc.gfx 等）中的
 *             精灵经 MoveSprite 迁移到正确文件，源文件记入待清理集（写空头
 *             spriteTypes）。外部 root 的 .gfx 只读不迁移。
 *     步骤 3：计算本次将写入内容的哈希（BuildAllStyleBlocks 序列化，含规整化
 *             修正），并与“写盘前 adapter 内存 AST 中的样式块基线”比较。
 *     步骤 4：将内存样式表写回 galaxy_shapes.txt（SaveToAdapter；新建文件先
 *             经 SA.CreateEmptyFileInMemory 注册）。规整化修正的图标/descKey
 *             参数**本次保存即落盘**（不再等下一次）。
 *     步骤 5：若内容哈希有变化，根据 11.5 的总开关与样式独立开关语义，
 *             逐样式调用 ExportSingleIcon 和 ExportSinglePreview（使用快照
 *             全局参数）。
 *     步骤 6：执行配置回写（11.6）——无条件把内存中的相关设置同步到银河类别
 *             galaxy.json（样式开关 + 全局导出参数）。
 *     步骤 7：本地化写入——只写“待保存文件集”中的文件（WritePendingLocalisations，
 *             经 SA.WriteLocalisation 逐文件落盘到本 mod 目录；文件仍有
 *             CurrentPath 键写内容、无键写空头清理；待保存集含键的当前文件与
 *             OldPath 迁移来源文件，保证磁盘旧文件被清理）。绝不写游戏本体等
 *             外部 root 的文件，也不全量重写所有本地化文件。
 *     步骤 8：gfx 精灵表写回（SpriteManagementEngine.WriteAllSpriteDefinitions，
 *             只写本 mod 目录内涉及文件，不复制外部 root 的 .gfx；含步骤 2b
 *             迁移产生的待清理文件——写空头 spriteTypes）。
 *     步骤 9：返回 SaveResult 统计信息。
 *
 * 12.3 校验规则
 *     哈希计算范围：仅序列化内存样式表中的样式块（BuildAllStyleBlocks），
 *     基线为写盘前 adapter 内存 AST 中同名字样式的块（过滤 spriteTypes 等
 *     非样式块），二者使用相同序列化规则以保证可比性。
 *     注意：不能以“写盘后重读”作为基线——写盘后内容必然等于本次写入内容，
 *     会导致“无变化”判定恒真、导出永不执行。
 *     若写入操作失败，直接返回错误，不进行后续导出。
 *
 * 12.4 本地化写入流程
 *     在步骤 7 中，只对“待保存文件集”（“lang\0相对路径”）中的文件调用
 *     adapter.WriteLocalisation(lang, fileName)，不重写全部文件；外部 root
 *     （游戏本体等）的文件只读不写，覆盖性兼容由本 mod 文件达成。
 *
 *
 * 第十三章：错误恢复与降级策略
 * ============================
 *
 * 13.1 若螺旋臂点阵生成返回空列表，则跳过旋臂渲染，仅渲染环或圆盘。
 * 13.2 若环内径 ≥ 外径，则禁用环渲染，不报错。
 * 13.3 若采样后点集为空，返回一张全透明画布，并记录警告日志。
 * 13.4 “保存全部样式”时，若某个样式导出失败，不影响其他样式，
 *      但最终状态标记为“部分失败”。
 * 13.5 所有异常必须被捕获并转换为 OperationStatus，不得向上层抛出未处理异常。
 * 13.6 本地配置读取失败时，静默降级，所有参数使用硬编码默认值，
 *      导出照常进行（只是不使用用户配置）。
 * 13.7 本地配置回写失败时，仅记录错误日志，不中断导出流程。
 *
 *
 * 第十四章：与 SA、IA、SM 的协作约定（强制）
 * ==========================================
 *
 * 14.1 所有文件读写均通过 StellarisAdapter，禁止直接磁盘操作。
 * 14.2 所有图像操作在非必须情况下，均通过 ImageAssetEngine 进行，
 *      禁止直接使用图像库（如 SkiaSharp、BCnEncoder）。
 * 14.3 所有精灵管理均通过 SpriteManagementEngine，禁止手动修改 .gfx 文件。
 * 14.4 本地化写入必须调用 StellarisAdapter.WriteLocalisation。
 * 14.5 路径与精灵生成规范：
 *      - 导出文件路径（仅作为 .dds/.png 落盘位置，**严禁写入字段**）：
 *        gfx/interface/game_setup/galaxy_preview/{modPrefix}_{styleName}
 *        gfx/interface/game_setup/galaxy_button/{modPrefix}_{styleName}
 *      - preview_icon / button_icon 字段值 = **精灵名**（黑箱测试结论）：
 *        GFX_galaxy_preview_{styleName}
 *        GFX_galaxy_button_{styleName}
 *        必须先有 .gfx spriteType 声明（导出时由 SpriteManagementEngine 注册）
 *        才能被引用；任何实现严禁把文件路径写入 preview_icon / button_icon。
 *      - .gfx 文件：interface/game_setup/{modPrefix}_galaxy_shapes.gfx
 *      - 本地化文件：localisation/{lang}/{modPrefix}_style_l_{lang}.yml
 *        （样式名/desc 键的合规位置；AddStyle、UpdateLocalisation、规整化统一使用）
 *      - 本地配置文件：galaxy.json（存放于 LocalConfigManager 的根目录下）
 *      模组前缀（modPrefix）由引擎初始化时传入，不可为空。
 *
 *
 * 第十五章：性能与内存要求
 * ========================
 *
 * 15.1 样式表应缓存于内存中，所有查询操作 O(1) 访问。
 * 15.2 点阵生成应避免重复计算，可通过缓存候选点集优化。
 * 15.3 预览渲染时，内部画布尺寸默认为 500×500。
 * 15.4 内存检查由下层 IA 负责，引擎本身不额外进行内存预检。
 * 15.5 本地配置读取仅在 SaveAllStyles 时发生，不频繁读取磁盘。
 *
 *
 * ============================================================================
 * 附录 A：API 索引（按模块列出核心公共函数与所在文件，便于查找）
 * ============================================================================
 * 路径基准：Stellaris.Parser / Stellaris.Engine / Stellaris.Editor
 *
 * ---- Stellaris.Parser（StellarisAdapter，partial 类） ----
 *   ScanAll / Rescan                      全量/增量扫描文件          StellarisAdapter_Scan.cs
 *   AddRoot(root)                         添加根目录（顺序即优先级） StellarisAdapter.cs
 *   GetConfig / GetAllConfigs             读配置文件内存 AST         StellarisAdapter.cs
 *   WriteFile / WriteAllFiles             写回配置文件               StellarisAdapter.cs
 *   CreateEmptyFileInMemory               内存注册空文件             StellarisAdapter_CRUD.cs
 *   AddConfigNode(path, parent, node,     添加/更新 AST 节点；        StellarisAdapter_CRUD.cs
 *     existingPredicate)                  existingPredicate 自定义"已存在判定"
 *   UpdateConfigNode(path, target, node,  更新 AST 节点；            StellarisAdapter_CRUD.cs
 *     fullReplace, targetPredicate)       targetPredicate 定位目标，找不到 → Add
 *   RemoveConfigNode                      删除 AST 节点              StellarisAdapter_CRUD.cs
 *   ResolvePath                           AST 路径定位（含条件选择器）StellarisAdapter_CRUD.cs
 *   AddLocalisationEntry(s) / Update / RemoveLocalisationEntry        StellarisAdapter_CRUD.cs
 *   GetLocalisedText                      取显示值（已展开）          StellarisAdapter.cs
 *   GetLocalisedLogicalText               取逻辑值（原文）            StellarisAdapter.cs
 *   GetLocalisationKeyFiles               键→当前文件索引            StellarisAdapter.cs
 *   GetLocalisationOldPathIndex           键→迁移来源文件（OldPath）索引  StellarisAdapter.cs
 *   GetLocalisationFilePaths              涉及写入文件集（CurrentPath+OldPath）StellarisAdapter.cs
 *   ExpandLocalisationValues              全语言 $var$ 展开          StellarisAdapter_Scan.cs
 *   ExpandLocalisationKey(lang, key)      单键展开显示值             StellarisAdapter_Scan.cs
 *   WriteLocalisation / WriteAllLocalisations  本地化落盘（写逻辑值）StellarisAdapter.cs
 *   HasLocalisationKeysInPath             文件是否含 CurrentPath 键  StellarisAdapter.cs
 *   GetFilesRecursive / GetFileRoot       文件遍历 / 文件所属根目录  StellarisAdapter.cs
 *   类型：LocalisationEntry(Value/LogicalValue/CurrentPath/OldPath/Root)、
 *        AstNode、NodeType、ConstantValue、ParserTaskType、FileCategory
 *
 * ---- Stellaris.Engine/GalaxyStyle（GalaxyStyleEngine） ----
 *   LoadAllStyles / RefreshStyles / RefreshLocalisationCache          GalaxyStyleEngine.cs
 *   GetLocalisedText / GetLocalisedLogicalText / GetLocalisedTitle / GetLocalisedDescription
 *   GetStyleSwitch / SetStyleSwitch       银河类别导出开关            GalaxyStyleEngine.cs
 *   SetStyleIcons                         图标字段                   GalaxyStyleEngine.cs
 *   UpdateLocalisation                    更新标题/描述（逻辑值）     GalaxyStyleEngine.cs
 *   NormalizeKeys / NormalizeAllKeys / NormalizeKeysCore             规整化（仅内存）GalaxyStyleEngine.cs
 *   AddStyle(name, params, localisation, index) / DeleteStyle / RenameStyle
 *   UpdateStyle / UpdateStyleParam / GetStyleRawInputs
 *   GetStyle / GetAllStyleNames / GetAllStyles
 *   GetShapePolygons / GetShapePolygonsWithParameters
 *   ExportSinglePreview / ExportSingleIcon / SaveAllStyles            GalaxyStyleEngine.cs
 *   ReadEffectiveSwitch / SyncToLocalConfigInternal / SerializePreviewOptions / SerializeIconOptions
 *   WritePendingLocalisations / CollectPendingLocalisationFiles / StyleLocalisationFile
 *   IsInModRoot / MoveLocalisationKey
 *   类型：SaveResult、PreviewOptions、IconOptions、StarPreset、GalaxyShapeParameters、
 *        GalaxyStyleDefinition、StyleSwitches
 *
 * ---- Stellaris.Engine/SpriteManagement ----
 *   AddSprite / UpdateSprite / RemoveSprite / GetSpriteDefinition      SpriteManagementEngine.cs
 *   NormalizeSpriteFiles(targetGfxPath)   精灵位置规整化（迁移到合规 .gfx）SpriteManagementEngine.cs
 *   WriteAllSpriteDefinitions(extraFiles) gfx 落盘（只写本 mod，含待清理文件）SpriteManagementEngine.cs
 *   类型：SpriteDefinition(Name/TextureFile/NoOfFrames/SourceFile/SourceRoot)、SpriteQueryResult
 *
 * ---- Stellaris.Engine/LocalConfigManager ----
 *   IConfigManager：Set/Get/Delete/Exists/GetAll/Reload/SetBatch      LocalConfigManagerEngine.cs
 *   LocalConfigManager 实现：点路径键自动建嵌套节点；SetNode/ToJsonNode
 *     支持数组值（RGBA int[] → JsonArray）
 *
 * ---- Stellaris.Engine/GalaxyMap / ImageAsset ----
 *   GalaxyMapEngine / ImageAssetEngine    静态地图 / 图像资源引擎     GalaxyMap/*.cs、ImageAsset/*.cs
 *
 * ---- Stellaris.Editor ----
 *   App.OnStartup / RestartFromRoots / PrepareModConfig / InitializeEngines   App.xaml.cs
 *   MainWindow.RefreshUIAfterLanguageChange / BuildNavItems / ApplyUserFont    MainWindow.xaml.cs
 *   SettingsPage：BuildDirsPanel（多选拖拽+插入线）、BuildLangPanel（4 行）、
 *     BuildLanguageCombo（自称+定宽）、ReloadAll（重载入）、BuildModPanel        Pages/SettingsPage.xaml.cs
 *   GalaxyStylePage：ReloadStyles、BuildForms、BuildLocalisationBox（4 控件+逻辑/显示）、
 *     BuildStyleKeyRow（样式键→重命名）、AddNewStyle/RemoveSelectedStyles、
 *     AddColorRow/GetPreviewColor/SetPreviewColor（颜色随模组）、DrawPreview       Pages/GalaxyStylePage.xaml.cs
 *   ExportSettingsWindow：BuildFields（4 列成组）、BuildStarPresetSection（恒星预设
 *     右键增删改+概率排序）、WriteDefaultStarPresets（默认写入）、EditStarDialog（拾色器）ExportSettingsWindow.xaml.cs
 *   ColorPickerControl：彩色/黑白选项卡、ApplyLocalisation                       Controls/ColorPickerControl.xaml.cs
 *   UILocalisationManager：Load、GetLanguageDisplayName（自称）、LoadLanguageDeclarations、SetLanguage
 *   SaveProgressWindow                     保存进度弹窗（无边框可拖动转圈）      SaveProgressWindow.xaml.cs
 *   UserPreferences / ModPreferences / EngineServices                        各自 .cs
 *
 * ============================================================================
 * 规范结束
 * ============================================================================
 */

namespace Stellaris.Engine.GalaxyStyle;