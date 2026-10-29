using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Refii.Save;

namespace Refii.Titles.Fable2;

using Refii.Mods;

/// <summary>
/// Archon's Toolbox (JustSomeGuy1234's Fable 2 Lua mod manager) applied to the player's install:
/// detected from the game data, listed in the save's sidecar, and counted in the co-op mod set.
/// </summary>
/// <remarks>
/// WHAT THE TOOLBOX LEAVES IN THE INSTALL (its source, <c>Fable2SMM/Manager Scripts/</c>):
/// <list type="bullet">
/// <item><c>Data/gamescripts_r.bnk</c> byte-patched — <c>Gamescripts.cs</c> accepts the two
/// original SHA-256s (<see cref="BankOriginal10"/>, <see cref="BankOriginal1"/>) and recognises
/// its own result (<see cref="BankPatched10"/>, <see cref="BankPatched1"/>); the patch makes
/// <c>GeneralScriptManager</c> run <c>scripts/Mod Manager/generalscriptmanager_override.lua</c>.</item>
/// <item>loose Lua under <c>Data/scripts/Mod Manager/</c> (the runner) and
/// <c>Data/scripts/Mods/&lt;NameID&gt;/</c> (one folder per mod, with <c>modmanifest.json</c>),
/// every file appended to <c>Data/dir.manifest</c> (<c>DirManifest.AddFilesToDirManifest</c>).</item>
/// <item><c>Data/scripts/Mod Manager/installedmods.lua</c> — the mod list the runner reads:
/// <c>local installedmods = { &lt;NameID&gt; = { Enabled, Installed, ClearData, NameID,
/// VersionMajor, VersionMinor, … } }</c>, "no multi-line comments, hex, functions, operations or
/// variable values" by the toolbox's own rule, so a small scanner reads it.</item>
/// </list>
///
/// WHERE ITS SAVE STATE LIVES. The runner is a script table added through the game's own
/// <c>GeneralScriptManager.AddScript</c>; every running mod's table (<c>RunningMods</c>, each
/// mod's environment with its functions), the disabled and uninstalled mods' data and the hook
/// callbacks hang off it. The save carries all of it inside the script save table — the one
/// Pluto object graph the guest writes after the tokenised documents of <c>mainsave.bin</c>
/// (TU1 <c>sub_8245C7B8</c> → <c>sub_8245CB10</c>: the global <c>GetSaveTable</c> is called and
/// its table handed to <c>pluto_persist_entry</c> at <c>0x8245CC10</c>). Nothing of that is
/// filtered here: see <see cref="ModSaveStamp"/>'s remarks for why the blob is kept verbatim.
///
/// The detection is pure over text (<see cref="ParseInstalledMods"/>,
/// <see cref="ManifestScriptLines"/>, <see cref="ClassifyBank"/>, <see cref="RunnerVersion"/>);
/// only <see cref="Detect(string)"/> touches the disk, and it is cached by the files' size and
/// mtime so a save stamp costs stat calls.
/// </remarks>
public static class ArchonToolbox
{
	public const string Title = "Archon's Toolbox";

	/// <summary>SHA-256 (lower-case hex) of the original 10.1 (GOTY / Platinum Hits) gamescripts_r.bnk.</summary>
	public const string BankOriginal10 = "68a2ef1f703c3c325f268edcd5cdadbc5c869e96cf7c7e2e38bc017153c876e2";

	/// <summary>The 10.1 bank with the toolbox's patch applied.</summary>
	public const string BankPatched10 = "7234bccd80ca29cc023dc324d1c92e4d243cb7bd7069faf6f134f697011042fe";

	/// <summary>SHA-256 of the original 1.0 gamescripts_r.bnk.</summary>
	public const string BankOriginal1 = "15bc606d879145b5abaf2939857c891cd8c22b495643ad79fa8c81173370dd92";

	/// <summary>The 1.0 bank with the toolbox's patch applied.</summary>
	public const string BankPatched1 = "a94e7a64f32c86e96760f79bdd0606b81736b9ffe34068140efb9321e9ad7366";

	public const string BankFile = "gamescripts_r.bnk";
	public const string ManifestFile = "dir.manifest";
	public const string ScriptsFolder = "scripts";
	public const string RunnerFolder = "Mod Manager";
	public const string ModsFolder = "Mods";
	public const string RunnerFile = "runner.lua";
	public const string InstalledModsFile = "installedmods.lua";
	public const string ModManifestFile = "modmanifest.json";

	public enum BankState
	{
		/// <summary>No gamescripts_r.bnk.</summary>
		Missing,
		/// <summary>One of the two originals the toolbox accepts.</summary>
		Original,
		/// <summary>The toolbox's own patched result.</summary>
		Patched,
		/// <summary>Neither: changed by something else, or a version the toolbox does not know.</summary>
		Modified,
	}

	/// <summary>One entry of installedmods.lua.</summary>
	public readonly record struct Mod(string Name, string Version, bool Enabled, bool Installed)
	{
		/// <summary>Running under the toolbox: installed and enabled.</summary>
		public bool Active => Enabled && Installed;

		public string Label => (Name + " " + Version).Trim();
	}

	/// <summary>What one install carries of the toolbox.</summary>
	public sealed class Report
	{
		/// <summary>The data directory read (the one holding dir.manifest), or empty.</summary>
		public string DataDirectory = string.Empty;
		/// <summary><c>scripts/Mod Manager/runner.lua</c> exists.</summary>
		public bool Runner;
		/// <summary>The runner's <c>Version = N</c>, or empty.</summary>
		public string RunnerVersion = string.Empty;
		public BankState Bank;
		/// <summary>The bank's SHA-256 (lower-case hex), or empty when missing.</summary>
		public string BankSha256 = string.Empty;
		/// <summary>installedmods.lua's entries, in file order.</summary>
		public List<Mod> Mods = new();
		/// <summary><c>scripts/Mods/&lt;name&gt;</c> folders carrying a modmanifest.json.</summary>
		public List<string> Folders = new();
		/// <summary>dir.manifest lines that point at the toolbox's files.</summary>
		public List<string> ManifestLines = new();

		/// <summary>The toolbox is applied: its runner is on disk, or its patch is in the bank, or the manifest names its files.</summary>
		public bool Present => Runner || Bank == BankState.Patched || ManifestLines.Count != 0;

		/// <summary>The mods running under the toolbox, in file order.</summary>
		public List<Mod> Active
		{
			get
			{
				var active = new List<Mod>();
				foreach (Mod mod in Mods)
				{
					if (mod.Active)
					{
						active.Add(mod);
					}
				}

				return active;
			}
		}

		public string Describe()
		{
			if (!Present)
			{
				return Title + " not applied";
			}

			var text = new StringBuilder(Title);
			text.Append(RunnerVersion.Length == 0 ? string.Empty : " runner " + RunnerVersion);
			text.Append(", bank ").Append(Bank.ToString().ToLowerInvariant());
			List<Mod> active = Active;
			text.Append(", ").Append(active.Count.ToString(CultureInfo.InvariantCulture)).Append(" mod(s) enabled");
			foreach (Mod mod in active)
			{
				text.Append("\n  ").Append(mod.Label);
			}

			return text.ToString();
		}
	}

	// ---------------------------------------------------------------- the disk

	private static readonly object Lock = new();

	private static string _cachedDirectory = string.Empty;
	private static string _cachedStamp = string.Empty;
	private static Report? _cached;

	/// <summary>The install the game runs from: the mounted game directory, else the resolved one.</summary>
	public static string GameDirectory()
	{
		string mounted = Refii.Assets.Engine_GameAssets.Instance?.GameDirectory ?? string.Empty;
		return mounted.Length != 0 ? mounted : Refii.Core.Engine_GameData.Resolve();
	}

	public static Report Detect() => Detect(GameDirectory());

	/// <summary>
	/// The toolbox's state under <paramref name="gameDirectory"/> (<c>assets/game</c> or its
	/// <c>data</c>). Never throws: an unreadable file reads as absent. Cached by the stamps of
	/// the bank, installedmods.lua, dir.manifest and runner.lua.
	/// </summary>
	public static Report Detect(string gameDirectory)
	{
		string data = DataDirectory(gameDirectory);
		if (data.Length == 0)
		{
			return new Report();
		}

		string bank = Path.Combine(data, BankFile);
		string manifest = Path.Combine(data, ManifestFile);
		string scripts = FindDirectory(data, ScriptsFolder);
		string runnerDir = scripts.Length == 0 ? string.Empty : FindDirectory(scripts, RunnerFolder);
		string modsDir = scripts.Length == 0 ? string.Empty : FindDirectory(scripts, ModsFolder);
		string runner = runnerDir.Length == 0 ? string.Empty : Path.Combine(runnerDir, RunnerFile);
		string installed = runnerDir.Length == 0 ? string.Empty : Path.Combine(runnerDir, InstalledModsFile);
		string stamp = StampOf(bank) + "|" + StampOf(manifest) + "|" + StampOf(runner) + "|" + StampOf(installed) + "|" + modsDir;

		lock (Lock)
		{
			if (_cached is not null && _cachedDirectory == data && _cachedStamp == stamp)
			{
				return _cached;
			}
		}

		var report = new Report { DataDirectory = data };
		report.BankSha256 = HashFile(bank);
		report.Bank = ClassifyBank(report.BankSha256);
		report.Runner = runner.Length != 0 && File.Exists(runner);
		if (report.Runner)
		{
			report.RunnerVersion = RunnerVersion(ReadText(runner));
		}

		if (installed.Length != 0 && File.Exists(installed))
		{
			report.Mods = ParseInstalledMods(ReadText(installed));
		}

		if (modsDir.Length != 0 && Directory.Exists(modsDir))
		{
			try
			{
				foreach (string folder in Directory.EnumerateDirectories(modsDir))
				{
					if (File.Exists(Path.Combine(folder, ModManifestFile)))
					{
						report.Folders.Add(Path.GetFileName(folder));
					}
				}

				report.Folders.Sort(StringComparer.OrdinalIgnoreCase);
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException)
			{
			}
		}

		if (File.Exists(manifest))
		{
			report.ManifestLines = ManifestScriptLines(ReadText(manifest));
		}

		lock (Lock)
		{
			_cachedDirectory = data;
			_cachedStamp = stamp;
			_cached = report;
		}

		return report;
	}

	/// <summary>
	/// The directory holding dir.manifest: <paramref name="gameDirectory"/> itself, or its
	/// <c>data</c> child (either case). Empty when neither has one.
	/// </summary>
	public static string DataDirectory(string gameDirectory)
	{
		if (string.IsNullOrEmpty(gameDirectory) || !Directory.Exists(gameDirectory))
		{
			return string.Empty;
		}

		if (File.Exists(Path.Combine(gameDirectory, ManifestFile)))
		{
			return gameDirectory;
		}

		string data = FindDirectory(gameDirectory, "data");
		return data.Length != 0 && File.Exists(Path.Combine(data, ManifestFile)) ? data : string.Empty;
	}

	/// <summary>A child directory by name, case-insensitively (the toolbox writes Windows paths).</summary>
	private static string FindDirectory(string parent, string name)
	{
		string exact = Path.Combine(parent, name);
		if (Directory.Exists(exact))
		{
			return exact;
		}

		try
		{
			foreach (string child in Directory.EnumerateDirectories(parent))
			{
				if (string.Equals(Path.GetFileName(child), name, StringComparison.OrdinalIgnoreCase))
				{
					return child;
				}
			}
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
		}

		return string.Empty;
	}

	private static string StampOf(string path)
	{
		try
		{
			if (path.Length == 0 || !File.Exists(path))
			{
				return "-";
			}

			var info = new FileInfo(path);
			return info.Length.ToString(CultureInfo.InvariantCulture) + ":" + info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			return "?";
		}
	}

	private static string ReadText(string path)
	{
		try
		{
			return File.ReadAllText(path);
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			return string.Empty;
		}
	}

	/// <summary>The file's SHA-256 as lower-case hex, empty when it cannot be read.</summary>
	public static string HashFile(string path)
	{
		try
		{
			if (!File.Exists(path))
			{
				return string.Empty;
			}

			using FileStream stream = File.OpenRead(path);
			return Convert.ToHexString(Refii.Core.Engine_Hash.Sha256(stream)).ToLowerInvariant();
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			return string.Empty;
		}
	}

	// ---------------------------------------------------------------- the parsers

	public static BankState ClassifyBank(string sha256)
	{
		if (string.IsNullOrEmpty(sha256))
		{
			return BankState.Missing;
		}

		string sha = sha256.ToLowerInvariant();
		if (sha == BankPatched10 || sha == BankPatched1)
		{
			return BankState.Patched;
		}

		return sha == BankOriginal10 || sha == BankOriginal1 ? BankState.Original : BankState.Modified;
	}

	/// <summary>The runner's <c>Version = 1.04</c> line (what the toolbox itself regex-reads), or empty.</summary>
	public static string RunnerVersion(string runnerLua)
	{
		const string key = "Version = ";
		int at = runnerLua.IndexOf(key, StringComparison.Ordinal);
		if (at < 0)
		{
			return string.Empty;
		}

		int start = at + key.Length;
		int end = start;
		while (end < runnerLua.Length && (char.IsDigit(runnerLua[end]) || runnerLua[end] == '.'))
		{
			++end;
		}

		return runnerLua[start..end];
	}

	/// <summary>
	/// installedmods.lua's mods: the direct children of the <c>installedmods</c> table, each a
	/// table of scalar fields. The name is the entry's <c>NameID</c> (else its key), the version
	/// <c>VersionMajor.VersionMinor</c> as written. Tolerant: an entry without a table or with no
	/// fields is still a mod.
	/// </summary>
	public static List<Mod> ParseInstalledMods(string lua)
	{
		var mods = new List<Mod>();
		string text = StripComments(lua);
		int open = -1;
		int at = text.IndexOf("installedmods", StringComparison.Ordinal);
		while (at >= 0)
		{
			int eq = SkipSpaces(text, at + "installedmods".Length);
			if (eq < text.Length && text[eq] == '=')
			{
				int brace = SkipSpaces(text, eq + 1);
				if (brace < text.Length && text[brace] == '{')
				{
					open = brace;
					break;
				}
			}

			at = text.IndexOf("installedmods", at + 1, StringComparison.Ordinal);
		}

		if (open < 0)
		{
			return mods;
		}

		// Walk the table's direct entries: `Key = { … }` at depth 1.
		int pos = open + 1;
		while (pos < text.Length)
		{
			pos = SkipSpaces(text, pos);
			if (pos >= text.Length || text[pos] == '}')
			{
				break;
			}

			if (text[pos] == ',' || text[pos] == ';')
			{
				++pos;
				continue;
			}

			string key = ReadKey(text, ref pos);
			pos = SkipSpaces(text, pos);
			if (key.Length == 0 || pos >= text.Length || text[pos] != '=')
			{
				pos = SkipValue(text, pos);
				continue;
			}

			pos = SkipSpaces(text, pos + 1);
			if (pos < text.Length && text[pos] == '{')
			{
				int close = MatchBrace(text, pos);
				string body = text[(pos + 1)..Math.Max(pos + 1, close)];
				mods.Add(ModFrom(key, body));
				pos = close + 1;
			}
			else
			{
				pos = SkipValue(text, pos);
			}
		}

		return mods;
	}

	private static Mod ModFrom(string key, string body)
	{
		Dictionary<string, string> fields = ScalarFields(body);
		string name = fields.TryGetValue("NameID", out string? id) && id.Length != 0 ? id : key;
		string version = string.Empty;
		if (fields.TryGetValue("VersionMajor", out string? major) && major.Length != 0)
		{
			version = major;
			if (fields.TryGetValue("VersionMinor", out string? minor) && minor.Length != 0)
			{
				version += "." + minor;
			}
		}

		return new Mod(name, version, IsTrue(fields, "Enabled"), IsTrue(fields, "Installed"));
	}

	private static bool IsTrue(Dictionary<string, string> fields, string key) =>
		fields.TryGetValue(key, out string? value) && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

	/// <summary>The depth-0 <c>Key = scalar</c> pairs of a table body; nested tables are skipped whole.</summary>
	private static Dictionary<string, string> ScalarFields(string body)
	{
		var fields = new Dictionary<string, string>(StringComparer.Ordinal);
		int pos = 0;
		while (pos < body.Length)
		{
			pos = SkipSpaces(body, pos);
			if (pos >= body.Length)
			{
				break;
			}

			if (body[pos] == ',' || body[pos] == ';')
			{
				++pos;
				continue;
			}

			string key = ReadKey(body, ref pos);
			pos = SkipSpaces(body, pos);
			if (key.Length == 0 || pos >= body.Length || body[pos] != '=')
			{
				pos = SkipValue(body, pos);
				continue;
			}

			pos = SkipSpaces(body, pos + 1);
			if (pos < body.Length && body[pos] == '{')
			{
				pos = MatchBrace(body, pos) + 1;
				continue;
			}

			int start = pos;
			pos = SkipValue(body, pos);
			string value = body[start..pos].Trim();
			if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
			{
				value = value[1..^1];
			}

			fields[key] = value;
		}

		return fields;
	}

	private static string StripComments(string lua)
	{
		var text = new StringBuilder(lua.Length);
		foreach (string raw in lua.Split('\n'))
		{
			string line = raw;
			bool inString = false;
			char quote = '\0';
			for (int i = 0; i < line.Length; i++)
			{
				char c = line[i];
				if (inString)
				{
					if (c == '\\')
					{
						++i;
					}
					else if (c == quote)
					{
						inString = false;
					}
				}
				else if (c == '"' || c == '\'')
				{
					inString = true;
					quote = c;
				}
				else if (c == '-' && i + 1 < line.Length && line[i + 1] == '-')
				{
					line = line[..i];
					break;
				}
			}

			text.Append(line).Append('\n');
		}

		return text.ToString();
	}

	private static int SkipSpaces(string text, int pos)
	{
		while (pos < text.Length && char.IsWhiteSpace(text[pos]))
		{
			++pos;
		}

		return pos;
	}

	private static string ReadKey(string text, ref int pos)
	{
		int start = pos;
		if (pos < text.Length && text[pos] == '[')
		{
			// ["Name"] = …
			int close = text.IndexOf(']', pos);
			if (close < 0)
			{
				pos = text.Length;
				return string.Empty;
			}

			string inner = text[(pos + 1)..close].Trim();
			pos = close + 1;
			return inner.Length >= 2 && (inner[0] == '"' || inner[0] == '\'') ? inner[1..^1] : inner;
		}

		while (pos < text.Length && (char.IsLetterOrDigit(text[pos]) || text[pos] == '_'))
		{
			++pos;
		}

		return text[start..pos];
	}

	/// <summary>Past one value: a string, a nested table, or a bare word; stops at `,`, `;`, `}` or a newline.</summary>
	private static int SkipValue(string text, int pos)
	{
		if (pos >= text.Length)
		{
			return pos;
		}

		if (text[pos] == '{')
		{
			return MatchBrace(text, pos) + 1;
		}

		if (text[pos] == '"' || text[pos] == '\'')
		{
			char quote = text[pos];
			for (int i = pos + 1; i < text.Length; i++)
			{
				if (text[i] == '\\')
				{
					++i;
				}
				else if (text[i] == quote)
				{
					return i + 1;
				}
			}

			return text.Length;
		}

		while (pos < text.Length && text[pos] != ',' && text[pos] != ';' && text[pos] != '}' && text[pos] != '\n')
		{
			++pos;
		}

		return pos;
	}

	private static int MatchBrace(string text, int open)
	{
		int depth = 0;
		for (int i = open; i < text.Length; i++)
		{
			char c = text[i];
			if (c == '"' || c == '\'')
			{
				i = SkipValue(text, i) - 1;
				continue;
			}

			if (c == '{')
			{
				++depth;
			}
			else if (c == '}' && --depth == 0)
			{
				return i;
			}
		}

		return text.Length - 1;
	}

	/// <summary>
	/// The dir.manifest lines that name the toolbox's files: anything under <c>scripts\Mod
	/// Manager\</c>, <c>scripts\Mods\</c> or <c>scripts\GUIState\</c>, and its
	/// <c>scripts\Quests\MultipageMenu.lua</c> (the forced list, <c>dir.forced.manifest</c>).
	/// Separators and case are the manifest's own; both slashes are read.
	/// </summary>
	public static List<string> ManifestScriptLines(string manifest)
	{
		var lines = new List<string>();
		foreach (string raw in manifest.Split('\n'))
		{
			string line = raw.Trim();
			if (line.Length == 0)
			{
				continue;
			}

			string key = line.Replace('/', '\\').ToLowerInvariant();
			if (key.StartsWith("scripts\\mod manager\\", StringComparison.Ordinal) ||
				key.StartsWith("scripts\\mods\\", StringComparison.Ordinal) ||
				key.StartsWith("scripts\\guistate\\", StringComparison.Ordinal) ||
				key == "scripts\\quests\\multipagemenu.lua")
			{
				lines.Add(line);
			}
		}

		return lines;
	}

	// ---------------------------------------------------------------- the co-op set

	/// <summary>The name prefix of the toolbox's entries in a <see cref="ModFingerprint"/>.</summary>
	public const string FingerprintPrefix = "archon:";

	public static bool IsFingerprintEntry(ModFingerprint.Mod mod) =>
		mod.Name.StartsWith(FingerprintPrefix, StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// One set entry per mod running under the toolbox, sorted by name so both machines state
	/// them in one order: <c>archon:&lt;NameID&gt;</c>, titled under the toolbox, versioned as
	/// installedmods.lua says, with the PATCHED BANK's hash as the package hash (the scripts the
	/// mod runs against) and <c>Native</c> set — it cannot be packaged, downloaded, suspended or
	/// replaced for a session (<see cref="NotSyncable"/>).
	/// </summary>
	public static List<ModFingerprint.Mod> FingerprintEntries(Report report)
	{
		var entries = new List<ModFingerprint.Mod>();
		if (!report.Present)
		{
			return entries;
		}

		List<Mod> active = report.Active;
		active.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
		foreach (Mod mod in active)
		{
			entries.Add(new ModFingerprint.Mod(
				FingerprintPrefix + mod.Name, Title + ": " + mod.Name, mod.Version, report.BankSha256, 0, Native: true));
		}

		return entries;
	}

	/// <summary>
	/// Why the toolbox's mods are never synced: they are loose files in the game data and a
	/// byte-patched script bank, not a package Refii can send, mount for a session or put down.
	/// </summary>
	public const string NotSyncable =
		"an Archon's Toolbox mod: loose script files and a patched gamescripts bank in the game data, which cannot be packaged or synced for a session";

	/// <summary>The joiner lacks the host's toolbox mod (or runs another build of it).</summary>
	public static string BlockedText(ModFingerprint.Mod theirs, bool haveAnotherBuild) =>
		haveAnotherBuild
			? $"{theirs.Label} is {NotSyncable}; your copy differs from the host's — match the toolbox's bank and the mod's version, then join"
			: $"{theirs.Label} is {NotSyncable}; install it with {Title}, enable it and relaunch to join";

	/// <summary>The joiner's own toolbox mod the host does not run.</summary>
	public static string OwnBlockedText(ModFingerprint.Mod mine) =>
		$"your {mine.Label} is {NotSyncable}; disable it in {Title} and relaunch to join";

	// ---------------------------------------------------------------- the save layer

	/// <summary>The sidecar's <c>archon</c> layer for the install as it is now; null when the toolbox is not applied.</summary>
	public static ArchonLayer? Stamp(Report report)
	{
		if (!report.Present)
		{
			return null;
		}

		var layer = new ArchonLayer
		{
			Bank = report.BankSha256,
			Patched = report.Bank == BankState.Patched,
			Runner = report.RunnerVersion,
		};
		foreach (Mod mod in report.Active)
		{
			layer.Mods.Add(new ArchonMod { Name = mod.Name, Version = mod.Version });
		}

		return layer;
	}

	/// <summary>
	/// How a save's toolbox stamp compares with the install now: <see cref="Missing"/> the
	/// stamped mods not running under the toolbox now (all of them when
	/// <see cref="Present"/> is false), <see cref="Added"/> the ones running now the save never saw.
	/// </summary>
	public sealed record Diff(IReadOnlyList<ArchonMod> Missing, IReadOnlyList<Mod> Added, bool Present)
	{
		public bool Same => Missing.Count == 0 && Added.Count == 0;
	}

	public static Diff Compare(ArchonLayer? stamp, Report now)
	{
		var running = new Dictionary<string, Mod>(StringComparer.OrdinalIgnoreCase);
		foreach (Mod mod in now.Active)
		{
			running[mod.Name] = mod;
		}

		var missing = new List<ArchonMod>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (ArchonMod was in stamp?.Mods ?? new List<ArchonMod>())
		{
			seen.Add(was.Name);
			if (!now.Present || !running.ContainsKey(was.Name))
			{
				missing.Add(was);
			}
		}

		var added = new List<Mod>();
		foreach (Mod mod in now.Active)
		{
			if (!seen.Contains(mod.Name))
			{
				added.Add(mod);
			}
		}

		return new Diff(missing, added, now.Present);
	}

	/// <summary>The heading of the toolbox's part of the "Mods in this save" page.</summary>
	public const string NoticeHeading = "Made with these " + Title + " mods:";

	/// <summary>The line a stamped mod carries when the toolbox itself is not applied now.</summary>
	public const string NotPresentMark = " — " + Title + " not present";

	/// <summary>The line a stamped mod carries when the toolbox is applied but the mod is not enabled in it.</summary>
	public const string NotEnabledMark = " — not enabled in " + Title;

	/// <summary>
	/// The toolbox's part of the notice: its mods one per line, the ones not running now marked,
	/// and — because nothing of theirs is filtered — the plain statement that their state stays in
	/// the save. The runner itself is in the save (a script table the game persists with its
	/// functions): without its files it reports that installedmods.lua is missing, and a mod whose
	/// folder is gone is offered for removal by the runner's own box, so the player is told to put
	/// the toolbox back to manage them.
	/// </summary>
	public static string NoticeText(ArchonLayer stamp, Diff diff)
	{
		var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (ArchonMod mod in diff.Missing)
		{
			missing.Add(mod.Name);
		}

		var text = new StringBuilder();
		text.Append(stamp.Mods.Count == 0 ? "Made with " + Title + " applied.\n" : NoticeHeading + "\n");
		foreach (ArchonMod mod in stamp.Mods)
		{
			text.Append(mod.Label);
			if (missing.Contains(mod.Name))
			{
				text.Append(diff.Present ? NotEnabledMark : NotPresentMark);
			}

			text.Append('\n');
		}

		if (!diff.Present)
		{
			text.Append('\n').Append(Title).Append(" is not applied to this install. The mod manager and its mods' state stay inside the save as they were; apply ")
				.Append(Title).Append(" to this install to manage them.\n");
		}
		else if (missing.Count != 0)
		{
			text.Append("\nA mod not enabled keeps its state inside the save; the mod manager will ask about mods whose files are gone.\n");
		}

		return text.ToString();
	}
}
