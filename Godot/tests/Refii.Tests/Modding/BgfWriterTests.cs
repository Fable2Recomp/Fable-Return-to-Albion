namespace Refii.Tests.Modding;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Bnk;
using Refii.Core;
using Refii.Textures;
using Refii.Ui;

/// <summary>
/// The <c>.bgf</c> writer (Engine_BgfWriter) is the parser's exact inverse: every shipped
/// presentation in the player's banks, parsed and written back, is the same bytes; a synthetic
/// presentation built and edited through Engine_BgfEdit parses back to the document that was
/// written, in both endiannesses. No Godot type is built; the shipped files are read in place
/// from the install and nothing derived from them is written anywhere.
/// </summary>
[TestSuite]
public class BgfWriterTests
{
	/// <summary>The bank holding the presentations, found beside the tree's packaged install; null without one.</summary>
	private static string? ShippedBank()
	{
		string? env = Environment.GetEnvironmentVariable(Engine_GameData.EnvironmentVariable);
		if (!string.IsNullOrEmpty(env) && File.Exists(Path.Combine(env, "data", "guiscripts.bnk")))
		{
			return Path.Combine(env, "data", "guiscripts.bnk");
		}

		string? at = AppContext.BaseDirectory;
		for (int i = 0; i < 16 && at != null; i++)
		{
			string direct = Path.Combine(at, "assets", "game", "data", "guiscripts.bnk");
			if (File.Exists(direct))
			{
				return direct;
			}

			string packages = Path.Combine(at, "out", "package");
			if (Directory.Exists(packages))
			{
				foreach (string package in Directory.EnumerateDirectories(packages))
				{
					string candidate = Path.Combine(package, "assets", "game", "data", "guiscripts.bnk");
					if (File.Exists(candidate))
					{
						return candidate;
					}
				}
			}

			at = Path.GetDirectoryName(at);
		}

		return null;
	}

	/// <summary>Every presentation in the bank: its entry path and bytes.</summary>
	private static List<(string Path, byte[] Bytes)> ShippedPresentations(string bank)
	{
		var result = new List<(string, byte[])>();
		AssertThat(BnkArchive.TryOpen(bank, out BnkArchive? archive, out string error)).OverrideFailureMessage(Msg(error)).IsTrue();
		using (archive)
		{
			foreach (BnkEntry entry in archive!.Entries)
			{
				if (entry.RelativePath.EndsWith(".bgf", StringComparison.OrdinalIgnoreCase))
				{
					result.Add((entry.RelativePath, archive.ReadEntryBytes(entry)));
				}
			}
		}

		return result;
	}

	/// <summary>gdUnit4 refuses an empty override message, and a passing check has none.</summary>
	private static string Msg(string text) => text.Length == 0 ? "(no detail)" : text;

	private static int FirstDifference(byte[] a, byte[] b)
	{
		int n = Math.Min(a.Length, b.Length);
		for (int i = 0; i < n; i++)
		{
			if (a[i] != b[i])
			{
				return i;
			}
		}

		return a.Length == b.Length ? -1 : n;
	}

	private static string Hex(byte[] d, int at, int n) =>
		string.Join(" ", Enumerable.Range(at, n).Where(i => i >= 0 && i < d.Length).Select(i => d[i].ToString("X2")));

	[TestCase]
	public void EveryShippedPresentationWritesBackByteIdentical()
	{
		string? bank = ShippedBank();
		if (bank is null)
		{
			Console.WriteLine("[bgf] no packaged install beside the tree; the shipped round-trip is not run here");
			return;
		}

		List<(string Path, byte[] Bytes)> files = ShippedPresentations(bank);
		AssertThat(files.Count).OverrideFailureMessage($"no .bgf entries in {bank}").IsGreater(0);

		int identical = 0;
		int documentEqual = 0;
		var failed = new List<string>();
		var census = new StringBuilder();
		var recountMisses = new Dictionary<int, int>();
		foreach ((string path, byte[] bytes) in files)
		{
			Engine_BgfDocument doc = Engine_BgfDocument.Parse(bytes);
			string name = Path.GetFileName(path);

			// The manifest's words against what the content derives: a word that every file
			// reproduces is one Recount may state; one that does not is left as carried.
			uint[] stored = (uint[])doc.MemoryCounts.Clone();
			var recounted = Engine_BgfDocument.Parse(bytes);
			Engine_BgfWriter.Recount(recounted);
			for (int i = 0; i < stored.Length; i++)
			{
				if (stored[i] != recounted.MemoryCounts[i])
				{
					recountMisses[i] = recountMisses.TryGetValue(i, out int c) ? c + 1 : 1;
				}
			}

			census.Append(path).Append(": elements ").Append(doc.Elements.Length)
				.Append(" (components ").Append(doc.Elements.Count(e => e.IsComponent)).Append(")")
				.Append(" attrs ").Append(doc.Elements.Sum(e => e.Attributes.Length))
				.Append(" strings ").Append(doc.Strings.Length)
				.Append(" contracts ").Append(doc.Contracts.Length).Append('/').Append(doc.Contracts.Sum(c => c.NameIndices.Length))
				.Append(" anims ").Append(doc.Animations.Length).Append('/').Append(doc.Animations.Sum(a => a.Keys.Length))
				.Append(doc.AnimationRaw is null ? string.Empty : " RAW")
				.Append(" logic ").Append(doc.LogicSection.Length)
				.Append(" params ").Append(doc.ParamsSection.Length)
				.Append(" slides ").Append(doc.Slides.Length).Append('/').Append(doc.Slides.Sum(s => s.Entries.Length))
				.Append('/').Append(doc.Slides.Sum(s => s.Entries.Sum(e => e.CommandCount)))
				.Append('/').Append(doc.Slides.Sum(s => s.Entries.Sum(e => e.AnimationEvents.Length)))
				.Append(" strbytes ").Append(doc.Strings.Sum(s => Encoding.UTF8.GetByteCount(s)))
				.Append(" be ").Append(doc.BigEndian)
				.Append("\n  mem ").Append(string.Join(",", doc.MemoryCounts))
				.Append(" extra ").Append(string.Join(",", doc.MemoryExtra))
				.Append("\n  header ").Append(Hex(bytes, 0, 32))
				.Append("\n  root type ").Append(doc.Elements[0].Type).Append(" flags ").Append(doc.Elements[0].Flags)
				.Append(" pad ").Append(doc.Elements[0].ComponentPad).Append(" extra ").Append(doc.Elements[0].ComponentExtra)
				.Append(" objectId ").Append(doc.Elements[0].ObjectId)
				.Append(" attrs ").Append(string.Join(" ", doc.Elements[0].Attributes.Select(a => Engine_BgfAttributeNames.Describe(a.Id) + ":" + a.Type + "=" + a.RawValue)))
				.Append("\n  elementRefs ").Append(doc.Elements.Sum(e => e.Attributes.Count(a => a.Type == BgfValueType.ElementRef)))
				.Append(" (values<count ").Append(doc.Elements.Sum(e => e.Attributes.Count(a => a.Type == BgfValueType.ElementRef && a.RawValue < (uint)doc.Elements.Length))).Append(")")
				.Append(" highBits ").Append(doc.Elements.Sum(e => e.Attributes.Count(a => a.PackedHigh != 0)))
				.Append(" ids unique ").Append(doc.Elements.Select(e => e.ObjectId).Distinct().Count() == doc.Elements.Length)
				.Append(" idMax ").Append(doc.Elements.Max(e => e.ObjectId))
				.Append(" parentsBefore ").Append(doc.Elements.Select((e, i) => e.ParentOrdinal < i).All(b => b))
				.Append(" contiguous ").Append(Enumerable.Range(0, doc.Elements.Length).All(i => Contiguous(doc, i)))
				.Append(" componentFlags ").Append(string.Join(",", doc.Elements.Where(e => e.IsComponent).Select(e => e.Flags).Distinct()))
				.Append(" componentPads ").Append(string.Join(",", doc.Elements.Where(e => e.IsComponent).Select(e => e.ComponentPad).Distinct()))
				.Append(" componentExtras ").Append(string.Join(",", doc.Elements.Where(e => e.IsComponent).Select(e => e.ComponentExtra).Distinct().Take(6)))
				.Append(" eventFlags ").Append(string.Join(",", doc.Slides.SelectMany(s => s.Entries).SelectMany(e => e.AnimationEvents).Select(ev => ev.Flag).Distinct()))
				.Append(" slideFlags ").Append(string.Join(",", doc.Slides.Select(s => s.Flags).Distinct()))
				.Append('\n');

			if (!Engine_BgfWriter.TryWrite(doc, out byte[] written, out string error))
			{
				failed.Add($"{name}: {error}");
				continue;
			}

			int diff = FirstDifference(bytes, written);
			if (diff < 0)
			{
				++identical;
				continue;
			}

			Engine_BgfDocument back = Engine_BgfDocument.Parse(written);
			if (Engine_BgfWriter.Same(doc, back, out string difference))
			{
				++documentEqual;
				census.Append("  DOCUMENT-EQUAL ONLY: first byte difference at ").Append(diff)
					.Append(" (sizes ").Append(bytes.Length).Append(" vs ").Append(written.Length).Append(") shipped [")
					.Append(Hex(bytes, diff - 8, 24)).Append("] written [").Append(Hex(written, diff - 8, 24)).Append("]\n");
			}
			else
			{
				failed.Add($"{name}: byte difference at {diff}, and the document differs: {difference}");
			}
		}

		census.Append("recount misses by index: ")
			.Append(string.Join(", ", recountMisses.OrderBy(p => p.Key).Select(p => $"[{p.Key}] x{p.Value}")))
			.Append('\n');
		census.Append($"byte-identical {identical}, document-equal {documentEqual}, failed {failed.Count} of {files.Count}\n");
		foreach (string f in failed)
		{
			census.Append("  FAILED ").Append(f).Append('\n');
		}

		Console.WriteLine("[bgf] " + census.ToString().Replace("\n", "\n[bgf] "));
		string? scratch = Environment.GetEnvironmentVariable("REFII_BGF_CENSUS");
		if (!string.IsNullOrEmpty(scratch))
		{
			File.WriteAllText(scratch, census.ToString());
		}

		AssertThat(failed.Count).OverrideFailureMessage(Msg(string.Join("\n", failed))).IsEqual(0);
		AssertThat(identical + documentEqual).IsEqual(files.Count);

		// Byte-identical on every file: no field has slack, so a parse written back IS the file.
		AssertThat(identical).OverrideFailureMessage($"{documentEqual} file(s) round-trip document-equal but not byte-identical:\n{census}").IsEqual(files.Count);

		// Every manifest word Recount states is reproduced from every shipped file's content.
		AssertThat(recountMisses.Count).OverrideFailureMessage("Recount does not reproduce the shipped manifest: " +
			string.Join(", ", recountMisses.OrderBy(p => p.Key).Select(p => $"[{p.Key}] on {p.Value} file(s)"))).IsEqual(0);
	}

	/// <summary>Depth-first storage: an element's subtree is the contiguous run after it.</summary>
	private static bool Contiguous(Engine_BgfDocument doc, int ordinal)
	{
		int end = Engine_BgfEdit.SubtreeEnd(doc, ordinal);
		for (int i = end; i < doc.Elements.Length; i++)
		{
			for (int at = doc.Elements[i].ParentOrdinal; at >= 0; at = doc.Elements[at].ParentOrdinal)
			{
				if (at == ordinal)
				{
					return false;
				}
			}
		}

		return true;
	}

	[TestCase]
	public void ASyntheticPresentationEditedAndWrittenParsesBackEqual()
	{
		foreach (bool bigEndian in new[] { true, false })
		{
			Engine_BgfDocument doc = Engine_BgfEdit.NewDocument("Scene", 1280, 720, bigEndian);
			AssertThat(Engine_BgfWriter.TryWrite(doc, out byte[] empty, out string error)).OverrideFailureMessage(Msg(error)).IsTrue();
			AssertThat(Engine_BgfDocument.Parse(empty).Elements.Length).IsEqual(1);

			int layer = Engine_BgfEdit.AddChild(doc, 0, "Layer");
			int frame = Engine_BgfEdit.AddChild(doc, layer, "Frame", 10f, -20f);
			int label = Engine_BgfEdit.AddChild(doc, layer, "Label", 5f, 5f);
			int badge = Engine_BgfEdit.AddChild(doc, frame, "Badge");
			AssertThat(layer).IsEqual(1);
			AssertThat(frame).IsEqual(2);
			// Badge is Frame's child, so it sits inside Frame's subtree, before Label (Layer's later child).
			AssertThat(badge).IsEqual(3);
			AssertThat(Engine_BgfEdit.NameOf(doc, 4)).IsEqual("Label");
			AssertThat(doc.Elements[4].ParentOrdinal).IsEqual(layer);
			AssertThat(doc.Elements[3].ParentOrdinal).IsEqual(frame);

			// A slide naming Label and a track on it follow the shift when Frame's subtree grows.
			doc.Slides = new[]
			{
				new BgfSlide
				{
					Name = "Master Slide",
					OwnerElement = 0,
					Entries = new[]
					{
						new BgfSlideEntry { ElementOrdinal = 4, Active = true, Commands = Array.Empty<BgfAttribute>(), AnimationEvents = new[] { new BgfAnimationEvent(1, 0) } },
					},
				},
			};
			doc.Animations = new[]
			{
				new BgfAnimation { ElementOrdinal = 4, AttributeId = Engine_BgfEdit.PositionXId, ActiveOnLoad = true, Keys = new[] { new BgfAnimationKey(0, 5f, 0, 0, 0, 0), new BgfAnimationKey(400, 0f, 0, 0, 0, 0) } },
			};
			doc.Contracts = new[] { new BgfContract { ElementOrdinal = 4, NameIndices = new[] { Engine_BgfEdit.StringIndex(doc, "OnShow") } } };
			Engine_BgfWriter.Recount(doc);

			int ribbon = Engine_BgfEdit.AddChild(doc, frame, "Ribbon");
			AssertThat(ribbon).IsEqual(4);
			AssertThat(doc.Slides[0].Entries[0].ElementOrdinal).IsEqual(5);
			AssertThat(doc.Animations[0].ElementOrdinal).IsEqual(5);
			AssertThat(doc.Contracts[0].ElementOrdinal).IsEqual(5);
			AssertThat(Engine_BgfEdit.NameOf(doc, 5)).IsEqual("Label");

			Engine_BgfEdit.Rename(doc, 5, "Caption");
			Engine_BgfEdit.Move(doc, 5, 100f, 200f);
			AssertThat(Engine_BgfEdit.NameOf(doc, 5)).IsEqual("Caption");
			AssertThat(doc.Elements[5].NameHash).IsEqual(Engine_GameHash.Sdbm("Caption"));
			AssertThat(Engine_BgfEdit.PositionOf(doc, 5)).IsEqual((100f, 200f));

			// Cloning Frame (Frame, Badge, Ribbon) lands the copy right after it, parents inside the copy relocated.
			int copy = Engine_BgfEdit.Clone(doc, frame, "Frame2");
			AssertThat(copy).IsEqual(5);
			AssertThat(doc.Elements.Length).IsEqual(9);
			AssertThat(Engine_BgfEdit.NameOf(doc, 5)).IsEqual("Frame2");
			AssertThat(doc.Elements[5].ParentOrdinal).IsEqual(layer);
			AssertThat(doc.Elements[6].ParentOrdinal).IsEqual(5);
			AssertThat(doc.Elements[7].ParentOrdinal).IsEqual(5);
			AssertThat(Engine_BgfEdit.NameOf(doc, 6)).IsEqual("Badge");
			AssertThat(Engine_BgfEdit.NameOf(doc, 8)).IsEqual("Caption");
			AssertThat(doc.Slides[0].Entries[0].ElementOrdinal).IsEqual(8);
			AssertThat(doc.Elements.Select(e => e.ObjectId).Distinct().Count()).IsEqual(9);

			AssertThat(doc.MemoryCounts[0]).IsEqual(8u);
			AssertThat(doc.MemoryCounts[1]).IsEqual(1u);
			AssertThat(doc.MemoryCounts[6]).IsEqual((uint)doc.Strings.Length);
			AssertThat(doc.MemoryCounts[14]).IsEqual(1u);

			AssertThat(Engine_BgfWriter.TryWrite(doc, out byte[] bytes, out error)).OverrideFailureMessage(Msg(error)).IsTrue();
			AssertThat(bytes[0]).IsEqual((byte)(bigEndian ? 1 : 0));
			Engine_BgfDocument back = Engine_BgfDocument.Parse(bytes);
			AssertThat(Engine_BgfWriter.Same(doc, back, out string difference)).OverrideFailureMessage(Msg(difference)).IsTrue();
			AssertThat(back.BigEndian).IsEqual(bigEndian);
			AssertThat(back.CanvasWidth).IsEqual(1280);
			AssertThat(back.Elements.Length).IsEqual(9);
			AssertThat(back.GetName(back.Elements[8])).IsEqual("Caption");
			AssertThat(back.Animations[0].Keys[1].Time).IsEqual(400f);
			AssertThat(back.Slides[0].Entries[0].ActivatedAnimations![0]).IsEqual(0);
			AssertThat(back.Contracts[0].Names[0]).IsEqual("OnShow");

			// The second write of the parse is the same bytes: nothing in the format has slack.
			AssertThat(Engine_BgfWriter.Write(back)).IsEqual(bytes);
		}
	}

	[TestCase]
	public void AWriteThatWouldNotReadBackIsRefused()
	{
		Engine_BgfDocument doc = Engine_BgfEdit.NewDocument("Scene", 960, 720, true);
		// A string index past the table is carried as the index (the parse resolves the name to
		// empty but keeps the index), so the document comes back equal and writes.
		doc.Contracts = new[] { new BgfContract { ElementOrdinal = 0, NameIndices = new[] { 7 } } };
		AssertThat(Engine_BgfWriter.TryWrite(doc, out byte[] bytes, out string error)).IsTrue();
		AssertThat(bytes.Length).IsGreater(32);
		AssertThat(Engine_BgfDocument.Parse(bytes).Contracts[0].Names[0]).IsEqual(string.Empty);

		// A version the parser rejects does not come back as bytes: the writer reads its own
		// output with the shipping reader and says why it refused.
		doc.Version = 2;
		AssertThat(Engine_BgfWriter.TryWrite(doc, out bytes, out error)).IsFalse();
		AssertThat(bytes.Length).IsEqual(0);
		AssertThat(error).Contains("unsupported version");
	}

	/// <summary>Every entry of the bank with the extension: its path and bytes.</summary>
	private static List<(string Path, byte[] Bytes)> ShippedEntries(string bank, string extension)
	{
		var result = new List<(string, byte[])>();
		AssertThat(BnkArchive.TryOpen(bank, out BnkArchive? archive, out string error)).OverrideFailureMessage(Msg(error)).IsTrue();
		using (archive)
		{
			foreach (BnkEntry entry in archive!.Entries)
			{
				if (entry.RelativePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
				{
					result.Add((entry.RelativePath, archive.ReadEntryBytes(entry)));
				}
			}
		}

		return result;
	}

	[TestCase]
	public void EveryShippedSidecarWritesBackByteIdentical()
	{
		string? bank = ShippedBank();
		if (bank is null)
		{
			Console.WriteLine("[bgf] no packaged install beside the tree; the shipped sidecar round-trip is not run here");
			return;
		}

		var report = new StringBuilder();
		var failed = new List<string>();

		// .fac — text; a parse written back is its lines as read.
		List<(string Path, byte[] Bytes)> facs = ShippedEntries(bank, ".fac");
		int facIdentical = 0;
		int facDocumentEqual = 0;
		foreach ((string path, byte[] bytes) in facs)
		{
			Engine_FacDocument doc = Engine_FacDocument.Parse(bytes);
			if (!Engine_FacWriter.TryWrite(doc, out byte[] written, out string error))
			{
				failed.Add($"{path}: {error}");
				continue;
			}

			int diff = FirstDifference(bytes, written);
			if (diff < 0)
			{
				++facIdentical;
			}
			else if (Engine_FacWriter.Same(doc, Engine_FacDocument.Parse(written), out string difference))
			{
				++facDocumentEqual;
				report.Append($"  {path}: document-equal only, first byte difference at {diff} ({bytes.Length} vs {written.Length})\n");
			}
			else
			{
				failed.Add($"{path}: byte difference at {diff} and {difference}");
			}
		}

		// .bsg — binary; every node written from its fields over its carried bytes.
		List<(string Path, byte[] Bytes)> bsgs = ShippedEntries(bank, ".bsg");
		int bsgIdentical = 0;
		int bsgDocumentEqual = 0;
		int positionalJoins = 0;
		int positionalMisses = 0;
		var bgfByStem = ShippedPresentations(bank).ToDictionary(f => Path.ChangeExtension(f.Path, null), f => f.Bytes, StringComparer.OrdinalIgnoreCase);
		foreach ((string path, byte[] bytes) in bsgs)
		{
			Engine_BsgDocument doc = Engine_BsgDocument.Parse(bytes);
			if (!Engine_BsgWriter.TryWrite(doc, out byte[] written, out string error))
			{
				failed.Add($"{path}: {error}");
				continue;
			}

			int diff = FirstDifference(bytes, written);
			if (diff < 0)
			{
				++bsgIdentical;
			}
			else if (Engine_BsgWriter.Same(doc, Engine_BsgDocument.Parse(written), out string difference))
			{
				++bsgDocumentEqual;
				report.Append($"  {path}: document-equal only, first byte difference at {diff}\n");
			}
			else
			{
				failed.Add($"{path}: byte difference at {diff} and {difference}");
			}

			// The positional join the edits rely on: the k-th spatial element's node carries that
			// element's object id and its parent's node index.
			if (bgfByStem.TryGetValue(Path.ChangeExtension(path, null), out byte[]? bgfBytes))
			{
				Engine_BgfDocument bgf = Engine_BgfDocument.Parse(bgfBytes);
				for (int i = 0; i < bgf.Elements.Length; i++)
				{
					if (!Engine_UiScreen.IsSpatial(bgf.Elements[i].Type))
					{
						continue;
					}

					int k = Engine_BgfEdit.SpatialIndex(bgf, i);
					int parent = bgf.Elements[i].ParentOrdinal;
					int expectedParent = parent < 0 ? -1 : Engine_BgfEdit.SpatialIndex(bgf, parent);
					if (k < doc.Nodes.Length && doc.Nodes[k].ObjectId == bgf.Elements[i].ObjectId && doc.Nodes[k].ParentIndex == expectedParent)
					{
						++positionalJoins;
					}
					else
					{
						++positionalMisses;
					}
				}
			}
		}

		report.Append($".fac: byte-identical {facIdentical}, document-equal {facDocumentEqual}, failed {failed.Count(f => f.Contains(".fac"))} of {facs.Count}\n");
		report.Append($".bsg: byte-identical {bsgIdentical}, document-equal {bsgDocumentEqual}, failed {failed.Count(f => f.Contains(".bsg"))} of {bsgs.Count}\n");
		report.Append($"positional .bsg joins: {positionalJoins} elements agree (object id and parent node), {positionalMisses} do not\n");
		foreach (string f in failed)
		{
			report.Append("  FAILED ").Append(f).Append('\n');
		}

		Console.WriteLine("[bgf] " + report.ToString().Replace("\n", "\n[bgf] "));
		string? scratch = Environment.GetEnvironmentVariable("REFII_BGF_CENSUS");
		if (!string.IsNullOrEmpty(scratch))
		{
			File.AppendAllText(scratch, report.ToString());
		}

		AssertThat(facs.Count).IsGreater(0);
		AssertThat(bsgs.Count).IsGreater(0);
		AssertThat(failed.Count).OverrideFailureMessage(Msg(string.Join("\n", failed))).IsEqual(0);
		AssertThat(facIdentical).OverrideFailureMessage(Msg(report.ToString())).IsEqual(facs.Count);
		AssertThat(bsgIdentical).OverrideFailureMessage(Msg(report.ToString())).IsEqual(bsgs.Count);
		AssertThat(positionalMisses).OverrideFailureMessage(Msg(report.ToString())).IsEqual(0);
	}

	[TestCase]
	public void ACloneAndANewQuadCarryTheirArtThroughTheSidecars()
	{
		foreach (bool bigEndian in new[] { true, false })
		{
			Engine_BgfDocument doc = Engine_BgfEdit.NewDocument("Scene", 1280, 720, bigEndian);
			Engine_FacDocument fac = Engine_BgfEdit.NewSidecar();
			Engine_BsgDocument bsg = Engine_BgfEdit.NewSceneGraph(bigEndian);

			int layer = Engine_BgfEdit.AddChild(doc, 0, "Layer", 0f, 0f, bsg);
			int badge = Engine_BgfEdit.AddQuad(doc, fac, bsg, layer, "Badge", 10f, -20f, 64f, 32f, "art/gui/mymod/badge.tex", 0.25f, 0f, 0.5f, 1f);
			AssertThat(bsg.Nodes.Length).IsEqual(3);
			AssertThat(bsg.Nodes[1].ParentIndex).IsEqual(0);
			AssertThat(bsg.Nodes[2].ParentIndex).IsEqual(1);
			AssertThat(bsg.Nodes[2].NodeType).IsEqual((byte)2);
			AssertThat(bsg.Nodes[2].ObjectId).IsEqual(doc.Elements[badge].ObjectId);
			AssertThat(fac.Uv.Count).IsEqual(1);
			AssertThat(fac.Uv[0].StableId).IsEqual((int)doc.Elements[badge].ObjectId);
			AssertThat(fac.Uv[0].TextureHash).IsEqual(Engine_GameHash.ResourcePath("art/gui/mymod/badge.tex"));
			AssertThat(fac.Uv[0].UMin).IsEqual(0.25f);
			AssertThat(doc.Elements[badge].TryGetAttribute("scale.x", out BgfAttribute sx) && sx.AsFloat() == 0.64f).IsTrue();

			// A text element with a TEXT record, then the layer cloned: the copy's quad and text
			// are re-keyed to the copies' ids, and its scene nodes sit after the source's.
			int caption = Engine_BgfEdit.AddChild(doc, layer, "Caption", 0f, 0f, bsg);
			doc.Elements[caption].Type = BgfElementType.Text;
			fac.Add(new FacTextRecord { StableId = (int)doc.Elements[caption].ObjectId, Text = "It's \"quoted\" — ok", Size = 18, BoxWidth = 0.35f, BoxHeight = 0.25f, HorzAlign = 1, R = 1f, G = 1f, B = 0.188235f });
			bsg.Assets = new[] { new BsgAsset(doc.Elements[badge].ObjectId, 7) };

			int copy = Engine_BgfEdit.Clone(doc, layer, "Layer2", fac, bsg);
			AssertThat(copy).IsEqual(4);
			AssertThat(doc.Elements.Length).IsEqual(7);
			AssertThat(bsg.Nodes.Length).IsEqual(7);
			AssertThat(bsg.Nodes[4].ParentIndex).IsEqual(0);
			AssertThat(bsg.Nodes[5].ParentIndex).IsEqual(4);
			AssertThat(bsg.Nodes[6].ParentIndex).IsEqual(4);
			AssertThat(bsg.Nodes[5].NodeType).IsEqual((byte)2);
			AssertThat(bsg.Nodes[5].ObjectId).IsEqual(doc.Elements[5].ObjectId);
			AssertThat(fac.Uv.Count).IsEqual(2);
			AssertThat(fac.Uv[1].StableId).IsEqual((int)doc.Elements[5].ObjectId);
			AssertThat(fac.Uv[1].TextureHash).IsEqual(fac.Uv[0].TextureHash);
			AssertThat(fac.Text.Count).IsEqual(2);
			AssertThat(fac.Text[1].StableId).IsEqual((int)doc.Elements[6].ObjectId);
			AssertThat(bsg.Assets.Length).IsEqual(2);
			AssertThat(bsg.Assets[1].ObjectId).IsEqual(doc.Elements[5].ObjectId);

			// Each file writes, reads back equal, and writes the same bytes again.
			AssertThat(Engine_BgfWriter.TryWrite(doc, out byte[] bgfBytes, out string error)).OverrideFailureMessage(Msg(error)).IsTrue();
			AssertThat(Engine_FacWriter.TryWrite(fac, out byte[] facBytes, out error)).OverrideFailureMessage(Msg(error)).IsTrue();
			AssertThat(Engine_BsgWriter.TryWrite(bsg, out byte[] bsgBytes, out error)).OverrideFailureMessage(Msg(error)).IsTrue();
			AssertThat(Engine_FacWriter.Write(Engine_FacDocument.Parse(facBytes))).IsEqual(facBytes);
			AssertThat(Engine_BsgWriter.Write(Engine_BsgDocument.Parse(bsgBytes))).IsEqual(bsgBytes);

			Engine_FacDocument facBack = Engine_FacDocument.Parse(facBytes);
			AssertThat(facBack.Text[0].Text).IsEqual("It's \"quoted\" — ok");
			AssertThat(Encoding.ASCII.GetString(facBytes)).Contains("%27");
			AssertThat(Encoding.ASCII.GetString(facBytes)).StartsWith("UV\r\n");
			AssertThat(Encoding.ASCII.GetString(facBytes)).EndsWith("\r\n\r\n");

			// The screen stands on the three files: the quad resolves its texture and rectangle
			// from the mod's path, on the source and on the copy.
			var names = Engine_TextureNameTable.FromPaths(new[] { "art/gui/mymod/badge.tex" });
			Engine_UiScreen screen = Engine_UiScreen.Parse("synthetic", bgfBytes, facBytes, bsgBytes, names);
			AssertThat(screen.Nodes[badge].Quad).IsNotNull();
			AssertThat(screen.Nodes[badge].Quad!.TexturePath.Replace('\\', '/')).IsEqual("art/gui/mymod/badge.tex");
			AssertThat(screen.Nodes[5].Quad).IsNotNull();
			AssertThat(screen.Nodes[5].Quad!.UMax).IsEqual(0.75f);
			AssertThat(screen.Nodes[6].Text).IsNotNull();
			AssertThat(screen.UnmatchedFacRecords).IsEqual(0);
		}
	}
}
