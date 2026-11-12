using System;
using System.Collections.Generic;
using System.Text;

namespace Refii.Recompile;

/// <summary>
/// Parses Makefile-format dependency files (<c>.d</c>) produced by <c>clang -MD</c> and <c>zig c++ -MD</c>
/// on both Unix and Windows.
/// </summary>
public static class CompileDepfile
{
    /// <summary>
    /// Extracts the ordered prerequisite file paths from one or more <c>clang -MD</c> dependency rules,
    /// omitting rule targets and empty <c>-MP</c> phony rules.
    /// </summary>
    /// <param name="text">The raw contents of the <c>.d</c> file written by Clang.</param>
    /// <returns>The unescaped dependency paths listed after each rule's target separator colon.</returns>
    /// <remarks>
    /// Implements Ninja/GNU Make depfile escaping rules emitted by Clang on both platforms:
    /// LF/CRLF line continuations (<c>\\\n</c>, <c>\\\r\n</c>), <c>2N+1</c> backslashes before space or <c>#</c>
    /// for <c>N</c> literal backslashes plus an escaped character (<c>\ </c>, <c>\#</c>, <c>\\\ </c>),
    /// <c>2N</c> backslashes before space for <c>N</c> literal backslashes followed by a token delimiter,
    /// literal Windows path separators (<c>C:\...</c>), <c>$$</c> for literal <c>$</c>, multi-target rules,
    /// and multiple logical rules per file.
    /// </remarks>
    public static List<string> Parse(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new List<string>();
        }

        var dependencies = new List<string>();
        var word = new StringBuilder();
        bool sawAnyRuleColon = false;

        foreach (string rule in SplitLogicalRules(text))
        {
            word.Clear();
            bool pastTargetColon = false;

            void Flush(bool endsTarget = false)
            {
                if (word.Length > 0)
                {
                    if (pastTargetColon)
                    {
                        dependencies.Add(word.ToString());
                    }

                    word.Clear();
                }

                if (endsTarget)
                {
                    pastTargetColon = true;
                    sawAnyRuleColon = true;
                }
            }

            for (int i = 0; i < rule.Length; i++)
            {
                char c = rule[i];
                if (c == '\\')
                {
                    int runEnd = i;
                    while (runEnd < rule.Length && rule[runEnd] == '\\')
                    {
                        runEnd++;
                    }

                    int backslashCount = runEnd - i;
                    if (runEnd < rule.Length && rule[runEnd] is ' ' or '#')
                    {
                        word.Append('\\', backslashCount / 2);
                        if ((backslashCount & 1) == 1)
                        {
                            word.Append(rule[runEnd]);
                            i = runEnd;
                        }
                        else
                        {
                            i = runEnd - 1;
                        }

                        continue;
                    }

                    word.Append('\\', backslashCount);
                    i = runEnd - 1;
                }
                else if (c == '$' && i + 1 < rule.Length && rule[i + 1] == '$')
                {
                    word.Append('$');
                    i++;
                }
                else if (!pastTargetColon && c == ':')
                {
                    bool isWindowsDrivePrefix = word.Length == 1
                        && char.IsAsciiLetter(word[0])
                        && i + 1 < rule.Length
                        && rule[i + 1] is '\\' or '/';
                    if (isWindowsDrivePrefix)
                    {
                        word.Append(c);
                    }
                    else
                    {
                        Flush(endsTarget: true);
                    }
                }
                else if (c == '#')
                {
                    // Unescaped '#' begins a Make comment to the end of the logical rule.
                    break;
                }
                else if (char.IsWhiteSpace(c))
                {
                    Flush();
                }
                else
                {
                    word.Append(c);
                }
            }

            Flush();
        }

        return sawAnyRuleColon ? dependencies : new List<string>();
    }

    private static List<string> SplitLogicalRules(string text)
    {
        var rules = new List<string>();
        var current = new StringBuilder();

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    current.Append(' ');
                    i++;
                    continue;
                }

                if (i + 2 < text.Length && text[i + 1] == '\r' && text[i + 2] == '\n')
                {
                    current.Append(' ');
                    i += 2;
                    continue;
                }
            }

            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                if (current.Length > 0)
                {
                    rules.Add(current.ToString());
                    current.Clear();
                }

                i++;
                continue;
            }

            if (c is '\n' or '\r')
            {
                if (current.Length > 0)
                {
                    rules.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            rules.Add(current.ToString());
        }

        return rules;
    }
}
