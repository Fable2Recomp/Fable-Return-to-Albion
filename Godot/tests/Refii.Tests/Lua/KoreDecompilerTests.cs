namespace Refii.Tests.Lua;

using System;
using System.Collections.Generic;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Lua.Kore;
using static Refii.Lua.Kore.Engine_KoreOpcodes;
using Proto = Refii.Lua.Kore.Engine_KoreChunk.Proto;
using Constant = Refii.Lua.Kore.Engine_KoreChunk.Constant;
using LocVar = Refii.Lua.Kore.Engine_KoreChunk.LocVar;

/// <summary>
/// The Kore (Fable 3 Lua) chunk reader, opcode decoder and decompiler on tiny hand-assembled
/// chunks. Words are built with the encoder that re-encodes all 1,490 shipped chunks
/// identically, so these chunks have the shape the game's compiler gives them. No game data
/// and no Godot type is touched.
/// </summary>
[TestSuite]
public class KoreDecompilerTests
{
	private static Instr I(Lua51Op op, int a = 0, int b = 0, int c = 0) => new() { Op = op, A = a, B = b, C = c };

	private static Instr Bx(Lua51Op op, int a, int bx) => new() { Op = op, A = a, Bx = bx };

	private static Instr Jmp(Lua51Op op, int a, int sbx) => new() { Op = op, A = a, SBx = sbx };

	private static Constant S(string s) => new() { Type = Constant.Kind.String, String = s };

	private static Constant N(double n) => new() { Type = Constant.Kind.Number, Number = n };

	/// <summary>A function from Lua 5.1 operations, encoded as the Kore compiler does (including _R1).</summary>
	private static Proto Fn(Instr[] code, Constant[] k, int maxStack, int numParams = 0, int isVararg = 0,
		Proto[]? protos = null, int[]? lines = null, LocVar[]? locals = null, string? source = null, string? name = null)
	{
		var p = new Proto
		{
			Source = source,
			Name = name,
			NumParams = numParams,
			IsVararg = isVararg,
			MaxStackSize = maxStack,
			Constants = new List<Constant>(k),
			Protos = new List<Proto>(protos ?? Array.Empty<Proto>()),
			LineInfo = lines ?? Array.Empty<int>(),
			LocVars = new List<LocVar>(locals ?? Array.Empty<LocVar>()),
		};
		p.Code = EncodeFunction(code, i => p.Constants[i].Type == Constant.Kind.String, i => p.Protos[i].Nups);
		return p;
	}

	private static string Decompile(byte[] chunk)
	{
		bool ok = Engine_KoreDecompiler.TryDecompile(chunk, out string source, out string error);
		AssertThat(error).IsEqual(string.Empty);
		AssertThat(ok).IsTrue();
		return source;
	}

	/// <summary>Collapses whitespace so the assertions do not depend on the line layout.</summary>
	private static string Flat(string text) => string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

	[TestCase]
	public void TheHeaderIsTheOneTheLoaderChecks()
	{
		byte[] chunk = Engine_KoreChunk.Write(Fn(new[] { I(Lua51Op.RETURN, 0, 1) }, Array.Empty<Constant>(), 2, isVararg: 2), strip: true);
		AssertThat(BitConverter.ToString(chunk, 0, Engine_KoreChunk.HeaderSize)).IsEqual("1B-4C-75-61-51-02-01-04-04-04-04-00");
		AssertThat(Engine_KoreDecompiler.IsKoreChunk(chunk)).IsTrue();
		AssertThat(Engine_KoreChunk.TryRead(chunk, out var parsed, out _)).IsTrue();
		AssertThat(parsed.IsKore).IsTrue();
		AssertThat(parsed.Main.Code.Length).IsEqual(1);
	}

	[TestCase]
	public void TruncatedOrForeignBytesAreRejected()
	{
		byte[] chunk = Engine_KoreChunk.Write(Fn(new[] { I(Lua51Op.RETURN, 0, 1) }, Array.Empty<Constant>(), 2, isVararg: 2), strip: true);
		AssertThat(Engine_KoreChunk.TryRead(chunk[..^1], out _, out string error)).IsFalse();
		AssertThat(error.Length > 0).IsTrue();
		AssertThat(Engine_KoreDecompiler.TryDecompile(new byte[] { (byte)'x', (byte)'=', (byte)'1' }, out _, out _)).IsFalse();
		byte[] wrongFormat = (byte[])chunk.Clone();
		wrongFormat[5] = 1;
		AssertThat(Engine_KoreChunk.TryRead(wrongFormat, out _, out _)).IsFalse();
	}

	/// <summary>Field layout from the VM fetch: op 7 | B 8 | C 9 | A 8; GETFIELD's 8-bit C is a constant index.</summary>
	[TestCase]
	public void FieldsDecodeAsTheVmReadsThem()
	{
		// GETFIELD A=3 B=1 C=7  ->  op 0, B<<17, C<<8, A.
		uint getfield = (1u << 17) | (7u << 8) | 3u;
		Instr g = Decode(getfield, 0);
		AssertThat(g.Op).IsEqual(Lua51Op.GETTABLE);
		AssertThat(g.A).IsEqual(3);
		AssertThat(g.B).IsEqual(1);
		AssertThat(g.C).IsEqual(RkBit + 7);

		// ADD_BK (35): the opcode's low bit is B's constant flag; C's bit 8 is its own.
		uint add = (35u << 25) | (2u << 17) | ((uint)(RkBit + 4) << 8) | 5u;
		Instr x = Decode(add, 0);
		AssertThat(x.Op).IsEqual(Lua51Op.ADD);
		AssertThat(x.B).IsEqual(RkBit + 2);
		AssertThat(x.C).IsEqual(RkBit + 4);

		// JMP sBx: Bx - 0xFFFF.
		uint jmp = (28u << 25) | ((uint)(0xFFFF - 3) << 8);
		AssertThat(Decode(jmp, 0).SBx).IsEqual(-3);

		// The _R1 forms decode to the plain operation with the register field intact.
		uint testR1 = (65u << 25) | (1u << 8) | 4u;
		Instr t = Decode(testR1, 0);
		AssertThat(t.Op).IsEqual(Lua51Op.TEST);
		AssertThat(t.A).IsEqual(4);
		AssertThat(t.C).IsEqual(1);
	}

	/// <summary>
	/// The compiler's _R1 pass: GETFIELD on the register the GETGLOBAL just produced, then the
	/// call on the register that GETFIELD produced (`Debug.Break()`).
	/// </summary>
	[TestCase]
	public void TheEncoderAppliesR1AfterAProducer()
	{
		Proto p = Fn(new[]
		{
			Bx(Lua51Op.GETGLOBAL, 0, 0),
			I(Lua51Op.GETTABLE, 0, 0, RkBit + 1),
			I(Lua51Op.CALL, 0, 1, 1),
			I(Lua51Op.RETURN, 0, 1),
		}, new[] { S("Debug"), S("Break") }, 2, isVararg: 2);
		AssertThat(NameOf(OpOf(p.Code[0]))).IsEqual("GETGLOBAL");
		AssertThat(NameOf(OpOf(p.Code[1]))).IsEqual("GETFIELD_R1");
		AssertThat(NameOf(OpOf(p.Code[2]))).IsEqual("CALL_I_R1");
		AssertThat(NameOf(OpOf(p.Code[3]))).IsEqual("RETURN");
	}

	[TestCase]
	public void AGlobalAssignmentAndACall()
	{
		// x = 1; print("hi", x)
		Proto main = Fn(new[]
		{
			Bx(Lua51Op.LOADK, 0, 1),
			Bx(Lua51Op.SETGLOBAL, 0, 0),
			Bx(Lua51Op.GETGLOBAL, 0, 2),
			Bx(Lua51Op.LOADK, 1, 3),
			Bx(Lua51Op.GETGLOBAL, 2, 0),
			I(Lua51Op.CALL, 0, 3, 1),
			I(Lua51Op.RETURN, 0, 1),
		}, new[] { S("x"), N(1), S("print"), S("hi") }, 3, isVararg: 2);
		string text = Flat(Decompile(Engine_KoreChunk.Write(main, strip: true)));
		AssertThat(text).IsEqual("x = 1 print(\"hi\", x)");
	}

	[TestCase]
	public void AnIfWithATableField()
	{
		// if Config.Enabled then Config.Count = Config.Count + 1 end
		Proto main = Fn(new[]
		{
			Bx(Lua51Op.GETGLOBAL, 0, 0),
			I(Lua51Op.GETTABLE, 0, 0, RkBit + 1),
			I(Lua51Op.TEST, 0, 0, 0),
			Jmp(Lua51Op.JMP, 0, 5),
			Bx(Lua51Op.GETGLOBAL, 0, 0),
			Bx(Lua51Op.GETGLOBAL, 1, 0),
			I(Lua51Op.GETTABLE, 1, 1, RkBit + 2),
			I(Lua51Op.ADD, 1, 1, RkBit + 3),
			I(Lua51Op.SETTABLE, 0, RkBit + 2, 1),
			I(Lua51Op.RETURN, 0, 1),
		}, new[] { S("Config"), S("Enabled"), S("Count"), N(1) }, 2, isVararg: 2);
		AssertThat(NameOf(OpOf(main.Code[2]))).IsEqual("TEST_R1");
		string text = Flat(Decompile(Engine_KoreChunk.Write(main, strip: true)));
		AssertThat(text).IsEqual("if Config.Enabled then Config.Count = Config.Count + 1 end");
	}

	[TestCase]
	public void ANumericForLoop()
	{
		// for i = 1, 3 do print(i) end
		Proto main = Fn(new[]
		{
			Bx(Lua51Op.LOADK, 0, 0),
			Bx(Lua51Op.LOADK, 1, 1),
			Bx(Lua51Op.LOADK, 2, 0),
			Jmp(Lua51Op.FORPREP, 0, 3),
			Bx(Lua51Op.GETGLOBAL, 4, 2),
			I(Lua51Op.MOVE, 5, 3),
			I(Lua51Op.CALL, 4, 2, 1),
			Jmp(Lua51Op.FORLOOP, 0, -4),
			I(Lua51Op.RETURN, 0, 1),
		}, new[] { N(1), N(3), S("print") }, 6, isVararg: 2);
		string text = Flat(Decompile(Engine_KoreChunk.Write(main, strip: true)));
		AssertThat(text).IsEqual("for i = 1, 3 do print(i) end");
	}

	/// <summary>
	/// `function Foo:Bar() end`, as a debug chunk: the recorded function name decides the
	/// declaration form, so the same code prints three ways.
	/// </summary>
	private static byte[] MethodChunk(string? name)
	{
		Proto child = Fn(new[] { I(Lua51Op.RETURN, 0, 1) }, Array.Empty<Constant>(), 2, numParams: 1,
			lines: new[] { 1 }, locals: new[] { new LocVar("self", 0, 1) }, source: "@t.lua", name: name);
		child.LineDefined = 1;
		child.LastLineDefined = 1;
		Proto main = Fn(new[]
		{
			Bx(Lua51Op.GETGLOBAL, 0, 0),
			Bx(Lua51Op.CLOSURE, 1, 0),
			I(Lua51Op.SETTABLE, 0, RkBit + 1, 1),
			I(Lua51Op.RETURN, 0, 1),
		}, new[] { S("Foo"), S("Bar") }, 2, isVararg: 2, protos: new[] { child },
			lines: new[] { 1, 1, 1, 1 }, source: "@t.lua", name: "(main chunk)");
		return Engine_KoreChunk.Write(main);
	}

	[TestCase]
	public void TheRecordedNameDecidesTheDeclarationForm()
	{
		AssertThat(Flat(Decompile(MethodChunk("Foo:Bar")))).IsEqual("function Foo:Bar() end");
		AssertThat(Flat(Decompile(MethodChunk("Foo.Bar")))).IsEqual("function Foo.Bar(self) end");
		AssertThat(Flat(Decompile(MethodChunk(null)))).IsEqual("Foo.Bar = function(self) end");
	}

	[TestCase]
	public void TheDisassemblerNamesKoreOpcodes()
	{
		AssertThat(Engine_KoreDecompiler.TryDisassemble(MethodChunk("Foo:Bar"), out string listing, out string error)).IsTrue();
		AssertThat(error).IsEqual(string.Empty);
		AssertThat(listing.Contains("GETGLOBAL", StringComparison.Ordinal)).IsTrue();
		AssertThat(listing.Contains("SETFIELD", StringComparison.Ordinal)).IsTrue();
		AssertThat(listing.Contains("\"Foo:Bar\"", StringComparison.Ordinal)).IsTrue();
	}

	/// <summary>The structure opcodes have no Lua 5.1 form: the decompiler says so instead of guessing.</summary>
	[TestCase]
	public void AStructureOpcodeIsRefused()
	{
		Proto main = Fn(new[] { I(Lua51Op.RETURN, 0, 1) }, Array.Empty<Constant>(), 2, isVararg: 2);
		main.Code = new uint[] { 69u << 25, main.Code[0] };
		byte[] chunk = Engine_KoreChunk.Write(main, strip: true);
		AssertThat(Engine_KoreDecompiler.TryDecompile(chunk, out _, out string error)).IsFalse();
		AssertThat(error.Length > 0).IsTrue();
	}

	private static Constant Nil() => new() { Type = Constant.Kind.Nil };

	/// <summary>An and/or value that a comparison of the same chain consumes is an operand, not a leaf.</summary>
	[TestCase]
	public void AnAndOrValueInsideAComparison()
	{
		// if x == nil or (x.T or 1) == 1 then return true end
		Proto main = Fn(new[]
		{
			Bx(Lua51Op.GETGLOBAL, 0, 0),
			I(Lua51Op.EQ, 1, 0, RkBit + 1),
			Jmp(Lua51Op.JMP, 0, 7),
			Bx(Lua51Op.GETGLOBAL, 0, 0),
			I(Lua51Op.GETTABLE, 0, 0, RkBit + 2),
			I(Lua51Op.TEST, 0, 0, 1),
			Jmp(Lua51Op.JMP, 0, 1),
			Bx(Lua51Op.LOADK, 0, 3),
			I(Lua51Op.EQ, 0, 0, RkBit + 3),
			Jmp(Lua51Op.JMP, 0, 2),
			I(Lua51Op.LOADBOOL, 0, 1, 0),
			I(Lua51Op.RETURN, 0, 2),
			I(Lua51Op.RETURN, 0, 1),
		}, new[] { S("x"), Nil(), S("T"), N(1) }, 2, isVararg: 2);
		string text = Flat(Decompile(Engine_KoreChunk.Write(main, strip: true)));
		AssertThat(text).IsEqual("if x == nil or (x.T or 1) == 1 then return true end");
	}

	/// <summary>`if false` is a bare jump; its dead body ends in the escape over the else body.</summary>
	[TestCase]
	public void IfFalseWithAnElse()
	{
		// if false then f() else g() end
		Proto main = Fn(new[]
		{
			Jmp(Lua51Op.JMP, 0, 3),
			Bx(Lua51Op.GETGLOBAL, 0, 0),
			I(Lua51Op.CALL, 0, 1, 1),
			Jmp(Lua51Op.JMP, 0, 2),
			Bx(Lua51Op.GETGLOBAL, 0, 1),
			I(Lua51Op.CALL, 0, 1, 1),
			I(Lua51Op.RETURN, 0, 1),
		}, new[] { S("f"), S("g") }, 2, isVararg: 2);
		string text = Flat(Decompile(Engine_KoreChunk.Write(main, strip: true)));
		AssertThat(text).IsEqual("if false then f() else g() end");
	}

	/// <summary>Values beyond the targets are evaluated into the registers above and dropped.</summary>
	[TestCase]
	public void AnAssignmentWithAnExtraValue()
	{
		// t.a = "x", y
		Proto main = Fn(new[]
		{
			Bx(Lua51Op.GETGLOBAL, 0, 0),
			Bx(Lua51Op.LOADK, 1, 2),
			Bx(Lua51Op.GETGLOBAL, 2, 3),
			I(Lua51Op.SETTABLE, 0, RkBit + 1, 1),
			I(Lua51Op.RETURN, 0, 1),
		}, new[] { S("t"), S("a"), S("x"), S("y") }, 3, isVararg: 2);
		string text = Flat(Decompile(Engine_KoreChunk.Write(main, strip: true)));
		AssertThat(text).IsEqual("t.a = \"x\", y");
	}

	/// <summary>A value chain whose leaves jump to both halves of the LOADBOOL pair.</summary>
	[TestCase]
	public void AValueChainUsingBothBooleanLabels()
	{
		// return a ~= b or c or not d and e
		Proto main = Fn(new[]
		{
			Bx(Lua51Op.GETGLOBAL, 0, 0),
			Bx(Lua51Op.GETGLOBAL, 1, 1),
			I(Lua51Op.EQ, 0, 0, 1),
			Jmp(Lua51Op.JMP, 0, 9),
			Bx(Lua51Op.GETGLOBAL, 0, 2),
			I(Lua51Op.TEST, 0, 0, 1),
			Jmp(Lua51Op.JMP, 0, 7),
			Bx(Lua51Op.GETGLOBAL, 0, 3),
			I(Lua51Op.TEST, 0, 0, 1),
			Jmp(Lua51Op.JMP, 0, 2),
			Bx(Lua51Op.GETGLOBAL, 0, 4),
			Jmp(Lua51Op.JMP, 0, 2),
			I(Lua51Op.LOADBOOL, 0, 0, 1),
			I(Lua51Op.LOADBOOL, 0, 1, 0),
			I(Lua51Op.RETURN, 0, 2),
			I(Lua51Op.RETURN, 0, 1),
		}, new[] { S("a"), S("b"), S("c"), S("d"), S("e") }, 2, isVararg: 2);
		string text = Flat(Decompile(Engine_KoreChunk.Write(main, strip: true)));
		AssertThat(text).IsEqual("return a ~= b or c or not d and e");
	}

	/// <summary>
	/// Stripped: a test never reads a materialised and/or value (luaK_goiftrue keeps the jumps),
	/// so the register tested again is a local.
	/// </summary>
	[TestCase]
	public void ATestedAndOrValueIsALocal()
	{
		// local v = a or b; v = v or c; f(v)
		Proto main = Fn(new[]
		{
			Bx(Lua51Op.GETGLOBAL, 0, 0),
			I(Lua51Op.TEST, 0, 0, 1),
			Jmp(Lua51Op.JMP, 0, 1),
			Bx(Lua51Op.GETGLOBAL, 0, 1),
			I(Lua51Op.TEST, 0, 0, 1),
			Jmp(Lua51Op.JMP, 0, 1),
			Bx(Lua51Op.GETGLOBAL, 0, 2),
			Bx(Lua51Op.GETGLOBAL, 1, 3),
			I(Lua51Op.MOVE, 2, 0),
			I(Lua51Op.CALL, 1, 2, 1),
			I(Lua51Op.RETURN, 0, 1),
		}, new[] { S("a"), S("b"), S("c"), S("f") }, 3, isVararg: 2);
		string text = Flat(Decompile(Engine_KoreChunk.Write(main, strip: true)));
		AssertThat(text).IsEqual("local v0 = a or b if not v0 then v0 = c end f(v0)");
	}

	/// <summary>
	/// Stripped: a store's key constant is created when its target is parsed. One created after
	/// the value's own constants means the value was parsed first, into a local.
	/// </summary>
	[TestCase]
	public void AKeyCreatedAfterTheValueMeansALocal()
	{
		// local s = g; local i = f(); s.k = i
		Proto main = Fn(new[]
		{
			Bx(Lua51Op.GETGLOBAL, 0, 0),
			Bx(Lua51Op.GETGLOBAL, 1, 1),
			I(Lua51Op.CALL, 1, 1, 2),
			I(Lua51Op.SETTABLE, 0, RkBit + 2, 1),
			I(Lua51Op.RETURN, 0, 1),
		}, new[] { S("g"), S("f"), S("k") }, 2, isVararg: 2);
		string text = Flat(Decompile(Engine_KoreChunk.Write(main, strip: true)));
		AssertThat(text).IsEqual("local g_2 = g local f_2 = f() g_2.k = f_2");
	}

	/// <summary>Strings keep their bytes: valid UTF-8 prints as text, any other high byte as an escape.</summary>
	[TestCase]
	public void StringBytesRoundTrip()
	{
		string utf8 = Engine_KoreChunk.FromBytes(new byte[] { 0x63, 0x61, 0x66, 0xC3, 0xA9 });
		string latin = Engine_KoreChunk.FromBytes(new byte[] { 0x63, 0x61, 0x66, 0xE9, 0x31 });
		Proto main = Fn(new[]
		{
			Bx(Lua51Op.LOADK, 0, 1),
			Bx(Lua51Op.SETGLOBAL, 0, 0),
			Bx(Lua51Op.LOADK, 0, 3),
			Bx(Lua51Op.SETGLOBAL, 0, 2),
			I(Lua51Op.RETURN, 0, 1),
		}, new[] { S("a"), S(utf8), S("b"), S(latin) }, 2, isVararg: 2);
		string text = Flat(Decompile(Engine_KoreChunk.Write(main, strip: true)));
		AssertThat(text).IsEqual("a = \"café\" b = \"caf\\2331\"");
	}
}
