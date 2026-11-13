using Godot;
using GdUnit4;
using Refii.AndroidRuntime;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>`android_lean_tree_streams` (AndroidLeanTreeStreams): the decision and the CUSTOM1 conversion.</summary>
[TestSuite]
public sealed class AndroidLeanTreeStreamsTests
{
    [TestCase]
    public void WantedOnlyOnAPhoneWithTheKeyAndNoClassicRenderer()
    {
        AssertThat(AndroidLeanTreeStreams.Wanted(true, true, false)).IsTrue();
        AssertThat(AndroidLeanTreeStreams.Wanted(false, true, false)).IsFalse();
        AssertThat(AndroidLeanTreeStreams.Wanted(true, false, false)).IsFalse();
        AssertThat(AndroidLeanTreeStreams.Wanted(true, true, true)).IsFalse();
    }

    /// <summary>Off a phone the live decision is false before the key is read or a config listener hung.</summary>
    [TestCase]
    public void TheDesktopNeverReadsTheKey()
    {
        AssertThat(Refii.Core.Engine_Platform.IsAndroid).IsFalse();
        AssertThat(AndroidLeanTreeStreams.Wanted()).IsFalse();
        AssertThat((bool)(typeof(AndroidLeanTreeStreams).GetField("_hooked", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null) ?? true)).IsFalse();
    }

    [TestCase]
    public void EveryD3dColourByteRoundTripsExactly()
    {
        var floats = new float[256];
        for (int b = 0; b < 256; b++) floats[b] = b / 255f;
        byte[] bytes = AndroidLeanTreeStreams.ToRgba8(floats);
        for (int b = 0; b < 256; b++) AssertThat((int)bytes[b]).IsEqual(b);
    }

    [TestCase]
    public void OutOfRangeValuesClamp()
    {
        byte[] bytes = AndroidLeanTreeStreams.ToRgba8(new[] { -0.5f, 1.5f, 0f, 1f });
        AssertThat((int)bytes[0]).IsEqual(0);
        AssertThat((int)bytes[1]).IsEqual(255);
        AssertThat((int)bytes[2]).IsEqual(0);
        AssertThat((int)bytes[3]).IsEqual(255);
    }

    [TestCase, RequireGodotRuntime]
    public void ATreeShapeIsRecognisedAndATransferSurfaceIsNot()
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = new[] { Vector3.Zero, Vector3.One };
        arrays[(int)Mesh.ArrayType.Custom1] = new float[8];
        arrays[(int)Mesh.ArrayType.Custom2] = new float[8];
        arrays[(int)Mesh.ArrayType.Custom3] = new float[8];
        AssertThat(AndroidLeanTreeStreams.IsTreeShape(arrays)).IsTrue();

        arrays[(int)Mesh.ArrayType.Custom0] = new float[8];
        AssertThat(AndroidLeanTreeStreams.IsTreeShape(arrays)).IsFalse();
    }
}
