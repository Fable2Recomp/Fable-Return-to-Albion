namespace Refii.Tests.Modding;

using System.Collections.Generic;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Fx;

/// <summary>
/// The level-load texture prefetch's contract (Engine_FxTexturePrefetch): which names the bank
/// makes it decode, and the once-only hand-off to the main thread's loader. No Godot type is
/// built; the store is exercised with strings standing in for decoded images.
/// </summary>
[TestSuite]
public class FxTexturePrefetchTests
{
	private static FxBank BankNaming(params string[] textures)
	{
		var bank = new FxBank();
		foreach (string texture in textures)
		{
			var system = new FxSystemDef();
			system.Visuals.Add(new FxVisual { HasTexture = texture.Length != 0, Texture = texture });
			bank.Systems.Add(system);
		}

		return bank;
	}

	[TestCase]
	public void NamesAreTheVisualsTexturesEachOnceInFirstSeenOrder()
	{
		FxBank bank = BankNaming(
			@"art\fx\pfx_textures\fxt_flash_01.tex",
			"",
			@"art\fx\pfx_textures\fxt_ribbon_1.tex",
			@"ART\FX\PFX_TEXTURES\FXT_FLASH_01.TEX");
		bank.Systems[1].Visuals.Add(new FxVisual { HasMesh = true, MeshPath = "x.mdl" });

		List<string> names = Engine_FxTexturePrefetch.Names(bank);

		AssertArray(names).ContainsExactly(
			@"art\fx\pfx_textures\fxt_flash_01.tex",
			@"art\fx\pfx_textures\fxt_ribbon_1.tex");
	}

	[TestCase]
	public void AnOfferedItemIsTakenExactlyOnce()
	{
		var store = new Engine_FxPrefetchStore<string>();

		AssertBool(store.Wanted("a.tex")).IsTrue();
		AssertBool(store.Offer("a.tex", "decoded")).IsTrue();
		AssertBool(store.Wanted("a.tex")).IsFalse();
		AssertInt(store.Pending).IsEqual(1);

		AssertBool(store.TryTake("A.TEX", out string taken)).IsTrue();
		AssertString(taken).IsEqual("decoded");
		AssertInt(store.Pending).IsEqual(0);
		AssertBool(store.TryTake("a.tex", out _)).IsFalse();

		// Taken means loaded: a late decode of the same name is dropped.
		AssertBool(store.Wanted("a.tex")).IsFalse();
		AssertBool(store.Offer("a.tex", "again")).IsFalse();
		AssertInt(store.Pending).IsEqual(0);
	}

	[TestCase]
	public void TheMainThreadLoadingFirstDropsThePrefetchedCopy()
	{
		var store = new Engine_FxPrefetchStore<string>();

		// Loaded before the prefetch reached it: the offer is refused.
		store.MarkLoaded("early.tex");
		AssertBool(store.Wanted("early.tex")).IsFalse();
		AssertBool(store.Offer("early.tex", "decoded")).IsFalse();
		AssertBool(store.TryTake("early.tex", out _)).IsFalse();

		// Loaded after the prefetch held it: the held copy is released.
		AssertBool(store.Offer("late.tex", "decoded")).IsTrue();
		store.MarkLoaded("late.tex");
		AssertInt(store.Pending).IsEqual(0);
		AssertBool(store.TryTake("late.tex", out _)).IsFalse();
	}

	[TestCase]
	public void AnUnknownNameIsNotTaken()
	{
		var store = new Engine_FxPrefetchStore<string>();
		AssertBool(store.TryTake("nothing.tex", out string item)).IsFalse();
		AssertObject(item).IsNull();
		AssertInt(store.Pending).IsEqual(0);
	}
}
