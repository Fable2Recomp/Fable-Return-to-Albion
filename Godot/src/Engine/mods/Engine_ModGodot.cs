using System;
using System.Collections.Generic;
using System.Text;
using Godot;

namespace Refii.Mods;

/// <summary>
/// A mod's GDScript, run INSIDE THE GAME: every <c>godot/*.gd</c> in every enabled package is
/// compiled, instantiated and added under one <c>RefiiMods</c> node in the session's tree, and
/// talks to the game through the <see cref="RefiiHost"/> singleton. The natural home of a
/// visual, audio or UI mod: Godot owns the picture, so a script that draws, plays or lays out
/// runs where the picture is made.
/// </summary>
/// <remarks>
/// <para>A script must extend <c>Node</c> (or a Node subclass); it is instantiated once per
/// session when the guest is up and freed when the session stops, so <c>_ready</c> is where it
/// connects to <c>Engine.get_singleton("RefiiHost")</c>. A script that does not parse, or does
/// not extend Node, is named in a warning and skipped; the others still load. A <c>.gd</c> is
/// never an asset overlay: it is kept off the mount (<see cref="IsGodotScriptPath"/>).</para>
/// <para>The script has no resource path, so <c>preload</c> of a relative path does not resolve;
/// anything else a mod ships is reached through the asset mount (<c>game://</c>) as every
/// other overlay is.</para>
/// </remarks>
public static class ModGodot
{
	public const string Folder = "godot/";
	public const string RootName = "RefiiMods";

	private static RefiiHost? _host;
	private static Node? _root;

	/// <summary>The session's singleton while one stands, for host code that raises its signals.</summary>
	public static RefiiHost? Host => _host is not null && GodotObject.IsInstanceValid(_host) ? _host : null;

	/// <summary>True for a GDScript source under <c>godot/</c>: run by the host, never mounted.</summary>
	public static bool IsGodotScriptPath(string path)
	{
		string normalized = Normalise(path);
		return normalized.StartsWith(Folder, StringComparison.OrdinalIgnoreCase) &&
			normalized.EndsWith(".gd", StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>Call every frame with whether a guest is up and driven: starts the scripts on the rise, stops them on the fall.</summary>
	public static void Pump(Node session, bool guestUp)
	{
		if (guestUp)
		{
			if (_root is null)
			{
				Start(session);
			}
		}
		else if (_root is not null)
		{
			Stop();
		}
	}

	/// <summary>Registers the singleton, then loads every enabled mod's scripts under <see cref="RootName"/>.</summary>
	public static void Start(Node session)
	{
		Stop();

		string modsRoot = Refii.Assets.Engine_GameAssets.ModsRoot;
		ModLoadOrder order = ModLoadOrder.Read(modsRoot);

		_host = new RefiiHost { Name = RefiiHost.SingletonName, EnabledMods = order.Enabled.ToArray() };
		_root = new Node { Name = RootName };
		_host.ScriptsRoot = _root;
		session.AddChild(_host);
		if (Engine.HasSingleton(RefiiHost.SingletonName))
		{
			Engine.UnregisterSingleton(RefiiHost.SingletonName);
		}

		Engine.RegisterSingleton(RefiiHost.SingletonName, _host);

		int loaded = 0;
		foreach ((string mod, string file, string text) in ReadScripts(modsRoot, order))
		{
			if (Instantiate(mod, file, text) is { } node)
			{
				_root.AddChild(node);
				loaded++;
			}
		}

		// The root enters the tree after the singleton exists, so a script's _ready finds it.
		session.AddChild(_root);
		if (loaded != 0)
		{
			GD.Print($"[mods] godot: {loaded} script(s) running under {RootName}");
		}
	}

	/// <summary>Unregisters the singleton and frees the scripts; safe when nothing was started.</summary>
	public static void Stop()
	{
		if (Engine.HasSingleton(RefiiHost.SingletonName))
		{
			Engine.UnregisterSingleton(RefiiHost.SingletonName);
		}

		if (_root is not null)
		{
			if (GodotObject.IsInstanceValid(_root))
			{
				_root.GetParent()?.RemoveChild(_root);
				_root.QueueFree();
			}

			_root = null;
		}

		if (_host is not null)
		{
			if (GodotObject.IsInstanceValid(_host))
			{
				_host.ScriptsRoot = null;
				_host.GetParent()?.RemoveChild(_host);
				_host.QueueFree();
			}

			_host = null;
		}
	}

	/// <summary>Every <c>godot/*.gd</c> of every enabled package, lowest priority first (as ModEntries reads).</summary>
	private static List<(string Mod, string File, string Text)> ReadScripts(string modsRoot, ModLoadOrder order)
	{
		var result = new List<(string Mod, string File, string Text)>();
		if (string.IsNullOrEmpty(modsRoot) || !System.IO.Directory.Exists(modsRoot))
		{
			return result;
		}

		var installs = new Dictionary<string, ModInstall>(StringComparer.OrdinalIgnoreCase);
		foreach (ModInstall install in ModWorkspace.DiscoverModInstalls(modsRoot))
		{
			installs[install.Name] = install;
		}

		for (int i = order.Enabled.Count - 1; i >= 0; i--)
		{
			if (!installs.TryGetValue(order.Enabled[i], out ModInstall? mod) ||
				!F2ModPackage.TryOpen(mod.PackagePath, out F2ModPackage? package, out _) ||
				package is null)
			{
				continue;
			}

			using (package)
			{
				foreach (F2ModEntry entry in package.Entries)
				{
					string path = Normalise(entry.Path);
					if (path.StartsWith("files/", StringComparison.OrdinalIgnoreCase))
					{
						path = path["files/".Length..];
					}

					if (!IsGodotScriptPath(path))
					{
						continue;
					}

					result.Add((mod.Name, path[Folder.Length..], Encoding.UTF8.GetString(package.ReadEntryBytes(entry))));
				}
			}
		}

		return result;
	}

	/// <summary>Compiles and instantiates one script; null, with the reason warned, when it cannot run.</summary>
	private static Node? Instantiate(string mod, string file, string text)
	{
		string label = $"{mod}/{Folder}{file}";
		var script = new GDScript { SourceCode = text, ResourceName = label };
		Error error = script.Reload();
		if (error != Error.Ok || !script.CanInstantiate())
		{
			GD.PushWarning($"[mods] godot: {label} does not parse ({error}) and is NOT run");
			return null;
		}

		StringName baseType = script.GetInstanceBaseType();
		if (!ClassDB.IsParentClass(baseType, "Node"))
		{
			GD.PushWarning($"[mods] godot: {label} extends {baseType}, not Node, and is NOT run");
			return null;
		}

		if (script.New(Array.Empty<Variant>()).AsGodotObject() is not Node node)
		{
			GD.PushWarning($"[mods] godot: {label} did not instantiate as a Node and is NOT run");
			return null;
		}

		// A node name cannot carry '/', '.' or ':'; the origin stays readable in the metadata.
		node.Name = $"{mod}-{file[..^3]}".Replace('/', '-').Replace('.', '-').Replace(':', '-');
		node.SetMeta("refii_mod", mod);
		node.SetMeta("refii_file", Folder + file);
		return node;
	}

	private static string Normalise(string path) => path.Replace('\\', '/').TrimStart('/');
}
