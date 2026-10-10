using System.Buffers.Binary;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;

namespace FH6CarEditor;

/// <summary>PC, single-texture burG / BC7 thumbnails. All decoding stays in memory.</summary>
public static class SwatchThumbnailDecoder
{
    public static BitmapSource Decode(byte[] bytes)
    {
        var data = bytes.AsSpan();
        if (data.Length < 140 || !data[..4].SequenceEqual("burG"u8) ||
            !data.Slice(20,4).SequenceEqual("BCXT"u8) || !data.Slice(44,4).SequenceEqual("HCXT"u8))
            throw new InvalidDataException("Unsupported thumbnail texture container.");
        uint U(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset,4));
        int width = checked((int)U(76)), height = checked((int)U(80));
        if (U(4) != 257 || U(16) != 1 || U(116) != 9 || U(84) != 1 ||
            width is < 1 or > 4096 || height is < 1 or > 4096 || (long)width * height > 8_000_000)
            throw new InvalidDataException("Unsupported thumbnail format/dimensions (PC BC7 required).");
        int offset = checked((int)U(32)), size = checked((int)U(36));
        int baseSize = checked(((width + 3) / 4) * ((height + 3) / 4) * 16);
        if (offset < 140 || size < baseSize || (long)offset + size > data.Length || U(12) != data.Length || U(40) != size)
            throw new InvalidDataException("Incomplete thumbnail texture payload.");
        // Decode only the main mip. BCnEncoder.NET is MIT licensed; no native
        // DLL, DDS conversion, extraction directory or decrypted file is needed.
        var decoder = new BcDecoder();decoder.Options.IsParallel = false;
        using var input = new MemoryStream(bytes,offset,baseSize,false);
        var rgba = decoder.DecodeRaw(input,width,height,CompressionFormat.Bc7);
        byte[] pixels = new byte[checked(width * height * 4)];
        for (int i = 0; i < rgba.Length; i++) {
            pixels[i*4] = rgba[i].b;pixels[i*4+1] = rgba[i].g;
            pixels[i*4+2] = rgba[i].r;pixels[i*4+3] = rgba[i].a;
        }
        var image = BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,pixels,width*4);
        image.Freeze();return image;
    }
}
