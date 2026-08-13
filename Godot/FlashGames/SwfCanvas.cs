using System;
using Godot;

namespace Refii.FlashGames;

public sealed class SwfCanvas
{
    private readonly float[] _pixels;

    public SwfCanvas(int width, int height, Color background)
    {
        Width = width;
        Height = height;
        _pixels = new float[width * height * 4];
        for (int i = 0; i < _pixels.Length; i += 4)
        {
            _pixels[i] = background.R;
            _pixels[i + 1] = background.G;
            _pixels[i + 2] = background.B;
            _pixels[i + 3] = background.A;
        }
    }

    public int Width { get; }

    public int Height { get; }

    public void Blend(int x, int y, float r, float g, float b, float a)
    {
        if (a <= 0f || x < 0 || y < 0 || x >= Width || y >= Height)
        {
            return;
        }

        int i = ((y * Width) + x) * 4;
        float dstA = _pixels[i + 3];
        float outA = a + (dstA * (1f - a));
        if (outA <= 0f)
        {
            _pixels[i] = 0f;
            _pixels[i + 1] = 0f;
            _pixels[i + 2] = 0f;
            _pixels[i + 3] = 0f;
            return;
        }

        float inv = dstA * (1f - a);
        _pixels[i] = ((r * a) + (_pixels[i] * inv)) / outA;
        _pixels[i + 1] = ((g * a) + (_pixels[i + 1] * inv)) / outA;
        _pixels[i + 2] = ((b * a) + (_pixels[i + 2] * inv)) / outA;
        _pixels[i + 3] = outA;
    }

    public Image ToImage()
    {
        var bytes = new byte[Width * Height * 4];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)Mathf.Clamp(Mathf.RoundToInt(_pixels[i] * 255f), 0, 255);
        }

        return Image.CreateFromData(Width, Height, false, Image.Format.Rgba8, bytes);
    }
}

public sealed class SwfBitmapSampler
{
    private readonly byte[] _pixels;

    public SwfBitmapSampler(Image image)
    {
        Image source = image;
        if (source.IsCompressed())
        {
            source = (Image)image.Duplicate();
            source.Decompress();
        }

        if (source.GetFormat() != Image.Format.Rgba8)
        {
            if (ReferenceEquals(source, image))
            {
                source = (Image)image.Duplicate();
            }

            source.Convert(Image.Format.Rgba8);
        }

        Width = source.GetWidth();
        Height = source.GetHeight();
        _pixels = source.GetData();
    }

    public int Width { get; }

    public int Height { get; }

    public bool TrySample(int x, int y, out float r, out float g, out float b, out float a)
    {
        r = g = b = a = 0f;
        if (x < 0 || y < 0 || x >= Width || y >= Height)
        {
            return false;
        }

        int i = ((y * Width) + x) * 4;
        if (i + 3 >= _pixels.Length)
        {
            return false;
        }

        r = _pixels[i] / 255f;
        g = _pixels[i + 1] / 255f;
        b = _pixels[i + 2] / 255f;
        a = _pixels[i + 3] / 255f;
        return true;
    }
}
