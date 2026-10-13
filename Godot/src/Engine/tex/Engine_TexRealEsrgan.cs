using System;
using System.Runtime.InteropServices;
using Godot;

namespace Refii.Textures;

/// <summary>
/// The neural backend: §8's Real-ESRGAN, driven through the native ABI in
/// <c>src/upscaler/include/refii/upscaler.h</c>.
/// </summary>
/// <remarks>
/// EMBEDDED, NOT A SUBPROCESS. §8 asks for an implementation that keeps the model and the Vulkan
/// context alive, and lists the costs of the alternative: process creation, Python startup, PNG
/// encode, PNG decode, and re-initializing Vulkan for every texture. Every one of those is a
/// consequence of the boundary being a FILE, so this boundary is a buffer.
///
/// IT LOADS OR IT DECLINES. The native library is not built yet and may be absent on a player's
/// machine regardless — no suitable Vulkan device, no weights beside the build. Every path here
/// treats that as a normal answer and returns false, which the pipeline already handles by
/// keeping the original texture. Nothing here may throw into a worker.
/// </remarks>
public sealed class Engine_TexRealEsrgan : ITexUpscaler, IDisposable
{
	private const string Library = "refii_upscaler";

	/// <summary>
	/// Installs the native library resolver before the first P/Invoke, as every other native
	/// binding here does.
	/// </summary>
	/// <remarks>
	/// NOT OPTIONAL, and it is not obvious that it is missing. The build outputs are on no default
	/// search path, so without the resolver a perfectly good <c>librefii_upscaler.so</c> sitting
	/// in <c>Godot/build/&lt;arch&gt;/bin</c> reports as "not present" — indistinguishable from never having
	/// been built. The other bindings get away with it implicitly because
	/// <see cref="Refii.Video.Engine_BinkDecoder"/>, <see cref="Refii.Audio.Engine_XmaDecoder"/>
	/// and the host bridge all install it from their own static constructors, and something
	/// touches one of them first in a normal session. Nothing on the texture path does.
	/// </remarks>
	static Engine_TexRealEsrgan() => Refii.Core.Engine_NativeLibraries.Install();

	private IntPtr _handle;

	private string _id = "realesrgan-unloaded";

	/// <summary>
	/// Creates the backend, or null when the native side is unavailable.
	/// </summary>
	/// <remarks>
	/// A <see cref="DllNotFoundException"/> here is the ordinary case until the library is
	/// vendored and built, so it is caught and reported once rather than propagated. The caller
	/// registers the classical resampler instead.
	/// </remarks>
	public static Engine_TexRealEsrgan? TryCreate(string modelDirectory, string modelName)
	{
		// CHECKED HERE so the failure can be NAMED. The native create returns null for "no Vulkan
		// device" and "no weights" alike, and the message that used to come out said "declined to
		// start on this machine" — which reads as a hardware problem. A real session spent its
		// time on the classical resampler because the weights simply were not beside the build,
		// and the log pointed at the graphics card.
		string param = System.IO.Path.Combine(modelDirectory, modelName + ".param");
		string bin = System.IO.Path.Combine(modelDirectory, modelName + ".bin");
		if (!System.IO.File.Exists(param) || !System.IO.File.Exists(bin))
		{
			// FALL BACK TO WHATEVER IS ACTUALLY THERE, and say so. The configured name is a
			// default nobody chose — a session ran the classical path for a day because the
			// setting said `realesr-general-x4v3` and the weights beside the build were a
			// different model. Dropping a model in should be enough to use it.
			//
			// Named in the log either way, because silently using a model the settings do not
			// mention is its own kind of confusing, and the SCALE is read from the file name.
			string? found = FindAnyWeights(modelDirectory);
			if (found is null)
			{
				GD.Print(
					$"[tex] no weights for '{modelName}' in {modelDirectory} " +
					"(expected .param and .bin); neural upscaling unavailable");
				return null;
			}

			GD.Print($"[tex] '{modelName}' not found; using '{found}' from {modelDirectory}");
			modelName = found;
		}

		var backend = new Engine_TexRealEsrgan();

		try
		{
			backend._handle = refii_upscaler_create(modelDirectory, modelName);
		}
		catch (DllNotFoundException)
		{
			GD.Print($"[tex] {Library} is not present; neural upscaling unavailable");
			return null;
		}
		catch (EntryPointNotFoundException e)
		{
			GD.PushWarning($"[tex] {Library} is present but does not match the ABI: {e.Message}");
			return null;
		}

		if (backend._handle == IntPtr.Zero)
		{
			// The weights exist, so this really is the machine: no suitable Vulkan device, or a
			// model ncnn could not parse.
			GD.Print(
				$"[tex] '{modelName}' is present but the neural upscaler would not start " +
				"(no suitable device, or the model failed to parse)");
			return null;
		}

		try
		{
			IntPtr id = refii_upscaler_id(backend._handle);
			backend._id = id == IntPtr.Zero
				? "realesrgan"
				: Marshal.PtrToStringAnsi(id) ?? "realesrgan";
		}
		catch (Exception)
		{
			backend._id = "realesrgan";
		}

		GD.Print($"[tex] neural upscaler ready: {backend._id}");
		return backend;
	}

	/// <summary>
	/// Any complete <c>.param</c> + <c>.bin</c> pair in the directory, by name.
	/// </summary>
	/// <remarks>
	/// Alphabetical so the choice is STABLE. Picking by directory order would mean two machines
	/// with the same two models could load different ones and cache under different ids, and the
	/// difference would only ever show up as an unexplained cache miss.
	///
	/// A lone .param or a lone .bin is not a model; both have to be there.
	/// </remarks>
	private static string? FindAnyWeights(string directory)
	{
		try
		{
			string[] parameters = System.IO.Directory.GetFiles(directory, "*.param");
			Array.Sort(parameters, StringComparer.Ordinal);

			foreach (string candidate in parameters)
			{
				string name = System.IO.Path.GetFileNameWithoutExtension(candidate);
				if (System.IO.File.Exists(System.IO.Path.Combine(directory, name + ".bin")))
				{
					return name;
				}
			}
		}
		catch (Exception)
		{
			// An unreadable directory is the same answer as an empty one.
		}

		return null;
	}

	/// <summary>
	/// The model AND its weights. Rides in the cache key.
	/// </summary>
	/// <remarks>
	/// Taken from the native side rather than composed here, because the weights are what decide
	/// the output and only the native side knows which it loaded. A stale id would serve the
	/// previous model's output out of the cache forever.
	/// </remarks>
	public string Id => _id;

	public bool TryUpscale(Image source, TexFilterClass filter, int scale, out Image? result)
	{
		result = null;

		if (_handle == IntPtr.Zero || scale <= 1 ||
			source.IsCompressed() || source.HasMipmaps())
		{
			return false;
		}

		int width = source.GetWidth();
		int height = source.GetHeight();
		int outWidth = width * scale;
		int outHeight = height * scale;

		byte[] src = source.GetData();
		if (src.Length != width * height * 4)
		{
			// The contract is tightly packed RGBA8. Anything else means a caller stopped honouring
			// it, and guessing a stride would corrupt the image rather than fail.
			return false;
		}

		// Pooled: the upscaled RGBA8 is 16x the source at x4, and Image.CreateFromData copies it
		// out of the span below.
		int dstBytes = outWidth * outHeight * 4;
		byte[] dst = System.Buffers.ArrayPool<byte>.Shared.Rent(dstBytes);
		try
		{
			int status;
			try
			{
				status = refii_upscaler_run(
					_handle, src, width, height, scale, (int)filter, dst, dstBytes);
			}
			catch (Exception e)
			{
				GD.PushWarning($"[tex] neural upscaler failed: {e.Message}");
				return false;
			}

			if (status != 0)
			{
				return false;
			}

			result = Image.CreateFromData(
				outWidth, outHeight, false, Image.Format.Rgba8, new ReadOnlySpan<byte>(dst, 0, dstBytes));
			return true;
		}
		finally
		{
			System.Buffers.ArrayPool<byte>.Shared.Return(dst);
		}
	}

	public void Dispose()
	{
		if (_handle == IntPtr.Zero)
		{
			return;
		}

		try
		{
			refii_upscaler_destroy(_handle);
		}
		catch (Exception)
		{
		}

		_handle = IntPtr.Zero;
	}

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	private static extern IntPtr refii_upscaler_create(string modelDirectory, string modelName);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	private static extern void refii_upscaler_destroy(IntPtr upscaler);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	private static extern IntPtr refii_upscaler_id(IntPtr upscaler);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	private static extern int refii_upscaler_run(
		IntPtr upscaler,
		byte[] src,
		int srcWidth,
		int srcHeight,
		int scale,
		int filter,
		byte[] dst,
		int dstCapacity);
}
