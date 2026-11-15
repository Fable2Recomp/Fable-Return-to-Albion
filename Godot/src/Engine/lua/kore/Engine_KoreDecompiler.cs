using System;
using System.Collections.Generic;
using static Refii.Lua.Kore.Engine_KoreAst;
using Proto = Refii.Lua.Kore.Engine_KoreChunk.Proto;
using Constant = Refii.Lua.Kore.Engine_KoreChunk.Constant;

namespace Refii.Lua.Kore;

/// <summary>
/// Decompiles Fable 3's shipped Lua (Kore chunks, format 2) to source the Kore compiler accepts.
///
/// <para>Kore's compiler is Lua 5.1's parser and code generator; only the encoding differs
/// (<see cref="Engine_KoreOpcodes"/>). Every instruction is decoded to the Lua 5.1 operation it
/// performs and the Fable 2 decompiler's structure, scope and line-layout rules
/// (Fable2/src/lua/src/tools/luadec, exact on all Fable 2 banks) run on that, ported to C#. With
/// line info present the text is laid out on the original lines so it recompiles to the same
/// line table; stripped chunks get their locals by inference.</para>
///
/// <para>Kore records the name of each function statement. When the chunk carries it, the
/// declaration form follows it: <c>function a.b:c()</c> when it names the function,
/// <c>a.b.c = function(self)</c> when it does not.</para>
///
/// <para>Pure .NET: nothing here touches Godot or the disk.</para>
/// </summary>
public static class Engine_KoreDecompiler
{
	/// <summary>Whether the bytes are a compiled Lua chunk rather than source text.</summary>
	public static bool IsChunk(byte[] bytes) => bytes != null && Engine_KoreChunk.IsChunk(bytes);

	/// <summary>Whether the bytes are a chunk the Kore VM loads (format 2).</summary>
	public static bool IsKoreChunk(byte[] bytes) => IsChunk(bytes) && bytes.Length > 5 && bytes[5] == 2;

	/// <summary>
	/// Decompiles <paramref name="chunk"/>. False when it cannot be read or its structure cannot be
	/// recovered; <paramref name="error"/> then names the function and pc.
	/// </summary>
	public static bool TryDecompile(byte[] chunk, out string source, out string error) =>
		TryDecompile(chunk, lineLayout: true, out source, out error);

	/// <summary>
	/// <paramref name="lineLayout"/>: put each token on the line the compiler recorded for it (so
	/// the recompiled line table matches); false gives a plain layout.
	/// </summary>
	public static bool TryDecompile(byte[] chunk, bool lineLayout, out string source, out string error)
	{
		source = string.Empty;
		if (!Engine_KoreChunk.TryRead(chunk, out var parsed, out error))
		{
			return false;
		}
		return TryDecompile(parsed, lineLayout, out source, out error);
	}

	public static bool TryDecompile(Engine_KoreChunk chunk, bool lineLayout, out string source, out string error)
	{
		source = string.Empty;
		error = string.Empty;
		try
		{
			var reserved = new SortedSet<string>(StringComparer.Ordinal);
			CollectReservedNames(chunk.Main, reserved);
			FunctionAst ast = Engine_KoreFunctionDecompiler.RunInferring(chunk.Main, new List<string>(), "main", reserved);
			var printer = new Engine_KorePrinter(lineLayout, "\t");
			printer.PrintChunk(ast);
			source = printer.Finish();
			return true;
		}
		catch (DecompileException e)
		{
			error = e.Message;
			return false;
		}
		catch (Exception e) when (e is not OutOfMemoryException)
		{
			error = "internal: " + e.GetType().Name + ": " + e.Message;
			return false;
		}
	}

	/// <summary>The instruction listing of a chunk, for one that will not decompile.</summary>
	public static bool TryDisassemble(byte[] chunk, out string text, out string error) =>
		Engine_KoreDisassembler.TryDisassemble(chunk, out text, out error);

	/// <summary>
	/// Every identifier-like string constant in the chunk: a synthetic local must not shadow a
	/// global or field name the code refers to.
	/// </summary>
	private static void CollectReservedNames(Proto proto, SortedSet<string> names)
	{
		foreach (Constant k in proto.Constants)
		{
			if (k.Type == Constant.Kind.String && IsIdentifier(k.String)) names.Add(k.String);
		}
		foreach (Proto child in proto.Protos) CollectReservedNames(child, names);
	}
}
