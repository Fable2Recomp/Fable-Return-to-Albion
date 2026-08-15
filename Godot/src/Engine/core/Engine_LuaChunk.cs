using System;
using System.Collections.Generic;
using System.Text;

namespace Refii.Core;

/// <summary>
/// One function prototype out of a Lua 5.1 precompiled chunk.
/// </summary>
public sealed class Engine_LuaProto
{
	public string? Source { get; set; }

	public uint[] Code { get; set; } = Array.Empty<uint>();

	/// <summary>Constants, as null / bool / double / string.</summary>
	public object?[] Constants { get; set; } = Array.Empty<object?>();

	public Engine_LuaProto[] Protos { get; set; } = Array.Empty<Engine_LuaProto>();

	public byte UpvalueCount { get; set; }

	public byte ParameterCount { get; set; }
}

/// <summary>
/// Reads Fable 2's shipped Lua as the bytecode it actually is.
///
/// THE `.lua` FILES IN THE BANKS ARE COMPILED CHUNKS, not source. The game's data-definition
/// scripts — the locomotion state tables among them — exist nowhere else, so anything that
/// wants to know what the game says about locomotion has to read them from the player's own
/// install. That is also why this reads rather than converts: a generated C# table would be
/// extracted game data, which does not go in the repo or a build.
///
/// TWO THINGS ABOUT THIS BUILD ARE NOT THE STOCK FORMAT and both are load-bearing:
/// the chunks are BIG-ENDIAN, and `lua_Number` is a 4-BYTE FLOAT rather than the usual double
/// (`lua_pushnumber` stores with `stfs`, not `stfd`). Reading either the stock way turns every
/// number in the file into noise. Nothing below assumes a width or an order: every size comes
/// out of the header, so a chunk that disagrees costs nothing and a chunk this reader cannot
/// honestly read fails instead of returning plausible garbage.
/// </summary>
public static class Engine_LuaChunk
{
	private const byte Version51 = 0x51;

	/// <summary>Constant tags, as `lundump.c` writes them.</summary>
	private const byte TagNil = 0;

	private const byte TagBoolean = 1;

	private const byte TagNumber = 3;

	private const byte TagString = 4;

	public static bool TryLoad(byte[]? bytes, out Engine_LuaProto? root, out string error)
	{
		root = null;
		error = string.Empty;

		if (bytes is null || bytes.Length < 12)
		{
			error = "not a Lua chunk: too short";
			return false;
		}

		try
		{
			var reader = new Reader(bytes);
			reader.ReadHeader();
			root = reader.ReadFunction();
			return true;
		}
		catch (Exception exception)
		{
			root = null;
			error = exception.Message;
			return false;
		}
	}

	/// <summary>The five fields packed into one 32-bit Lua 5.1 instruction.</summary>
	public readonly record struct Instruction(int Op, int A, int B, int C, int Bx)
	{
		public static Instruction Decode(uint raw) => new(
			(int)(raw & 0x3F),
			(int)((raw >> 6) & 0xFF),
			(int)((raw >> 23) & 0x1FF),
			(int)((raw >> 14) & 0x1FF),
			(int)((raw >> 14) & 0x3FFFF));
	}

	/// <summary>Lua 5.1 opcode numbers, in `lopcodes.h` order.</summary>
	public enum Op
	{
		Move = 0, LoadK, LoadBool, LoadNil, GetUpval, GetGlobal, GetTable,
		SetGlobal, SetUpval, SetTable, NewTable, Self, Add, Sub, Mul,
		Div, Mod, Pow, Unm, Not, Len, Concat, Jmp, Eq, Lt, Le,
		Test, TestSet, Call, TailCall, Return, ForLoop, ForPrep,
		TForLoop, SetList, Close, Closure, Vararg,
	}

	/// <summary>
	/// Lua 5.1 packs "register or constant" into one operand: 256 and above is a constant
	/// index, below is a register.
	/// </summary>
	public static bool IsConstantOperand(int operand) => operand >= 256;

	public static int ConstantIndex(int operand) => operand - 256;

	private sealed class Reader
	{
		private readonly byte[] _data;

		private int _at;

		private bool _littleEndian;

		private int _intSize = 4;

		private int _sizeTSize = 4;

		private int _numberSize = 8;

		private bool _integralNumbers;

		public Reader(byte[] data) => _data = data;

		public void ReadHeader()
		{
			if (_data[0] != 0x1B || _data[1] != (byte)'L' ||
				_data[2] != (byte)'u' || _data[3] != (byte)'a')
			{
				throw new InvalidOperationException("not a Lua chunk: bad signature");
			}

			_at = 4;

			byte version = Byte();
			if (version != Version51)
			{
				throw new InvalidOperationException(
					$"expected Lua 5.1, got 0x{version:X2}");
			}

			Byte();                                   // format
			_littleEndian = Byte() != 0;
			_intSize = Byte();
			_sizeTSize = Byte();

			int instructionSize = Byte();
			if (instructionSize != 4)
			{
				throw new InvalidOperationException(
					$"unsupported instruction size {instructionSize}");
			}

			_numberSize = Byte();
			_integralNumbers = Byte() != 0;

			if (_intSize is not (1 or 2 or 4 or 8) || _sizeTSize is not (1 or 2 or 4 or 8))
			{
				throw new InvalidOperationException("unsupported integer widths");
			}

			if (_numberSize is not (4 or 8))
			{
				throw new InvalidOperationException(
					$"unsupported lua_Number size {_numberSize}");
			}
		}

		public Engine_LuaProto ReadFunction()
		{
			var proto = new Engine_LuaProto { Source = String() };

			Integer();                                // line defined
			Integer();                                // last line defined
			proto.UpvalueCount = Byte();
			proto.ParameterCount = Byte();
			Byte();                                   // is_vararg
			Byte();                                   // max stack size

			int codeCount = Integer();
			var code = new uint[codeCount];
			for (int i = 0; i < codeCount; i++)
			{
				code[i] = UInt32();
			}

			proto.Code = code;

			int constantCount = Integer();
			var constants = new object?[constantCount];
			for (int i = 0; i < constantCount; i++)
			{
				byte tag = Byte();
				constants[i] = tag switch
				{
					TagNil => null,
					TagBoolean => Byte() != 0,
					TagNumber => Number(),
					TagString => String(),
					_ => throw new InvalidOperationException(
						$"unknown constant tag {tag}"),
				};
			}

			proto.Constants = constants;

			int protoCount = Integer();
			var protos = new Engine_LuaProto[protoCount];
			for (int i = 0; i < protoCount; i++)
			{
				protos[i] = ReadFunction();
			}

			proto.Protos = protos;

			// The debug section carries nothing this reader's callers need, but it has to be
			// CONSUMED — leaving it would put every following prototype at the wrong offset.
			int lineCount = Integer();
			for (int i = 0; i < lineCount; i++)
			{
				Integer();
			}

			int localCount = Integer();
			for (int i = 0; i < localCount; i++)
			{
				String();
				Integer();
				Integer();
			}

			int upvalueNames = Integer();
			for (int i = 0; i < upvalueNames; i++)
			{
				String();
			}

			return proto;
		}

		private byte Byte()
		{
			Require(1);
			return _data[_at++];
		}

		private void Require(int count)
		{
			if (_at + count > _data.Length)
			{
				throw new InvalidOperationException(
					$"chunk ends early at {_at} wanting {count}");
			}
		}

		private ulong Raw(int width)
		{
			Require(width);
			ulong value = 0;

			if (_littleEndian)
			{
				for (int i = width - 1; i >= 0; i--)
				{
					value = (value << 8) | _data[_at + i];
				}
			}
			else
			{
				for (int i = 0; i < width; i++)
				{
					value = (value << 8) | _data[_at + i];
				}
			}

			_at += width;
			return value;
		}

		private uint UInt32() => (uint)Raw(4);

		private int Integer()
		{
			ulong raw = Raw(_intSize);
			return _intSize switch
			{
				1 => (sbyte)raw,
				2 => (short)raw,
				4 => (int)(uint)raw,
				_ => (int)(long)raw,
			};
		}

		private int Size()
		{
			ulong raw = Raw(_sizeTSize);
			if (raw > int.MaxValue)
			{
				throw new InvalidOperationException("string length out of range");
			}

			return (int)raw;
		}

		private double Number()
		{
			ulong raw = Raw(_numberSize);

			if (_integralNumbers)
			{
				return _numberSize == 4 ? (int)(uint)raw : (long)raw;
			}

			// THE 4-BYTE CASE IS THE ONE THIS GAME USES. Widening the bits to a double would
			// read the float's exponent as a double's mantissa; the value has to be decoded at
			// the width it was stored.
			return _numberSize == 4
				? BitConverter.Int32BitsToSingle((int)(uint)raw)
				: BitConverter.Int64BitsToDouble((long)raw);
		}

		private string? String()
		{
			int length = Size();
			if (length == 0)
			{
				return null;
			}

			Require(length);

			// The stored length counts the terminating NUL.
			string value = Encoding.UTF8.GetString(_data, _at, length - 1);
			_at += length;
			return value;
		}
	}
}
