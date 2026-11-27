using System;
using System.Collections.Generic;
using Refii.Gdb;

namespace Refii.Level;

/// <summary>
/// What a title knows about its own placed buildings that its data does not state: a stand-in
/// model that is really another, the parts a building brings with it, unique level shells, and
/// multi-instance meshes. The shell's placement build (<see cref="Engine_LevelProps"/>) asks;
/// the default knows nothing.
/// </summary>
public class Engine_TitleProps
{
	/// <summary>A title with no placement quirks.</summary>
	public static readonly Engine_TitleProps None = new();

	/// <summary>Whether the level already places the base model a <see cref="SubstituteModel"/> would stand in for.</summary>
	public virtual bool PlacesSubstitutedBase(IReadOnlyList<GdbPlacement> placements) => false;

	/// <summary>
	/// The model a placement really stands for when its own is a stand-in, or null; the stand-in
	/// is kept beside it as <paramref name="companion"/>.
	/// </summary>
	public virtual string? SubstituteModel(
		GdbPlacement placement, string entityName, string modelPath, bool levelPlacesBase, out string? companion)
	{
		companion = null;
		return null;
	}

	/// <summary>The parts a placed model brings with it (none for most), under one instance tag.</summary>
	public virtual IReadOnlyList<string> CompanionParts(string modelPath, out string tag)
	{
		tag = string.Empty;
		return Array.Empty<string>();
	}

	/// <summary>A building shell placed once per level, which a placement off the world must not draw.</summary>
	public virtual bool IsUniqueShellModel(string normalisedPath) => false;

	/// <summary>A model file holding several instances of one mesh, re-parsed into each.</summary>
	public virtual bool IsMultiInstanceModel(string normalisedPath) => false;
}
