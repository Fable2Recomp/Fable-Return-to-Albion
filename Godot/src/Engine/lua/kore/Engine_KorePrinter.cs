// Ported from Fable2/src/lua/src/tools/luadec/luadec.cpp (class Printer).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using static Refii.Lua.Kore.Engine_KoreAst;
using static Refii.Lua.Kore.Engine_KoreOpcodes;

namespace Refii.Lua.Kore;

/// <summary>
/// Prints a decompiled function tree as Kore source. With the line layout on, every token is
/// emitted on the line the original compiler recorded for its instruction, so the recompiled
/// lineinfo matches (luadec.cpp's Printer, ported line by line).
/// </summary>
internal sealed class Engine_KorePrinter
{
	// C++ DecompileOptions{line_layout, indent}.
	private readonly bool _lineLayout;
	private readonly string _indent;

	private readonly StringBuilder _out = new();
	// out_.back() / out_.empty(): tracked on every append (StringBuilder indexing is not O(1)).
	private char _outBack;
	private bool _lateIndex = false;
	private int _curLine = 1;
	private int _depth = 0;
	private bool _atLineStart = true;
	/// <summary>lastlinedefined of the open functions.</summary>
	private readonly List<int> _lineLimits = new();
	/// <summary>Line (exclusive) by which the open block must be done, 0 = none.</summary>
	private readonly List<int> _deadlines = new();
	/// <summary>A parenless call puts its argument's '{' on the CALL line.</summary>
	private int _braceLineOverride = 0;

	public Engine_KorePrinter(bool lineLayout, string indent)
	{
		_lineLayout = lineLayout;
		_indent = indent;
	}

	public string Finish()
	{
		Append("\n");
		return _out.ToString();
	}

	public void PrintChunk(FunctionAst ast)
	{
		PrintBlock(ast.Body, 0, 0);
	}

	private void Append(string text)
	{
		if (text.Length == 0) return;
		_out.Append(text);
		_outBack = text[^1];
	}

	// Emits `text` at `line` (0 = wherever we are). Line moves are monotone.
	private void Tok(int line, string text, bool space = true)
	{
		if (_lineLayout && line > _curLine)
		{
			while (_curLine < line)
			{
				Append("\n");
				++_curLine;
			}
			_atLineStart = true;
		}
		if (_atLineStart)
		{
			for (int i = 0; i < _depth; ++i) Append(_indent);
			_atLineStart = false;
		}
		else if (space && _out.Length != 0 && _outBack != '\n' && _outBack != '(' &&
				 _outBack != '[' && _outBack != '{' && _outBack != '.' &&
				 _outBack != ':' && _outBack != '#' && _outBack != '\t')
		{
			Append(" ");
		}
		Append(text);
	}

	private void Newline()
	{
		if (!_atLineStart)
		{
			Append("\n");
			++_curLine;
			_atLineStart = true;
		}
	}

	// Ensures the next token starts a fresh line unless the layout forces it
	// to stay on the current one.
	private void Break(int nextLine)
	{
		if (_lineLayout && _lineLimits.Count != 0 && _lineLimits[^1] > 0 &&
			_curLine >= _lineLimits[^1])
		{
			// The enclosing function ends on this line: nothing below it may
			// move down.
			return;
		}
		if (!_lineLayout || nextLine == 0 || nextLine > _curLine)
		{
			Newline();
		}
	}

	// A closing keyword whose line is known stays on the current line when
	// that is its line; otherwise it opens a new one.
	private void CloseKeyword(int closeLine, int followingLine, string word)
	{
		if (_lineLayout && closeLine > 0 && closeLine <= _curLine)
		{
			Tok(0, word);
			return;
		}
		Break(followingLine);
		Tok(closeLine, word);
	}

	private static int Prec(Expr e)
	{
		switch (e.Kind)
		{
			case ExprKind.Or: return 1;
			case ExprKind.And: return 2;
			case ExprKind.Compare: return 3;
			case ExprKind.Binary:
				switch (e.Op)
				{
					case Lua51Op.CONCAT: return 4;
					case Lua51Op.ADD: case Lua51Op.SUB: return 5;
					case Lua51Op.MUL: case Lua51Op.DIV: case Lua51Op.MOD: return 6;
					case Lua51Op.POW: return 8;
					default: return 6;
				}
			case ExprKind.Unary:
			case ExprKind.Not:
				return 7;
			default:
				return 9;
		}
	}

	private static bool RightAssoc(Expr e)
	{
		return e.Kind == ExprKind.Binary && (e.Op == Lua51Op.CONCAT || e.Op == Lua51Op.POW);
	}

	private void PrintExpr(Expr e, int parentPrec = 0, bool rightSide = false)
	{
		int prec = Prec(e);
		bool parens = false;
		if (prec < 9 && parentPrec != 0)
		{
			if (prec < parentPrec) parens = true;
			else if (prec == parentPrec)
			{
				// same precedence: parenthesise the side that associativity does not cover
				parens = rightSide != RightAssoc(e);
				if (e.Kind == ExprKind.Unary || e.Kind == ExprKind.Not) parens = false;
			}
		}
		if (parens) Tok(FirstLineOf(e), "(");
		PrintExprInner(e, parens ? 0 : parentPrec);
		if (parens) Tok((e.Kind == ExprKind.And || e.Kind == ExprKind.Or) ? e.EndLine : 0, ")", false);
	}

	private static int FirstLineOf(Expr e)
	{
		switch (e.Kind)
		{
			case ExprKind.Compare:
				if (e.Negate && e.Op != Lua51Op.EQ) return e.Line;
				return FirstLineOf(e.Swapped ? e.B! : e.A!);
			case ExprKind.Index: case ExprKind.Call: case ExprKind.MethodCall: case ExprKind.Binary:
			case ExprKind.And: case ExprKind.Or: case ExprKind.Paren:
			case ExprKind.Unary: case ExprKind.Not:
				return e.A != null ? FirstLineOf(e.A) : e.Line;
			case ExprKind.Function:
				return e.Func != null ? e.Func.LineDefined : e.Line;
			default:
				return e.Line;
		}
	}

	private void PrintExprInner(Expr e, int parentPrec)
	{
		switch (e.Kind)
		{
			case ExprKind.Multi: PrintExpr(e.A!, parentPrec); break;  // collapsed by its assignment
			case ExprKind.Nil: Tok(e.Line, "nil"); break;
			case ExprKind.True: Tok(e.Line, "true"); break;
			case ExprKind.False: Tok(e.Line, "false"); break;
			case ExprKind.Number:
			{
				string text = FormatNumber(e.Number);
				Tok(e.Line, text);
				break;
			}
			case ExprKind.String: Tok(e.Line, QuoteString(e.Name)); break;
			case ExprKind.Vararg:
				if (e.ParenCall)
				{
					Tok(e.Line, "(");
					Tok(e.Line, "...", false);
					Tok(0, ")", false);
				}
				else
				{
					Tok(e.Line, "...");
				}
				break;
			case ExprKind.Local:
			case ExprKind.Upvalue:
			case ExprKind.Global:
				Tok(e.Line, e.Name);
				break;
			case ExprKind.Index:
			{
				bool late = _lateIndex;
				PrintPrefix(e.A!);
				_lateIndex = false;
				int keyLine = late ? 0 : (e.B!.Line != 0 ? e.B.Line : e.Line);
				if (e.B!.Kind == ExprKind.String && IsIdentifier(e.B.Name))
				{
					// The dot stays on the object's line: a constructor item
					// is looked ahead one token, so a dot on the next line
					// would advance the object's own line.
					Tok(0, ".", false);
					Tok(keyLine, e.B.Name, false);
				}
				else
				{
					Tok(keyLine, "[", false);
					PrintExpr(e.B);
					Tok(0, "]", false);
				}
				break;
			}
			case ExprKind.Call:
			case ExprKind.MethodCall:
			{
				if (e.ParenCall) Tok(FirstLineOf(e), "(");
				_lateIndex = false;
				PrintPrefix(e.A!);
				if (e.Kind == ExprKind.MethodCall)
				{
					int nameLine = e.NameLine != 0 ? e.NameLine : e.Line;
					Tok(nameLine, ":", false);
					Tok(nameLine, e.Name, false);
				}
				bool braceCall = e.Args.Count == 1 && e.Args[0].Kind == ExprKind.Table &&
								 e.Args[0].Line > 0 && e.Line > e.Args[0].Line;
				if (braceCall)
				{
					// `name\n{...}`: the CALL takes the line of the `{`, the
					// NEWTABLE that of the token before it.
					_braceLineOverride = e.Line;
					// The `}` closes the call too: the consumer's line, when later, is its line.
					Expr braceTable = e.Args[0];
					if (e.EndLine > braceTable.EndLine)
					{
						braceTable = braceTable.Clone();
						braceTable.EndLine = e.EndLine;
					}
					PrintExpr(braceTable);
					_braceLineOverride = 0;
					if (e.ParenCall) Tok(0, ")", false);
					break;
				}
				Tok(e.Line, "(", false);
				for (int i = 0; i < e.Args.Count; ++i)
				{
					// VARARG is coded before `...` is read: the separator before it carries its line.
					if (i != 0) Tok(e.Args[i].Kind == ExprKind.Vararg ? e.Args[i].Line : 0, ",", false);
					_lateIndex = i + 1 == e.Args.Count;
					PrintExpr(e.Args[i]);
					_lateIndex = false;
				}
				Tok(e.EndLine, ")", false);
				if (e.ParenCall) Tok(0, ")", false);
				break;
			}
			case ExprKind.Function:
				PrintFunction(e.Func!, e.Func!.LineDefined, "function", true);
				break;
			case ExprKind.Table:
			{
				// NEWTABLE carries the line of the token before '{'; a nested
				// constructor as the first item pins where '{' itself sits.
				int braceLine = e.Line;
				// Copy (C++ copies the vector; TableField is a struct).
				List<TableField> fields = new List<TableField>(e.Fields);
				StableSort(fields, (x, y) => x.Pc < y.Pc);
				if (fields.Count != 0 && fields[0].Key == null && fields[0].Value!.Kind == ExprKind.Table &&
					fields[0].Value!.Line > braceLine)
				{
					braceLine = fields[0].Value!.Line;
				}
				if (_braceLineOverride > braceLine) braceLine = _braceLineOverride;
				_braceLineOverride = 0;
				MoveTo(e.Line);
				Tok(braceLine, "{");
				for (int i = 0; i < fields.Count; ++i)
				{
					TableField field = fields[i];
					bool last = i + 1 == fields.Count;
					// A nested constructor's NEWTABLE carries the line of the
					// token before its '{': the separator. A positional item is
					// discharged when the separator after it is consumed, so
					// its instruction line is that separator's line.
					int sepLine = (field.Key == null && field.Value!.Kind == ExprKind.Table) ? field.Value.Line : 0;
					if (i != 0 && fields[i - 1].Key == null && sepLine == 0)
					{
						Expr prev = fields[i - 1].Value!;
						ExprKind pk = prev.Kind;
						if (pk == ExprKind.Index || pk == ExprKind.Global || pk == ExprKind.Local || pk == ExprKind.Upvalue ||
							pk == ExprKind.String || pk == ExprKind.Number || pk == ExprKind.Nil || pk == ExprKind.True ||
							pk == ExprKind.False)
						{
							sepLine = prev.Line;
						}
					}
					if (i != 0) Tok(sepLine, ",", false);
					if (field.Key != null)
					{
						if (field.Key.Kind == ExprKind.String && IsIdentifier(field.Key.Name))
						{
							Tok(FirstLineOf(field.Value!) != 0 ? Math.Min(FirstLineOf(field.Value!), field.Line) : field.Line,
								field.Key.Name);
						}
						else
						{
							// A constant key loads nothing: like a name key, it sits no
							// later than the value's first instruction.
							bool constKey = field.Key.Kind == ExprKind.String || field.Key.Kind == ExprKind.Number ||
											field.Key.Kind == ExprKind.True || field.Key.Kind == ExprKind.False;
							int keyLine = field.Key.Line;
							if (constKey && FirstLineOf(field.Value!) != 0 && (keyLine == 0 || FirstLineOf(field.Value!) < keyLine))
							{
								keyLine = FirstLineOf(field.Value!);
							}
							Tok(keyLine, "[");
							if (constKey)
							{
								Expr key = field.Key.Clone();
								key.Line = 0;
								PrintExpr(key);
							}
							else
							{
								PrintExpr(field.Key);
							}
							Tok(0, "]", false);
						}
						Tok(0, "=");
						PrintExpr(field.Value!);
					}
					else if (last && (field.Value!.Kind == ExprKind.Call ||
									  field.Value.Kind == ExprKind.MethodCall ||
									  field.Value.Kind == ExprKind.Vararg) &&
							 field.Value.NResults == 1)
					{
						Tok(FirstLineOf(field.Value), "(");
						PrintExpr(field.Value);
						Tok(0, ")", false);
					}
					else
					{
						_lateIndex = field.Key == null && !last;
						PrintExpr(field.Value!);
						_lateIndex = false;
					}
				}
				Tok(e.EndLine, "}", e.Fields.Count == 0 ? false : true);
				break;
			}
			case ExprKind.Binary:
			{
				int prec = Prec(e);
				bool late = _lateIndex;
				_lateIndex = false;
				PrintExpr(e.A!, prec, false);
				Tok(0, BinaryOpText(e.Op));
				_lateIndex = late;
				PrintExpr(e.B!, prec, true);
				break;
			}
			case ExprKind.Unary:
			{
				int prec = Prec(e);
				Tok(FirstLineOf(e), e.Op == Lua51Op.UNM ? "-" : "#");
				// `- -x` needs a space; `-x` does not.
				PrintExpr(e.A!, prec, true);
				break;
			}
			case ExprKind.Not:
			{
				Tok(FirstLineOf(e), "not");
				PrintExpr(e.A!, Prec(e), true);
				break;
			}
			case ExprKind.And:
			case ExprKind.Or:
			{
				int prec = Prec(e);
				bool late = _lateIndex;
				_lateIndex = false;
				PrintExpr(e.A!, prec, false);
				Tok(e.OpLine, e.Kind == ExprKind.And ? "and" : "or");
				_lateIndex = late;
				PrintExpr(e.B!, prec, true);
				break;
			}
			case ExprKind.Compare:
			{
				int prec = Prec(e);
				bool lateCmp = _lateIndex;
				_lateIndex = false;
				// C++ RestoreLate: late_index_ is set to false when this case's scope exits.
				try
				{
					_ = lateCmp;
					if (e.Negate && e.Op != Lua51Op.EQ)
					{
						Tok(FirstLineOf(e), "not");
						Tok(0, "(");
						if (e.Swapped)
						{
							PrintExpr(e.B!, prec, false);
							Tok(0, e.Op == Lua51Op.LT ? ">" : ">=");
							PrintExpr(e.A!, prec, true);
						}
						else
						{
							PrintExpr(e.A!, prec, false);
							Tok(0, e.Op == Lua51Op.LT ? "<" : "<=");
							PrintExpr(e.B!, prec, true);
						}
						Tok(0, ")", false);
						break;
					}
					bool swap = e.Swapped;
					if (swap)
					{
						PrintExpr(e.B!, prec, false);
						Tok(0, e.Op == Lua51Op.LT ? ">" : ">=");
						PrintExpr(e.A!, prec, true);
					}
					else
					{
						PrintExpr(e.A!, prec, false);
						Tok(0, e.Op == Lua51Op.EQ ? (e.Negate ? "~=" : "==") : (e.Op == Lua51Op.LT ? "<" : "<="));
						PrintExpr(e.B!, prec, true);
					}
				}
				finally
				{
					_lateIndex = false;
				}
				break;
			}
			case ExprKind.Paren:
				Tok(FirstLineOf(e), "(");
				PrintExpr(e.A!);
				Tok(0, ")", false);
				break;
		}
		_ = parentPrec;
	}

	// A prefix expression: parenthesised unless it is a name, index or call.
	private void PrintPrefix(Expr e)
	{
		switch (e.Kind)
		{
			case ExprKind.Index:
				PrintExpr(e);
				break;
			case ExprKind.Local: case ExprKind.Upvalue: case ExprKind.Global:
			case ExprKind.Call: case ExprKind.MethodCall: case ExprKind.Paren:
				_lateIndex = false;
				PrintExpr(e);
				break;
			default:
				Tok(FirstLineOf(e), "(");
				PrintExpr(e);
				Tok(0, ")", false);
				break;
		}
	}

	private static string BinaryOpText(Lua51Op op)
	{
		switch (op)
		{
			case Lua51Op.ADD: return "+";
			case Lua51Op.SUB: return "-";
			case Lua51Op.MUL: return "*";
			case Lua51Op.DIV: return "/";
			case Lua51Op.MOD: return "%";
			case Lua51Op.POW: return "^";
			case Lua51Op.CONCAT: return "..";
			default: return "?";
		}
	}

	private void PrintFunction(FunctionAst func, int line, string keyword,
							   bool anonymous, Expr? name = null, bool method = false,
							   string simpleName = "")
	{
		Tok(line, keyword);
		if (simpleName.Length != 0)
		{
			Tok(0, simpleName);
		}
		else if (name != null)
		{
			if (method)
			{
				PrintMethodName(name);
			}
			else
			{
				PrintExpr(name);
			}
		}
		Tok(0, "(", false);
		int firstParam = method ? 1 : 0;
		for (int i = firstParam; i < func.Params.Count; ++i)
		{
			if (i != firstParam) Tok(0, ",", false);
			Tok(0, func.Params[i], i != firstParam);
		}
		if (func.Vararg)
		{
			if (func.Params.Count > firstParam) Tok(0, ",", false);
			Tok(0, "...", func.Params.Count > firstParam);
		}
		Tok(0, ")", false);
		++_depth;
		_lineLimits.Add(func.LastLineDefined);
		_deadlines.Add(func.LastLineDefined > 0 ? func.LastLineDefined + 1 : 0);
		PrintBlock(func.Body, func.LastLineDefined, func.LastLineDefined);
		_deadlines.RemoveAt(_deadlines.Count - 1);
		_lineLimits.RemoveAt(_lineLimits.Count - 1);
		--_depth;
		Break(func.LastLineDefined);
		Tok(func.LastLineDefined, "end");
		_ = anonymous;
	}

	private void PrintMethodName(Expr name)
	{
		PrintPrefix(name.A!);
		Tok(name.B!.Line, ":", false);
		Tok(name.B.Line, name.B.Name, false);
	}

	// `next_line` is the line of whatever follows the block (0 = unknown), so
	// closing keywords can stay on the current line when the source did.
	// The earliest line a statement must reach: the header line of the first
	// function it contains, or 0.
	private static int FirstAnchorExpr(Expr? e)
	{
		if (e == null) return 0;
		if (e.Kind == ExprKind.Function && e.Func != null) return e.Func.LineDefined;
		int best = 0;
		void Take(int line)
		{
			if (line > 0 && (best == 0 || line < best)) best = line;
		}
		Take(FirstAnchorExpr(e.A));
		Take(FirstAnchorExpr(e.B));
		foreach (Expr arg in e.Args) Take(FirstAnchorExpr(arg));
		foreach (TableField f in e.Fields)
		{
			Take(FirstAnchorExpr(f.Key));
			Take(FirstAnchorExpr(f.Value));
		}
		return best;
	}

	private static int FirstAnchor(Stmt s)
	{
		int best = 0;
		void Take(int line)
		{
			if (line > 0 && (best == 0 || line < best)) best = line;
		}
		if (s.Func != null) Take(s.Func.LineDefined);
		foreach (Expr e in s.Targets) Take(FirstAnchorExpr(e));
		foreach (Expr e in s.Values) Take(FirstAnchorExpr(e));
		Take(FirstAnchorExpr(s.Cond));
		foreach (Stmt child in s.Body) Take(FirstAnchor(child));
		foreach (Stmt child in s.ElseBody) Take(FirstAnchor(child));
		return best;
	}

	private void PrintBlock(List<Stmt> body, int nextLine, int endLine)
	{
		// Statements without a recorded line each open a new one, unless the
		// lines left before the next function header (or the enclosing
		// deadline) are needed by the statements that follow.
		int n = body.Count;
		int outer = _deadlines.Count == 0 ? 0 : _deadlines[^1];
		var anchors = new int[n];
		for (int i = 0; i < n; ++i) anchors[i] = FirstAnchor(body[i]);
		for (int i = 0; i < n; ++i)
		{
			Stmt stmt = body[i];
			int following = i + 1 < n ? body[i + 1].Line : nextLine;
			int j = i;
			while (j < n && anchors[j] == 0) ++j;
			int limit = 0;  // latest line on which this statement may start
			int childDeadline = 0;
			if (j < n)
			{
				limit = anchors[j] - (j - i);
				childDeadline = j > i ? anchors[j] - (j - i - 1) : 0;
			}
			if (j == n || j == i)
			{
				if (outer > 0)
				{
					int fromOuter = outer - (n - i);
					if (limit == 0 || fromOuter < limit) limit = fromOuter;
					childDeadline = outer - (n - 1 - i);
				}
			}
			if (_lineLayout && stmt.Line == 0 && limit > 0 && _curLine + 1 > limit)
			{
				// no room: stay on this line
			}
			else
			{
				Break(stmt.Line);
			}
			_deadlines.Add(childDeadline);
			bool early = (stmt.Kind == StmtKind.Return || stmt.Kind == StmtKind.Break ||
						  stmt.Kind == StmtKind.Continue) &&
						 i + 1 < n;
			if (early)
			{
				Tok(stmt.Line, "do");
				PrintStmt(stmt, following);
				Tok(0, "end");
			}
			else
			{
				PrintStmt(stmt, following);
			}
			_deadlines.RemoveAt(_deadlines.Count - 1);
		}
		_ = endLine;
	}

	private void PrintNameList(List<string> names, int line)
	{
		for (int i = 0; i < names.Count; ++i)
		{
			if (i != 0) Tok(0, ",", false);
			Tok(line, names[i], true);
		}
	}

	private void PrintExprList(List<Expr> values, int expected = -1)
	{
		for (int i = 0; i < values.Count; ++i)
		{
			if (i != 0) Tok(values[i].Kind == ExprKind.Vararg ? values[i].Line : 0, ",", false);
			Expr value = values[i];
			bool last = i + 1 == values.Count;
			bool multretKind = value.Kind == ExprKind.Call ||
							   value.Kind == ExprKind.MethodCall ||
							   value.Kind == ExprKind.Vararg;
			if (last && multretKind && value.NResults == 1 &&
				((expected >= 0 && expected > values.Count) || expected == -2))
			{
				Tok(FirstLineOf(value), "(");
				PrintExpr(value);
				Tok(0, ")", false);
			}
			else
			{
				_lateIndex = last;
				PrintExpr(value);
				_lateIndex = false;
			}
		}
	}

	private void MoveTo(int line)
	{
		if (_lineLayout && line > _curLine)
		{
			while (_curLine < line)
			{
				Append("\n");
				++_curLine;
			}
			_atLineStart = true;
		}
	}

	private void PrintStmt(Stmt s, int followingLine)
	{
		MoveTo(s.Line);
		switch (s.Kind)
		{
			case StmtKind.Local:
				Tok(s.Line, "local");
				PrintNameList(s.Names, 0);
				if (s.Values.Count != 0)
				{
					Tok(0, "=");
					PrintExprList(s.Values, s.Names.Count);
				}
				break;
			case StmtKind.Assign:
				for (int i = 0; i < s.Targets.Count; ++i)
				{
					if (i != 0) Tok(0, ",", false);
					PrintExpr(s.Targets[i]);
				}
				Tok(0, "=");
				PrintExprList(s.Values, s.Targets.Count);
				break;
			case StmtKind.Call:
				PrintExpr(s.Values[0]);
				break;
			case StmtKind.Return:
				Tok(s.Line, "return");
				PrintExprList(s.Values, -2);
				break;
			case StmtKind.Break:
				Tok(s.Line, "break");
				break;
			case StmtKind.Continue:
				// The C++ prints `continue` here; Kore has no such statement.
				throw new DecompileException("continue is not a Kore statement");
			case StmtKind.If:
			{
				// C++ `const Stmt* current`: a reference walk down the elseif chain.
				Stmt current = s;
				Tok(s.Line, "if");
				for (;;)
				{
					PrintExpr(current.Cond!);
					Tok(current.ThenLine, "then");
					++_depth;
					int afterThen = current.HasElse
						? (current.ElseBody.Count == 0 ? followingLine : current.ElseBody[0].Line)
						: followingLine;
					PrintBlock(current.Body, afterThen, 0);
					--_depth;
					if (!current.HasElse) break;
					if (current.ElseBody.Count == 1 && current.ElseBody[0].Kind == StmtKind.If)
					{
						Break(current.ElseBody[0].Line);
						Tok(current.ElseBody[0].Line, "elseif");
						current = current.ElseBody[0];
						continue;
					}
					if (_lineLayout && current.EscapeLine > _curLine)
					{
						// the jump before `else` was coded after a token on
						// its own line: an empty statement
						Tok(current.EscapeLine, ";");
					}
					Break(current.ElseBody.Count == 0 ? followingLine : current.ElseBody[0].Line);
					Tok(0, "else");
					++_depth;
					PrintBlock(current.ElseBody, followingLine, 0);
					--_depth;
					break;
				}
				CloseKeyword(s.CloseLine, followingLine, "end");
				break;
			}
			case StmtKind.While:
				Tok(s.Line, "while");
				PrintExpr(s.Cond!);
				Tok(s.ThenLine, "do");
				++_depth;
				PrintBlock(s.Body, followingLine, 0);
				--_depth;
				CloseKeyword(s.CloseLine, followingLine, "end");
				break;
			case StmtKind.Repeat:
				Tok(s.Line, "repeat");
				++_depth;
				PrintBlock(s.Body, FirstLineOf(s.Cond!), 0);
				--_depth;
				Break(FirstLineOf(s.Cond!));
				Tok(0, "until");
				PrintExpr(s.Cond!);
				break;
			case StmtKind.NumFor:
				Tok(s.KeywordLine != 0 ? s.KeywordLine : s.Line, "for");
				PrintNameList(s.Names, 0);
				Tok(0, "=");
				PrintExpr(s.Values[0]);
				Tok(0, ",", false);
				PrintExpr(s.Values[1]);
				if (!(s.Values[2].Kind == ExprKind.Number && s.Values[2].Number == 1.0))
				{
					Tok(0, ",", false);
					PrintExpr(s.Values[2]);
				}
				Tok(0, "do");
				++_depth;
				PrintBlock(s.Body, followingLine, 0);
				--_depth;
				CloseKeyword(s.CloseLine, followingLine, "end");
				break;
			case StmtKind.GenFor:
				Tok(s.KeywordLine != 0 ? s.KeywordLine : s.Line, "for");
				PrintNameList(s.Names, 0);
				Tok(0, "in");
				PrintExprList(s.Values);
				Tok(0, "do");
				++_depth;
				PrintBlock(s.Body, followingLine, 0);
				--_depth;
				CloseKeyword(s.CloseLine, followingLine, "end");
				break;
			case StmtKind.Do:
				Tok(s.Line, "do");
				++_depth;
				PrintBlock(s.Body, followingLine, 0);
				--_depth;
				CloseKeyword(s.CloseLine, followingLine, "end");
				break;
			case StmtKind.LocalFunction:
				// `local function name(...)`: the only `local function` spelling. `local f = function`
				// is a StmtKind.Local whose value is an ExprKind.Function (printed by PrintExprInner).
				Tok(s.Line, "local");
				PrintFunction(s.Func!, s.Line, "function", false, null, false, s.Names[0]);
				break;
			case StmtKind.FunctionDecl:
				// `function a.b:c(...)` (Method: the first parameter is the implicit self and is
				// not printed) or `function a.b.c(...)`. `a.b.c = function(self)` is a
				// StmtKind.Assign whose value is an ExprKind.Function.
				PrintFunction(s.Func!, s.Line, "function", false, s.FuncName, s.Method);
				break;
		}
	}

	// std::stable_sort with the C++ less-than predicate (insertion sort: stable).
	private static void StableSort<T>(List<T> list, Func<T, T, bool> less)
	{
		for (int i = 1; i < list.Count; i++)
		{
			T item = list[i];
			int j = i - 1;
			while (j >= 0 && less(item, list[j]))
			{
				list[j + 1] = list[j];
				j--;
			}
			list[j + 1] = item;
		}
	}
}
