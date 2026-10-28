using System.Collections.Generic;
using Godot;

namespace Refii.Ui;

/// <summary>
/// Exports each screen's chrome units as `PackedScene` under
/// <c>user://ui/components/&lt;screen&gt;/&lt;unit&gt;.tscn</c> — the authoring set beside the
/// template registry's JSON snapshot. `user://` only: this is converted game art and is not
/// redistributable. A component is the file-only build from <see cref="Engine_UiTheme.Art"/>,
/// carrying `object_id` on every node, which is the join the live driver uses.
/// </summary>
public static class Engine_UiComponentLibrary
{
	/// <summary>Where the exported components land. One directory per screen template.</summary>
	public const string Root = "user://ui/components";

	/// <summary>What one export did, for the report.</summary>
	public readonly record struct Result(int Screens, int Components, int Failed)
	{
		public override string ToString() =>
			$"{Components} component(s) from {Screens} screen(s)" +
			(Failed == 0 ? string.Empty : $", {Failed} FAILED");
	}

	/// <summary>
	/// Runs the desktop authoring export at HUD startup. Android builds screens on demand:
	/// exporting every component would first retain every screen's detached pattern tree,
	/// although gameplay never reads these editable scene files.
	/// </summary>
	/// <param name="android">Whether this startup belongs to the Android player.</param>
	/// <param name="export">The desktop export, deferred so Android never opens its pattern books.</param>
	/// <returns>The desktop export result, or null when Android leaves authoring to the editor.</returns>
	internal static Result? ExportAtStartup(bool android, System.Func<Result> export) =>
		android ? null : export();

	/// <summary>
	/// Writes every screen's chrome units out as scenes. Screens whose presentation is not in the
	/// shipped set are skipped silently — 21 of the 52 `.bgf` are never loaded by the game either.
	/// </summary>
	/// <param name="patternBook">Presentation name to file-only catalogue —
	/// <see cref="Engine_UiPresentationHost.PatternBookFor"/>.</param>
	public static Result ExportAll(System.Func<string, Engine_UiTheme?> patternBook)
	{
		int screens = 0;
		int components = 0;
		int failed = 0;

		DirAccess.MakeDirRecursiveAbsolute(Root);

		foreach (Engine_UiScreenTemplate template in Engine_UiTemplates.Screens)
		{
			if (template.Presentation.Length == 0 || template.ChromeUnits.Count == 0)
			{
				continue;
			}

			if (patternBook(template.Presentation) is not { } book)
			{
				continue;
			}

			++screens;
			string directory = $"{Root}/{template.Name}";
			DirAccess.MakeDirRecursiveAbsolute(directory);

			foreach (string entry in Units(template))
			{
				(string unit, int variant) = SplitVariant(entry);
				if (book.Art(unit, variant) is not { } art)
				{
					++failed;
					continue;
				}

				// The authored transform, not the zeroed one `Art()` hands back.
				if (book.ArtSourceNode(unit, variant) is { } source)
				{
					art.Transform = Engine_UiTheme.CanvasTransform(source);
				}

				art.Name = Sanitise(unit) + (variant == 0 ? string.Empty : $"_{variant}");
				if (Save(art, $"{directory}/{art.Name}.tscn"))
				{
					++components;
				}
				else
				{
					++failed;
				}

				art.QueueFree();
			}
		}

		return new Result(screens, components, failed);
	}

	/// <summary>The units worth exporting: the chrome, plus the backdrop where a screen has one.</summary>
	private static IEnumerable<string> Units(Engine_UiScreenTemplate template)
	{
		foreach (string unit in template.ChromeUnits)
		{
			yield return unit;
		}

		if (template.BackdropUnit.Length != 0)
		{
			yield return template.BackdropUnit;
		}
	}

	/// <summary>
	/// Packs a subtree and writes it. Every node needs `Owner` set to the root or
	/// `PackedScene.Pack` keeps only the root; a tree built in code has none set.
	/// </summary>
	private static bool Save(Node root, string path)
	{
		Own(root, root);

		var packed = new PackedScene();
		if (packed.Pack(root) != Error.Ok)
		{
			GD.PushWarning($"[ui-components] could not pack '{path}'");
			return false;
		}

		Error saved = ResourceSaver.Save(packed, path);
		if (saved != Error.Ok)
		{
			GD.PushWarning($"[ui-components] could not write '{path}': {saved}");
			return false;
		}

		return true;
	}

	private static void Own(Node node, Node root)
	{
		foreach (Node child in node.GetChildren())
		{
			child.Owner = root;
			Own(child, root);
		}
	}

	/// <summary>`Name#N` picks which bearer of a repeated name — see the owned book.</summary>
	private static (string Name, int Variant) SplitVariant(string entry)
	{
		int hash = entry.LastIndexOf('#');
		if (hash <= 0 || !int.TryParse(entry[(hash + 1)..], out int variant) || variant < 0)
		{
			return (entry, 0);
		}

		return (entry[..hash], variant);
	}

	/// <summary>A file name Godot will accept; element names are file-system input.</summary>
	private static string Sanitise(string name)
	{
		var clean = new System.Text.StringBuilder(name.Length);
		foreach (char c in name)
		{
			clean.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
		}

		return clean.Length == 0 ? "unnamed" : clean.ToString();
	}
}
