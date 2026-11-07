namespace Refii.Titles.Fable3;

using Godot;
using Refii.Ui;

/// <summary>
/// Fable 3's end of the pointer's frame: its screens are separate canvases, each processing on its
/// own, so this runs after all of them (a late process priority) and hands what none took to the
/// pad — a free right click is B, the wheel's notches D-pad steps — as Fable 2's UI does at the end
/// of its frame (<see cref="Engine_UiPointer.HandLeftoversToPad"/>, one implementation).
/// </summary>
public partial class Fable3PointerLeftovers : Node
{
	public override void _Ready() => ProcessPriority = 100000;

	public override void _Process(double delta) => Engine_UiPointer.HandLeftoversToPad();
}
