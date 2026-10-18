#if TOOLS
using System.Linq;
using Refii.Mods;

namespace Refii.EditorTools;

/// <summary>The schema check on the script API: the active mod's package as the mount checks it (<see cref="ModValidate"/>).</summary>
public partial class RefiiModApi
{
	/// <summary>
	/// Every issue of the active mod: each content kind against its loader's schema, and its
	/// <c>mod.toml</c>. One line per issue, <c>error: &lt;path&gt;: &lt;pointer&gt;: &lt;message&gt;</c>
	/// or <c>warning: ...</c>; empty when the mod is clean.
	/// </summary>
	public string[] ValidateActiveMod()
	{
		string package = PackagePath();
		if (package.Length == 0)
		{
			return new[] { "error: " + NoActiveMod };
		}

		return ModValidate.ValidateSource(package)
			.Select(issue => (issue.Error ? "error: " : "warning: ") + issue)
			.ToArray();
	}
}
#endif
