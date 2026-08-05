/*
 * ============================================================================
 * STELLARIS LOCAL CONFIG MANAGER STANDARD SPECIFICATION (REVISION 3.0)
 * ============================================================================
 * 本规范为本地配置管理引擎（LocalConfigManager / IConfigManager）实现的
 * 唯一权威依据。所有实现必须严格遵循本规范定义的接口签名、数据结构、
 * 持久化格式、点路径规则与边界条件。
 * 本规范在逻辑上优先于任何现有代码实现，所有实现偏差均视为缺陷。
 * ============================================================================
 *
 *
 * 术语定义
 * --------
 * - 类别（Category）：配置文件的标识（文件名，不含扩展名），如 "galaxy"。
 *   类别名只允许不含非法文件名字符的字符串，防止路径遍历。
 * - 配置键（Key）：配置项的名称，支持点路径（见第三章）。
 * - 本地配置（Local Config）：独立于游戏数据的用户偏好/运行时覆盖设置，
 *   存储于 {configRootPath}/{category}.json 文件中。
 *
 *
 * 第一章：引擎概述与架构
 * ======================
 *
 * 1.1 引擎定位
 *     本地配置管理引擎提供基于 JSON 文件的键值配置存储，支持内存缓存、
 *     原子写入、点路径嵌套结构与批量更新。上层引擎（如 GalaxyStyleEngine）
 *     通过 IConfigManager 接口使用，不直接操作配置文件。
 *
 * 1.2 公开接口（IConfigManager）
 *     - void Set(string category, string key, object value)
 *     - object Get(string category, string key)
 *     - void Delete(string category, string key)
 *     - bool Exists(string category, string key)
 *     - IReadOnlyDictionary<string, object>? GetAll(string category)
 *     - void Reload(string category)
 *     - void ReloadAll()
 *     - void SetBatch(string category, IDictionary<string, object> values)
 *
 * 1.3 实现类（LocalConfigManager）
 *     LocalConfigManager(string configRootPath, ILogger<LocalConfigManager>? logger = null)
 *     - configRootPath 为空 → ArgumentException；构造时创建目录，
 *       创建失败抛 InvalidOperationException。
 *     - 内部状态：ConcurrentDictionary<string, JsonObject> _cache（类别 → JSON 对象）、
 *       object _fileLock（文件读写互斥）。
 *
 *
 * 第二章：持久化与缓存
 * ====================
 *
 * 2.1 文件路径
 *     文件路径 = Path.Combine(configRootPath, $"{category}.json")。
 *     category 为空 → ArgumentException；包含非法文件名字符
 *     （Path.GetInvalidFileNameChars）→ ArgumentException。
 *
 * 2.2 内存缓存
 *     - LoadOrCreateJson(category, createIfMissing)：缓存命中直接返回缓存
 *       引用（所有修改必须经由引擎方法以保证一致）；否则从磁盘读取并解析
 *       （根节点必须为 JSON 对象，否则抛 InvalidOperationException）；
 *       文件不存在且 createIfMissing 为 true 时创建空 JsonObject 并入缓存。
 *     - LoadJsonIfExists(category)：缓存命中返回；文件不存在返回 null；
 *       解析失败抛 InvalidOperationException。
 *
 * 2.3 原子写入（SaveJson）
 *     流程：
 *       1) 序列化当前 JsonObject 为缩进 JSON 文本。
 *       2) 写入 "{path}.temp"。
 *       3) 目标文件存在则删除，File.Move(temp, path) 替换。
 *       4) 更新内存缓存为该 JsonObject。
 *       5) 任一环节失败：删除临时文件，抛 InvalidOperationException。
 *     序列化选项必须配置 TypeInfoResolver（如 DefaultJsonTypeInfoResolver），
 *     否则 .NET 10 下 JsonValue.Create 生成的节点序列化必然抛异常。
 *
 * 2.4 值类型转换（ConvertNodeToObject）
 *     将 JsonNode 转为 .NET 对象：
 *       - JsonValue：按 TryGetValue 链依次尝试 bool → int → long → double
 *         → string，首个成功者返回；全部失败回退 GetValue<object>()。
 *       - JsonObject / JsonArray：返回 DeepClone 后的节点（避免外部修改
 *         污染内存缓存）。
 *     GetAll 对每个顶层条目应用此转换；Get 对目标节点应用此转换。
 *
 *
 * 第三章：点路径规则（REVISION 3.0 新增）
 * ========================================
 *
 * 3.1 键的点路径语义
 *     键可使用点分隔的路径（如 "styles.spiral_2.preview"），映射到 JSON
 *     的嵌套对象结构：
 *       "styles.spiral_2.preview" → { "styles": { "spiral_2": { "preview": ... } } }
 *     无点的键（如 "foo"）行为与旧版一致（顶层键）。
 *
 * 3.2 写入（Set / SetBatch → SetNode）
 *     按路径逐段遍历：中间节点不存在或非对象时自动创建空 JsonObject；
 *     最终段写入 JsonValue.Create(value)。value 为 null 时抛 ArgumentException。
 *
 * 3.3 读取（Get → GetNode）
 *     按路径逐段遍历；任一段不存在或中间节点非对象 → 返回 null，
 *     由调用方抛 KeyNotFoundException（含类别与键）。
 *
 * 3.4 存在性（Exists → GetNode）
 *     路径可达且最终段存在 → true，否则 false。
 *
 * 3.5 删除（Delete → RemoveNode）
 *     递归到达最终段所在父对象并 Remove；中间节点缺失 → 静默返回
 *     （不视为错误）。删除后保存文件；文件/键不存在时静默返回。
 *
 * 3.6 读取隔离
 *     GetAll 返回顶层条目（"global"、"styles" 等），嵌套对象以深拷贝
 *     JsonNode 形式返回；上层负责继续向下解析。
 *
 *
 * 第四章：公开接口行为细节
 * ========================
 *
 * 4.1 Set(category, key, value)
 *     校验：category / key 非空，value 非 null（违反抛 ArgumentException）。
 *     流程：LoadOrCreateJson → SetNode → SaveJson（立即落盘）。
 *
 * 4.2 Get(category, key)
 *     校验：category / key 非空。
 *     类别文件不存在或键不存在 → KeyNotFoundException（消息含类别与键）。
 *     成功 → ConvertNodeToObject(目标节点)。
 *
 * 4.3 Delete(category, key)
 *     校验：category / key 非空。
 *     类别文件不存在或键不存在 → 静默返回；删除成功后 SaveJson。
 *
 * 4.4 Exists(category, key)
 *     校验：category / key 非空。返回路径可达性。
 *
 * 4.5 GetAll(category)
 *     校验：category 非空。类别文件不存在 → 返回 null；
 *     存在 → 返回顶层条目字典（值经 ConvertNodeToObject 转换）。
 *
 * 4.6 Reload(category)
 *     移除该类别缓存，下次访问时重新读取磁盘。
 *
 * 4.7 ReloadAll()
 *     移除全部类别缓存。
 *
 * 4.8 SetBatch(category, values)
 *     校验：values 为 null → ArgumentNullException；含 null 值 →
 *     ArgumentException；values 为空 → 直接返回（无操作）。
 *     流程：LoadOrCreateJson → 逐条 SetNode（支持点路径）→ SaveJson
 *     （单次原子写入）。
 *
 *
 * 第五章：错误处理与日志
 * ======================
 * 5.1 参数错误（空值、非法类别名）→ ArgumentException / ArgumentNullException。
 * 5.2 配置缺失（Get/Exists 目标不存在）→ KeyNotFoundException。
 * 5.3 文件解析失败、写入失败 → InvalidOperationException（记录 Error 日志）。
 * 5.4 日志使用 Microsoft.Extensions.Logging 标准接口。
 *
 * ============================================================================
 * 规范结束
 * ============================================================================
 */

namespace Stellaris.Engine.LocalConfigManager;
