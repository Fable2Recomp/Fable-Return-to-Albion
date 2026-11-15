// Ported from Fable2/src/lua/src/tools/luadec/luadec.cpp (class FunctionDecompiler).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using static Refii.Lua.Kore.Engine_KoreAst;
using static Refii.Lua.Kore.Engine_KoreOpcodes;
using Proto = Refii.Lua.Kore.Engine_KoreChunk.Proto;
using Constant = Refii.Lua.Kore.Engine_KoreChunk.Constant;
using LocVar = Refii.Lua.Kore.Engine_KoreChunk.LocVar;

namespace Refii.Lua.Kore;

/// <summary>
/// Recovers the statements of one Kore function. A line-for-line port of the Fable 2 decompiler's
/// FunctionDecompiler (Fable2/src/lua/src/tools/luadec/luadec.cpp), which is byte-exact on every
/// Fable 2 bank: Kore's compiler is Lua 5.1's, so the same structure, scope and line rules apply to
/// instructions decoded to Lua 5.1 operations (<see cref="Engine_KoreOpcodes.Decode"/>).
///
/// <para>Kore's differences from Lua 5.1.2, each found by recompiling the shipped scripts:
/// a function-start LOADNIL is always kept (160 debug-bank functions begin with one, none elides
/// it); every vararg function uses the compat `arg` table (is_vararg 3 or 7, never 2 below the
/// main chunk); a condition's last test is coded after `then` / `do` is read, so it carries the
/// keyword's line; and function statements record their name, which decides between
/// <c>function a.b:c()</c> and <c>a.b.c = function(self)</c>.</para>
/// </summary>
internal sealed class Engine_KoreFunctionDecompiler
{
	public enum LoopKind { While, Repeat, NumFor, GenFor }

	public struct Loop
	{
		public LoopKind Kind;
		/// <summary>First pc of the loop (condition or FORPREP/JMP).</summary>
		public int Start;
		public int BodyStart;
		/// <summary>Exclusive.</summary>
		public int BodyEnd;
		/// <summary>Pc of the closing instruction (back JMP / FORLOOP / TFORLOOP's JMP).</summary>
		public int End;
		/// <summary>Pc after the loop.</summary>
		public int Exit;
		public bool Used;
	}

	public struct Slot
	{
		public Expr? Expr;
		public int Pc;
		/// <summary>Extra result of a multi-result call/vararg held in `Expr`.</summary>
		public bool Multi;

		public Slot() { Expr = null; Pc = -1; Multi = false; }
		public Slot(Expr? expr, int pc, bool multi) { Expr = expr; Pc = pc; Multi = multi; }
	}

	public struct Leaf
	{
		/// <summary>Test instruction (or the bare jump of a constant leaf).</summary>
		public int Pc;
		/// <summary>Instructions the leaf occupies.</summary>
		public int Width;
		/// <summary>Jump target.</summary>
		public int Target;
		/// <summary>Target came from an enclosing closing jump.</summary>
		public bool Remapped;
		public bool JumpWhenTrue;
		public Lua51Op Op;
		/// <summary>TESTSET destination (or TEST register).</summary>
		public int Reg;
		/// <summary>Condition in positive form.</summary>
		public Expr? Cond;
		/// <summary>TESTSET, or TEST on the destination register.</summary>
		public bool ValueKeeping;

		public Leaf() { Pc = 0; Width = 2; Target = 0; Remapped = false; JumpWhenTrue = false; Op = Lua51Op.TEST; Reg = -1; Cond = null; ValueKeeping = false; }
	}

	public struct Ctx
	{
		public int LoopExit;
		public int BlockEnd;
		/// <summary>Capture writes to this register instead of emitting statements.</summary>
		public int ValueReg;

		public Ctx() { LoopExit = -1; BlockEnd = 0; ValueReg = -1; }
	}

	public struct ClosingJump
	{
		public int JmpPc;
		public int Target;
	}

	private readonly Proto _f;
	private readonly List<string> _upvalueNames;
	private readonly string _path;
	private readonly Instr[] _ins;
	/// <summary>Per pc: register -> locvar index.</summary>
	private int[][] _active = Array.Empty<int[]>();
	private SortedSet<int> _claimedLocvars = new();
	private List<Loop> _loops = new();
	private List<ClosingJump> _jumpStack = new();
	private List<LocVar> _locvars;
	private SortedSet<string> _reservedNames = new(StringComparer.Ordinal);
	/// <summary>[start, end) of enclosing blocks.</summary>
	private List<(int First, int Second)> _blockStack = new();
	/// <summary>Last expression each register held.</summary>
	private Expr?[] _lastValue;
	private Slot[] _regs;
	private bool[] _written;
	private List<int> _firstConstantUse = new();
	private Slot[] _snapshot = Array.Empty<Slot>();
	private bool _stripped;
	private bool _grouping;
	private bool _probing;
	/// <summary>GatherChain retrying a chain whose operand value nothing consumed.</summary>
	private bool _operandValuesOff;
	private int _pendingNextPc;
	/// <summary>Per open block: first pc of the statement being built.</summary>
	private List<int> _stmtStarts = new();
	/// <summary>First pc of the statement most recently completed.</summary>
	private int _lastStmtStart;
	/// <summary>While building a loop condition: its first pc.</summary>
	private int _condFloor = -1;

	public Engine_KoreFunctionDecompiler(Proto proto, List<string> upvalueNames, string path,
		List<LocVar>? inferred = null, SortedSet<string>? reservedNames = null)
	{
		_f = proto;
		_upvalueNames = upvalueNames;
		_path = path;
		_ins = (Instr[])proto.Ins.Clone();
		_locvars = new List<LocVar>(_f.LocVars);
		_stripped = _f.LineInfo.Length == 0;  // debug info stripped: locals must be inferred
		if (inferred != null)
		{
			_locvars = new List<LocVar>(inferred);
			_stripped = true;
		}
		if (reservedNames != null) _reservedNames = new SortedSet<string>(reservedNames, StringComparer.Ordinal);
		BuildScopes();
		if (_stripped && SanitiseScopes()) BuildScopes();
		BuildLoops();
		_regs = new Slot[_f.MaxStackSize + 4];
		for (int i = 0; i < _regs.Length; i++) _regs[i] = new Slot();
		_written = new bool[_regs.Length];
		_lastValue = new Expr?[_regs.Length];
	}

	/// <summary>
	/// std::stable_sort with a C++ less-than predicate: a stable insertion sort (an element moves
	/// before another only when strictly less), which is what any stable sort gives for a
	/// consistent ordering.
	/// </summary>
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

	// Scope end for a local declared by the write at `writePc`: the innermost
	// enclosing block that contains that write.
	private int ScopeEndFor(int writePc)
	{
		int end = _ins.Length;
		for (int i = _blockStack.Count - 1; i >= 0; --i)
		{
			var it = _blockStack[i];
			if (it.First <= writePc && writePc < it.Second)
			{
				end = it.Second;
				break;
			}
		}
		// The blocks built so far may not include the one this write sits in
		// (a chain still being gathered): every loop and every forward jump
		// over the write bounds a block, and no local crosses one.
		foreach (Loop loop in _loops)
		{
			if (loop.Start <= writePc && writePc <= loop.End)
			{
				int bodyEnd = loop.Kind == LoopKind.Repeat ? loop.End + 1 : loop.End;
				if (bodyEnd > writePc && bodyEnd < end) end = bodyEnd;
			}
		}
		for (int j = 0; j < writePc; ++j)
		{
			Instr ins = _ins[j];
			if (ins.Op != Lua51Op.JMP && ins.Op != Lua51Op.FORPREP) continue;
			int target = j + 1 + ins.SBx;
			if (target > writePc && target < end) end = target;
		}
		return end;
	}

	private int LastWriteBefore(int reg, int pc)
	{
		for (int p = pc - 1; p >= 0; --p)
		{
			if (InstructionWrites(p, reg)) return p;
		}
		return -1;
	}

	// Runs inference until the locals are stable, then decompiles.
	public static FunctionAst RunInferring(Proto proto, List<string> upvalueNames, string path, SortedSet<string> reserved)
	{
		if (proto.LineInfo.Length != 0)
		{
			var plain = new Engine_KoreFunctionDecompiler(proto, upvalueNames, path, null, reserved);
			return plain.Run();
		}
		List<LocVar> inferred = SeedLocals(proto, reserved);
		for (int round = 0; round < 400; ++round)
		{
			var pass = new Engine_KoreFunctionDecompiler(proto, upvalueNames, path, inferred, reserved);
			try
			{
				var ast = pass.Run();
				if (Environment.GetEnvironmentVariable("REFII_LUADEC_LOCALS") != null)
				{
					foreach (LocVar var in pass._locvars)
					{
						Console.Error.Write($"{path} local {var.Name} [{var.StartPc},{var.EndPc}) r{var.Reg}\n");
					}
				}
				return ast;
			}
			catch (RestartInferenceException)
			{
				inferred = new List<LocVar>(pass._locvars);
			}
		}
		throw new DecompileException(path + ": local inference did not converge");
	}

	public FunctionAst Run()
	{
		var ast = new FunctionAst();
		ast.LineDefined = _f.LineDefined;
		ast.KoreName = _f.Name;
		ast.NameRecorded = _f.NameRecorded;
		ast.LastLineDefined = _f.LastLineDefined;
		ast.Vararg = (_f.IsVararg & 2) != 0;
		for (int i = 0; i < _f.NumParams; ++i)
		{
			if (i < _locvars.Count)
			{
				ast.Params.Add(_locvars[i].Name);
			}
			else
			{
				ast.Params.Add("arg" + (i + 1).ToString());
			}
		}
		// Parameters, and the compat `arg` table after them, are declared by
		// the header rather than by a statement.
		int headerLocals = _f.NumParams + ((_f.IsVararg & 1) != 0 ? 1 : 0);
		for (int i = 0; i < headerLocals && i < _locvars.Count; ++i)
		{
			_claimedLocvars.Add(i);
		}
		int last = _ins.Length - 1;
		int end = _ins.Length;
		if (last >= 0 && _ins[last].Op == Lua51Op.RETURN && _ins[last].A == 0 && _ins[last].B == 1)
		{
			end = last;  // implicit return
		}
		ast.Body = BuildBlock(0, end, -1, end);
		// The main chunk's implicit return is coded at EOS: its line is the
		// chunk's last token, which closes the last statement.
		if (end == last && _f.LineDefined == 0 && !_stripped) HintEndLine(ast.Body, _ins[last].Line);
		InsertGhostConstants(ast.Body);
		return ast;
	}

	// A constant no instruction references was still mentioned in the source
	// (`if "x" then end` interns "x" and emits nothing). Mention it again just
	// before the first statement that references a later constant, so the
	// constant table comes out in the same order.
	public void InsertGhostConstants(List<Stmt> body)
	{
		var used = new bool[_f.Constants.Count];
		void Mark(int v)
		{
			if (v >= RkBit && v - RkBit < used.Length) used[v - RkBit] = true;
		}
		foreach (Instr ins in _ins)
		{
			switch (ins.Op)
			{
				case Lua51Op.LOADK: case Lua51Op.GETGLOBAL: case Lua51Op.SETGLOBAL:
					if (ins.Bx < used.Length) used[ins.Bx] = true;
					break;
				case Lua51Op.GETTABLE: case Lua51Op.SELF: Mark(ins.C); break;
				case Lua51Op.SETTABLE: case Lua51Op.ADD: case Lua51Op.SUB: case Lua51Op.MUL: case Lua51Op.DIV: case Lua51Op.MOD:
				case Lua51Op.POW: case Lua51Op.EQ: case Lua51Op.LT: case Lua51Op.LE:
					Mark(ins.B); Mark(ins.C); break;
				default: break;
			}
		}
		for (int g = 0; g < used.Length; ++g)
		{
			if (used[g]) continue;
			var mention = new Stmt();
			mention.Kind = StmtKind.If;
			mention.Cond = ConstExpr(g, 0);
			if (!InsertBefore(body, g, mention)) body.Add(mention);
		}
	}

	private static int MaxConstant(Expr? e)
	{
		if (e == null) return -1;
		int best = e.KIndex;
		foreach (var child in new[] { e.A, e.B }) best = Math.Max(best, MaxConstant(child));
		foreach (Expr arg in e.Args) best = Math.Max(best, MaxConstant(arg));
		foreach (TableField field in e.Fields)
		{
			best = Math.Max(best, MaxConstant(field.Key));
			best = Math.Max(best, MaxConstant(field.Value));
		}
		return best;
	}

	private static int MaxConstantInHead(Stmt s)
	{
		int best = -1;
		foreach (Expr e in s.Targets) best = Math.Max(best, MaxConstant(e));
		foreach (Expr e in s.Values) best = Math.Max(best, MaxConstant(e));
		best = Math.Max(best, MaxConstant(s.Cond));
		best = Math.Max(best, MaxConstant(s.FuncName));
		return best;
	}

	private static bool InsertBefore(List<Stmt> body, int ghost, Stmt mention)
	{
		for (int i = 0; i < body.Count; ++i)
		{
			Stmt s = body[i];
			if (MaxConstantInHead(s) > ghost)
			{
				body.Insert(i, mention);
				return true;
			}
			if (InsertBefore(s.Body, ghost, mention)) return true;
			if (InsertBefore(s.ElseBody, ghost, mention)) return true;
		}
		return false;
	}

	// [[noreturn]] in C++: returns the exception; every call site is `throw Fail(...)`.
	private DecompileException Fail(int pc, string what)
	{
		return new DecompileException(_path + " pc=" + pc.ToString() + " " +
			(pc >= 0 && pc < _ins.Length
				? _ins[pc].Op.ToString()
				: "-") +
			": " + what);
	}

	// ---- scopes -----------------------------------------------------------

	// _active[pc][reg] = index of the local living in `reg` at `pc`, or -1.
	private void BuildScopes()
	{
		int n = _ins.Length;
		int width = _f.MaxStackSize + 4;
		_active = new int[n + 1][];
		for (int r = 0; r < _active.Length; r++)
		{
			_active[r] = new int[width];
			Array.Fill(_active[r], -1);
		}
		for (int pc = 0; pc <= n; ++pc)
		{
			int[] row = _active[pc];
			int positional = 0;
			for (int i = 0; i < _locvars.Count; ++i)
			{
				LocVar var = _locvars[i];
				if (!(var.StartPc <= pc && pc < var.EndPc)) continue;
				int reg = var.Reg;
				if (reg < 0) reg = positional++;
				if (reg >= 0 && reg < width && row[reg] < 0)
				{
					row[reg] = i;
				}
			}
		}
	}

	// Locals every stripped function has: parameters and loop controls.
	// Locals in declaration order: by start pc, then by register when known
	// (two locals declared by one statement share a start pc).
	private static void SortLocvars(List<LocVar> vars)
	{
		StableSort(vars, (a, b) =>
		{
			if (a.StartPc != b.StartPc) return a.StartPc < b.StartPc;
			if (a.Reg < 0 || b.Reg < 0) return false;
			return a.Reg < b.Reg;
		});
	}

	private static List<LocVar> SeedLocals(Proto proto, SortedSet<string> reserved)
	{
		var vars = new List<LocVar>();
		int n = proto.Code.Length;
		var taken = new SortedSet<string>(reserved, StringComparer.Ordinal);
		string Fresh(string baseName)
		{
			string name = baseName;
			for (int k = 2; taken.Contains(name) || !IsIdentifier(name); ++k) name = baseName + "_" + k.ToString();
			taken.Add(name);
			return name;
		}
		for (int i = 0; i < proto.NumParams; ++i)
		{
			vars.Add(new LocVar(Fresh(i == 0 ? "self" : "arg" + i.ToString()), 0, n, i));
		}
		if ((proto.IsVararg & 1) != 0)
		{
			// VARARG_HASARG (LUA_COMPAT_VARARG): the function holds the `arg`
			// table in the register after its parameters.
			vars.Add(new LocVar("arg", 0, n, proto.NumParams));
		}
		for (int pc = 0; pc < n; ++pc)
		{
			Instr ins = proto.Ins[pc];
			if (ins.Op == Lua51Op.FORPREP)
			{
				int end = pc + 1 + ins.SBx;
				vars.Add(new LocVar("(for index)", pc, end + 1, ins.A));
				vars.Add(new LocVar("(for limit)", pc, end + 1, ins.A + 1));
				vars.Add(new LocVar("(for step)", pc, end + 1, ins.A + 2));
				vars.Add(new LocVar(Fresh("i"), pc + 1, end, ins.A + 3));
			}
			else if (ins.Op == Lua51Op.JMP)
			{
				int target = pc + 1 + ins.SBx;
				bool paired = pc > 0 && IsTestOp(proto.Ins[pc - 1].Op);
				if (!paired && target > pc && target + 1 < n)
				{
					Instr t = proto.Ins[target];
					Instr back = proto.Ins[target + 1];
					if (t.Op == Lua51Op.TFORLOOP && back.Op == Lua51Op.JMP && target + 2 + back.SBx == pc + 1)
					{
						vars.Add(new LocVar("(for generator)", pc, target + 2, t.A));
						vars.Add(new LocVar("(for state)", pc, target + 2, t.A + 1));
						vars.Add(new LocVar("(for control)", pc, target + 2, t.A + 2));
						for (int k = 0; k < t.C; ++k)
						{
							vars.Add(new LocVar(Fresh(k == 0 ? "k" : k == 1 ? "v" : "v" + k.ToString()), pc + 1, target, t.A + 3 + k));
						}
					}
				}
			}
		}
		foreach (ref LocVar var in CollectionsMarshal.AsSpan(vars)) var.Seeded = true;
		SortLocvars(vars);
		return vars;
	}

	// Declares register `reg` as a local from the instruction that last wrote
	// it before `pc`, then restarts the pass.
	// [[noreturn]] in C++: always throws; every call site is `throw DeclareInferredLocal(...)`.
	private Exception DeclareInferredLocal(string why, int reg, int pc, int scopeEnd, Expr? hint, int knownWrite = -1)
	{
		if (_probing)
		{
			// Speculative operand evaluation must not decide scopes: end the
			// probe here and let the real block build infer with its stack.
			throw new DecompileException("probe needs inference");
		}
		int writePc = -1;
		int innerWrite = -1;
		// The latest write may complete an and/or value expression whose
		// leaves each wrote the register: then the local starts after it.
		int latest = knownWrite >= 0 ? knownWrite : LastWriteBefore(reg, pc);
		int chainStart = -1;
		for (;;)
		{
			bool elided = false;
			chainStart = latest >= 0 ? ValueChainStart(latest, reg, out elided) : -1;
			if (chainStart < 0 || !elided) break;
			// An assignment chain to an existing local: its declaration lies
			// before it, and the chain is at most the statement just built.
			if (_lastStmtStart <= latest && _lastStmtStart > chainStart) chainStart = _lastStmtStart;
			latest = LastWriteBefore(reg, chainStart);
			knownWrite = -1;
		}
		bool chained = chainStart >= 0 && !IsInsideClosedStructure(chainStart, pc);
		if (TraceEnabled) Trace($"{_path}   declare r{reg} use={pc} known={knownWrite} latest={latest} chain_start={chainStart} chained={(chained ? 1 : 0)} last_stmt={_lastStmtStart}");
		// A value pending at a block's end may have been declared inside that
		// block: that reading is taken only when no enclosing write exists.
		bool innerOk = knownWrite >= 0;
		if (chained)
		{
			writePc = latest;
		}
		else
		{
			for (int p = pc - 1; p >= 0; --p)
			{
				if (!InstructionWrites(p, reg) || !CanDeclare(p) || FrameBelowBetween(p, pc, reg)) continue;
				// A write inside a block that closed before this use was an
				// assignment; the declaration is an earlier write outside, or
				// absent (then the local is hoisted to the function start).
				if (IsInsideClosedStructure(p, pc))
				{
					// The last load of an and/or value sits behind the value's own
					// jumps: the value, not a closed block, ends at this write.
					int valueStart = ValueChainStart(p, reg);
					if (valueStart >= 0 && !IsInsideClosedStructure(valueStart, pc))
					{
						writePc = p;
						chained = true;
						chainStart = valueStart;
						break;
					}
					if (innerWrite < 0) innerWrite = p;
					continue;
				}
				writePc = p;
				break;
			}
			if (writePc >= 0) innerOk = false;
			if (writePc < 0 && innerOk) writePc = knownWrite;
			if (writePc < 0 && innerWrite < 0) throw Fail(pc, "cannot find the declaration of register " + reg.ToString());
			if (writePc < 0) writePc = innerWrite;
		}
		scopeEnd = Math.Max(scopeEnd, ScopeEndFor(chained ? chainStart : writePc));
		int startpc = writePc + 1;
		if (!chained && !innerOk && IsInsideClosedStructure(writePc, pc))
		{
			// Declared inside a block that closed before this use: the source
			// declared it earlier without an initialiser. Only a declaration
			// at the function start emits nothing (LOADNIL is elided at pc 0).
			foreach (LocVar existing in _locvars)
			{
				if (existing.StartPc == 0 && existing.Reg == reg)
				{
					throw Fail(pc, "register " + reg.ToString() + " already hoisted");
				}
			}
			// Retract the block-scoped declarations inference made for this
			// register: they were assignments to the hoisted local.
			string name = string.Empty;
			foreach (LocVar existing in _locvars)
			{
				if (existing.Reg == reg && existing.StartPc > 0 && name.Length == 0) name = existing.Name;
			}
			// C++ `v.name[0]` on an empty std::string reads the terminator ('\0' != '(').
			_locvars.RemoveAll(v => v.Reg == reg && v.StartPc > 0 && (v.Name.Length == 0 || v.Name[0] != '('));
			var hoisted = new LocVar();
			hoisted.Name = name.Length == 0 ? NameForLocal(hint, reg) : name;
			hoisted.StartPc = 0;
			hoisted.EndPc = _ins.Length;
			hoisted.Reg = reg;
			_locvars.Add(hoisted);
			SortLocvars(_locvars);
			if (TraceEnabled) Trace($"{_path} hoist local {hoisted.Name} r{reg} to the function start");
			throw new RestartInferenceException();
		}
		if (_ins[writePc].Op == Lua51Op.CLOSURE) startpc += _f.Protos[_ins[writePc].Bx].Nups;
		if (_ins[writePc].Op == Lua51Op.NEWTABLE)
		{
			// A constructor's fields are stored before the local exists; the
			// NEWTABLE size hints say how many fields the constructor holds.
			int hashBudget = FloatingByteToInt(_ins[writePc].C);
			int arrayBudget = FloatingByteToInt(_ins[writePc].B);
			int hashCount = 0;
			int arrayCount = 0;
			int p = writePc + 1;
			int lastStore = writePc;
			while (p < _ins.Length)
			{
				Instr ins = _ins[p];
				if (ins.Op == Lua51Op.SETTABLE && ins.A == reg)
				{
					if (hashCount >= hashBudget) break;
					++hashCount;
					lastStore = p;
				}
				else if (ins.Op == Lua51Op.SETLIST && ins.A == reg)
				{
					if (arrayCount >= arrayBudget && arrayBudget > 0) break;
					arrayCount += ins.B == 0 ? arrayBudget - arrayCount : ins.B;
					lastStore = p;
					if (ins.C == 0) ++p;
				}
				else if ((ins.Op == Lua51Op.SETTABLE || ins.Op == Lua51Op.SETLIST) && ins.A > reg)
				{
					// a nested constructor above the table: part of a field value
				}
				else if (IsTestOp(ins.Op) && ins.Op != Lua51Op.EQ && ins.Op != Lua51Op.LT && ins.Op != Lua51Op.LE &&
					ins.A > reg)
				{
					// an and/or chain building a field value above the table
				}
				else if (ins.Op == Lua51Op.JMP && ins.SBx > 0 && ConstructorResumesAt(p + 1 + ins.SBx, reg))
				{
					// the chain's exit lands on the rest of the field's value or its store
				}
				else if (!IsPureOperand(p) || InstructionWrites(p, reg) || IsOperandOf(p, reg))
				{
					break;
				}
				++p;
			}
			startpc = lastStore + 1;
		}
		{
			Span<LocVar> span = CollectionsMarshal.AsSpan(_locvars);
			for (int i = 0; i < span.Length; ++i)
			{
				ref LocVar lv = ref span[i];
				if (lv.StartPc == startpc && !lv.Seeded && RegisterOfLocvarIn(_locvars, i) == reg)
				{
					// Already declared but read past its recorded scope: extend it.
					int wanted = Math.Max(scopeEnd, EnclosingStructureEnd(pc, lv.StartPc - 1));
					if (lv.EndPc >= wanted)
					{
						throw Fail(pc, "register " + reg.ToString() + " already declared at " + startpc.ToString());
					}
					lv.EndPc = wanted;
					if (TraceEnabled) Trace($"{_path} extend local {lv.Name} r{reg} to {lv.EndPc}");
					throw new RestartInferenceException();
				}
			}
		}
		var declared = new LocVar();
		declared.Name = NameForLocal(hint, reg);
		declared.StartPc = startpc;
		declared.EndPc = Math.Max(scopeEnd, startpc);
		declared.Reg = reg;
		if (_ins[writePc].Op == Lua51Op.LOADNIL)
		{
			// `local x` codes its LOADNIL into the first free register: older
			// locals still holding it, or any register above, had ended.
			foreach (ref LocVar lv in CollectionsMarshal.AsSpan(_locvars))
			{
				if (lv.Reg < reg || lv.Seeded || lv.StartPc > writePc || lv.EndPc <= writePc) continue;
				if (TraceEnabled) Trace($"{_path} end local {lv.Name} r{reg} at {writePc}: declaration of {declared.Name}");
				lv.EndPc = writePc;
			}
		}
		// A register holds one local at a time: declarations inferred inside
		// this one's scope were assignments to it.
		_locvars.RemoveAll(v => v.Reg == reg && !v.Seeded && v.StartPc > declared.StartPc && v.EndPc <= declared.EndPc);
		_locvars.Add(declared);
		SortLocvars(_locvars);
		if (TraceEnabled) Trace($"{_path} infer local {declared.Name} r{reg} [{declared.StartPc},{declared.EndPc}) at {pc}: {why}");
		if (TraceEnabled)
		{
			// C++ `&other != &locvars_.back()`: compared by index.
			for (int i = 0; i < _locvars.Count; ++i)
			{
				LocVar other = _locvars[i];
				if (other.Reg == reg && i != _locvars.Count - 1)
				{
					if (TraceEnabled) Trace($"{_path}   (r{reg} also holds {other.Name} [{other.StartPc},{other.EndPc}))");
				}
			}
		}
		throw new RestartInferenceException();
	}

	// Whether `writePc` lies inside an if body or loop that ends before `usePc`.
	private bool IsInsideClosedStructure(int writePc, int usePc)
	{
		foreach (Loop loop in _loops)
		{
			if (loop.Start <= writePc && writePc <= loop.End && loop.End < usePc) return true;
		}
		for (int q = 0; q < writePc; ++q)
		{
			if (_ins[q].Op != Lua51Op.JMP) continue;
			int target = q + 1 + _ins[q].SBx;
			// a conditional jump over the write (then body) or an escape over it (else body)
			if (writePc < target && target <= usePc) return true;
		}
		return false;
	}

	// C++ took `const LocVar& var` and stopped at the element with the same address;
	// `index` is that element's position in `vars`.
	private static int RegisterOfLocvarIn(List<LocVar> vars, int index)
	{
		LocVar var = vars[index];
		if (var.Reg >= 0) return var.Reg;
		int reg = 0;
		for (int j = 0; j < vars.Count; ++j)
		{
			if (j == index) break;
			LocVar other = vars[j];
			if (other.StartPc <= var.StartPc && var.StartPc < other.EndPc) ++reg;
		}
		return reg;
	}

	// Whether the code at `pc` is operand loads above `reg` leading to a
	// store into the table in `reg` (a constructor field continues there).
	private bool ConstructorResumesAt(int pc, int reg)
	{
		for (int p = pc; p < _ins.Length; ++p)
		{
			Instr ins = _ins[p];
			if ((ins.Op == Lua51Op.SETTABLE || ins.Op == Lua51Op.SETLIST) && ins.A == reg) return true;
			if ((ins.Op == Lua51Op.SETTABLE || ins.Op == Lua51Op.SETLIST) && ins.A > reg) continue;  // nested field
			if (!IsOperandOp(p) || ins.A <= reg) return false;
		}
		return false;
	}

	// Whether the instruction at `pc` stores register `reg` somewhere.
	private bool IsStoreOf(int pc, int reg)
	{
		Instr ins = _ins[pc];
		switch (ins.Op)
		{
			case Lua51Op.SETTABLE: return ins.C == reg;
			case Lua51Op.SETGLOBAL: case Lua51Op.SETUPVAL: return ins.A == reg;
			case Lua51Op.MOVE: return ins.B == reg && IsLocalAt(pc, ins.A);
			default: return false;
		}
	}

	// Whether the instruction at `pc` reads register `reg`.
	private bool IsOperandOf(int pc, int reg)
	{
		Instr ins = _ins[pc];
		bool Rk(int v) => v < RkBit && v == reg;
		switch (ins.Op)
		{
			case Lua51Op.MOVE: case Lua51Op.UNM: case Lua51Op.NOT: case Lua51Op.LEN: return ins.B == reg;
			case Lua51Op.GETTABLE: return ins.B == reg || Rk(ins.C);
			case Lua51Op.SETTABLE: return ins.A == reg || Rk(ins.B) || Rk(ins.C);
			case Lua51Op.SELF: return ins.B == reg || Rk(ins.C);
			case Lua51Op.ADD: case Lua51Op.SUB: case Lua51Op.MUL: case Lua51Op.DIV: case Lua51Op.MOD: case Lua51Op.POW:
			case Lua51Op.EQ: case Lua51Op.LT: case Lua51Op.LE:
				return Rk(ins.B) || Rk(ins.C);
			case Lua51Op.CONCAT: return ins.B <= reg && reg <= ins.C;
			case Lua51Op.TEST: return ins.A == reg;
			case Lua51Op.TESTSET: return ins.B == reg;
			case Lua51Op.CALL: case Lua51Op.TAILCALL:
				return ins.B == 0 ? reg >= ins.A : (ins.A <= reg && reg < ins.A + ins.B);
			case Lua51Op.RETURN:
				return ins.B == 0 ? reg >= ins.A : (ins.A <= reg && reg < ins.A + ins.B - 1);
			case Lua51Op.SETLIST:
				return ins.B == 0 ? reg >= ins.A : (ins.A <= reg && reg <= ins.A + ins.B);
			case Lua51Op.SETGLOBAL: case Lua51Op.SETUPVAL: return ins.A == reg;
			case Lua51Op.FORPREP: case Lua51Op.FORLOOP: return ins.A <= reg && reg <= ins.A + 2;
			case Lua51Op.TFORLOOP: return ins.A <= reg && reg <= ins.A + 2;
			case Lua51Op.CLOSURE:
			{
				Proto child = _f.Protos[ins.Bx];
				for (int i = 0; i < child.Nups; ++i)
				{
					Instr bind = _ins[pc + 1 + i];
					if (bind.Op == Lua51Op.MOVE && bind.B == reg) return true;
				}
				return false;
			}
			default: return false;
		}
	}

	private bool InstructionWrites(int pc, int reg)
	{
		Instr ins = _ins[pc];
		switch (ins.Op)
		{
			case Lua51Op.MOVE: case Lua51Op.LOADK: case Lua51Op.LOADBOOL: case Lua51Op.GETUPVAL: case Lua51Op.GETGLOBAL:
			case Lua51Op.GETTABLE: case Lua51Op.NEWTABLE: case Lua51Op.ADD: case Lua51Op.SUB: case Lua51Op.MUL: case Lua51Op.DIV:
			case Lua51Op.MOD: case Lua51Op.POW: case Lua51Op.UNM: case Lua51Op.NOT: case Lua51Op.LEN: case Lua51Op.CONCAT:
			case Lua51Op.CLOSURE: case Lua51Op.TESTSET:
				return ins.A == reg;
			case Lua51Op.LOADNIL:
				return ins.A <= reg && reg <= ins.B;
			case Lua51Op.SELF:
				return ins.A == reg || ins.A + 1 == reg;
			case Lua51Op.CALL:
				return ins.C == 0 ? ins.A <= reg : (ins.A <= reg && reg < ins.A + ins.C - 1);
			case Lua51Op.VARARG:
				return ins.B == 0 ? ins.A <= reg : (ins.A <= reg && reg < ins.A + ins.B - 1);
			case Lua51Op.TFORLOOP:
				return ins.A + 3 <= reg && reg < ins.A + 3 + ins.C;
			case Lua51Op.FORLOOP: case Lua51Op.FORPREP:
				return ins.A <= reg && reg <= ins.A + 3;
			default:
				return false;
		}
	}

	private string NameForLocal(Expr? hint, int reg)
	{
		string baseName = string.Empty;
		if (hint != null)
		{
			switch (hint.Kind)
			{
				case ExprKind.Call: case ExprKind.MethodCall:
				{
					string n = hint.Kind == ExprKind.MethodCall ? hint.Name : "";
					Expr? callee = hint.A;
					if (hint.Kind == ExprKind.Call && callee != null)
					{
						if (callee.Kind == ExprKind.Index && callee.B!.Kind == ExprKind.String) n = callee.B.Name;
						else if (callee.Kind == ExprKind.Global || callee.Kind == ExprKind.Local) n = callee.Name;
					}
					if (n.StartsWith("Get", StringComparison.Ordinal) && n.Length > 3) n = n.Substring(3);
					else if (n.StartsWith("Is", StringComparison.Ordinal) && n.Length > 2) n = "is_" + n.Substring(2);
					baseName = n;
					break;
				}
				case ExprKind.Index:
					if (hint.B!.Kind == ExprKind.String) baseName = hint.B.Name;
					break;
				case ExprKind.Global: case ExprKind.Upvalue: case ExprKind.Local:
					baseName = hint.Name;
					break;
				case ExprKind.Table: baseName = "t"; break;
				case ExprKind.Function: baseName = "fn"; break;
				case ExprKind.String: baseName = "str"; break;
				case ExprKind.Number: baseName = "n"; break;
				default: break;
			}
		}
		string snake = string.Empty;
		for (int i = 0; i < baseName.Length; ++i)
		{
			char ch = baseName[i];
			if (char.IsAsciiLetterUpper(ch))
			{
				if (i > 0 && !char.IsAsciiLetterUpper(baseName[i - 1]) && baseName[i - 1] != '_') snake += '_';
				snake += char.ToLowerInvariant(ch);
			}
			else if (char.IsAsciiLetterOrDigit(ch) || ch == '_')
			{
				snake += ch;
			}
		}
		if (snake.Length == 0 || !IsIdentifier(snake)) snake = "v" + reg.ToString();
		var taken = new SortedSet<string>(_reservedNames, StringComparer.Ordinal);
		foreach (LocVar var in _locvars) taken.Add(var.Name);
		string name = snake;
		for (int k = 2; taken.Contains(name) || !IsIdentifier(name); ++k) name = snake + "_" + k.ToString();
		return name;
	}

	private int LocalIndexAt(int pc, int reg)
	{
		if (pc < 0) pc = 0;
		if (pc > _ins.Length) pc = _ins.Length;
		int[] row = _active[pc];
		if (reg < 0 || reg >= row.Length) return -1;
		return row[reg];
	}

	private bool IsLocalAt(int pc, int reg) { return LocalIndexAt(pc, reg) >= 0; }

	private string LocalNameAt(int pc, int reg)
	{
		int index = LocalIndexAt(pc, reg);
		if (index >= 0) return _locvars[index].Name;
		return "r" + reg.ToString();
	}

	// Locvars that begin exactly at `pc` and are not parameters / loop internals.
	private List<int> LocalsStartingAt(int pc)
	{
		var result = new List<int>();
		for (int i = 0; i < _locvars.Count; ++i)
		{
			LocVar var = _locvars[i];
			if (var.StartPc == pc && var.EndPc >= var.StartPc &&
				!_claimedLocvars.Contains(i) &&
				!(var.Name.Length > 1 && var.Name[0] == '('))
			{
				result.Add(i);
			}
		}
		return result;
	}

	// Locals are stack-allocated: a local's register is the number of locals
	// declared before it that are still live where it starts.
	private int RegisterOfLocvar(int index)
	{
		LocVar var = _locvars[index];
		if (var.Reg >= 0) return var.Reg;
		int reg = 0;
		bool emptyScope = var.EndPc <= var.StartPc;
		for (int j = 0; j < index; ++j)
		{
			LocVar other = _locvars[j];
			if (other.StartPc <= var.StartPc && var.StartPc < other.EndPc)
			{
				++reg;
			}
			else if (emptyScope && other.StartPc < var.StartPc && other.EndPc == var.StartPc)
			{
				++reg;  // ends together with this declaration at the block's end
			}
			else if (other.StartPc == var.StartPc && other.EndPc == other.StartPc)
			{
				++reg;  // empty-scope sibling declared just before
			}
		}
		return reg;
	}

	// ---- loops ------------------------------------------------------------

	private void BuildLoops()
	{
		int n = _ins.Length;
		for (int pc = 0; pc < n; ++pc)
		{
			Instr instr = _ins[pc];
			if (instr.Op == Lua51Op.FORPREP)
			{
				var loop = new Loop();
				loop.Kind = LoopKind.NumFor;
				loop.Start = pc;
				loop.BodyStart = pc + 1;
				loop.End = pc + 1 + instr.SBx;
				loop.BodyEnd = loop.End;
				loop.Exit = loop.End + 1;
				_loops.Add(loop);
			}
			else if (instr.Op == Lua51Op.JMP)
			{
				int target = pc + 1 + instr.SBx;
				if (target > pc && target < n && _ins[target].Op == Lua51Op.TFORLOOP &&
					target + 1 < n && _ins[target + 1].Op == Lua51Op.JMP &&
					target + 2 + _ins[target + 1].SBx == pc + 1)
				{
					var loop = new Loop();
					loop.Kind = LoopKind.GenFor;
					loop.Start = pc;
					loop.BodyStart = pc + 1;
					loop.BodyEnd = target;
					loop.End = target + 1;
					loop.Exit = target + 2;
					_loops.Add(loop);
				}
				else if (target <= pc)
				{
					// Skip the JMP that closes a generic for (recognised above).
					if (pc > 0 && _ins[pc - 1].Op == Lua51Op.TFORLOOP) continue;
					bool merged = false;
					foreach (ref Loop existing in CollectionsMarshal.AsSpan(_loops))
					{
						if ((existing.Kind == LoopKind.While || existing.Kind == LoopKind.Repeat) &&
							existing.Start == target)
						{
							existing.End = Math.Max(existing.End, pc);
							existing.Exit = existing.End + 1;
							merged = true;
						}
					}
					if (!merged)
					{
						var loop = new Loop();
						loop.Start = target;
						loop.End = pc;
						loop.Exit = pc + 1;
						_loops.Add(loop);
					}
				}
			}
		}
		// Nested loops sharing a start pc (`while true do while true do`): an
		// inner back-jump is a loop end when some bare forward jump lands right
		// after it (a break out of the inner loop).
		{
			var extra = new List<Loop>();
			foreach (Loop loop in _loops)
			{
				if (loop.Kind != LoopKind.While) continue;
				for (int e = loop.Start; e < loop.End; ++e)
				{
					if (_ins[e].Op != Lua51Op.JMP || e + 1 + _ins[e].SBx != loop.Start || IsPairedJump(e)) continue;
					bool exitTarget = false;
					for (int p = loop.Start; p < e; ++p)
					{
						if (_ins[p].Op == Lua51Op.JMP && !IsPairedJump(p) && p + 1 + _ins[p].SBx == e + 1) exitTarget = true;
					}
					if (!exitTarget) continue;
					var inner = new Loop();
					inner.Start = loop.Start;
					inner.End = e;
					inner.Exit = e + 1;
					extra.Add(inner);
				}
			}
			_loops.AddRange(extra);
		}
		foreach (ref Loop loop in CollectionsMarshal.AsSpan(_loops))
		{
			if (loop.Kind == LoopKind.While)
			{
				// repeat: the closing backward jump is the paired jump of a test.
				if (loop.End > 0 && IsTestOp(_ins[loop.End - 1].Op))
				{
					loop.Kind = LoopKind.Repeat;
				}
				loop.BodyStart = loop.Start;
				loop.BodyEnd = loop.End;
			}
		}
	}

	private bool LoopStartsAt(int pc)
	{
		foreach (Loop loop in _loops)
		{
			if (loop.Start == pc && !loop.Used) return true;
		}
		return false;
	}

	// C++ returned a Loop*; here the index into _loops, -1 for nullptr.
	private int FindLoopStartingAt(int pc, int blockEnd)
	{
		int best = -1;
		for (int i = 0; i < _loops.Count; ++i)
		{
			Loop loop = _loops[i];
			if (loop.Start != pc || loop.Used || loop.Exit > blockEnd + 1) continue;
			if (loop.Exit > blockEnd) continue;
			if (best == -1 || loop.End > _loops[best].End) best = i;
		}
		return best;
	}

	// ---- registers --------------------------------------------------------

	private Expr ConstExpr(int index, int line)
	{
		if (index < 0 || index >= _f.Constants.Count)
		{
			throw Fail(-1, "constant index out of range");
		}
		Constant constant = _f.Constants[index];
		Expr? expr = null;
		switch (constant.Type)
		{
			case Constant.Kind.Nil: expr = MakeExpr(ExprKind.Nil, line); break;
			case Constant.Kind.Boolean:
				expr = MakeExpr(constant.Boolean ? ExprKind.True : ExprKind.False, line);
				break;
			case Constant.Kind.Number:
				expr = MakeExpr(ExprKind.Number, line);
				expr.Number = constant.Number;
				break;
			case Constant.Kind.String:
				expr = MakeExpr(ExprKind.String, line);
				expr.Name = constant.String;
				break;
		}
		return expr!;
	}

	private Expr RegExpr(int reg, int pc)
	{
		if (IsLocalAt(pc, reg))
		{
			var local = MakeExpr(ExprKind.Local, _ins[pc].Line);
			local.Name = LocalNameAt(pc, reg);
			return local;
		}
		ref Slot slot = ref _regs[reg];
		if (slot.Expr == null)
		{
			if (!_written[reg])
			{
				// Never written: the compiler skipped the LOADNIL that would
				// have been the function's first instruction.
				return MakeExpr(ExprKind.Nil, _ins[pc].Line);
			}
			if (_stripped)
			{
				// The value outlived its statement: it is a local.
				int writePc = LastWriteBefore(reg, pc);
				throw DeclareInferredLocal("outlived", reg, pc, ScopeEndFor(writePc), _lastValue[reg]);
			}
			throw Fail(pc, "read of register " + reg.ToString() + " with no value");
		}
		if (_stripped && !slot.Multi && _blockStack.Count != 0 && slot.Pc < _blockStack[^1].First)
		{
			// A temporary does not cross a block boundary: this value was
			// written before the block that reads it began.
			throw DeclareInferredLocal("block-cross", reg, pc, ScopeEndFor(slot.Pc), slot.Expr, slot.Pc);
		}
		if (_stripped && !slot.Multi && _condFloor >= 0 && slot.Pc < _condFloor)
		{
			// A loop condition runs every iteration: a value from before the
			// loop that it reads was a local.
			throw DeclareInferredLocal("loop-cond", reg, pc, ScopeEndFor(slot.Pc), slot.Expr, slot.Pc);
		}
		bool storeTarget = _ins[pc].Op == Lua51Op.SETTABLE && (_ins[pc].A == reg || _ins[pc].B == reg);
		if (_stripped && !slot.Multi && !storeTarget)
		{
			// Temporaries are a stack: consuming `reg` while an unrelated
			// pending value sits above it means `reg` outlived a statement.
			// (A store's table and key are evaluated before the values of a
			// multiple assignment and legitimately sit below them.)
			int extraTop = ExtraValuesSource(pc, out int extraCount) == reg ? reg + extraCount : reg;
			for (int above = extraTop + 1; above < _regs.Length; ++above)
			{
				Slot other = _regs[above];
				if (other.Expr != null && !IsOperandOf(pc, above) && !IsLocalAt(pc, above))
				{
					throw DeclareInferredLocal("stack-above", reg, pc, ScopeEndFor(slot.Pc), slot.Expr, slot.Pc);
				}
			}
		}
		if (slot.Multi && _stripped && !_grouping && !IsStoreOf(pc, reg))
		{
			// `local a, b = f()`: the secondary result is used as a value. A
			// store may open a multiple assignment instead; the leftover
			// rule declares the local if the primary is never stored.
			throw DeclareInferredLocal("multi-read", reg, slot.Pc + 1, ScopeEndFor(slot.Pc), null, slot.Pc);
		}
		if (slot.Multi)
		{
			// A secondary result of a multi-result call: only meaningful as a
			// value in a multiple assignment, which collapses it.
			var marker = MakeExpr(ExprKind.Multi, _ins[pc].Line);
			marker.A = slot.Expr;
			slot = new Slot();
			return marker;
		}
		Expr? expr = slot.Expr;
		if (expr!.Pc < 0) expr.Pc = slot.Pc;
		_lastValue[reg] = expr;
		slot = new Slot();
		return expr;
	}

	private Expr RkExpr(int value, int pc)
	{
		if (value >= RkBit)
		{
			Expr? expr = ConstExpr(value - RkBit, _ins[pc].Line);
			expr!.KIndex = value - RkBit;
			return expr;
		}
		return RegExpr(value, pc);
	}

	private bool HasPending(int reg)
	{
		return reg >= 0 && reg < _regs.Length &&
			_regs[reg].Expr != null;
	}

	// Registers holding pending temporaries from `from` upward, contiguous.
	private int PendingTop(int from)
	{
		int top = from;
		while (top < _regs.Length && _regs[top].Expr != null)
		{
			++top;
		}
		return top;
	}

	private void ClearPendingFrom(int from)
	{
		for (int r = from; r < _regs.Length; ++r)
		{
			_regs[r] = new Slot();
		}
	}

	// ---- statements --------------------------------------------------------

	// struct Ctx: in Header.cs.

	// A call frame opens on a free register: an inferred local still active
	// there ended earlier (a `do` block), so its scope shrinks to this pc.
	private void CheckFrameOnLocal(int pc, int baseReg)
	{
		if (!IsLocalAt(pc, baseReg)) return;
		int index = _active[pc][baseReg];
		ref LocVar var = ref CollectionsMarshal.AsSpan(_locvars)[index];
		// C++ name[0] on an empty std::string reads '\0', never '('.
		if (var.Reg < 0 || (var.Name.Length > 0 && var.Name[0] == '(') || var.StartPc == 0)
		{
			throw Fail(pc, "call frame on local " + var.Name);
		}
		if (_probing) throw new DecompileException("probe needs inference");
		// The register was free when this frame's loads began: the scope
		// closed before them, or with the block holding the declaration.
		int endpc = Math.Max(var.StartPc, FrameStart(pc, baseReg));
		for (int i = _blockStack.Count; i-- > 0;)
		{
			if (_blockStack[i].First <= var.StartPc - 1 && var.StartPc - 1 < _blockStack[i].Second)
			{
				// the statement of the declaring block that holds this frame
				endpc = Math.Min(endpc, Math.Max(_stmtStarts[i], var.StartPc));
				break;
			}
		}
		int blockEnd = ScopeEndFor(var.StartPc - 1);
		if (blockEnd <= pc) endpc = Math.Min(endpc, Math.Max(blockEnd, var.StartPc));
		var.EndPc = endpc;
		if (TraceEnabled) Trace($"{_path} shrink local {var.Name} r{baseReg} to [{var.StartPc},{var.EndPc}): call frame at {pc}");
		throw new RestartInferenceException();
	}

	// Base register of an instruction that builds its operands in fresh
	// registers at and above that base, or -1. Every register at or above
	// the base is a temporary while it executes.
	private int FrameOpBase(int pc)
	{
		ref readonly Instr ins = ref _ins[pc];
		switch (ins.Op)
		{
			case Lua51Op.CALL: case Lua51Op.TAILCALL: case Lua51Op.SELF: case Lua51Op.FORPREP: case Lua51Op.NEWTABLE: return ins.A;
			case Lua51Op.RETURN: return (ins.B == 0 || ins.B > 2) ? ins.A : -1;
			case Lua51Op.SETLIST: return ins.A + 1;
			case Lua51Op.CONCAT: return ins.B;
			default: return -1;
		}
	}

	// Whether a frame op between `from` (exclusive) and `to` (exclusive)
	// proves `reg` was a temporary there.
	private bool FrameBelowBetween(int from, int to, int reg)
	{
		for (int q = from + 1; q < to; ++q)
		{
			int baseReg = FrameOpBase(q);
			if (baseReg >= 0 && baseReg <= reg) return true;
		}
		return false;
	}

	// First pc of the statement that contains the frame loads starting at
	// `pc`: the condition of an enclosing `if` that precedes them, walked
	// back over its operand loads.
	private int StatementStartBefore(int pc, int reg)
	{
		int start = pc;
		// Operand loads belong to the statement only as a condition's: a load
		// right before the frame, with no test after it, ended the statement
		// before.
		bool inCondition = false;
		for (int q = pc - 1; q >= 0; --q)
		{
			ref readonly Instr ins = ref _ins[q];
			bool prefix = false;
			if (ins.Op == Lua51Op.JMP)
			{
				prefix = q + 1 + ins.SBx > pc;  // conditional jump over this code
				inCondition = inCondition || prefix;
			}
			else if (IsTestOp(ins.Op))
			{
				prefix = true;
				inCondition = true;
			}
			else if (ins.Op == Lua51Op.CALL)
			{
				prefix = ins.C != 1;
			}
			else if (ins.A == reg && InstructionWrites(q, reg) && ins.Op != Lua51Op.SETTABLE)
			{
				// the register under question: its scope is what is being decided
				prefix = ins.Op != Lua51Op.LOADNIL || ins.B == reg;
			}
			else
			{
				prefix = inCondition && IsPureOperand(q);
			}
			if (!prefix) break;
			start = q;
		}
		return start;
	}

	// Inferred scopes are checked against the frames inside them before a
	// pass: a local cannot be live where a frame opens at or below its
	// register, so its scope ends before the statement holding that frame.
	private bool SanitiseScopes()
	{
		bool changed = false;
		foreach (ref LocVar var in CollectionsMarshal.AsSpan(_locvars))
		{
			if (var.Reg < 0 || var.Seeded || var.StartPc == 0) continue;
			for (int q = var.StartPc; q < var.EndPc && q < _ins.Length; ++q)
			{
				int baseReg = FrameOpBase(q);
				int cut = -1;
				string why = "";
				if (q == var.StartPc)
				{
					// `local a, b = f()`: the names of one multi-result
					// declaration share their scope; a sibling cut earlier
					// cuts this one too.
					int decl = var.StartPc - 1;
					foreach (LocVar other in _locvars)
					{
						if (other.Reg <= var.Reg || other.Seeded || other.StartPc != var.StartPc || other.EndPc >= var.EndPc) continue;
						if (decl < 0 || (_ins[decl].Op != Lua51Op.CALL && _ins[decl].Op != Lua51Op.VARARG)) continue;
						if (!InstructionWrites(decl, var.Reg) || !InstructionWrites(decl, other.Reg)) continue;
						if (cut < 0 || other.EndPc < cut) cut = other.EndPc;
					}
					if (cut >= 0) why = "multi-result sibling";
				}
				if (cut >= 0)
				{
				}
				else if (baseReg >= 0 && baseReg <= var.Reg)
				{
					cut = StatementStartBefore(FrameStart(q, baseReg), var.Reg);
					why = "frame";
				}
				else if (q > var.StartPc && q + 1 < var.EndPc && IsFreshLoad(q, var.Reg) &&
						 _ins[q + 1].Op == Lua51Op.GETTABLE && _ins[q + 1].A == var.Reg && _ins[q + 1].B == var.Reg)
				{
					// `G.f` loads G into a temporary and indexes it in place; a
					// live local is assigned the finished value, never the object.
					// A call argument's statement opens with the call's frame.
					int from = q;
					for (int c = q + 2; c < _ins.Length; ++c)
					{
						if (IsOperandOf(c, var.Reg))
						{
							Instr use = _ins[c];
							if ((use.Op == Lua51Op.CALL || use.Op == Lua51Op.TAILCALL) && use.A <= var.Reg) from = FrameStart(c, use.A);
							break;
						}
						if (InstructionWrites(c, var.Reg)) break;
					}
					cut = StatementStartBefore(from, var.Reg);
					why = "indexed temporary";
				}
				else
				{
					// A store of this register whose key constant was created
					// before a constant first used by the value's loads: the
					// value was written inline for the store, as a temporary.
					ref readonly Instr ins = ref _ins[q];
					int key = -1;
					if (ins.Op == Lua51Op.SETTABLE && ins.C == var.Reg && ins.B >= RkBit) key = ins.B - RkBit;
					if (ins.Op == Lua51Op.SETGLOBAL && ins.A == var.Reg) key = ins.Bx;
					// only when the value was just computed into the register
					if (key >= 0 && q > var.StartPc && InstructionWrites(q - 1, var.Reg) && FirstConstantUse(key) == q)
					{
						int region = FrameStart(q, var.Reg);
						for (int p = region; p < q && cut < 0; ++p)
						{
							foreach (int c in ConstantsOf(p))
							{
								if (c > key && FirstConstantUse(c) == p) cut = region;
							}
						}
						why = "key before value";
					}
				}
				if (cut < 0) continue;
				cut = Math.Max(var.StartPc, cut);
				if (cut >= var.EndPc) continue;
				if (TraceEnabled) Trace($"{_path} sanitise local {var.Name} r{var.Reg} [{var.StartPc},{var.EndPc}) -> [{var.StartPc},{cut}): {why} at {q}");
				var.EndPc = cut;
				changed = true;
				break;
			}
		}
		return changed;
	}

	// A load into `reg` that reads no register at or below it.
	private bool IsFreshLoad(int pc, int reg)
	{
		Instr ins = _ins[pc];
		switch (ins.Op)
		{
			case Lua51Op.GETGLOBAL: case Lua51Op.GETUPVAL: return ins.A == reg;
			default: return false;
		}
	}

	// Constant indices referenced by the instruction at `pc`.
	private List<int> ConstantsOf(int pc)
	{
		Instr ins = _ins[pc];
		var outList = new List<int>();
		void Rk(int v) { if (v >= RkBit) outList.Add(v - RkBit); }
		switch (ins.Op)
		{
			case Lua51Op.LOADK: case Lua51Op.GETGLOBAL: case Lua51Op.SETGLOBAL: outList.Add(ins.Bx); break;
			case Lua51Op.GETTABLE: case Lua51Op.SELF: Rk(ins.C); break;
			case Lua51Op.SETTABLE: case Lua51Op.EQ: case Lua51Op.LT: case Lua51Op.LE:
			case Lua51Op.ADD: case Lua51Op.SUB: case Lua51Op.MUL: case Lua51Op.DIV: case Lua51Op.MOD: case Lua51Op.POW:
				Rk(ins.B);
				Rk(ins.C);
				break;
			default: break;
		}
		return outList;
	}

	// First pc that references constant `index`, or -1.
	private int FirstConstantUse(int index)
	{
		if (_firstConstantUse.Count == 0)
		{
			// first_constant_use_.assign(constants.size(), -1)
			_firstConstantUse.Clear();
			for (int i = 0; i < _f.Constants.Count; i++) _firstConstantUse.Add(-1);
			for (int p = _ins.Length - 1; p >= 0; --p)
			{
				foreach (int c in ConstantsOf(p))
				{
					if (c >= 0 && c < _firstConstantUse.Count) _firstConstantUse[c] = p;
				}
			}
		}
		if (index < 0 || index >= _firstConstantUse.Count) return -1;
		return _firstConstantUse[index];
	}

	// A loop condition is an expression: an inferred local written by the
	// operand loads leading to its first test had gone out of scope.
	private void ShrinkLocalsInCondition(int start, int exit)
	{
		int q = start;
		while (q < _ins.Length && !IsTestOp(_ins[q].Op) && _ins[q].Op != Lua51Op.JMP)
		{
			if (!IsOperandOp(q)) return;
			++q;
		}
		if (q >= _ins.Length || !IsTestOp(_ins[q].Op)) return;
		// Only a chain that leaves the loop is its condition (`while true`
		// has none: its body's first statement starts here).
		bool leaves = false;
		for (int p = q; p + 1 < _ins.Length && !leaves;)
		{
			if (IsTestOp(_ins[p].Op) && _ins[p + 1].Op == Lua51Op.JMP)
			{
				int target = p + 2 + _ins[p + 1].SBx;
				leaves = JumpsTo(target, exit) || target == exit;
				p += 2;
			}
			else if (IsOperandOp(p))
			{
				++p;
			}
			else
			{
				break;
			}
		}
		if (!leaves) return;
		for (int p = start; p < q; ++p)
		{
			ref readonly Instr ins = ref _ins[p];
			if (ins.Op == Lua51Op.SETTABLE || ins.Op == Lua51Op.SETLIST || !IsLocalAt(p, ins.A)) continue;
			int index = _active[p][ins.A];
			ref LocVar var = ref CollectionsMarshal.AsSpan(_locvars)[index];
			// C++ name[0] on an empty std::string reads '\0', never '('.
			if (var.Reg < 0 || (var.Name.Length > 0 && var.Name[0] == '(') || var.StartPc == 0 || var.StartPc > start) continue;
			if (_probing) throw new DecompileException("probe needs inference");
			var.EndPc = start;
			if (TraceEnabled) Trace($"{_path} shrink local {var.Name} r{ins.A} to [{var.StartPc},{var.EndPc}): loop condition at {start}");
			throw new RestartInferenceException();
		}
	}

	// End (exclusive) of the outermost loop or jump region that contains
	// `read_pc` but not `decl_pc`: a local declared outside such a region
	// and read inside it lives past the region.
	private int EnclosingStructureEnd(int readPc, int declPc)
	{
		int end = readPc + 1;
		for (bool grown = true; grown;)
		{
			grown = false;
			foreach (Loop loop in _loops)
			{
				if (loop.Start <= readPc && readPc <= loop.End && declPc < loop.Start && loop.Exit > end)
				{
					end = loop.Exit;
					grown = true;
				}
			}
			for (int j = 0; j < readPc; ++j)
			{
				if (_ins[j].Op != Lua51Op.JMP && _ins[j].Op != Lua51Op.FORPREP) continue;
				int target = j + 1 + _ins[j].SBx;
				if (declPc <= j && target > readPc && target > end)
				{
					end = target;
					grown = true;
				}
			}
			readPc = end - 1;
		}
		return end;
	}

	// First pc of the straight-line loads that build the call frame based at
	// `base` and executed at `pc`: the callee and argument loads, which only
	// write registers at or above the base.
	private int FrameStart(int pc, int baseReg)
	{
		int start = pc;
		for (int q = pc - 1; q >= 0; --q)
		{
			ref readonly Instr ins = ref _ins[q];
			bool frameLoad = false;
			switch (ins.Op)
			{
				case Lua51Op.MOVE: case Lua51Op.LOADK: case Lua51Op.LOADBOOL: case Lua51Op.LOADNIL: case Lua51Op.GETUPVAL:
				case Lua51Op.GETGLOBAL: case Lua51Op.GETTABLE: case Lua51Op.NEWTABLE: case Lua51Op.ADD: case Lua51Op.SUB:
				case Lua51Op.MUL: case Lua51Op.DIV: case Lua51Op.MOD: case Lua51Op.POW: case Lua51Op.UNM: case Lua51Op.NOT:
				case Lua51Op.LEN: case Lua51Op.CONCAT: case Lua51Op.SELF: case Lua51Op.VARARG: case Lua51Op.CLOSURE:
					frameLoad = ins.A >= baseReg;
					break;
				case Lua51Op.SETTABLE: case Lua51Op.SETLIST:
				{
					// a field store of a constructor still being built
					int tableWrite = LastWriteBefore(ins.A, q);
					frameLoad = ins.A >= baseReg && tableWrite >= 0 && _ins[tableWrite].Op == Lua51Op.NEWTABLE;
					break;
				}
				case Lua51Op.CALL:
					frameLoad = ins.C != 1 && ins.A >= baseReg;
					break;
				default:
					break;
			}
			if (!frameLoad) break;
			start = q;
		}
		return start;
	}

	// Temporaries are allocated at the top of the stack: a free register
	// below a temporary write belonged to a local.
	private void CheckGapBelow(int pc, int reg)
	{
		if (pc + 1 < _ins.Length && _ins[pc + 1].Op == Lua51Op.SELF &&
			_ins[pc + 1].C == reg && _ins[pc + 1].A + 2 == reg)
		{
			// SELF reserves its two base registers before a spilled key is
			// loaded above them: the gap is the frame, not a local.
			return;
		}
		for (int r = reg - 1; r >= 0; --r)
		{
			if (IsLocalAt(pc, r)) continue;  // locals sit below; look further down
			Slot below = _regs[r];
			if (below.Expr != null) break;
			if (_written[r])
			{
				throw DeclareInferredLocal("gap-below", r, pc, ScopeEndFor(LastWriteBefore(r, pc)), _lastValue[r]);
			}
			throw HoistNilLocal(r, pc, "gap-below-unwritten");
		}
	}

	// A register never written yet in use held a `local x` declared at the
	// function start, whose LOADNIL the compiler elides at pc 0.
	// [[noreturn]]: like DeclareInferredLocal it always throws; it is typed to
	// return Exception so call sites can write `throw HoistNilLocal(...)`.
	private Exception HoistNilLocal(int reg, int pc, string why)
	{
		if (_probing) throw new DecompileException("probe needs inference");
		var var = new LocVar(NameForLocal(null, reg), 0, _ins.Length, reg);
		_locvars.Add(var);
		SortLocvars(_locvars);
		if (TraceEnabled) Trace($"{_path} infer nil local {var.Name} r{reg} [0,{var.EndPc}) at {pc}: {why}");
		throw new RestartInferenceException();
	}

	// Result of writing a register: either the value is parked or a statement
	// was produced (assignment to an active local).
	private void WriteReg(int pc, int reg, Expr? value, List<Stmt> outList, in Ctx ctx)
	{
		if (ctx.ValueReg == reg)
		{
			_regs[reg] = new Slot(value, pc, false);
			return;
		}
		if (IsLocalAt(pc, reg))
		{
			var target = MakeExpr(ExprKind.Local, _ins[pc].Line);
			target.Name = LocalNameAt(pc, reg);
			if (_grouping)
			{
				EmitAssign(pc, target, value!, outList, ctx);
			}
			else
			{
				_pendingNextPc = EmitSet(pc, target, value!, outList, ctx);
			}
			return;
		}
		if (_stripped && ctx.ValueReg < 0) CheckGapBelow(pc, reg);
		ref Slot slot = ref _regs[reg];
		if (slot.Expr != null && !slot.Multi && !IsLocalAt(pc + 1, reg))
		{
			// A temporary overwritten before use would lose an expression.
			if (slot.Expr.Kind != ExprKind.Nil || _stripped)
			{
				if (_stripped)
				{
					// The pending value was a local; this write assigns to it.
					throw DeclareInferredLocal("overwrite", reg, slot.Pc + 1, ScopeEndFor(slot.Pc), slot.Expr, slot.Pc);
				}
				throw Fail(pc, "temporary in register " + reg.ToString() + " overwritten");
			}
		}
		_written[reg] = true;
		slot = new Slot(value, pc, false);
	}

	private void EmitAssign(int pc, Expr target, Expr value, List<Stmt> outList, in Ctx ctx)
	{
		var stmt = new Stmt();
		stmt.Kind = StmtKind.Assign;
		stmt.Line = _ins[pc].Line;
		if (value.Kind == ExprKind.Table)
		{
			value = value.Clone();
			value.TableOpen = false;
		}
		if ((value.Kind == ExprKind.Table || value.Kind == ExprKind.Call ||
			 value.Kind == ExprKind.MethodCall) && value.EndLine == 0)
		{
			value.EndLine = _ins[pc].Line;
		}
		// Function statements keep their declaration form.
		bool funcstatLine = _ins[pc].Line == 0 || value.Func == null ||
							_ins[pc].Line == value.Func.LineDefined;
		// Kore records each function statement's name: when the chunk kept it, it decides.
		if (value.Kind == ExprKind.Function && value.Func != null && value.Func.NameRecorded)
		{
			funcstatLine = value.Func.KoreName != null;
		}
		if (value.Kind == ExprKind.Function && value.Func != null && IsFunctionNameChain(target) && funcstatLine)
		{
			stmt.Kind = StmtKind.FunctionDecl;
			stmt.Func = value.Func;
			stmt.FuncName = target;
			stmt.Line = value.Func.LineDefined;
			if (target.Kind == ExprKind.Index && stmt.Func.Params.Count != 0 &&
				stmt.Func.Params[0] == "self")
			{
				stmt.Method = true;
			}
			if (stmt.Func.NameRecorded && stmt.Func.KoreName != null)
			{
				stmt.Method = target.Kind == ExprKind.Index && stmt.Func.KoreName.Contains(':');
			}
			outList.Add(stmt);
			return;
		}
		stmt.Targets.Add(target);
		stmt.Values.Add(value);
		Expr firstTarget = stmt.Targets[0];
		if (firstTarget.Kind == ExprKind.Global || firstTarget.Kind == ExprKind.Local ||
			firstTarget.Kind == ExprKind.Upvalue || firstTarget.Kind == ExprKind.Index)
		{
			firstTarget.Line = 0;  // the store's line is the statement's end, not its start
		}
		if (firstTarget.Kind == ExprKind.Index && firstTarget.B != null &&
			(firstTarget.B.Kind == ExprKind.String || firstTarget.B.Kind == ExprKind.Number))
		{
			firstTarget.B.Line = 0;
		}
		if (firstTarget.Kind == ExprKind.Index && firstTarget.A != null &&
			(firstTarget.A.Kind == ExprKind.Local || firstTarget.A.Kind == ExprKind.Upvalue))
		{
			firstTarget.A.Line = 0;  // no load instruction: the line is the store's
		}
		stmt.Line = FirstLine(firstTarget);
		if (stmt.Line == 0) stmt.Line = FirstLine(stmt.Values[0]);
		outList.Add(stmt);
		_ = ctx;
	}

	private static bool IsFunctionNameChain(Expr target)
	{
		if (target.Kind == ExprKind.Global || target.Kind == ExprKind.Local ||
			target.Kind == ExprKind.Upvalue)
		{
			return true;
		}
		if (target.Kind == ExprKind.Index && target.B!.Kind == ExprKind.String &&
			IsIdentifier(target.B.Name))
		{
			return IsFunctionNameChain(target.A!);
		}
		return false;
	}

	// A local or upvalue operand has no load of its own: the line it carries
	// is its consumer's. The other operand's first token bounds the start.
	private static int FirstOperandLine(Expr? first, Expr? second, int ownLine)
	{
		int la = FirstLine(first);
		if (first != null && (first.Kind == ExprKind.Local || first.Kind == ExprKind.Upvalue))
		{
			int lb = FirstLine(second);
			if (lb != 0 && (la == 0 || lb < la)) return lb;
		}
		return la != 0 ? la : ownLine;
	}

	private static int FirstLine(Expr? expr)
	{
		if (expr == null) return 0;
		switch (expr.Kind)
		{
			case ExprKind.Index:
			{
				int line = FirstLine(expr.A);
				if (line != 0) return line;
				// A key loaded into a register (a constant past the RK range)
				// is coded with its own token, before the GETTABLE.
				int keyLine = FirstLine(expr.B);
				return keyLine != 0 && (expr.Line == 0 || keyLine < expr.Line) ? keyLine : expr.Line;
			}
			case ExprKind.Call:
			{
				int line = FirstLine(expr.A);
				return line != 0 ? line : expr.Line;
			}
			case ExprKind.MethodCall:
			{
				int line = FirstLine(expr.A);
				return line != 0 ? line : expr.Line;
			}
			case ExprKind.Compare:
			{
				if (expr.Negate && expr.Op != Lua51Op.EQ) return expr.Line;  // `not (` first
				Expr? first = expr.Swapped ? expr.B : expr.A;
				Expr? second = expr.Swapped ? expr.A : expr.B;
				return FirstOperandLine(first, second, expr.Line);
			}
			case ExprKind.Binary:
			case ExprKind.And:
			case ExprKind.Or:
				return FirstOperandLine(expr.A, expr.B, expr.Line);
			case ExprKind.Unary:
			case ExprKind.Not:
			case ExprKind.Paren:
			{
				int line = FirstLine(expr.A);
				return line != 0 ? line : expr.Line;
			}
			case ExprKind.Function:
				return expr.Func != null ? expr.Func.LineDefined : expr.Line;
			default:
				return expr.Line;
		}
	}

	// Emits `local` statements for locvars that begin at `pc`, consuming the
	// pending initialisers in their registers.
	private void EmitLocalsAt(int pc, List<Stmt> outList)
	{
		List<int> starting = LocalsStartingAt(pc);
		if (starting.Count == 0) return;
		if (TraceEnabled) Trace($"{_path} locals at {pc}: {starting.Count} (first reg {RegisterOfLocvar(starting[0])})");
		var stmt = new Stmt();
		stmt.Kind = StmtKind.Local;
		bool allNil = true;
		int firstReg = -1;
		foreach (int index in starting)
		{
			_claimedLocvars.Add(index);
			stmt.ScopeEnd = Math.Max(stmt.ScopeEnd, _locvars[index].EndPc);
			int reg = RegisterOfLocvar(index);
			if (reg < 0 || reg >= _regs.Length)
			{
				throw Fail(pc, "cannot place local " + _locvars[index].Name);
			}
			if (firstReg < 0) firstReg = reg;
			stmt.Names.Add(_locvars[index].Name);
			ref Slot slot = ref _regs[reg];
			if (slot.Expr != null && !slot.Multi)
			{
				if (slot.Expr.Kind != ExprKind.Nil || slot.Pc < 0 ||
					_ins[slot.Pc].Op != Lua51Op.LOADNIL)
				{
					allNil = false;
				}
				stmt.Values.Add(slot.Expr);
				if (stmt.Line == 0) stmt.Line = FirstLine(slot.Expr);
				if (stmt.Line == 0) stmt.Line = _ins[slot.Pc].Line;
			}
			else if (slot.Expr != null && slot.Multi)
			{
				// covered by the multi-result call before it
			}
			else
			{
				allNil = allNil && true;
			}
			slot = new Slot();
		}
		// Extra initialisers beyond the names are evaluated and discarded;
		// they sit in the registers right after the declared ones.
		if (firstReg >= 0 && stmt.Values.Count != 0)
		{
			int extra = firstReg + starting.Count;
			// size_t: the C++ `pc - 1 - (...) * 0` is size_t arithmetic; the product is 0, so the
			// comparison is slot.pc == pc - 1 (both sides wrap identically at -1). Same in int.
			while (extra < _regs.Length && _regs[extra].Expr != null &&
				   !_regs[extra].Multi &&
				   _regs[extra].Pc == pc - 1 - (extra - firstReg - starting.Count) * 0 &&
				   _regs[extra].Pc >= 0 && _regs[extra].Pc < pc &&
				   !IsLocalAt(pc, extra))
			{
				stmt.Values.Add(_regs[extra].Expr!);
				_regs[extra] = new Slot();
				allNil = false;
				++extra;
			}
		}
		if (allNil)
		{
			// `local a, b` — LOADNIL initialisers are implicit.
			if (stmt.Values.Count != 0 && stmt.Values[^1].Kind == ExprKind.Nil)
			{
				stmt.Values.Clear();
			}
		}
		// A local whose initialiser is a function may be `local function`.
		if (stmt.Names.Count == 1 && stmt.Values.Count == 1 &&
			stmt.Values[0].Kind == ExprKind.Function && stmt.Values[0].Func != null &&
			(!stmt.Values[0].Func!.NameRecorded || stmt.Values[0].Func!.KoreName != null))
		{
			stmt.Kind = StmtKind.LocalFunction;
			stmt.Func = stmt.Values[0].Func;
			stmt.Line = stmt.Func!.LineDefined;
			stmt.Values.Clear();
		}
		if (stmt.Line == 0 && stmt.Values.Count != 0) stmt.Line = FirstLine(stmt.Values[0]);
		bool nested = false;
		for (int i = 1; i < starting.Count; ++i) nested = nested || _locvars[starting[i]].EndPc < _locvars[starting[i - 1]].EndPc;
		if (stmt.Kind == StmtKind.Local && stmt.Values.Count == 0 && nested)
		{
			// `local a` then `do local b`: luaK_nil folds b's LOADNIL into a's,
			// so both start here, and b's scope ends first. Each run of names
			// whose scope ends earlier than the one before is its own statement
			// (WrapDoBlocks then opens its `do`).
			int from = 0;
			for (int i = 1; i <= starting.Count; ++i)
			{
				if (i < starting.Count && _locvars[starting[i]].EndPc >= _locvars[starting[i - 1]].EndPc) continue;
				var part = new Stmt();
				part.Kind = StmtKind.Local;
				part.Line = stmt.Line;
				part.ScopeEnd = -1;
				for (int k = from; k < i; ++k)
				{
					part.Names.Add(stmt.Names[k]);
					part.ScopeEnd = Math.Max(part.ScopeEnd, _locvars[starting[k]].EndPc);
				}
				outList.Add(part);
				from = i;
			}
			return;
		}
		outList.Add(stmt);
	}

	// Groups the chain of assignments that a multiple assignment produces:
	// the first SET stores the last target, older temporaries follow.
	private void GroupAssignments(List<Stmt> outList, int firstIndex)
	{
		if (firstIndex + 1 >= outList.Count) return;
		Stmt head = outList[firstIndex];
		if (head.Kind != StmtKind.Assign) return;
		var targets = new List<Expr>();
		var values = new List<Expr>();
		for (int i = firstIndex; i < outList.Count; ++i)
		{
			Stmt s = outList[i];
			if (s.Kind != StmtKind.Assign) return;
			targets.Add(s.Targets[0]);
			values.Add(s.Values[0]);
		}
		targets.Reverse();
		values.Reverse();
		var stmt = new Stmt();
		stmt.Kind = StmtKind.Assign;
		stmt.Targets = targets;
		// Collapse secondary results of one multi-result expression.
		foreach (Expr value in values)
		{
			if (value.Kind == ExprKind.Multi)
			{
				bool present = false;
				foreach (Expr existing in stmt.Values) present = present || ReferenceEquals(existing, value.A);
				if (!present) stmt.Values.Add(value.A!);
				continue;
			}
			if (stmt.Values.Count != 0 && ReferenceEquals(stmt.Values[^1], value)) continue;
			stmt.Values.Add(value);
		}
		stmt.Line = 0;
		foreach (Expr t in stmt.Targets) if (stmt.Line == 0) stmt.Line = FirstLine(t);
		foreach (Expr v in stmt.Values) if (stmt.Line == 0) stmt.Line = FirstLine(v);
		outList.RemoveRange(firstIndex, outList.Count - firstIndex);
		outList.Add(stmt);
	}

	// Builds a block whose last instruction may be a break: jumps threaded
	// through that break resolve to it.
	private List<Stmt> BuildBodyBlock(int start, int end, int loopExit)
	{
		bool endsInJump = end - 1 >= start && _ins[end - 1].Op == Lua51Op.JMP &&
						  _ins[end - 1].SBx >= 0 && !IsPairedJump(end - 1);
		if (endsInJump) PushClosingJump(end - 1);
		// No RAII here in C++ either: an exception from BuildBlock leaves the jump pushed.
		List<Stmt> body = BuildBlock(start, end, loopExit, end);
		if (endsInJump) PopClosingJump();
		return body;
	}

	// The line of the jump that closes a block is the line of the block's last
	// token; give it to a trailing call or constructor so its ')' or '}' lands there.
	private static void HintEndLine(List<Stmt> body, int line)
	{
		if (line == 0 || body.Count == 0) return;
		Stmt last = body[^1];
		switch (last.Kind)
		{
			case StmtKind.If: case StmtKind.While: case StmtKind.NumFor: case StmtKind.GenFor: case StmtKind.Do:
			case StmtKind.FunctionDecl: case StmtKind.LocalFunction:
				if (last.CloseLine < line) last.CloseLine = line;
				return;
			default:
				break;
		}
		Expr? value = null;
		if (last.Kind == StmtKind.Call) value = last.Values[0];
		else if ((last.Kind == StmtKind.Assign || last.Kind == StmtKind.Local) && last.Values.Count != 0) value = last.Values[^1];
		else if (last.Kind == StmtKind.Return && last.Values.Count != 0) value = last.Values[^1];
		if (value == null || value.EndLine >= line) return;
		if (value.Kind == ExprKind.Table)
		{
			value.EndLine = line;
			return;
		}
		if (value.Kind == ExprKind.Call || value.Kind == ExprKind.MethodCall)
		{
			// The last argument, unless it is a call, constructor or operator
			// (coded when its right operand ends), is loaded after ')' and so
			// already carries the ')' line.
			if (value.Args.Count != 0)
			{
				ExprKind k = value.Args[^1].Kind;
				if (k != ExprKind.Call && k != ExprKind.MethodCall && k != ExprKind.Table &&
					k != ExprKind.Vararg && k != ExprKind.Function && k != ExprKind.Binary &&
					k != ExprKind.Unary)
				{
					return;
				}
			}
			value.EndLine = line;
		}
	}

	// A backward jump to an enclosing loop's start (other than the loop's own
	// closing jump) was threaded by the compiler from a jump to that closing
	// JMP; report the closing JMP as the target.
	private int EffectiveJumpTarget(int pc)
	{
		int target = pc + 1 + _ins[pc].SBx;
		for (int i = _jumpStack.Count - 1; i >= 0; --i)
		{
			ClosingJump it = _jumpStack[i];
			if (it.Target == target && pc != it.JmpPc && pc < it.JmpPc)
			{
				return it.JmpPc;
			}
		}
		return target;
	}

	// Follows a chain of jumps from `pc`: the pc a pending jump would have
	// been threaded to when the instruction at `pc` is itself a JMP.
	private bool IsPairedJump(int pc)
	{
		return pc > 0 && pc < _ins.Length && _ins[pc].Op == Lua51Op.JMP &&
			   IsTestOp(_ins[pc - 1].Op);
	}

	private int ResolveThreaded(int pc)
	{
		int guard = 0;
		while (pc >= 0 && pc < _ins.Length && _ins[pc].Op == Lua51Op.JMP &&
			   !IsPairedJump(pc) && guard++ < 64)
		{
			pc = pc + 1 + _ins[pc].SBx;
		}
		return pc;
	}

	private bool JumpsTo(int rawTarget, int expected)
	{
		return rawTarget == expected || rawTarget == ResolveThreaded(expected);
	}

	// struct ClosingJump: in Header.cs.

	private void PushClosingJump(int jmpPc)
	{
		_jumpStack.Add(new ClosingJump { JmpPc = jmpPc, Target = jmpPc + 1 + _ins[jmpPc].SBx });
	}

	private void PopClosingJump() { _jumpStack.RemoveAt(_jumpStack.Count - 1); }

	// ---- block builder -------------------------------------------------

	private List<Stmt> BuildBlock(int start, int end, int loopExit, int blockEnd)
	{
		if (TraceEnabled) Trace($"{_path} block [{start},{end}) loop_exit={loopExit}");
		_blockStack.Add((start, end));
		// C++ `struct Pop` RAII guard (destructor pops block_stack_, also while unwinding):
		// the outer try/finally. `struct PopStart` (pops stmt_starts_) is the inner one; it is
		// declared later, so it is destroyed first, as the nesting gives. The return value
		// (WrapDoBlocks) is computed before either finally runs, as in C++.
		try
		{
			var outList = new List<Stmt>();
			Ctx ctx = new Ctx();
			ctx.LoopExit = loopExit;
			ctx.BlockEnd = blockEnd;
			int pc = start;
			int stmtIndex = outList.Count;
			int stmtStart = start;
			_stmtStarts.Add(start);
			try
			{
				void Stamp(int nextPc)
				{
					bool grew = stmtIndex < outList.Count;
					for (; stmtIndex < outList.Count; ++stmtIndex)
					{
						if (outList[stmtIndex].StartPc < 0) outList[stmtIndex].StartPc = stmtStart;
						outList[stmtIndex].EndPc = nextPc;
					}
					if (grew) _lastStmtStart = stmtStart;
					if (grew && _stripped)
					{
						// A statement just ended: temporaries still pending outlived it.
						if (TraceEnabled) Trace($"{_path} stamp {nextPc}: last stmt kind={(int)outList[^1].Kind} [{outList[^1].StartPc},{outList[^1].EndPc})");
						for (int r = 0; r < _regs.Length; ++r)
						{
							Slot slot = _regs[r];
							if (slot.Expr == null || IsLocalAt(nextPc, r)) continue;
							if (slot.Multi) throw DeclareInferredLocal("stmt-leftover-multi", r, slot.Pc + 1, ScopeEndFor(slot.Pc), null, slot.Pc);
							throw DeclareInferredLocal("stmt-leftover", r, slot.Pc + 1, ScopeEndFor(slot.Pc), slot.Expr, slot.Pc);
						}
					}
					stmtStart = nextPc;
					_stmtStarts[^1] = nextPc;
				}
				while (pc < end)
				{
					EmitLocalsAt(pc, outList);
					Stamp(pc);
					// Loop* -> index into _loops (-1 = nullptr), aliased by ref.
					int li = FindLoopStartingAt(pc, end);
					if (li >= 0)
					{
						ref Loop loop = ref CollectionsMarshal.AsSpan(_loops)[li];
						if (TraceEnabled) Trace($"{_path} loop kind={(int)loop.Kind} [{loop.Start},{loop.End}] exit={loop.Exit}");
						loop.Used = true;
						BuildLoop(ref loop, outList, ctx);
						pc = loop.Exit;
						Stamp(pc);
						continue;
					}
					Instr instr = _ins[pc];
					if (IsTestOp(instr.Op))
					{
						pc = BuildConditional(pc, end, outList, ref ctx);
						Stamp(pc);
						continue;
					}
					if (instr.Op == Lua51Op.JMP)
					{
						int rawTarget = pc + 1 + instr.SBx;
						int target = EffectiveJumpTarget(pc);
						if (loopExit >= 0 && (JumpsTo(rawTarget, loopExit) || JumpsTo(target, loopExit)))
						{
							var stmt = new Stmt();
							stmt.Kind = StmtKind.Break;
							stmt.Line = instr.Line;
							outList.Add(stmt);
							++pc;
							continue;
						}
						if (target == pc + 1)
						{
							var stmt = new Stmt();
							stmt.Kind = StmtKind.If;
							stmt.Cond = MakeExpr(ExprKind.False, instr.Line);
							stmt.Line = instr.Line;
							outList.Add(stmt);
							++pc;
							continue;
						}
						if (target > pc && target <= end && IsPairedJump(target - 1) &&
							!LoopStartsAt(pc + 1))
						{
							// `if true or <cond> then`: the constant leads the chain.
							pc = BuildConditional(pc, end, outList, ref ctx);
							Stamp(pc);
							continue;
						}
						if (target > pc && target <= end)
						{
							// Lua 5.1.2 compiles `if false then ... end` to a bare jump
							// over the (dead) body.
							var stmt = new Stmt();
							stmt.Kind = StmtKind.If;
							stmt.Cond = MakeExpr(ExprKind.False, instr.Line);
							stmt.Line = instr.Line;
							// `if false then ... else ... end`: the dead body ends in
							// the escape over the else body.
							int thenEnd = target;
							int elseEnd = target;
							if (target - 1 > pc && _ins[target - 1].Op == Lua51Op.JMP && !IsPairedJump(target - 1))
							{
								int escape = EffectiveJumpTarget(target - 1);
								if (escape > end && ResolveThreaded(end) == escape) escape = end;
								int escapeRaw = target + _ins[target - 1].SBx;
								bool isBreak = loopExit >= 0 && (JumpsTo(escapeRaw, loopExit) || JumpsTo(escape, loopExit));
								bool ownStatement = escape == target && target - 2 > pc &&
													_ins[target - 1].Line > _ins[target - 2].Line;
								if (!isBreak && !ownStatement && escape > target && escape <= end)
								{
									thenEnd = target - 1;
									elseEnd = escape;
									stmt.HasElse = true;
								}
							}
							if (stmt.HasElse) PushClosingJump(thenEnd);
							stmt.Body = BuildBodyBlock(pc + 1, thenEnd, ctx.LoopExit);
							if (stmt.HasElse)
							{
								PopClosingJump();
								stmt.ElseBody = BuildBodyBlock(target, elseEnd, ctx.LoopExit);
								HintEndLine(stmt.Body, _ins[thenEnd].Line);
								stmt.EscapeLine = _ins[thenEnd].Line;
							}
							outList.Add(stmt);
							pc = elseEnd;
							continue;
						}
						// A jump back to an enclosing loop's head (or forward to its
						// FORLOOP) that no structure explains is the game compiler's
						// `continue`.
						foreach (Loop loop in _loops)
						{
							if (!loop.Used || pc < loop.BodyStart || pc >= loop.BodyEnd) continue;
							bool toHead = rawTarget == loop.Start &&
										  (loop.Kind == LoopKind.While || loop.Kind == LoopKind.Repeat);
							bool toForloop = rawTarget == loop.End &&
											 (loop.Kind == LoopKind.NumFor || loop.Kind == LoopKind.GenFor);
							if (toHead || toForloop)
							{
								var stmt = new Stmt();
								stmt.Kind = StmtKind.Continue;
								stmt.Line = instr.Line;
								outList.Add(stmt);
								++pc;
								goto next_instruction;
							}
						}
						throw Fail(pc, "unstructured jump to " + target.ToString());
					}
					if (instr.Op == Lua51Op.FORLOOP || instr.Op == Lua51Op.FORPREP || instr.Op == Lua51Op.TFORLOOP)
					{
						throw Fail(pc, "loop instruction outside a recognised loop");
					}
					pc = ProcessSimple(pc, outList, ctx);
					Stamp(pc);
				next_instruction:;
				}
				EmitLocalsAt(pc, outList);
				Stamp(pc);
				if (_stripped)
				{
					// Values still pending at the block's end were locals of this block.
					for (int r = 0; r < _regs.Length; ++r)
					{
						Slot slot = _regs[r];
						if (slot.Expr == null || IsLocalAt(end, r) || slot.Pc < start) continue;
						if (slot.Multi)
						{
							// secondary result: declared with the same start as its call
							throw DeclareInferredLocal("block-leftover-multi", r, slot.Pc + 1, end, null, slot.Pc);
						}
						throw DeclareInferredLocal("block-leftover", r, slot.Pc + 1, end, slot.Expr, slot.Pc);
					}
				}
				// `out` is moved in C++: it is not used again here.
				return WrapDoBlocks(start, end, outList);
			}
			finally
			{
				_stmtStarts.RemoveAt(_stmtStarts.Count - 1);
			}
		}
		finally
		{
			_blockStack.RemoveAt(_blockStack.Count - 1);
		}
	}

	// Locals whose scope ends before the enclosing block does were declared
	// inside a `do ... end`. Wrap the statements from the declaration to the
	// scope end.
	// `stmts` is taken by value (moved) in C++; every caller hands over a list it
	// does not use again, so it is edited in place and returned.
	private List<Stmt> WrapDoBlocks(int start, int end, List<Stmt> stmts)
	{
		_ = start;
		for (int i = 0; i < stmts.Count; ++i)
		{
			Stmt stmt = stmts[i];
			if ((stmt.Kind != StmtKind.Local && stmt.Kind != StmtKind.LocalFunction) ||
				stmt.ScopeEnd < 0 || stmt.ScopeEnd >= end)
			{
				continue;
			}
			// The scope ends on the block's own CLOSE (emitted at leaveblock,
			// before any escape jump): that is the block's end, not a do-block.
			if (stmt.ScopeEnd < _ins.Length && _ins[stmt.ScopeEnd].Op == Lua51Op.CLOSE)
			{
				int afterClose = stmt.ScopeEnd + 1;
				if (afterClose < _ins.Length && _ins[afterClose].Op == Lua51Op.JMP) ++afterClose;
				if (afterClose >= end) continue;
			}
			// The scope closes before the block does: a `do ... end` from this
			// declaration through the last statement inside the scope.
			if (TraceEnabled) Trace($"{_path} do-block for local at stmt {i} scope_end={stmt.ScopeEnd} block_end={end}");
			int j = i;
			while (j + 1 < stmts.Count && stmts[j + 1].EndPc <= stmt.ScopeEnd) ++j;
			if (_stripped && j + 1 < stmts.Count && stmts[j + 1].StartPc < stmt.ScopeEnd)
			{
				// An inferred scope ending inside the next statement: a `do`
				// block closes on statement boundaries, so the scope ended
				// before that statement began.
				int boundary = stmts[j + 1].StartPc;
				// Assignments to the local right before its scope ends are
				// dead stores: they were the next statement's own loads.
				for (int k = j; k > i; --k)
				{
					Stmt prev = stmts[k];
					if (prev.Kind != StmtKind.Assign || prev.Targets.Count != 1 ||
						prev.Targets[0].Kind != ExprKind.Local)
					{
						break;
					}
					bool declaredHere = false;
					foreach (string n in stmt.Names) declaredHere = declaredHere || n == prev.Targets[0].Name;
					if (!declaredHere) break;
					boundary = prev.StartPc;
				}
				bool trimmed = false;
				foreach (ref LocVar var in CollectionsMarshal.AsSpan(_locvars))
				{
					if (var.Reg < 0 || var.Seeded || var.EndPc != stmt.ScopeEnd) continue;
					if (var.StartPc < stmt.StartPc || var.StartPc > stmt.EndPc) continue;
					if (boundary <= var.StartPc) continue;
					if (TraceEnabled) Trace($"{_path} trim local {var.Name} r{var.Reg} to [{var.StartPc},{boundary}): statement boundary");
					var.EndPc = boundary;
					trimmed = true;
				}
				if (trimmed) throw new RestartInferenceException();
			}
			var block = new Stmt();
			block.Kind = StmtKind.Do;
			block.Line = stmt.Line;
			block.StartPc = stmt.StartPc;
			block.EndPc = stmts[j].EndPc;
			block.Body = stmts.GetRange(i, j - i + 1);
			block.Body = WrapDoBlocks(block.StartPc, stmt.ScopeEnd, block.Body);
			stmts.RemoveRange(i, j - i + 1);
			stmts.Insert(i, block);
		}
		return stmts;
	}

	// Processes one non-control instruction. Returns the next pc.
	private int ProcessSimple(int pc, List<Stmt> outList, in Ctx ctx)
	{
		ref readonly Instr ins = ref _ins[pc];
		int line = ins.Line;
		switch (ins.Op)
		{
			case Lua51Op.MOVE:
			{
				if (_stripped && ins.A < ins.B && !IsLocalAt(pc, ins.A) && !IsLocalAt(pc, ins.B) &&
					_regs[ins.B].Multi)
				{
					// A secondary call result is only moved by a multiple
					// assignment into a local: the target register is one.
					Slot target = _regs[ins.A];
					if (target.Expr != null)
					{
						throw DeclareInferredLocal("multi-store", ins.A, pc, ScopeEndFor(target.Pc), target.Expr, target.Pc);
					}
					if (_written[ins.A])
					{
						throw DeclareInferredLocal("multi-store", ins.A, pc, ScopeEndFor(LastWriteBefore(ins.A, pc)),
							_lastValue[ins.A]);
					}
					throw HoistNilLocal(ins.A, pc, "multi-store-unwritten");
				}
				_pendingNextPc = pc + 1;
				WriteReg(pc, ins.A, RegExpr(ins.B, pc), outList, ctx);
				return _pendingNextPc;
			}
			case Lua51Op.LOADK:
			{
				Expr expr = ConstExpr(ins.Bx, line);
				expr.KIndex = ins.Bx;
				WriteReg(pc, ins.A, expr, outList, ctx);
				return pc + 1;
			}
			case Lua51Op.LOADBOOL:
			{
				if (ins.C != 0) throw Fail(pc, "LOADBOOL with skip outside a comparison");
				WriteReg(pc, ins.A, MakeExpr(ins.B != 0 ? ExprKind.True : ExprKind.False, line), outList, ctx);
				return pc + 1;
			}
			case Lua51Op.LOADNIL:
			{
				if (_stripped)
				{
					bool consumedNext = (pc + 1 < _ins.Length &&
										 (_ins[pc + 1].Op == Lua51Op.SETGLOBAL || _ins[pc + 1].Op == Lua51Op.SETUPVAL ||
										  _ins[pc + 1].Op == Lua51Op.SETTABLE || _ins[pc + 1].Op == Lua51Op.RETURN ||
										  _ins[pc + 1].Op == Lua51Op.CALL || _ins[pc + 1].Op == Lua51Op.SETLIST)) ||
										IsOperandOfLater(pc, ins.A);
					if (!consumedNext)
					{
						for (int r = ins.A; r <= ins.B; ++r)
						{
							bool declared = IsLocalAt(pc + 1, r) || IsLocalAt(pc, r);
							foreach (LocVar v in _locvars)
							{
								if (v.Reg == r && v.StartPc == pc + 1) declared = true;
							}
							if (!declared)
							{
								if (_probing) throw new DecompileException("probe needs inference");
								LocVar locVar = new LocVar(string.Empty, 0, 0);
								locVar.Name = NameForLocal(null, r);
								locVar.StartPc = pc + 1;
								locVar.EndPc = ScopeEndFor(pc);
								locVar.Reg = r;
								_locvars.Add(locVar);
								SortLocvars(_locvars);
								if (TraceEnabled) Trace($"{_path} infer nil local {locVar.Name} r{r} [{locVar.StartPc},{locVar.EndPc})");
								throw new RestartInferenceException();
							}
						}
					}
				}
				// Registers A..B. Locals get an assignment each; temporaries park nil.
				int firstStmt = outList.Count;
				for (int r = ins.A; r <= ins.B; ++r)
				{
					WriteReg(pc, r, MakeExpr(ExprKind.Nil, line), outList, ctx);
				}
				// `a = nil` statements in a row merge into one LOADNIL, so each
				// local keeps its own statement.
				_ = firstStmt;
				return pc + 1;
			}
			case Lua51Op.GETUPVAL:
			{
				var expr = MakeExpr(ExprKind.Upvalue, line);
				expr.Name = UpvalueName(ins.B);
				WriteReg(pc, ins.A, expr, outList, ctx);
				return pc + 1;
			}
			case Lua51Op.GETGLOBAL:
			{
				var expr = MakeExpr(ExprKind.Global, line);
				expr.Name = _f.Constants[ins.Bx].String;
				expr.KIndex = ins.Bx;
				WriteReg(pc, ins.A, expr, outList, ctx);
				return pc + 1;
			}
			case Lua51Op.GETTABLE:
			{
				if (_stripped)
				{
					CheckRkConstant(pc, ins.C);
					CheckOperandOrder(pc, ins.B, ins.C);
				}
				var expr = MakeExpr(ExprKind.Index, line);
				expr.A = RegExpr(ins.B, pc);
				expr.B = RkExpr(ins.C, pc);
				if ((expr.A.Kind == ExprKind.Local || expr.A.Kind == ExprKind.Upvalue) && ins.C < RkBit &&
					expr.B.Line != 0 && expr.B.Line < expr.A.Line)
				{
					// A local object loads nothing; its token sat no later than
					// the key's own load (a constant past the RK range).
					expr.A.Line = 0;
				}
				WriteReg(pc, ins.A, expr, outList, ctx);
				return pc + 1;
			}
			case Lua51Op.SETGLOBAL:
			{
				if (_stripped) CheckKeyAfterValue(pc, ins.Bx, ins.A);
				var target = MakeExpr(ExprKind.Global, line);
				target.Name = _f.Constants[ins.Bx].String;
				target.KIndex = ins.Bx;
				return EmitSet(pc, target, RegExpr(ins.A, pc), outList, ctx);
			}
			case Lua51Op.SETUPVAL:
			{
				var target = MakeExpr(ExprKind.Upvalue, line);
				target.Name = UpvalueName(ins.B);
				return EmitSet(pc, target, RegExpr(ins.A, pc), outList, ctx);
			}
			case Lua51Op.SETTABLE:
			{
				if (_stripped)
				{
					CheckRkConstant(pc, ins.B);
					CheckRkConstant(pc, ins.C);
					CheckOperandOrder(pc, ins.A, ins.B, ins.C);
				}
				ref Slot slot = ref _regs[ins.A];
				if (!IsLocalAt(pc, ins.A) && slot.Expr != null && slot.Expr.Kind == ExprKind.Table &&
					slot.Expr.TableOpen)
				{
					int hashFields = 0;
					foreach (TableField f in slot.Expr.Fields) hashFields += f.Key != null ? 1 : 0;
					if (_stripped && hashFields >= slot.Expr.HashBudget)
					{
						// The constructor is complete: this store assigns to a local table.
						throw DeclareInferredLocal("table-complete", ins.A, pc, ScopeEndFor(slot.Expr.Pc), slot.Expr, slot.Expr.Pc);
					}
					slot.Expr = slot.Expr.Clone();
					TableField field = new TableField();
					field.Key = RkExpr(ins.B, pc);
					field.Value = RkExpr(ins.C, pc);
					field.Line = line;
					field.Pc = pc;
					if (field.Value!.Kind == ExprKind.Table)
					{
						field.Value.TableOpen = false;
						if (field.Value.EndLine == 0) field.Value.EndLine = line;
					}
					slot.Expr.Fields.Add(field);
					return pc + 1;
				}
				if (_stripped && ins.B >= RkBit) CheckKeyAfterValue(pc, ins.B - RkBit, ins.C);
				var target = MakeExpr(ExprKind.Index, line);
				target.A = RegExpr(ins.A, pc);
				target.B = RkExpr(ins.B, pc);
				target.B.Line = 0;
				return EmitSet(pc, target, RkExpr(ins.C, pc), outList, ctx);
			}
			case Lua51Op.NEWTABLE:
			{
				var expr = MakeExpr(ExprKind.Table, line);
				expr.Pc = pc;
				expr.TableOpen = true;
				expr.ArrayBudget = FloatingByteToInt(ins.B);
				expr.HashBudget = FloatingByteToInt(ins.C);
				WriteReg(pc, ins.A, expr, outList, ctx);
				return pc + 1;
			}
			case Lua51Op.SELF:
			{
				if (_stripped)
				{
					CheckRkConstant(pc, ins.C);
					CheckFrameOnLocal(pc, ins.A);
					if (ctx.ValueReg < 0) CheckGapBelow(pc, ins.A);
					Slot objectSlot = _regs[ins.B];
					if (ins.B == ins.A - 1 && !IsLocalAt(pc, ins.B) && objectSlot.Expr != null && !objectSlot.Multi)
					{
						// A temporary object would have been freed and the
						// frame opened in its register (luaK_self).
						throw DeclareInferredLocal("self-object", ins.B, pc, ScopeEndFor(objectSlot.Pc), objectSlot.Expr, objectSlot.Pc);
					}
				}
				Expr obj = RegExpr(ins.B, pc);
				var method = MakeExpr(ExprKind.MethodCall, line);
				method.NameLine = line;
				method.A = obj;
				Expr key = RkExpr(ins.C, pc);
				if (key.Kind != ExprKind.String || !IsIdentifier(key.Name))
				{
					throw Fail(pc, "SELF with a non-identifier method name");
				}
				method.Name = key.Name;
				method.KIndex = key.KIndex;
				_regs[ins.A] = new Slot(method, pc, false);
				_regs[ins.A + 1] = new Slot(obj, pc, true);
				_written[ins.A] = true;
				_written[ins.A + 1] = true;
				return pc + 1;
			}
			case Lua51Op.ADD: case Lua51Op.SUB: case Lua51Op.MUL: case Lua51Op.DIV: case Lua51Op.MOD: case Lua51Op.POW:
			{
				if (_stripped)
				{
					CheckRkConstant(pc, ins.B);
					CheckRkConstant(pc, ins.C);
					CheckOperandOrder(pc, ins.B, ins.C);
					// Two numeric constants would have been folded by the compiler.
					bool bNum = ins.B >= RkBit ? _f.Constants[ins.B - RkBit].Type == Constant.Kind.Number
						: PendingIsNumber(ins.B, pc);
					bool cNum = ins.C >= RkBit ? _f.Constants[ins.C - RkBit].Type == Constant.Kind.Number
						: PendingIsNumber(ins.C, pc);
					if (bNum && cNum)
					{
						if (ins.B < RkBit) throw DeclareInferredLocal("fold-b", ins.B, pc, ScopeEndFor(_regs[ins.B].Pc), _regs[ins.B].Expr, _regs[ins.B].Pc);
						if (ins.C < RkBit) throw DeclareInferredLocal("fold-c", ins.C, pc, ScopeEndFor(_regs[ins.C].Pc), _regs[ins.C].Expr, _regs[ins.C].Pc);
					}
				}
				var expr = MakeExpr(ExprKind.Binary, line);
				expr.Op = ins.Op;
				expr.A = RkExpr(ins.B, pc);
				expr.B = RkExpr(ins.C, pc);
				WriteReg(pc, ins.A, expr, outList, ctx);
				return pc + 1;
			}
			case Lua51Op.UNM: case Lua51Op.NOT: case Lua51Op.LEN:
			{
				if (_stripped && ins.Op == Lua51Op.UNM && PendingIsNumber(ins.B, pc))
				{
					// `-<constant>` folds; the operand was a local.
					throw DeclareInferredLocal("unm-fold", ins.B, pc, ScopeEndFor(_regs[ins.B].Pc), _regs[ins.B].Expr, _regs[ins.B].Pc);
				}
				Expr operand = RegExpr(ins.B, pc);
				Expr expr;
				if (ins.Op == Lua51Op.NOT)
				{
					expr = MakeExpr(ExprKind.Not, line);
					expr.A = operand;
					expr.ExplicitNot = true;
				}
				else
				{
					expr = MakeExpr(ExprKind.Unary, line);
					expr.Op = ins.Op;
					expr.A = operand;
				}
				WriteReg(pc, ins.A, expr, outList, ctx);
				return pc + 1;
			}
			case Lua51Op.CONCAT:
			{
				Expr? result = null;
				for (int r = ins.C; r >= ins.B; --r)
				{
					Expr piece = RegExpr(r, pc);
					if (result == null)
					{
						result = piece;
					}
					else
					{
						var expr = MakeExpr(ExprKind.Binary, line);
						expr.Op = Lua51Op.CONCAT;
						expr.A = piece;
						expr.B = result;
						result = expr;
					}
				}
				WriteReg(pc, ins.A, result, outList, ctx);
				return pc + 1;
			}
			case Lua51Op.CALL:
			case Lua51Op.TAILCALL:
			{
				if (_stripped)
				{
					CheckFrameOnLocal(pc, ins.A);
					List<int> order = new();
					int last = ins.B == 0 ? PendingTop(ins.A + 1) : ins.A + ins.B;
					for (int r = ins.A; r < last; ++r) order.Add(r);
					CheckOperandOrder(pc, order);
				}
				Expr call = BuildCall(pc);
				if (ins.Op == Lua51Op.TAILCALL)
				{
					if (pc + 1 >= _ins.Length || _ins[pc + 1].Op != Lua51Op.RETURN)
					{
						throw Fail(pc, "TAILCALL not followed by RETURN");
					}
					call.NResults = -1;
					// The RETURN is coded after the call's closing token: its line is that token's.
					if (call.EndLine == 0 && _ins[pc + 1].Line > call.Line) call.EndLine = _ins[pc + 1].Line;
					var stmt = new Stmt();
					stmt.Kind = StmtKind.Return;
					stmt.Values.Add(call);
					stmt.Line = FirstLine(call);
					outList.Add(stmt);
					return pc + 2;
				}
				if (ins.C == 1)
				{
					var stmt = new Stmt();
					stmt.Kind = StmtKind.Call;
					stmt.Values.Add(call);
					stmt.Line = FirstLine(call);
					outList.Add(stmt);
					return pc + 1;
				}
				call.NResults = ins.C == 0 ? -1 : ins.C - 1;
				WriteReg(pc, ins.A, call, outList, ctx);
				if (ins.C > 2)
				{
					for (int r = ins.A + 1; r <= ins.A + ins.C - 2; ++r)
					{
						_regs[r] = new Slot(call, pc, true);
					}
				}
				return pc + 1;
			}
			case Lua51Op.RETURN:
			{
				var stmt = new Stmt();
				stmt.Kind = StmtKind.Return;
				stmt.Line = line;
				if (ins.B == 0)
				{
					int top = PendingTop(ins.A);
					for (int r = ins.A; r < top; ++r)
					{
						CollectValue(r, pc, stmt.Values);
					}
				}
				else
				{
					for (int r = ins.A; r < ins.A + ins.B - 1; ++r)
					{
						CollectValue(r, pc, stmt.Values);
					}
				}
				if (stmt.Values.Count != 0)
				{
					// The RETURN is coded after the last value's closing token.
					Expr lastValue = stmt.Values[^1];
					if ((lastValue.Kind == ExprKind.Call || lastValue.Kind == ExprKind.MethodCall) &&
						lastValue.EndLine == 0 && line > lastValue.Line)
					{
						lastValue.EndLine = line;
					}
					stmt.Line = FirstLine(stmt.Values[0]);
				}
				outList.Add(stmt);
				return pc + 1;
			}
			case Lua51Op.VARARG:
			{
				var expr = MakeExpr(ExprKind.Vararg, line);
				expr.NResults = ins.B == 0 ? -1 : ins.B - 1;
				WriteReg(pc, ins.A, expr, outList, ctx);
				for (int r = ins.A + 1; r < ins.A + ins.B - 1; ++r)
				{
					_regs[r] = new Slot(expr, pc, true);
				}
				return pc + 1;
			}
			case Lua51Op.SETLIST:
			{
				ref Slot slot = ref _regs[ins.A];
				if (slot.Expr == null || slot.Expr.Kind != ExprKind.Table || !slot.Expr.TableOpen)
				{
					throw Fail(pc, "SETLIST on a register that is not an open constructor");
				}
				slot.Expr = slot.Expr.Clone();
				int c = ins.C;
				int next = pc + 1;
				if (c == 0)
				{
					c = (int)_f.Code[pc + 1];
					next = pc + 2;
				}
				int count = ins.B == 0 ? PendingTop(ins.A + 1) - (ins.A + 1) : ins.B;
				for (int i = 0; i < count; ++i)
				{
					int r = ins.A + 1 + i;
					TableField field = new TableField();
					ref Slot item = ref _regs[r];
					if (item.Expr == null) throw Fail(pc, "SETLIST item missing");
					if (item.Multi)
					{
						item = new Slot();
						continue;
					}
					field.Value = item.Expr;
					field.Line = item.Expr.Line;
					field.Pc = item.Pc;
					item = new Slot();
					slot.Expr.Fields.Add(field);
				}
				slot.Expr.TableArrayCount += count;
				slot.Expr.EndLine = line;
				_ = c;
				return next;
			}
			case Lua51Op.CLOSE:
				return pc + 1;
			case Lua51Op.CLOSURE:
			{
				Proto child = _f.Protos[ins.Bx];
				List<string> names = new();
				for (int i = 0; i < child.Nups; ++i)
				{
					ref readonly Instr bind = ref _ins[pc + 1 + i];
					string name;
					if (i < child.Upvalues.Count && !string.IsNullOrEmpty(child.Upvalues[i]))
					{
						name = child.Upvalues[i];
					}
					else if (bind.Op == Lua51Op.MOVE)
					{
						if (_stripped && !IsLocalAt(pc, bind.B))
						{
							if (!_written[bind.B]) throw HoistNilLocal(bind.B, pc, "captured-unwritten");
							int writePc = LastWriteBefore(bind.B, pc);
							Slot captured = _regs[bind.B];
							throw DeclareInferredLocal("captured", bind.B, pc, ScopeEndFor(writePc),
								captured.Expr != null ? captured.Expr : _lastValue[bind.B]);
						}
						name = LocalNameAt(pc, bind.B);
					}
					else if (bind.Op == Lua51Op.GETUPVAL)
					{
						name = UpvalueName(bind.B);
					}
					else
					{
						throw Fail(pc, "bad closure upvalue binding");
					}
					names.Add(name);
				}
				var expr = MakeExpr(ExprKind.Function, line);
				SortedSet<string> childReserved = new SortedSet<string>(_reservedNames, StringComparer.Ordinal);
				foreach (string name in names) childReserved.Add(name);
				foreach (LocVar v in _locvars) childReserved.Add(v.Name);
				expr.Func = RunInferring(child, names, _path + "." + ins.Bx.ToString(), childReserved);
				WriteReg(pc, ins.A, expr, outList, ctx);
				return pc + 1 + child.Nups;
			}
			default:
				throw Fail(pc, "unexpected opcode in straight-line code");
		}
	}

	// The temporaries of a chain's leaves are written in leaf order: a leaf
	// operand written before an earlier leaf's operands was a local.
	// (C++ `int* last_write`; its one caller passes a non-null address.)
	private void CheckLeafOrder(int pc, ref int lastWrite)
	{
		if (!_stripped) return;
		ref readonly Instr ins = ref _ins[pc];
		int[] operands;
		switch (ins.Op)
		{
			case Lua51Op.TEST: operands = new[] { ins.A }; break;
			case Lua51Op.TESTSET: operands = new[] { ins.B }; break;
			case Lua51Op.EQ: case Lua51Op.LT: case Lua51Op.LE: operands = new[] { ins.B, ins.C }; break;
			default: return;
		}
		int newest = lastWrite;
		foreach (int r in operands)
		{
			if (r >= RkBit || IsLocalAt(pc, r)) continue;
			Slot slot = _regs[r];
			if (slot.Expr == null || slot.Multi) continue;
			bool tested = ins.Op == Lua51Op.TEST || ins.Op == Lua51Op.TESTSET;
			if (tested && (slot.Expr.Kind == ExprKind.And || slot.Expr.Kind == ExprKind.Or))
			{
				// An and/or operand of a test keeps its jumps (luaK_goiftrue never
				// discharges a jump list): a materialised one was a local's value.
				throw DeclareInferredLocal("tested-value", r, pc, ScopeEndFor(slot.Pc), slot.Expr, slot.Pc);
			}
			if (slot.Pc < lastWrite)
			{
				throw DeclareInferredLocal("leaf-order", r, pc, ScopeEndFor(slot.Pc), slot.Expr, slot.Pc);
			}
			newest = Math.Max(newest, slot.Pc);
		}
		lastWrite = newest;
	}

	// A store's target is parsed before its value, so its name constant is
	// created first. A key first used by the store, created after a constant
	// the value's loads use first, was parsed after the value: the value is
	// a local declared by an earlier statement.
	private void CheckKeyAfterValue(int pc, int key, int valueReg)
	{
		if (valueReg >= RkBit || IsLocalAt(pc, valueReg)) return;
		Slot slot = _regs[valueReg];
		if (slot.Expr == null || slot.Multi || slot.Pc < 0 || slot.Pc >= pc) return;
		if (FirstConstantUse(key) != pc) return;
		// The value's loads begin at its last fresh write of the register
		// (one that does not read it): earlier frame loads are another value's.
		int region = FrameStart(pc, valueReg);
		for (int q = pc - 1; q > region; --q)
		{
			if (InstructionWrites(q, valueReg) && !IsOperandOf(q, valueReg))
			{
				region = q;
				break;
			}
		}
		for (int p = region; p < pc; ++p)
		{
			foreach (int c in ConstantsOf(p))
			{
				if (c < key && FirstConstantUse(c) == p)
				{
					throw DeclareInferredLocal("key-after-value", valueReg, pc, ScopeEndFor(slot.Pc), slot.Expr, slot.Pc);
				}
			}
		}
	}

	// A LOADK'd constant consumed where an RK operand could name it directly
	// was a local (the compiler only spills constants past index 255).
	private void CheckRkConstant(int pc, int operand)
	{
		if (operand >= RkBit || IsLocalAt(pc, operand)) return;
		Slot slot = _regs[operand];
		if (slot.Expr == null || slot.Pc < 0) return;
		// `a = "x", y`: values before the last go to registers (exp2nextreg)
		if (ExtraValuesSource(pc, out int extras) == operand && extras > 0) return;
		ref readonly Instr load = ref _ins[slot.Pc];
		if (load.Op == Lua51Op.LOADK)
		{
			if (slot.Expr.Kind != ExprKind.Number && slot.Expr.Kind != ExprKind.String) return;
			if (load.Bx > 255) return;
			// A string names its constant by index; a number goes through the
			// pool size instead (luaK_exp2RK), which the consumer's own key
			// constant has already grown.
			if (slot.Expr.Kind == ExprKind.Number && PoolSizeWhenDischarged(slot.Pc, pc, load.Bx) > 255) return;
		}
		else if (load.Op == Lua51Op.LOADBOOL && load.C == 0 && load.A == operand &&
				 (slot.Expr.Kind == ExprKind.True || slot.Expr.Kind == ExprKind.False))
		{
			// a literal, not the pair that materialises a comparison
			if (PoolSizeWhenDischarged(slot.Pc, pc, -1) > 255) return;
		}
		else if (load.Op == Lua51Op.LOADNIL && load.A == operand && load.B == operand &&
				 slot.Expr.Kind == ExprKind.Nil)
		{
			if (PoolSizeWhenDischarged(slot.Pc, pc, -1) > 255) return;
		}
		else
		{
			return;
		}
		throw DeclareInferredLocal("rk-const", operand, pc, ScopeEndFor(slot.Pc), slot.Expr, slot.Pc);
	}

	// Number of constants in the pool when the value loaded at `load_pc`
	// (constant `own`, or -1) was discharged for the instruction at `use_pc`:
	// every constant referenced earlier, plus the consumer's constant
	// operands parsed before the value (a store's key).
	private int PoolSizeWhenDischarged(int loadPc, int usePc, int own)
	{
		int size = ConstantsBefore(loadPc);
		ref readonly Instr use = ref _ins[usePc];
		if (use.Op == Lua51Op.SETTABLE && use.B >= RkBit) size = Math.Max(size, use.B - RkBit + 1);
		// A store whose value was still being built (a pending register
		// below the load) had its key created before the value.
		int loaded = _ins[loadPc].A;
		for (int r = 0; r < loaded && r < _regs.Length; ++r)
		{
			Slot below = _regs[r];
			if (below.Expr == null || below.Pc >= loadPc || IsLocalAt(loadPc, r)) continue;
			for (int q = usePc; q < _ins.Length; ++q)
			{
				ref readonly Instr ins = ref _ins[q];
				if (ins.Op == Lua51Op.SETTABLE && ins.C == r)
				{
					if (ins.B >= RkBit) size = Math.Max(size, ins.B - RkBit + 1);
					break;
				}
				if (ins.Op == Lua51Op.SETGLOBAL && ins.A == r)
				{
					size = Math.Max(size, ins.Bx + 1);
					break;
				}
				if (InstructionWrites(q, r)) break;
			}
		}
		if (own >= 0 && own < size) return size;  // already in the pool
		if (own >= 0) return own;                  // created by this discharge
		return size;
	}

	// Size of the constant pool when the instruction at `pc` was emitted:
	// constants are numbered in order of first use.
	private int ConstantsBefore(int pc)
	{
		int maxIndex = -1;
		void Rk(int v) { if (v >= RkBit) maxIndex = Math.Max(maxIndex, v - RkBit); }
		for (int p = 0; p < pc; ++p)
		{
			ref readonly Instr ins = ref _ins[p];
			switch (ins.Op)
			{
				case Lua51Op.LOADK: case Lua51Op.GETGLOBAL: case Lua51Op.SETGLOBAL:
					maxIndex = Math.Max(maxIndex, ins.Bx);
					break;
				case Lua51Op.GETTABLE: case Lua51Op.SELF:
					Rk(ins.C);
					break;
				case Lua51Op.SETTABLE: case Lua51Op.EQ: case Lua51Op.LT: case Lua51Op.LE:
				case Lua51Op.ADD: case Lua51Op.SUB: case Lua51Op.MUL: case Lua51Op.DIV: case Lua51Op.MOD: case Lua51Op.POW:
					Rk(ins.B);
					Rk(ins.C);
					break;
				default:
					break;
			}
		}
		return maxIndex + 1;
	}

	// Whether register `reg` written at `pc` is read by an instruction within
	// the same straight-line stretch before being written again.
	private bool IsOperandOfLater(int pc, int reg)
	{
		for (int p = pc + 1; p < _ins.Length; ++p)
		{
			if (IsOperandOf(p, reg)) return true;
			if (InstructionWrites(p, reg) || _ins[p].Op == Lua51Op.JMP || IsTestOp(_ins[p].Op) ||
				_ins[p].Op == Lua51Op.RETURN)
			{
				return false;
			}
		}
		return false;
	}

	private bool PendingIsNumber(int reg, int pc)
	{
		if (IsLocalAt(pc, reg)) return false;
		Slot slot = _regs[reg];
		return slot.Expr != null && slot.Expr.Kind == ExprKind.Number;
	}

	// Temporaries are written in evaluation order: an operand written before
	// one that the source evaluates earlier was a local.
	private void CheckOperandOrder(int pc, params int[] operands)
	{
		CheckOperandOrder(pc, (IReadOnlyList<int>)operands);
	}

	private void CheckOperandOrder(int pc, IReadOnlyList<int> operands)
	{
		int prevPc = -1;
		foreach (int r in operands)
		{
			if (r >= RkBit || IsLocalAt(pc, r)) continue;
			Slot slot = _regs[r];
			if (slot.Expr == null || slot.Multi) continue;
			if (slot.Pc < prevPc)
			{
				throw DeclareInferredLocal("operand-order", r, pc, ScopeEndFor(slot.Pc), slot.Expr, slot.Pc);
			}
			prevPc = slot.Pc;
		}
	}

	private string UpvalueName(int index)
	{
		if (index < _upvalueNames.Count) return _upvalueNames[index];
		return "upvalue_" + index.ToString();
	}

	private void CollectValue(int reg, int pc, List<Expr> values)
	{
		ref Slot slot = ref _regs[reg];
		if ((IsLocalAt(pc, reg) || !_written[reg] || _stripped) && slot.Expr == null)
		{
			values.Add(RegExpr(reg, pc));
			return;
		}
		if (slot.Expr == null) throw Fail(pc, "missing value in register " + reg.ToString());
		if (slot.Multi)
		{
			slot = new Slot();
			return;
		}
		values.Add(slot.Expr);
		slot = new Slot();
	}

	private Expr BuildCall(int pc)
	{
		ref readonly Instr ins = ref _ins[pc];
		ref Slot fslot = ref _regs[ins.A];
		Expr call;
		int firstArg = ins.A + 1;
		if (!IsLocalAt(pc, ins.A) && fslot.Expr != null && fslot.Expr.Kind == ExprKind.MethodCall)
		{
			call = fslot.Expr.Clone();  // speculative probes share nodes
			fslot = new Slot();
			_regs[ins.A + 1] = new Slot();
			firstArg = ins.A + 2;
		}
		else
		{
			call = MakeExpr(ExprKind.Call, ins.Line);
			call.A = RegExpr(ins.A, pc);
		}
		call.Line = ins.Line;
		int last = ins.B == 0 ? PendingTop(firstArg) : ins.A + ins.B;
		for (int r = firstArg; r < last; ++r)
		{
			CollectValue(r, pc, call.Args);
		}
		if (TraceEnabled) Trace($"{_path} call at {pc}: first_arg={firstArg} last={last} args={call.Args.Count}");
		// The last argument is discharged after `)` is read when it was still a pending load
		// (a field, global, upvalue, constant or local): its instruction carries the line of `)`.
		if (call.Args.Count != 0 && pc > 0 && call.EndLine == 0)
		{
			Instr prev = _ins[pc - 1];
			bool pendingLoad = prev.Op is Lua51Op.GETTABLE or Lua51Op.GETGLOBAL or Lua51Op.GETUPVAL or
				Lua51Op.LOADK or Lua51Op.LOADBOOL or Lua51Op.LOADNIL or Lua51Op.MOVE;
			if (pendingLoad && prev.A == last - 1 && prev.Line > call.Line) call.EndLine = prev.Line;
		}
		if (ins.B != 0 && call.Args.Count != 0)
		{
			Expr tail = call.Args[^1];
			if ((tail.Kind == ExprKind.Call || tail.Kind == ExprKind.MethodCall ||
				 tail.Kind == ExprKind.Vararg) && tail.NResults == 1)
			{
				tail.ParenCall = true;
			}
		}
		return call;
	}

	private int EmitSet(int pc, Expr target, Expr value, List<Stmt> outList, in Ctx ctx)
	{
		int first = outList.Count;
		List<Expr> extras = TakeExtraValues(pc);
		EmitAssign(pc, target, value, outList, ctx);
		int next = pc + 1;
		// C++ GroupGuard: sets grouping_ for the rest of the function, clears it on any exit.
		_grouping = true;
		try
		{
			// Older temporaries left pending belong to a multiple assignment whose
			// remaining stores follow immediately.
			int lowest = -1;
			for (int r = 0; r < _regs.Length; ++r)
			{
				if (_regs[r].Expr != null && !IsLocalAt(pc, r))
				{
					lowest = r;
					break;
				}
			}
			while (lowest >= 0 && next < _ins.Length)
			{
				ref readonly Instr ins = ref _ins[next];
				bool isStore = ins.Op == Lua51Op.SETGLOBAL || ins.Op == Lua51Op.SETUPVAL ||
							   ins.Op == Lua51Op.SETTABLE ||
							   (ins.Op == Lua51Op.MOVE && IsLocalAt(next, ins.A));
				if (!isStore) break;
				int before = outList.Count;
				next = ProcessSimpleNoGroup(next, outList, ctx);
				if (outList.Count != before + 1) break;
				lowest = -1;
				for (int r = 0; r < _regs.Length; ++r)
				{
					if (_regs[r].Expr != null && !IsLocalAt(next, r))
					{
						lowest = r;
						break;
					}
				}
			}
			if (outList.Count > first + 1)
			{
				GroupAssignments(outList, first);
			}
			if (extras.Count != 0)
			{
				if (outList.Count != first + 1 || outList[first].Kind != StmtKind.Assign)
				{
					throw Fail(pc, "extra assignment values without an assignment");
				}
				outList[first].Values.AddRange(extras);
			}
			return next;
		}
		finally
		{
			_grouping = false;
		}
	}

	// `a = x, y`: values beyond the targets are evaluated into the registers
	// above the first store's source and dropped (restassign's
	// `freereg -= nexps - nvars`). Takes them, lowest register first, when
	// each is a temporary dead after the store.
	private List<Expr> TakeExtraValues(int pc)
	{
		var extras = new List<Expr>();
		int src = ExtraValuesSource(pc, out int count);
		for (int q = src + 1; q <= src + count; ++q)
		{
			extras.Add(_regs[q].Expr!);
			_regs[q] = new Slot();
		}
		return extras;
	}

	// The store's source register and how many dropped extra values sit
	// above it (0 when the instruction is no such store).
	private int ExtraValuesSource(int pc, out int count)
	{
		count = 0;
		Instr ins = _ins[pc];
		int src;
		switch (ins.Op)
		{
			case Lua51Op.SETGLOBAL: case Lua51Op.SETUPVAL: src = ins.A; break;
			case Lua51Op.SETTABLE: src = ins.C < RkBit ? ins.C : -1; break;
			case Lua51Op.MOVE: src = ins.B; break;
			default: src = -1; break;
		}
		if (src < 0 || IsLocalAt(pc, src)) return -1;
		int lastPc = -1;
		int r = src + 1;
		for (; r < _regs.Length; ++r)
		{
			Slot slot = _regs[r];
			if (slot.Expr == null || slot.Multi || IsLocalAt(pc, r) || slot.Pc >= pc || slot.Pc <= lastPc) break;
			if (!DeadAfter(pc, r)) break;
			lastPc = slot.Pc;
		}
		count = r - src - 1;
		return src;
	}

	// Whether some path from `start` reads `reg` before writing it.
	private bool LiveAt(int start, int reg)
	{
		var seen = new bool[_ins.Length];
		var work = new Stack<int>();
		work.Push(start);
		while (work.Count != 0)
		{
			int q = work.Pop();
			if (q < 0 || q >= _ins.Length || seen[q]) continue;
			seen[q] = true;
			if (IsOperandOf(q, reg)) return true;
			if (InstructionWrites(q, reg)) continue;
			Instr ins = _ins[q];
			switch (ins.Op)
			{
				case Lua51Op.JMP:
					work.Push(q + 1 + ins.SBx);
					break;
				case Lua51Op.EQ: case Lua51Op.LT: case Lua51Op.LE: case Lua51Op.TEST: case Lua51Op.TESTSET:
					work.Push(q + 1);
					work.Push(q + 2);
					break;
				case Lua51Op.LOADBOOL:
					work.Push(ins.C != 0 ? q + 2 : q + 1);
					break;
				case Lua51Op.FORLOOP: case Lua51Op.FORPREP:
					work.Push(q + 1);
					work.Push(q + 1 + ins.SBx);
					break;
				case Lua51Op.TFORLOOP:
					work.Push(q + 1);
					work.Push(q + 2);
					break;
				case Lua51Op.RETURN: case Lua51Op.TAILCALL:
					break;
				default:
					work.Push(q + 1);
					break;
			}
		}
		return false;
	}

	// Whether `reg` is written before it is read again, in pc order from `pc`.
	private bool DeadAfter(int pc, int reg)
	{
		for (int q = pc + 1; q < _ins.Length; ++q)
		{
			if (IsOperandOf(q, reg)) return false;
			if (InstructionWrites(q, reg)) return true;
		}
		return true;
	}

	private int ProcessSimpleNoGroup(int pc, List<Stmt> outList, in Ctx ctx)
	{
		ref readonly Instr ins = ref _ins[pc];
		switch (ins.Op)
		{
			case Lua51Op.SETGLOBAL:
			{
				var target = MakeExpr(ExprKind.Global, ins.Line);
				target.Name = _f.Constants[ins.Bx].String;
				EmitAssign(pc, target, RegExpr(ins.A, pc), outList, ctx);
				return pc + 1;
			}
			case Lua51Op.SETUPVAL:
			{
				var target = MakeExpr(ExprKind.Upvalue, ins.Line);
				target.Name = UpvalueName(ins.B);
				EmitAssign(pc, target, RegExpr(ins.A, pc), outList, ctx);
				return pc + 1;
			}
			case Lua51Op.SETTABLE:
			{
				var target = MakeExpr(ExprKind.Index, ins.Line);
				target.A = RegExpr(ins.A, pc);
				target.B = RkExpr(ins.B, pc);
				target.B.Line = 0;
				EmitAssign(pc, target, RkExpr(ins.C, pc), outList, ctx);
				return pc + 1;
			}
			case Lua51Op.MOVE:
				WriteReg(pc, ins.A, RegExpr(ins.B, pc), outList, ctx);
				return pc + 1;
			default:
				return ProcessSimple(pc, outList, ctx);
		}
	}

	// ---- loops ---------------------------------------------------------------

	// `loop` aliases an element of _loops (C++ Loop&); _loops is only grown by BuildLoops
	// in the constructor, so the reference stays valid through the nested block builds.
	private void BuildLoop(ref Loop loop, List<Stmt> outList, in Ctx ctx)
	{
		_ = ctx;
		var stmt = new Stmt();
		switch (loop.Kind)
		{
			case LoopKind.NumFor:
			{
				ref readonly Instr prep = ref _ins[loop.Start];
				stmt.Kind = StmtKind.NumFor;
				for (int r = prep.A; r < prep.A + 3; ++r)
				{
					ref Slot slot = ref _regs[r];
					if (slot.Expr == null) throw Fail(loop.Start, "numeric for without initialisers");
					stmt.Values.Add(slot.Expr);
					slot = new Slot();
				}
				stmt.KeywordLine = _ins[loop.End].Line;
				stmt.Line = FirstLine(stmt.Values[0]);
				ClaimLoopLocals(loop.Start, prep.A, 3);
				ClaimLoopLocals(loop.BodyStart, prep.A, 3);
				stmt.Names.Add(LocalNameAt(loop.BodyStart, prep.A + 3));
				ClaimLoopLocals(loop.BodyStart, prep.A + 3, 1);
				stmt.Body = BuildBlock(loop.BodyStart, loop.BodyEnd, loop.Exit, loop.BodyEnd);
				break;
			}
			case LoopKind.GenFor:
			{
				ref readonly Instr tfor = ref _ins[loop.BodyEnd];
				stmt.Kind = StmtKind.GenFor;
				int baseReg = tfor.A;
				for (int r = baseReg; r < baseReg + 3; ++r)
				{
					ref Slot slot = ref _regs[r];
					if (slot.Expr == null) break;
					if (!slot.Multi) stmt.Values.Add(slot.Expr);
					slot = new Slot();
				}
				// Trailing nils supplied by adjustment are implicit.
				while (stmt.Values.Count > 1 && stmt.Values[^1].Kind == ExprKind.Nil)
				{
					stmt.Values.RemoveAt(stmt.Values.Count - 1);
				}
				stmt.KeywordLine = tfor.Line;
				stmt.Line = stmt.Values.Count == 0 ? tfor.Line : FirstLine(stmt.Values[0]);
				ClaimLoopLocals(loop.Start, baseReg, 3);
				ClaimLoopLocals(loop.BodyStart, baseReg, 3);
				for (int i = 0; i < tfor.C; ++i)
				{
					stmt.Names.Add(LocalNameAt(loop.BodyStart, baseReg + 3 + i));
				}
				ClaimLoopLocals(loop.BodyStart, baseReg + 3, tfor.C);
				stmt.Body = BuildBlock(loop.BodyStart, loop.BodyEnd, loop.Exit, loop.BodyEnd);
				HintEndLine(stmt.Body, _ins[loop.End].Line);
				break;
			}
			case LoopKind.While:
			{
				stmt.Kind = StmtKind.While;
				int bodyStart = loop.Start;
				{
					List<Leaf> leaves = new();
					int truePc = 0;
					int falsePc = 0;
					if (_stripped) ShrinkLocalsInCondition(loop.Start, loop.Exit);
					_condFloor = loop.Start;
					int after = GatherChain(loop.Start, loop.End, leaves, out truePc, out falsePc,
						/*allowValue:*/ false);
					_condFloor = -1;
					for (int i = 0; i + 1 < leaves.Count; ++i)
					{
						ref Leaf leaf = ref CollectionsMarshal.AsSpan(leaves)[i];
						if (leaf.Target != truePc && leaf.Target == ResolveThreaded(truePc))
						{
							leaf.Target = truePc;
						}
					}
					if (leaves.Count != 0 && JumpsTo(falsePc, loop.Exit))
					{
						stmt.Cond = BuildCondition(leaves, 0, leaves.Count, truePc, falsePc);
						stmt.ThenLine = KeywordTestLine(leaves);
						bodyStart = after;
					}
					else
					{
						// Not the loop condition after all: restore and treat as `while true`.
						RestoreSnapshot();
					}
				}
				if (stmt.Cond == null)
				{
					stmt.Cond = MakeExpr(ExprKind.True, _ins[loop.Start].Line);
				}
				stmt.Line = FirstLine(stmt.Cond);
				PushClosingJump(loop.End);
				stmt.Body = BuildBodyBlock(bodyStart, loop.BodyEnd, loop.Exit);
				PopClosingJump();
				HintEndLine(stmt.Body, _ins[loop.End].Line);
				break;
			}
			case LoopKind.Repeat:
			{
				stmt.Kind = StmtKind.Repeat;
				stmt.Line = _ins[loop.Start].Line;
				// The until-condition is the trailing chain whose jumps go back to
				// the loop start. Build the body up to it.
				int condStart = FindRepeatConditionStart(in loop);
				PushClosingJump(loop.End);
				stmt.Body = BuildBlock(loop.Start, condStart, loop.Exit, condStart);
				PopClosingJump();
				List<Leaf> leaves = new();
				int truePc = 0;
				int falsePc = 0;
				_condFloor = condStart;
				int after = GatherChain(condStart, loop.End + 1, leaves, out truePc, out falsePc,
					/*allowValue:*/ false);
				_condFloor = -1;
				if (leaves.Count == 0 || after != loop.End + 1)
				{
					throw Fail(loop.End, "repeat condition not recognised");
				}
				// For repeat the back jump is the false exit and falling through exits.
				stmt.Cond = BuildCondition(leaves, 0, leaves.Count, loop.Exit, loop.Start);
				break;
			}
		}
		outList.Add(stmt);
	}

	private void ClaimLoopLocals(int pc, int firstReg, int count)
	{
		for (int r = firstReg; r < firstReg + count; ++r)
		{
			int index = LocalIndexAt(pc, r);
			if (index >= 0) _claimedLocvars.Add(index);
		}
	}

	// The until condition of a repeat loop starts after the last statement of
	// the body. Walk back from the closing jump over the condition's leaves and
	// their operand loads.
	private int FindRepeatConditionStart(in Loop loop)
	{
		// Find the earliest pc from which every instruction up to loop.end is a
		// pure operand load or a test/jump pair whose targets are the loop
		// start, the loop exit, or interior points.
		int start = loop.End - 1;  // the last test
		for (;;)
		{
			int candidate = start;
			// extend backwards over operand loads
			while (candidate - 1 >= loop.Start && IsOperandLoad(candidate - 1, loop))
			{
				--candidate;
			}
			// is there another leaf of this chain before?
			if (candidate - 2 >= loop.Start && IsTestOp(_ins[candidate - 2].Op) &&
				_ins[candidate - 1].Op == Lua51Op.JMP)
			{
				int target = candidate + _ins[candidate - 1].SBx;
				if (target == loop.Start || target == loop.Exit ||
					(target > candidate - 1 && target <= loop.End + 1))
				{
					start = candidate - 2;
					continue;
				}
			}
			return candidate;
		}
	}

	private bool IsOperandLoad(int pc, in Loop loop)
	{
		_ = loop;
		ref readonly Instr ins = ref _ins[pc];
		switch (ins.Op)
		{
			case Lua51Op.MOVE: case Lua51Op.LOADK: case Lua51Op.GETUPVAL: case Lua51Op.GETGLOBAL: case Lua51Op.GETTABLE:
			case Lua51Op.ADD: case Lua51Op.SUB: case Lua51Op.MUL: case Lua51Op.DIV: case Lua51Op.MOD: case Lua51Op.POW:
			case Lua51Op.UNM: case Lua51Op.NOT: case Lua51Op.LEN: case Lua51Op.CONCAT: case Lua51Op.SELF:
			case Lua51Op.NEWTABLE: case Lua51Op.VARARG:
				return !IsLocalAt(pc, ins.A);
			case Lua51Op.LOADBOOL:
				return !IsLocalAt(pc, ins.A) && ins.C == 0;
			case Lua51Op.CALL:
				return ins.C != 1 && !IsLocalAt(pc, ins.A);
			case Lua51Op.SETTABLE:
			case Lua51Op.SETLIST:
				return !IsLocalAt(pc, ins.A) && HasOpenTableBefore(pc, ins.A);
			case Lua51Op.CLOSURE:
				return !IsLocalAt(pc, ins.A);
			default:
				return false;
		}
	}

	// Whether an earlier NEWTABLE into `reg` is still being built at `pc`.
	private bool HasOpenTableBefore(int pc, int reg)
	{
		for (int p = pc - 1; p >= 0; --p)
		{
			ref readonly Instr ins = ref _ins[p];
			if (ins.Op == Lua51Op.NEWTABLE && ins.A == reg) return true;
			if (ins.Op == Lua51Op.JMP || IsTestOp(ins.Op) || ins.Op == Lua51Op.CALL || ins.Op == Lua51Op.RETURN)
			{
				if (ins.Op == Lua51Op.CALL && ins.A > reg) continue;
				return false;
			}
			if (ins.A == reg && ins.Op != Lua51Op.SETTABLE && ins.Op != Lua51Op.SETLIST) return false;
		}
		return false;
	}

	// ---- conditions ----------------------------------------------------------

	private void TakeSnapshot() { _snapshot = (Slot[])_regs.Clone(); }
	private void RestoreSnapshot() { _regs = (Slot[])_snapshot.Clone(); }

	/// <summary>
	/// Kore codes a condition's last test after reading `then` / `do` (Lua 5.1 codes it before):
	/// a TEST/TESTSET ending the chain carries the keyword's line. Comparisons are coded with
	/// their operands, so they say nothing about the keyword.
	/// </summary>
	private int KeywordTestLine(List<Leaf> leaves)
	{
		if (leaves.Count == 0) return 0;
		Leaf last = leaves[^1];
		return last.Op == Lua51Op.TEST || last.Op == Lua51Op.TESTSET ? _ins[last.Pc].Line : 0;
	}

	private Leaf MakeLeaf(int pc)
	{
		// Copy of the instruction: ins_ is never written after construction.
		Instr ins = _ins[pc];
		Leaf leaf = new Leaf();
		leaf.Pc = pc;
		leaf.Op = ins.Op;
		leaf.Target = pc + 2 + _ins[pc + 1].SBx;
		void PinParen(Expr? e)
		{
			if (e == null || (e.Kind != ExprKind.Call && e.Kind != ExprKind.MethodCall) || e.EndLine != 0 ||
				e.Args.Count == 0)
			{
				return;
			}
			ExprKind k = e.Args[^1].Kind;
			if (k == ExprKind.Function || k == ExprKind.Table) e.EndLine = ins.Line;
		}
		switch (ins.Op)
		{
			case Lua51Op.EQ:
			case Lua51Op.LT:
			case Lua51Op.LE:
			{
				var cmp = MakeExpr(ExprKind.Compare, ins.Line);
				cmp.Op = ins.Op;
				cmp.A = RkExpr(ins.B, pc);
				cmp.B = RkExpr(ins.C, pc);
				cmp.Swapped = ins.Op != Lua51Op.EQ && ShouldSwapComparison(cmp.A!, cmp.B!);
				{
					// A local first operand carries the comparison's own line,
					// which is the second operand's last token: its token sat
					// no later than the second operand's first.
					Expr first = cmp.Swapped ? cmp.B! : cmp.A!;
					Expr second = cmp.Swapped ? cmp.A! : cmp.B!;
					int secondLine = FirstLine(second);
					if ((first.Kind == ExprKind.Local || first.Kind == ExprKind.Upvalue) && secondLine != 0 &&
						first.Line > secondLine)
					{
						first.Line = 0;
					}
				}
				leaf.Cond = cmp;
				leaf.JumpWhenTrue = ins.A != 0;
				break;
			}
			case Lua51Op.TEST:
				leaf.Reg = ins.A;
				leaf.Cond = RegExpr(ins.A, pc);
				PinParen(leaf.Cond);
				leaf.Cond!.TestLine = ins.Line;
				leaf.JumpWhenTrue = ins.C != 0;
				break;
			case Lua51Op.TESTSET:
				leaf.Reg = ins.A;
				leaf.Cond = RegExpr(ins.B, pc);
				leaf.Cond!.TestLine = ins.Line;
				leaf.JumpWhenTrue = ins.C != 0;
				leaf.ValueKeeping = true;
				break;
			default:
				throw Fail(pc, "not a test");
		}
		return leaf;
	}

	// `x > y` compiles to LT y x with x loaded first. The operand that was
	// loaded (or whose constant was interned) later is the one the source
	// wrote first when it sits in B.
	private static int FirstConstant(Expr? e)
	{
		if (e == null) return -1;
		if (e.KIndex >= 0) return e.KIndex;
		int best = -1;
		foreach (var child in new[] { e.A, e.B })
		{
			int k = FirstConstant(child);
			if (k >= 0 && (best < 0 || k < best)) best = k;
		}
		return best;
	}

	private static bool ShouldSwapComparison(Expr b, Expr c)
	{
		bool bConst = b.Kind == ExprKind.Number || b.Kind == ExprKind.String ||
					  b.Kind == ExprKind.Nil || b.Kind == ExprKind.True ||
					  b.Kind == ExprKind.False;
		bool cConst = c.Kind == ExprKind.Number || c.Kind == ExprKind.String ||
					  c.Kind == ExprKind.Nil || c.Kind == ExprKind.True ||
					  c.Kind == ExprKind.False;
		if (b.Pc >= 0 && c.Pc >= 0) return b.Pc > c.Pc;  // both loaded into registers
		if (bConst && cConst) return false;
		if (!bConst && !cConst)
		{
			if (b.Pc >= 0 && c.Pc < 0) return true;   // B is a temp, C a local: C was first
			return false;
		}
		if (bConst)
		{
			// B constant, C register. The source wrote `C > B` unless the
			// constant order shows B was interned before C's first constant.
			int kc = FirstConstant(c);
			if (kc < 0) return true;
			return b.KIndex > kc;
		}
		return false;  // B register, C constant: `B < C`
	}

	private bool ChainValid(List<Leaf> leaves)
	{
		int n = leaves.Count;
		int falsePc = leaves[^1].Target;
		// Value chains keep a tested operand by jumping past the LOADBOOL pair.
		// Jumps of the other truth list land on the pair's other half
		// (exp2reg's p_f / p_t labels).
		int endPc = -1;
		int otherLabel = -1;
		if (falsePc - 1 >= 0 && falsePc < _ins.Length &&
			_ins[falsePc].Op == Lua51Op.LOADBOOL && _ins[falsePc - 1].Op == Lua51Op.LOADBOOL &&
			_ins[falsePc - 1].C == 1)
		{
			endPc = falsePc + 1;
			otherLabel = falsePc - 1;
		}
		else if (falsePc + 1 < _ins.Length &&
				 _ins[falsePc].Op == Lua51Op.LOADBOOL && _ins[falsePc].C == 1 &&
				 _ins[falsePc + 1].Op == Lua51Op.LOADBOOL)
		{
			endPc = falsePc + 2;
			otherLabel = falsePc + 1;
		}
		int body = leaves[^1].Pc + leaves[^1].Width;
		int bodyThreaded = ResolveThreaded(body);
		for (int i = 0; i < n - 1; ++i)
		{
			int target = leaves[i].Target;
			if (target == falsePc || target == endPc || target == bodyThreaded || target == otherLabel) continue;
			bool interior = false;
			for (int k = i + 1; k < n; ++k)
			{
				if (target == leaves[k].Pc + leaves[k].Width)
				{
					interior = true;
					break;
				}
			}
			if (!interior) return false;
		}
		return true;
	}

	// Collects the test/jump leaves of one condition starting at `pc`. Operand
	// loads between leaves are evaluated into temporaries. Returns the pc after
	// the chain and sets the body start (true) and false exit.
	// C++ `int* true_pc, int* false_pc` (always written on return) -> out.
	private int GatherChain(int pc, int limit, List<Leaf> leaves, out int truePc, out int falsePc,
		bool allowValue)
	{
		leaves.Clear();
		List<Leaf> tentative = new();
		Slot[] bestRegs = Array.Empty<Slot>();
		int bestAfter = pc;
		int cur = pc;
		bool hasTestset = false;
		int chainLastWrite = -1;
		List<Stmt> scratch = new();
		Ctx ctx = new Ctx();
		ctx.BlockEnd = limit;
		TakeSnapshot();
		if (cur < limit && _ins[cur].Op == Lua51Op.JMP && _ins[cur].SBx > 0 && !IsPairedJump(cur))
		{
			// `true or ...`: Lua 5.1.2 emits an unconditional jump to the body.
			Leaf leaf = new Leaf();
			leaf.Pc = cur;
			leaf.Width = 1;
			leaf.Target = cur + 1 + _ins[cur].SBx;
			leaf.JumpWhenTrue = true;
			leaf.Cond = MakeExpr(ExprKind.True, _ins[cur].Line);
			tentative.Add(leaf);
			cur += 1;
		}
		// ProbeGuard: probing_ is set here and restored when the function
		// leaves by any path, exceptions included (an operand value nests a chain).
		bool wasProbing = _probing;
		bool usedOperandValue = false;
		_probing = true;
		try
		{
			// Operand loads before the first test.
			while (cur < limit && (IsTestOp(_ins[cur].Op) || IsPureOperand(cur)) &&
				   (cur == pc || (LocalsStartingAt(cur).Count == 0 && !LoopStartsAt(cur))))
			{
				if (IsTestOp(_ins[cur].Op))
				{
					int valueEnd = OperandValue(cur, limit);
					if (valueEnd < 0) break;
					usedOperandValue = true;
					cur = valueEnd;
					continue;
				}
				try
				{
					cur = ProcessSimple(cur, scratch, ctx);
				}
				catch (DecompileException)
				{
					break;
				}
				if (scratch.Count != 0) break;
			}
			for (;;)
			{
				// A bare forward jump right after a leaf is a constant operand
				// (Lua 5.1.2 emits `luaK_jump` for `true` in an or-test and `false`
				// in an and-test). It must land inside the enclosing block.
				if (tentative.Count != 0 && cur < limit && _ins[cur].Op == Lua51Op.JMP && _ins[cur].SBx > 0 &&
					cur + 1 + _ins[cur].SBx <= limit)
				{
					Leaf leaf = new Leaf();
					leaf.Pc = cur;
					leaf.Width = 1;
					leaf.Target = cur + 1 + _ins[cur].SBx;
					leaf.JumpWhenTrue = true;
					leaf.Cond = MakeExpr(ExprKind.True, _ins[cur].Line);
					tentative.Add(leaf);
					cur += 1;
					if (ChainValid(tentative))
					{
						leaves.Clear();
						leaves.AddRange(tentative);
						bestRegs = (Slot[])_regs.Clone();
						bestAfter = cur;
					}
					int probe = cur;
					bool ok = true;
					while (probe < limit)
					{
						if (IsTestOp(_ins[probe].Op))
						{
							if (LocalsStartingAt(probe).Count != 0 || LoopStartsAt(probe)) break;
							int valueEnd = OperandValue(probe, limit);
							if (valueEnd < 0) break;
							usedOperandValue = true;
							probe = valueEnd;
							continue;
						}
						if (!IsPureOperand(probe) || LocalsStartingAt(probe).Count != 0 ||
							LoopStartsAt(probe))
						{
							ok = false;
							break;
						}
						try
						{
							probe = ProcessSimple(probe, scratch, ctx);
						}
						catch (DecompileException)
						{
							ok = false;
							break;
						}
						if (scratch.Count != 0)
						{
							ok = false;
							break;
						}
					}
					if (!ok || probe >= limit) break;
					cur = probe;
				}
				if (cur + 1 >= _ins.Length || !IsTestOp(_ins[cur].Op) ||
					_ins[cur + 1].Op != Lua51Op.JMP)
				{
					break;
				}
				bool isTestset = _ins[cur].Op == Lua51Op.TESTSET;
				if (isTestset && !allowValue) break;
				if (isTestset && tentative.Count != 0 && !hasTestset && !allowValue) break;
				_probing = false;
				CheckLeafOrder(cur, ref chainLastWrite);
				// the next leaf's operands are evaluated after this test
				chainLastWrite = Math.Max(chainLastWrite, cur);
				Leaf testLeaf = MakeLeaf(cur);
				_probing = true;
				hasTestset = hasTestset || isTestset;
				tentative.Add(testLeaf);
				cur += 2;
				if (ChainValid(tentative))
				{
					leaves.Clear();
					leaves.AddRange(tentative);
					bestRegs = (Slot[])_regs.Clone();
					bestAfter = cur;
				}
				// Evaluate operand loads leading to the next test.
				if (cur < limit && _ins[cur].Op == Lua51Op.JMP) continue;  // constant leaf
				{
					int probe = cur;
					bool ok = true;
					while (probe < limit)
					{
						if (IsTestOp(_ins[probe].Op))
						{
							if (LocalsStartingAt(probe).Count != 0 || LoopStartsAt(probe)) break;
							int valueEnd = OperandValue(probe, limit);
							if (valueEnd < 0) break;
							usedOperandValue = true;
							probe = valueEnd;
							continue;
						}
						if (!IsPureOperand(probe) || LocalsStartingAt(probe).Count != 0 ||
							LoopStartsAt(probe))
						{
							ok = false;
							break;
						}
						try
						{
							probe = ProcessSimple(probe, scratch, ctx);
						}
						catch (DecompileException)
						{
							ok = false;
							break;
						}
						if (scratch.Count != 0)
						{
							ok = false;
							break;
						}
					}
					if (!ok || probe >= limit || LocalsStartingAt(probe).Count != 0 || LoopStartsAt(probe)) break;
					cur = probe;
				}
			}
			if (leaves.Count == 0)
			{
				RestoreSnapshot();
				if (usedOperandValue && !_operandValuesOff)
				{
					// No chain consumes the value: the test is the chain's own.
					_operandValuesOff = true;
					try
					{
						return GatherChain(pc, limit, leaves, out truePc, out falsePc, allowValue);
					}
					finally
					{
						_operandValuesOff = false;
					}
				}
				truePc = pc;
				falsePc = -1;
				return pc;
			}
			_regs = bestRegs;  // bestRegs is dead afterwards: a move
			if (TraceEnabled)
			{
				string desc = "";
				foreach (Leaf leaf in leaves) desc += " " + leaf.Pc.ToString() + "->" + leaf.Target.ToString();
				Trace($"{_path} chain from {pc}:{desc} true={bestAfter} false={leaves[^1].Target}");
			}
			truePc = bestAfter;
			falsePc = leaves[^1].Target;
			return bestAfter;
		}
		finally
		{
			_probing = wasProbing;
		}
	}

	// `(a or b) == c`: an and/or value that a later test of the same chain
	// consumes is one of its operands, not a leaf. Builds it into its
	// register (pending, as an operand load would be) and returns the pc
	// after it; -1 when the test at `pc` does not start one.
	private int OperandValue(int pc, int limit)
	{
		if (_operandValuesOff || pc + 1 >= limit) return -1;
		Instr test = _ins[pc];
		if ((test.Op != Lua51Op.TEST && test.Op != Lua51Op.TESTSET) || _ins[pc + 1].Op != Lua51Op.JMP) return -1;
		int reg = test.A;
		if (IsLocalAt(pc, reg)) return -1;
		int end = pc + 2 + _ins[pc + 1].SBx;
		if (end <= pc + 2) return -1;
		// Jumps of nested and/or groups widen the value to their exit.
		for (int q = pc + 2; q < end && q < limit; ++q)
		{
			if (_ins[q].Op != Lua51Op.JMP) continue;
			int target = q + 1 + _ins[q].SBx;
			if (target <= pc) return -1;
			if (target > end) end = target;
		}
		if (end >= limit || !IsPureValueRegion(pc, end, reg) || IsLocalAt(end, reg)) return -1;
		for (int q = pc + 1; q <= end; ++q)
		{
			if (LocalsStartingAt(q).Count != 0 || LoopStartsAt(q)) return -1;
		}
		// The consumer: operand code from the end that reads the register,
		// up to the test that closes the operand.
		bool read = false;
		int use = end;
		for (; use < limit; ++use)
		{
			if (use > end && (LocalsStartingAt(use).Count != 0 || LoopStartsAt(use))) return -1;
			if (IsOperandOf(use, reg)) read = true;
			if (IsTestOp(_ins[use].Op)) break;
			if (!IsPureOperand(use)) return -1;
			if (!read && InstructionWrites(use, reg)) return -1;
		}
		if (!read || use + 1 >= limit || _ins[use + 1].Op != Lua51Op.JMP) return -1;
		// A temporary dies with its consumer; a register read again on some
		// path held a local (`local x = a or b` then `if x > 1`).
		if (LiveAt(use + 1, reg)) return -1;
		Slot[] savedRegs = (Slot[])_regs.Clone();
		Slot[] savedSnapshot = _snapshot;
		bool savedProbing = _probing;
		var scratch = new List<Stmt>();
		Ctx vctx = new Ctx();
		vctx.BlockEnd = end;
		vctx.ValueReg = reg;
		bool ok = false;
		try
		{
			int after = BuildConditional(pc, end, scratch, ref vctx);
			ok = after == end && scratch.Count == 0 && _regs[reg].Expr != null;
		}
		catch (DecompileException)
		{
			ok = false;
		}
		finally
		{
			_snapshot = savedSnapshot;
			_probing = savedProbing;
		}
		if (!ok)
		{
			_regs = savedRegs;
			return -1;
		}
		if (TraceEnabled) Trace($"{_path} operand value at {pc} -> r{reg} end={end}");
		return end;
	}

	// The operand-load opcodes, whatever register they target.
	private bool IsOperandOp(int pc)
	{
		Instr ins = _ins[pc];
		switch (ins.Op)
		{
			case Lua51Op.MOVE: case Lua51Op.LOADK: case Lua51Op.GETUPVAL: case Lua51Op.GETGLOBAL: case Lua51Op.GETTABLE:
			case Lua51Op.ADD: case Lua51Op.SUB: case Lua51Op.MUL: case Lua51Op.DIV: case Lua51Op.MOD: case Lua51Op.POW:
			case Lua51Op.UNM: case Lua51Op.NOT: case Lua51Op.LEN: case Lua51Op.CONCAT: case Lua51Op.SELF:
			case Lua51Op.NEWTABLE: case Lua51Op.VARARG: case Lua51Op.CLOSURE: case Lua51Op.LOADNIL:
				return true;
			case Lua51Op.LOADBOOL:
				return ins.C == 0;
			case Lua51Op.CALL:
				return ins.C != 1;
			case Lua51Op.SETTABLE: case Lua51Op.SETLIST:
				return HasOpenTableBefore(pc, ins.A);
			default:
				return false;
		}
	}

	private bool IsPureOperand(int pc)
	{
		Instr ins = _ins[pc];
		switch (ins.Op)
		{
			case Lua51Op.MOVE: case Lua51Op.LOADK: case Lua51Op.GETUPVAL: case Lua51Op.GETGLOBAL: case Lua51Op.GETTABLE:
			case Lua51Op.ADD: case Lua51Op.SUB: case Lua51Op.MUL: case Lua51Op.DIV: case Lua51Op.MOD: case Lua51Op.POW:
			case Lua51Op.UNM: case Lua51Op.NOT: case Lua51Op.LEN: case Lua51Op.CONCAT: case Lua51Op.SELF:
			case Lua51Op.NEWTABLE: case Lua51Op.VARARG: case Lua51Op.CLOSURE:
				return !IsLocalAt(pc, ins.A);
			case Lua51Op.LOADBOOL:
				return !IsLocalAt(pc, ins.A) && ins.C == 0;
			case Lua51Op.LOADNIL:
				return !IsLocalAt(pc, ins.A);
			case Lua51Op.CALL:
				return ins.C != 1 && !IsLocalAt(pc, ins.A);
			case Lua51Op.SETTABLE:
			case Lua51Op.SETLIST:
				return !IsLocalAt(pc, ins.A) && HasOpenTableBefore(pc, ins.A);
			default:
				return false;
		}
	}

	// Condition of a chain in which jumping to `true_pc` means true and
	// jumping to `false_pc` means false.
	private Expr? BuildCondition(List<Leaf> leaves, int i, int j, int truePc, int falsePc)
	{
		Expr? result = BuildConditionInner(leaves, i, j, truePc, falsePc);
		FixOperatorLines(result);
		return result;
	}

	// The TEST of a leaf is coded when the token after it is consumed: the
	// and/or operator following it, or the closing token of the whole
	// expression. Every operator and group-closing line follows from the
	// last leaf of its left operand.
	private static int LastLeafTestLine(Expr? e)
	{
		if (e == null) return 0;
		if (e.Kind == ExprKind.And || e.Kind == ExprKind.Or) return LastLeafTestLine(e.B);
		if (e.Kind == ExprKind.Not && !e.ExplicitNot) return LastLeafTestLine(e.A);
		return e.TestLine;
	}

	private static void FixOperatorLines(Expr? e)
	{
		if (e == null) return;
		if (e.Kind == ExprKind.And || e.Kind == ExprKind.Or)
		{
			if (e.OpLine == 0) e.OpLine = LastLeafTestLine(e.A);
			if (e.EndLine == 0) e.EndLine = LastLeafTestLine(e);
			FixOperatorLines(e.A);
			FixOperatorLines(e.B);
		}
		else if (e.Kind == ExprKind.Not)
		{
			FixOperatorLines(e.A);
		}
	}

	private Expr? BuildConditionInner(List<Leaf> leaves, int i, int j, int truePc, int falsePc)
	{
		if (i >= j) return null;
		Leaf leaf = leaves[i];
		int line = _ins[leaf.Pc].Line;
		int opLine = (leaf.Op == Lua51Op.TEST || leaf.Op == Lua51Op.TESTSET) ? line : 0;
		Expr? fall = leaf.JumpWhenTrue ? MakeNot(leaf.Cond!, line) : leaf.Cond;
		if (i + 1 == j)
		{
			if ((leaf.Op == Lua51Op.TEST || leaf.Op == Lua51Op.TESTSET) && leaf.Cond != null &&
				(leaf.Cond.Kind == ExprKind.Call || leaf.Cond.Kind == ExprKind.MethodCall) &&
				leaf.Cond.EndLine == 0 && leaf.Cond.Args.Count == 0)
			{
				leaf.Cond.EndLine = line;  // `f(\n)`: the test is coded after ')'
			}
			if (leaf.Target == falsePc) return fall;
			if (leaf.Target == truePc) return MakeNot(fall!, line);
			throw Fail(leaf.Pc, "condition leaf jumps outside its chain");
		}
		if (leaf.Target == falsePc)
		{
			return MakeLogic(ExprKind.And, fall, BuildCondition(leaves, i + 1, j, truePc, falsePc), opLine);
		}
		if (leaf.Target == truePc)
		{
			return MakeLogic(ExprKind.Or, MakeNot(fall!, line),
							 BuildCondition(leaves, i + 1, j, truePc, falsePc), opLine);
		}
		int k = -1;
		for (int m = i + 1; m < j; ++m)
		{
			if (LeafStart(leaves, m) == leaf.Target)
			{
				k = m;
				break;
			}
		}
		if (k < 0)
		{
			for (int m = i + 1; m < j; ++m)
			{
				Leaf lm = leaves[m];
				if (lm.Pc + lm.Width == leaf.Target)
				{
					k = m + 1;
					break;
				}
			}
		}
		if (k < 0 || k > j) throw Fail(leaf.Pc, "condition leaf jumps to an unknown point");
		// The group [i, k) either ends on the false exit, making it an or-chain
		// that is and-ed with the rest, or ends on a true exit X, making it an
		// and-chain that is or-ed with the alternatives up to X and and-ed with
		// whatever follows X.
		int groupExit = leaves[k - 1].Target;
		int lastTestLine(int upto)
		{
			Leaf last = leaves[upto - 1];
			return (last.Op == Lua51Op.TEST || last.Op == Lua51Op.TESTSET) ? _ins[last.Pc].Line : 0;
		}
		if (groupExit == falsePc)
		{
			Expr? inner0 = BuildCondition(leaves, i + 1, k, leaf.Target, falsePc);
			Expr? rest0 = BuildCondition(leaves, k, j, truePc, falsePc);
			Expr? group0 = MakeLogic(ExprKind.Or, MakeNot(fall!, line), inner0, opLine);
			group0!.EndLine = lastTestLine(k);
			return MakeLogic(ExprKind.And, group0, rest0, lastTestLine(k));
		}
		int mm = j;
		if (groupExit != truePc)
		{
			mm = -1;
			for (int q = k; q < j; ++q)
			{
				if (LeafStart(leaves, q) == groupExit)
				{
					mm = q;
					break;
				}
			}
			if (mm < 0) throw Fail(leaf.Pc, "condition group exits to an unknown point");
		}
		Expr? inner = BuildCondition(leaves, i + 1, k, groupExit, leaf.Target);
		Expr? alternatives = BuildCondition(leaves, k, mm, groupExit, falsePc);
		Expr? rest = BuildCondition(leaves, mm, j, truePc, falsePc);
		Expr? group = MakeLogic(ExprKind.And, fall, inner, opLine);
		group!.EndLine = lastTestLine(k);
		Expr? either = MakeLogic(ExprKind.Or, group, alternatives, lastTestLine(k));
		either!.EndLine = lastTestLine(mm);
		return MakeLogic(ExprKind.And, either, rest, lastTestLine(mm));
	}

	private int LeafStart(List<Leaf> leaves, int m)
	{
		// pc after the previous leaf's jump, or the leaf itself for the first
		if (m == 0) return leaves[0].Pc;
		Leaf prev = leaves[m - 1];
		return prev.Pc + prev.Width;
	}

	// Value form: a value-keeping leaf (TESTSET, or TEST on the result
	// register) contributes its operand; C says whether it is `and` or `or`.
	private Expr? BuildValueChain(List<Leaf> leaves, int i, int j, int truePc, int falsePc,
		int endPc, Expr? rest)
	{
		if (i >= j) return rest;
		Leaf leaf = leaves[i];
		int line = _ins[leaf.Pc].Line;
		int opLine = (leaf.Op == Lua51Op.TEST || leaf.Op == Lua51Op.TESTSET) ? line : 0;
		Leaf lastLeaf = leaves[j - 1];
		int restStart = lastLeaf.Pc + lastLeaf.Width;
		int findK(int target)
		{
			for (int m = i + 1; m < j; ++m)
			{
				if (LeafStart(leaves, m) == target) return m;
			}
			if (target == restStart) return j;
			return -1;
		}
		if (leaf.ValueKeeping)
		{
			if (leaf.Target == endPc)
			{
				Expr? tail0 = BuildValueChain(leaves, i + 1, j, truePc, falsePc, endPc, rest);
				return MakeLogic(leaf.JumpWhenTrue ? ExprKind.Or : ExprKind.And, leaf.Cond, tail0, opLine);
			}
			int k0 = findK(leaf.Target);
			if (k0 < 0) throw Fail(leaf.Pc, "value chain leaf jumps to an unknown point");
			Expr? inner0 = BuildValueChain(leaves, i + 1, k0, truePc, falsePc, endPc, null);
			Expr? outer = BuildValueChain(leaves, k0, j, truePc, falsePc, endPc, rest);
			if (leaf.JumpWhenTrue)
			{
				return MakeLogic(ExprKind.And, MakeLogic(ExprKind.Or, leaf.Cond, inner0, opLine), outer);
			}
			return MakeLogic(ExprKind.Or, MakeLogic(ExprKind.And, leaf.Cond, inner0, opLine), outer);
		}
		Expr? fall = leaf.JumpWhenTrue ? MakeNot(leaf.Cond!, line) : leaf.Cond;
		if (leaf.Target == falsePc)
		{
			Expr? tail1 = BuildValueChain(leaves, i + 1, j, truePc, falsePc, endPc, rest);
			return MakeLogic(ExprKind.And, fall, tail1, opLine);
		}
		if (leaf.Target == truePc)
		{
			Expr? tail2 = BuildValueChain(leaves, i + 1, j, truePc, falsePc, endPc, rest);
			return MakeLogic(ExprKind.Or, MakeNot(fall!, line), tail2, opLine);
		}
		int k = findK(leaf.Target);
		if (k < 0) throw Fail(leaf.Pc, "value chain leaf jumps to an unknown point");
		// C++ `const Leaf* last` (nullptr unless k - 1 > i) -> hasLast + a copy.
		bool hasLast = k - 1 > i;
		Leaf last = hasLast ? leaves[k - 1] : new Leaf();
		int groupExit = hasLast ? last.Target : -1;
		bool endsFalse = hasLast && (groupExit == falsePc ||
									 (last.ValueKeeping && groupExit == endPc && !last.JumpWhenTrue));
		if (endsFalse)
		{
			Expr? inner1 = BuildValueChain(leaves, i + 1, k, leaf.Target, falsePc, endPc, null);
			Expr? tail3 = BuildValueChain(leaves, k, j, truePc, falsePc, endPc, rest);
			return MakeLogic(ExprKind.And, MakeLogic(ExprKind.Or, MakeNot(fall!, line), inner1), tail3);
		}
		if (hasLast && groupExit == restStart && groupExit != endPc)
		{
			if (leaf.JumpWhenTrue)
			{
				// `((a or b) and c) or rest`: the or-group fails into the rest.
				Expr? inner2 = BuildValueChain(leaves, i + 1, k, leaf.Target, groupExit, endPc, null);
				Expr? continuation = BuildValueChain(leaves, k, j, groupExit, falsePc, endPc, null);
				return MakeLogic(ExprKind.Or, MakeLogic(ExprKind.And, MakeLogic(ExprKind.Or, MakeNot(fall!, line), inner2), continuation), rest);
			}
			// `(a and b or c) and rest`: the or-group's true exit is the continuation.
			Expr? inner3 = BuildValueChain(leaves, i + 1, k, groupExit, leaf.Target, endPc, null);
			Expr? alternatives0 = BuildValueChain(leaves, k, j, groupExit, falsePc, endPc, null);
			return MakeLogic(ExprKind.And, MakeLogic(ExprKind.Or, MakeLogic(ExprKind.And, fall, inner3), alternatives0), rest);
		}
		int mm = j;
		if (k - 1 > i && groupExit != truePc && groupExit != endPc && groupExit != restStart)
		{
			mm = -1;
			for (int q = k; q < j; ++q)
			{
				if (LeafStart(leaves, q) == groupExit)
				{
					mm = q;
					break;
				}
			}
			if (mm < 0) throw Fail(leaf.Pc, "value chain group exits to an unknown point");
		}
		int innerTrue = k - 1 > i ? groupExit : truePc;
		Expr? inner = BuildValueChain(leaves, i + 1, k, innerTrue, leaf.Target, endPc, null);
		Expr? alternatives = BuildValueChain(leaves, k, mm, innerTrue, falsePc, endPc, mm == j ? rest : null);
		Expr? tail = mm == j ? null : BuildValueChain(leaves, mm, j, truePc, falsePc, endPc, rest);
		return MakeLogic(ExprKind.And, MakeLogic(ExprKind.Or, MakeLogic(ExprKind.And, fall, inner), alternatives), tail);
	}

	// Resolves leaf targets through the closing jumps of enclosing blocks and
	// brings threaded true-jumps back to the body start.
	private void NormalizeChain(List<Leaf> leaves, int blockEnd, int truePc)
	{
		foreach (ref Leaf leaf in CollectionsMarshal.AsSpan(leaves))
		{
			int raw = leaf.Pc + leaf.Width + _ins[leaf.Pc + leaf.Width - 1].SBx;
			leaf.Target = EffectiveJumpTarget(leaf.Pc + leaf.Width - 1);
			if (leaf.Target > blockEnd && ResolveThreaded(blockEnd) == leaf.Target)
			{
				leaf.Target = blockEnd;
			}
			leaf.Remapped = leaf.Target != raw;
		}
		// A true-jump onto a `break` or escape at the body start was threaded
		// through it; bring it back to the body start. The last leaf always
		// jumps on false, so it is never one of these.
		for (int i = 0; i + 1 < leaves.Count; ++i)
		{
			ref Leaf leaf = ref CollectionsMarshal.AsSpan(leaves)[i];
			if (!leaf.Remapped && leaf.Target != truePc && leaf.Target == ResolveThreaded(truePc))
			{
				leaf.Target = truePc;
			}
		}
	}

	// Handles a test at `pc`: an if statement, or a boolean/and-or value.
	private int BuildConditional(int pc, int blockEnd, List<Stmt> outList, ref Ctx ctx)
	{
		List<Leaf> leaves = new();
		int truePc = 0;
		int falsePc = 0;
		int after = GatherChain(pc, blockEnd, leaves, out truePc, out falsePc, /*allowValue=*/true);
		NormalizeChain(leaves, blockEnd, truePc);
		// Targets remapped through enclosing closing jumps can invalidate a
		// chain that looked consistent on raw targets: keep the longest valid
		// prefix by gathering again up to the first leaf that breaks it.
		for (int guard = 0; guard < 8 && leaves.Count > 1 && !ChainValid(leaves); ++guard)
		{
			List<Leaf> prefix = new List<Leaf>(leaves);
			while (prefix.Count > 1 && !ChainValid(prefix)) prefix.RemoveAt(prefix.Count - 1);
			int limit = leaves[prefix.Count].Pc;
			RestoreSnapshot();
			GatherChain(pc, limit, leaves, out truePc, out falsePc, /*allowValue=*/true);
			NormalizeChain(leaves, blockEnd, truePc);
		}
		falsePc = leaves[^1].Target;
		after = truePc;

		// Value forms.
		int valueReg = -1;
		int valueEnd = -1;
		foreach (Leaf leaf in leaves)
		{
			if (leaf.Op == Lua51Op.TESTSET)
			{
				valueReg = leaf.Reg;
				valueEnd = Math.Max(valueEnd, leaf.Target);
			}
		}
		bool lbPairAt(int p)
		{
			return p >= 0 && p + 1 < _ins.Length && _ins[p].Op == Lua51Op.LOADBOOL &&
				   _ins[p].C == 1 && _ins[p + 1].Op == Lua51Op.LOADBOOL;
		}
		if (lbPairAt(truePc))
		{
			valueReg = _ins[truePc].A;
			valueEnd = truePc + 2;
		}
		else if (lbPairAt(falsePc) && falsePc - 1 >= truePc && _ins[falsePc - 1].Op == Lua51Op.JMP)
		{
			valueReg = _ins[falsePc].A;
			valueEnd = falsePc + 2;
		}
		else if (lbPairAt(falsePc - 1) && falsePc - 2 >= truePc && _ins[falsePc - 2].Op == Lua51Op.JMP)
		{
			// the last leaf jumps straight to the `true` load
			valueReg = _ins[falsePc].A;
			valueEnd = falsePc + 1;
		}
		if (valueReg < 0)
		{
			// `r = a or b` with `a` already in r compiles to TEST r; the register
			// is a temporary that the code after the chain rewrites and keeps.
			foreach (Leaf leaf in leaves)
			{
				if (leaf.Op != Lua51Op.TEST || IsLocalAt(leaf.Pc, leaf.Reg)) continue;
				if (!IsPureValueRegion(truePc, falsePc, leaf.Reg)) continue;
				bool emptyScope = false;
				foreach (LocVar var in _locvars)
				{
					if (var.StartPc == falsePc && var.EndPc == falsePc) emptyScope = true;
				}
				if (emptyScope) continue;
				valueReg = leaf.Reg;
				valueEnd = falsePc;
				break;
			}
		}
		if (valueReg >= 0)
		{
			// A plain TEST on another register that exits to the end keeps no
			// value: this is an `if` whose body assigns a boolean, not a value.
			bool plainExit = false;
			int guardCount = 0;
			for (int i = 0; i < leaves.Count; ++i)
			{
				Leaf leaf = leaves[i];
				bool keeps = leaf.Op == Lua51Op.TESTSET || (leaf.Op == Lua51Op.TEST && leaf.Reg == valueReg);
				if (!keeps && leaf.Target == valueEnd)
				{
					plainExit = true;
					if (guardCount == i) guardCount = i + 1;  // leading guard leaves
				}
			}
			if (plainExit)
			{
				valueReg = -1;
				if (guardCount > 0 && guardCount < leaves.Count)
				{
					// `if guard then r = <value> end`: keep only the guard leaves.
					int limit = leaves[guardCount].Pc;
					RestoreSnapshot();
					after = GatherChain(pc, limit, leaves, out truePc, out falsePc, /*allowValue=*/false);
					NormalizeChain(leaves, blockEnd, truePc);
					falsePc = leaves[^1].Target;
					after = truePc;
				}
			}
		}
		if (valueReg >= 0)
		{
			foreach (ref Leaf leaf in CollectionsMarshal.AsSpan(leaves))
			{
				if (leaf.Op == Lua51Op.TEST && leaf.Reg == valueReg && leaf.Target == valueEnd)
				{
					leaf.ValueKeeping = true;
				}
			}
			return BuildValue(pc, leaves, truePc, falsePc, blockEnd, outList, ref ctx);
		}
		// Not a value: leaves that jump to a LOADBOOL label belong to a value
		// expression inside the body. Re-gather the chain up to the first one.
		{
			int cut = -1;
			foreach (Leaf leaf in leaves)
			{
				int t = leaf.Target;
				bool lbLabel = t >= 1 && t < _ins.Length &&
							   _ins[t].Op == Lua51Op.LOADBOOL &&
							   ((_ins[t].C == 1) || (_ins[t - 1].Op == Lua51Op.LOADBOOL && _ins[t - 1].C == 1));
				if (lbLabel)
				{
					cut = leaf.Pc;
					break;
				}
			}
			if (cut > pc)
			{
				RestoreSnapshot();
				after = GatherChain(pc, cut, leaves, out truePc, out falsePc, /*allowValue=*/false);
				foreach (ref Leaf leaf in CollectionsMarshal.AsSpan(leaves)) leaf.Target = EffectiveJumpTarget(leaf.Pc + leaf.Width - 1);
				falsePc = leaves[^1].Target;
				for (int i = 0; i + 1 < leaves.Count; ++i)
				{
					ref Leaf leaf = ref CollectionsMarshal.AsSpan(leaves)[i];
					if (leaf.Target != truePc && leaf.Target == ResolveThreaded(truePc))
					{
						leaf.Target = truePc;
					}
				}
			}
		}

		if (falsePc > blockEnd)
		{
			// The false exit may have been threaded through a `break` that
			// opens the else body: find the escape that closes the then body.
			for (int p = after; p < blockEnd; ++p)
			{
				if (_ins[p].Op == Lua51Op.JMP && !IsPairedJump(p) && _ins[p].SBx >= 0 &&
					ResolveThreaded(p + 1) == falsePc && p + 1 < blockEnd)
				{
					falsePc = p + 1;
					foreach (ref Leaf leaf in CollectionsMarshal.AsSpan(leaves))
					{
						if (leaf.Target == ResolveThreaded(p + 1)) leaf.Target = falsePc;
					}
					break;
				}
			}
		}
		if (falsePc > blockEnd || falsePc < after)
		{
			throw Fail(pc, "if body extends past the enclosing block");
		}
		var stmt = new Stmt();
		stmt.Kind = StmtKind.If;
		stmt.Cond = BuildCondition(leaves, 0, leaves.Count, truePc, falsePc);
		stmt.Line = FirstLine(stmt.Cond);
		stmt.ThenLine = KeywordTestLine(leaves);
		int thenEnd = falsePc;
		int elseEnd = falsePc;
		if (falsePc - 1 >= after && _ins[falsePc - 1].Op == Lua51Op.JMP && !IsPairedJump(falsePc - 1))
		{
			int rawTarget = falsePc + _ins[falsePc - 1].SBx;
			int target = EffectiveJumpTarget(falsePc - 1);
			if (target > blockEnd && ResolveThreaded(blockEnd) == target) target = blockEnd;
			bool isBreak = ctx.LoopExit >= 0 &&
						   (JumpsTo(rawTarget, ctx.LoopExit) || JumpsTo(target, ctx.LoopExit));
			// A backward jump here may be a `continue` rather than an else
			// escape: an escape inside the then body that lands on the
			// would-be else start proves the then body extends past this jump.
			bool isContinue = false;
			if (rawTarget <= falsePc - 1)
			{
				for (int p = after; p < falsePc - 1; ++p)
				{
					if (_ins[p].Op == Lua51Op.JMP && !IsPairedJump(p) && p + 1 + _ins[p].SBx == falsePc)
					{
						isContinue = true;
					}
				}
			}
			bool ownStatement = target == falsePc && falsePc - 2 >= after &&
								_ins[falsePc - 1].Line > _ins[falsePc - 2].Line;
			// The then block's locals end at its leaveblock, before its escape:
			// a local of the body still active at the jump puts it inside the body
			// (an inner `else end`).
			bool insideBody = false;
			if (!_stripped)
			{
				foreach (LocVar var in _locvars)
				{
					if (var.StartPc >= after && var.StartPc < falsePc - 1 && var.EndPc > falsePc - 1) insideBody = true;
				}
			}
			if (!isBreak && !isContinue && !ownStatement && !insideBody && target > falsePc - 1 && target <= blockEnd)
			{
				thenEnd = falsePc - 1;
				elseEnd = target;
				stmt.HasElse = true;
			}
		}
		if (TraceEnabled) Trace($"{_path} if at {pc}: then [{after},{thenEnd}) else [{falsePc},{elseEnd}) has_else={(stmt.HasElse ? 1 : 0)} block_end={blockEnd}");
		if (stmt.HasElse) PushClosingJump(thenEnd);
		stmt.Body = BuildBodyBlock(after, thenEnd, ctx.LoopExit);
		if (stmt.HasElse)
		{
			PopClosingJump();
			stmt.ElseBody = BuildBodyBlock(falsePc, elseEnd, ctx.LoopExit);
		}
		HintEndLine(stmt.Body, stmt.HasElse ? _ins[thenEnd].Line : 0);
		stmt.EscapeLine = stmt.HasElse ? _ins[thenEnd].Line : 0;
		outList.Add(stmt);
		return elseEnd;
	}

	// Whether the value written to `reg` at `write_pc` is next used where
	// only a temporary can sit: a call frame, a return list, a constructor
	// item list or a concatenation. Locals are copied into those places.
	private bool ConsumedAsFrameTemp(int writePc, int reg)
	{
		for (int q = writePc + 1; q < _ins.Length; ++q)
		{
			Instr ins = _ins[q];
			switch (ins.Op)
			{
				case Lua51Op.CALL: case Lua51Op.TAILCALL:
					if (ins.B == 0 ? reg >= ins.A : (ins.A <= reg && reg < ins.A + ins.B)) return true;
					break;
				case Lua51Op.RETURN:
					// `return x` returns a local from its own register; longer
					// lists and open lists are built from temporaries.
					return ins.B == 0 ? reg >= ins.A : (ins.B > 2 && ins.A <= reg && reg < ins.A + ins.B - 1);
				case Lua51Op.SETLIST:
					if (reg > ins.A && (ins.B == 0 || reg <= ins.A + ins.B)) return true;
					break;
				case Lua51Op.CONCAT:
					if (ins.B <= reg && reg <= ins.C) return true;
					break;
				case Lua51Op.SELF:
					if (ins.A == reg) return ins.B == reg;
					break;
				case Lua51Op.JMP: case Lua51Op.FORPREP: case Lua51Op.FORLOOP: case Lua51Op.TFORLOOP:
				case Lua51Op.TEST: case Lua51Op.TESTSET: case Lua51Op.EQ: case Lua51Op.LT: case Lua51Op.LE:
					// frame temporaries are consumed within straight-line code
					return false;
				default:
					break;
			}
			if (InstructionWrites(q, reg)) return false;
		}
		return false;
	}

	// Whether the instruction at `pc` can be a local's initialiser: a call
	// feeding another call's arguments (C=0) or a SELF frame never is.
	private bool CanDeclare(int pc)
	{
		Instr ins = _ins[pc];
		switch (ins.Op)
		{
			case Lua51Op.CALL: return ins.C != 0;
			case Lua51Op.VARARG: return ins.B != 0;
			case Lua51Op.SELF: case Lua51Op.FORPREP: case Lua51Op.FORLOOP: case Lua51Op.TFORLOOP: return false;
			default: return true;
		}
	}

	// C++ default argument `bool* elided = nullptr`: this overload discards it.
	private int ValueChainStart(int writePc, int reg) => ValueChainStart(writePc, reg, out _);

	// If the write at `write_pc` completes an and/or value expression into
	// `reg` (every jump to the pc after it follows a test that keeps the
	// register's value), the pc where that expression starts; otherwise -1.
	// `elided` is always assigned (the C++ writes *elided before any return).
	private int ValueChainStart(int writePc, int reg, out bool elided)
	{
		int end = writePc + 1;
		bool landed = false;
		int start = writePc;
		// `x = ... or x` elides the final load: the chain ends with a jump to
		// its own end, and jumps there may come from tests of other
		// registers (the discarded `and` operands).
		bool elidedTail = _ins[writePc].Op == Lua51Op.JMP && writePc + 1 + _ins[writePc].SBx == end;
		int endAdjusted = end;
		if (!elidedTail && end < _ins.Length && _ins[end].Op == Lua51Op.JMP &&
			end + 1 + _ins[end].SBx == end + 1 && (_ins[writePc].Op == Lua51Op.TESTSET || _ins[writePc].Op == Lua51Op.TEST))
		{
			// the last test keeps the value and its jump skips the elided load
			elidedTail = true;
			endAdjusted = end + 1;
		}
		elided = elidedTail;
		if (elidedTail && writePc == 0) return -1;
		end = endAdjusted;
		// A comparison materialised as a value ends in LOADBOOL r 0 1 / LOADBOOL
		// r 1 0: jumps land on either of them.
		bool boolPair = writePc >= 1 && _ins[writePc].Op == Lua51Op.LOADBOOL && _ins[writePc].A == reg &&
						_ins[writePc].C == 0 && _ins[writePc - 1].Op == Lua51Op.LOADBOOL &&
						_ins[writePc - 1].A == reg && _ins[writePc - 1].C == 1;
		if (boolPair)
		{
			landed = true;
			start = writePc - 1;
		}
		while (start > 0)
		{
			int p = start - 1;
			Instr ins = _ins[p];
			if (ins.Op == Lua51Op.JMP)
			{
				int target = p + 1 + ins.SBx;
				if (boolPair && (target == end - 1 || target == end - 2))
				{
					// comparison true/false jumps onto the pair
				}
				else if (target == end)
				{
					if (p == 0) return -1;
					Instr test = _ins[p - 1];
					bool kept = (test.Op == Lua51Op.TEST || test.Op == Lua51Op.TESTSET) && test.A == reg;
					// with a pair, the last operand is loaded and skips it
					bool loaded = boolPair && InstructionWrites(p - 1, reg);
					bool discarded = elidedTail && IsTestOp(test.Op);
					if (!kept && !loaded && !discarded) return -1;
					landed = true;
				}
				else if (target <= p || target > end)
				{
					break;
				}
			}
			else if (!IsTestOp(ins.Op))
			{
				if (!IsPureOperand(p)) break;
				if (ins.Op == Lua51Op.MOVE && ins.A == reg && ins.B > reg) return -1;
				bool below = false;
				for (int r = 0; r < reg; ++r)
				{
					if (InstructionWrites(p, r)) below = true;
				}
				if ((ins.Op == Lua51Op.SETTABLE || ins.Op == Lua51Op.SETLIST) && ins.A < reg) below = true;
				if (below) break;
			}
			start = p;
		}
		return landed ? start : -1;
	}

	// Whether [start, end) is operand code whose last instruction writes `reg`.
	private bool IsPureValueRegion(int start, int end, int reg)
	{
		if (start >= end) return false;
		int lastWrite = -1;
		for (int pc = start; pc < end; ++pc)
		{
			Instr ins = _ins[pc];
			if (IsTestOp(ins.Op) || ins.Op == Lua51Op.JMP)
			{
				// nested and/or inside the value: allowed if it stays inside
				if (ins.Op == Lua51Op.JMP)
				{
					int target = pc + 1 + ins.SBx;
					if (target > end || target <= start) return false;
				}
				continue;
			}
			if (!IsPureOperand(pc)) return false;
			if (ins.Op == Lua51Op.SETTABLE || ins.Op == Lua51Op.SETLIST)
			{
				// a constructor store leaves the table on top
				lastWrite = ins.A;
				continue;
			}
			// A leaf is discharged straight into the target register; a
			// value built above it and moved down means the register was a
			// live local being assigned.
			if (ins.Op == Lua51Op.MOVE && ins.A == reg && ins.B > reg) return false;
			lastWrite = ins.A;
			if (ins.Op == Lua51Op.LOADNIL) lastWrite = ins.A <= reg && reg <= ins.B ? reg : ins.A;
		}
		// The value is complete at the region's last instruction, which must
		// be the write itself (a trailing jump means a statement structure).
		Lua51Op lastOp = _ins[end - 1].Op;
		if (lastOp == Lua51Op.JMP || IsTestOp(lastOp)) return false;
		return lastWrite == reg;
	}

	// Builds `r = <and/or/comparison expression>` from a chain and the code
	// that follows it up to the point where the register is complete.
	private int BuildValue(int pc, List<Leaf> leaves, int truePc, int falsePc, int blockEnd,
		List<Stmt> outList, ref Ctx ctx)
	{
		// Destination register.
		int reg = -1;
		foreach (Leaf leaf in leaves)
		{
			if (leaf.ValueKeeping) reg = leaf.Reg;
		}
		int endPc = 0;
		foreach (Leaf leaf in leaves) endPc = Math.Max(endPc, leaf.Target);

		Expr? rest = null;
		int lbFalse = -1;
		int lbTrue = -1;
		// LOADBOOL pair directly after the chain.
		if (truePc + 1 < _ins.Length && _ins[truePc].Op == Lua51Op.LOADBOOL &&
			_ins[truePc].C == 1 && _ins[truePc + 1].Op == Lua51Op.LOADBOOL)
		{
			lbFalse = truePc;
			lbTrue = truePc + 1;
			reg = _ins[truePc].A;
			endPc = Math.Max(endPc, lbTrue + 1);
			rest = null;
		}
		else
		{
			// Straight code computing the rest value into `reg`, optionally
			// followed by a jump over a LOADBOOL pair.
			int cur = truePc;
			int stop = endPc;
			// If the chain's exit is a LOADBOOL pair, the rest ends before its jump.
			if (falsePc < _ins.Length && _ins[falsePc].Op == Lua51Op.LOADBOOL &&
				_ins[falsePc].C == 1)
			{
				lbFalse = falsePc;
				lbTrue = falsePc + 1;
				reg = _ins[falsePc].A;
				endPc = lbTrue + 1;
				stop = falsePc - 1;  // the JMP over the pair
				if (_ins[stop].Op != Lua51Op.JMP) throw Fail(stop, "expected jump over LOADBOOL pair");
			}
			else if (falsePc - 1 >= 0 && _ins[falsePc - 1].Op == Lua51Op.LOADBOOL &&
					 _ins[falsePc - 1].C == 1 && _ins[falsePc].Op == Lua51Op.LOADBOOL)
			{
				lbFalse = falsePc - 1;
				lbTrue = falsePc;
				reg = _ins[falsePc].A;
				endPc = lbTrue + 1;
				stop = lbFalse - 1;
				if (_ins[stop].Op != Lua51Op.JMP) throw Fail(stop, "expected jump over LOADBOOL pair");
			}
			if (reg < 0)
			{
				// TEST-only chains: the register is whatever the rest writes last.
				throw Fail(pc, "value chain without a destination register");
			}
			Ctx inner = ctx;
			inner.ValueReg = reg;
			List<Stmt> scratch = new();
			while (cur < stop)
			{
				if (IsTestOp(_ins[cur].Op))
				{
					cur = BuildConditional(cur, stop, scratch, ref inner);
				}
				else
				{
					cur = ProcessSimple(cur, scratch, inner);
				}
				if (scratch.Count != 0) throw Fail(cur, "statement inside a value expression");
			}
			ref Slot slot = ref _regs[reg];
			if (_stripped && cur == truePc && !IsLocalAt(truePc, reg) && slot.Expr != null && slot.Pc < pc)
			{
				// `x = ... or x` with the final load elided: the register held
				// x from before this statement, so x is a local declared by
				// that earlier write.
				throw DeclareInferredLocal("elided-assign", reg, pc, ScopeEndFor(slot.Pc), slot.Expr, slot.Pc);
			}
			if (slot.Expr == null && cur == truePc && IsLocalAt(truePc, reg))
			{
				// `x = a or x`: the fallback is the register's own value.
				rest = MakeExpr(ExprKind.Local, _ins[truePc - 1].Line);
				rest.Name = LocalNameAt(truePc, reg);
			}
			else
			{
				if (slot.Expr == null) throw Fail(truePc, "value expression leaves no result");
				rest = slot.Expr;
				slot = new Slot();
			}
		}
		Expr? value = BuildValueChain(leaves, 0, leaves.Count, lbTrue, lbFalse,
									  endPc, rest);
		FixOperatorLines(value);
		_ = blockEnd;
		// Deliver the value: the register may be an active local (assignment)
		// or a temporary.
		int writePc = endPc - 1;
		if (TraceEnabled) Trace($"{_path} value chain at {pc} -> r{reg} end={endPc}");
		if (ctx.ValueReg != reg && IsLocalAt(writePc, reg) && !IsLocalAt(pc, reg))
		{
			// becomes a local right after: park for the local statement
			_regs[reg] = new Slot(value, writePc, false);
		}
		else
		{
			WriteReg(writePc, reg, value, outList, ctx);
		}
		return endPc;
	}
}
