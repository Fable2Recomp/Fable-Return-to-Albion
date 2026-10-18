using System;
using System.Collections.Generic;
using System.Linq;

namespace Refii.Mods;

/// <summary>One unmet need of an enabled mod, in words the Mods panel shows.</summary>
public readonly record struct ModDependencyProblem(string Mod, string Message);

/// <summary>
/// What <c>mod.toml</c>'s <c>requires</c> and <c>after</c> mean for the load order.
///
/// A REQUIRED MOD SITS BENEATH THE MOD THAT NEEDS IT. Lower in <see cref="ModLoadOrder.Enabled"/>
/// is lower priority: mounted first, its Lua composed first, its hooks inside the dependent's in a
/// chain. So the dependent sees the requirement's tables, values and headers already there. The
/// order is sorted to keep that true; a mod is refused enabling while something it requires is
/// missing or off.
/// </summary>
public static class ModDependencies
{
	/// <summary>
	/// Every enabled mod's unmet requirement: not installed, not enabled, or below the version
	/// floor. <paramref name="onlyMods"/> limits the check to those mods.
	/// </summary>
	public static List<ModDependencyProblem> Check(
		ModLoadOrder order, IReadOnlyList<ModInstall> installed, IEnumerable<string>? onlyMods = null)
	{
		var problems = new List<ModDependencyProblem>();
		HashSet<string>? only = onlyMods is null
			? null
			: new HashSet<string>(onlyMods, StringComparer.OrdinalIgnoreCase);
		Dictionary<string, ModInstall> byName = ByName(installed);
		foreach (string name in order.Enabled)
		{
			if (only is not null && !only.Contains(name))
			{
				continue;
			}

			if (byName.TryGetValue(name, out ModInstall? mod))
			{
				problems.AddRange(Unmet(mod, order, byName));
			}
		}

		return problems;
	}

	/// <summary>What <paramref name="mod"/> would lack if it were enabled now.</summary>
	public static List<ModDependencyProblem> CheckOne(
		ModInstall mod, ModLoadOrder order, IReadOnlyList<ModInstall> installed) =>
		Unmet(mod, order, ByName(installed)).ToList();

	private static IEnumerable<ModDependencyProblem> Unmet(
		ModInstall mod, ModLoadOrder order, Dictionary<string, ModInstall> byName)
	{
		foreach (ModRequirement requirement in mod.Metadata.Requires)
		{
			if (!byName.TryGetValue(requirement.Name, out ModInstall? required))
			{
				yield return new ModDependencyProblem(mod.Name, $"requires '{requirement.Name}', which is not installed");
			}
			else if (!order.Enabled.Contains(required.Name, StringComparer.OrdinalIgnoreCase))
			{
				yield return new ModDependencyProblem(mod.Name, $"requires '{required.Name}', which is disabled");
			}
			else if (!requirement.Satisfied(required.Metadata.Version))
			{
				yield return new ModDependencyProblem(mod.Name,
					$"requires '{required.Name}' {requirement.MinVersion} or newer; " +
					$"{(required.Metadata.Version.Length == 0 ? "no version" : required.Metadata.Version)} is installed");
			}
		}
	}

	/// <summary>
	/// Reorders <see cref="ModLoadOrder.Order"/> so every mod comes before (above) what it requires
	/// or is declared <c>after</c>, moving as little as possible, then syncs the enabled list.
	/// Returns whether anything moved. A cycle leaves the order alone and names its members in
	/// <paramref name="cycle"/>.
	/// </summary>
	public static bool Sort(ModLoadOrder order, IReadOnlyList<ModInstall> installed, out string cycle)
	{
		cycle = string.Empty;
		Dictionary<string, ModInstall> byName = ByName(installed);
		List<string> names = order.Order.ToList();
		var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		for (int i = 0; i < names.Count; i++)
		{
			index.TryAdd(names[i], i);
		}

		// before[x] = the mods that must come before x; x waits until all of them are placed.
		var before = names.ToDictionary(name => name, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);
		var afterOf = names.ToDictionary(name => name, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);
		foreach (string name in names)
		{
			if (!byName.TryGetValue(name, out ModInstall? mod))
			{
				continue;
			}

			IEnumerable<string> below = mod.Metadata.Requires.Select(r => r.Name).Concat(mod.Metadata.After);
			foreach (string dependency in below)
			{
				if (!index.ContainsKey(dependency) ||
					string.Equals(dependency, name, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				string key = names[index[dependency]];
				if (!before[key].Contains(name, StringComparer.OrdinalIgnoreCase))
				{
					before[key].Add(name);
					afterOf[name].Add(key);
				}
			}
		}

		var placed = new List<string>(names.Count);
		var remaining = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		foreach (string name in names)
		{
			remaining[name] = before[name].Count;
		}

		while (placed.Count < names.Count)
		{
			string? next = null;
			foreach (string name in names)
			{
				if (remaining.TryGetValue(name, out int waiting) && waiting == 0)
				{
					next = name;
					break;
				}
			}

			if (next is null)
			{
				cycle = string.Join(" -> ", remaining.Where(entry => entry.Value > 0).Select(entry => entry.Key));
				return false;
			}

			placed.Add(next);
			remaining.Remove(next);
			foreach (string freed in afterOf[next])
			{
				if (remaining.ContainsKey(freed))
				{
					remaining[freed]--;
				}
			}
		}

		bool changed = !placed.SequenceEqual(order.Order, StringComparer.OrdinalIgnoreCase);
		if (changed)
		{
			order.Order.Clear();
			order.Order.AddRange(placed);
			order.SyncEnabledWithOrder();
		}

		return changed;
	}

	private static Dictionary<string, ModInstall> ByName(IReadOnlyList<ModInstall> installed)
	{
		var byName = new Dictionary<string, ModInstall>(StringComparer.OrdinalIgnoreCase);
		foreach (ModInstall mod in installed)
		{
			byName.TryAdd(mod.Name, mod);
		}

		return byName;
	}
}
