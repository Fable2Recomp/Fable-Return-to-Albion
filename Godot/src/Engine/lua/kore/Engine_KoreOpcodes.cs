using System;

namespace Refii.Lua.Kore;

/// <summary>
/// The Kore (Fable 3 Lua, "2.0 Beta rLionhead-2010-07-05") instruction set, read from the VM.
///
/// <para>Encoding, from the fetch at TU3 0x822A2948 (disc 0x822A4318) in the interpreter
/// TU3 sub_822A2860 (disc sub_822A4230): <c>lwz r31,0(r21); srwi r11,r31,25; clrlwi r24,r31,24;
/// cmplwi r11,0x51</c> then an 82-entry jump table (TU3 0x822A2980, disc 0x822A4350). All 82
/// handler bodies are instruction-for-instruction identical between the builds after masking
/// addresses (FORPREP differs only in the address of its float constants).</para>
///
/// <code>
///   31      25 24     17 16       8 7      0
///   [ op : 7 ][  B : 8  ][  C : 9   ][ A : 8 ]
///             [    Bx : 17 (C and B)  ]          sBx = Bx - 0xFFFF
/// </code>
///
/// <list type="bullet">
/// <item>A: <c>clrlslwi r23,r31,24,3</c> (A*8 = the TValue slot, base r25).</item>
/// <item>B: <c>extrwi rX,r31,8,7</c> = (w &gt;&gt; 17) &amp; 0xFF, or <c>rlwinm rX,r31,18,21,28</c> = B*8.</item>
/// <item>C: <c>extrwi rX,r31,8,16</c> = (w &gt;&gt; 8) &amp; 0xFF; RK operands read 9 bits with
///   <c>slwi rX,r31,15; srawi rX,rX,31</c> (bit 16 selects K(C &amp; 0xFF) from r20 over R(C) from r25).</item>
/// <item>Bx: <c>rlwinm rX,r31,27,12,28</c> = Bx*8, sBx: <c>extrwi rX,r31,17,7; addis -1; addi +1</c>.</item>
/// <item>Paired opcodes (EQ/EQ_BK, LT/LT_BK, LE/LE_BK, ADD..POW and their _BK, SETTABLE_S/_BK,
///   SETTABLE_N/_BK, SETTABLE/_BK) share one handler; the opcode's low bit (w bit 25) is the
///   constant flag of B: arithmetic reads <c>extrwi rX,r31,9,6</c>, a 9-bit RK B; the others
///   select with <c>slwi rX,r31,6; srawi rX,rX,31</c>.</item>
/// </list>
///
/// <para>The _R1 forms take one operand from r19, the interpreter's copy of the value the
/// previous instruction produced, instead of loading the register; the register field is still
/// encoded, so they decode to the plain form. The compiler (proven against the shipped corpus)
/// uses them when the previous real instruction is MOVE, GETUPVAL, GETGLOBAL, GETFIELD(_R1),
/// SELF or NEWTABLE writing that operand's register and the instruction is not a jump target
/// (<see cref="ApplyR1"/>).</para>
///
/// <para>Type-specialised forms (GETTABLE_S/_N, SETTABLE_S/_N, CALL_I/_C/_M, TAILCALL_I/_C/_M)
/// rewrite themselves at run time through TU3 sub_822A6A68 (disc sub_822A8438): every handler
/// that sees the wrong type stores <c>sub_822A6A68(word, next)</c> back over the instruction.
/// The compiler emits GETTABLE_S, SETTABLE_S(_BK), CALL_I and TAILCALL_I; they all decode to the
/// generic Lua 5.1 operation.</para>
/// </summary>
public static class Engine_KoreOpcodes
{
	/// <summary>The opcode numbers of the Kore VM.</summary>
	public enum Op : byte
	{
		GETFIELD = 0, TEST = 1, CALL_I = 2, CALL_C = 3, EQ = 4, EQ_BK = 5, GETGLOBAL = 6, MOVE = 7,
		SELF = 8, RETURN = 9, GETTABLE_S = 10, GETTABLE_N = 11, GETTABLE = 12, LOADBOOL = 13,
		TFORLOOP = 14, SETFIELD = 15, SETTABLE_S = 16, SETTABLE_S_BK = 17, SETTABLE_N = 18,
		SETTABLE_N_BK = 19, SETTABLE = 20, SETTABLE_BK = 21, TAILCALL_I = 22, TAILCALL_C = 23,
		TAILCALL_M = 24, LOADK = 25, LOADNIL = 26, SETGLOBAL = 27, JMP = 28, CALL_M = 29, CALL = 30,
		TAILCALL = 31, GETUPVAL = 32, SETUPVAL = 33, ADD = 34, ADD_BK = 35, SUB = 36, SUB_BK = 37,
		MUL = 38, MUL_BK = 39, DIV = 40, DIV_BK = 41, MOD = 42, MOD_BK = 43, POW = 44, POW_BK = 45,
		NEWTABLE = 46, UNM = 47, NOT = 48, LEN = 49, LT = 50, LT_BK = 51, LE = 52, LE_BK = 53,
		CONCAT = 54, TESTSET = 55, FORPREP = 56, FORLOOP = 57, SETLIST = 58, CLOSE = 59,
		CLOSURE = 60, VARARG = 61, TAILCALL_I_R1 = 62, CALL_I_R1 = 63, SETUPVAL_R1 = 64,
		TEST_R1 = 65, NOT_R1 = 66, GETFIELD_R1 = 67, SETFIELD_R1 = 68,
		STRUCT_69 = 69, NOP_70 = 70, STRUCT_71 = 71, STRUCT_72 = 72, STRUCT_73 = 73, STRUCT_74 = 74,
		STRUCT_75 = 75, STRUCT_76 = 76, STRUCT_77 = 77, STRUCT_78 = 78, STRUCT_79 = 79, STRUCT_80 = 80,
		STRUCT_81 = 81,
	}

	/// <summary>Stock Lua 5.1 operations every Kore opcode decodes to.</summary>
	public enum Lua51Op : byte
	{
		MOVE, LOADK, LOADBOOL, LOADNIL, GETUPVAL, GETGLOBAL, GETTABLE, SETGLOBAL, SETUPVAL, SETTABLE,
		NEWTABLE, SELF, ADD, SUB, MUL, DIV, MOD, POW, UNM, NOT, LEN, CONCAT, JMP, EQ, LT, LE, TEST,
		TESTSET, CALL, TAILCALL, RETURN, FORLOOP, FORPREP, TFORLOOP, SETLIST, CLOSE, CLOSURE, VARARG,
		/// <summary>No Lua 5.1 equivalent (the structure opcodes); the decompiler rejects it.</summary>
		Unsupported = 0xFF,
	}

	/// <summary>Operand layout of an opcode as the VM reads it.</summary>
	public enum Format : byte
	{
		/// <summary>A, B (8), C (8 or 9 bit RK; see the operand notes).</summary>
		ABC,
		/// <summary>A, Bx (17-bit unsigned).</summary>
		ABx,
		/// <summary>A, sBx (Bx - 0xFFFF).</summary>
		AsBx,
		/// <summary>Not decoded.</summary>
		None,
	}

	/// <summary>One opcode: its name, layout, the Lua 5.1 operation it decodes to and where it was read.</summary>
	public readonly record struct Info(
		Op Code, string Name, Format Format, Lua51Op Generic, string Operands);

	/// <summary>
	/// The opcode table. Handler addresses are the jump-table targets (TU3 0x822A2980, disc
	/// 0x822A4350); "Operands" is what the handler reads, quoted from its body.
	/// </summary>
	public static readonly Info[] Table =
	{
		// TU3 0x822A2AC8: rlwinm r11,r31,18,21,28; ldx r19,r11,r25 (R(B)); rlwinm r10,r31,27,21,28; ldx r30,r10,r20 (K(C), 8 bits); result stdx r3,r23,r25.
		new(Op.GETFIELD, "GETFIELD", Format.ABC, Lua51Op.GETTABLE, "R(A) := R(B)[K(C)], C 8-bit constant index"),
		// TU3 0x822A44E0: ld r19,0(r30) (R(A)); extrwi r10,r31,8,16 (C); truth(R(A)) != C -> pc++ else take the next JMP's sBx.
		new(Op.TEST, "TEST", Format.ABC, Lua51Op.TEST, "if truth(R(A)) == C then pc += next sBx + 1 else pc++"),
		// TU3 0x822A4088: tag of R(A) must be 9 (Lua closure) else rewrite to 3; extrwi r10,r31,8,16 (C-1 results); extrwi r11,r31,8,7 (B; 0 = to top).
		new(Op.CALL_I, "CALL_I", Format.ABC, Lua51Op.CALL, "R(A)..R(A+C-2) := R(A)(R(A+1)..R(A+B-1)), Lua callee"),
		// TU3 0x822A3FB4: tag of R(A) must be 10 (C function) else rewrite to 29; same B/C as CALL_I; luaD_precallC.
		new(Op.CALL_C, "CALL_C", Format.ABC, Lua51Op.CALL, "CALL specialised for a C callee"),
		// TU3 0x822A45A4 (cases 4,5): slwi r11,r31,6; srawi 31 selects K(B)/R(B) by opcode bit 0; slwi r5,r31,15; srawi 31 selects K(C)/R(C); result vs r24 (A).
		new(Op.EQ, "EQ", Format.ABC, Lua51Op.EQ, "if (RK(B) == RK(C)) == A then take the next JMP else pc++"),
		new(Op.EQ_BK, "EQ_BK", Format.ABC, Lua51Op.EQ, "EQ with B a constant"),
		// TU3 0x822A4258: lwz r11,4(r18) (closure env); rlwinm r10,r31,27,12,28; ldx r30,r10,r20 (K(Bx)); result stdx r3,r23,r25.
		new(Op.GETGLOBAL, "GETGLOBAL", Format.ABx, Lua51Op.GETGLOBAL, "R(A) := Env[K(Bx)]"),
		// TU3 0x822A3500: rlwinm r11,r31,18,21,28; ldx r19,r11,r25; std r19,0(r30).
		new(Op.MOVE, "MOVE", Format.ABC, Lua51Op.MOVE, "R(A) := R(B)"),
		// TU3 0x822A4E44: RK(C) by bit 16; ldx r31,r7,r25 (R(B)); std r31,8(r30) (R(A+1)); R(A) := R(B)[RK(C)].
		new(Op.SELF, "SELF", Format.ABC, Lua51Op.SELF, "R(A+1) := R(B); R(A) := R(B)[RK(C)]"),
		// TU3 0x822A4120: extrwi r11,r31,8,7; addi r3,r11,-1 (B-1 results, 0 = to top).
		new(Op.RETURN, "RETURN", Format.ABC, Lua51Op.RETURN, "return R(A)..R(A+B-2)"),
		// TU3 0x822A2E34: RK(C) by bit 16 must be tag 4 (string) else rewrite to 11.
		new(Op.GETTABLE_S, "GETTABLE_S", Format.ABC, Lua51Op.GETTABLE, "R(A) := R(B)[RK(C)], string key"),
		// TU3 0x822A2F98: RK(C) must be tag 3 (number) else rewrite to 12.
		new(Op.GETTABLE_N, "GETTABLE_N", Format.ABC, Lua51Op.GETTABLE, "R(A) := R(B)[RK(C)], number key"),
		// TU3 0x822A310C: generic key.
		new(Op.GETTABLE, "GETTABLE", Format.ABC, Lua51Op.GETTABLE, "R(A) := R(B)[RK(C)]"),
		// TU3 0x822A4AA0: extrwi r10,r31,8,7 (B) -> value, tag 1; rlwinm r11,r31,26,22,29 = C*4 added to pc.
		new(Op.LOADBOOL, "LOADBOOL", Format.ABC, Lua51Op.LOADBOOL, "R(A) := (bool)B; if C then pc++"),
		// TU3 0x822A46DC: extrwi r31,r31,8,16 (C results); fast paths for next/ipairs when C == 2.
		new(Op.TFORLOOP, "TFORLOOP", Format.ABC, Lua51Op.TFORLOOP, "R(A+3)..R(A+2+C) := R(A)(R(A+1), R(A+2))"),
		// TU3 0x822A3C10: ld r19,0(r30) (R(A)); lwzx r5,r7,r20 with r7 = B*8 (K(B)); RK(C) by bit 16.
		new(Op.SETFIELD, "SETFIELD", Format.ABC, Lua51Op.SETTABLE, "R(A)[K(B)] := RK(C), B 8-bit constant index"),
		// TU3 0x822A3CAC (cases 16,17): key by opcode bit 0 (K(B)/R(B)) must be tag 4 else rewrite to 18; value RK(C).
		new(Op.SETTABLE_S, "SETTABLE_S", Format.ABC, Lua51Op.SETTABLE, "R(A)[R(B)] := RK(C), string key"),
		new(Op.SETTABLE_S_BK, "SETTABLE_S_BK", Format.ABC, Lua51Op.SETTABLE, "R(A)[K(B)] := RK(C)"),
		// TU3 0x822A3D84 (cases 18,19): key must be tag 3 else rewrite to 20.
		new(Op.SETTABLE_N, "SETTABLE_N", Format.ABC, Lua51Op.SETTABLE, "R(A)[R(B)] := RK(C), number key"),
		new(Op.SETTABLE_N_BK, "SETTABLE_N_BK", Format.ABC, Lua51Op.SETTABLE, "R(A)[K(B)] := RK(C), number key"),
		// TU3 0x822A3E58 (cases 20,21): generic.
		new(Op.SETTABLE, "SETTABLE", Format.ABC, Lua51Op.SETTABLE, "R(A)[RK(B)] := RK(C)"),
		new(Op.SETTABLE_BK, "SETTABLE_BK", Format.ABC, Lua51Op.SETTABLE, "R(A)[K(B)] := RK(C)"),
		// TU3 0x822A5278: tag 9 else rewrite to 23; extrwi r11,r31,8,7 (B).
		new(Op.TAILCALL_I, "TAILCALL_I", Format.ABC, Lua51Op.TAILCALL, "return R(A)(R(A+1)..R(A+B-1)), Lua callee"),
		// TU3 0x822A51A8: tag 10 else rewrite to 24.
		new(Op.TAILCALL_C, "TAILCALL_C", Format.ABC, Lua51Op.TAILCALL, "TAILCALL, C callee"),
		// TU3 0x822A5114: __call metamethod (sub_822C9830).
		new(Op.TAILCALL_M, "TAILCALL_M", Format.ABC, Lua51Op.TAILCALL, "TAILCALL through __call"),
		// TU3 0x822A4328: rlwinm r11,r31,27,12,28; lwzx r9,r11,r20 (K(Bx)) copied to R(A).
		new(Op.LOADK, "LOADK", Format.ABx, Lua51Op.LOADK, "R(A) := K(Bx)"),
		// TU3 0x822A53D8: rlwinm r11,r31,18,21,28 (B*8); nil-fill R(A)..R(B).
		new(Op.LOADNIL, "LOADNIL", Format.ABC, Lua51Op.LOADNIL, "R(A)..R(B) := nil"),
		// TU3 0x822A5408: env from closure+4; K(Bx) by rlwinm r10,r31,27,12,28; value R(A) (r30).
		new(Op.SETGLOBAL, "SETGLOBAL", Format.ABx, Lua51Op.SETGLOBAL, "Env[K(Bx)] := R(A)"),
		// TU3 0x822A44C8: extrwi r11,r31,17,7; addis r10,r11,-1; addi r10,r10,1; slwi 2; add r21.
		new(Op.JMP, "JMP", Format.AsBx, Lua51Op.JMP, "pc += sBx"),
		// TU3 0x822A3F20: any callee, sub_822C9830 for __call.
		new(Op.CALL_M, "CALL_M", Format.ABC, Lua51Op.CALL, "CALL through __call"),
		// TU3 0x822A3F0C: tag 9 -> case 2 body, tag 10 -> case 3 body, else case 29 body.
		new(Op.CALL, "CALL", Format.ABC, Lua51Op.CALL, "CALL, unspecialised"),
		// TU3 0x822A5100: tag 9 -> case 22, tag 10 -> case 23, else case 24.
		new(Op.TAILCALL, "TAILCALL", Format.ABC, Lua51Op.TAILCALL, "TAILCALL, unspecialised"),
		// TU3 0x822A4538: extrwi r11,r31,8,7; addi 5; lwzx r9,(B+5)*4,r18 = closure->upvals[B]; value at upval+8.
		new(Op.GETUPVAL, "GETUPVAL", Format.ABC, Lua51Op.GETUPVAL, "R(A) := UpValue[B]"),
		// TU3 0x822A4564: ld r19,0(r30) then the case-64 body: std r19 into closure->upvals[B] + barrier.
		new(Op.SETUPVAL, "SETUPVAL", Format.ABC, Lua51Op.SETUPVAL, "UpValue[B] := R(A)"),
		// TU3 0x822A3510 (cases 34,35): extrwi r11,r31,9,6 (9-bit B, opcode bit 0 = constant); cmpwi 0x100; extrwi r11,r31,9,15 (9-bit C); fadds.
		new(Op.ADD, "ADD", Format.ABC, Lua51Op.ADD, "R(A) := RK(B) + RK(C)"),
		new(Op.ADD_BK, "ADD_BK", Format.ABC, Lua51Op.ADD, "ADD with B a constant"),
		// TU3 0x822A35A8 (cases 36,37): same operand reads, fsubs.
		new(Op.SUB, "SUB", Format.ABC, Lua51Op.SUB, "R(A) := RK(B) - RK(C)"),
		new(Op.SUB_BK, "SUB_BK", Format.ABC, Lua51Op.SUB, "SUB with B a constant"),
		// TU3 0x822A364C (cases 38,39): fmuls.
		new(Op.MUL, "MUL", Format.ABC, Lua51Op.MUL, "R(A) := RK(B) * RK(C)"),
		new(Op.MUL_BK, "MUL_BK", Format.ABC, Lua51Op.MUL, "MUL with B a constant"),
		// TU3 0x822A36F0 (cases 40,41): fdivs.
		new(Op.DIV, "DIV", Format.ABC, Lua51Op.DIV, "R(A) := RK(B) / RK(C)"),
		new(Op.DIV_BK, "DIV_BK", Format.ABC, Lua51Op.DIV, "DIV with B a constant"),
		// TU3 0x822A3794 (cases 42,43): a - floor(a/b)*b.
		new(Op.MOD, "MOD", Format.ABC, Lua51Op.MOD, "R(A) := RK(B) % RK(C)"),
		new(Op.MOD_BK, "MOD_BK", Format.ABC, Lua51Op.MOD, "MOD with B a constant"),
		// TU3 0x822A3844 (cases 44,45): pow.
		new(Op.POW, "POW", Format.ABC, Lua51Op.POW, "R(A) := RK(B) ^ RK(C)"),
		new(Op.POW_BK, "POW_BK", Format.ABC, Lua51Op.POW, "POW with B a constant"),
		// TU3 0x822A4D10: extrwi r11,r31,8,16 (C) and extrwi r9,r31,8,7 (B), each fb2int ((x&7)+8)<<((x>>3)-1); luaH_new(L, fb2int(B), fb2int(C)).
		new(Op.NEWTABLE, "NEWTABLE", Format.ABC, Lua51Op.NEWTABLE, "R(A) := {} (array fb2int(B), hash fb2int(C))"),
		// TU3 0x822A38EC: rlwinm r11,r31,18,21,28 (R(B)); fneg.
		new(Op.UNM, "UNM", Format.ABC, Lua51Op.UNM, "R(A) := -R(B)"),
		// TU3 0x822A4F30: ldx r19 = R(B), then the case-66 body: cntlzw of truth.
		new(Op.NOT, "NOT", Format.ABC, Lua51Op.NOT, "R(A) := not R(B)"),
		// TU3 0x822A4F88: R(B) table -> luaH_getn, struct, string length, else __len.
		new(Op.LEN, "LEN", Format.ABC, Lua51Op.LEN, "R(A) := #R(B)"),
		// TU3 0x822A4344 (cases 50,51): RK(B) by opcode bit 0, RK(C) by bit 16; fcmpu ... blt; compared with r24 (A).
		new(Op.LT, "LT", Format.ABC, Lua51Op.LT, "if (RK(B) < RK(C)) == A then take the next JMP else pc++"),
		new(Op.LT_BK, "LT_BK", Format.ABC, Lua51Op.LT, "LT with B a constant"),
		// TU3 0x822A4AB8 (cases 52,53): same operand reads, <=.
		new(Op.LE, "LE", Format.ABC, Lua51Op.LE, "if (RK(B) <= RK(C)) == A then take the next JMP else pc++"),
		new(Op.LE_BK, "LE_BK", Format.ABC, Lua51Op.LE, "LE with B a constant"),
		// TU3 0x822A5504: sub_822B79C8(L, base, pc, r6 = A, r7 = B (extrwi 8,7), r8 = C (extrwi 8,16)).
		new(Op.CONCAT, "CONCAT", Format.ABC, Lua51Op.CONCAT, "R(A) := R(B) .. ... .. R(C)"),
		// TU3 0x822A5498: R(B) (rlwinm 18,21,28); C (extrwi 8,16); on match copy R(B) to R(A) and take the next JMP.
		new(Op.TESTSET, "TESTSET", Format.ABC, Lua51Op.TESTSET, "if truth(R(B)) == C then R(A) := R(B), take the next JMP else pc++"),
		// TU3 0x822A4938: coerces R(A), R(A+2), R(A+4)... (strings via sub_822CEF88), then jumps by sBx.
		new(Op.FORPREP, "FORPREP", Format.AsBx, Lua51Op.FORPREP, "R(A) -= R(A+2); pc += sBx"),
		// TU3 0x822A3934: lfs 0x10(r30) step; fadds; store R(A) and R(A+3); extrwi r10,r31,17,7 -> sBx.
		new(Op.FORLOOP, "FORLOOP", Format.AsBx, Lua51Op.FORLOOP, "R(A) += R(A+2); if in range { pc += sBx; R(A+3) := R(A) }"),
		// TU3 0x822A5318: extrwi r27,r31,8,7 (B, 0 = to top); extrwi r31,r31,9,15 (C, 0 = next word); mulli 0x32 (50 per flush).
		new(Op.SETLIST, "SETLIST", Format.ABC, Lua51Op.SETLIST, "R(A)[(C-1)*50+i] := R(A+i), 1 <= i <= B"),
		// TU3 0x822A5528: luaF_close(L, r30 = R(A)).
		new(Op.CLOSE, "CLOSE", Format.ABC, Lua51Op.CLOSE, "close upvalues >= R(A)"),
		// TU3 0x822A5538: rlwinm r10,r31,26,13,29 = Bx*4 into proto->p; then per upvalue reads the next word: clrrwi r10,r11,25 == 0xE000000 (MOVE) captures R(B) (extrwi 8,7), else GETUPVAL.
		new(Op.CLOSURE, "CLOSURE", Format.ABx, Lua51Op.CLOSURE, "R(A) := closure(P[Bx]), followed by MOVE/GETUPVAL per upvalue"),
		// TU3 0x822A569C: extrwi r26,r31,8,7; addi r31,r26,-1 (B-1 values, 0 = all).
		new(Op.VARARG, "VARARG", Format.ABC, Lua51Op.VARARG, "R(A)..R(A+B-2) := ..."),
		// TU3 0x822A5284: the case-22 body entered with r19 as the callee.
		new(Op.TAILCALL_I_R1, "TAILCALL_I_R1", Format.ABC, Lua51Op.TAILCALL, "TAILCALL_I with R(A) from r19"),
		// TU3 0x822A4094: the case-2 body entered with r19 as the callee.
		new(Op.CALL_I_R1, "CALL_I_R1", Format.ABC, Lua51Op.CALL, "CALL_I with R(A) from r19"),
		// TU3 0x822A4568: the case-33 body without its ld r19,0(r30).
		new(Op.SETUPVAL_R1, "SETUPVAL_R1", Format.ABC, Lua51Op.SETUPVAL, "SETUPVAL with R(A) from r19"),
		// TU3 0x822A44E4: the case-1 body without its ld r19,0(r30).
		new(Op.TEST_R1, "TEST_R1", Format.ABC, Lua51Op.TEST, "TEST with R(A) from r19"),
		// TU3 0x822A4F38: the case-48 body without its ldx r19 = R(B).
		new(Op.NOT_R1, "NOT_R1", Format.ABC, Lua51Op.NOT, "NOT with R(B) from r19"),
		// TU3 0x822A2AD0: the case-0 body without its ldx r19 = R(B).
		new(Op.GETFIELD_R1, "GETFIELD_R1", Format.ABC, Lua51Op.GETTABLE, "GETFIELD with R(B) from r19"),
		// TU3 0x822A3C14: the case-15 body without its ld r19,0(r30).
		new(Op.SETFIELD_R1, "SETFIELD_R1", Format.ABC, Lua51Op.SETTABLE, "SETFIELD with R(A) from r19"),
		// 69..81: structure (kstructure) opcodes. No shipped chunk uses them; not decoded.
		new(Op.STRUCT_69, "STRUCT_69", Format.None, Lua51Op.Unsupported, "structure opcode, not decoded"),
		// TU3 case 70 is the jump table's default target, the fetch itself (0x822A2948): a no-op.
		new(Op.NOP_70, "NOP_70", Format.None, Lua51Op.Unsupported, "dispatches straight to the next fetch"),
		new(Op.STRUCT_71, "STRUCT_71", Format.None, Lua51Op.Unsupported, "structure opcode, not decoded"),
		new(Op.STRUCT_72, "STRUCT_72", Format.None, Lua51Op.Unsupported, "structure opcode, not decoded"),
		new(Op.STRUCT_73, "STRUCT_73", Format.None, Lua51Op.Unsupported, "structure opcode, not decoded"),
		new(Op.STRUCT_74, "STRUCT_74", Format.None, Lua51Op.Unsupported, "structure opcode, not decoded"),
		new(Op.STRUCT_75, "STRUCT_75", Format.None, Lua51Op.Unsupported, "structure opcode, not decoded"),
		new(Op.STRUCT_76, "STRUCT_76", Format.None, Lua51Op.Unsupported, "structure opcode, not decoded"),
		new(Op.STRUCT_77, "STRUCT_77", Format.None, Lua51Op.Unsupported, "structure opcode, not decoded"),
		new(Op.STRUCT_78, "STRUCT_78", Format.None, Lua51Op.Unsupported, "structure opcode, not decoded"),
		new(Op.STRUCT_79, "STRUCT_79", Format.None, Lua51Op.Unsupported, "structure opcode, not decoded"),
		new(Op.STRUCT_80, "STRUCT_80", Format.None, Lua51Op.Unsupported, "structure opcode, not decoded"),
		new(Op.STRUCT_81, "STRUCT_81", Format.None, Lua51Op.Unsupported, "structure opcode, not decoded"),
	};

	public const int OpcodeCount = 82;

	/// <summary>sBx bias: Bx is 17 bits, so the signed range is centred on (2^17 - 1) / 2.</summary>
	public const int SbxBias = 0xFFFF;

	/// <summary>RK operands at or above this are constants (bit 8 of a 9-bit field).</summary>
	public const int RkBit = 256;

	public static int OpOf(uint word) => (int)(word >> 25);
	public static int A(uint word) => (int)(word & 0xFF);
	public static int B8(uint word) => (int)((word >> 17) & 0xFF);
	public static int B9(uint word) => (int)((word >> 17) & 0x1FF);
	public static int C8(uint word) => (int)((word >> 8) & 0xFF);
	public static int C9(uint word) => (int)((word >> 8) & 0x1FF);
	public static int Bx(uint word) => (int)((word >> 8) & 0x1FFFF);
	public static int SBx(uint word) => Bx(word) - SbxBias;

	public static string NameOf(int op) => op >= 0 && op < Table.Length ? Table[op].Name : "OP_" + op;

	/// <summary>A decoded instruction in Lua 5.1 terms (RK operands carry the 256 constant bit).</summary>
	public struct Instr
	{
		public Lua51Op Op;
		public int A, B, C, Bx, SBx, Line;
		/// <summary>The Kore opcode it came from (or the stock opcode of a format-0 chunk).</summary>
		public int Raw;

		public override readonly string ToString() => $"{Op} {A} {B} {C}";
	}

	/// <summary>
	/// Decodes a Kore word into the Lua 5.1 operation it performs. GETFIELD / SETFIELD carry
	/// their 8-bit constant index into the RK form (K(C) becomes RK 256 + C).
	/// </summary>
	public static Instr Decode(uint word, int line)
	{
		int op = OpOf(word);
		var ins = new Instr { A = A(word), Line = line, Raw = op, Bx = Bx(word), SBx = SBx(word) };
		ins.Op = op < Table.Length ? Table[op].Generic : Lua51Op.Unsupported;
		switch ((Op)op)
		{
			case Op.GETFIELD: case Op.GETFIELD_R1:
				ins.B = B8(word); ins.C = RkBit + C8(word); break;
			case Op.SETFIELD: case Op.SETFIELD_R1:
				ins.B = RkBit + B8(word); ins.C = C9(word); break;
			case Op.EQ: case Op.EQ_BK: case Op.LT: case Op.LT_BK: case Op.LE: case Op.LE_BK:
			case Op.ADD: case Op.ADD_BK: case Op.SUB: case Op.SUB_BK: case Op.MUL: case Op.MUL_BK:
			case Op.DIV: case Op.DIV_BK: case Op.MOD: case Op.MOD_BK: case Op.POW: case Op.POW_BK:
			case Op.SETTABLE_S: case Op.SETTABLE_S_BK: case Op.SETTABLE_N: case Op.SETTABLE_N_BK:
			case Op.SETTABLE: case Op.SETTABLE_BK:
				ins.B = B9(word); ins.C = C9(word); break;
			case Op.SELF: case Op.GETTABLE_S: case Op.GETTABLE_N: case Op.GETTABLE: case Op.SETLIST:
				ins.B = B8(word); ins.C = C9(word); break;
			default:
				ins.B = B8(word); ins.C = C8(word); break;
		}
		return ins;
	}

	/// <summary>Decodes a stock Lua 5.1 word (a format-0 chunk): op 6 | A 8 | C 9 | B 9, sBx bias 131071.</summary>
	public static Instr DecodeStock(uint word, int line)
	{
		int op = (int)(word & 0x3F);
		var ins = new Instr
		{
			Op = op <= (int)Lua51Op.VARARG ? (Lua51Op)op : Lua51Op.Unsupported,
			A = (int)((word >> 6) & 0xFF),
			C = (int)((word >> 14) & 0x1FF),
			B = (int)((word >> 23) & 0x1FF),
			Bx = (int)((word >> 14) & 0x3FFFF),
			Line = line,
			Raw = op,
		};
		ins.SBx = ins.Bx - 131071;
		return ins;
	}

	/// <summary>
	/// Encodes a Lua 5.1 operation the way the Kore compiler does, before the _R1 pass
	/// (<see cref="ApplyR1"/>). <paramref name="keyIsString"/> says whether an RK constant key is
	/// a string (GETTABLE/SETTABLE with one become GETFIELD/SETFIELD).
	/// </summary>
	public static uint Encode(in Instr ins, Func<int, bool> keyIsString)
	{
		static uint Abc(Op op, int a, int b, int c) => ((uint)op << 25) | ((uint)(b & 0xFF) << 17) | ((uint)(c & 0x1FF) << 8) | (uint)(a & 0xFF);
		static uint Abx(Op op, int a, int bx) => ((uint)op << 25) | ((uint)(bx & 0x1FFFF) << 8) | (uint)(a & 0xFF);
		static Op Bk(Op op, int b) => b >= RkBit ? op + 1 : op;
		int a = ins.A, b = ins.B, c = ins.C;
		switch (ins.Op)
		{
			case Lua51Op.MOVE: return Abc(Op.MOVE, a, b, 0);
			case Lua51Op.LOADK: return Abx(Op.LOADK, a, ins.Bx);
			case Lua51Op.LOADBOOL: return Abc(Op.LOADBOOL, a, b, c);
			case Lua51Op.LOADNIL: return Abc(Op.LOADNIL, a, b, 0);
			case Lua51Op.GETUPVAL: return Abc(Op.GETUPVAL, a, b, 0);
			case Lua51Op.GETGLOBAL: return Abx(Op.GETGLOBAL, a, ins.Bx);
			case Lua51Op.GETTABLE:
				return c >= RkBit && keyIsString(c - RkBit) ? Abc(Op.GETFIELD, a, b, c - RkBit) : Abc(Op.GETTABLE_S, a, b, c);
			case Lua51Op.SETGLOBAL: return Abx(Op.SETGLOBAL, a, ins.Bx);
			case Lua51Op.SETUPVAL: return Abc(Op.SETUPVAL, a, b, 0);
			case Lua51Op.SETTABLE:
				if (b >= RkBit) return keyIsString(b - RkBit) ? Abc(Op.SETFIELD, a, b - RkBit, c) : Abc(Op.SETTABLE_S_BK, a, b, c);
				return Abc(Op.SETTABLE_S, a, b, c);
			case Lua51Op.NEWTABLE: return Abc(Op.NEWTABLE, a, b, c);
			case Lua51Op.SELF: return Abc(Op.SELF, a, b, c);
			case Lua51Op.ADD: return Abc(Bk(Op.ADD, b), a, b, c);
			case Lua51Op.SUB: return Abc(Bk(Op.SUB, b), a, b, c);
			case Lua51Op.MUL: return Abc(Bk(Op.MUL, b), a, b, c);
			case Lua51Op.DIV: return Abc(Bk(Op.DIV, b), a, b, c);
			case Lua51Op.MOD: return Abc(Bk(Op.MOD, b), a, b, c);
			case Lua51Op.POW: return Abc(Bk(Op.POW, b), a, b, c);
			case Lua51Op.UNM: return Abc(Op.UNM, a, b, 0);
			case Lua51Op.NOT: return Abc(Op.NOT, a, b, 0);
			case Lua51Op.LEN: return Abc(Op.LEN, a, b, 0);
			case Lua51Op.CONCAT: return Abc(Op.CONCAT, a, b, c);
			case Lua51Op.JMP: return Abx(Op.JMP, a, ins.SBx + SbxBias);
			case Lua51Op.EQ: return Abc(Bk(Op.EQ, b), a, b, c);
			case Lua51Op.LT: return Abc(Bk(Op.LT, b), a, b, c);
			case Lua51Op.LE: return Abc(Bk(Op.LE, b), a, b, c);
			case Lua51Op.TEST: return Abc(Op.TEST, a, 0, c);
			case Lua51Op.TESTSET: return Abc(Op.TESTSET, a, b, c);
			case Lua51Op.CALL: return Abc(Op.CALL_I, a, b, c);
			case Lua51Op.TAILCALL: return Abc(Op.TAILCALL_I, a, b, c);
			case Lua51Op.RETURN: return Abc(Op.RETURN, a, b, 0);
			case Lua51Op.FORLOOP: return Abx(Op.FORLOOP, a, ins.SBx + SbxBias);
			case Lua51Op.FORPREP: return Abx(Op.FORPREP, a, ins.SBx + SbxBias);
			case Lua51Op.TFORLOOP: return Abc(Op.TFORLOOP, a, 0, c);
			case Lua51Op.SETLIST: return Abc(Op.SETLIST, a, b, c);
			case Lua51Op.CLOSE: return Abc(Op.CLOSE, a, 0, 0);
			case Lua51Op.CLOSURE: return Abx(Op.CLOSURE, a, ins.Bx);
			case Lua51Op.VARARG: return Abc(Op.VARARG, a, b, 0);
			default: throw new ArgumentException("no Kore encoding for " + ins.Op);
		}
	}

	/// <summary>
	/// Encodes a function's Lua 5.1 operations the way the Kore compiler emits them, including the
	/// _R1 pass. <paramref name="keyIsString"/> says whether constant k is a string;
	/// <paramref name="nupsOfProto"/> gives a child prototype's upvalue count (the CLOSURE's
	/// MOVE/GETUPVAL bindings that follow it). The SETLIST count word (C == 0) is copied raw.
	/// </summary>
	public static uint[] EncodeFunction(Instr[] code, Func<int, bool> keyIsString, Func<int, int> nupsOfProto)
	{
		int n = code.Length;
		var words = new uint[n];
		var target = new bool[n + 1];
		var pseudo = new bool[n + 1];
		for (int pc = 0; pc < n; pc++)
		{
			Instr ins = code[pc];
			if (ins.Op is Lua51Op.JMP or Lua51Op.FORPREP or Lua51Op.FORLOOP)
			{
				int t = pc + 1 + ins.SBx;
				if (t >= 0 && t <= n) target[t] = true;
			}
			// A CLOSURE at pc 0 does not mark its bindings (shipped evidence:
			// MovementCombatStates.lua's NonFormationKeepDistanceMovement opens with
			// CLOSURE, MOVE 0 0, GETFIELD_R1 B=0; the same sequence later in a function is plain).
			if (ins.Op == Lua51Op.CLOSURE && pc > 0)
			{
				int nups = nupsOfProto(ins.Bx);
				for (int k = 1; k <= nups && pc + k < n; k++) pseudo[pc + k] = true;
			}
		}
		for (int pc = 0; pc < n; pc++)
		{
			bool raw = pc > 0 && code[pc - 1].Op == Lua51Op.SETLIST && code[pc - 1].C == 0;
			words[pc] = raw ? (uint)code[pc].Bx : Encode(code[pc], keyIsString);
		}
		ApplyR1(words, target, pseudo);
		return words;
	}

	/// <summary>
	/// The compiler's _R1 pass over finished code: an instruction whose operand is the register
	/// the previous real instruction produced takes the operand from r19. Rule from the shipped
	/// corpus (all 1,490 format-2 chunks recompile with it): producers MOVE, GETUPVAL, GETGLOBAL,
	/// GETFIELD(_R1), SELF, NEWTABLE; consumers GETFIELD (B), SETFIELD (A), TEST (A), NOT (B),
	/// CALL_I (A), TAILCALL_I (A), SETUPVAL (A); never at a jump target, never on or right after a
	/// CLOSURE's upvalue binding (<paramref name="isPseudo"/>). SETUPVAL_R1 is supported by the VM
	/// but no shipped chunk needs it (unproven).
	/// </summary>
	public static void ApplyR1(uint[] code, bool[] isJumpTarget, bool[] isPseudo)
	{
		for (int pc = 1; pc < code.Length; pc++)
		{
			if (isJumpTarget[pc] || isPseudo[pc] || isPseudo[pc - 1]) continue;
			uint prev = code[pc - 1];
			var pop = (Op)OpOf(prev);
			bool producer = pop is Op.MOVE or Op.GETUPVAL or Op.GETGLOBAL or Op.GETFIELD or Op.GETFIELD_R1 or Op.SELF or Op.NEWTABLE;
			if (!producer) continue;
			int pa = A(prev);
			uint w = code[pc];
			Op r1 = (Op)OpOf(w) switch
			{
				Op.GETFIELD when B8(w) == pa => Op.GETFIELD_R1,
				Op.SETFIELD when A(w) == pa => Op.SETFIELD_R1,
				Op.TEST when A(w) == pa => Op.TEST_R1,
				Op.NOT when B8(w) == pa => Op.NOT_R1,
				Op.CALL_I when A(w) == pa => Op.CALL_I_R1,
				Op.TAILCALL_I when A(w) == pa => Op.TAILCALL_I_R1,
				Op.SETUPVAL when A(w) == pa => Op.SETUPVAL_R1,
				_ => (Op)0xFF,
			};
			if ((byte)r1 != 0xFF) code[pc] = (w & 0x01FFFFFFu) | ((uint)r1 << 25);
		}
	}
}
