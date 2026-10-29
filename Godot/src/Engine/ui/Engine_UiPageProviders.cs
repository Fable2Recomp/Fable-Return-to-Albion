using System.Collections.Generic;
using Godot;

namespace Refii.Ui;

/// <summary>
/// The providers a screen template can name, and the registry that resolves the name.
///
/// Each is a thin adapter over the page class that already computes the screen's values — the
/// computation stays where it was, and only the ROUTING moves into data. That keeps this change
/// to the one thing it is meant to fix: which code runs for which screen.
/// </summary>
public static class Engine_UiPageProviders
{
	public static readonly IReadOnlyDictionary<string, string> NoValues = new Dictionary<string, string>();
	public static readonly IReadOnlyDictionary<string, uint> NoTextures = new Dictionary<string, uint>();
	public static readonly IReadOnlyDictionary<string, bool> NoActive = new Dictionary<string, bool>();

	public static readonly IReadOnlyDictionary<string, float> NoOpacities =
		new Dictionary<string, float>();

	public static readonly IReadOnlyDictionary<string, float> NoRotations =
		new Dictionary<string, float>();
	public static readonly IReadOnlyDictionary<string, Vector3> NoPositions = new Dictionary<string, Vector3>();

	public static readonly IReadOnlyDictionary<string, Color> NoTints = new Dictionary<string, Color>();
	public static readonly IReadOnlyDictionary<string, int> NoSlides = new Dictionary<string, int>();

	/// <summary>The host page book (<see cref="Engine_UiHostBoxPage"/>): a host question on a page.</summary>
	public const string HostBox = "hostbox";

	/// <summary>
	/// The DATA provider (<see cref="Engine_UiDataPage"/>): a screen record's own <c>fields</c> and
	/// <c>switches</c>, bound to <c>mod:</c> / <c>player:</c> values, the highlighted row, Babel tags or
	/// literals — how a mod's screen is filled without C#.
	/// </summary>
	public const string Data = "data";

	/// <summary>
	/// The providers by name. The shell owns two (<see cref="HostBox"/>, <see cref="Data"/>); a title
	/// adds its screens' through <see cref="RegisterShipped"/> when it installs, and a mod its own
	/// through <see cref="Register"/>.
	/// </summary>
	private static readonly Dictionary<string, Engine_UiPageProvider> Registry = new()
	{
		[HostBox] = new HostBoxProvider(),
		[Data] = new Engine_UiDataPage(),
	};

	/// <summary>The shipped provider names — the shell's and the title's — which a mod may not replace.</summary>
	private static readonly HashSet<string> Shipped = new() { HostBox, Data };

	private static readonly List<string> ShippedOrder = new() { HostBox, Data };

	/// <summary>The shipped provider names — the shell's, then the active title's — in the order they were added.</summary>
	public static string[] ShippedNames
	{
		get
		{
			Refii.Core.Engine_TitleGame.EnsureInstalled();
			lock (Registry)
			{
				return ShippedOrder.ToArray();
			}
		}
	}

	/// <summary>The provider a template names, or null when it names none or an unknown one.</summary>
	public static Engine_UiPageProvider? Find(string name)
	{
		Refii.Core.Engine_TitleGame.EnsureInstalled();
		lock (Registry)
		{
			return name.Length != 0 && Registry.TryGetValue(name, out Engine_UiPageProvider? provider) ? provider : null;
		}
	}

	/// <summary>
	/// Registers a provider a screen record can name — a mod's Godot code filling its own screen. The
	/// shipped names stay shipped: false for one of them or an empty name.
	/// </summary>
	public static bool Register(string name, Engine_UiPageProvider provider)
	{
		Refii.Core.Engine_TitleGame.EnsureInstalled();
		lock (Registry)
		{
			if (name.Length == 0 || Shipped.Contains(name))
			{
				return false;
			}

			Registry[name] = provider;
			return true;
		}
	}

	/// <summary>A title's own screen provider, installed with the title; no mod may replace it.</summary>
	public static void RegisterShipped(string name, Engine_UiPageProvider provider)
	{
		lock (Registry)
		{
			Registry[name] = provider;
			if (Shipped.Add(name))
			{
				ShippedOrder.Add(name);
			}
		}
	}

	public static IEnumerable<string> Names
	{
		get
		{
			Refii.Core.Engine_TitleGame.EnsureInstalled();
			lock (Registry)
			{
				return new List<string>(Registry.Keys);
			}
		}
	}

	private sealed class HostBoxProvider : Engine_UiPageProvider
	{
		public IReadOnlyDictionary<string, string> Values(in Engine_UiPageContext page) =>
			Engine_UiHostBoxPage.Values(page.Row);

		public IReadOnlyDictionary<string, bool> Active(in Engine_UiPageContext page) =>
			Engine_UiHostBoxPage.Active(page.Row);
	}
}
