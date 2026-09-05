using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Refii.Mods;

/// <summary>
/// Which mods are on, in what order, and which files are pinned against that order.
///
/// ORDER IS THE RESOLUTION. The list is highest-priority first; the mount indexes it in reverse
/// so the top entry is inserted last and wins. Pins are the escape hatch for the one case order
/// cannot express: this file from that mod, whatever the order says.
///
/// <c>mods/loadorder.toml</c>:
/// <code>
/// enabled = ["hd-ui", "harder-economy"]
///
/// [pins]
/// "art/gui/hud.bgf" = "harder-economy"
/// </code>
/// </summary>
public sealed class ModLoadOrder
{
	public const string Filename = "loadorder.toml";
	public const string LegacyFilename = "enabled.txt";
	public const string MigratedLegacyFilename = "enabled.txt.migrated";

	/// <summary>Enabled mod folder names, highest priority first.</summary>
	public List<string> Enabled { get; } = new();

	/// <summary>Normalised virtual path to the mod name that owns it regardless of order.</summary>
	public Dictionary<string, string> Pins { get; } = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Reads the order for <paramref name="modsRoot"/>.
	///
	/// <c>loadorder.toml</c> wins; <c>enabled.txt</c> is read only when it is absent, so an
	/// install that predates this file keeps its list until the next write migrates it.
	/// </summary>
	public static ModLoadOrder Read(string modsRoot)
	{
		var order = new ModLoadOrder();
		if (string.IsNullOrEmpty(modsRoot))
		{
			return order;
		}

		string path = Path.Combine(modsRoot, Filename);
		if (File.Exists(path))
		{
			ReadToml(path, order);
			return order;
		}

		ReadLegacy(Path.Combine(modsRoot, LegacyFilename), order);
		return order;
	}

	/// <summary>
	/// Writes the order, and retires <c>enabled.txt</c> by renaming it.
	///
	/// Renamed rather than deleted: its content is carried into the new file, and a rename says
	/// what happened without destroying something the player may have hand-edited.
	/// </summary>
	public bool Write(string modsRoot, out string error)
	{
		error = string.Empty;
		if (string.IsNullOrEmpty(modsRoot))
		{
			error = "No mods folder is configured.";
			return false;
		}

		try
		{
			Directory.CreateDirectory(modsRoot);

			var text = new StringBuilder();
			text.AppendLine("# Refii mod load order. The FIRST entry wins conflicts.");
			text.Append("enabled = [");
			for (int i = 0; i < Enabled.Count; i++)
			{
				if (i != 0)
				{
					text.Append(", ");
				}

				text.Append('"').Append(Escape(Enabled[i])).Append('"');
			}

			text.AppendLine("]");

			if (Pins.Count != 0)
			{
				text.AppendLine();
				text.AppendLine("# One file, one owner — applied after the order above.");
				text.AppendLine("[pins]");
				foreach ((string file, string owner) in Pins)
				{
					text.Append('"').Append(Escape(file)).Append("\" = \"")
						.Append(Escape(owner)).AppendLine("\"");
				}
			}

			File.WriteAllText(Path.Combine(modsRoot, Filename), text.ToString());

			string legacy = Path.Combine(modsRoot, LegacyFilename);
			if (File.Exists(legacy))
			{
				string retired = Path.Combine(modsRoot, MigratedLegacyFilename);
				File.Delete(retired);
				File.Move(legacy, retired);
			}

			return true;
		}
		catch (Exception exception)
		{
			error = exception.Message;
			return false;
		}
	}

	private static void ReadToml(string path, ModLoadOrder order)
	{
		string[] lines;
		try
		{
			lines = File.ReadAllLines(path);
		}
		catch (Exception)
		{
			return;
		}

		bool inPins = false;
		foreach (string raw in lines)
		{
			string line = raw.Trim();
			if (line.Length == 0 || line[0] == '#')
			{
				continue;
			}

			if (line[0] == '[')
			{
				inPins = line.StartsWith("[pins]", StringComparison.OrdinalIgnoreCase);
				continue;
			}

			int equals = line.IndexOf('=');
			if (equals < 0)
			{
				continue;
			}

			string key = ModWorkspace.Unquote(line[..equals].Trim());
			string value = line[(equals + 1)..].Trim();

			if (inPins)
			{
				string owner = ModWorkspace.Unquote(value);
				if (key.Length != 0 && owner.Length != 0)
				{
					order.Pins[Normalize(key)] = owner;
				}

				continue;
			}

			if (!key.Equals("enabled", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			foreach (string name in SplitArray(value))
			{
				if (ModWorkspace.IsSafeModFolderName(name) && !order.Enabled.Contains(name))
				{
					order.Enabled.Add(name);
				}
			}
		}
	}

	private static void ReadLegacy(string path, ModLoadOrder order)
	{
		if (!File.Exists(path))
		{
			return;
		}

		string[] lines;
		try
		{
			lines = File.ReadAllLines(path);
		}
		catch (Exception)
		{
			return;
		}

		foreach (string raw in lines)
		{
			string name = raw.Trim();
			if (name.Length == 0 || name[0] == '#')
			{
				continue;
			}

			if (ModWorkspace.IsSafeModFolderName(name) && !order.Enabled.Contains(name))
			{
				order.Enabled.Add(name);
			}
		}
	}

	/// <summary>
	/// The names in a single-line TOML array. Anything the writer above produces round-trips;
	/// a hand-written file with the array spread over several lines keeps only its first line,
	/// which is why the writer never spreads it.
	/// </summary>
	private static IEnumerable<string> SplitArray(string value)
	{
		int open = value.IndexOf('[');
		int close = value.LastIndexOf(']');
		string body = open >= 0 && close > open ? value[(open + 1)..close] : value;

		foreach (string part in body.Split(','))
		{
			string name = ModWorkspace.Unquote(part.Trim());
			if (name.Length != 0)
			{
				yield return name;
			}
		}
	}

	private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

	internal static string Normalize(string path) =>
		path.Replace('\\', '/').Trim('/').ToLowerInvariant();
}
