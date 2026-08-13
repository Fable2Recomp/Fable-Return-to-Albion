using System;

namespace Refii.FlashGames;

public sealed class SwfBitReader
{
    private readonly byte[] _data;
    private int _bytePosition;
    private int _bitPosition;

    public SwfBitReader(byte[] data, int offset = 0)
    {
        _data = data;
        _bytePosition = offset;
    }

    public int BytePosition => _bytePosition;

    public bool AtEnd => _bytePosition >= _data.Length;

    public void Align()
    {
        if (_bitPosition != 0)
        {
            _bitPosition = 0;
            _bytePosition++;
        }
    }

    public uint ReadUnsigned(int bits)
    {
        uint value = 0;
        for (int i = 0; i < bits; i++)
        {
            if (_bytePosition >= _data.Length)
            {
                throw new InvalidOperationException("swf bit reader ran past the end");
            }

            int bit = (_data[_bytePosition] >> (7 - _bitPosition)) & 1;
            value = (value << 1) | (uint)bit;
            _bitPosition++;
            if (_bitPosition == 8)
            {
                _bitPosition = 0;
                _bytePosition++;
            }
        }

        return value;
    }

    public int ReadSigned(int bits)
    {
        if (bits == 0)
        {
            return 0;
        }

        uint raw = ReadUnsigned(bits);
        if (bits >= 32)
        {
            return unchecked((int)raw);
        }

        uint sign = 1u << (bits - 1);
        return (raw & sign) != 0 ? (int)raw - (int)(1u << bits) : (int)raw;
    }

    public float ReadFixed(int bits)
    {
        return ReadSigned(bits) / 65536f;
    }

    public byte ReadByte()
    {
        Align();
        if (_bytePosition >= _data.Length)
        {
            throw new InvalidOperationException("swf bit reader ran past the end");
        }

        return _data[_bytePosition++];
    }

    public ushort ReadUInt16()
    {
        Align();
        if (_bytePosition + 2 > _data.Length)
        {
            throw new InvalidOperationException("swf bit reader ran past the end");
        }

        ushort value = BitConverter.ToUInt16(_data, _bytePosition);
        _bytePosition += 2;
        return value;
    }

    public uint ReadUInt32()
    {
        Align();
        if (_bytePosition + 4 > _data.Length)
        {
            throw new InvalidOperationException("swf bit reader ran past the end");
        }

        uint value = BitConverter.ToUInt32(_data, _bytePosition);
        _bytePosition += 4;
        return value;
    }

    public SwfRect ReadRect()
    {
        Align();
        int bits = (int)ReadUnsigned(5);
        int xMin = ReadSigned(bits);
        int xMax = ReadSigned(bits);
        int yMin = ReadSigned(bits);
        int yMax = ReadSigned(bits);
        Align();
        return new SwfRect(xMin, xMax, yMin, yMax);
    }

    public SwfMatrix ReadMatrix()
    {
        Align();
        float scaleX = 1f;
        float scaleY = 1f;
        float rotateSkew0 = 0f;
        float rotateSkew1 = 0f;

        if (ReadUnsigned(1) != 0)
        {
            int scaleBits = (int)ReadUnsigned(5);
            scaleX = ReadFixed(scaleBits);
            scaleY = ReadFixed(scaleBits);
        }

        if (ReadUnsigned(1) != 0)
        {
            int rotateBits = (int)ReadUnsigned(5);
            rotateSkew0 = ReadFixed(rotateBits);
            rotateSkew1 = ReadFixed(rotateBits);
        }

        int translateBits = (int)ReadUnsigned(5);
        int translateX = ReadSigned(translateBits);
        int translateY = ReadSigned(translateBits);
        Align();

        return new SwfMatrix(scaleX, rotateSkew0, rotateSkew1, scaleY, translateX, translateY);
    }

    public SwfColor ReadRgb()
    {
        return new SwfColor(ReadByte(), ReadByte(), ReadByte(), 255);
    }

    public SwfColor ReadRgba()
    {
        return new SwfColor(ReadByte(), ReadByte(), ReadByte(), ReadByte());
    }
}
