using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Refii.Mods;

/// <summary>
/// The schema check over whole mods: every entry of a package (or a loose asset tree) whose path is
/// a checked kind, and its <c>mod.toml</c>. Separate from the per-file checks so those stay free of
/// the package reader.
/// </summary>
public static partial class ModValidate
{
	/// <summary>
	/// Every issue of every enabled mod, in load order, for the mount to log once:
	/// <c>[mods] &lt;mod&gt;/&lt;issue&gt;</c>.
	/// </summary>
	public static List<(string Mod, ModIssue Issue)> ValidateOverlays(IReadOnlyList<ModOverlay> overlays)
	{
		var found = new List<(string Mod, ModIssue Issue)>();
		foreach (ModOverlay overlay in overlays)
		{
			string source = !string.IsNullOrEmpty(overlay.PackagePath) ? overlay.PackagePath : overlay.AssetRoot;
			foreach (ModIssue issue in ValidateSource(source))
			{
				found.Add((overlay.Name, issue));
			}
		}

		return found;
	}

	/// <summary>
	/// Every issue of one mod: a <c>.f2mod</c> package (its metadata checked as the <c>mod.toml</c> it
	/// was packed from, re-stated) or a loose folder (its own <c>mod.toml</c> and every file), with what
	/// <paramref name="title"/> (its folder; the active title's when null) does not read or raise.
	/// </summary>
	public static List<ModIssue> ValidateSource(string source, string? title = null)
	{
		title ??= Refii.Core.Engine_Title.Active.Folder;
		var issues = new List<ModIssue>();
		if (File.Exists(source) && source.EndsWith(ModWorkspace.PackageExtension, StringComparison.OrdinalIgnoreCase))
		{
			if (!F2ModPackage.TryOpen(source, out F2ModPackage? package, out string error) || package is null)
			{
				issues.Add(new ModIssue(Path.GetFileName(source), string.Empty, true, $"the package cannot be read: {error}"));
				return issues;
			}

			using (package)
			{
				var texts = new Dictionary<string, string>(StringComparer.Ordinal)
				{
					[ModWorkspace.ModMetadataFilename] = ModWorkspace.FormatModToml(package.Metadata),
				};
				issues.AddRange(Validate(ModWorkspace.ModMetadataFilename, texts[ModWorkspace.ModMetadataFilename]));
				foreach (F2ModEntry entry in package.Entries)
				{
					issues.AddRange(TitleIssues(entry.Path, () => Encoding.UTF8.GetString(package.ReadEntryBytes(entry)), title));
					if (Normalize(entry.Path) == EnglishTable)
					{
						texts[EnglishTable] = Encoding.UTF8.GetString(package.ReadEntryBytes(entry));
					}

					if (KindOf(entry.Path).Length == 0 || KindOf(entry.Path) == ModWorkspace.ModMetadataFilename)
					{
						continue;
					}

					string text;
					try
					{
						text = Encoding.UTF8.GetString(package.ReadEntryBytes(entry));
					}
					catch (Exception exception)
					{
						issues.Add(new ModIssue(Normalize(entry.Path), string.Empty, true, $"cannot be read: {exception.Message}"));
						continue;
					}

					texts[Normalize(entry.Path)] = text;
					issues.AddRange(Validate(entry.Path, text));
				}

				issues.AddRange(TextKeys(texts));
			}

			return issues;
		}

		if (!Directory.Exists(source))
		{
			return issues;
		}

		var loose = new Dictionary<string, string>(StringComparer.Ordinal);
		string toml = Path.Combine(source, ModWorkspace.ModMetadataFilename);
		if (File.Exists(toml))
		{
			loose[ModWorkspace.ModMetadataFilename] = ReadText(toml);
			issues.AddRange(Validate(ModWorkspace.ModMetadataFilename, loose[ModWorkspace.ModMetadataFilename]));
		}

		foreach (string file in ModManifest.EnumerateAssets(source))
		{
			string relative = Path.GetRelativePath(source, file);
			issues.AddRange(TitleIssues(relative, () => ReadText(file), title));
			if (Normalize(relative) == EnglishTable)
			{
				loose[EnglishTable] = ReadText(file);
			}

			if (KindOf(relative).Length != 0)
			{
				string text = ReadText(file);
				loose[Normalize(relative)] = text;
				issues.AddRange(Validate(relative, text));
			}
		}

		issues.AddRange(TextKeys(loose));
		return issues;
	}

	/// <summary>A mod's own en-uk text table, which every text key it names must have a row in.</summary>
	public const string EnglishTable = "language/en-uk/text/book.babel.csv";

	/// <summary>
	/// THE TEXT KEYS a mod names where the player reads them — <c>{KEY}</c> in <c>settings.ui.json</c>'s
	/// labels, descriptions and option labels, <c>input.json</c>'s labels and <c>"touch"</c> pill, label and
	/// caption text, and <c>mod.toml</c>'s name and
	/// description (<see cref="ModText"/>) — each WARNED when the mod's own en-uk table
	/// (<see cref="EnglishTable"/>) has no row for it: in a language the mod does not translate it would
	/// fall to the running book (fine for a game tag) or be drawn as written. <paramref name="files"/> is
	/// the mod's texts by normalised path (<see cref="Normalize"/>), <c>mod.toml</c> under its own name.
	/// </summary>
	public static List<ModIssue> TextKeys(IReadOnlyDictionary<string, string> files)
	{
		var issues = new List<ModIssue>();
		var english = new HashSet<string>(StringComparer.Ordinal);
		if (files.TryGetValue(EnglishTable, out string? table))
		{
			foreach (Refii.Text.BabelOverlayRow row in Refii.Text.BabelOverlay.ParseCsv(table, new List<string>()))
			{
				english.Add(row.Key);
			}
		}

		void Check(string path, string pointer, string? text)
		{
			foreach (string key in ModText.Keys(text))
			{
				if (!english.Contains(key))
				{
					issues.Add(new ModIssue(path, pointer, false,
						$"names the text key {{{key}}}, which {EnglishTable} has no row for; it is drawn from the game's book if the " +
						"game has that tag, else as written"));
				}
			}
		}

		if (files.TryGetValue(ModWorkspace.ModMetadataFilename, out string? toml))
		{
			ModMetadata metadata = ModWorkspace.ParseModToml(toml.TrimStart('\uFEFF'));
			Check(ModWorkspace.ModMetadataFilename, "name", metadata.Title);
			Check(ModWorkspace.ModMetadataFilename, "description", metadata.Description);
		}

		foreach ((string path, string[] fields, bool options) in new[]
		{
			(ModSettingsUi.ModSettingsUiPath, new[] { "label", "description" }, true),
			(ModInput.ModInputPath, new[] { "label" }, false),
		})
		{
			if (!files.TryGetValue(path, out string? json))
			{
				continue;
			}

			JsonDocument document;
			try
			{
				document = JsonDocument.Parse(json.TrimStart('\uFEFF'),
					new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
			}
			catch (JsonException)
			{
				continue;  // the file's own check says why
			}

			using (document)
			{
				if (document.RootElement.ValueKind != JsonValueKind.Object)
				{
					continue;
				}

				foreach (JsonProperty entry in document.RootElement.EnumerateObject())
				{
					if (entry.Value.ValueKind != JsonValueKind.Object)
					{
						continue;
					}

					foreach (string field in fields)
					{
						if (entry.Value.TryGetProperty(field, out JsonElement value) && value.ValueKind == JsonValueKind.String)
						{
							Check(path, entry.Name + "." + field, value.GetString());
						}
					}

					if (options && entry.Value.TryGetProperty("options", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
					{
						int i = 0;
						foreach (JsonElement option in list.EnumerateArray())
						{
							if (option.ValueKind == JsonValueKind.Object &&
								option.TryGetProperty("label", out JsonElement label) && label.ValueKind == JsonValueKind.String)
							{
								Check(path, $"{entry.Name}.options[{i}].label", label.GetString());
							}

							i++;
						}
					}

					// input.json's "touch": a pill's text, or { pill | label, caption }.
					if (path == ModInput.ModInputPath && entry.Value.TryGetProperty("touch", out JsonElement touch))
					{
						if (touch.ValueKind == JsonValueKind.String)
						{
							Check(path, entry.Name + ".touch", touch.GetString());
						}
						else if (touch.ValueKind == JsonValueKind.Object)
						{
							foreach (string part in new[] { "pill", "label", "caption" })
							{
								if (touch.TryGetProperty(part, out JsonElement said) && said.ValueKind == JsonValueKind.String)
								{
									Check(path, $"{entry.Name}.touch.{part}", said.GetString());
								}
							}
						}
					}
				}
			}
		}

		return issues;
	}

	/// <summary>A file's text with its byte-order mark kept, so the check sees what the loader reads.</summary>
	private static string ReadText(string file) => Encoding.UTF8.GetString(File.ReadAllBytes(file));
}
