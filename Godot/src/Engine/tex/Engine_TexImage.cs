using Godot;

namespace Refii.Textures;

public static class Engine_TexImage
{
    public static Image.Format ToGodotFormat(TexSurfaceFormat format) => format switch
    {
        TexSurfaceFormat.Bc1 => Image.Format.Dxt1,
        TexSurfaceFormat.Bc3 => Image.Format.Dxt5,
        TexSurfaceFormat.Bc4 => Image.Format.RgtcR,
        _ => Image.Format.Dxt1,
    };

    public static Image? TryCreateImage(byte[] texBytes, out string error)
    {
        if (!Engine_TexData.TryDecode(texBytes, out TexSurface surface, out error))
        {
            return null;
        }

        if (surface.Format == TexSurfaceFormat.Unsupported)
        {
            error = "unsupported surface format";
            return null;
        }

        Image.Format format = ToGodotFormat(surface.Format);

        if (surface.HasMipmaps)
        {
            Image mipped = Image.CreateFromData(
                surface.Width, surface.Height, true, format, surface.Data);
            if (IsUsable(mipped, surface))
            {
                return mipped;
            }
        }

        int topLevel = TopLevelBytes(surface);
        byte[] top = surface.Data;
        if (topLevel > 0 && top.Length > topLevel)
        {
            top = new byte[topLevel];
            System.Buffer.BlockCopy(surface.Data, 0, top, 0, topLevel);
        }

        Image image = Image.CreateFromData(
            surface.Width, surface.Height, false, format, top);

        if (!IsUsable(image, surface))
        {
            error = $"Image.CreateFromData rejected {surface.Width}x{surface.Height} " +
                    $"{surface.Format} ({top.Length} bytes)";
            return null;
        }

        return image;
    }

    private static bool IsUsable(Image? image, TexSurface surface) =>
        image is not null &&
        image.GetWidth() == surface.Width &&
        image.GetHeight() == surface.Height;

    private static int TopLevelBytes(TexSurface surface)
    {
        int blocksW = System.Math.Max(1, (surface.Width + 3) / 4);
        int blocksH = System.Math.Max(1, (surface.Height + 3) / 4);
        int blockBytes = surface.Format == TexSurfaceFormat.Bc3 ? 16 : 8;
        return blocksW * blocksH * blockBytes;
    }

    public static ImageTexture? TryCreateTexture(byte[] texBytes, out string error)
    {
        Image? image = TryCreateImage(texBytes, out error);
        return image is null ? null : ImageTexture.CreateFromImage(image);
    }
}
