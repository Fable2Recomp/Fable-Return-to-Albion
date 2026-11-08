using System.Text;
using Godot;
using Refii.Bnk;

namespace Refii.Titles.Fable2;

using Refii.Save;

/// <summary>
/// Lets a save load when it names a script function these scripts do not define.
/// </summary>
/// <remarks>
/// <para>A save writes every running coroutine, and a frame's function goes out BY NAME: a
/// permanent, the FNV hash of its path (<c>saveloadsystem.lua</c>, <c>AddToPermanentsTables</c>).
/// On load <c>pluto_unpersist</c> (TU1 0x82BFBF90, type 0x65) looks the hash up in
/// <c>PlutoPermanentsLoadTable</c> with <c>lua_gettable</c> and registers whatever comes back, nil
/// included; <c>pluto_unpersistthread</c> (0x82BFBA60) then rebuilds the frame's pc from that
/// function's code and reads through the nil (0x82BFBC10). A console title update's WatchDogs
/// (<c>EpisodeUpdater</c>, <c>EpisodeUnlocker</c>) in a save brought over from the Xbox 360 are the
/// case seen: 2026-10-24, beta 6, every load of that save.</para>
/// <para>The lookup honours <c>__index</c>, so an unknown hash resolves to a stub instead: a LUA
/// function (a C function's +0x10 is its pointer, not a Proto) whose body is <see cref="StubMoves"/>
/// MOVE no-ops between two locals and a return. A frame resumed at any saved pc runs off the end
/// of them, returns nothing, and its coroutine ends; the script managers drop dead threads. The
/// stub is not a permanent, so a save taken before the thread runs writes it by value and loads
/// back as a plain closure. Each unknown hash is logged once.</para>
/// <para>Appended to the core chunk: <c>LoadStartupScripts</c> runs <c>InitialisePermanentsLoadTable</c>
/// first and the core chunk after, so the table exists here exactly when a save is being loaded.</para>
/// </remarks>
public static class Engine_SaveCompatLua
{
	/// <summary>The stub's no-ops. A saved pc past this many instructions would read off the end
	/// of its code; the game's own coroutine bodies are hundreds of instructions at most.</summary>
	public const int StubMoves = 8192;

	public static readonly string Source = Build();

	/// <summary>
	/// Appends the stub to the title's core chunk <paramref name="coreChunk"/> (Fable 2's is
	/// `refii/core_init.lua`), which must already be in the mount: it runs in the same VM pass.
	/// </summary>
	public static void Attach(BnkFileSystem mounted, string coreChunk)
	{
		if (Refii.Lua.Engine_LuaDecompiler.Available &&
			!Refii.Lua.Engine_LuaDecompiler.TryCheckSource(Source, "@refii/save-compat", out string error))
		{
			GD.PushError($"[save-compat] the chunk does not compile and is NOT run: {error}");
			return;
		}

		string previous = mounted.TryGetFile(coreChunk, out var core)
			? Encoding.UTF8.GetString(core.ReadAllBytes())
			: string.Empty;
		byte[] bytes = Encoding.UTF8.GetBytes(previous + "\n" + Source);
		mounted.AddVirtualFile(coreChunk, coreChunk, bytes.Length, () => bytes, "Refii");
	}

	private static string Build()
	{
		var text = new StringBuilder(StubMoves * 6 + 1024);
		text.Append("-- Refii save compatibility: a permanent the save names and these scripts do not define\n");
		text.Append("-- (Engine_SaveCompatLua.cs). Unpersisting it as nil faults in pluto_unpersistthread; it\n");
		text.Append("-- resolves to RefiiSaveStub instead, whose frame runs off its no-ops and ends the thread.\n");
		text.Append("if PlutoPermanentsLoadTable ~= nil and getmetatable(PlutoPermanentsLoadTable) == nil then\n");
		text.Append("\tlocal function RefiiSaveStub()\n");
		text.Append("\t\tlocal a, b\n");
		for (int pair = 0; pair < StubMoves / 2; pair++)
		{
			text.Append(pair % 16 == 0 ? "\t\t" : " ");
			text.Append("a = b b = a");
			if (pair % 16 == 15)
			{
				text.Append('\n');
			}
		}

		text.Append("\n\tend\n");
		text.Append("\tlocal seen = {}\n");
		text.Append("\tsetmetatable(PlutoPermanentsLoadTable, { __index = function(_, key)\n");
		text.Append("\t\tif not seen[key] then\n");
		text.Append("\t\t\tseen[key] = true\n");
		text.Append("\t\t\tif Refii ~= nil and Refii.log ~= nil then\n");
		text.Append("\t\t\t\tRefii.log(\"[save-compat] permanent \" .. (tostring(key):gsub(\"^userdata: \", \"\"))\n");
		text.Append("\t\t\t\t\t.. \" is not defined by these scripts; its saved function is stubbed and its thread ends\")\n");
		text.Append("\t\t\tend\n");
		text.Append("\t\tend\n");
		text.Append("\t\treturn RefiiSaveStub\n");
		text.Append("\tend })\n");
		text.Append("end\n");
		return text.ToString();
	}
}
