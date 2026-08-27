namespace Refii.Ui;

/// <summary>
/// The movie the guest asked for, taken from its own `GUI.Play*MovieInternal` call.
/// </summary>
/// <remarks>
/// The five `Play*MovieInternal` natives are thunks into
/// `sub_823C0D90(player, name, gendered, localised, sound)` differing only in three `li`
/// immediates; there is no filename suffix, so all five play the same file. What the flags
/// select is the Bink audio TRACK: 1 male / 2 female for `en-uk`, +2 for any other language
/// with a mounted `videos` package — hero attribute key 12 at `823c1220`, language shift at
/// `823c1264`. The track is not computed here because the call states only the two booleans.
/// Taking the call gives the name earlier (`player+8` is written synchronously) and already
/// prefixed by `sub_823C0D90`, which existence-checks and rewrites `game:\data\` to `epi:\`.
/// </remarks>
public sealed class Engine_UiGuiMovie
{
	/// <summary>One stated request.</summary>
	/// <param name="Name">The argument as the script gave it — it already carries `.bik`.</param>
	/// <param name="Gendered">Pick the track by the hero's gender rather than track 1.</param>
	/// <param name="Localised">Resolve under the language's `videos` package, not `GAME:\`.</param>
	/// <param name="Sound">False only for `PlaySilentMovieInternal`; stored at `player+80`.</param>
	public readonly record struct Request(
		string Name, bool Gendered, bool Localised, bool Sound)
	{
		/// <summary>
		/// The prefix the guest would apply. Non-localised only: the localised root is not a
		/// string in the binary but a runtime lookup of the mounted `"videos"` content package,
		/// so a localised request must be resolved by whatever the host mounts.
		/// </summary>
		public string GuestPath =>
			Localised ? Name : @"GAME:\Data\Art\Videos\" + Name;
	}

	/// <summary>The most recent request, or null when the guest has asked for nothing.</summary>
	public Request? Current { get; private set; }

	/// <summary>Bumped on every request, so a consumer can act once per ask.</summary>
	public ulong Generation { get; private set; }

	/// <summary>How many the guest has asked for this session.</summary>
	public int Asked { get; private set; }

	/// <summary>`GUI.Play*MovieInternal(name)` — records what the guest stated.</summary>
	public Request State(string name, bool gendered, bool localised, bool sound)
	{
		var request = new Request(name, gendered, localised, sound);
		Current = request;
		++Generation;
		++Asked;
		return request;
	}

	/// <summary>Forgets the request. A level change, or the movie finishing.</summary>
	public void Clear()
	{
		if (Current is null)
		{
			return;
		}

		Current = null;
		++Generation;
	}

	/// <summary>One line for a report.</summary>
	public string Describe() =>
		Current is not { } request
			? "nothing asked for"
			: $"'{request.Name}'{(request.Localised ? " localised" : string.Empty)}" +
				$"{(request.Gendered ? " gendered" : string.Empty)}" +
				$"{(request.Sound ? string.Empty : " SILENT")} -> {request.GuestPath}";
}
