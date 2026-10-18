using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Refii.Mods;

/// <summary>
/// The enabled mod set as one comparable value: what two co-op players must agree on before
/// they share a world.
/// </summary>
/// <remarks>
/// STRICT. Every enabled mod counts — one that only changes a texture can still carry Lua — and
/// a mod counts by its package BYTES as well as its name and version, so two builds of "0.1.0"
/// with different contents are different mods. The load order counts, because it decides which
/// override wins. The mod API version counts, because the same mod reads differently under
/// another API.
///
/// The list logic (<see cref="From"/>, <see cref="Digest"/>, <see cref="Describe"/> and the wire
/// form) is pure and runs on in-memory lists; only <see cref="Current()"/> and
/// <see cref="HashPackage"/> touch the disk.
/// </remarks>
public sealed class ModFingerprint
{
	/// <summary>One enabled mod. <see cref="Name"/> is the package name, the load order's key.</summary>
	public readonly record struct Mod(string Name, string Title, string Version, string Sha256)
	{
		/// <summary>The title and version, as a player reads them.</summary>
		public string Label => (Display + " " + Version).Trim();

		public string Display => Title.Length == 0 ? Name : Title;
	}

	/// <summary>The co-op channel message: <c>mods=&lt;digest&gt;/&lt;api&gt;/&lt;total&gt;:&lt;entries&gt;</c>.</summary>
	public const string WirePrefix = "mods=";

	/// <summary>Hex characters of a package's SHA-256 carried per entry on the wire.</summary>
	public const int WireShaChars = 12;

	/// <summary>Default UTF-8 budget for one message, under the channel's 1100-byte datagram.</summary>
	public const int WireBudget = 1024;

	/// <summary>The mod API version this set was built against.</summary>
	public int ApiVersion { get; }

	/// <summary>The enabled mods, highest priority first, as the load order lists them.</summary>
	public IReadOnlyList<Mod> Mods { get; }

	/// <summary>
	/// How many mods the set holds. Equal to <see cref="Mods"/>'s count for a local set; a
	/// decoded peer set whose list did not fit one message carries fewer entries than this.
	/// </summary>
	public int Total { get; }

	/// <summary>Whether <see cref="Mods"/> is the whole set.</summary>
	public bool Complete => Mods.Count == Total;

	private readonly string _digest;

	private ModFingerprint(int apiVersion, IReadOnlyList<Mod> mods, int total, string digest)
	{
		ApiVersion = apiVersion;
		Mods = mods;
		Total = total;
		_digest = digest;
	}

	/// <summary>A set from an in-memory list, highest priority first.</summary>
	public static ModFingerprint From(int apiVersion, IReadOnlyList<Mod> mods)
	{
		var copy = new List<Mod>(mods);
		return new ModFingerprint(apiVersion, copy, copy.Count, ComputeDigest(apiVersion, copy));
	}

	/// <summary>
	/// The short digest: the first 16 hex characters of the SHA-256 of the canonical form — the
	/// API version, then per mod in load order its lower-cased name, version and package SHA-256.
	/// The title is display only and is not hashed; it lives inside the package bytes anyway.
	/// </summary>
	public string Digest() => _digest;

	private static string ComputeDigest(int apiVersion, IReadOnlyList<Mod> mods)
	{
		var canonical = new StringBuilder();
		canonical.Append("refii-mods/1\napi=").Append(apiVersion.ToString(CultureInfo.InvariantCulture)).Append('\n');
		foreach (Mod mod in mods)
		{
			canonical.Append(mod.Name.ToLowerInvariant()).Append('\t')
				.Append(mod.Version).Append('\t')
				.Append(mod.Sha256.ToLowerInvariant()).Append('\n');
		}

		byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
		return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
	}

	// ---------------------------------------------------------------- diff

	/// <summary>
	/// What differs between this set and <paramref name="other"/>, as one line a player can act
	/// on — "joiner lacks Souls Combat 0.1.0; host lacks My Mod 1.2". Empty when the digests agree.
	/// <paramref name="self"/> and <paramref name="otherName"/> name the two sides.
	/// </summary>
	public string Describe(ModFingerprint other, string self, string otherName)
	{
		if (Digest() == other.Digest())
		{
			return string.Empty;
		}

		var parts = new List<string>();
		if (ApiVersion != other.ApiVersion)
		{
			parts.Add($"{self} runs mod API {ApiVersion}, {otherName} runs mod API {other.ApiVersion}");
		}

		var theirs = new Dictionary<string, Mod>(StringComparer.OrdinalIgnoreCase);
		foreach (Mod mod in other.Mods)
		{
			theirs[mod.Name] = mod;
		}

		var mine = new Dictionary<string, Mod>(StringComparer.OrdinalIgnoreCase);
		foreach (Mod mod in Mods)
		{
			mine[mod.Name] = mod;
		}

		foreach (Mod mod in Mods)
		{
			if (!theirs.TryGetValue(mod.Name, out Mod their))
			{
				// A list that did not fit one message cannot prove a mod absent.
				if (other.Complete)
				{
					parts.Add($"{otherName} lacks {mod.Label}");
				}

				continue;
			}

			if (!string.Equals(mod.Version, their.Version, StringComparison.Ordinal))
			{
				parts.Add($"{mod.Display}: {self} has {Shown(mod.Version)}, {otherName} has {Shown(their.Version)}");
			}
			else if (!SameBytes(mod.Sha256, their.Sha256))
			{
				parts.Add($"{mod.Label} is a different package on each side");
			}
		}

		foreach (Mod mod in other.Mods)
		{
			if (!mine.ContainsKey(mod.Name))
			{
				parts.Add($"{self} lacks {mod.Label}");
			}
		}

		if (!other.Complete)
		{
			parts.Add($"{otherName} has {other.Total} mods enabled, more than one message could list");
		}

		// THE SAME MODS IN ANOTHER ORDER: nothing above fired, and the sequences differ.
		if (parts.Count == 0 && other.Complete && Mods.Count == other.Mods.Count)
		{
			bool reordered = false;
			for (int i = 0; i < Mods.Count; i++)
			{
				if (!string.Equals(Mods[i].Name, other.Mods[i].Name, StringComparison.OrdinalIgnoreCase))
				{
					reordered = true;
					break;
				}
			}

			if (reordered)
			{
				parts.Add($"the load order differs ({self}: {Order(this)}; {otherName}: {Order(other)})");
			}
		}

		return parts.Count == 0 ? "the mod sets differ" : string.Join("; ", parts);
	}

	private static string Shown(string version) => version.Length == 0 ? "no version" : version;

	/// <summary>Package hashes agree on the characters both sides carry; an empty one never agrees.</summary>
	private static bool SameBytes(string left, string right)
	{
		int length = Math.Min(left.Length, right.Length);
		return length != 0 &&
			string.Compare(left, 0, right, 0, length, StringComparison.OrdinalIgnoreCase) == 0;
	}

	private static string Order(ModFingerprint set)
	{
		var names = new List<string>(set.Mods.Count);
		foreach (Mod mod in set.Mods)
		{
			names.Add(mod.Display);
		}

		return string.Join(", ", names);
	}

	// ---------------------------------------------------------------- wire

	/// <summary>
	/// The co-op channel message for this set. The DIGEST is the authority — the receiver
	/// compares it with its own — and the entries are only for the diff, so a list longer than
	/// <paramref name="budget"/> UTF-8 bytes carries as many entries as fit and states the total.
	/// Fields are tab-separated (name, title, version, short SHA-256), entries newline-separated.
	/// </summary>
	public string Encode(int budget = WireBudget)
	{
		var text = new StringBuilder();
		text.Append(WirePrefix).Append(Digest()).Append('/')
			.Append(ApiVersion.ToString(CultureInfo.InvariantCulture)).Append('/')
			.Append(Total.ToString(CultureInfo.InvariantCulture)).Append(':');
		int used = Encoding.UTF8.GetByteCount(text.ToString());

		bool first = true;
		foreach (Mod mod in Mods)
		{
			string sha = mod.Sha256.Length > WireShaChars ? mod.Sha256[..WireShaChars] : mod.Sha256;
			string title = string.Equals(mod.Title, mod.Name, StringComparison.Ordinal) ? string.Empty : mod.Title;
			string entry = (first ? string.Empty : "\n") +
				Field(mod.Name) + "\t" + Field(title) + "\t" + Field(mod.Version) + "\t" + Field(sha);
			int size = Encoding.UTF8.GetByteCount(entry);
			if (used + size > budget)
			{
				break;
			}

			text.Append(entry);
			used += size;
			first = false;
		}

		return text.ToString();
	}

	/// <summary>Reads a peer's <see cref="Encode"/> message. False for anything else.</summary>
	public static bool TryDecode(string message, out ModFingerprint? set)
	{
		set = null;
		if (!message.StartsWith(WirePrefix, StringComparison.Ordinal))
		{
			return false;
		}

		int colon = message.IndexOf(':', WirePrefix.Length);
		if (colon < 0)
		{
			return false;
		}

		string[] head = message[WirePrefix.Length..colon].Split('/');
		if (head.Length != 3 || head[0].Length == 0 ||
			!int.TryParse(head[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int api) ||
			!int.TryParse(head[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int total) ||
			total < 0)
		{
			return false;
		}

		var mods = new List<Mod>();
		string body = message[(colon + 1)..];
		if (body.Length != 0)
		{
			foreach (string line in body.Split('\n'))
			{
				string[] fields = line.Split('\t');
				if (fields.Length != 4 || fields[0].Length == 0)
				{
					return false;
				}

				mods.Add(new Mod(fields[0], fields[1].Length == 0 ? fields[0] : fields[1], fields[2], fields[3]));
			}
		}

		if (mods.Count > total)
		{
			return false;
		}

		set = new ModFingerprint(api, mods, total, head[0].ToLowerInvariant());
		return true;
	}

	/// <summary>A field with the wire's separators and control characters made spaces.</summary>
	private static string Field(string value)
	{
		var clean = new StringBuilder(value.Length);
		foreach (char c in value)
		{
			clean.Append(char.IsControl(c) ? ' ' : c);
		}

		return clean.ToString();
	}

	// ---------------------------------------------------------------- disk

	/// <summary>
	/// The set this instance runs: every enabled mod whose package is installed, in the load
	/// order's order — the same resolution the mount and the script loader make — under
	/// <see cref="ModApi.Version"/>.
	/// </summary>
	public static ModFingerprint Current() =>
		Current(Refii.Assets.Engine_GameAssets.ModsRoot, ModApi.Version);

	public static ModFingerprint Current(string modsRoot, int apiVersion)
	{
		var mods = new List<Mod>();
		if (!string.IsNullOrEmpty(modsRoot) && Directory.Exists(modsRoot))
		{
			ModLoadOrder order = ModLoadOrder.Read(modsRoot);
			var installs = new Dictionary<string, ModInstall>(StringComparer.OrdinalIgnoreCase);
			foreach (ModInstall install in ModWorkspace.DiscoverModInstalls(modsRoot))
			{
				installs[install.Name] = install;
			}

			foreach (string name in order.Enabled)
			{
				if (installs.TryGetValue(name, out ModInstall? mod) && File.Exists(mod.PackagePath))
				{
					mods.Add(new Mod(mod.Name, mod.Metadata.Title, mod.Metadata.Version, HashPackage(mod.PackagePath)));
				}
			}
		}

		return From(apiVersion, mods);
	}

	private static readonly object HashLock = new();

	private static readonly Dictionary<string, (string Stamp, string Sha256)> Hashes =
		new(StringComparer.Ordinal);

	/// <summary>
	/// The SHA-256 of a package's bytes as lower-case hex, empty when it cannot be read. Cached
	/// by the package's length+mtime stamp (<see cref="ModManifest.ComputeStamp"/>), in memory and
	/// beside the manifests at <c>&lt;mods&gt;/.cache/fingerprints/&lt;package&gt;.sha256</c>, so an
	/// unchanged package is hashed once.
	/// </summary>
	public static string HashPackage(string packagePath)
	{
		string stamp = ModManifest.ComputeStamp(packagePath);
		if (stamp.Length == 0)
		{
			return string.Empty;
		}

		lock (HashLock)
		{
			if (Hashes.TryGetValue(packagePath, out var held) && held.Stamp == stamp)
			{
				return held.Sha256;
			}
		}

		string cacheDir = Path.Combine(Path.GetDirectoryName(packagePath) ?? string.Empty, ".cache", "fingerprints");
		string cachePath = Path.Combine(cacheDir, Path.GetFileName(packagePath) + ".sha256");
		string sha = ReadCached(cachePath, stamp);
		if (sha.Length == 0)
		{
			try
			{
				using FileStream stream = File.OpenRead(packagePath);
				sha = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
			}
			catch (Exception)
			{
				return string.Empty;
			}

			try
			{
				Directory.CreateDirectory(cacheDir);
				File.WriteAllText(cachePath, stamp + "\n" + sha + "\n");
			}
			catch (Exception)
			{
			}
		}

		lock (HashLock)
		{
			Hashes[packagePath] = (stamp, sha);
		}

		return sha;
	}

	private static string ReadCached(string cachePath, string stamp)
	{
		try
		{
			if (!File.Exists(cachePath))
			{
				return string.Empty;
			}

			string[] lines = File.ReadAllLines(cachePath);
			return lines.Length >= 2 && lines[0] == stamp && lines[1].Length == 64 ? lines[1] : string.Empty;
		}
		catch (Exception)
		{
			return string.Empty;
		}
	}
}
