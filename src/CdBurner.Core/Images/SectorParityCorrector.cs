namespace CdBurner.Core.Images;

/// <summary>
/// Repairs a raw 2352-byte CD-ROM sector with the P and Q parity (RSPC) that every Mode 1 and Mode 2 Form 1 sector
/// carries, as laid down in ECMA-130 Annex A. The sector after the sync pattern (bytes 12..2351) is two interleaved
/// byte planes; each plane has 43 P codewords of 26 bytes (24 data + 2 parity) and 26 Q codewords of 45 bytes
/// (43 + 2). Each codeword can repair one wrong byte; going over Q, then P, then Q again lets a repair in one direction
/// unlock another in the other, as drives do.
/// <para>
/// This only proposes a repair. The caller must check the EDC afterwards and accept the result only if it passes:
/// two errors in one codeword can look like one error somewhere else, and the 32-bit EDC catches that.
/// </para>
/// </summary>
public static class SectorParityCorrector
{
    private const int MaxPasses = 16;

    // GF(2^8), field polynomial x^8 + x^4 + x^3 + x^2 + 1 (0x11D), primitive element 2 (ECMA-130 Annex A).
    private static readonly byte[] Exp = new byte[510];
    private static readonly int[] Log = new int[256];

    private static readonly int[][] PCodewords;
    private static readonly int[][] QCodewords;

    static SectorParityCorrector()
    {
        int x = 1;
        for (int i = 0; i < 255; i++)
        {
            Exp[i] = (byte)x;
            Log[x] = i;
            x <<= 1;
            if ((x & 0x100) != 0)
            {
                x ^= 0x11D;
            }
        }
        for (int i = 255; i < 510; i++)
        {
            Exp[i] = Exp[i - 255];
        }

        var p = new List<int[]>();
        var q = new List<int[]>();
        for (int plane = 0; plane < 2; plane++)
        {
            for (int n = 0; n < 43; n++)
            {
                var word = new int[26];
                for (int m = 0; m < 26; m++)
                {
                    word[m] = 12 + 2 * (43 * m + n) + plane;
                }
                p.Add(word);
            }
            for (int n = 0; n < 26; n++)
            {
                var word = new int[45];
                for (int m = 0; m < 43; m++)
                {
                    word[m] = 12 + 2 * ((44 * m + 43 * n) % 1118) + plane;
                }
                word[43] = 12 + 2 * (43 * 26 + n) + plane;
                word[44] = 12 + 2 * (44 * 26 + n) + plane;
                q.Add(word);
            }
        }
        PCodewords = p.ToArray();
        QCodewords = q.ToArray();
    }

    /// <summary>
    /// Repairs <paramref name="sector"/> in place where single wrong bytes per codeword allow it. Returns the number of
    /// bytes changed (0 when every codeword already checks, or nothing could be repaired).
    /// </summary>
    /// <param name="sector">A 2352-byte raw sector. For Mode 2 Form 1 pass it with bytes 12..15 set to zero, as the
    /// parity was computed that way.</param>
    public static int Correct(Span<byte> sector)
    {
        if (sector.Length < RawSectorCodec.RawSectorSize)
        {
            throw new ArgumentException($"A raw sector has {RawSectorCodec.RawSectorSize} bytes.", nameof(sector));
        }

        int changed = 0;
        for (int pass = 0; pass < MaxPasses; pass++)
        {
            int before = changed;
            foreach (int[] word in QCodewords)
            {
                changed += RepairOne(sector, word);
            }
            foreach (int[] word in PCodewords)
            {
                changed += RepairOne(sector, word);
            }
            if (changed == before)
            {
                break;
            }
        }
        return changed;
    }

    /// <summary>Codewords whose parity does not check (0 for an intact sector).</summary>
    public static int Violations(ReadOnlySpan<byte> sector)
    {
        int count = 0;
        foreach (int[] word in PCodewords)
        {
            count += Syndromes(sector, word) == (0, 0) ? 0 : 1;
        }
        foreach (int[] word in QCodewords)
        {
            count += Syndromes(sector, word) == (0, 0) ? 0 : 1;
        }
        return count;
    }

    /// <summary>
    /// The two syndromes of a codeword: S0 = sum of its bytes, S1 = sum of byte i times a^(n-1-i), the parity-check
    /// matrix of Annex A. Both are zero for an intact codeword.
    /// </summary>
    private static (int S0, int S1) Syndromes(ReadOnlySpan<byte> sector, int[] word)
    {
        int n = word.Length, s0 = 0, s1 = 0;
        for (int i = 0; i < n; i++)
        {
            int value = sector[word[i]];
            if (value != 0)
            {
                s0 ^= value;
                s1 ^= Exp[Log[value] + n - 1 - i];
            }
        }
        return (s0, s1);
    }

    /// <summary>
    /// One wrong byte e at position i gives S0 = e and S1 = e * a^(n-1-i), so i follows from log S1 - log S0.
    /// Anything else (one syndrome zero, or a position outside the codeword) is more than one error: left alone.
    /// </summary>
    private static int RepairOne(Span<byte> sector, int[] word)
    {
        var (s0, s1) = Syndromes(sector, word);
        if (s0 == 0 || s1 == 0)
        {
            return 0;
        }

        int n = word.Length;
        int index = n - 1 - ((Log[s1] - Log[s0]) % 255 + 255) % 255;
        if (index < 0 || index >= n)
        {
            return 0;
        }

        sector[word[index]] ^= (byte)s0;
        return 1;
    }
}
