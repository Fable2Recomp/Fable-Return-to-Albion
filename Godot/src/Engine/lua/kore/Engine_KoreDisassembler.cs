using System.Globalization;
using System.Text;
using static Refii.Lua.Kore.Engine_KoreAst;
using Proto = Refii.Lua.Kore.Engine_KoreChunk.Proto;
using Constant = Refii.Lua.Kore.Engine_KoreChunk.Constant;

namespace Refii.Lua.Kore;

/// <summary>
/// A listing of a Kore chunk: every instruction with its Kore opcode name, its operands as the VM
/// reads them (see <see cref="Engine_KoreOpcodes"/>), resolved constants and jump targets, plus
/// each function's name, locals and constants. For a script that will not decompile.
/// </summary>
public static class Engine_KoreDisassembler
{
	public static bool TryDisassemble(byte[] chunk, out string text, out string error)
	{
		text = string.Empty;
		if (!Engine_KoreChunk.TryRead(chunk, out var parsed, out error))
		{
			return false;
		}
		text = Disassemble(parsed);
		return true;
	}

	public static string Disassemble(Engine_KoreChunk chunk)
	{
		var sb = new StringBuilder();
		sb.Append("-- format ").Append(chunk.Format).Append(chunk.IsKore ? " (Kore)" : " (stock Lua 5.1; the Kore loader rejects it)").Append('\n');
		DisassembleProto(chunk, chunk.Main, "main", sb);
		return sb.ToString();
	}

	private static string Const(Proto p, int index)
	{
		if (index < 0 || index >= p.Constants.Count) return "k" + index + "(?)";
		Constant k = p.Constants[index];
		string value = k.Type switch
		{
			Constant.Kind.Nil => "nil",
			Constant.Kind.Boolean => k.Boolean ? "true" : "false",
			Constant.Kind.Number => FormatNumber(k.Number),
			_ => QuoteString(k.String),
		};
		return "k" + index + "(" + value + ")";
	}

	private static string Rk(Proto p, int v) => v >= Engine_KoreOpcodes.RkBit ? Const(p, v - Engine_KoreOpcodes.RkBit) : "r" + v;

	private static void DisassembleProto(Engine_KoreChunk chunk, Proto p, string path, StringBuilder sb)
	{
		var ci = CultureInfo.InvariantCulture;
		sb.Append("=== ").Append(path);
		if (p.Name != null) sb.Append(" \"").Append(p.Name).Append('"');
		sb.Append(string.Format(ci, " ({0} instructions, {1} params, {2} upvalues, vararg {3}, stack {4}, lines {5}-{6})\n",
			p.Code.Length, p.NumParams, p.Nups, p.IsVararg, p.MaxStackSize, p.LineDefined, p.LastLineDefined));
		if (p.Source != null) sb.Append("  source ").Append(p.Source).Append('\n');
		foreach (var v in p.LocVars) sb.Append(string.Format(ci, "  local {0} [{1},{2})\n", v.Name, v.StartPc, v.EndPc));
		for (int i = 0; i < p.Upvalues.Count; i++) sb.Append(string.Format(ci, "  upvalue {0} {1}\n", i, p.Upvalues[i]));
		for (int i = 0; i < p.Constants.Count; i++) sb.Append("  ").Append(Const(p, i)).Append('\n');
		for (int pc = 0; pc < p.Code.Length; pc++)
		{
			uint w = p.Code[pc];
			var ins = p.Ins[pc];
			string name = chunk.IsKore ? Engine_KoreOpcodes.NameOf(ins.Raw) : ins.Op.ToString();
			string operands;
			if (pc > 0 && p.Ins[pc - 1].Op == Engine_KoreOpcodes.Lua51Op.SETLIST && p.Ins[pc - 1].C == 0)
			{
				name = "(data)";
				operands = w.ToString(ci);
			}
			else
			{
				switch (ins.Op)
				{
					case Engine_KoreOpcodes.Lua51Op.JMP:
						operands = "-> " + (pc + 1 + ins.SBx).ToString(ci); break;
					case Engine_KoreOpcodes.Lua51Op.FORLOOP: case Engine_KoreOpcodes.Lua51Op.FORPREP:
						operands = "r" + ins.A + " -> " + (pc + 1 + ins.SBx).ToString(ci); break;
					case Engine_KoreOpcodes.Lua51Op.LOADK: case Engine_KoreOpcodes.Lua51Op.GETGLOBAL: case Engine_KoreOpcodes.Lua51Op.SETGLOBAL:
						operands = "r" + ins.A + " " + Const(p, ins.Bx); break;
					case Engine_KoreOpcodes.Lua51Op.CLOSURE:
						operands = "r" + ins.A + " proto " + ins.Bx.ToString(ci); break;
					case Engine_KoreOpcodes.Lua51Op.EQ: case Engine_KoreOpcodes.Lua51Op.LT: case Engine_KoreOpcodes.Lua51Op.LE:
						operands = ins.A + " " + Rk(p, ins.B) + " " + Rk(p, ins.C); break;
					case Engine_KoreOpcodes.Lua51Op.GETTABLE: case Engine_KoreOpcodes.Lua51Op.SELF:
						operands = "r" + ins.A + " r" + ins.B + " " + Rk(p, ins.C); break;
					case Engine_KoreOpcodes.Lua51Op.SETTABLE:
					case Engine_KoreOpcodes.Lua51Op.ADD: case Engine_KoreOpcodes.Lua51Op.SUB: case Engine_KoreOpcodes.Lua51Op.MUL:
					case Engine_KoreOpcodes.Lua51Op.DIV: case Engine_KoreOpcodes.Lua51Op.MOD: case Engine_KoreOpcodes.Lua51Op.POW:
						operands = "r" + ins.A + " " + Rk(p, ins.B) + " " + Rk(p, ins.C); break;
					default:
						operands = ins.A + " " + ins.B + " " + ins.C; break;
				}
			}
			sb.Append(string.Format(ci, "  {0,4} [{1,4}] {2:X8} {3,-14} {4}\n", pc, ins.Line, w, name, operands));
		}
		for (int i = 0; i < p.Protos.Count; i++)
		{
			DisassembleProto(chunk, p.Protos[i], path + "." + i.ToString(ci), sb);
		}
	}
}
