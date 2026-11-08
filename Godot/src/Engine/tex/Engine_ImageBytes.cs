using System;
using Godot;
using Godot.NativeInterop;

namespace Refii.Textures;

/// <summary>An image's data as the renderer's own buffer, without the managed copy
/// <see cref="Image.GetData"/> makes of it.</summary>
/// <remarks>Every texture compressed here, or that a phone GPU cannot sample as BC, reads its whole mip chain here. As a fresh array
/// that was large-object garbage per texture, which only a gen2 collection returns. The packed array
/// shares the image's buffer; holding it keeps that buffer alive and unchanged until
/// <see cref="Dispose"/>, even if the image is then written to.</remarks>
public unsafe ref struct Engine_ImageBytes
{
    private godot_packed_byte_array _array;

    /// <summary>Takes a reference to the image's data.</summary>
    public Engine_ImageBytes(Image image)
    {
        using Variant data = image.Call(Image.MethodName.GetData);
        godot_variant native = data.CopyNativeVariant();
        _array = NativeFuncs.godotsharp_variant_as_packed_byte_array(in native);
        NativeFuncs.godotsharp_variant_destroy(ref native);
    }

    /// <summary>The bytes, valid until <see cref="Dispose"/>.</summary>
    public readonly ReadOnlySpan<byte> Span => new(_array.Buffer, _array.Size);

    /// <summary>Lets go of the buffer.</summary>
    public void Dispose() => _array.Dispose();
}
