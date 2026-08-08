using System;
using Godot;

namespace Refii.Audio;

public static class Engine_AudioStream
{
    public static AudioStreamWav? TryCreateStream(byte[] bytes, out string error)
    {
        error = string.Empty;

        if (bytes is null || bytes.Length == 0)
        {
            error = "empty file";
            return null;
        }

        if (Engine_XmaDecoder.TryDecode(
                bytes, out short[] pcm, out int sampleRate, out int channels, out string xmaError))
        {
            return FromPcm16(pcm, sampleRate, channels, out error);
        }

        if (Engine_XmaDecoder.IsNotXma(xmaError) &&
            TryReadRiffPcm(bytes, out pcm, out sampleRate, out channels))
        {
            return FromPcm16(pcm, sampleRate, channels, out error);
        }

        error = xmaError.Length != 0 ? xmaError : "decode failed";
        return null;
    }

    private static AudioStreamWav? FromPcm16(
        short[] pcm, int sampleRate, int channels, out string error)
    {
        error = string.Empty;

        if (pcm.Length == 0)
        {
            error = "decoded to no samples";
            return null;
        }

        if (sampleRate <= 0)
        {
            error = $"implausible sample rate {sampleRate}";
            return null;
        }

        if (channels is not (1 or 2))
        {
            error = $"{channels} channels, AudioStreamWav supports mono or stereo";
            return null;
        }

        var data = new byte[pcm.Length * 2];
        Buffer.BlockCopy(pcm, 0, data, 0, data.Length);

        return new AudioStreamWav
        {
            Format = AudioStreamWav.FormatEnum.Format16Bits,
            MixRate = sampleRate,
            Stereo = channels == 2,
            Data = data,
        };
    }

    private static bool TryReadRiffPcm(
        byte[] bytes, out short[] pcm, out int sampleRate, out int channels)
    {
        pcm = Array.Empty<short>();
        sampleRate = 0;
        channels = 0;

        int riff = LocateRiff(bytes);
        if (riff < 0)
        {
            return false;
        }

        int fmtOffset = -1;
        int fmtSize = 0;
        int dataOffset = -1;
        int dataSize = 0;

        int position = riff + 12;
        while (position + 8 <= bytes.Length)
        {
            uint id = ReadU32Le(bytes, position);
            uint size = ReadU32Le(bytes, position + 4);
            int payload = position + 8;
            if (payload > bytes.Length)
            {
                break;
            }

            long end = Math.Min((long)payload + size, bytes.Length);

            if (id == 0x20746D66u)
            {
                fmtOffset = payload;
                fmtSize = (int)(end - payload);
            }
            else if (id == 0x61746164u)
            {
                dataOffset = payload;
                dataSize = (int)(end - payload);
            }

            long next = (long)payload + size + (size & 1u);
            if (next > bytes.Length || next <= position)
            {
                break;
            }

            position = (int)next;
        }

        if (fmtOffset < 0 || dataOffset < 0 || fmtSize < 16 || dataSize <= 0)
        {
            return false;
        }

        ushort formatTag = ReadU16Le(bytes, fmtOffset);
        ushort fmtChannels = ReadU16Le(bytes, fmtOffset + 2);
        ushort bitsPerSample = ReadU16Le(bytes, fmtOffset + 14);

        if (formatTag != 0x0001 && formatTag != 0xFFFE)
        {
            return false;
        }

        uint rateLe = ReadU32Le(bytes, fmtOffset + 4);
        uint rateBe = ReadU32Be(bytes, fmtOffset + 4);
        bool xboxEndian = (rateLe < 8000u || rateLe > 200000u) &&
                          rateBe >= 8000u && rateBe <= 200000u;
        uint rate = xboxEndian ? rateBe : rateLe;

        if (rate == 0 || fmtChannels is not (1 or 2))
        {
            return false;
        }

        if (bitsPerSample == 16)
        {
            int count = dataSize / 2;
            if (count == 0)
            {
                return false;
            }

            pcm = new short[count];
            Buffer.BlockCopy(bytes, dataOffset, pcm, 0, count * 2);
        }
        else if (bitsPerSample == 8)
        {
            pcm = new short[dataSize];
            for (int i = 0; i < dataSize; i++)
            {
                pcm[i] = (short)((bytes[dataOffset + i] - 128) << 8);
            }
        }
        else
        {
            return false;
        }

        sampleRate = (int)rate;
        channels = fmtChannels;
        return true;
    }

    private static int LocateRiff(byte[] bytes)
    {
        if (bytes.Length < 12)
        {
            return -1;
        }

        if (Matches(bytes, 0, "RIFF") && Matches(bytes, 8, "WAVE"))
        {
            return 0;
        }

        if (bytes.Length >= 16 &&
            bytes[0] == (byte)'x' && bytes[1] == (byte)'m' &&
            bytes[2] == (byte)'a' && bytes[3] == 0 &&
            Matches(bytes, 4, "RIFF") && Matches(bytes, 12, "WAVE"))
        {
            return 4;
        }

        return -1;
    }

    private static bool Matches(byte[] bytes, int at, string tag)
    {
        if (at + tag.Length > bytes.Length)
        {
            return false;
        }

        for (int i = 0; i < tag.Length; i++)
        {
            if (bytes[at + i] != (byte)tag[i])
            {
                return false;
            }
        }

        return true;
    }

    private static ushort ReadU16Le(byte[] data, int at) =>
        (ushort)(data[at] | (data[at + 1] << 8));

    private static uint ReadU32Le(byte[] data, int at) =>
        (uint)(data[at] | (data[at + 1] << 8) | (data[at + 2] << 16) | (data[at + 3] << 24));

    private static uint ReadU32Be(byte[] data, int at) =>
        ((uint)data[at] << 24) | ((uint)data[at + 1] << 16) |
        ((uint)data[at + 2] << 8) | data[at + 3];
}
