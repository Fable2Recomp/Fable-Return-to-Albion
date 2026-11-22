using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using Refii.Launcher.Core;

namespace Refii.Launcher;

/// <summary>
/// The Announcements box's two GitHub pages, written into its body: the project's latest commits
/// (Change Log) and its contributors with their avatars (Credits).
/// </summary>
/// <remarks>
/// <para>THE BOX'S TYPE. Text is walnut or sepia ink on parchment, and the one accent is the dark
/// gold ink the drawers' tabs use. The bright gold is for fills and rules only: as text on
/// parchment it does not read. The body font has no bold face, so nothing here asks for one; a
/// title is a size step up, and its detail line a step down in sepia.</para>
/// <para>The pages are written through the label's own calls, never as BBCode: a commit title or
/// a contributor's name is whatever its author typed.</para>
/// </remarks>
public sealed class LauncherNews
{
	/// <summary>The side of the square an avatar is fetched and kept at, in pixels.</summary>
	private const int AvatarPixels = 64;

	/// <summary>The most contributors whose avatars are fetched.</summary>
	private const int MostAvatars = 48;

	private readonly Dictionary<string, Texture2D> _avatars = new(StringComparer.OrdinalIgnoreCase);
	private Texture2D? _blankAvatar;

	/// <summary>Counts the avatar sets taken, so a page written before one arrived is written again.</summary>
	public int AvatarGeneration { get; private set; }

	private static int TitleSize => LauncherScale.Font(LauncherTokens.BodySize + 1);

	private static int DetailSize => LauncherScale.Font(11);

	private static Color Rule => new(LauncherTokens.Sepia, 0.35f);

	/// <summary>A page with nothing to list: one line saying why, and what to do about it.</summary>
	public static void WriteNotice(RichTextLabel body, string headline, string detail, bool failed)
	{
		Begin(body);
		body.PushColor(failed ? LauncherTokens.Crimson : LauncherTokens.SepiaInk);
		body.AddText(headline);
		body.Pop();
		if (detail.Length != 0)
		{
			body.Newline();
			body.PushFontSize(DetailSize);
			body.PushColor(LauncherTokens.SepiaInk);
			body.AddText(detail);
			body.PopAll();
		}
	}

	/// <summary>
	/// The Change Log: each commit's title, then its date, author, pull request and hash on one
	/// small line, with a hairline between commits.
	/// </summary>
	public static void WriteChangeLog(RichTextLabel body, IReadOnlyList<GitCommit> commits)
	{
		Begin(body);
		for (int i = 0; i < commits.Count; i++)
		{
			GitCommit commit = commits[i];
			if (i != 0)
			{
				body.AddHr(100, 1, Rule, HorizontalAlignment.Center, true, false);
				body.Newline();
			}

			string title = GitHubApi.SplitPullRequest(commit.Message, out string pullRequest);
			body.PushFontSize(TitleSize);
			body.PushColor(LauncherTokens.Walnut);
			body.AddText(title);
			body.Pop();
			body.Pop();
			body.Newline();

			var detail = new List<string>(3);
			if (commit.Date.Length != 0)
			{
				detail.Add(commit.Date);
			}

			if (commit.Author.Length != 0)
			{
				detail.Add(commit.Author);
			}

			if (pullRequest.Length != 0)
			{
				detail.Add("#" + pullRequest);
			}

			body.PushFontSize(DetailSize);
			body.PushColor(LauncherTokens.SepiaInk);
			body.AddText(string.Join("  ·  ", detail) + (detail.Count != 0 ? "  ·  " : string.Empty));
			body.PushColor(LauncherTokens.GoldInk);
			body.AddText(commit.Sha);
			body.Pop();
			body.Pop();
			body.Pop();
			body.Newline();
		}
	}

	/// <summary>
	/// Credits: one row a contributor, most contributions first as GitHub lists them: the avatar,
	/// the name, and the count at the right edge.
	/// </summary>
	public void WriteCredits(RichTextLabel body, IReadOnlyList<GitContributor> contributors)
	{
		int avatar = LauncherScale.Font(26);
		// A name sits beside the middle of its avatar, not level with its top.
		float drop = Mathf.Max(0.0f, (avatar - LauncherScale.Font(LauncherTokens.BodySize) * 1.35f) * 0.5f);
		float gap = LauncherScale.Px(4.0f);

		Begin(body);
		body.PushFontSize(DetailSize);
		body.PushColor(LauncherTokens.SepiaInk);
		body.AddText(contributors.Count == 1
			? "1 contributor"
			: contributors.Count + " contributors, by number of contributions");
		body.Pop();
		body.Pop();
		body.Newline();

		body.PushTable(3, InlineAlignment.Top, -1);
		body.SetTableColumnExpand(0, false, 1);
		body.SetTableColumnExpand(1, true, 1);
		body.SetTableColumnExpand(2, false, 1);
		foreach (GitContributor contributor in contributors)
		{
			body.PushCell();
			body.SetCellPadding(new Rect2(0.0f, gap, LauncherScale.Px(10.0f), gap));
			body.AddImage(AvatarFor(contributor.Login), avatar, avatar);
			body.Pop();

			body.PushCell();
			body.SetCellPadding(new Rect2(0.0f, gap + drop, LauncherScale.Px(8.0f), gap));
			body.PushColor(LauncherTokens.Walnut);
			body.AddText(contributor.Login);
			body.Pop();
			if (contributor.Name.Length != 0 &&
				!string.Equals(contributor.Name, contributor.Login, StringComparison.OrdinalIgnoreCase))
			{
				body.PushColor(LauncherTokens.SepiaInk);
				body.AddText("   " + contributor.Name);
				body.Pop();
			}

			body.Pop();

			body.PushCell();
			body.SetCellPadding(new Rect2(0.0f, gap + drop, 0.0f, gap));
			body.PushParagraph(HorizontalAlignment.Right);
			body.PushColor(LauncherTokens.SepiaInk);
			body.AddText(contributor.Contributions.ToString("N0"));
			body.Pop();
			body.Pop();
			body.Pop();
		}

		body.Pop();
	}

	/// <summary>
	/// Fetches the contributors' avatars, all at once, and keeps each as a round texture. Runs
	/// <paramref name="changed"/> once when any arrived. A contributor whose avatar cannot be
	/// fetched or read keeps the plain disc.
	/// </summary>
	public async Task LoadAvatarsAsync(IReadOnlyList<GitContributor> contributors, Action changed)
	{
		var fetches = new List<(string Login, Task<byte[]?> Bytes)>();
		foreach (GitContributor contributor in contributors)
		{
			if (fetches.Count == MostAvatars)
			{
				break;
			}

			if (contributor.AvatarUrl.Length == 0 || _avatars.ContainsKey(contributor.Login))
			{
				continue;
			}

			string url = contributor.AvatarUrl + (contributor.AvatarUrl.Contains('?') ? "&" : "?") + "s=" + AvatarPixels;
			fetches.Add((contributor.Login, GitHubApi.HttpGetBytesAsync(url)));
		}

		bool any = false;
		foreach ((string login, Task<byte[]?> bytes) in fetches)
		{
			// Awaited here, not on a worker: the textures are made on the launcher's own thread.
			if (await bytes is { Length: > 8 } data && RoundTexture(data) is { } texture)
			{
				_avatars[login] = texture;
				any = true;
			}
		}

		if (any)
		{
			AvatarGeneration++;
			changed();
		}
	}

	/// <summary>
	/// Empties the label for a page written call by call. Its Text is emptied as well as its
	/// content: the label ignores a Text equal to the one it last took, so a text page (About)
	/// shown before this one would otherwise not come back when it is set again.
	/// </summary>
	private static void Begin(RichTextLabel body)
	{
		body.Text = string.Empty;
		body.Clear();
	}

	private Texture2D AvatarFor(string login) =>
		_avatars.TryGetValue(login, out Texture2D? texture) ? texture : _blankAvatar ??= BlankAvatar();

	/// <summary>The avatar in <paramref name="bytes"/> (PNG or JPEG) as a round texture; null when it is neither.</summary>
	private static Texture2D? RoundTexture(byte[] bytes)
	{
		var image = new Image();
		bool png = bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;
		bool jpeg = bytes[0] == 0xFF && bytes[1] == 0xD8;
		if (!png && !jpeg)
		{
			return null;
		}

		Error error = png ? image.LoadPngFromBuffer(bytes) : image.LoadJpgFromBuffer(bytes);
		if (error != Error.Ok || image.IsEmpty())
		{
			return null;
		}

		image.Convert(Image.Format.Rgba8);
		image.Resize(AvatarPixels, AvatarPixels, Image.Interpolation.Lanczos);
		byte[] pixels = image.GetData();
		CutDisc(pixels, AvatarPixels);
		return ImageTexture.CreateFromImage(
			Image.CreateFromData(AvatarPixels, AvatarPixels, false, Image.Format.Rgba8, pixels));
	}

	private static Texture2D BlankAvatar()
	{
		var pixels = new byte[AvatarPixels * AvatarPixels * 4];
		Color fill = LauncherTokens.Sepia;
		for (int i = 0; i < pixels.Length; i += 4)
		{
			pixels[i] = (byte)fill.R8;
			pixels[i + 1] = (byte)fill.G8;
			pixels[i + 2] = (byte)fill.B8;
			pixels[i + 3] = 110;
		}

		CutDisc(pixels, AvatarPixels);
		return ImageTexture.CreateFromImage(
			Image.CreateFromData(AvatarPixels, AvatarPixels, false, Image.Format.Rgba8, pixels));
	}

	/// <summary>Clears an RGBA8 square's alpha outside its inscribed circle, with a one-pixel soft edge.</summary>
	private static void CutDisc(byte[] rgba, int side)
	{
		float centre = (side - 1) * 0.5f;
		float radius = side * 0.5f;
		for (int y = 0; y < side; y++)
		{
			for (int x = 0; x < side; x++)
			{
				float distance = Mathf.Sqrt(((x - centre) * (x - centre)) + ((y - centre) * (y - centre)));
				float cover = Mathf.Clamp(radius - distance, 0.0f, 1.0f);
				int alpha = ((y * side) + x) * 4 + 3;
				rgba[alpha] = (byte)(rgba[alpha] * cover);
			}
		}
	}
}
