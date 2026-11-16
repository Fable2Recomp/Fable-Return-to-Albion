using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Refii.Mods;

/// <summary>
/// What a mod asks of the title it is installed for: an event the title never raises, or a content
/// kind it does not read, is reported as a warning instead of doing nothing in silence. Fable 3 raises
/// the events <see cref="ModApi.Fable3Raised"/> lists and reads a subset of Fable 2's content.
/// </summary>
public static partial class ModValidate
{
	/// <summary>Content Fable 3 does not read yet, by package path prefix or suffix, with the reason.</summary>
	private static readonly (string Pattern, string Reason)[] Fable3Unread =
	{
		(ModLua.ModFrontendPath, "Fable 3 runs no front-end Lua: its game VM stands from START, so mod/init.lua runs on the front end too (Refii.screen, signed_in, save_count answer there)"),
		(ModSpells.Folder, "custom spells are Fable 2's spell system"),
		(Engine_ModUiFiles.SpellWheelPath, "the spell wheel is Fable 2's"),
		("fx/", "mod effects are composed into Fable 2's particle bank"),
	};

	/// <summary>A generic for over <c>pairs</c>: group 1 its loop variables.</summary>
	private static readonly Regex PairsLoop =
		new(@"\bfor\s+([A-Za-z_]\w*(?:\s*,\s*[A-Za-z_]\w*)*)\s+in\s+pairs\s*\(", RegexOptions.Compiled);

	/// <summary>A Lua comment: a long one (<c>--[[ ]]</c>, <c>--[==[ ]==]</c>) or one to the end of its line.</summary>
	private static readonly Regex LuaComment = new(@"--\[(=*)\[.*?\]\1\]|--[^\n]*", RegexOptions.Compiled | RegexOptions.Singleline);

	/// <summary>Lua subscriptions: <c>Refii.on("name"</c>.</summary>
	private static readonly Regex LuaSubscription = new(@"Refii\s*\.\s*on\s*\(\s*[""'](\w+)[""']", RegexOptions.Compiled);

	/// <summary>Godot subscriptions: <c>on_verdict("name"</c>, <c>connect("name"</c>, <c>host.name.connect(</c>.</summary>
	private static readonly Regex GodotSubscription =
		new(@"(?:on_verdict|connect)\s*\(\s*[""'](\w+)[""']|\.(\w+)\.connect\s*\(", RegexOptions.Compiled);

	/// <summary>
	/// The title issues of one package file: <paramref name="text"/> is read only for Lua and Godot
	/// scripts (null otherwise). Empty for Fable 2, which reads every kind and raises every event.
	/// </summary>
	public static IReadOnlyList<ModIssue> TitleIssues(string path, Func<string>? text, string title)
	{
		if (title != "Fable3")
		{
			return Array.Empty<ModIssue>();
		}

		string normalized = Normalize(path);
		var issues = new List<ModIssue>();
		foreach ((string pattern, string reason) in Fable3Unread)
		{
			if (normalized.StartsWith(pattern, StringComparison.Ordinal) || normalized == pattern)
			{
				issues.Add(new ModIssue(normalized, string.Empty, false, $"not used on Fable 3: {reason}"));
				return issues;
			}
		}

		bool lua = ModLua.IsModLuaPath(normalized);
		bool godot = normalized.StartsWith("godot/", StringComparison.Ordinal) && normalized.EndsWith(".gd", StringComparison.Ordinal);
		if ((!lua && !godot) || text is null)
		{
			return issues;
		}

		string source = text();
		if (lua)
		{
			// Fable 3's `pairs` is Havok Script's kpairs: it iterates only in a for with exactly two
			// loop variables (the VM's TFORLOOP walks it inline for two, TU3 0x822A46E0), and runs zero
			// times otherwise.
			foreach (Match m in PairsLoop.Matches(LuaComment.Replace(source, string.Empty)))
			{
				int variables = m.Groups[1].Value.Split(',').Length;
				if (variables != 2)
				{
					issues.Add(new ModIssue(normalized, m.Value.Trim(), false,
						$"on Fable 3 a pairs loop needs exactly two variables ('for k, v in pairs(t)'); with {variables} it runs zero times (or use 'for k in next, t')"));
				}
			}
		}

		var named = new HashSet<string>(StringComparer.Ordinal);
		foreach (Match m in (lua ? LuaSubscription : GodotSubscription).Matches(source))
		{
			string name = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
			if (ModApi.Find(name) is { } e && !ModApi.RaisedOn(e, "Fable3") && named.Add(name))
			{
				issues.Add(new ModIssue(normalized, name, false,
					$"Fable 3 does not raise '{name}' yet: the handler never runs (see the guide's Modding Fable III)"));
			}
		}

		return issues;
	}
}
