using System.Runtime.CompilerServices;

// Pure path and audio-budget helpers stay internal to the host; the CLI regression
// suite can exercise them directly without constructing native Godot scene objects.
[assembly: InternalsVisibleTo("Refii.Tests")]
