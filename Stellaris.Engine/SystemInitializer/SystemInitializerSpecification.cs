// 文件: Stellaris.Engine/SystemInitializer/SystemInitializerSpecification.cs
// ================================================================
//  恒星系预设引擎规范（SystemInitializerEngine）
// ================================================================
//
// 第一章：职责范围
//   本引擎负责群星"恒星系预设"（solar system initializer）的扫描、解析、
//   以及后续的可视化编辑与写回。它对应游戏数据目录：
//       common/solar_system_initializers/*.txt
//   每个文件顶级块定义一个预设，例如：
//       initializer_sol = {
//           name = "sol"
//           usage = custom_empire
//           ...
//       }
//   静态地图（StaticMapPage）中每个恒星系 system 的 initializer 字段引用这些预设名。
//
// 第二章：数据来源与优先级
//   2.1 目录位置：common/solar_system_initializers/，遍历 StellarisAdapter.Roots
//       全部 root（顺序 = 优先级，列表靠前者优先级高）。
//   2.2 同名预设：高优先级 root 覆盖低优先级 root 的同名预设（第一阶段仅收集名字，
//       去重时不区分来源；第二阶段解析时按优先级覆盖）。
//   2.3 只读规则：游戏本体等外部 root 的文件只读不写；本引擎产生的写入一律
//       落在 Roots 末位（mod 目录），覆盖性兼容由 mod 文件达成（与全局规则一致）。
//
// 第三章：第一阶段——扫描（当前已实现）
//   3.1 API：List<string> GetAvailableInitializers()
//       - 遍历全部 root 的 common/solar_system_initializers/*.txt；
//       - 按行粗略提取顶级 key（跳过注释与嵌套行），去重后按字典序排序返回；
//       - 目录不可读时返回空列表，不抛异常。
//   3.2 用途：静态地图"点设置"弹窗的 initializer 下拉候选；后续可视化编辑的入口列表。
//   3.3 局限（第一阶段）：仅返回名字，不解析参数结构；嵌套 key 误判风险由
//       "顶级行"启发式缓解（initializer 文件的顶级块 key 均顶格书写，实际风险很低）。
//
// 第四章：后续规划（占位，未实现）
//   4.1 解析：把 initializer 文件解析为结构化模型（名字、参数树），
//       经 StellarisAdapter.GetConfig/AddConfigNode 读写 AST，禁止直接操作磁盘。
//   4.2 可视化编辑：预设参数树编辑器（星体、轨道、资源、特殊对象等），
//       编辑结果在用户显式保存时写回 mod 目录（保存必须用户确认，规整化绝不直接写盘）。
//   4.3 联动：静态地图 system.initializer 字段选择后，可预览/引用该预设的结构化参数。
//   4.4 新建/复制/删除预设：同 GalaxyMap 的右键菜单模式（先 X 一次再 Y 一次……）。
//
// 第五章：API 索引
//   文件：Stellaris.Engine/SystemInitializer/SystemInitializerEngine.cs
//   - SystemInitializerEngine(StellarisAdapter adapter, ILogger? logger)  构造
//   - List<string> GetAvailableInitializers()                            扫描全部预设名
// ================================================================
namespace Stellaris.Engine.SystemInitializer;

/// <summary>恒星系预设引擎规范占位（规范正文见文件头注释）。</summary>
public static class SystemInitializerSpecification
{
}
