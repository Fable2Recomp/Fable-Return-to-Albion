using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Refii.Bnk;
using Refii.Core;
using Refii.Models;

namespace Refii.Launcher.Tools;

/// <summary>
/// Measures the material record across the WHOLE asset set.
///
/// It exists because the scalar block in front of each material's texture sets used to be
/// skipped as an opaque 45 bytes, and reading it instead is the kind of change that either works
/// everywhere or desynchronises every material after the first. A misaligned 4-byte read does not
/// fail — it returns a float, and a float assembled from the wrong bytes is a denormal or a
/// 10^30. So the check that matters is not "did it parse" but "are the numbers plausible":
/// a specular exponent lives in single digits to low hundreds, and anything outside that says the
/// split is wrong however green the parse rate looks.
///
/// It also counts which of the nine string slots the shipped data actually fills, because three
/// of them have no proven meaning and the honest way to weigh that is to find out how many
/// materials use them at all.
/// </summary>
public partial class MaterialSurvey : Node
{
	/// <summary>0 surveys everything; a positive value caps the sample for a quick pass.</summary>
	[Export] public int Limit { get; set; }

	public override void _Ready()
	{
		string dataRoot = Engine_GameData.Resolve();
		if (dataRoot.Length == 0)
		{
			GD.Print("no game data found; set REFII_GAME_DATA or stage assets/Fable2/game.");
			GetTree().Quit(1);
			return;
		}

		GD.Print($"[mat] mounting {dataRoot}");
		using BnkFileSystem filesystem = BnkFileSystem.Mount(dataRoot);

		List<BnkFile> models = filesystem.EnumerateAllFiles()
			.Where(f => f.VirtualPath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
			.OrderBy(f => f.VirtualPath, StringComparer.OrdinalIgnoreCase)
			.ToList();

		if (Limit > 0 && models.Count > Limit)
		{
			models = models.Take(Limit).ToList();
		}

		GD.Print($"[mat] surveying {models.Count:N0} models");

		int parsed = 0;
		int unreadable = 0;
		int legacy = 0;
		long materials = 0;

		// The nine strings, in the order the file stores them.
		string[] slotNames =
		{
			"1 diffuse s0", "2 specular s1", "3 normal s2", "4 envmap s14", "5 selfillum s13",
			"6 secondary diffuse", "7 absent=trunk", "8 discarded", "9 discarded",
		};
		var slotUsed = new long[9];

		long specSane = 0;
		long specInsane = 0;
		long specDefault = 0;
		float specMinLow = float.MaxValue, specMinHigh = float.MinValue;
		float specMaxLow = float.MaxValue, specMaxHigh = float.MinValue;
		long selfIllumNonZero = 0;
		float selfIllumHigh = float.MinValue;
		float selfIllumLow = float.MaxValue;
		var selfIllumValues = new HashSet<float>();
		var specPairs = new HashSet<string>();
		var examples = new List<string>();

		foreach (BnkFile model in models)
		{
			byte[] bytes;
			try
			{
				bytes = model.ReadAllBytes();
			}
			catch (Exception)
			{
				++unreadable;
				continue;
			}

			if (!Engine_MdlData.TryParseInfo(bytes, model.VirtualPath, out MdlInfo info, out _))
			{
				continue;
			}

			++parsed;

			// WHICH PARSER ANSWERED. Engine_MdlData.TryParseInfo tries the engine format first and
			// falls back to a legacy scanner that reads the same nine strings but SKIPS the scalar
			// block — so a material from that path keeps the constructor's (1, 20) and 0.0 rather
			// than the file's. Without this split, "362 records at the engine default" cannot be
			// told apart from "362 records the fallback never filled in".
			if (!info.IsEngineFormat)
			{
				++legacy;
			}

			foreach (MdlMesh mesh in info.Meshes)
			{
				foreach (MdlMaterial m in mesh.Materials)
				{
					++materials;

					string[] slots =
					{
						m.Diffuse, m.Specular, m.Normal, m.EnvironmentMap, m.SelfIllumination,
						m.ShaderParams.Length > 0 ? m.ShaderParams[0] : string.Empty,
						m.ShaderParams.Length > 1 ? m.ShaderParams[1] : string.Empty,
						m.ShaderParams.Length > 2 ? m.ShaderParams[2] : string.Empty,
						m.ShaderParams.Length > 3 ? m.ShaderParams[3] : string.Empty,
					};

					for (int i = 0; i < 9; i++)
					{
						if (!string.IsNullOrWhiteSpace(slots[i]))
						{
							++slotUsed[i];
						}
					}

					// A specular exponent is a small positive number. Denormals, negatives and
					// anything past a few thousand mean the bytes did not come from where we
					// think they did.
					float lo = m.SpecularPowerMin;
					float hi = m.SpecularPowerMax;
					bool sane = float.IsFinite(lo) && float.IsFinite(hi) &&
						lo >= 0f && lo <= 4096f && hi >= 0f && hi <= 4096f && hi >= lo;

					if (sane)
					{
						++specSane;
						specMinLow = MathF.Min(specMinLow, lo);
						specMinHigh = MathF.Max(specMinHigh, lo);
						specMaxLow = MathF.Min(specMaxLow, hi);
						specMaxHigh = MathF.Max(specMaxHigh, hi);
						if (lo == 1.0f && hi == 20.0f)
						{
							++specDefault;
						}
					}
					else
					{
						++specInsane;
						if (examples.Count < 6)
						{
							examples.Add($"{model.VirtualPath}: min={lo:G6} max={hi:G6}");
						}
					}

					specPairs.Add($"({lo:G6}, {hi:G6})");
					selfIllumValues.Add(m.SelfIlluminationScale);
					selfIllumLow = MathF.Min(selfIllumLow, m.SelfIlluminationScale);
					if (m.SelfIlluminationScale != 0f)
					{
						++selfIllumNonZero;
						selfIllumHigh = MathF.Max(selfIllumHigh, m.SelfIlluminationScale);
					}
				}
			}
		}

		GD.Print($"[mat] parsed {parsed:N0} models, {unreadable:N0} unreadable, " +
			$"{materials:N0} material records ({legacy:N0} via the legacy fallback)");
		GD.Print("[mat] --- texture slot occupancy ---");
		for (int i = 0; i < 9; i++)
		{
			double pct = materials == 0 ? 0 : 100.0 * slotUsed[i] / materials;
			GD.Print($"[mat]   slot {slotNames[i],-20} {slotUsed[i],8:N0}  {pct,6:F2}%");
		}

		GD.Print("[mat] --- specular power range (scalar block offsets 0 and 4) ---");
		GD.Print($"[mat]   plausible {specSane:N0}   IMPLAUSIBLE {specInsane:N0}");
		if (specSane > 0)
		{
			GD.Print($"[mat]   min in [{specMinLow:G6}, {specMinHigh:G6}]  " +
				$"max in [{specMaxLow:G6}, {specMaxHigh:G6}]");
			GD.Print($"[mat]   at the engine default (1, 20): {specDefault:N0}");
		}

		foreach (string example in examples)
		{
			GD.Print($"[mat]   BAD {example}");
		}

		GD.Print("[mat] --- self-illumination scale (offset 29) ---");
		GD.Print($"[mat]   non-zero {selfIllumNonZero:N0}, " +
			$"range [{selfIllumLow:G6}, {selfIllumHigh:G6}], {selfIllumValues.Count} distinct");

		// A FIELD THAT NEVER VARIES IS A FIELD WORTH DOUBTING. If every record in the game reports
		// the same pair, the honest readings are "the exporter writes a constant" and "these bytes
		// are not what we think they are", and the two are told apart by how many distinct values
		// the neighbouring fields carry — a genuinely constant field sits beside varying ones.
		GD.Print($"[mat]   distinct specular pairs: {specPairs.Count}");
		foreach (string pair in specPairs.OrderBy(p => p).Take(8))
		{
			GD.Print($"[mat]     {pair}");
		}

		GetTree().Quit(0);
	}
}
