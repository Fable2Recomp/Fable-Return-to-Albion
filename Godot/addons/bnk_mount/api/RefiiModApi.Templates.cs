#if TOOLS
using System.Linq;
using Refii.Mods;

namespace Refii.EditorTools;

/// <summary>
/// Starter mods: <see cref="ModTemplates"/> rendered into a fresh package through the same
/// writer every other call uses, so the result mounts and the Studio sees it at once.
/// </summary>
public partial class RefiiModApi
{
	/// <summary>Every template as "id — title — description".</summary>
	public string[] ListTemplates() =>
		ModTemplates.All.Select(t => $"{t.Id} — {t.Title} — {t.Description}").ToArray();

	/// <summary>
	/// Creates <paramref name="mod"/> (<see cref="CreateMod"/>, made active) and writes
	/// <paramref name="templateId"/>'s files into it. Returns an error for an unknown template,
	/// a name already taken, or a file that could not be written; empty when done.
	/// </summary>
	public string CreateModFromTemplate(string mod, string title, string author, string templateId)
	{
		if (ModTemplates.Find(templateId) is not { } template)
		{
			return $"no template '{templateId}' — ListTemplates() names them";
		}

		string error = CreateMod(mod, title, author, template.Description);
		if (error.Length != 0)
		{
			return error;
		}

		foreach ((string path, string text) in template.Render(mod, title))
		{
			error = WriteText(path, text);
			if (error.Length != 0)
			{
				return $"{path}: {error}";
			}
		}

		return string.Empty;
	}
}
#endif
