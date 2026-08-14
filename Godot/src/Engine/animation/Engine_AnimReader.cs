using System;
using System.Text;

namespace Refii.Animations;

internal struct Engine_AnimReader
{
    private readonly byte[] _data;

    private int _pos;

    private bool _ok;

    public Engine_AnimReader(byte[] data)
    {
        _data = data;
        _pos = 0;
        _ok = data is { Length: > 0 };
    }

    public bool Ok => _ok;

    public int Position
    {
        get => _pos;
        set => _pos = value;
    }

    public int Length => _data?.Length ?? 0;

    public bool Need(int count)
    {
        if (!_ok)
        {
            return false;
        }

        if (_pos + count > _data.Length)
        {
            _ok = false;
            return false;
        }

        return true;
    }

    public uint U32()
    {
        if (!Need(4))
        {
            return 0;
        }

        uint value = ((uint)_data[_pos] << 24) |
                     ((uint)_data[_pos + 1] << 16) |
                     ((uint)_data[_pos + 2] << 8) |
                     _data[_pos + 3];
        _pos += 4;
        return value;
    }

    public float F32() => BitConverter.Int32BitsToSingle((int)U32());

    public string CStr()
    {
        if (!_ok)
        {
            return string.Empty;
        }

        int start = _pos;
        while (_pos < _data.Length && _data[_pos] != 0)
        {
            _pos++;
        }

        if (_pos >= _data.Length)
        {
            _ok = false;
            return string.Empty;
        }

        string text = Encoding.UTF8.GetString(_data, start, _pos - start);
        _pos++;
        return text;
    }

    public bool Match(string magic)
    {
        if (!Need(magic.Length))
        {
            return false;
        }

        for (int i = 0; i < magic.Length; i++)
        {
            if (_data[_pos + i] != (byte)magic[i])
            {
                return false;
            }
        }

        _pos += magic.Length;
        return true;
    }

    public static uint ReadU32Be(byte[] data, int offset) =>
        ((uint)data[offset] << 24) |
        ((uint)data[offset + 1] << 16) |
        ((uint)data[offset + 2] << 8) |
        data[offset + 3];
}
