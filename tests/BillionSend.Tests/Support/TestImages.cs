namespace BillionSend.Tests.Support;

/// <summary>Real image bytes for the logo uploads.</summary>
public static class TestImages
{
    /// <summary>A valid 1×1 PNG (signature, IHDR, IDAT, IEND).</summary>
    public static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/q842iQAAAABJRU5ErkJggg==");

    /// <summary>The first bytes of a JPEG file: SOI, then a JFIF APP0 marker.</summary>
    public static readonly byte[] JpegHeader = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00];
}
