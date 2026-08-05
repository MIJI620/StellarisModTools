using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Stellaris.Parser
{
    /// <summary>
    /// 内联脚本展开器。严格遵循规范 2.1 ~ 2.3。
    /// 展开后，inline_script 节点被替换为其展开后的节点列表（平铺），
    /// 而非包裹成单一节点。
    /// </summary>
    public class ScriptExpander
    {
        private readonly List<string> _roots;
        private readonly HashSet<string> _expandingPaths = new();
        private readonly ILogger _logger;

        public ScriptExpander(List<string> roots, ILogger? logger = null)
        {
            _roots = roots ?? new List<string>();
            _logger = logger ?? NullLogger.Instance;
        }

        public List<AstNode> Expand(AstNode node)
        {
            if (node == null)
                return new List<AstNode>();

            // 内联脚本识别位于引擎层（本展开器），不依赖解析器特判：
            // - NodeType.InlineScript：兼容旧解析产物
            // - Block 且键名为 "inline_script"：新解析产物的通用块（引擎层按
            //   黑箱引擎唯一支持的内联键名识别，键名不可更改）
            if (node.Type == NodeType.InlineScript ||
                (node.Type == NodeType.Block && node.Key == "inline_script"))
            {
                return ExpandInlineScript(node);
            }

            if (node.Type == NodeType.Block || node.Type == NodeType.List)
            {
                var newChildren = new List<AstNode>();
                foreach (var child in node.Children)
                {
                    var expandedChildren = Expand(child);
                    newChildren.AddRange(expandedChildren);
                }
                node.Children = newChildren;
                return new List<AstNode> { node };
            }

            return new List<AstNode> { node };
        }

        private List<AstNode> ExpandInlineScript(AstNode node)
        {
            string? scriptPath = null;
            var localParams = new Dictionary<string, object?>();

            foreach (var child in node.Children)
            {
                if (child.Type == NodeType.Simple)
                {
                    if (child.Key == "script")
                    {
                        scriptPath = child.Value?.ToString();
                    }
                    else
                    {
                        localParams[child.Key ?? string.Empty] = child.Value;
                    }
                }
            }

            if (string.IsNullOrEmpty(scriptPath))
            {
                _logger.LogWarning("inline_script 节点缺少 'script' 参数，展开失败");
                _logger.LogError("内联脚本缺少 script 参数，原节点被保留");
                return new List<AstNode> { node };
            }

            string normalizedPath = scriptPath.Replace('\\', '/');
            if (_expandingPaths.Contains(normalizedPath))
            {
                _logger.LogWarning("检测到内联脚本循环引用: {Path}，停止展开", normalizedPath);
                _logger.LogError("内联脚本循环引用，原节点被保留");
                return new List<AstNode> { node };
            }
            _expandingPaths.Add(normalizedPath);

            try
            {
                string? content = LoadScriptContent(scriptPath);
                if (string.IsNullOrEmpty(content))
                {
                    _logger.LogWarning("内联脚本文件不存在: common/inline_scripts/{Path}.txt", scriptPath);
                    _logger.LogError("内联脚本文件不存在，原节点被保留");
                    return new List<AstNode> { node };
                }

                var textReplacer = new TextReplacer(localParams);
                string replacedContent = textReplacer.Replace(content);

                var lexer = new Lexer(replacedContent);
                var tokens = new List<Token>();
                Token tok;
                while ((tok = lexer.NextToken()).Type != TokenType.Eof)
                    tokens.Add(tok);

                var lines = replacedContent.Split('\n');
                var parser = new Parser(tokens, lines, scriptPath, replacedContent);
                var result = parser.Parse();

                if (!result.Success || result.RootNodes.Count == 0)
                {
                    _logger.LogWarning("内联脚本解析失败: {Path}", scriptPath);
                    _logger.LogError("内联脚本解析失败，原节点被保留");
                    return new List<AstNode> { node };
                }

                var expandedNodes = new List<AstNode>();
                foreach (var rootNode in result.RootNodes)
                {
                    var expandedChildren = Expand(rootNode);
                    expandedNodes.AddRange(expandedChildren);
                }

                // 修正1：保留原 inline_script 节点的注释，附加到展开后的第一个节点
                if (expandedNodes.Count > 0 && node.AssociatedComments.Count > 0)
                {
                    foreach (var comment in node.AssociatedComments)
                        expandedNodes[0].AssociatedComments.Add(comment);
                }

                foreach (var expanded in expandedNodes)
                {
                    expanded.StartLine = node.StartLine;
                    expanded.EndLine = node.EndLine;
                    expanded.StartColumn = node.StartColumn;
                    expanded.EndColumn = node.EndColumn;
                }

                return expandedNodes;
            }
            finally
            {
                _expandingPaths.Remove(normalizedPath);
            }
        }

        private string? LoadScriptContent(string scriptPath)
        {
            foreach (var root in _roots.AsEnumerable().Reverse())
            {
                string relPath = Path.Combine("common", "inline_scripts", scriptPath + ".txt");
                string fullPath = Path.Combine(root, relPath);
                if (File.Exists(fullPath))
                    return File.ReadAllText(fullPath);
            }
            return null;
        }
    }
}