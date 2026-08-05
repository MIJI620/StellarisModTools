// 文件: Stellaris.Engine/LocalConfigManager/LocalConfigManagerEngine.cs

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Stellaris.Engine.LocalConfigManager
{
    /// <summary>
    /// 本地配置管理引擎的公开接口。
    /// 用于存储用户偏好、行为策略、导出模板等配置数据，与游戏数据隔离。
    /// </summary>
    public interface IConfigManager
    {
        /// <summary>
        /// 设置指定类别下的键值对，并立即原子写入磁盘。
        /// </summary>
        /// <param name="category">配置类别（即文件名，不含扩展名）。</param>
        /// <param name="key">配置键。</param>
        /// <param name="value">配置值（不能为 null）。</param>
        void Set(string category, string key, object value);

        /// <summary>
        /// 获取指定类别下的键值。
        /// </summary>
        /// <param name="category">配置类别。</param>
        /// <param name="key">配置键。</param>
        /// <returns>配置值（object 类型）。</returns>
        /// <exception cref="KeyNotFoundException">当类别文件不存在或键不存在时抛出。</exception>
        object Get(string category, string key);

        /// <summary>
        /// 删除指定类别下的键值对。若文件或键不存在则静默返回。
        /// </summary>
        void Delete(string category, string key);

        /// <summary>
        /// 检查指定类别下键是否存在。
        /// </summary>
        bool Exists(string category, string key);

        /// <summary>
        /// 获取指定类别的所有配置项（只读副本）。
        /// </summary>
        /// <returns>若文件存在则返回只读字典，否则返回 null。</returns>
        IReadOnlyDictionary<string, object>? GetAll(string category);

        /// <summary>
        /// 强制从磁盘重新加载指定类别的配置，丢弃内存缓存。
        /// </summary>
        void Reload(string category);

        /// <summary>
        /// 强制从磁盘重新加载所有已加载的类别配置。
        /// </summary>
        void ReloadAll();

        /// <summary>
        /// 一次性设置同一类别下的多个键值对，并原子写入磁盘。
        /// </summary>
        /// <param name="values">要设置的键值对集合，所有值不能为 null。</param>
        void SetBatch(string category, IDictionary<string, object> values);
    }

    /// <summary>
    /// 本地配置管理引擎实现，使用 JSON 文件持久化，支持内存缓存和原子写入。
    /// </summary>
    public sealed class LocalConfigManager : IConfigManager
    {
        private readonly string _configRootPath;
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<string, JsonObject> _cache = new();
        private readonly object _fileLock = new();

        // .NET 10 要求 JsonSerializerOptions 必须配置 TypeInfoResolver 才能序列化
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };

        /// <summary>
        /// 初始化配置管理引擎。
        /// </summary>
        /// <param name="configRootPath">配置文件存储根目录。</param>
        /// <param name="logger">日志记录器，可为 null。</param>
        /// <exception cref="ArgumentException">当 configRootPath 为空或无效时抛出。</exception>
        public LocalConfigManager(string configRootPath, ILogger<LocalConfigManager>? logger = null)
        {
            if (string.IsNullOrWhiteSpace(configRootPath))
                throw new ArgumentException("配置根目录不能为空", nameof(configRootPath));

            _configRootPath = configRootPath;
            _logger = logger ?? NullLogger<LocalConfigManager>.Instance;

            // 确保根目录存在
            try
            {
                Directory.CreateDirectory(_configRootPath);
                _logger.LogDebug("配置根目录已确保存在: {Path}", _configRootPath);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"无法创建配置根目录: {_configRootPath}", ex);
            }
        }

        // ===== 辅助方法 =====

        private string GetFilePath(string category)
        {
            if (string.IsNullOrWhiteSpace(category))
                throw new ArgumentException("类别不能为空", nameof(category));

            // 防止路径遍历攻击，只允许字母数字下划线短横线（可根据需要放宽）
            if (category.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException($"类别名包含非法文件名字符: {category}", nameof(category));

            return Path.Combine(_configRootPath, $"{category}.json");
        }

        private JsonObject LoadOrCreateJson(string category, bool createIfMissing = true)
        {
            string filePath = GetFilePath(category);
            lock (_fileLock)
            {
                // 先尝试从缓存获取
                if (_cache.TryGetValue(category, out var cached))
                {
                    // 返回深拷贝？考虑到 JsonObject 是可变的，但我们在修改后会更新缓存，所以直接返回引用是安全的（因为所有修改都会通过 Set 等方法来更新）
                    // 但为了防御外部修改，我们返回一个克隆。但为了性能，我们返回引用，但确保所有修改都通过引擎方法。
                    // 这里返回缓存的引用，因为引擎会控制修改。
                    return cached;
                }

                JsonObject? json = null;
                if (File.Exists(filePath))
                {
                    try
                    {
                        string content = File.ReadAllText(filePath);
                        var node = JsonNode.Parse(content);
                        if (node is JsonObject obj)
                            json = obj;
                        else
                            throw new InvalidOperationException($"JSON 文件格式错误：根对象不是对象类型。文件: {filePath}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "读取或解析 JSON 文件失败: {Path}", filePath);
                        throw new InvalidOperationException($"读取配置失败: {filePath}", ex);
                    }
                }

                if (json == null && createIfMissing)
                {
                    json = new JsonObject();
                }

                if (json != null)
                {
                    _cache[category] = json;
                }

                return json ?? new JsonObject(); // 如果 createIfMissing 为 false 且文件不存在，返回 null 并调用者自行处理
            }
        }

        // 注意：对于不存在文件的情况，我们让调用方法检查 null
        private JsonObject? LoadJsonIfExists(string category)
        {
            string filePath = GetFilePath(category);
            lock (_fileLock)
            {
                if (_cache.TryGetValue(category, out var cached))
                    return cached;

                if (!File.Exists(filePath))
                    return null;

                try
                {
                    string content = File.ReadAllText(filePath);
                    var node = JsonNode.Parse(content);
                    if (node is JsonObject obj)
                    {
                        _cache[category] = obj;
                        return obj;
                    }
                    throw new InvalidOperationException($"JSON 文件根对象不是对象类型。文件: {filePath}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "读取 JSON 文件失败: {Path}", filePath);
                    throw new InvalidOperationException($"读取配置失败: {filePath}", ex);
                }
            }
        }

        /// <summary>递归排序 JsonObject 的键（字母升序）；数组保持元素顺序，数组内对象也排序。</summary>
        private static JsonNode SortJsonObject(JsonNode node)
        {
            if (node is JsonArray arr)
            {
                var sa = new JsonArray();
                foreach (var item in arr)
                    sa.Add(item is JsonObject or JsonArray ? SortJsonObject(item) : item?.DeepClone());
                return sa;
            }
            var obj = (JsonObject)node;
            var sorted = new JsonObject();
            foreach (var kv in obj.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var v = kv.Value;
                // JsonNode 有父引用——标量必须 DeepClone（否则"node already has a parent"）
                sorted[kv.Key] = v is JsonObject or JsonArray ? SortJsonObject(v) : v?.DeepClone();
            }
            return sorted;
        }

        private void SaveJson(string category, JsonObject data)
        {
            string filePath = GetFilePath(category);
            string tempPath = filePath + ".temp";
            lock (_fileLock)
            {
                try
                {
                    // 格式化：保存时按 key 字母升序排序（递归——嵌套对象与数组内对象同样排序，数组顺序保持）
                    var sortedData = SortJsonObject(data);
                    string jsonString = sortedData.ToJsonString(JsonOptions);
                    File.WriteAllText(tempPath, jsonString);
                    // 原子替换
                    if (File.Exists(filePath))
                        File.Delete(filePath);
                    File.Move(tempPath, filePath);

                    // 更新缓存
                    _cache[category] = data;
                    _logger.LogDebug("配置文件已保存: {Path}", filePath);
                }
                catch (Exception ex)
                {
                    // 清理临时文件
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                    _logger.LogError(ex, "写入配置文件失败: {Path}", filePath);
                    throw new InvalidOperationException($"写入配置失败: {filePath}", ex);
                }
            }
        }

        // ===== 公开方法 =====

        /// <inheritdoc />
        public void Set(string category, string key, object value)
        {
            if (string.IsNullOrWhiteSpace(category))
                throw new ArgumentException("类别不能为空", nameof(category));
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("键不能为空", nameof(key));
            if (value == null)
                throw new ArgumentException("值不能为 null", nameof(value));

            var json = LoadOrCreateJson(category, createIfMissing: true);
            SetNode(json, key, value);
            SaveJson(category, json);
        }

        /// <inheritdoc />
        public object Get(string category, string key)
        {
            if (string.IsNullOrWhiteSpace(category))
                throw new ArgumentException("类别不能为空", nameof(category));
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("键不能为空", nameof(key));

            var json = LoadJsonIfExists(category);
            if (json == null)
                throw new KeyNotFoundException($"配置类别 '{category}' 不存在");

            JsonNode? node = GetNode(json, key);
            if (node == null)
                throw new KeyNotFoundException($"键 '{key}' 在类别 '{category}' 中不存在");

            return ConvertNodeToObject(node);
        }

        /// <inheritdoc />
        public void Delete(string category, string key)
        {
            if (string.IsNullOrWhiteSpace(category))
                throw new ArgumentException("类别不能为空", nameof(category));
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("键不能为空", nameof(key));

            var json = LoadJsonIfExists(category);
            if (json == null)
                return;

            if (!RemoveNode(json, SplitPath(key), 0))
                return; // 键不存在，静默返回

            // 若对象为空，保留空对象（也可以选择删除文件，但规范推荐保留空文件）
            SaveJson(category, json);
        }

        /// <inheritdoc />
        public bool Exists(string category, string key)
        {
            if (string.IsNullOrWhiteSpace(category))
                throw new ArgumentException("类别不能为空", nameof(category));
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("键不能为空", nameof(key));

            var json = LoadJsonIfExists(category);
            if (json == null)
                return false;

            return GetNode(json, key) != null;
        }

        /// <inheritdoc />
        public IReadOnlyDictionary<string, object>? GetAll(string category)
        {
            if (string.IsNullOrWhiteSpace(category))
                throw new ArgumentException("类别不能为空", nameof(category));

            var json = LoadJsonIfExists(category);
            if (json == null)
                return null;

            var dict = new Dictionary<string, object>();
            foreach (var kvp in json)
            {
                if (kvp.Value != null)
                    dict[kvp.Key] = ConvertNodeToObject(kvp.Value);
            }
            return dict;
        }

        /// <inheritdoc />
        public void Reload(string category)
        {
            if (string.IsNullOrWhiteSpace(category))
                throw new ArgumentException("类别不能为空", nameof(category));

            lock (_fileLock)
            {
                // 从缓存中移除
                _cache.TryRemove(category, out _);
                // 重新加载（下次访问时会自动读取）
                _logger.LogDebug("已重载类别: {Category}", category);
            }
        }

        /// <inheritdoc />
        public void ReloadAll()
        {
            lock (_fileLock)
            {
                foreach (var key in _cache.Keys)
                {
                    _cache.TryRemove(key, out _);
                }
                _logger.LogDebug("已重载所有类别");
            }
        }

        /// <inheritdoc />
        public void SetBatch(string category, IDictionary<string, object> values)
        {
            if (string.IsNullOrWhiteSpace(category))
                throw new ArgumentException("类别不能为空", nameof(category));
            if (values == null)
                throw new ArgumentNullException(nameof(values));
            if (values.Count == 0)
                return; // 无操作

            // 检查是否有 null 值
            foreach (var kvp in values)
            {
                if (kvp.Value == null)
                    throw new ArgumentException($"键 '{kvp.Key}' 的值为 null，禁止设置 null 值", nameof(values));
            }

            var json = LoadOrCreateJson(category, createIfMissing: true);
            foreach (var kvp in values)
            {
                SetNode(json, kvp.Key, kvp.Value);
            }
            SaveJson(category, json);
        }

        // ===== 点路径嵌套辅助 =====

        /// <summary>
        /// 拆分点路径键（"styles.spiral_2.preview" -> ["styles", "spiral_2", "preview"]）。
        /// 无点的键视为单段路径，行为与旧版一致。
        /// </summary>
        private static string[] SplitPath(string key) => key.Split('.');

        /// <summary>
        /// 按点路径在 JsonObject 中写入值，沿途自动创建缺失的嵌套对象。
        /// 数组（如 RGBA 颜色 int[]）逐元素转为 JsonArray。
        /// </summary>
        private static void SetNode(JsonObject root, string key, object value)
        {
            string[] parts = SplitPath(key);
            var current = root;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                string p = parts[i];
                if (current[p] is JsonObject next)
                {
                    current = next;
                }
                else
                {
                    var newObj = new JsonObject();
                    current[p] = newObj;
                    current = newObj;
                }
            }
            current[parts[^1]] = ToJsonNode(value);
        }

        /// <summary>对象 → JsonNode；数组/枚举（非 string）逐元素递归转 JsonArray，其余用 JsonValue.Create。</summary>
        private static JsonNode? ToJsonNode(object value)
        {
            if (value is System.Collections.IDictionary dict)
            {
                var obj = new JsonObject();
                foreach (System.Collections.DictionaryEntry e in dict)
                    obj[e.Key.ToString()!] = ToJsonNode(e.Value!);
                return obj;
            }
            if (value is System.Collections.IEnumerable seq && value is not string)
            {
                var arr = new JsonArray();
                foreach (var item in seq)
                    arr.Add(ToJsonNode(item));
                return arr;
            }
            return JsonValue.Create(value);
        }

        /// <summary>
        /// 按点路径读取 JsonNode；路径不存在返回 null。
        /// </summary>
        private static JsonNode? GetNode(JsonObject root, string key)
        {
            string[] parts = SplitPath(key);
            JsonNode? current = root;
            for (int i = 0; i < parts.Length; i++)
            {
                if (current is not JsonObject obj)
                    return null;
                if (!obj.TryGetPropertyValue(parts[i], out var child))
                    return null;
                current = child;
            }
            return current;
        }

        /// <summary>
        /// 按点路径删除节点；父级中间节点不存在时返回 false。
        /// </summary>
        private static bool RemoveNode(JsonObject current, string[] parts, int index)
        {
            if (index == parts.Length - 1)
                return current.Remove(parts[index]);

            if (current[parts[index]] is JsonObject next)
                return RemoveNode(next, parts, index + 1);
            return false;
        }

        /// <summary>
        /// 将 JsonNode 转换为 .NET 对象：值类型装箱为基础类型，
        /// 对象/数组返回深拷贝的 JsonNode（避免外部修改内存缓存）。
        /// </summary>
        private static object ConvertNodeToObject(JsonNode node)
        {
            if (node is JsonValue value)
            {
                if (value.TryGetValue<bool>(out bool b)) return b;
                if (value.TryGetValue<int>(out int i)) return i;
                if (value.TryGetValue<long>(out long l)) return l;
                if (value.TryGetValue<double>(out double d)) return d;
                if (value.TryGetValue<string>(out string? s)) return s;
                return value.GetValue<object>();
            }
            return node.DeepClone();
        }
    }
}