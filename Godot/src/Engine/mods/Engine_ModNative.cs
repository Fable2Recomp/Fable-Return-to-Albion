using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Refii.Mods;

/// <summary>One file of a hard mod's native sources, by its path under <c>native/</c>.</summary>
public readonly record struct ModNativeFile(string Path, byte[] Bytes);

/// <summary>
/// A hard mod's native hook sources: what it compiles into the game module, and which guest
/// functions it overrides.
/// </summary>
public sealed class ModNativeSources
{
	public string ModName = string.Empty;
	public string Version = string.Empty;
	public List<ModNativeFile> Files = new();

	/// <summary>Function names the mod defines strongly, derived from its sources.</summary>
	public List<string> Overrides = new();

	/// <summary>SHA-256 over every native file's path and bytes. What consent is given to.</summary>
	public string ContentHash = string.Empty;

	public IEnumerable<ModNativeFile> CompiledFiles => Files.Where(file => ModNative.IsCompiled(file.Path));
}

/// <summary>Two owners defining one guest function. A function has one body, so this blocks.</summary>
public readonly record struct ModNativeConflict(string Function, string First, string Second);

/// <summary>
/// Hard mods: native hook sources a mod ships under <c>native/</c>, compiled into the payload on
/// the player's machine in the same step as refii's own hooks.
///
/// THE OVERRIDE SET IS DERIVED, NEVER DECLARED. A hook is a strong <c>REX_EXTERN(name) {</c>
/// definition of a function the generator emits weak; the names are read out of the sources the
/// same way for a mod and for refii, so a conflict between them cannot be missed by a manifest
/// someone forgot to update.
///
/// Conflicts do not resolve by load order. Two strong definitions of one symbol are a link error,
/// and choosing one silently would drop the other mod's behaviour, so both are reported and the
/// build refuses. A mod that needs to run alongside a function refii already hooks calls the
/// original through <c>__imp__name</c> from a function refii does not hook.
/// </summary>
public static class ModNative
{
	public const string Folder = "native/";

	/// <summary>The owner name refii's own hooks are reported under.</summary>
	public const string RefiiOwner = "refii";

	private static readonly string[] CompiledExtensions = { ".c", ".cc", ".cpp", ".cxx" };
	private static readonly string[] SourceExtensions =
		{ ".c", ".cc", ".cpp", ".cxx", ".h", ".hh", ".hpp", ".hxx", ".inl", ".inc" };

	// A DEFINITION: the macro, the name, the closing paren, then the body's brace. A declaration
	// (`REX_EXTERN(__imp__x);`) ends in a semicolon and does not match.
	private static readonly Regex OverrideDefinition = new(
		@"\bREX_EXTERN\s*\(\s*([A-Za-z_][A-Za-z0-9_]*)\s*\)\s*\{",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private static readonly Regex Comments = new(
		@"//[^\n]*|/\*.*?\*/",
		RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.CultureInvariant);

	public static bool IsNativePath(string path) =>
		Normalise(path).StartsWith(Folder, StringComparison.OrdinalIgnoreCase);

	public static bool IsCompiled(string path) =>
		CompiledExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

	private static bool IsSource(string path) =>
		SourceExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

	private static string Normalise(string path) => path.Replace('\\', '/').TrimStart('/');

	/// <summary>The guest functions <paramref name="sourceText"/> defines strongly.</summary>
	public static IEnumerable<string> DeriveOverrides(string sourceText)
	{
		string code = Comments.Replace(sourceText, " ");
		foreach (Match match in OverrideDefinition.Matches(code))
		{
			string name = match.Groups[1].Value;
			if (!name.StartsWith("__imp__", StringComparison.Ordinal))
			{
				yield return name;
			}
		}
	}

	/// <summary>The mod's native sources, or null when it ships none (a soft mod).</summary>
	public static ModNativeSources? Read(ModInstall mod)
	{
		if (!F2ModPackage.TryOpen(mod.PackagePath, out F2ModPackage? package, out _) || package == null)
		{
			return null;
		}

		using (package)
		{
			var sources = new ModNativeSources
			{
				ModName = mod.Name,
				Version = package.Metadata.Version,
			};

			foreach (F2ModEntry entry in package.Entries)
			{
				string path = Normalise(entry.Path);
				if (!path.StartsWith(Folder, StringComparison.OrdinalIgnoreCase) || !IsSource(path))
				{
					continue;
				}

				string relative = path.Substring(Folder.Length);
				if (!IsSafeRelativePath(relative))
				{
					continue;
				}

				sources.Files.Add(new ModNativeFile(relative, package.ReadEntryBytes(entry)));
			}

			if (sources.Files.Count == 0)
			{
				return null;
			}

			sources.Files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
			sources.Overrides = sources.CompiledFiles
				.SelectMany(file => DeriveOverrides(Encoding.UTF8.GetString(file.Bytes)))
				.Distinct(StringComparer.Ordinal)
				.OrderBy(name => name, StringComparer.Ordinal)
				.ToList();
			sources.ContentHash = HashFiles(sources.Files);
			return sources;
		}
	}

	/// <summary>Every enabled mod's native sources, highest priority first, soft mods skipped.</summary>
	public static List<ModNativeSources> ReadEnabled(string modsRoot)
	{
		var result = new List<ModNativeSources>();
		ModLoadOrder order = ModLoadOrder.Read(modsRoot);
		Dictionary<string, ModInstall> installs = ModWorkspace.DiscoverModInstalls(modsRoot)
			.ToDictionary(mod => mod.Name, StringComparer.OrdinalIgnoreCase);

		foreach (string name in order.Enabled)
		{
			if (installs.TryGetValue(name, out ModInstall? mod) && Read(mod) is { } sources)
			{
				result.Add(sources);
			}
		}

		return result;
	}

	/// <summary>
	/// What every <c>.c/.cc/.cpp</c> under <paramref name="directory"/> overrides, by function. Used
	/// for refii's own staged hooks (<c>recompile/hooks</c>).
	/// </summary>
	public static Dictionary<string, string> ReadOverridesInDirectory(string directory, string owner)
	{
		var result = new Dictionary<string, string>(StringComparer.Ordinal);
		if (!Directory.Exists(directory))
		{
			return result;
		}

		foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
		{
			if (!IsCompiled(file))
			{
				continue;
			}

			foreach (string name in DeriveOverrides(File.ReadAllText(file)))
			{
				result.TryAdd(name, owner);
			}
		}

		return result;
	}

	/// <summary>
	/// Every function defined by two owners: refii against a mod, or two mods. Each pair is
	/// reported once, lower-priority owner second.
	/// </summary>
	public static List<ModNativeConflict> FindConflicts(
		IReadOnlyList<ModNativeSources> mods, IReadOnlyDictionary<string, string>? builtIn)
	{
		var conflicts = new List<ModNativeConflict>();
		var owners = new Dictionary<string, string>(StringComparer.Ordinal);
		if (builtIn != null)
		{
			foreach ((string function, string owner) in builtIn)
			{
				owners[function] = owner;
			}
		}

		foreach (ModNativeSources mod in mods)
		{
			foreach (string function in mod.Overrides)
			{
				if (owners.TryGetValue(function, out string? existing))
				{
					conflicts.Add(new ModNativeConflict(function, existing, mod.ModName));
				}
				else
				{
					owners[function] = mod.ModName;
				}
			}
		}

		return conflicts;
	}

	/// <summary>
	/// The fingerprint of a native mod set, for the build record: empty when no enabled mod ships
	/// native code, so a soft-only mod set never forces a rebuild.
	/// </summary>
	public static string HashSet(IReadOnlyList<ModNativeSources> mods)
	{
		if (mods.Count == 0)
		{
			return string.Empty;
		}

		var text = new StringBuilder();
		foreach (ModNativeSources mod in mods)
		{
			text.Append(mod.ModName).Append('\n').Append(mod.ContentHash).Append('\n');
		}

		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
	}

	private static string HashFiles(IEnumerable<ModNativeFile> files)
	{
		using var sha = SHA256.Create();
		foreach (ModNativeFile file in files)
		{
			byte[] name = Encoding.UTF8.GetBytes(file.Path + "\n");
			sha.TransformBlock(name, 0, name.Length, null, 0);
			sha.TransformBlock(file.Bytes, 0, file.Bytes.Length, null, 0);
		}

		sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
		return Convert.ToHexString(sha.Hash ?? Array.Empty<byte>());
	}

	/// <summary>No rooted path, no drive, no <c>..</c>: an entry cannot be extracted outside its mod.</summary>
	public static bool IsSafeRelativePath(string relative)
	{
		if (relative.Length == 0 || relative.Contains(':') || relative.StartsWith('/'))
		{
			return false;
		}

		foreach (string part in relative.Split('/'))
		{
			if (part.Length == 0 || part == "." || part == "..")
			{
				return false;
			}
		}

		return true;
	}
}
