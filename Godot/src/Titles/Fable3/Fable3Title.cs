namespace Refii.Titles.Fable3;

using System.Runtime.CompilerServices;
using Refii.Core;

/// <summary>
/// Fable 3 to the shell. It has no game-level pieces of its own yet: the shell runs it as it is.
/// </summary>
public sealed class Fable3Title : Engine_TitleGame
{
	[ModuleInitializer]
	internal static void RegisterTitle() => Register(new Fable3Title());

	public override Engine_Title Title => Engine_Title.Fable3;
}
