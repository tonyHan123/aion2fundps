using System.Buffers;
using K4os.Compression.LZ4;

namespace Aion2FunDps.Protocol;

/// <summary>
/// LZ4 block-format decompressor for Aion 2 game packets.
///
/// Aion 2 compressed packet structure (after 0xff 0xff marker is consumed):
///   [originLength: uint32 LE, 4 bytes] [LZ4-compressed data]
///
/// We MUST know originLength up-front because LZ4 block format requires the
/// caller to provide an exact-sized output buffer.
/// </summary>
public sealed class Lz4Decompressor
{
    private long _successCount;
    private long _failureCount;

    public long SuccessCount => Volatile.Read(ref _successCount);
    public long FailureCount => Volatile.Read(ref _failureCount);

    /// <summary>
    /// Decompresses with a known target size, then invokes consumer with the data.
    /// Buffer is returned to ArrayPool after consumer completes.
    /// </summary>
    public bool TryDecompress(ReadOnlySpan<byte> compressed, int originLength, Action<ReadOnlyMemory<byte>> consumer)
    {
        if (originLength <= 0 || originLength > 4 * 1024 * 1024)
        {
            Interlocked.Increment(ref _failureCount);
            return false;
        }

        var output = ArrayPool<byte>.Shared.Rent(originLength);
        try
        {
            int decoded = LZ4Codec.Decode(compressed, output.AsSpan(0, originLength));
            if (decoded != originLength)
            {
                Interlocked.Increment(ref _failureCount);
                return false;
            }
            Interlocked.Increment(ref _successCount);
            consumer(new ReadOnlyMemory<byte>(output, 0, decoded));
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
