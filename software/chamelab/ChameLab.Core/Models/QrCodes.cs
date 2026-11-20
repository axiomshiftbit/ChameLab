using System;
using System.Collections.Generic;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;
using ZXing.QrCode.Internal;

namespace ChameLab.Models;

/// <summary>QR code modules and decoding, fully managed (ZXing.Net) so it runs on every platform including the web.</summary>
public static class QrCodes
{
    /// <summary>
    /// The modules of a code for <paramref name="text"/>, without the quiet zone: <c>[y, x]</c>, true = dark.
    /// Low error correction: the codes are shown on a clean screen, and fewer modules scan more easily.
    /// </summary>
    public static bool[,] Encode(string text)
    {
        var hints = new Dictionary<EncodeHintType, object> { [EncodeHintType.CHARACTER_SET] = "ISO-8859-1" };
        var matrix = Encoder.encode(text, ErrorCorrectionLevel.L, hints).Matrix;
        var modules = new bool[matrix.Height, matrix.Width];
        for (var y = 0; y < matrix.Height; y++)
            for (var x = 0; x < matrix.Width; x++)
                modules[y, x] = matrix[x, y] == 1;
        return modules;
    }

    /// <summary>The text of a QR code in an 8-bit grayscale image (one byte per pixel, rows of <paramref name="width"/>), or null.</summary>
    public static string? Decode(byte[] luminance, int width, int height)
    {
        ValidateImage(width, height, luminance.Length, 1);
        var source = new PlanarYUVLuminanceSource(luminance, width, height, 0, 0, width, height, false);
        var hints = new Dictionary<DecodeHintType, object>
        {
            [DecodeHintType.TRY_HARDER] = true,
            [DecodeHintType.POSSIBLE_FORMATS] = new List<BarcodeFormat> { BarcodeFormat.QR_CODE },
        };
        var reader = new QRCodeReader();
        foreach (var bitmap in new[] { new BinaryBitmap(new HybridBinarizer(source)), new BinaryBitmap(new GlobalHistogramBinarizer(source)) })
        {
            try
            {
                if (reader.decode(bitmap, hints) is { } result) return result.Text;
            }
            catch (ReaderException) { /* try the next binarizer */ }
            reader.reset();
        }
        // Clean screenshots have no perspective. Some dense payloads confuse the finder detector;
        // the pure-image path decodes their exact module grid after the normal camera paths.
        try
        {
            hints[DecodeHintType.PURE_BARCODE] = true;
            var pure = reader.decode(new BinaryBitmap(new HybridBinarizer(source)), hints);
            if (pure is not null) return pure.Text;
        }
        catch (ReaderException) { }
        finally { hints.Remove(DecodeHintType.PURE_BARCODE); reader.reset(); }
        // A code shown in dark mode or inverted.
        try
        {
            var inverted = new BinaryBitmap(new HybridBinarizer(source.invert()));
            return reader.decode(inverted, hints)?.Text;
        }
        catch (ReaderException) { return null; }
    }

    /// <summary>
    /// Grayscale from 32-bit pixels: BGRA (<paramref name="bgra"/> true, Skia / Avalonia) or RGBA (canvas).
    /// </summary>
    private static void ValidateImage(int width, int height, int length, int channels)
    {
        if (width <= 0 || height <= 0 || (long)width * height > 16_000_000 || (long)width * height * channels != length)
            throw new ArgumentException("Invalid QR image dimensions.");
    }

    public static byte[] Luminance(ReadOnlySpan<byte> pixels, int width, int height, bool bgra)
    {
        ValidateImage(width, height, pixels.Length, 4);
        var gray = new byte[width * height];
        var (r, b) = bgra ? (2, 0) : (0, 2);
        for (var i = 0; i < gray.Length; i++)
        {
            var p = i * 4;
            // Rec. 601 weights in integer math; transparent pixels count as white.
            var a = pixels[p + 3];
            var y = (pixels[p + r] * 77 + pixels[p + 1] * 150 + pixels[p + b] * 29) >> 8;
            gray[i] = (byte)((y * a + 255 * (255 - a)) / 255);
        }
        return gray;
    }
}
