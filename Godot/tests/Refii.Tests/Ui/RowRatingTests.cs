namespace Refii.Tests.Ui;

using System;
using System.Linq;
using GdUnit4;
using Godot;
using Refii.Core;
using Refii.Ui;
using static GdUnit4.Assertions;

/// <summary>Rating slides move their cloned art as well as selecting its visible stars.</summary>
[TestSuite]
public sealed class RowRatingTests
{
	[TestCase]
	public void SlidTransformsRetainPivotAndDepthConversion()
	{
		var node = new UiNode
		{
			PositionX = 12, PositionY = 4, PositionZ = 3,
			ScaleX = 2, ScaleY = 3, PivotX = 1, PivotY = 2, PivotZ = 1,
		};
		Transform3D transformed = Engine_UiSceneBuilder.BuildTransform(node,
			(attribute, parsed) => attribute == "position.y" ? -8 : parsed);
		AssertThat(transformed.Origin).IsEqual(new Vector3(10, -14, -2));
		AssertThat(transformed.Basis.Scale).IsEqual(new Vector3(2, 3, 1));
		AssertThat(Engine_UiSceneBuilder.BuildTransform(node).Origin).IsEqual(new Vector3(10, -2, -2));
	}

	private static Engine_UiScreen Screen()
	{
		Engine_BgfDocument doc = Engine_BgfEdit.NewDocument("Scene", 1280, 720, true);
		int menu = Engine_BgfEdit.AddChild(doc, 0, "ExpandableMenu");
		int buttons = Engine_BgfEdit.AddChild(doc, menu, "Buttons");
		int button = Engine_BgfEdit.AddChild(doc, buttons, "Button");
		doc.Elements[button].Type = BgfElementType.Component;
		doc.Elements[button].Flags |= 4;
		int tab = Engine_BgfEdit.AddChild(doc, button, "Tab");
		int stars = Engine_BgfEdit.AddChild(doc, tab, "Stars");
		int group = Engine_BgfEdit.AddChild(doc, stars, "Group");
		Engine_BgfEdit.SetFloat(doc, group, "scale.x", 0.5f);
		Engine_BgfEdit.SetFloat(doc, group, "scale.y", 0.5f);
		int star = Engine_BgfEdit.AddChild(doc, group, "starfull");
		doc.Slides = Enumerable.Range(1, 11).Select(slide => new BgfSlide
		{
			Name = $"Rating{slide}", OwnerElement = stars,
			Entries = new[]
			{
				new BgfSlideEntry
				{
					ElementOrdinal = group, Active = true,
					Commands = new[] { new BgfAttribute(Engine_GameHash.AttributeId("position.y"),
						BgfValueType.Float, BitConverter.SingleToUInt32Bits(slide is >= 5 and <= 10 ? -8f : 0f)) },
					AnimationEvents = Array.Empty<BgfAnimationEvent>(),
				},
				new BgfSlideEntry
				{
					ElementOrdinal = star, Active = slide != 11,
					Commands = Array.Empty<BgfAttribute>(), AnimationEvents = Array.Empty<BgfAnimationEvent>(),
				},
			},
		}).ToArray();
		return Engine_UiScreen.Parse("rating", Engine_BgfWriter.Write(doc), null);
	}

	[TestCase]
	[RequireGodotRuntime]
	public void ChangingRatingsRestoresEachSlidesPlacementWithoutDrift()
	{
		Engine_UiRowRating ratings = Engine_UiRowRating.Resolve(Screen())!;
		var frame = new Node3D();
		var tab = new Node3D { Name = "Tab" };
		var stars = new Node3D { Name = "Stars" };
		var group = new Node3D { Name = "Group" };
		var star = new Node3D { Name = "starfull" };
		frame.AddChild(tab);
		tab.AddChild(stars);
		stars.AddChild(group);
		group.AddChild(star);
		try
		{
			foreach (int slide in new[] { 8, 10, 2, 11, 8, 8 })
			{
				ratings.Apply(frame, new Engine_UiMenuFormatter.RowRating(slide, Array.Empty<int>()));
				AssertThat(group.Position.Y).IsEqual(slide is >= 5 and <= 10 ? -8f : 0f);
				AssertThat(group.Scale).IsEqual(new Vector3(0.5f, 0.5f, 1f));
				AssertThat(star.Visible).IsEqual(slide != 11);
			}
		}
		finally { frame.Free(); }
	}
}
