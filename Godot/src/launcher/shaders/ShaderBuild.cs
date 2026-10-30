using System;
using System.IO;
using System.Threading.Tasks;
using Refii.Bnk;
using Refii.Core;
using Refii.Launcher.Core;
using Refii.Launcher.Recompile;

namespace Refii.Launcher.Shaders;

/// <summary>
/// The "Compile Shaders" install step: the player's Shaders.sbk extracted, translated and
/// compiled into the unpacked workspace, where the modding tools read it.
/// </summary>
/// <remarks>
/// THE SAME SHAPE AS <see cref="GameCodeBuild"/>: a native library driven in this process,
/// progress into the install UI, pause and stop through a <see cref="BuildControl"/>, and the
/// output of a stopped run removed. It is one step rather than a pipeline of them because the
/// native side already is the whole pipeline.
///
/// INPUT AND OUTPUT ARE THE INSTALL'S. The banks are read from the installed game data and
/// the artifacts land under the unpacked workspace beside the banks the modding tools unpack,
/// which is where <see cref="Installer.GetReinstallStepStatus"/> looks for the report that
/// marks the step done.
///
/// A TITLE THAT PACKS ITS SHADER BANKS (<see cref="Engine_TitleGame.PackedShaderBanks"/>: Fable 3
/// keeps them in levels.bnk) has them read out of its mounted banks into
/// <see cref="UnpackedBankDirectory"/> first; the native pipeline only reads files.
/// </remarks>
public static class ShaderBuild
{
	/// <summary>The directory holding Shaders.sbk and ShadersRelease.sbk as loose files.</summary>
	public static string BankDirectory =>
		Path.Combine(Installer.GetGameContentDirectoryPath(), "Shaders");

	/// <summary>
	/// Where a title's packed shader banks are read out to: the title's unpacked workspace,
	/// derived from the player's own install.
	/// </summary>
	public static string UnpackedBankDirectory =>
		Path.Combine(Engine_GameData.UnpackedRootFor(Installer.GetActiveGameDataDirectoryPath()),
			"shader_banks");

	/// <summary>Where the blobs, the translated artifacts and the reports are written.</summary>
	public static string OutputDirectory => Path.Combine(Installer.GetLuaWorkspacePath(), "shaders");

	/// <summary>The report the pipeline writes last; its presence is the step's tick.</summary>
	public static string ReportPath => Path.Combine(OutputDirectory, "compile_report.tsv");

	/// <summary>
	/// Runs the pipeline off the calling thread, reporting each step. Returns a stopped result
	/// when <paramref name="control"/> asked for one, with the partial output removed.
	/// </summary>
	public static async Task<GameCodeBuild.Result> RunAsync(
		Action<GameCodeBuild.Progress> progress, BuildControl control, Action<string> log)
	{
		string title = Engine_Title.Active.Folder;
		string banks = BankDirectory;
		if (Engine_TitleGame.Active.PackedShaderBanks is { } packed)
		{
			progress(new GameCodeBuild.Progress("Reading the shader banks out of the game data...", 0.0f));
			banks = UnpackedBankDirectory;
			string destination = banks;
			string unpackError = await Task.Run(() => UnpackShaderBanks(packed, destination, log))
				.ConfigureAwait(false);
			if (unpackError.Length != 0)
			{
				return new GameCodeBuild.Result(false, unpackError);
			}
		}

		if (!HasBank(banks, "Shaders.sbk"))
		{
			return new GameCodeBuild.Result(false,
				"Shaders.sbk is not in " + banks + ". Install the game first.");
		}

		string output = OutputDirectory;
		// THE PLAYER'S TARGET IS "godot" ALONE. It is what the classic renderer reads, and the
		// recompiler writes it directly: no backend compiler runs. The pipeline's "auto" set adds
		// the reference renderer's vulkan and d3d12 artifacts, which the Godot-hosted game never
		// loads and which run the dxc tool once per blob — most of the wait, for nothing. Those
		// stay a developer's command-line run.
		GameCodeBuild.Result result = await Task.Run(() => ShaderPipeline.Build(
				title, banks, output, "godot", control,
				step => progress(new GameCodeBuild.Progress(step.Status, step.Fraction)),
				log))
			.ConfigureAwait(false);

		if (result.Stopped)
		{
			progress(new GameCodeBuild.Progress("Shader compile stopped.", 0.0f));
			// Whatever the pipeline had written: a half-translated set would otherwise pass for
			// a finished one the next time the report is looked for.
			try
			{
				if (Directory.Exists(output))
				{
					Directory.Delete(output, recursive: true);
				}
			}
			catch (Exception exception)
			{
				log("could not remove the stopped shader compile's output: " + exception.Message);
			}
		}

		Installer.InvalidateReinstallStepStatus();
		return result;
	}

	/// <summary>Whether <paramref name="directory"/> holds <paramref name="bank"/>, in any case.</summary>
	private static bool HasBank(string directory, string bank)
	{
		if (!Directory.Exists(directory))
		{
			return false;
		}

		foreach (string file in Directory.EnumerateFiles(directory))
		{
			if (string.Equals(Path.GetFileName(file), bank, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// Reads the title's two shader banks out of its mounted banks into
	/// <paramref name="destination"/>, under their own file names. Returns an error, or empty.
	/// </summary>
	private static string UnpackShaderBanks(
		(string Named, string Release) packed, string destination, Action<string> log)
	{
		string data = Installer.GetGameContentDirectoryPath();
		try
		{
			// The game's banks only: no nested containers, no loose files, no mods. The compile
			// translates what the title shipped.
			using BnkFileSystem mounted = BnkFileSystem.Mount(
				data, expandNested: false, looseExtensions: Array.Empty<string>());
			Directory.CreateDirectory(destination);
			foreach (string path in new[] { packed.Named, packed.Release })
			{
				if (!mounted.TryGetFile(path, out BnkFile file))
				{
					return path + " is not in the game data's banks under " + data +
					       ". Install the game first.";
				}

				byte[] bytes = file.ReadAllBytes();
				string target = Path.Combine(destination, Path.GetFileName(path));
				File.WriteAllBytes(target, bytes);
				log("shader bank " + path + " (" + bytes.Length + " bytes, from " +
				    file.SourceArchiveName + ") -> " + target);
			}
		}
		catch (Exception exception)
		{
			return "Reading the shader banks out of " + data + " failed: " + exception.Message;
		}

		return string.Empty;
	}
}
