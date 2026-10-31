using GdUnit4;
using Godot;
using Refii.Game;
using static GdUnit4.Assertions;

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
}
