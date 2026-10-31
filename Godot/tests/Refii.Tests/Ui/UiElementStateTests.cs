namespace Refii.Tests;

using System.Collections.Generic;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Ui;

/// <summary>
/// The element cache behind every owned surface: a presentation read while the guest rebuilds
/// it must be read again, not remembered as empty.
/// </summary>
[TestSuite]
public class UiElementStateTests
{
	/// <summary>The address the crime screen gave up and the message box was built at.</summary>
	private const uint Reused = 0x4D6B9FF0u;

	private static List<Engine_UiModel.UiElement> Tree(string text) => new()
	{
		new Engine_UiModel.UiElement(0x11u, 0, null, 0, 0x3000, 0x40000000u),
		new Engine_UiModel.UiElement(0x22u, 0, text, 5, 0x3000, 0x40000010u),
	};

	/// <summary>Runs <paramref name="body"/> against an authored source, restoring the session's.</summary>
	private static void With(Engine_UiAuthoredSource source, System.Action body)
	{
		Engine_UiSource saved = Engine_UiSource.Current;
		Engine_UiSource.Current = source;
		try
		{
			body();
		}
		finally
		{
			Engine_UiSource.Current = saved;
		}
	}

	[TestCase]
	public void TestAnEmptyWalkIsReadAgain()
	{
		var source = new Engine_UiAuthoredSource();
		ulong frame = 0;
		var state = new Engine_UiElementState(() => frame);

		With(source, () =>
		{
			// Mid-rebuild: the address has no tree yet.
			AssertObject(state.Elements(Reused)).IsNull();

			// The box is built there; the next frame's read must find it.
			source.Elements[Reused] = Tree("Bandits are once again congregating");
			++frame;
			List<Engine_UiModel.UiElement>? elements = state.Elements(Reused);
			AssertObject(elements).IsNotNull();
			AssertInt(elements!.Count).IsEqual(2);
			AssertString(elements[1].Text).IsEqual("Bandits are once again congregating");
		});
	}

	[TestCase]
	public void TestATreeThatEmptiesAndRefillsIsReadAgain()
	{
		var source = new Engine_UiAuthoredSource();
		ulong frame = 0;
		var state = new Engine_UiElementState(() => frame);

		With(source, () =>
		{
			// The crime screen, seeded.
			source.Elements[Reused] = Tree("Pay a Fine");
			AssertInt(state.Elements(Reused)!.Count).IsEqual(2);

			// Torn down under the cache, then the message box stood up at the same address.
			source.Elements.Remove(Reused);
			++frame;
			AssertObject(state.Elements(Reused)).IsNull();

			source.Elements[Reused] = Tree("Do you want to set the quest as your target?");
			++frame;
			AssertString(state.Elements(Reused)![1].Text)
				.IsEqual("Do you want to set the quest as your target?");
		});
	}
}
