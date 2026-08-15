using System;
using System.Collections.Generic;
using System.Globalization;

namespace Refii.Core;

/// <summary>A Lua table: the 1-based array part and the string-keyed hash part.</summary>
public sealed class Engine_LuaTable
{
	private readonly List<object?> _array = new();

	private readonly Dictionary<string, object?> _hash = new();

	/// <summary>The array part, in order. Empty for a pure record.</summary>
	public IReadOnlyList<object?> Items => _array;

	public IReadOnlyDictionary<string, object?> Fields => _hash;

	public bool Has(string key) => _hash.ContainsKey(key);

	public object? Get(string key) => _hash.TryGetValue(key, out object? value) ? value : null;

	/// <summary>
	/// A number field, or <paramref name="fallback"/> when the key is absent.
	///
	/// It genuinely falls back. A reader whose "default" throws on a missing key is not a
	/// default, and most of these state fields are optional by design — only 34 of the 41
	/// locomotion states carry a `VelocityArea` at all.
	/// </summary>
	public double Number(string key, double fallback = 0) =>
		Get(key) is double value ? value : fallback;

	public float Float(string key, float fallback = 0) => (float)Number(key, fallback);

	public int Int(string key, int fallback = 0) =>
		Get(key) is double value ? (int)Math.Round(value) : fallback;

	public bool Bool(string key, bool fallback = false) =>
		Get(key) is bool value ? value : fallback;

	public string? Text(string key) => Get(key) as string;

	public Engine_LuaTable? Table(string key) => Get(key) as Engine_LuaTable;

	public void Set(object? key, object? value)
	{
		switch (key)
		{
			case null:
				return;

			case string text:
				_hash[text] = value;
				return;

			case double number when IsArrayIndex(number, out int index):
				while (_array.Count < index)
				{
					_array.Add(null);
				}

				_array[index - 1] = value;
				return;

			default:
				_hash[Describe(key)] = value;
				return;
		}
	}

	public object? Get(object? key)
	{
		switch (key)
		{
			case string text:
				return Get(text);

			case double number when IsArrayIndex(number, out int index):
				return index <= _array.Count ? _array[index - 1] : null;

			default:
				return key is null ? null : Get(Describe(key));
		}
	}

	private static bool IsArrayIndex(double number, out int index)
	{
		index = (int)number;
		return index >= 1 && Math.Abs(number - index) < double.Epsilon;
	}

	private static string Describe(object key) =>
		key as string ?? Convert.ToString(key, CultureInfo.InvariantCulture) ?? string.Empty;
}

/// <summary>A global the host supplies, called by name when the chunk calls it.</summary>
public sealed record Engine_LuaNative(string Name);

/// <summary>
/// Runs a Lua 5.1 DATA chunk far enough to recover the tables it defines.
///
/// WHY EXECUTE RATHER THAN DECOMPILE. A decompiler has to guess which register held which
/// global, and on Fable 2's locomotion script it guesses wrong: it renders whole states as
/// assignments of each other (`HeroLocomotionStateIdleIdle = HeroLocomotionStateAimIdle`) and
/// emits `L0_0 = L0_0`, which would have every state aliasing one table. Executing cannot
/// guess — the globals left behind ARE the answer, and running the real bytecode showed those
/// 41 state tables are all distinct objects.
///
/// DELIBERATELY A SUBSET. These chunks are straight-line table construction: they never
/// branch, loop, or close over anything. Only the opcodes that shape actually uses are
/// implemented, and ANY other opcode fails the whole evaluation rather than being skipped.
/// That is the point — a VM that quietly ignores a branch it does not understand returns a
/// table that looks entirely reasonable and is missing whichever half the branch guarded.
/// This is not, and must not grow into, a general Lua interpreter; the guest has one.
/// </summary>
public static class Engine_LuaDataVm
{
	private const int MaxRegisters = 256;

	/// <summary>How many array slots one SETLIST batch covers, from `lopcodes.h`.</summary>
	private const int FieldsPerFlush = 50;

	public static bool TryEvaluate(
		Engine_LuaProto? proto,
		Dictionary<string, object?> globals,
		Func<string, IReadOnlyList<object?>, object?>? natives,
		out string error)
	{
		error = string.Empty;

		if (proto is null)
		{
			error = "no chunk";
			return false;
		}

		try
		{
			Run(proto, globals, natives);
			return true;
		}
		catch (Exception exception)
		{
			error = exception.Message;
			return false;
		}
	}

	private static void Run(
		Engine_LuaProto proto,
		Dictionary<string, object?> globals,
		Func<string, IReadOnlyList<object?>, object?>? natives)
	{
		var registers = new object?[MaxRegisters];
		uint[] code = proto.Code;

		// Where a multi-result CALL left the stack, for a following SETLIST that takes "all
		// values up to the top" rather than a counted run.
		int openTop = -1;

		int pc = 0;
		while (pc < code.Length)
		{
			Engine_LuaChunk.Instruction i = Engine_LuaChunk.Instruction.Decode(code[pc]);
			pc++;

			switch ((Engine_LuaChunk.Op)i.Op)
			{
				case Engine_LuaChunk.Op.Move:
					registers[i.A] = registers[i.B];
					break;

				case Engine_LuaChunk.Op.LoadK:
					registers[i.A] = Constant(proto, i.Bx);
					break;

				case Engine_LuaChunk.Op.LoadNil:
					for (int r = i.A; r <= i.B; r++)
					{
						registers[r] = null;
					}

					break;

				case Engine_LuaChunk.Op.LoadBool:
					registers[i.A] = i.B != 0;
					if (i.C != 0)
					{
						pc++;
					}

					break;

				case Engine_LuaChunk.Op.GetGlobal:
					registers[i.A] = globals.TryGetValue(
						Name(proto, i.Bx), out object? global) ? global : null;
					break;

				case Engine_LuaChunk.Op.SetGlobal:
					globals[Name(proto, i.Bx)] = registers[i.A];
					break;

				case Engine_LuaChunk.Op.NewTable:
					registers[i.A] = new Engine_LuaTable();
					break;

				case Engine_LuaChunk.Op.GetTable:
					registers[i.A] = (registers[i.B] as Engine_LuaTable)
						?.Get(Rk(proto, registers, i.C));
					break;

				case Engine_LuaChunk.Op.SetTable:
				{
					if (registers[i.A] is not Engine_LuaTable target)
					{
						throw new InvalidOperationException(
							$"SETTABLE on a non-table at {pc - 1}");
					}

					target.Set(Rk(proto, registers, i.B), Rk(proto, registers, i.C));
					break;
				}

				case Engine_LuaChunk.Op.SetList:
				{
					if (registers[i.A] is not Engine_LuaTable list)
					{
						throw new InvalidOperationException(
							$"SETLIST on a non-table at {pc - 1}");
					}

					int batch = i.C;
					if (batch == 0)
					{
						// The batch number did not fit the operand and rides in the next word.
						batch = (int)code[pc];
						pc++;
					}

					int count = i.B;
					if (count == 0)
					{
						if (openTop < 0)
						{
							throw new InvalidOperationException(
								$"SETLIST to an unset stack top at {pc - 1}");
						}

						count = openTop - i.A - 1;
						openTop = -1;
					}

					int super = (batch - 1) * FieldsPerFlush;
					for (int n = 1; n <= count; n++)
					{
						list.Set((double)(super + n), registers[i.A + n]);
					}

					break;
				}

				case Engine_LuaChunk.Op.Unm:
					registers[i.A] = -AsNumber(registers[i.B], pc);
					break;

				case Engine_LuaChunk.Op.Add:
					registers[i.A] = Arith(proto, registers, i, pc, (a, b) => a + b);
					break;

				case Engine_LuaChunk.Op.Sub:
					registers[i.A] = Arith(proto, registers, i, pc, (a, b) => a - b);
					break;

				case Engine_LuaChunk.Op.Mul:
					registers[i.A] = Arith(proto, registers, i, pc, (a, b) => a * b);
					break;

				case Engine_LuaChunk.Op.Div:
					registers[i.A] = Arith(proto, registers, i, pc, (a, b) => a / b);
					break;

				case Engine_LuaChunk.Op.Pow:
					registers[i.A] = Arith(proto, registers, i, pc, Math.Pow);
					break;

				case Engine_LuaChunk.Op.Call:
				{
					if (i.B == 0)
					{
						throw new InvalidOperationException(
							$"CALL with an open argument list at {pc - 1}");
					}

					var arguments = new object?[i.B - 1];
					for (int n = 0; n < arguments.Length; n++)
					{
						arguments[n] = registers[i.A + 1 + n];
					}

					registers[i.A] = Invoke(registers[i.A], arguments, natives, pc);

					// C == 0 leaves the results on the stack for a following SETLIST. Every
					// native here returns exactly one value.
					openTop = i.C == 0 ? i.A + 1 : -1;
					break;
				}

				case Engine_LuaChunk.Op.Closure:
				{
					// The upvalue bindings ride as pseudo-instructions after the CLOSURE and
					// are not executable. A data chunk never calls what it closes, so the
					// closure itself only has to be skipped, not built.
					if (i.Bx < proto.Protos.Length)
					{
						pc += proto.Protos[i.Bx].UpvalueCount;
					}

					registers[i.A] = null;
					break;
				}

				case Engine_LuaChunk.Op.Return:
					return;

				default:
					throw new InvalidOperationException(
						$"opcode {(Engine_LuaChunk.Op)i.Op} at {pc - 1} is outside the data " +
						"subset; this chunk needs a real interpreter");
			}
		}
	}

	private static object? Invoke(
		object? callee,
		object?[] arguments,
		Func<string, IReadOnlyList<object?>, object?>? natives,
		int pc)
	{
		if (callee is not Engine_LuaNative native)
		{
			throw new InvalidOperationException(
				$"call to a non-native value at {pc - 1}");
		}

		if (natives is null)
		{
			throw new InvalidOperationException(
				$"chunk calls {native.Name} but no natives were supplied");
		}

		return natives(native.Name, arguments);
	}

	private static object Arith(
		Engine_LuaProto proto,
		object?[] registers,
		Engine_LuaChunk.Instruction i,
		int pc,
		Func<double, double, double> op) =>
		op(AsNumber(Rk(proto, registers, i.B), pc), AsNumber(Rk(proto, registers, i.C), pc));

	private static double AsNumber(object? value, int pc) => value switch
	{
		double number => number,
		bool flag => flag ? 1 : 0,
		_ => throw new InvalidOperationException(
			$"arithmetic on a non-number at {pc - 1}"),
	};

	private static object? Rk(Engine_LuaProto proto, object?[] registers, int operand) =>
		Engine_LuaChunk.IsConstantOperand(operand)
			? Constant(proto, Engine_LuaChunk.ConstantIndex(operand))
			: registers[operand];

	private static object? Constant(Engine_LuaProto proto, int index) =>
		index >= 0 && index < proto.Constants.Length ? proto.Constants[index] : null;

	private static string Name(Engine_LuaProto proto, int index) =>
		Constant(proto, index) as string
		?? throw new InvalidOperationException($"global name constant {index} is not a string");
}
