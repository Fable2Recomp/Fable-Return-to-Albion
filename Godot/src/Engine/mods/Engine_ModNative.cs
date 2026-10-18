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

	/// <summary>
	/// Of <see cref="Overrides"/>, the functions whose hook never calls the original
	/// (<c>__imp__name</c> appears in none of the mod's sources). Such a hook replaces the body
	/// and has to be the innermost owner of the function; every other hook wraps.
	/// </summary>
	public HashSet<string> Replaces = new(StringComparer.Ordinal);

	/// <summary>SHA-256 over every native file's path and bytes. What consent is given to.</summary>
	public string ContentHash = string.Empty;

	public IEnumerable<ModNativeFile> CompiledFiles => Files.Where(file => ModNative.IsCompiled(file.Path));
}

/// <summary>
/// An owner that replaces a guest function's body while another owner hooks it beneath. The
/// replacing hook would swallow the inner one, and no order changes that, so this blocks.
/// <paramref name="First"/> is the replacer, <paramref name="Second"/> the owner it would swallow.
/// </summary>
public readonly record struct ModNativeConflict(string Function, string First, string Second);

/// <summary>
/// One guest function hooked by more than one owner: <see cref="Owners"/> outermost first,
/// <see cref="ModNative.RefiiOwner"/> last when refii's own hooks define it. The game's body stays
/// <c>__imp__&lt;Function&gt;</c> under the innermost owner.
/// </summary>
public sealed record ModNativeChain(string Function, IReadOnlyList<string> Owners);

/// <summary>
/// Hard mods: native hook sources a mod ships under <c>native/</c>, compiled into the payload on
/// the player's machine in the same step as refii's own hooks.
///
/// THE OVERRIDE SET IS DERIVED, NEVER DECLARED. A hook is a strong <c>REX_EXTERN(name) {</c>
/// definition of a function the generator emits weak; the names are read out of the sources the
/// same way for a mod and for refii, so a conflict between them cannot be missed by a manifest
/// someone forgot to update.
///
/// ONE FUNCTION, MANY HOOKS, ONE CHAIN. The generator emits every function as a weak symbol over
/// its <c>__imp__</c> body, so two strong definitions of one name would be a link error. When more
/// than one owner hooks a function the launcher compiles each owner's hook under a symbol of its
/// own (<see cref="ChainSymbol"/>) with its original-call macro redirected to the next owner inward
/// (<see cref="ChainDefines"/>), and emits the real name once as a tail call into the outermost
/// owner (<see cref="ChainSource"/>). Order is <c>loadorder.toml</c>: highest priority outermost,
/// refii's own hook innermost. A hook that refuses without calling the original stops the chain,
/// which is what a gate means.
///
/// The one conflict left is a hook that never calls the original while another owner hooks the
/// same function beneath it: the inner hook would never run. That is reported and the build
/// refuses; order cannot fix it.
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

	/// <summary>
	/// Of <paramref name="overrides"/>, the functions whose original (<c>__imp__name</c>) is called
	/// nowhere in <paramref name="sourceTexts"/>: the hooks that replace rather than wrap.
	/// </summary>
	public static HashSet<string> DeriveReplacements(
		IEnumerable<string> overrides, IEnumerable<string> sourceTexts)
	{
		var replaces = new HashSet<string>(overrides, StringComparer.Ordinal);
		if (replaces.Count == 0)
		{
			return replaces;
		}

		var calls = new Regex(@"\b__imp__([A-Za-z_][A-Za-z0-9_]*)\b", RegexOptions.CultureInvariant);
		foreach (string text in sourceTexts)
		{
			string code = Comments.Replace(text, " ");
			foreach (Match match in calls.Matches(code))
			{
				replaces.Remove(match.Groups[1].Value);
			}
		}

		return replaces;
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
			List<string> texts = sources.CompiledFiles
				.Select(file => Encoding.UTF8.GetString(file.Bytes))
				.ToList();
			sources.Overrides = texts
				.SelectMany(DeriveOverrides)
				.Distinct(StringComparer.Ordinal)
				.OrderBy(name => name, StringComparer.Ordinal)
				.ToList();
			sources.Replaces = DeriveReplacements(sources.Overrides, texts);
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
	/// Every function's owners, outermost first: <paramref name="mods"/> in their order (highest
	/// priority first), then <see cref="RefiiOwner"/> when <paramref name="builtIn"/> (refii's own
	/// overrides, function to owner) defines it.
	/// </summary>
	private static Dictionary<string, List<string>> OwnersByFunction(
		IReadOnlyList<ModNativeSources> mods, IReadOnlyDictionary<string, string>? builtIn)
	{
		var owners = new Dictionary<string, List<string>>(StringComparer.Ordinal);
		foreach (ModNativeSources mod in mods)
		{
			foreach (string function in mod.Overrides)
			{
				if (!owners.TryGetValue(function, out List<string>? list))
				{
					owners[function] = list = new List<string>();
				}

				list.Add(mod.ModName);
			}
		}

		if (builtIn != null)
		{
			foreach ((string function, string owner) in builtIn)
			{
				if (!owners.TryGetValue(function, out List<string>? list))
				{
					owners[function] = list = new List<string>();
				}

				list.Add(owner);
			}
		}

		return owners;
	}

	/// <summary>
	/// Every hook that replaces a function's body (never calls its original) while an owner hooks
	/// the same function beneath it: one entry per swallowed owner, replacer first.
	/// </summary>
	public static List<ModNativeConflict> FindConflicts(
		IReadOnlyList<ModNativeSources> mods, IReadOnlyDictionary<string, string>? builtIn)
	{
		var conflicts = new List<ModNativeConflict>();
		var replacers = new Dictionary<string, ModNativeSources>(StringComparer.Ordinal);
		foreach (ModNativeSources mod in mods)
		{
			replacers[mod.ModName] = mod;
		}

		foreach ((string function, List<string> owners) in OwnersByFunction(mods, builtIn))
		{
			for (int i = 0; i + 1 < owners.Count; i++)
			{
				if (replacers.TryGetValue(owners[i], out ModNativeSources? mod) && mod.Replaces.Contains(function))
				{
					for (int inner = i + 1; inner < owners.Count; inner++)
					{
						conflicts.Add(new ModNativeConflict(function, owners[i], owners[inner]));
					}
				}
			}
		}

		return conflicts;
	}

	/// <summary>Every function hooked by more than one owner, with its owners outermost first.</summary>
	public static List<ModNativeChain> PlanChains(
		IReadOnlyList<ModNativeSources> mods, IReadOnlyDictionary<string, string>? builtIn)
	{
		return OwnersByFunction(mods, builtIn)
			.Where(entry => entry.Value.Count > 1)
			.OrderBy(entry => entry.Key, StringComparer.Ordinal)
			.Select(entry => new ModNativeChain(entry.Key, entry.Value))
			.ToList();
	}

	/// <summary>The symbol <paramref name="owner"/>'s hook of <paramref name="function"/> compiles to inside a chain.</summary>
	public static string ChainSymbol(string function, string owner)
	{
		var name = new StringBuilder(function.Length + owner.Length + 2);
		name.Append(function).Append("__");
		foreach (char c in owner)
		{
			name.Append(char.IsAsciiLetterOrDigit(c) ? c : '_');
		}

		return name.ToString();
	}

	/// <summary>
	/// The compiler defines <paramref name="owner"/>'s sources take so its hooks join the chains
	/// they are part of: the function's name becomes the owner's own symbol, and the original-call
	/// name becomes the next owner inward. The innermost owner keeps the game's <c>__imp__</c>.
	/// Token pasting re-scans, so <c>REFII_MOD_ORIGINAL(name)</c> is renamed along with the call.
	/// </summary>
	public static List<string> ChainDefines(IReadOnlyList<ModNativeChain> chains, string owner)
	{
		var defines = new List<string>();
		foreach (ModNativeChain chain in chains)
		{
			int at = -1;
			for (int i = 0; i < chain.Owners.Count; i++)
			{
				if (string.Equals(chain.Owners[i], owner, StringComparison.Ordinal))
				{
					at = i;
					break;
				}
			}

			if (at < 0)
			{
				continue;
			}

			defines.Add($"-D{chain.Function}={ChainSymbol(chain.Function, owner)}");
			if (at + 1 < chain.Owners.Count)
			{
				defines.Add($"-D__imp__{chain.Function}={ChainSymbol(chain.Function, chain.Owners[at + 1])}");
			}
		}

		return defines;
	}

	/// <summary>
	/// The one translation unit that defines each chained function's real name: a tail call into
	/// its outermost owner. Empty when nothing is chained.
	/// </summary>
	public static string ChainSource(IReadOnlyList<ModNativeChain> chains)
	{
		if (chains.Count == 0)
		{
			return string.Empty;
		}

		var text = new StringBuilder();
		text.AppendLine("// Generated by the launcher. Each function here is hooked by more than one owner; every");
		text.AppendLine("// owner's hook was compiled under its own symbol with its original-call pointing at the");
		text.AppendLine("// next owner inward, and the game's body stays __imp__<name>. Order is loadorder.toml,");
		text.AppendLine("// outermost first.");
		text.AppendLine("#include <rex/ppc/context.h>");
		text.AppendLine("#include <rex/ppc/func.h>");
		text.AppendLine();
		foreach (ModNativeChain chain in chains)
		{
			string outer = ChainSymbol(chain.Function, chain.Owners[0]);
			text.Append("// ").Append(chain.Function).Append(": ")
				.AppendLine(string.Join(" -> ", chain.Owners));
			text.Append("REX_EXTERN(").Append(outer).AppendLine(");");
			text.Append("REX_EXTERN(").Append(chain.Function).AppendLine(") {");
			text.Append("  [[clang::musttail]] return ").Append(outer).AppendLine("(ctx, base);");
			text.AppendLine("}");
			text.AppendLine();
		}

		return text.ToString();
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
