using System;
using System.IO;

namespace FH6LocalCryptoTool;

/// <summary>
/// Structural routing candidates only. Callers must decrypt and validate the
/// SQLite payload before accepting an encrypted database or template.
/// </summary>
public static class GameDbContainerFormat
{
    public enum Kind
    {
        Unknown,
        Sqlite,
        Fh6Aes36,
        ForzaMotorsportTransformIt32
    }

    public const int DataChunkSize = 0x20000;
    public const int MacSize = 0x10;
    public const int SlotSize = DataChunkSize + MacSize;
    public const int Fh6HeaderSize = 0x24;
    public const int MotorsportHeaderSize = 0x20;

    public static Kind Detect(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[16];
        int read = stream.Read(header);
        return Detect(stream.Length, header[..read]);
    }

    public static Kind Detect(ReadOnlySpan<byte> file) => Detect(file.Length, file[..Math.Min(file.Length, 16)]);

    public static Kind Detect(long length, ReadOnlySpan<byte> header)
    {
        // An encrypted container can inherit a SQLite-looking IV/header from an
        // older template. Its framing must not be hidden by that signature.
        long fh6Payload = length - Fh6HeaderSize;
        if (fh6Payload >= SlotSize && fh6Payload % SlotSize == 0)
            return Kind.Fh6Aes36;

        long motorsportPayload = length - MotorsportHeaderSize;
        if (motorsportPayload >= SlotSize && motorsportPayload % SlotSize == 0)
            return Kind.ForzaMotorsportTransformIt32;

        if (header.Length >= 16 && header[..16].SequenceEqual("SQLite format 3\0"u8))
            return Kind.Sqlite;

        return Kind.Unknown;
    }

    public static string DisplayName(Kind kind) => kind switch
    {
        Kind.Sqlite => "SQLite",
        Kind.Fh6Aes36 => "FH6 AES GameDB",
        Kind.ForzaMotorsportTransformIt32 => "Forza Motorsport TransformIT GameDB",
        _ => "unknown GameDB format"
    };
}
