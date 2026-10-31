using Godot;

namespace Refii.Video;

/// <summary>
/// A loaded Bink movie, as a resource — the video counterpart to the .mdl and .tex
/// readers.
///
/// Holds the file, not a decode. Decoding is stateful (a decoder walks frames and keeps
/// reference data between them), so a shared resource cannot own one without two viewers
/// fighting over the playhead; <see cref="CreateDecoder"/> hands each player its own.
///
/// <see cref="Thumbnail"/> is the first frame, so a movie previews in a browser exactly
/// like a texture does, without anything having to play it.
/// </summary>
[Tool]
public partial class Engine_BinkVideo : Resource
{
	private byte[] _data = System.Array.Empty<byte>();

	[Export] public int Width { get; private set; }
	[Export] public int Height { get; private set; }
	[Export] public int FrameCount { get; private set; }
	[Export] public double FrameSeconds { get; private set; } = 1.0 / 30.0;

	/// <summary>First frame, for previewing. Null when the movie could not be decoded.</summary>
	public ImageTexture? Thumbnail { get; private set; }

	public double Duration => FrameCount * FrameSeconds;

	/// <summary>Builds a resource from raw .bik bytes, or returns null with the reason.</summary>
	public static Engine_BinkVideo? FromBytes(byte[] data, out string error)
	{
		using Engine_BinkDecoder? probe = Engine_BinkDecoder.Open(data, out error);
		if (probe is null)
		{
			return null;
		}

		var video = new Engine_BinkVideo
		{
			_data = data,
			Width = probe.Width,
			Height = probe.Height,
			FrameCount = probe.FrameCount,
			FrameSeconds = probe.FrameSeconds,
		};

		video.Thumbnail = RenderFirstFrame(probe);
		return video;
	}

	/// <summary>A decoder positioned at the first frame. The caller owns it.</summary>
	public Engine_BinkDecoder? CreateDecoder(out string error) =>
		Engine_BinkDecoder.Open(_data, out error);

	/// <summary>
	/// Decodes frame 0 to an RGB8 texture.
	///
	/// On the CPU deliberately: a thumbnail is decoded once and then only looked at, so
	/// the shader path the player uses would cost three textures and a material for a
	/// still. Playback keeps the GPU conversion.
	/// </summary>
	private static ImageTexture? RenderFirstFrame(Engine_BinkDecoder decoder)
	{
		int chromaWidth = (decoder.Width + 1) / 2;
		int chromaHeight = (decoder.Height + 1) / 2;

		var y = new byte[decoder.Width * decoder.Height];
		var u = new byte[chromaWidth * chromaHeight];
		var v = new byte[chromaWidth * chromaHeight];

		if (!decoder.NextFrame(y, u, v, out int cw, out int ch) || cw <= 0 || ch <= 0)
		{
			return null;
		}

		var rgb = new byte[decoder.Width * decoder.Height * 3];
		for (int row = 0; row < decoder.Height; ++row)
		{
			// Chroma is scaled by its REAL ratio rather than assumed to be half: the
			// planes are sized independently.
			int chromaRow = row * ch / decoder.Height;
			for (int col = 0; col < decoder.Width; ++col)
			{
				int chromaCol = col * cw / decoder.Width;

				// BT.601 studio swing, matching the playback shader.
				int luma = y[(row * decoder.Width) + col] - 16;
				int cb = u[(chromaRow * cw) + chromaCol] - 128;
				int cr = v[(chromaRow * cw) + chromaCol] - 128;
				int c = 298 * luma;

				int at = ((row * decoder.Width) + col) * 3;
				rgb[at + 0] = Clamp((c + (409 * cr) + 128) >> 8);
				rgb[at + 1] = Clamp((c - (100 * cb) - (208 * cr) + 128) >> 8);
				rgb[at + 2] = Clamp((c + (516 * cb) + 128) >> 8);
			}
		}

		return Refii.Textures.Engine_AndroidTextureUpload.CreateTexture(
			Image.CreateFromData(decoder.Width, decoder.Height, false, Image.Format.Rgb8, rgb));
	}

	private static byte Clamp(int value) =>
		value < 0 ? (byte)0 : value > 255 ? (byte)255 : (byte)value;
}
