using System;
using System.Collections.Generic;
using System.IO;

namespace Refii.Mods;

/// <summary>
/// The mod set a co-op session runs, held APART from the player's own: the host's enabled mods in
/// the host's order, each resolved to a package on this machine (installed, or received into the
/// session cache). While one is active every game-side reader of "the enabled mods" answers from
/// it; the launcher's <c>loadorder.toml</c> and the player's settings are never written.
/// </summary>
/// <remarks>
/// ONE ACCESSOR. <see cref="Resolve"/> is what the mount (<c>ModIndex</c>), the composers
/// (<c>ModEntries</c>, <c>ModGodot</c>) and the fingerprint read; with no session active it is the
/// launcher's list, so there is one answer to "what runs" at any moment. The launcher's panel and
/// the installer keep reading <see cref="ModLoadOrder.Read"/> directly: they write the file back,
/// and a session set must never reach it.
///
/// The set is memory only: a process that ends mid-session comes back on its own list. A session
/// that ends normally calls <see cref="Restore"/>, which remounts the player's own mods and runs
/// their Lua again.
/// </remarks>
public static class ModSession
{
	/// <summary>One mod of the session set, in session order (highest priority first).</summary>
	public sealed record Entry(string Name, string PackagePath, string Title, string Version, string Sha256);

	private static readonly object Lock = new();

	private static IReadOnlyList<Entry>? _active;

	/// <summary>The session set, or null when the player's own mods run.</summary>
	public static IReadOnlyList<Entry>? Active
	{
		get
		{
			lock (Lock)
			{
				return _active;
			}
		}
	}

	public static bool IsActive => Active is not null;

	/// <summary>
	/// The enabled order and the installs it resolves against: the session's while one is active,
	/// otherwise the launcher's list and the packages under <paramref name="modsRoot"/>.
	/// </summary>
	public static (ModLoadOrder Order, List<ModInstall> Installs) Resolve(string modsRoot)
	{
		IReadOnlyList<Entry>? active = Active;
		if (active is null)
		{
			return (ModLoadOrder.Read(modsRoot), ModWorkspace.DiscoverModInstalls(modsRoot));
		}

		var order = new ModLoadOrder();
		var installs = new List<ModInstall>(active.Count);
		foreach (Entry entry in active)
		{
			if (!File.Exists(entry.PackagePath) || order.Enabled.Exists(n => string.Equals(n, entry.Name, StringComparison.OrdinalIgnoreCase)))
			{
				continue;
			}

			ModMetadata meta = new();
			if (!F2ModPackage.TryReadMetadata(entry.PackagePath, out meta, out _))
			{
				meta = new ModMetadata();
			}

			if (string.IsNullOrWhiteSpace(meta.Title))
			{
				meta.Title = entry.Title.Length != 0 ? entry.Title : entry.Name;
			}

			if (meta.Version.Length == 0)
			{
				meta.Version = entry.Version;
			}

			order.Enabled.Add(entry.Name);
			order.Order.Add(entry.Name);
			installs.Add(new ModInstall { Name = entry.Name, PackagePath = entry.PackagePath, Metadata = meta });
		}

		return (order, installs);
	}

	/// <summary>
	/// Holds <paramref name="set"/> as the session's and folds it into the running game: the live
	/// remount (<see cref="ModRemount.Start"/>), with the mods' Lua run again. False with the
	/// reason when the remount cannot start; the set is then not held.
	/// </summary>
	public static bool Apply(IReadOnlyList<Entry> set, Action<ModRemount.Report> done, out string error)
	{
		IReadOnlyList<Entry>? previous;
		lock (Lock)
		{
			previous = _active;
			_active = new List<Entry>(set);
		}

		if (ModRemount.Start(lua: true, done, out error))
		{
			return true;
		}

		lock (Lock)
		{
			_active = previous;
		}

		return false;
	}

	/// <summary>
	/// Drops the session set and folds the player's own mods in again. True when a remount was
	/// started (<paramref name="done"/> runs when it finishes); false when no session set was held
	/// or the remount cannot start — the set is dropped either way, so the next mount is the
	/// player's.
	/// </summary>
	public static bool Restore(Action<ModRemount.Report> done, out string error)
	{
		error = string.Empty;
		lock (Lock)
		{
			if (_active is null)
			{
				return false;
			}

			_active = null;
		}

		return ModRemount.Start(lua: true, done, out error);
	}
}
