namespace Refii.Tests;

using System;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Ui;

/// <summary>
/// The identity-keyed cache behind the pattern-book catalogues and the item display names:
/// an entry serves only while the instances it was built from are the ones asked with.
/// </summary>
[TestSuite]
public class UiIdentityCacheTests
{
	private sealed class Tree { }

	private sealed class Fonts { }

	[TestCase]
	public void TestMissOnEmpty()
	{
		var cache = new Engine_UiIdentityCache<string, string>();
		AssertBool(cache.TryGet("abilitiesscreen", out _, new Tree(), new Fonts())).IsFalse();
		AssertInt(cache.Count).IsEqual(0);
	}

	[TestCase]
	public void TestHitWhileBuiltFromTheSameInstances()
	{
		var tree = new Tree();
		var fonts = new Fonts();
		var cache = new Engine_UiIdentityCache<string, string>();
		cache.Put("abilitiesscreen", "catalogue", tree, fonts);

		AssertBool(cache.TryGet("abilitiesscreen", out string value, tree, fonts)).IsTrue();
		AssertString(value).IsEqual("catalogue");
		AssertInt(cache.Count).IsEqual(1);
	}

	[TestCase]
	public void TestMissAndForgetWhenAnInstanceChanged()
	{
		var tree = new Tree();
		var fonts = new Fonts();
		var cache = new Engine_UiIdentityCache<string, string>();
		cache.Put("abilitiesscreen", "catalogue", tree, fonts);

		// The tree was rebuilt on a remount: same name, a new node.
		AssertBool(cache.TryGet("abilitiesscreen", out _, new Tree(), fonts)).IsFalse();
		AssertInt(cache.Count).IsEqual(0);

		// And the fonts alone changing is a miss too.
		cache.Put("abilitiesscreen", "catalogue", tree, fonts);
		AssertBool(cache.TryGet("abilitiesscreen", out _, tree, new Fonts())).IsFalse();
	}

	[TestCase]
	public void TestNullIdentitiesCompareByReferenceToo()
	{
		var gdb = new Tree();
		var cache = new Engine_UiIdentityCache<uint, string>();

		// Built while the text bank was not loaded: good only while it is still not loaded.
		cache.Put(0x2FDDF213u, "Toy Gun", gdb, null);
		AssertBool(cache.TryGet(0x2FDDF213u, out string value, gdb, null)).IsTrue();
		AssertString(value).IsEqual("Toy Gun");
		AssertBool(cache.TryGet(0x2FDDF213u, out _, gdb, new Fonts())).IsFalse();
	}

	[TestCase]
	public void TestADifferentNumberOfIdentitiesIsAMiss()
	{
		var tree = new Tree();
		var cache = new Engine_UiIdentityCache<string, string>();
		cache.Put("hud", "catalogue", tree);
		AssertBool(cache.TryGet("hud", out _, tree, null)).IsFalse();
	}

	[TestCase]
	public void TestPutReplacesAndRemoveForgets()
	{
		var tree = new Tree();
		var cache = new Engine_UiIdentityCache<string, string>();
		cache.Put("hud", "first", tree);
		cache.Put("hud", "second", tree);
		AssertBool(cache.TryGet("hud", out string value, tree)).IsTrue();
		AssertString(value).IsEqual("second");
		AssertInt(cache.Count).IsEqual(1);

		AssertBool(cache.Remove("hud")).IsTrue();
		AssertBool(cache.TryGet("hud", out _, tree)).IsFalse();
		AssertBool(cache.Remove("hud")).IsFalse();
	}

	[TestCase]
	public void TestPutCopiesTheIdentityArray()
	{
		var tree = new Tree();
		var fonts = new Fonts();
		object?[] builtFrom = { tree, fonts };
		var cache = new Engine_UiIdentityCache<string, string>();
		cache.Put("hud", "catalogue", builtFrom);

		// Mutating the caller's array after the put must not change what the entry was built from.
		builtFrom[0] = new Tree();
		AssertBool(cache.TryGet("hud", out _, tree, fonts)).IsTrue();
	}

	[TestCase]
	public void TestKeyComparerIsHonoured()
	{
		var tree = new Tree();
		var cache = new Engine_UiIdentityCache<string, string>(StringComparer.OrdinalIgnoreCase);
		cache.Put("AbilitiesScreen", "catalogue", tree);
		AssertBool(cache.TryGet("abilitiesscreen", out _, tree)).IsTrue();

		cache.Clear();
		AssertInt(cache.Count).IsEqual(0);
		AssertBool(cache.TryGet("abilitiesscreen", out _, tree)).IsFalse();
	}
}
