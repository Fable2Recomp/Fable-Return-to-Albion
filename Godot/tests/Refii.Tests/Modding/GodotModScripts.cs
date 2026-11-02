namespace Refii.Tests.Modding;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using static GdUnit4.Assertions;
using Refii.Mods;

/// <summary>
/// What a bundled mod's Godot script asks of <see cref="RefiiHost"/>, read from the .gd TEXT (no
/// Godot runtime): every <c>host.name(…)</c> call with its argument count, every <c>host.name</c>
/// signal, every <c>ask("verb", …)</c> and <c>read("verb", …)</c>, every verdict and every named number. The facts are held to
/// the host's own methods and signals (reflection), the catalog, the guest's query verbs and the
/// fields each verb answers (mod_commands.cpp), so a script that calls a member the host lacks, with
/// an argument count no overload takes, or reads an answer field the guest never writes, fails here
/// instead of at run time — GDScript reaches RefiiHost dynamically.
/// </summary>
internal static class GodotModScripts
{
    private static readonly Regex HostMember = new(@"\bhost\.([a-z_][a-z0-9_]*)", RegexOptions.Compiled);
    // host.ask("verb", …), or a script's own wrapper of it taking a first argument: _ask(join, "verb", …).
    private static readonly Regex Ask = new(@"(?:\bhost\.ask|\b_ask)\(\s*(?:[A-Za-z_][A-Za-z0-9_]*\s*,\s*)?""([a-z_]+)""", RegexOptions.Compiled);
    // host.read("verb", …): a verdict's synchronous read.
    private static readonly Regex Read = new(@"\bhost\.read\(\s*""([a-z_]+)""", RegexOptions.Compiled);
    private static readonly Regex Verdict = new(@"\bhost\.on_verdict\(\s*""([a-z_]+)""\s*,\s*([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);
    private static readonly Regex Connect = new(@"\bhost\.([a-z_][a-z0-9_]*)\.connect\(\s*(func\s*\(([^)]*)\)|[A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);
    private static readonly Regex Named = new(@"\.get\(\s*""([A-Z][A-Z0-9_]+)""", RegexOptions.Compiled);

    public static string? RepoRoot()
    {
        string? at = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && at != null; i++)
        {
            if (File.Exists(Path.Combine(at, "Fable2", "src", "vfs", "include", "core", "refii_mod_events.h")))
            {
                return at;
            }

            at = Path.GetDirectoryName(at);
        }

        return null;
    }

    public static string ModFolder(string root, string mod) => Path.Combine(root, "Fable2", "mods-src", mod);

    /// <summary>A script's text with every comment taken out (a '#' outside a string to the line's end).</summary>
    public static string Code(string text)
    {
        var code = new StringBuilder(text.Length);
        char quote = '\0';
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quote != '\0')
            {
                code.Append(c);
                if (c == '\\' && i + 1 < text.Length)
                {
                    code.Append(text[++i]);
                }
                else if (c == quote)
                {
                    quote = '\0';
                }
            }
            else if (c == '"' || c == '\'')
            {
                quote = c;
                code.Append(c);
            }
            else if (c == '#')
            {
                while (i + 1 < text.Length && text[i + 1] != '\n')
                {
                    i++;
                }
            }
            else
            {
                code.Append(c);
            }
        }

        return code.ToString();
    }

    public static string Script(string root, string mod, string file) =>
        File.ReadAllText(Path.Combine(ModFolder(root, mod), "godot", file));

    /// <summary>RefiiHost's script methods by name, each with the argument counts its overloads take.</summary>
    public static Dictionary<string, HashSet<int>> HostMethods() =>
        typeof(RefiiHost).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .GroupBy(m => m.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(m => m.GetParameters().Length).ToHashSet(), StringComparer.Ordinal);

    /// <summary>RefiiHost's signals by name, each with its argument count.</summary>
    public static Dictionary<string, int> HostSignals() =>
        typeof(RefiiHost).GetNestedTypes(BindingFlags.Public)
            .Where(t => t.Name.EndsWith("EventHandler", StringComparison.Ordinal) && typeof(Delegate).IsAssignableFrom(t))
            .ToDictionary(t => t.Name[..^"EventHandler".Length], t => t.GetMethod("Invoke")!.GetParameters().Length, StringComparer.Ordinal);

    /// <summary>The method names the catalog's Godot section states.</summary>
    public static HashSet<string> CatalogMethods() =>
        ModApi.GodotHelpers.SelectMany(h => ModApi.GodotMethodNames(h.Signature)).ToHashSet(StringComparer.Ordinal);

    /// <summary>Every <c>host.name(…)</c> call: the name and its top-level argument count.</summary>
    public static List<(string Name, int Arguments)> Calls(string code)
    {
        var calls = new List<(string, int)>();
        foreach (Match m in HostMember.Matches(code))
        {
            int open = m.Index + m.Length;
            while (open < code.Length && code[open] == ' ')
            {
                open++;
            }

            if (open < code.Length && code[open] == '(')
            {
                calls.Add((m.Groups[1].Value, Arguments(code, open)));
            }
        }

        return calls;
    }

    /// <summary>Every <c>host.name</c> that is not called: a signal the script connects to or names.</summary>
    public static HashSet<string> Signals(string code)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in HostMember.Matches(code))
        {
            int after = m.Index + m.Length;
            while (after < code.Length && code[after] == ' ')
            {
                after++;
            }

            if (after >= code.Length || code[after] != '(')
            {
                names.Add(m.Groups[1].Value);
            }
        }

        return names;
    }

    /// <summary>The verbs the script asks the guest.</summary>
    public static HashSet<string> Asked(string code) =>
        Ask.Matches(code).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    /// <summary>The verbs the script reads at once (<c>host.read</c>), each with the function it is read in.</summary>
    public static List<(string Verb, string Function)> Reads(string code) =>
        Read.Matches(code).Select(m =>
        {
            MatchCollection funcs = Regex.Matches(code[..m.Index], @"(?m)^func\s+([A-Za-z_][A-Za-z0-9_]*)");
            return (m.Groups[1].Value, funcs.Count != 0 ? funcs[^1].Groups[1].Value : string.Empty);
        }).ToList();

    /// <summary>The events the script gives a verdict on, with the handler's name.</summary>
    public static List<(string Event, string Handler)> Verdicts(string code) =>
        Verdict.Matches(code).Select(m => (m.Groups[1].Value, m.Groups[2].Value)).ToList();

    /// <summary>The upper-case names the script reads from <c>constants()</c>.</summary>
    public static HashSet<string> Constants(string code) =>
        Named.Matches(code).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    /// <summary>The parameter count of the script's <c>func name(…)</c>, or -1 when it has none.</summary>
    public static int Parameters(string code, string function)
    {
        Match m = Regex.Match(code, @"(?m)^func\s+" + Regex.Escape(function) + @"\s*\(([^)]*)\)");
        return m.Success ? Count(m.Groups[1].Value) : -1;
    }

    private static int Count(string parameters) =>
        parameters.Trim().Length == 0 ? 0 : parameters.Split(',').Length;

    /// <summary>The argument count of the call whose '(' is at <paramref name="open"/>.</summary>
    private static int Arguments(string code, int open)
    {
        int depth = 0;
        int commas = 0;
        bool any = false;
        char quote = '\0';
        for (int i = open; i < code.Length; i++)
        {
            char c = code[i];
            if (quote != '\0')
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            switch (c)
            {
                case '"' or '\'':
                    quote = c;
                    any = true;
                    break;
                case '(' or '[' or '{':
                    depth++;
                    if (depth > 1)
                    {
                        any = true;
                    }

                    break;
                case ')' or ']' or '}':
                    depth--;
                    if (depth == 0)
                    {
                        return any ? commas + 1 : 0;
                    }

                    break;
                case ',' when depth == 1:
                    commas++;
                    break;
                default:
                    if (depth >= 1 && !char.IsWhiteSpace(c))
                    {
                        any = true;
                    }

                    break;
            }
        }

        return -1;
    }

    /// <summary>The verbs the guest answers (mod_commands.cpp's <c>what == "…"</c>).</summary>
    public static HashSet<string> QueryVerbs(string root) =>
        Regex.Matches(Commands(root), "what == \"([a-z_]+)\"").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The fields a query verb's answer carries, from its branch of mod_commands.cpp: every
    /// <c>name=</c> its format strings write (a numbered <c>e%d=</c> as <c>e</c>), and <c>ok</c>.
    /// </summary>
    public static HashSet<string> Answer(string root, string verb)
    {
        string text = Commands(root);
        Match branch = Regex.Match(text, "what == \"" + Regex.Escape(verb) + "\"(?:\\s*\\|\\|\\s*what == \"[a-z_]+\")*\\)\\s*\\{");
        AssertThat(branch.Success).OverrideFailureMessage("mod_commands.cpp answers no query " + verb).IsTrue();
        int end = text.IndexOf("} else if (what ==", branch.Index + branch.Length, StringComparison.Ordinal);
        string body = text[branch.Index..(end < 0 ? text.Length : end)];
        var fields = new HashSet<string>(StringComparer.Ordinal) { "ok" };
        foreach (Match literal in Regex.Matches(body, "\"((?:[^\"\\\\]|\\\\.)*)\""))
        {
            foreach (Match field in Regex.Matches(literal.Groups[1].Value, @"(?:^|,)([a-z_]+)(?:%d)?="))
            {
                fields.Add(field.Groups[1].Value);
            }
        }

        return fields;
    }

    private static string Commands(string root) =>
        File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_commands.cpp"));

    /// <summary>
    /// The script's whole contract with the host: it extends Node, every call is a RefiiHost method
    /// the catalog states with an overload of that argument count, every signal is RefiiHost's and
    /// each named handler takes the signal's arguments, every asked verb is one the guest answers,
    /// every verdict is on an event that can be refused with a handler of one argument, and every
    /// named number is one constants() states. Returns the method names called.
    /// </summary>
    public static HashSet<string> AssertHostContract(string root, string file, string text)
    {
        string code = Code(text);
        AssertThat(Regex.IsMatch(code, @"^\s*extends\s+Node\s*$", RegexOptions.Multiline))
            .OverrideFailureMessage(file + " does not extend Node").IsTrue();

        Dictionary<string, HashSet<int>> methods = HostMethods();
        HashSet<string> catalog = CatalogMethods();
        var called = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string name, int arguments) in Calls(code))
        {
            called.Add(name);
            AssertThat(methods.ContainsKey(name)).OverrideFailureMessage($"{file} calls host.{name}, which RefiiHost lacks").IsTrue();
            AssertThat(catalog.Contains(name)).OverrideFailureMessage($"{file} calls host.{name}, which the catalog does not state").IsTrue();
            if (methods.TryGetValue(name, out HashSet<int>? arities))
            {
                AssertThat(arities.Contains(arguments))
                    .OverrideFailureMessage($"{file} calls host.{name} with {arguments} argument(s); RefiiHost takes {string.Join(", ", arities.OrderBy(n => n))}")
                    .IsTrue();
            }
        }

        Dictionary<string, int> signals = HostSignals();
        foreach (string name in Signals(code))
        {
            AssertThat(signals.ContainsKey(name)).OverrideFailureMessage($"{file} names host.{name}, which is no RefiiHost signal").IsTrue();
        }

        foreach (Match m in Connect.Matches(code))
        {
            string signal = m.Groups[1].Value;
            int expected = signals.TryGetValue(signal, out int n) ? n : -1;
            int taken = m.Groups[3].Success ? Count(m.Groups[3].Value) : Parameters(code, m.Groups[2].Value);
            AssertThat(taken).OverrideFailureMessage($"{file}: the handler of {signal} takes {taken} argument(s), the signal passes {expected}").IsEqual(expected);
        }

        HashSet<string> verbs = QueryVerbs(root);
        foreach (string verb in Asked(code))
        {
            AssertThat(verbs.Contains(verb)).OverrideFailureMessage($"{file} asks '{verb}', which the guest does not answer").IsTrue();
        }

        // A read answers only inside a verdict handler, and only a verb that changes nothing.
        var handlers = Verdicts(code).Select(v => v.Handler).ToHashSet(StringComparer.Ordinal);
        foreach ((string verb, string function) in Reads(code))
        {
            AssertThat(ModVerdictRead.Allowed(verb) && verbs.Contains(verb))
                .OverrideFailureMessage($"{file} reads '{verb}', which is no read-only query verb").IsTrue();
            AssertThat(handlers.Contains(function))
                .OverrideFailureMessage($"{file} reads '{verb}' in {function}, which is no verdict handler: read answers only inside one").IsTrue();
        }

        var events = ModApi.Events.ToDictionary(e => e.LuaName, e => e, StringComparer.Ordinal);
        foreach ((string name, string handler) in Verdicts(code))
        {
            AssertThat(events.TryGetValue(name, out ModApiEvent? e) && e!.Cancel.Length != 0)
                .OverrideFailureMessage($"{file} gives a verdict on {name}, which cannot be refused").IsTrue();
            AssertThat(Parameters(code, handler)).OverrideFailureMessage($"{file}: the verdict {handler} takes the event alone").IsEqual(1);
            AssertThat(Regex.IsMatch(code, @"(?m)^func\s+" + Regex.Escape(handler) + @"\s*\([^)]*\)\s*->\s*bool"))
                .OverrideFailureMessage($"{file}: the verdict {handler} answers a bool").IsTrue();
        }

        var named = ModApi.NamedNumbers.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        foreach (string name in Constants(code))
        {
            AssertThat(named.Contains(name)).OverrideFailureMessage($"{file} reads {name}, which constants() does not state").IsTrue();
        }

        return called;
    }

    /// <summary>Each event the script must connect, with the fields it reads; each field the catalog's.</summary>
    public static void AssertEvents(string file, string code, params (string Event, string[] Fields)[] expected)
    {
        var events = ModApi.Events.ToDictionary(e => e.LuaName, e => e, StringComparer.Ordinal);
        HashSet<string> named = Signals(code);
        foreach ((string name, string[] fields) in expected)
        {
            AssertThat(events.ContainsKey(name)).OverrideFailureMessage("no catalog event " + name).IsTrue();
            AssertThat(named.Contains(name) || code.Contains("on_verdict(\"" + name + "\"", StringComparison.Ordinal))
                .OverrideFailureMessage($"{file} never hears {name}").IsTrue();
            var have = events[name].Fields.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
            foreach (string field in fields)
            {
                AssertThat(have.Contains(field)).OverrideFailureMessage($"{name} has no field {field}").IsTrue();
                AssertThat(code.Contains("\"" + field + "\"", StringComparison.Ordinal))
                    .OverrideFailureMessage($"{file} never reads {name}'s {field}").IsTrue();
            }
        }
    }

    /// <summary>Each asked (or read) verb's answer fields the script reads are fields the guest writes.</summary>
    public static void AssertAnswers(string root, string file, string code, params (string Verb, string[] Fields)[] expected)
    {
        HashSet<string> asked = Asked(code);
        asked.UnionWith(Reads(code).Select(r => r.Verb));
        foreach ((string verb, string[] fields) in expected)
        {
            AssertThat(asked.Contains(verb)).OverrideFailureMessage($"{file} never asks or reads {verb}").IsTrue();
            HashSet<string> answer = Answer(root, verb);
            foreach (string field in fields)
            {
                AssertThat(answer.Contains(field)).OverrideFailureMessage($"query {verb} answers no {field} ({string.Join(", ", answer)})").IsTrue();
                AssertThat(Regex.IsMatch(code, "\\.get\\(\\s*\"" + Regex.Escape(field) + "(%d)?\"") || code.Contains("r." + field, StringComparison.Ordinal))
                    .OverrideFailureMessage($"{file} never reads {verb}'s {field}").IsTrue();
            }
        }
    }

    /// <summary>The mod's data files validate with no error; returns how many were checked and the warnings.</summary>
    public static (int Checked, List<ModIssue> Warnings) AssertDataValidates(string folder)
    {
        var issues = new List<ModIssue>();
        int checkedFiles = 0;
        foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(folder, file);
            if (ModValidate.KindOf(relative).Length != 0)
            {
                checkedFiles++;
                issues.AddRange(ModValidate.Validate(relative, File.ReadAllText(file)));
            }
        }

        AssertThat(issues.Count(i => i.Error))
            .OverrideFailureMessage("issues:\n" + string.Join("\n", issues.Select(i => $"{i.Path} {i.Pointer}: {i.Message}")))
            .IsEqual(0);
        return (checkedFiles, issues.Where(i => !i.Error).ToList());
    }

    /// <summary>The mod ships no native code and exactly these Lua files (none for a pure Godot mod) and Godot scripts.</summary>
    public static void AssertShips(string folder, string[] lua, string[] scripts)
    {
        AssertThat(Directory.Exists(Path.Combine(folder, "native"))).IsFalse();
        foreach (string pattern in new[] { "*.cpp", "*.h", "*.c" })
        {
            AssertThat(Directory.EnumerateFiles(folder, pattern, SearchOption.AllDirectories).Any())
                .OverrideFailureMessage($"{folder} ships {pattern}").IsFalse();
        }

        AssertThat(Directory.EnumerateFiles(folder, "*.lua", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/')).OrderBy(f => f, StringComparer.Ordinal).ToList())
            .IsEqual(lua.OrderBy(f => f, StringComparer.Ordinal).ToList());
        AssertThat(Directory.EnumerateFiles(folder, "*.gd", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/')).OrderBy(f => f, StringComparer.Ordinal).ToList())
            .IsEqual(scripts.OrderBy(f => f, StringComparer.Ordinal).ToList());
    }
}
