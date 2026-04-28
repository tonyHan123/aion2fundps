using System.Buffers;
using K4os.Compression.LZ4;

namespace Aion2FunDps.Protocol;

/// <summary>
/// LZ4 block-format decompressor. Output buffer rented from ArrayPool.
/// Aion 2 game packets are typically &lt; 64KB after decompression.
/// </summary>
public sealed class Lz4Decompressor
{
    private const int MaxDecompressedSize = 64 * 1024;
    private long _successCount;
    private long _failureCount;

    public long SuccessCount => Volatile.Read(ref _successCount);
    public long FailureCount => Volatile.Read(ref _failureCount);

    /// <summary>
    /// Tries to decompress, then invokes consumer with the decompressed data.
    /// Buffer is returned to pool after consumer completes.
    /// </summary>
    public bool TryDecompress(ReadOnlySpan<byte> compressed, Action<ReadOnlyMemory<byte>> consumer)
    {
        var output = ArrayPool<byte>.Shared.Rent(MaxDecompressedSize);
        try
        {
            int len = LZ4Codec.Decode(compressed, output);
            if (len <= 0)
            {
                Interlocked.Increment(ref _failureCount);
                return false;
            }
            Interlocked.Increment(ref _successCount);
            consumer(new ReadOnlyMemory<byte>(output, 0, len));
            return true;
        }
        catch
        {
            Interlocked.Increment(ref _failureCount);
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(output);
        }
    }
}
