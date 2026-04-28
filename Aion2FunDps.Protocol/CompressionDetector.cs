namespace Aion2FunDps.Protocol;

/// <summary>
/// Detects whether a game packet body is LZ4-compressed via the 0xff 0xff marker.
/// Some packets carry "extra flag" bytes (range 0xf0..0xfe) before the marker.
///
/// Phase 1b only DETECTS. Actual LZ4 decompression is Phase 1c.
/// </summary>
public static class CompressionDetector
{
    public static bool IsCompressed(ReadOnlySpan<byte> body, out int compressedDataOffset)
    {
        compressedDataOffset = 0;
        int i = 0;

        // Skip extra-flag bytes (0xf0..0xfe).
        while (i < body.Length && body[i] >= 0xf0 && body[i] <= 0xfe)
            i++;

        // Look for 0xff 0xff marker.
        if (i + 1 < body.Length && body[i] == 0xff && body[i + 1] == 0xff)
        {
            compressedDataOffset = i + 2;
            return true;
        }
        return false;
    }
}
