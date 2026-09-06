using System;
using System.Collections.Generic;
using System.IO;
using Refii.Core;

namespace Refii.Gdb;

/// <summary>
/// Every game database on disk, as one lookup. The equipment indexes each load the handful of
/// files they need and only once a bank resolver is handed to them; a guest-stated record hash
/// (a worn item, a definition) has to resolve whether or not any of them was built, so this
/// reads the loose `.gdb` files under the game data root once and answers for all of them.
/// </summary>
public static class Engine_GdbCatalog
{
	private static GdbMulti? _all;

	private static bool _tried;

	/// <summary>The union of every `.gdb` under the game data, or null when none is on disk.</summary>
	public static GdbMulti? All
	{
		get
		{
			if (_tried)
			{
				return _all;
			}

			_tried = true;
			try
			{
				string root = Engine_GameData.Resolve();
				if (root.Length == 0 || !Directory.Exists(root))
				{
					return null;
				}

				var blobs = new List<byte[]?>();
				foreach (string file in Directory.EnumerateFiles(root, "*.gdb", SearchOption.AllDirectories))
				{
					blobs.Add(File.ReadAllBytes(file));
				}

				_all = blobs.Count == 0 ? null : GdbMulti.Build(blobs);
			}
			catch (Exception)
			{
				_all = null;
			}

			return _all;
		}
	}
}
