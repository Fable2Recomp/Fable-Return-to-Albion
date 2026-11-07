namespace Refii.Tests.Titles;

using System.Collections.Generic;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Titles.Fable3;

/// <summary>
/// `minigamebarcontrol.lua` reproduced (Fable3MiniGameBar): the table reuse and cloning, the
/// segment rule, removal, the hit fades, the pip, and the layer-opacity chunks (Fable3LuaContracts).
/// </summary>
[TestSuite]
public class Fable3MiniGameBarTests
{
	private static Dictionary<string, Fable3LuaParameter> Spawn(string colour, float at, float width, float index) => new()
	{
		["String1"] = new Fable3LuaParameter(colour, null),
		["Float1"] = new Fable3LuaParameter(null, at),
		["Float2"] = new Fable3LuaParameter(null, width),
		["Float3"] = new Fable3LuaParameter(null, index),
	};

	private static readonly Dictionary<string, Fable3LuaParameter> None = new();

	[TestCase]
	public void ShowStoresParksAndShowsTheBar()
	{
		var bar = new Fable3MiniGameBar();
		AssertThat(bar.Run("g_MiniGameBar:Show()", None)).IsTrue();
		AssertThat(bar.BarSlide).IsEqual(Fable3MiniGameBar.ShowSlide);
		AssertThat(bar.Elements.Count).IsEqual(5);
		foreach (Fable3MiniGameBar.Element element in bar.Elements)
		{
			AssertThat(element.Opacity).IsEqual(0f);
			AssertThat(element.X).IsEqual(Fable3MiniGameBar.ParkedX);
		}

		bar.Run("g_MiniGameBar:Hide()", None);
		AssertThat(bar.BarSlide).IsEqual(Fable3MiniGameBar.HideSlide);
	}

	[TestCase]
	public void HideBeforeShowWritesNothing()
	{
		var bar = new Fable3MiniGameBar();
		bar.Run("g_MiniGameBar:Hide()", None);
		bar.Run("g_MiniGameBar:SetPipTravel(3)", None);
		AssertThat(bar.BarSlide).IsNull();
		AssertThat(bar.PipX).IsNull();
	}

	[TestCase]
	public void SpawnPlacesTheTemplateThenClones()
	{
		var bar = new Fable3MiniGameBar();
		bar.Run("g_MiniGameBar:Show()", None);
		bar.Run("g_MiniGameBar:SpawnButton()", Spawn("G", 0.25f, 0.5f, 0f));
		Fable3MiniGameBar.Element a = bar.Elements[0];
		AssertThat(a.Colour).IsEqual(Fable3MiniGameBar.Colour.A);
		AssertThat(a.X).IsEqual(-310f + (0.25f * 620f));
		AssertThat(a.ScaleX).IsEqual(0.5f);
		AssertThat(a.Opacity).IsEqual(100f);

		bar.Run("g_MiniGameBar:SpawnButton()", Spawn("G", 0.75f, 0.25f, 1f));
		AssertThat(bar.Elements.Count).IsEqual(6);
		Fable3MiniGameBar.Element clone = bar.Elements[5];
		AssertThat(clone.Colour).IsEqual(Fable3MiniGameBar.Colour.A);
		AssertThat(clone.Copy).IsEqual(1);
		AssertThat(clone.X).IsEqual(-310f + (0.75f * 620f));

		// R is the B table (ButtonB), B the X table, Bl black; an unknown code spawns nothing.
		bar.Run("g_MiniGameBar:SpawnButton()", Spawn("R", 0.5f, 0.1f, 2f));
		AssertThat(bar.Elements[1].Colour).IsEqual(Fable3MiniGameBar.Colour.B);
		AssertThat(bar.Elements[1].Opacity).IsEqual(100f);
		bar.Run("g_MiniGameBar:SpawnButton()", Spawn("Q", 0.5f, 0.1f, 3f));
		AssertThat(bar.Elements.Count).IsEqual(6);
	}

	[TestCase]
	public void RemoveAndHitWritesFollowTheIndex()
	{
		var bar = new Fable3MiniGameBar();
		bar.Run("g_MiniGameBar:Show()", None);
		bar.Run("g_MiniGameBar:SpawnButton()", Spawn("Y", 0.1f, 0.2f, 4f));
		Fable3MiniGameBar.Element y = bar.Elements[3];

		var level = new Dictionary<string, Fable3LuaParameter>
		{
			["Float1"] = new Fable3LuaParameter(null, 4f),
			["Float2"] = new Fable3LuaParameter(null, 3f),
		};
		bar.Run("g_MiniGameBar:SetButtonHitLevel()", level);
		AssertThat(y.Opacity).IsEqual(70f);
		bar.Run("g_MiniGameBar:SetButtonHitFinal()", level);
		AssertThat(y.Opacity).IsEqual(20f);

		bar.Run("g_MiniGameBar:RemoveButton()", level);
		AssertThat(y.Opacity).IsEqual(0f);
		AssertThat(y.X).IsEqual(Fable3MiniGameBar.ParkedX);

		// The entry is free again: the next Y reuses the template, no clone.
		bar.Run("g_MiniGameBar:SpawnButton()", Spawn("Y", 0.3f, 0.2f, 5f));
		AssertThat(bar.Elements.Count).IsEqual(5);
		AssertThat(y.Opacity).IsEqual(100f);
	}

	[TestCase]
	public void PipBackgroundAndForeignChunks()
	{
		var bar = new Fable3MiniGameBar();
		bar.Run("g_MiniGameBar:Show()", None);
		bar.Run("g_MiniGameBar:SetPipTravel(5.000000)", None);
		AssertThat(bar.PipX).IsEqual(5f * 67.8f);

		bar.Run("g_MiniGameBar:SetBackground()", new Dictionary<string, Fable3LuaParameter>
		{
			["String1"] = new Fable3LuaParameter("G", null),
		});
		AssertThat(bar.Background).IsEqual("G");

		AssertThat(bar.Run("g_HUD:Enable()", None)).IsFalse();
		AssertThat(bar.Run("GUI:OnEnter()", None)).IsFalse();
	}

	[TestCase]
	public void LayerOpacityChunks()
	{
		AssertThat(Fable3LuaContracts.LayerOpacity("g_HUD:Enable()")).IsEqual(100f);
		AssertThat(Fable3LuaContracts.LayerOpacity("g_HUD:Disable()")).IsEqual(0f);
		AssertThat(Fable3LuaContracts.LayerOpacity("setAttribute(getElement('Scene.Layer'), 'opacity', 0)")).IsEqual(0f);
		AssertThat(Fable3LuaContracts.LayerOpacity("setAttribute(getElement('Scene.Layer'), 'opacity', 100)")).IsEqual(100f);
		AssertThat(Fable3LuaContracts.LayerOpacity("GUI:SetToDisplayInWidescreen()")).IsNull();
	}
}
