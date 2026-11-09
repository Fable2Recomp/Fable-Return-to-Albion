using GdUnit4;
using Godot;
using Refii.Game;
using static GdUnit4.Assertions;
using Refii.Titles.Fable2;

namespace Refii.Tests;

/// <summary>The expression wheel's pointer, in the guest's own sector numbering.</summary>
[TestSuite]
public sealed class GameUiExpressionWheelTests
{
    /// <summary>The expression wheel's sectors in the guest's own numbering, as measured: 0 south-east, counter-clockwise.</summary>
    [TestCase]
    public void WheelSectorsMatchTheGuest()
    {
        float d = Mathf.Sqrt(0.5f);
        AssertThat(GameUi.WheelSector(new Vector2(d, -d))).IsEqual(0);  // Dog
        AssertThat(GameUi.WheelSector(new Vector2(d, d))).IsEqual(2);   // Fun
        AssertThat(GameUi.WheelSector(new Vector2(0f, 1f))).IsEqual(3); // Social
        AssertThat(GameUi.WheelSector(new Vector2(-d, -d))).IsEqual(6); // Turn Safety
        AssertThat(GameUi.WheelSector(new Vector2(0.1f, 0.1f))).IsEqual(-1);
    }

    /// <summary>After an in-game reload the old HUD is listed beside the new one, in either order: the new one is followed.</summary>
    [TestCase]
    public void AReloadFollowsTheNewHudNotTheOldOne()
    {
        var retired = new System.Collections.Generic.HashSet<uint> { 0xAAAAu };
        AssertThat(GameUi.ChosenHud(0xBBBBu, 0xAAAAu, retired)).IsEqual(0xBBBBu);
        AssertThat(GameUi.ChosenHud(0xAAAAu, 0xBBBBu, retired)).IsEqual(0xBBBBu);
    }

    /// <summary>A retired HUD alone is still followed: the guest may have recycled its address for the new one.</summary>
    [TestCase]
    public void ARetiredHudAloneIsStillFollowed()
    {
        var retired = new System.Collections.Generic.HashSet<uint> { 0xAAAAu };
        AssertThat(GameUi.ChosenHud(0u, 0xAAAAu, retired)).IsEqual(0xAAAAu);
        AssertThat(GameUi.ChosenHud(0xAAAAu, 0xAAAAu, retired)).IsEqual(0xAAAAu);
    }
}
