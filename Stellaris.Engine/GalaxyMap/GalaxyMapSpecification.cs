/*
 * ============================================================================
 * STELLARIS GALAXY MAP ENGINE STANDARD SPECIFICATION (REVISION 1.0)
 * ============================================================================
 * 本规范为银河地图引擎（GalaxyMapEngine）实现的唯一权威依据。
 * 所有实现必须严格遵循本规范定义的接口签名、数据结构、算法与边界条件。
 * 本规范在逻辑上优先于任何现有代码实现，所有实现偏差均视为缺陷。
 * ============================================================================
 *
 *
 * 术语定义
 * --------
 * - 地图文件（Scenario File）：位于 `map/setup_scenarios/` 目录下的 `.txt` 文件，
 *   包含 `setup_scenario`（动态地图）或 `static_galaxy_scenario`（静态地图）定义。
 * - 动态地图（Dynamic Scenario）：通过样式名称（如 `spiral_2`）和参数（`radius`、`num_stars`）
 *   定义星系生成规则，不包含具体坐标。
 * - 静态地图（Static Scenario）：通过显式列举每个星系的 ID、坐标、超空间航道等，
 *   精确控制星系布局。
 * - 伪样式（Pseudo-Style）：为静态地图生成的轻量级样式参数，用于预览和图标渲染，
 *   仅开放 `core_radius_perc` 供用户调节。
 * - 用户偏好顺序（Preferred Order）：上层传入的全局样式排序列表（来自用户设置），
 *   与文件内的 `supports_shape` 取交集后应用。
 * - 文件顺序（File Order）：`.txt` 文件中 `supports_shape` 的原始书写顺序，
 *   保存时按此顺序落盘。
 * - 坐标精度（Coordinate Precision）：坐标值在序列化时保留的小数位数，
 *   默认 2 位，用户可通过 UI 调整；内部计算使用双精度浮点数。
 * - 逻辑坐标（Logical Coordinates）：以星系中心为原点、范围为 [-500, 500] 的
 *   二维/三维浮点数坐标系，单位与游戏内单位一致。
 *   静态地图（static_galaxy_scenario）无“半径 500 圆”限制，但受
 *   **[-500, 500] × [-500, 500] 方形边界**约束（x 与 y 均须在 ±500 内）；
 *   动态地图/渲染仍按 GalaxyStyle 的半径 500 约定处理。
 * - 网格生成（Lattice Generation）：基于正三角形、正四边形或正六边形，通过细分
 *   自动生成星系点阵及超空间航道的算法。
 *
 *
 * 第一章：引擎概述与架构
 * ======================
 *
 * 1.1 引擎定位
 *     银河地图引擎负责管理 `map/setup_scenarios/` 目录下的所有动态和静态地图文件，
 *     提供加载、保存、增删改查、样式排序同步、坐标变换、伪样式生成、
 *     资产导出、图像转点阵、网格自动生成以及单点编辑等完整功能。
 *
 * 1.2 依赖关系
 *     - Stellaris.Parser.StellarisAdapter          ：所有文件读写与内存 CRUD。
 *     - Stellaris.Engine.GalaxyStyle.GalaxyStyleEngine：样式查询与点阵生成。
 *     - Stellaris.Engine.ImageAsset.ImageAssetEngine  ：图像加载与导出。
 *     - Stellaris.Engine.SpriteManagement.SpriteManagementEngine：精灵注册。
 *
 * 1.3 文件路径与扩展名
 *     - 根目录：`map/setup_scenarios/`（相对于每个模组根目录）。
 *     - 扩展名：`.txt`。
 *     - 引擎按根目录优先级（由高到低）查找文件；保存时写入最高优先级根目录。
 *
 * 1.4 线程安全
 *     所有公开方法必须使用锁（如 `lock (_syncRoot)`）保护内部状态。
 *
 *
 * 第二章：核心数据结构
 * ====================
 *
 * 2.1 动态地图参数（DynamicScenario）
 *     对应 `setup_scenario` 块。所有字段类型、默认值及约束如下：
 *
 *     ---- 顶层必填字段 ----
 *     name (string)                            ：地图名称（唯一标识）。
 *     priority (int)                           ：显示顺序，默认 0。
 *     num_stars (int)                          ：恒星总数，范围 [50, 2000]，默认 200。
 *     radius (int)                             ：终止半径，范围 [100, 500]，默认 200。
 *
 *     ---- 帝国相关 ----
 *     num_empires (block)                     ：普通帝国数量范围。
 *        min (int)  : 最小数量，默认 0。
 *        max (int)  : 最大数量，默认 10。
 *     num_empire_default (int)                ：默认数量，默认 5。
 *     advanced_empire_default (int)           ：高级帝国默认数量，默认 0。
 *     fallen_empire_default (int)             ：堕落帝国默认数量，默认 0。
 *     fallen_empire_max (int)                 ：堕落帝国最大数量，默认 6。
 *     marauder_empire_default (int)           ：劫掠者帝国默认数量，默认 0。
 *     marauder_empire_max (int)               ：劫掠者帝国最大数量，默认 3。
 *     nomad_empire_default (int)              ：游牧帝国默认数量，默认 0。
 *     nomad_empire_max (int)                  ：游牧帝国最大数量，默认 3。
 *
 *     ---- 行星与危机 ----
 *     colonizable_planet_odds (double)        ：宜居星球倍率，范围 [0, 2.0]，默认 1.0。
 *     primitive_odds (double)                 ：土著星球倍率，默认 1.0。
 *     crisis_strength (double)                ：天灾强度，范围 [0, 5.0]，默认 1.0。
 *     extra_crisis_strength (list<double>)    ：额外天灾强度列表，默认空。
 *
 *     ---- 星云 ----
 *     num_nebulas (int)                       ：星云数量，默认 2。
 *     nebula_size (int)                       ：星云半径，默认 60。
 *     nebula_min_dist (int)                   ：星云间最小距离，默认 100。
 *
 *     ---- 虫洞、星门、航道 ----
 *     num_wormhole_pairs (block)              ：虫洞对数范围。
 *        min (int) : 最小，默认 0。
 *        max (int) : 最大，默认 5。
 *     num_wormhole_pairs_default (int)        ：默认虫洞对数，默认 1。
 *     num_gateways (block)                    ：星门数量范围。
 *        min (int) : 最小，默认 0。
 *        max (int) : 最大，默认 5。
 *     num_gateways_default (int)              ：默认星门数，默认 1。
 *     num_hyperlanes (block)                  ：航道密度范围（float）。
 *        min (float) : 最小，默认 0.5。
 *        max (float) : 最大，默认 3.0。
 *     num_hyperlanes_default (float)          ：默认密度，默认 1.0。
 *
 *     ---- 星系生成参数 ----
 *     cluster_count (block)                   ：星团数量设置。
 *        method (string) : 方法，可选 "one_every_x_empire" 或 "constant"，默认前者。
 *        value (int)    : 每 X 个帝国一个星团，默认 1。
 *        max (int)      : 最大星团数，默认由引擎根据 radius 和 num_stars 自动计算。
 *     cluster_radius (int)                    ：星团半径，默认由引擎自动计算。
 *     cluster_distance_from_core (int)        ：星团距核心距离，默认自动计算。
 *     max_hyperlane_distance (int)            ：最大航道长度，默认 50。
 *     home_system_partitions (block)          ：母星系分区设置。
 *        max_systems (int) : 最大系统数，默认 15。
 *        min_systems (int) : 最小系统数，默认 8。
 *        min_bridges (int) : 最小桥接数，默认 2。
 *        max_bridges (int) : 最大桥接数，默认 4。
 *        method (string)   : 分区算法，可选 "breadth_first" 或 "depth_first"，默认前者。
 *     open_space_partitions (block)           ：开放空间分区，结构同上。
 *
 *     ---- 支持形状 ----
 *     supports_shape (string)                 ：可重复出现，每个表示一个支持的形状名称。
 *                                              顺序即文件顺序（规则 2）。
 *
 *     所有字段在解析时若缺失，均使用上述默认值；若值超出范围，则截断至合法范围并记录警告。
 *
 * 2.2 静态地图参数（StaticScenario）
 *     对应 `static_galaxy_scenario` 块。除与动态地图共有的字段（如帝国设置）外，
 *     还包括：
 *     ---- 布局相关 ----
 *     coordinate_transform (block)            ：坐标变换规则。
 *        x (block) : { add, sub, mul, div }  对 X 坐标依次执行（Add→Sub→Mul→Div）。
 *        y (block) : 同上。
 *        z (block) : 同上。
 *     system (block)                          ：每个系统定义，可重复出现。
 *        id (string)          ：唯一标识，不能重复。
 *        name (string)        ：星系名称，默认为空。
 *        position (block)     ：坐标 { x, y, z }，其中 x/y/z 可为数值或带有 { min, max } 的随机范围块。
 *                               随机范围块在加载时**原样保留**（不取随机值、不固化），
 *                               仅在实际使用时（如渲染、几何计算、导出点阵）随机取中间值。
 *        initializer (string) ：初始器（如 "dyson_sphere_init_01"），可选。
 *        spawn_design (string)：生成设计，可选。
 *        spawn_weight (block) ：生成权重（如 { base = 1 }），可选。
 *        effect (block)       ：效果块，可选。
 *        category (string)    ：星系类别，默认 "normal"。
 *     add_hyperlane (block)                   ：添加超空间航道，重复出现。
 *        from (string) : 起点系统 ID。
 *        to (string)   : 终点系统 ID。
 *     prevent_hyperlane (block)               ：禁止超空间航道，结构同 add_hyperlane。
 *     nebula (block)                          ：星云定义，重复出现。
 *        name (string)   ：星云名称。
 *        position (block)：坐标 { x, y, z }。
 *        radius (int)    ：半径。
 *
 * 2.3 坐标精度设置
 *     引擎提供 `SetCoordinatePrecision(int digits)`，默认 digits=2（保留 2 位小数）。
 *     范围 0~6，超出时裁剪。内部计算始终使用 double，仅在序列化时格式化输出。
 *
 * 2.4 图像转点阵参数（ImageGenerationOptions）
 *     用于 `GeneratePointsFromImage` 方法。
 *     - LayerSelection (enum)                ：选择 R、G、B、A 中的单一图层，或 `InverseR`、`InverseG` 等。
 *     - CompositeMode (enum)                 ：若 Composite 为 true，则指定叠加方式，如 `Add`、`Multiply`、`Average`。
 *     - Threshold (double)                   ：像素值阈值 [0,1]，低于此值的区域视为无效（不生成点），默认 0.0。
 *     - Gamma (double)                       ：指数校正，默认为 1.0。
 *     - GenerationMode (enum)                ：`Spacing` 或 `Count`。
 *     - MinDistance (double)                 ：当模式为 Spacing 时，指定最小间距（逻辑坐标单位）。
 *     - TotalCount (int)                     ：当模式为 Count 时，指定要生成的总点数。
 *     - MaxAttempts (int)                    ：随机采样最大尝试次数，默认 10000，超出则抛出警告并返回部分结果。
 *
 *     若用户未指定任何图层（LayerSelection 为 None），则默认使用全部图层（R、G、B 取平均值，A 作为乘数）。
 *     若图像加载失败（格式错误、损坏等），引擎抛出 `InvalidDataException` 并附带文件路径。
 *
 * 2.5 网格生成参数（LatticeGenerationOptions）
 *     用于 `GenerateLattice` 方法。
 *     - ShapeType (enum)                     ：`Triangle`、`Square`、`Hexagon`。
 *     - SideLength (double)                  ：正多边形的外接圆半径或边长（逻辑坐标单位），必须 > 0。
 *     - Spacing (double)                     ：细分后相邻点之间的最小距离，必须 > 0 且 <= SideLength。
 *     - CenterX, CenterY (double)            ：生成中心位置，默认 (0,0)。
 *
 * 2.6 伪样式（PseudoStyle）
 *     伪样式在代码层面是**一个只有必要组件的合法样式**：它以合法的
 *     `galaxy_shapes.txt` 样式块形式存在（并注册进 GalaxyStyleEngine 的
 *     样式表，作为“合法占位服务”），但在游戏内仅作为静态地图在用户选择
 *     界面中的**预览效果**，不具备实际生成效果——因此无论其参数如何，
 *     都不会影响静态地图的最终结果。
 *
 *     字段：
 *       - double CoreRadiusPerc                  ：核心半径比例（用户可修改）。
 *       - string PreviewIcon                     ：预览图标键，自动生成，精灵名格式（遵循 GalaxyStyle 规范 14.5，须先有 .gfx 声明）：
 *         `GFX_galaxy_preview_{prefix}_{name}`。
 *       - string ButtonIcon                      ：按钮图标键，自动生成，同样遵循 14.5 路径格式。
 *       - string DescKey                         ：描述本地化键，自动生成（如 `{name}_desc`）。
 *     其余参数（`num_stars_core_perc`、`stars_min_dist`）由引擎根据散点分布
 *     自动计算，用户不可修改。
 *
 *     用户配置（galaxy.json）中，伪样式对应的开关被写入：
 *       - preview = false、icon = false、normalize = false
 *     **保存与"不参与"的边界（强制）**：
 *       - 伪样式是合法样式块，在 GalaxyStyleEngine.SaveAllStyles 中**照常被正常
 *         保存**（正常写入 galaxy_shapes.txt），不因其配置开关而被移除或跳过。
 *       - "不参与"仅指 SaveAllStyles 的**自动化导出流程**：不为其自动生成
 *         预览/图标、不执行 NormalizeKeys、不自动创建/更新其本地化条目。
 *       - 伪样式的预览、图标与本地化的**创建和更新全部由 GalaxyMap（静态地图
 *         侧）负责**；GalaxyStyle（动态样式侧）不得修改伪样式的图标、预览或
 *         本地化内容。
 *     当与该伪样式同名的静态地图被修改时，GalaxyMap 引擎**主动重建**其
 *     预览与图标（见 4.6）。
 *
 *
 * 第三章：动态地图管理
 * ====================
 *
 * 3.1 加载与保存
 *     - 通过 StellarisAdapter 读取文件，解析 `setup_scenario` 块。
 *     - 保存时序列化为 `setup_scenario = { ... }` 格式，原子写入。
 *
 * 3.2 样式排序（双重机制）
 *     a) 规则 2（文件顺序）：`SupportedShapes` 列表的顺序即为文件内 `supports_shape` 的出现顺序。
 *        引擎提供接口（如 `GetShapeOrder`、`SetShapeOrder`）供上层自由调整，保存时按此顺序落盘。
 *     b) 规则 1（用户偏好/兜底）：上层可调用 `ApplyPreferredOrder(List<string> preferredOrder)`，
 *        将传入列表与当前 `SupportedShapes` 取交集（过滤掉文件中不存在的样式），
 *        交集结果**按用户偏好列表的顺序**覆盖内存中的顺序。
 *        例：用户偏好 `ABCD`，地图实际支持 `ADC` → 交集顺序为 `ACD`。
 *        此操作仅用于显示/导出时的顺序决策，
 *        **严禁主动写入文件**，除非用户通过“一键排序”功能显式触发。
 *     c) 一键排序功能：提供公开方法 `SyncOrderWithPreferred(List<string> preferredOrder)`，
 *        执行上述交集替换，并将更新后的顺序写入文件（覆盖规则 2）。
 *
 * 3.3 大致样式接口（容量预警）
 *     提供方法 `GetEstimatedCapacity(string mapName)`，返回：
 *       - double Radius                         ：地图的终止半径。
 *       - List<string> SupportedShapes          ：引用的样式名称列表。
 *       - Dictionary<string, int> MaxStarsPerShape：对每个形状，调用 `GalaxyPointGenerator.ComputeAreas`
 *         估算在该地图半径下可容纳的最大恒星数，并乘以 0.8 的安全系数。
 *     上层可据此渲染理论生存范围，并提示用户当前 `num_stars` 是否超出推荐值。
 *
 * 3.4 CRUD 操作
 *     - 增删改动态地图文件。
 *     - 修改时需同步更新文件内容。
 *
 *
 * 第四章：静态地图管理
 * ====================
 *
 * 4.1 加载与保存
 *     - 通过 StellarisAdapter 读取文件，解析 `static_galaxy_scenario` 块。
 *     - 保存时序列化为 `static_galaxy_scenario = { ... }` 格式，原子写入。
 *
 * 4.2 ID 自动生成与补零规则
 *     - 保存时，`SystemEntry.Id` 从 `"0"` 开始连续编号。
 *     - 补零位数由总点数 `N` 决定：`PadWidth = (int)Math.Ceiling(Math.Log10(N + 1))`，
 *       最小为 1。例如 N=100 → 3 位（"000"~"099"），N=1000 → 4 位。
 *     - 用户可通过接口 `SetIdPadding(int minDigits)` 调整最小补零位数。
 *     - **重编号联动**：保存时重新编号后，所有 `add_hyperlane` / `prevent_hyperlane`
 *       的 `from`/`to` 必须同步更新为新的 ID（保持超空间航道拓扑不变）。
 *       加载时保留文件中的原始 ID，仅保存时重编号。
 *
 * 4.3 ID 冲突处理（致命错误）
 *     - 加载时，若发现两个 `SystemEntry` 的 `Id` 完全相同，引擎**必须**抛出
 *       `InvalidOperationException`，异常信息包含冲突的 ID 和文件路径。
 *     - 关联的超空间航道（`FromId` 或 `ToId` 指向冲突 ID）被自动标记为无效，
 *       并在异常抛出前记录警告日志，但不保存。
 *
 * 4.4 坐标变换与对称
 *     a) 加载时，将 `CoordinateTransform` 中定义的 Add/Sub/Mul/Div 按顺序
 *        应用到所有星系的坐标（X、Y、Z 独立计算）：
 *        x = (x + add − sub) × mul ÷ div（Add→Sub→Mul→Div，缺省操作跳过）。
 *        变换结果**立即写入内存坐标**（后续渲染/导出/显示均使用变换后值）；
 *        **保存时把内存中变换后的数值写入文件**（坐标固化，不再保留
 *        coordinate_transform 块，避免下次加载重复变换）。
 *     b) 支持 X 轴镜像、Y 轴镜像（通过 `Transform` 字段中的 `Mul = -1` 实现）。
 *     c) 支持旋转对称：引擎提供 `ApplyRotationalSymmetry(int times, double angleDeg)`，
 *        对当前所有星系坐标绕中心 (0,0,0) 进行旋转复制。
 *        例如 `times=3, angle=45`，则对每个点生成 3 个副本（原角度 + 0°, +45°, +90°）。
 *     d) **航道同步**：所有坐标变换（镜像、旋转）只改变系统坐标；超空间航道
 *        （add_hyperlane / prevent_hyperlane）仅含 `from`/`to` ID、不含坐标，
 *        因此变换后航道的 ID 引用保持不变；仅当 ID 重编号（4.2）时同步更新引用。
 *     e) 点编辑交互模式（对已存在的系统）：
 *        - 单点编辑：点击某个系统时，**先按该点原位置映射后续协同点**
 *          （若存在旋转对称设置，则同步映射到各对称副本），再统一修改这些点。
 *        - 框选编辑：框选多个系统后统一修改（移动/改参数等）。
 *        - 框选旋转：框选后统一旋转（围绕指定中心与角度）。
 *        - 创建模式：仅执行创建操作（如网格生成、图像转点阵），
 *          不合并其他额外内容。
 *        编辑模式与创建模式相互独立，不混合。
 *
 * 4.5 伪样式管理
 *     - 伪样式作为**合法样式**注册进 GalaxyStyleEngine 的样式表（合法占位服务），
 *       与同名的静态地图一一对应；静态地图加载/创建时同步注册或更新，
 *       静态地图删除时移除。
 *     - `PseudoStyle.CoreRadiusPerc` 允许用户通过 `UpdatePseudoStyleCoreRadius` 修改。
 *     - `PreviewIcon`、`ButtonIcon`、`DescKey` 由引擎自动生成，
 *       精灵名格式为 `GFX_galaxy_preview_{prefix}_{name}`、
 *       `GFX_galaxy_button_{prefix}_{name}`（须先有 .gfx spriteType 声明）、`{name}_desc`。
 *     - 其他参数（`num_stars_core_perc`、`stars_min_dist`）由引擎根据散点
 *       分布自动计算并写入伪样式对象。
 *     - 用户配置（galaxy.json）中该伪样式条目的 preview / icon / normalize
 *       均为 false，不参与 SaveAllStyles 自动化（见 2.6）。
 *     - **职责边界**：伪样式在 SaveAllStyles 中照常被正常保存（合法样式块），
 *       但其预览、图标、本地化的创建与更新**全部由本引擎（静态侧）负责**；
 *       GalaxyStyle（动态侧）不得修改伪样式的图标/预览/本地化内容。
 *
 * 4.6 资产导出（预览与图标）
 *     - 提供方法 `ExportAssets(string mapName, bool forceRebuild = false)`。
 *     - 基于当前伪样式（已在 GalaxyStyleEngine 样式表中注册）和星系坐标点阵，
 *       调用 `GalaxyStyleEngine` 的单导出方法（`ExportSinglePreview` 和
 *       `ExportSingleIcon`，传入伪样式名）。
 *     - 使用伪样式中的参数和默认 `PreviewOptions`/`IconOptions`。
 *     - 导出路径与命名遵循 GalaxyStyle 规范 14.5（`gfx/interface/game_setup/...`）。
 *     - **重建时机**：与该伪样式同名的静态地图被修改（增删系统、改坐标、
 *       网格/图像生成等）后，引擎主动重建其预览与图标；forceRebuild=true
 *       时无条件重建。
 *
 * 4.7 大致样式接口
 *     提供方法 `GetEstimatedShape(string mapName)`，返回：
 *       - List<Vector2> OutlinePoints           ：基于伪样式参数调用
 *         `GalaxyStyleEngine.GetShapePolygonsWithParameters` 生成的边界多边形
 *         顶点（逻辑坐标系），与 GalaxyStyle 的几何输出保持**双向联动**；
 *         若 GalaxyStyle 缺少所需接口（如面积/边界点导出），应补充 GalaxyStyle
 *         侧接口而非在 GalaxyMap 侧另起实现。
 *     上层据此渲染理论生存范围。
 *
 * 4.8 网格与晶格生成（Lattice Generation）
 *     引擎提供基于几何形状的静态地图点阵批量生成功能，支持三种基础形状，
 *     所有生成操作需用户显式触发，禁止自动创建。
 *
 *     4.8.1 形状定义与细分规则
 *       | 基础形状 | 细分方式 | 输出结构 |
 *       |----------|----------|----------|
 *       | 正三角形 | 三角形细分（连接各边中点，形成 4 个全等小三角形） | 小三角形顶点作为星系点，小三角形边作为超空间航道 |
 *       | 正四边形 | 四边形网格细分（按指定间距切分为 `n × n` 网格） | 网格交点作为星系点，相邻点之间自动生成航道 |
 *       | 正六边形 | 同心六边形环（见 4.8.1-a 规格） | 中心点 + 各环整点作为星系点，环内/环间相邻点连接为航道 |
 *
 *     4.8.1-a 正六边形的同心环规格
 *       中心为第 0 层（1 个点）。第 k 层（k ≥ 1）为六边形网格中满足
 *       `max(|x|, |y|, |x+y|) = k` 的所有整点，共 `2k+1` 行，
 *       每行宽度为 `k+1, k+2, …, 2k+1, …, k+1`（对称金字塔）：
 *         - 第 1 层：行宽 `2 3 2`
 *         - 第 2 层：行宽 `3 4 5 4 3`
 *         - 第 3 层：行宽 `4 5 6 7 6 5 4`
 *       层间间距由 Spacing 决定：第 k 层到中心的距离 = k × Spacing。
 *
 *     4.8.2 生成参数（用户必须提供）
 *       用户通过 `GenerateLattice(string mapName, LatticeGenerationOptions options)` 传入：
 *         - ShapeType：`Triangle`、`Square` 或 `Hexagon`。
 *         - SideLength（边长）：正多边形的外接圆半径或边长（逻辑坐标单位），必须 > 0。
 *         - Spacing（点间距）：细分后相邻点之间的最小距离，必须 > 0 且 <= SideLength。
 *         - CenterX, CenterY：生成中心位置，默认 (0,0)。
 *       若 `Spacing > SideLength`，则细分无效，引擎抛出 `InvalidParameterException` 并提示调整。
 *
 *     4.8.3 生成流程
 *       1. 根据 `ShapeType` 和 `SideLength` 计算基础形状的外接圆半径。
 *       2. 根据 `Spacing` 计算每个边上的细分段数 `n = floor(SideLength / Spacing)`，确保 `n >= 1`。
 *       3. 生成所有细分顶点坐标：
 *          - **三角形**：按层次递归细分，直到边长 <= Spacing。
 *          - **四边形**：生成 `(n+1) × (n+1)` 的**完整正方形网格**，不做圆形
 *            裁剪；仅受 [-500, 500]² 方形边界约束，超出边界的点跳过（见 6.3）。
 *          - **六边形**：按 4.8.1-a 的同心六边形环规格生成（中心 + 各层整点）。
 *       4. 将顶点坐标平移到 `(CenterX, CenterY)`。
 *       5. 根据细分结构自动生成超空间航道（连接相邻细分点）。
 *       6. 自动分配系统 ID（按补零规则）并追加到当前静态地图的 `Systems` 列表中。
 *       7. 若生成过程中发现与已有系统坐标重叠（距离 < 0.5），则跳过该点并记录警告。
 *
 *     4.8.4 用户确认机制
 *       引擎提供 `PreviewLattice(options)` 方法，可预计算并返回生成的点集和航道列表（不写入内存），
 *       供用户预览。用户确认后，调用 `ApplyLattice()` 正式写入。
 *
 * 4.9 单点超空间航道创建（Connect To 3 Neighbors）
 *     - **接口**：`void ConnectToThreeNeighbors(string mapName, string systemId)`
 *     - **行为**：
 *       1. 在指定静态地图中查找 `systemId` 对应的系统。
 *       2. 若不存在，抛出 `KeyNotFoundException`。
 *       3. 计算当前点到地图中所有其他点的距离，按升序排序。
 *       4. 取最近的 3 个点（距离必须 > 0.1，避免自连），并创建从当前点到这三个点的超空间航道（`add_hyperlane`）。
 *       5. 若地图中总点数 < 4，则无法生成 3 条航道，抛出 `InvalidOperationException` 并提示点数不足。
 *       6. 若已有航道与生成的航道重复（相同 `from`/`to`），则跳过重复项并记录警告。
 *
 * 4.10 单点类别设置（Point Category）
 *     - **接口**：`void SetSystemCategory(string mapName, string systemId, string category)`
 *     - **数据结构扩展**：`SystemEntry` 增加属性 `string Category { get; set; }`，默认值为 `"normal"`。
 *     - **预定义类别**（推荐）：
 *       - `"normal"`：普通星系。
 *       - `"starting"`：玩家起始星系。
 *       - `"fallen"`：堕落帝国星系。
 *       - `"special"`：特殊事件星系（如 L-星团、守护者）。
 *     - **行为**：
 *       1. 若指定的 `systemId` 不存在，抛出 `KeyNotFoundException`。
 *       2. 更新对应 `SystemEntry` 的 `Category` 字段。
 *       3. 该类别会作为额外子节点写入静态地图文件，格式为 `category = "normal"`（位于 `system` 块内部）。
 *
 *
 * 第五章：图像转点阵算法详述
 * ==========================
 *
 * 5.1 功能入口
 *     `GeneratePointsFromImage(string mapName, string imagePath, ImageGenerationOptions options)`
 *     将指定 PNG 图像转换为静态地图的星系坐标点集。
 *
 * 5.2 图层选择与像素值计算
 *     输入图像为 PNG（RGBA）。用户可指定：
 *       - 单图层：直接取该通道值（归一化到 [0,1]）。
 *       - 反向：`1 - 通道值`。
 *       - 多图层复合：按指定的 `CompositeMode`（Add、Multiply、Average）计算。
 *     若未指定任何图层，默认使用全部图层，按下方公式计算。
 *     最终像素权重 `p = (A / A_max) × ((R + G + B) / (R_max + G_max + B_max))`，
 *     各通道最大值（A_max、R_max 等）取 255。用户指定图层选择后，
 *     **不用哪个通道就把哪个通道从公式中移除**：
 *       - 单图层 R：p = R / R_max
 *       - 单图层 G：p = G / G_max
 *       - 单图层 B：p = B / B_max
 *       - 单图层 A：p = A / A_max
 *       - 多图层复合（Add / Multiply / Average）：仅对选中的通道按上式计算
 *       - 反向通道（InverseR 等）：p = 1 − 通道值 / 通道最大值
 *     阈值过滤：`p < Threshold` 的像素视为无效，不参与生成。
 *
 * 5.3 密度归一化与采样概率
 *     所有有效像素的 `p` 值构成权重分布，采样概率 `P(i) ∝ p_i^Gamma`。
 *     若 `Gamma != 1`，则对权重进行指数调整（默认 1）。
 *
 * 5.4 生成模式算法
 *     a) 按最小间距生成（`Mode = Spacing`）：
 *        1. 构建候选点列表（所有有效像素坐标映射到逻辑坐标系）。
 *        2. 按权重降序排序候选点。
 *        3. 初始化空网格（SpatialGrid，网格单元大小 = MinDistance）。
 *        4. 遍历候选点，若与网格中已有邻点距离 >= MinDistance，则接受该点并加入网格。
 *        5. 返回接受的坐标点。
 *     b) 按总数生成（`Mode = Count`）：
 *        1. 计算总权重和。
 *        2. 重复 `TotalCount` 次：
 *           a) 随机选择一个像素（权重为 p^Gamma）。
 *           b) 检查与已选点的最小距离，若满足则接受，否则重试（最多 MaxAttempts 次）。
 *        3. 若未达到 TotalCount，返回已选点并记录警告。
 *
 * 5.5 输出
 *     生成的坐标点以**创建模式**追加到当前静态地图的 `Systems` 列表中
 *     （不覆盖现有系统，不做合并）；随后自动触发 ID 重编号（4.2）与
 *     边界校验（6.3）。图像转点阵本身不含编辑模式（点编辑交互见 4.4-e）。
 *
 *
 * 第六章：错误处理与边缘情况
 * ==========================
 *
 * 6.1 ID 冲突
 *     加载时检测到重复 ID 时，抛出 `InvalidOperationException`，
 *     异常消息包含冲突 ID 和文件路径，并列出所有涉及该 ID 的航道。
 *
 * 6.2 航道断裂
 *     若航道的 `FromId` 或 `ToId` 指向不存在的星系 ID，引擎应在加载时
 *     记录警告日志，并**丢弃该航道**（不保存到内存，也不写入文件）。
 *
 * 6.3 坐标越界与缩放
 *     静态地图的合法范围是 [-500, 500] × [-500, 500] 方形（见术语定义）。
 *     在变换、图像生成或网格生成后，若存在坐标超出该方形，引擎应记录警告，
 *     并按 `scale = 500 / max(|x|, |y|)`（取所有点中最大绝对值）整体缩放至
 *     方形内，保持中心不变。
 *
 * 6.4 图像读取失败
 *     若 PNG 文件不存在或解析失败，抛出 `FileNotFoundException` 或 `InvalidDataException`。
 *
 * 6.5 精度与性能
 *     默认坐标精度为 2 位小数，用户可通过 `SetCoordinatePrecision(int digits)`
 *     调整；内部计算仍使用 `double`，仅在序列化时格式化输出。
 *
 * 6.6 网格生成参数校验
 *     - `SideLength` 和 `Spacing` 必须为正数（> 0），否则抛出 `ArgumentOutOfRangeException`。
 *     - 若 `Spacing > SideLength`，细分段数 `n` 为 0，此时引擎提示用户减小间距或增大边长，不执行生成。
 *     - 若计算出的总点数超过 10000（防止内存溢出），引擎自动拒绝并提示用户调整参数。
 *
 * 6.7 单点连接失败降级
 *     - 若地图中总点数 < 4，`ConnectToThreeNeighbors` 抛出异常，并给出当前点数，建议用户先生成更多点。
 *     - 若最近的 3 个点中某一点与当前点之间已存在航道，则跳过该航道，并记录 Debug 日志。
 *
 * 6.8 类别值容错
 *     - 若用户传入的 `category` 为 null 或空白字符串，自动重置为 `"normal"`。
 *     - 引擎不对类别值做任何语义校验，完全由上层（游戏或 UI）解释。
 *
 * 6.9 参数缺失与默认值
 *     所有可空参数在缺失时使用规范中定义的默认值。若默认值不明确（如图像转点阵的图层选择），
 *     则采用“全部图层”作为默认。
 *
 * 6.10 用户输入尽力解析
 *     引擎对所有数值参数进行范围校验（如 radius 必须 >0），若输入非数字则使用默认值并记录警告。
 *     对于字符串参数（如形状名），若不存在于样式表中，则忽略该条目并记录警告。
 *     所有异常均被捕获并转换为操作状态码，不崩溃。
 *
 * ============================================================================
 * 规范结束
 * ============================================================================
 */
namespace Stellaris.Engine.GalaxyMap;