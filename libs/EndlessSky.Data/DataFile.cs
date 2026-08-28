using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace EndlessSky.Data
{
    /// <summary>
    /// Parses the Endless Sky data-file syntax into a tree of <see cref="DataNode"/>.
    ///
    /// Faithful port of upstream <c>DataFile::LoadData</c>. The rules that matter:
    ///
    /// * Nesting is by raw count of leading whitespace characters, not by tab width.
    ///   A file is expected to use tabs OR spaces consistently; mixing warns.
    /// * Tokens are whitespace separated. A token may be quoted with " or ` , which
    ///   runs to the next occurrence of that same mark. There are no escape sequences:
    ///   a backtick-quoted token is how upstream embeds a literal double quote.
    /// * '#' begins a comment, but only when encountered outside a token.
    /// * Empty tokens are legal and preserved.
    /// </summary>
    public sealed class DataFile : IEnumerable<DataNode>
    {
        private readonly DataNode _root = new DataNode();

        public DataFile()
        {
        }

        public DataFile(string text, string sourceName = null)
        {
            Load(text, sourceName);
        }

        /// <summary>Nodes at the top level of the file.</summary>
        public IReadOnlyList<DataNode> Nodes => _root.Children;

        public IEnumerator<DataNode> GetEnumerator() => _root.Children.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <summary>Warnings raised while parsing (mixed indentation, unterminated quotes).</summary>
        public List<string> Diagnostics { get; } = new List<string>();

        public static DataFile FromPath(string path)
        {
            var file = new DataFile();
            file.Load(File.ReadAllText(path, new UTF8Encoding(false)), path);
            return file;
        }

        public void Load(string data, string sourceName = null)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            _root.SourceFile = sourceName;

            // Upstream appends a sentinel newline so every line terminates uniformly.
            if (data.Length == 0 || data[data.Length - 1] != '\n')
            {
                data += "\n";
            }

            int pos = 0;
            int end = data.Length;

            // Skip a UTF-8 BOM if present (it decodes to U+FEFF once read as text).
            if (end > 0 && data[0] == '﻿')
            {
                pos = 1;
            }

            // Stack of the most recent node at each indentation depth. The node at the
            // top is the parent for anything indented further than it.
            var stack = new List<DataNode> { _root };
            var separatorStack = new List<int> { -1 };

            bool fileIsTabs = false;
            bool fileIsSpaces = false;
            int lineNumber = 0;

            while (pos < end)
            {
                lineNumber++;
                int tokenPos = pos;
                char c = data[pos++];

                bool mixedIndentation = false;
                int separators = 0;

                // Advance to the first tokenizable character, counting indentation.
                while (c <= ' ' && c != '\n')
                {
                    if (!fileIsTabs && !fileIsSpaces)
                    {
                        if (c == '\t')
                        {
                            fileIsTabs = true;
                        }
                        else if (c == ' ')
                        {
                            fileIsSpaces = true;
                        }
                    }
                    else if ((fileIsTabs && c != '\t') || (fileIsSpaces && c != ' '))
                    {
                        mixedIndentation = true;
                    }

                    separators++;
                    tokenPos = pos;
                    if (pos >= end)
                    {
                        c = '\n';
                        break;
                    }

                    c = data[pos++];
                }

                // A comment line: consume to end of line.
                if (c == '#')
                {
                    if (mixedIndentation)
                    {
                        Diagnostics.Add($"Mixed whitespace usage for comment at line {lineNumber}");
                    }

                    while (c != '\n' && pos < end)
                    {
                        c = data[pos++];
                    }

                    c = '\n';
                }

                if (c == '\n')
                {
                    continue;
                }

                // Pop back to the correct parent for this indentation level.
                while (separatorStack[separatorStack.Count - 1] >= separators)
                {
                    separatorStack.RemoveAt(separatorStack.Count - 1);
                    stack.RemoveAt(stack.Count - 1);
                }

                DataNode node = stack[stack.Count - 1].AddChildInternal();
                node.LineNumber = lineNumber;

                stack.Add(node);
                separatorStack.Add(separators);

                // Tokenize the rest of the line.
                while (c != '\n')
                {
                    char endQuote = c;
                    bool isQuoted = endQuote == '"' || endQuote == '`';
                    if (isQuoted)
                    {
                        tokenPos = pos;
                        c = pos < end ? data[pos++] : '\n';
                    }

                    int endPos = tokenPos;

                    while (c != '\n' && (isQuoted ? c != endQuote : c > ' '))
                    {
                        endPos = pos;
                        c = pos < end ? data[pos++] : '\n';
                    }

                    node.AddTokenInternal(tokenPos == endPos
                        ? string.Empty
                        : data.Substring(tokenPos, endPos - tokenPos));

                    if (isQuoted && c == '\n')
                    {
                        Diagnostics.Add(
                            $"Closing quotation mark is missing at line {lineNumber}" +
                            (_root.SourceFile != null ? $" of {_root.SourceFile}" : string.Empty));
                    }

                    if (c != '\n')
                    {
                        if (isQuoted)
                        {
                            tokenPos = pos;
                            c = pos < end ? data[pos++] : '\n';
                        }

                        while (c != '\n' && c <= ' ' && c != '#')
                        {
                            tokenPos = pos;
                            c = pos < end ? data[pos++] : '\n';
                        }

                        // A '#' outside a token comments out the remainder of the line.
                        if (c == '#')
                        {
                            while (c != '\n' && pos < end)
                            {
                                c = data[pos++];
                            }

                            c = '\n';
                        }
                    }
                }

                if (mixedIndentation)
                {
                    Diagnostics.Add($"Mixed whitespace usage at line {lineNumber}");
                }
            }
        }
    }
}
