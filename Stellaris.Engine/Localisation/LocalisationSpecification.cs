/*
 * ============================================================================
 * STELLARIS LOCALISATION DICTIONARY ENGINE STANDARD SPECIFICATION (v1.0)
 * ============================================================================
 * 本规范为语言字典引擎（LocalisationDictionaryEngine）实现的唯一权威依据。
 * 本规范在逻辑上优先于任何现有代码实现，所有实现偏差均视为缺陷。
 * ============================================================================
 *
 * 定位与权限（强制）
 * ------------------
 * - 本引擎**只读**：查询本地化条目，无写入、无修改、无落盘。
 * - 本引擎是**唯一**允许使用正则的位置（用户授权：仅此处、仅读取）。
 *
 *
 * 数据模型（LocalisationEntryView）
 * ---------------------------------
 * - Language      语言标识（如 simp_chinese / english）。
 * - Key           本地化键。
 * - DisplayValue  显示值（经历过替换展开后的值）。
 * - LogicalValue  逻辑值（原文，含 $var$ 等替换占位——写回时用逻辑值）。
 * - RelativePath  条目所在文件的相对路径。
 * - AbsolutePath  条目所在文件的绝对路径。
 *
 *
 * 查询（Query）
 * -------------
 * 签名：Query(string? language, string? keyPattern, string? valuePattern, bool ignoreCase = false)。
 * 语义：按语种 + 正则匹配 key / 显示值查询全部本地化条目（UI 区分大小写按钮 → ignoreCase）。
 *
 *
 * 语言导航（UI 层——LanguageDictionaryPage）
 * ------------------------------------------
 * - 竖排语言列表（全部 + 各语言——显示当前界面语言下的译名 GetLanguageDisplayNameLocalized）。
 * - 切换语言导航只做本地过滤（不重新搜索——基于最近一次搜索结果缓存）。
 * - 区分大小写为切换按钮（ToggleButton）：按下（灰）= 区分大小写。
 * - 详情面板（右侧）：选中条目显示 Key/语言译名/显示值/逻辑值/绝对路径（行末复制按钮）。
 *
 *
 * ============================================================================
 * 附录 A：公开 API 索引
 * ============================================================================
 * 文件：Stellaris.Engine/Localisation/LocalisationDictionaryEngine.cs
 * - GetLanguages()                 全部可用语言。
 * - Query(language, keyPattern, valuePattern, caseSensitive, …)  查询（唯一正则处）。
 * 数据类：LocalisationEntryView（Language/Key/DisplayValue/LogicalValue/RelativePath/AbsolutePath）。
 * 页面：Stellaris.Editor/Pages/LanguageDictionaryPage.xaml(.cs)——顶部全宽搜索行
 *       （字段下拉 + 输入框 + Aa 区分大小写按钮 + 🔍）+ 语言导航 + 结果表 + 详情面板。
 * 语言名转换：Stellaris.Editor/Pages/LanguageNameConverter.cs。
 *
 * ============================================================================
 * 规范结束
 * ============================================================================
 */

namespace Stellaris.Engine.Localisation;
