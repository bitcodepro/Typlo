using System.Buffers.Binary;

namespace Typlo.App.Platform.Windows;

/// <summary>Conversions between clipboard DIBs and in-memory .bmp files / BGRA pixels.</summary>
internal static class Dib
{
    private const int BI_BITFIELDS = 3;

    /// <summary>Prepends a BITMAPFILEHEADER so image decoders can read a CF_DIB/CF_DIBV5 block.</summary>
    public static byte[] ToBmpFile(byte[] dib)
    {
        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(dib);
        var bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(14));
        var compression = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(16));
        var clrUsed = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(32));
        var offset = headerSize;
        // A plain BITMAPINFOHEADER with BI_BITFIELDS is followed by three DWORD masks.
        if (compression == BI_BITFIELDS && headerSize == 40) offset += 12;
        if (bitCount <= 8) offset += (clrUsed == 0 ? 1 << bitCount : clrUsed) * 4;
        else offset += clrUsed * 4;

        var file = new byte[14 + dib.Length];
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(2), file.Length);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(10), 14 + offset);
        dib.CopyTo(file, 14);
        return file;
    }

    /// <summary>Builds a bottom-up 32-bit CF_DIB from top-down BGRA pixels.</summary>
    public static byte[] FromBgra(ReadOnlySpan<byte> bgra, int width, int height)
    {
        var stride = width * 4;
        var dib = new byte[40 + stride * height];
        var h = dib.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(h, 40);
        BinaryPrimitives.WriteInt32LittleEndian(h[4..], width);
        BinaryPrimitives.WriteInt32LittleEndian(h[8..], height); // positive = bottom-up
        BinaryPrimitives.WriteUInt16LittleEndian(h[12..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(h[14..], 32);
        BinaryPrimitives.WriteInt32LittleEndian(h[20..], stride * height);
        for (var y = 0; y < height; y++)
            bgra.Slice(y * stride, stride).CopyTo(dib.AsSpan(40 + (height - 1 - y) * stride, stride));
        return dib;
    }
}
