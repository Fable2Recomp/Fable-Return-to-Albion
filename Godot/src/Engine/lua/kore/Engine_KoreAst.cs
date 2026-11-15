using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Refii.Lua.Kore;

/// <summary>
/// The syntax tree the Kore decompiler builds, and the literal rules it prints with. The design is
/// the Fable 2 decompiler's (Fable2/src/lua/src/tools/luadec): Kore's compiler is Lua 5.1's
/// parser and code generator with a different instruction encoding (proven: the 111 shipped
/// scripts with a plain-text twin recompile byte-for-byte through that model), so the structure
/// rules carry over unchanged once the instructions are decoded to Lua 5.1 operations.
/// </summary>
internal static class Engine_KoreAst
{
	public const int FieldsPerFlush = 50;

	public enum ExprKind
	{
		Nil, True, False, Number, String, Vararg, Local, Upvalue, Global, Index,
		Call, MethodCall, Function, Table, Binary, Unary, And, Or, Not, Compare,
		Paren, Multi,
	}

	public enum StmtKind
	{
		Local, Assign, Call, Return, Break, If, While, Repeat, NumFor, GenFor, Do,
		LocalFunction, FunctionDecl, Continue,
	}

	public struct TableField
	{
		/// <summary>Null for positional items.</summary>
		public Expr? Key;
		public Expr? Value;
		/// <summary>Line of the instruction that stored the field.</summary>
		public int Line;
		/// <summary>Source order: the store for hash fields, the load for items.</summary>
		public int Pc;
	}

	public sealed class Expr
	{
		public ExprKind Kind = ExprKind.Nil;
		/// <summary>Local/upvalue/global name, method name, string value (one char per byte).</summary>
		public string Name = string.Empty;
		public double Number;
		/// <summary>Binary lhs / index object / call function / unary operand / method object.</summary>
		public Expr? A;
		/// <summary>Binary rhs / index key.</summary>
		public Expr? B;
		public List<Expr> Args = new();
		public List<TableField> Fields = new();
		/// <summary>Constructor still collecting fields.</summary>
		public bool TableOpen;
		/// <summary>Positional items already committed.</summary>
		public int TableArrayCount;
		/// <summary>NEWTABLE B (decoded): positional items in the constructor.</summary>
		public int ArrayBudget;
		/// <summary>NEWTABLE C (decoded): hash fields in the constructor.</summary>
		public int HashBudget;
		public Engine_KoreOpcodes.Lua51Op Op = Engine_KoreOpcodes.Lua51Op.ADD;
		/// <summary>Compare: EQ becomes ~=, LT/LE become not (...).</summary>
		public bool Negate;
		/// <summary>Compare: the source wrote `b &gt; a` for LT a b.</summary>
		public bool Swapped;
		/// <summary>Call/vararg: -1 = multiple results.</summary>
		public int NResults = 1;
		/// <summary>Line of the producing instruction.</summary>
		public int Line;
		/// <summary>Producing instruction, -1 for locals and inline constants.</summary>
		public int Pc = -1;
		/// <summary>Constant index of a literal or of a global's name.</summary>
		public int KIndex = -1;
		/// <summary>Call: line of ')'; table: line of '}'.</summary>
		public int EndLine;
		/// <summary>Call/vararg truncated to one value by parentheses.</summary>
		public bool ParenCall;
		/// <summary>A Not that came from an OP_NOT instruction.</summary>
		public bool ExplicitNot;
		/// <summary>And/or: line of the operator token.</summary>
		public int OpLine;
		/// <summary>Method call: line of the SELF (the `:name` token).</summary>
		public int NameLine;
		/// <summary>Chain leaf: line of the TEST/TESTSET that consumed it.</summary>
		public int TestLine;
		public FunctionAst? Func;

		/// <summary>The C++ copy constructor: a new node with its own argument and field lists.</summary>
		public Expr Clone()
		{
			var copy = (Expr)MemberwiseClone();
			copy.Args = new List<Expr>(Args);
			copy.Fields = new List<TableField>(Fields);
			return copy;
		}
	}

	public sealed class Stmt
	{
		public StmtKind Kind = StmtKind.Call;
		/// <summary>Line of the first token, 0 = unconstrained.</summary>
		public int Line;
		/// <summary>for/while/if: line of the keyword's instruction hint.</summary>
		public int KeywordLine;
		/// <summary>Local names / for variables.</summary>
		public List<string> Names = new();
		/// <summary>Assignment targets.</summary>
		public List<Expr> Targets = new();
		/// <summary>Rhs / return values / call / for init.</summary>
		public List<Expr> Values = new();
		public Expr? Cond;
		public List<Stmt> Body = new();
		public List<Stmt> ElseBody = new();
		public bool HasElse;
		/// <summary>Local function / function declaration.</summary>
		public FunctionAst? Func;
		/// <summary>Function declaration target.</summary>
		public Expr? FuncName;
		/// <summary>Function declaration uses ':'.</summary>
		public bool Method;
		/// <summary>Instructions this statement covers.</summary>
		public int StartPc = -1;
		public int EndPc = -1;
		/// <summary>Local: endpc of the declared locals.</summary>
		public int ScopeEnd = -1;
		/// <summary>Line of the closing `end`/`until` when known.</summary>
		public int CloseLine;
		/// <summary>If with else: line of the then-block's escape jump.</summary>
		public int EscapeLine;
		/// <summary>If / while: line of the `then` / `do` keyword when the condition's last test pins it.</summary>
		public int ThenLine;

		public Stmt Clone()
		{
			var copy = (Stmt)MemberwiseClone();
			copy.Names = new List<string>(Names);
			copy.Targets = new List<Expr>(Targets);
			copy.Values = new List<Expr>(Values);
			copy.Body = new List<Stmt>(Body);
			copy.ElseBody = new List<Stmt>(ElseBody);
			return copy;
		}
	}

	public sealed class FunctionAst
	{
		public List<string> Params = new();
		public bool Vararg;
		public int LineDefined;
		public int LastLineDefined;
		public List<Stmt> Body = new();
		/// <summary>The name the chunk recorded for this function (format 2, unstripped), or null.</summary>
		public string? KoreName;
		/// <summary>Whether the chunk recorded function names (format 2, not stripped): then a null
		/// <see cref="KoreName"/> means the function was an expression, not a statement.</summary>
		public bool NameRecorded;
	}

	/// <summary>The structure could not be recovered; the message names the function and pc.</summary>
	public sealed class DecompileException : Exception
	{
		public DecompileException(string message) : base(message) { }
	}

	/// <summary>Thrown when inference adds a local: the function is decompiled again with it.</summary>
	public sealed class RestartInferenceException : Exception
	{
	}

	public static readonly bool TraceEnabled = Environment.GetEnvironmentVariable("REFII_KORE_TRACE") != null;

	public static void Trace(string message)
	{
		if (TraceEnabled)
		{
			Console.Error.WriteLine(message);
		}
	}

	public static Expr MakeExpr(ExprKind kind, int line) => new() { Kind = kind, Line = line };

	public static Expr MakeNot(Expr inner, int line)
	{
		if (inner.Kind == ExprKind.Not && !inner.ExplicitNot)
		{
			return inner.A!;
		}
		if (inner.Kind == ExprKind.Compare)
		{
			var copy = inner.Clone();
			copy.Negate = !copy.Negate;
			return copy;
		}
		var expr = MakeExpr(ExprKind.Not, line);
		expr.A = inner;
		return expr;
	}

	/// <summary>
	/// <paramref name="opLine"/> is the line of the operator token: a TEST is emitted when the
	/// parser consumes `and`/`or`, so that instruction's line is the operator's.
	/// </summary>
	public static Expr? MakeLogic(ExprKind kind, Expr? lhs, Expr? rhs, int opLine = 0)
	{
		if (rhs == null) return lhs;
		if (lhs == null) return rhs;
		// `a and (b and c)` and `(a and b) and c` compile identically; keep the
		// left-associative spelling, which needs no parentheses.
		if (rhs.Kind == kind)
		{
			Expr? left = MakeLogic(kind, lhs, rhs.A, opLine);
			var joined = MakeExpr(kind, rhs.Line);
			joined.A = left;
			joined.B = rhs.B;
			joined.OpLine = rhs.OpLine;
			return joined;
		}
		var expr = MakeExpr(kind, rhs.Line);
		expr.A = lhs;
		expr.B = rhs;
		expr.OpLine = opLine;
		return expr;
	}

	/// <summary>
	/// Shortest decimal that round-trips through a 4-byte float (the guest's lua_Number).
	/// Integral values print without a fraction.
	/// </summary>
	public static string FormatNumber(double value)
	{
		if (double.IsNaN(value)) return "(0/0)";
		if (double.IsInfinity(value)) return value > 0 ? "math.huge" : "-math.huge";
		float asFloat = (float)value;
		if ((double)asFloat == value && Math.Floor(value) == value && Math.Abs(value) < 1e15)
		{
			return value.ToString("F0", CultureInfo.InvariantCulture);
		}
		for (int precision = 1; precision <= 17; ++precision)
		{
			string text = FormatG(value, precision);
			double parsed = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
			if ((float)parsed == asFloat && (double)asFloat == value) return text;
			if (parsed == value) return text;
		}
		return FormatG(value, 17);
	}

	/// <summary>C's <c>%.*g</c>: <paramref name="precision"/> significant digits, exponent form
	/// when the exponent is below -4 or at least the precision, trailing zeros removed.</summary>
	public static string FormatG(double value, int precision)
	{
		if (value == 0) return value.ToString(CultureInfo.InvariantCulture).StartsWith("-", StringComparison.Ordinal) ? "-0" : "0";
		string e = value.ToString("E" + (precision - 1).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
		int ePos = e.IndexOf('E');
		int exponent = int.Parse(e.AsSpan(ePos + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
		if (exponent < -4 || exponent >= precision)
		{
			string mantissa = TrimZeros(e.Substring(0, ePos));
			string sign = exponent < 0 ? "-" : "+";
			int abs = Math.Abs(exponent);
			return mantissa + "e" + sign + (abs < 10 ? "0" : "") + abs.ToString(CultureInfo.InvariantCulture);
		}
		int decimals = Math.Max(0, precision - 1 - exponent);
		return TrimZeros(value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));
	}

	private static string TrimZeros(string text)
	{
		if (text.IndexOf('.') < 0) return text;
		text = text.TrimEnd('0');
		return text.EndsWith(".", StringComparison.Ordinal) ? text[..^1] : text;
	}

	/// <summary>
	/// A Lua string literal for bytes held one char per byte. Bytes that form valid UTF-8 are
	/// written as the characters they encode (so a UTF-8 save gives the same bytes back); any
	/// other byte at or above 0x80 is a decimal escape.
	/// </summary>
	public static string QuoteString(string value)
	{
		var bytes = new byte[value.Length];
		for (int i = 0; i < value.Length; i++) bytes[i] = (byte)value[i];
		var outText = new StringBuilder("\"");
		int pos = 0;
		while (pos < bytes.Length)
		{
			byte ch = bytes[pos];
			if (ch >= 0x80)
			{
				int len = Utf8SequenceLength(bytes, pos);
				if (len > 0)
				{
					outText.Append(Encoding.UTF8.GetString(bytes, pos, len));
					pos += len;
				}
				else
				{
					bool digitFollows = pos + 1 < bytes.Length && bytes[pos + 1] >= '0' && bytes[pos + 1] <= '9';
					outText.Append('\\').Append(digitFollows ? ch.ToString("D3", CultureInfo.InvariantCulture) : ch.ToString(CultureInfo.InvariantCulture));
					pos++;
				}
				continue;
			}
			switch (ch)
			{
				case (byte)'"': outText.Append("\\\""); break;
				case (byte)'\\': outText.Append("\\\\"); break;
				case (byte)'\n': outText.Append("\\n"); break;
				case (byte)'\r': outText.Append("\\r"); break;
				case (byte)'\t': outText.Append("\\t"); break;
				case 7: outText.Append("\\a"); break;
				case 8: outText.Append("\\b"); break;
				case 12: outText.Append("\\f"); break;
				case 11: outText.Append("\\v"); break;
				case 0:
				{
					bool digitFollows = pos + 1 < bytes.Length && bytes[pos + 1] >= '0' && bytes[pos + 1] <= '9';
					outText.Append(digitFollows ? "\\000" : "\\0");
					break;
				}
				default:
					if (ch < 0x20 || ch == 0x7F)
					{
						bool digitFollows = pos + 1 < bytes.Length && bytes[pos + 1] >= '0' && bytes[pos + 1] <= '9';
						outText.Append('\\').Append(digitFollows ? ch.ToString("D3", CultureInfo.InvariantCulture) : ch.ToString(CultureInfo.InvariantCulture));
					}
					else
					{
						outText.Append((char)ch);
					}
					break;
			}
			pos++;
		}
		outText.Append('"');
		return outText.ToString();
	}

	/// <summary>Length of a well-formed UTF-8 sequence starting at <paramref name="pos"/>, or 0.</summary>
	private static int Utf8SequenceLength(byte[] b, int pos)
	{
		byte lead = b[pos];
		int len = lead >= 0xF0 && lead <= 0xF4 ? 4 : lead >= 0xE0 ? 3 : lead >= 0xC2 && lead <= 0xDF ? 2 : 0;
		if (len == 0 || pos + len > b.Length) return 0;
		for (int i = 1; i < len; i++)
		{
			if ((b[pos + i] & 0xC0) != 0x80) return 0;
		}
		uint cp = len switch
		{
			2 => (uint)(((lead & 0x1F) << 6) | (b[pos + 1] & 0x3F)),
			3 => (uint)(((lead & 0x0F) << 12) | ((b[pos + 1] & 0x3F) << 6) | (b[pos + 2] & 0x3F)),
			_ => (uint)(((lead & 0x07) << 18) | ((b[pos + 1] & 0x3F) << 12) | ((b[pos + 2] & 0x3F) << 6) | (b[pos + 3] & 0x3F)),
		};
		if ((len == 3 && cp < 0x800) || (len == 4 && (cp < 0x10000 || cp > 0x10FFFF)) || (cp >= 0xD800 && cp <= 0xDFFF)) return 0;
		return len;
	}

	private static readonly string[] Keywords =
	{
		"and", "break", "do", "else", "elseif", "end", "false", "for", "function", "if", "in",
		"local", "nil", "not", "or", "repeat", "return", "then", "true", "until", "while",
		// Kore's own reserved words (the lexer's token table adds them).
		"kstructure", "kmake",
	};

	public static bool IsIdentifier(string name)
	{
		if (string.IsNullOrEmpty(name)) return false;
		if (!(char.IsAsciiLetter(name[0]) || name[0] == '_')) return false;
		foreach (char ch in name)
		{
			if (!(char.IsAsciiLetterOrDigit(ch) || ch == '_')) return false;
		}
		return Array.IndexOf(Keywords, name) < 0;
	}

	public static int FloatingByteToInt(int x)
	{
		int e = (x >> 3) & 31;
		if (e == 0) return x;
		return ((x & 7) + 8) << (e - 1);
	}

	public static bool IsTestOp(Engine_KoreOpcodes.Lua51Op op) =>
		op is Engine_KoreOpcodes.Lua51Op.EQ or Engine_KoreOpcodes.Lua51Op.LT or Engine_KoreOpcodes.Lua51Op.LE
			or Engine_KoreOpcodes.Lua51Op.TEST or Engine_KoreOpcodes.Lua51Op.TESTSET;
}
