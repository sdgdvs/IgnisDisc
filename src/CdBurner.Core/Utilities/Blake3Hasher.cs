using System.Buffers.Binary;
using System.Numerics;

namespace CdBurner.Core.Utilities;

/// <summary>
/// Incremental BLAKE3 hash (default 32-byte output, unkeyed), written from the public-domain (CC0) BLAKE3
/// specification and reference implementation. Portable scalar code: fast enough for disc staging and needs no
/// native library or download. Checked against the official test vectors in the test suite; its output matches
/// <c>b3sum</c>, so a <c>BLAKE3SUMS</c> file it writes can be checked with <c>b3sum -c</c>.
/// </summary>
public sealed class Blake3Hasher
{
    private const int BlockLen = 64;
    private const int ChunkLen = 1024;
    private const uint ChunkStart = 1;
    private const uint ChunkEnd = 2;
    private const uint Parent = 4;
    private const uint Root = 8;

    private static readonly uint[] Iv =
    {
        0x6A09E667, 0xBB67AE85, 0x3C6EF372, 0xA54FF53A, 0x510E527F, 0x9B05688C, 0x1F83D9AB, 0x5BE0CD19
    };

    private static readonly int[] MessagePermutation = { 2, 6, 3, 10, 7, 0, 4, 13, 1, 11, 12, 5, 9, 14, 15, 8 };

    // Chunk state
    private readonly uint[] _chunkCv = new uint[8];
    private readonly byte[] _block = new byte[BlockLen];
    private int _blockLen;
    private int _blocksCompressed;
    private ulong _chunkCounter;

    // Chaining values of completed subtrees, at most one per level (54 levels covers 2^64 bytes)
    private readonly uint[][] _cvStack = new uint[54][];
    private int _cvStackLen;

    // Scratch
    private readonly uint[] _words = new uint[16];
    private readonly uint[] _state = new uint[16];
    private readonly uint[] _out = new uint[16];

    public Blake3Hasher()
    {
        Iv.CopyTo(_chunkCv, 0);
    }

    /// <summary>One-shot BLAKE3 of <paramref name="data"/> (32 bytes).</summary>
    public static byte[] HashData(ReadOnlySpan<byte> data)
    {
        var hasher = new Blake3Hasher();
        hasher.Append(data);
        return hasher.GetHashAndReset();
    }

    private int ChunkBytesSoFar => _blocksCompressed * BlockLen + _blockLen;

    /// <summary>Adds bytes to the hash.</summary>
    public void Append(ReadOnlySpan<byte> input)
    {
        while (input.Length > 0)
        {
            // A full chunk is only finished once more input arrives: the last chunk must stay open so finalize can
            // mark it (and the tree root) correctly.
            if (ChunkBytesSoFar == ChunkLen)
            {
                uint[] chunkCv = new uint[8];
                OutputChainingValue(_chunkCv, _block, _blockLen, _chunkCounter, ChunkFlags() | ChunkEnd, chunkCv);
                ulong totalChunks = _chunkCounter + 1;
                AddChunkChainingValue(chunkCv, totalChunks);
                ResetChunk(totalChunks);
            }

            if (_blockLen == BlockLen)
            {
                LoadWords(_block, _words);
                Compress(_chunkCv, _words, _chunkCounter, BlockLen, ChunkFlags(), _out);
                Array.Copy(_out, _chunkCv, 8);
                _blocksCompressed++;
                _blockLen = 0;
                Array.Clear(_block);
            }

            int take = Math.Min(BlockLen - _blockLen, input.Length);
            input[..take].CopyTo(_block.AsSpan(_blockLen));
            _blockLen += take;
            input = input[take..];
        }
    }

    /// <summary>Adds bytes to the hash.</summary>
    public void Append(byte[] buffer, int offset, int count) => Append(buffer.AsSpan(offset, count));

    /// <summary>Returns the 32-byte hash of everything appended and resets the hasher for reuse.</summary>
    public byte[] GetHashAndReset()
    {
        // Output node of the last (possibly partial) chunk
        uint[] inputCv = (uint[])_chunkCv.Clone();
        uint[] blockWords = new uint[16];
        LoadWords(_block, blockWords);
        ulong counter = _chunkCounter;
        uint blockLen = (uint)_blockLen;
        uint flags = ChunkFlags() | ChunkEnd;

        // Fold in the completed subtrees from the right, each as a parent node
        for (int i = _cvStackLen - 1; i >= 0; i--)
        {
            uint[] rightCv = new uint[8];
            Compress(inputCv, blockWords, counter, blockLen, flags, _out);
            Array.Copy(_out, rightCv, 8);

            Array.Copy(_cvStack[i], 0, blockWords, 0, 8);
            Array.Copy(rightCv, 0, blockWords, 8, 8);
            Iv.CopyTo(inputCv, 0);
            counter = 0;
            blockLen = BlockLen;
            flags = Parent;
        }

        Compress(inputCv, blockWords, 0, blockLen, flags | Root, _out);
        byte[] hash = new byte[32];
        for (int i = 0; i < 8; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(hash.AsSpan(i * 4), _out[i]);
        }

        Iv.CopyTo(_chunkCv, 0);
        _cvStackLen = 0;
        ResetChunk(0);
        return hash;
    }

    private uint ChunkFlags() => _blocksCompressed == 0 ? ChunkStart : 0;

    private void ResetChunk(ulong chunkCounter)
    {
        Iv.CopyTo(_chunkCv, 0);
        _chunkCounter = chunkCounter;
        _blocksCompressed = 0;
        _blockLen = 0;
        Array.Clear(_block);
    }

    private void AddChunkChainingValue(uint[] newCv, ulong totalChunks)
    {
        // Merge equal-sized subtrees: one merge per trailing zero bit of the chunk count.
        while ((totalChunks & 1) == 0)
        {
            uint[] left = _cvStack[--_cvStackLen];
            Array.Copy(left, 0, _words, 0, 8);
            Array.Copy(newCv, 0, _words, 8, 8);
            Compress(Iv, _words, 0, BlockLen, Parent, _out);
            newCv = new uint[8];
            Array.Copy(_out, newCv, 8);
            totalChunks >>= 1;
        }
        _cvStack[_cvStackLen++] = newCv;
    }

    private void OutputChainingValue(uint[] cv, byte[] block, int blockLen, ulong counter, uint flags, uint[] destination)
    {
        LoadWords(block, _words);
        Compress(cv, _words, counter, (uint)blockLen, flags, _out);
        Array.Copy(_out, destination, 8);
    }

    private static void LoadWords(byte[] block, uint[] words)
    {
        for (int i = 0; i < 16; i++)
        {
            words[i] = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(i * 4));
        }
    }

    private void Compress(uint[] cv, uint[] blockWords, ulong counter, uint blockLen, uint flags, uint[] output)
    {
        uint[] s = _state;
        Array.Copy(cv, 0, s, 0, 8);
        s[8] = Iv[0];
        s[9] = Iv[1];
        s[10] = Iv[2];
        s[11] = Iv[3];
        s[12] = (uint)counter;
        s[13] = (uint)(counter >> 32);
        s[14] = blockLen;
        s[15] = flags;

        Span<uint> m = stackalloc uint[16];
        Span<uint> permuted = stackalloc uint[16];
        blockWords.AsSpan(0, 16).CopyTo(m);

        for (int round = 0; round < 7; round++)
        {
            G(s, 0, 4, 8, 12, m[0], m[1]);
            G(s, 1, 5, 9, 13, m[2], m[3]);
            G(s, 2, 6, 10, 14, m[4], m[5]);
            G(s, 3, 7, 11, 15, m[6], m[7]);
            G(s, 0, 5, 10, 15, m[8], m[9]);
            G(s, 1, 6, 11, 12, m[10], m[11]);
            G(s, 2, 7, 8, 13, m[12], m[13]);
            G(s, 3, 4, 9, 14, m[14], m[15]);

            if (round < 6)
            {
                for (int i = 0; i < 16; i++)
                {
                    permuted[i] = m[MessagePermutation[i]];
                }
                permuted.CopyTo(m);
            }
        }

        for (int i = 0; i < 8; i++)
        {
            output[i] = s[i] ^ s[i + 8];
            output[i + 8] = s[i + 8] ^ cv[i];
        }
    }

    private static void G(uint[] s, int a, int b, int c, int d, uint mx, uint my)
    {
        s[a] = s[a] + s[b] + mx;
        s[d] = BitOperations.RotateRight(s[d] ^ s[a], 16);
        s[c] = s[c] + s[d];
        s[b] = BitOperations.RotateRight(s[b] ^ s[c], 12);
        s[a] = s[a] + s[b] + my;
        s[d] = BitOperations.RotateRight(s[d] ^ s[a], 8);
        s[c] = s[c] + s[d];
        s[b] = BitOperations.RotateRight(s[b] ^ s[c], 7);
    }
}
