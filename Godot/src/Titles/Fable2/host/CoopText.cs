using System;
using System.Collections.Generic;
using Godot;
using Refii.Bnk;
using Refii.Core;
using Refii.Text;

namespace Refii.Titles.Fable2;

using Refii.Host;

/// <summary>
/// Co-op without a henchman, in words: player two is a Hero in their own right (Fable 3), so the
/// English text that calls them "the Henchman" calls them the partner, or Player Two where a
/// score list sets the two players side by side.
/// </summary>
/// <remarks>
/// <para>COMPUTED ON THE PLAYER'S MACHINE from their own English <c>book.babel</c>, never shipped:
/// each string that names the henchman is rewritten word by word (<see cref="Relabel"/>) and laid
/// over the table the way a mod's text rows are. The keys are the English bank's every string that
/// names the henchman, and every one is a co-op string (the pub and colosseum score lists, the gold
/// gift, the DLC "in spirit" notices, the co-op tutorials).</para>
/// <para>Other languages keep their own wording; the two tutorials that explain the henchman are
/// retired for every language in the core Lua (<see cref="CoopHeroLua"/>).</para>
/// <para>Applied after the mods' rows, over the composed table.</para>
/// </remarks>
public static class CoopText
{
	/// <summary>The English strings that name the henchman, by key (their text is read, not kept).</summary>
	private static readonly uint[] HenchmanKeys =
	{
		0x012C32D2u, 0x01B3DF74u, 0x09F27452u, 0x1323B7EEu, 0x5721371Fu, 0x58B38C94u, 0x60A912F4u,
		0x68031991u, 0xB73BD844u, 0xB84D10E4u, 0xBA9F68A8u, 0xCF01CC0Bu, 0xCF964874u, 0xE0ACE00Du,
		0xE531CCCBu, 0xED052E2Du, 0xED071A30u, 0xEFEC3838u, 0xF4716ADBu, 0xFEAE6758u,
	};

	/// <summary>Raise when the rewritten table changes in a way the build stamp would not show.</summary>
	private const int CacheFormat = 1;

	public static void Attach(BnkFileSystem mounted)
	{
		int changed = 0;
		foreach (string language in new List<string>(mounted.EnumerateDirectories("language")))
		{
			if (!language.StartsWith("en", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			string table = $"language/{language}/text/book.babel";
			if (!mounted.TryGetFile(table, out BnkFile file))
			{
				continue;
			}

			try
			{
				byte[] original = file.ReadAllBytes();
				// KEPT ON DISK: writing the table back deflates all of its blocks, about 0.4 s of
				// every start on a phone, for the same few strings over the same file.
				byte[] cacheKey = Engine_KeyedFileCache.Key(CacheFormat, typeof(CoopText), new[] { original });
				string cache = System.IO.Path.Combine(Engine_Title.Fable2.CacheDirectory, "coop-text", language + ".bin");
				if (Engine_KeyedFileCache.TryRead(cache, cacheKey, out byte[] kept))
				{
					mounted.AddVirtualFile(table, $"<refii-coop:{table}>", kept.LongLength, () => kept, "Refii");
					changed++;
					continue;
				}

				Engine_BabelDatabase database = Engine_BabelDatabase.Parse(original);
				var rows = new List<BabelOverlayRow>();
				foreach (uint key in HenchmanKeys)
				{
					if (!database.TryGetString(key, out string value))
					{
						continue;
					}

					string relabelled = Relabel(value);
					if (relabelled != value)
					{
						rows.Add(new BabelOverlayRow { Key = $"0x{key:X8}", Value = relabelled, Mod = "Refii" });
					}
				}

				BabelOverlayResult result = BabelOverlay.Apply(original, rows);
				foreach (string error in result.Errors)
				{
					GD.PushWarning($"[coop] henchman wording: {table}: {error}");
				}

				if (result.Applied == 0)
				{
					continue;
				}

				byte[] bytes = result.Bytes;
				Engine_KeyedFileCache.Write(cache, cacheKey, bytes);
				mounted.AddVirtualFile(table, $"<refii-coop:{table}>", bytes.LongLength, () => bytes, "Refii");
				changed += result.Applied;
			}
			catch (Exception exception)
			{
				GD.PushWarning($"[coop] henchman wording: '{table}' could not be read: {exception.Message}");
			}
		}

		if (changed != 0)
		{
			Refii.Gdb.Engine_BnkTextBankLoader.Invalidate();
		}
	}

	/// <summary>A string's henchman wording as the partner's; unchanged when it names none.</summary>
	public static string Relabel(string text)
	{
		if (text.IndexOf("enchman", StringComparison.Ordinal) < 0)
		{
			return text;
		}

		// A score list sets the two side by side: "Hero's …" and "Henchman's …".
		if (text.Contains("Henchman's", StringComparison.Ordinal))
		{
			text = text.Replace("Hero's ", "Player One's ", StringComparison.Ordinal)
				.Replace("Henchman's", "Player Two's", StringComparison.Ordinal);
		}

		return text
			.Replace("Co-op Henchman", "Co-op Partner", StringComparison.Ordinal)
			.Replace("Hero's Henchman", "Hero's partner", StringComparison.Ordinal)
			.Replace("The Henchman", "Your partner", StringComparison.Ordinal)
			.Replace("the Henchman", "your partner", StringComparison.Ordinal)
			.Replace("your Henchman", "your partner", StringComparison.Ordinal)
			.Replace("your henchman", "your partner", StringComparison.Ordinal)
			.Replace("the henchman", "your partner", StringComparison.Ordinal)
			.Replace("Henchman", "Player Two", StringComparison.Ordinal)
			.Replace("henchman", "partner", StringComparison.Ordinal);
	}
}
