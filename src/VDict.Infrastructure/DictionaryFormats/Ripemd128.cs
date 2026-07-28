using System.Buffers.Binary;

namespace VDict.Infrastructure.DictionaryFormats;

internal static class Ripemd128
{
    private static readonly int[] R =
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        7, 4, 13, 1, 10, 6, 15, 3, 12, 0, 9, 5, 2, 14, 11, 8,
        3, 10, 14, 4, 9, 15, 8, 1, 2, 7, 0, 6, 13, 11, 5, 12,
        1, 9, 11, 10, 0, 8, 12, 4, 13, 3, 7, 15, 14, 5, 6, 2
    ];

    private static readonly int[] Rp =
    [
        5, 14, 7, 0, 9, 2, 11, 4, 13, 6, 15, 8, 1, 10, 3, 12,
        6, 11, 3, 7, 0, 13, 5, 10, 14, 15, 8, 12, 4, 9, 1, 2,
        15, 5, 1, 3, 7, 14, 6, 9, 11, 8, 12, 2, 10, 0, 4, 13,
        8, 6, 4, 1, 3, 11, 15, 0, 5, 12, 2, 13, 9, 7, 10, 14
    ];

    private static readonly int[] S =
    [
        11, 14, 15, 12, 5, 8, 7, 9, 11, 13, 14, 15, 6, 7, 9, 8,
        7, 6, 8, 13, 11, 9, 7, 15, 7, 12, 15, 9, 11, 7, 13, 12,
        11, 13, 6, 7, 14, 9, 13, 15, 14, 8, 13, 6, 5, 12, 7, 5,
        11, 12, 14, 15, 14, 15, 9, 8, 9, 14, 5, 6, 8, 6, 5, 12
    ];

    private static readonly int[] Sp =
    [
        8, 9, 9, 11, 13, 15, 15, 5, 7, 7, 8, 11, 14, 14, 12, 6,
        9, 13, 15, 7, 12, 8, 9, 11, 7, 7, 12, 7, 6, 15, 13, 11,
        9, 7, 15, 11, 8, 6, 6, 14, 12, 13, 5, 14, 13, 13, 7, 5,
        15, 5, 8, 11, 14, 14, 6, 14, 6, 9, 12, 9, 12, 5, 15, 8
    ];

    public static byte[] Hash(byte[] message)
    {
        uint h0 = 0x67452301;
        uint h1 = 0xefcdab89;
        uint h2 = 0x98badcfe;
        uint h3 = 0x10325476;

        foreach (var block in PadAndSplit(message))
        {
            var a = h0;
            var b = h1;
            var c = h2;
            var d = h3;
            var ap = h0;
            var bp = h1;
            var cp = h2;
            var dp = h3;

            for (var j = 0; j < 64; j++)
            {
                var t = RotateLeft(a + F(j, b, c, d) + block[R[j]] + K(j), S[j]);
                a = d;
                d = c;
                c = b;
                b = t;

                t = RotateLeft(ap + F(63 - j, bp, cp, dp) + block[Rp[j]] + Kp(j), Sp[j]);
                ap = dp;
                dp = cp;
                cp = bp;
                bp = t;
            }

            var tmp = h1 + c + dp;
            h1 = h2 + d + ap;
            h2 = h3 + a + bp;
            h3 = h0 + b + cp;
            h0 = tmp;
        }

        var digest = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(0, 4), h0);
        BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(4, 4), h1);
        BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(8, 4), h2);
        BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(12, 4), h3);
        return digest;
    }

    private static IEnumerable<uint[]> PadAndSplit(byte[] message)
    {
        var originalLength = message.Length;
        var mod = ((originalLength - 56) % 64 + 64) % 64;
        var padLength = 64 - mod;

        var padded = new byte[originalLength + padLength + 8];
        Buffer.BlockCopy(message, 0, padded, 0, message.Length);
        padded[originalLength] = 0x80;
        BinaryPrimitives.WriteUInt64LittleEndian(padded.AsSpan(padded.Length - 8), (ulong)originalLength * 8);

        for (var i = 0; i < padded.Length; i += 64)
        {
            var block = new uint[16];
            for (var j = 0; j < 16; j++)
            {
                block[j] = BinaryPrimitives.ReadUInt32LittleEndian(padded.AsSpan(i + j * 4, 4));
            }

            yield return block;
        }
    }

    private static uint F(int j, uint x, uint y, uint z)
    {
        return j switch
        {
            < 16 => x ^ y ^ z,
            < 32 => (x & y) | (z & ~x),
            < 48 => (x | ~y) ^ z,
            _ => (x & z) | (y & ~z)
        };
    }

    private static uint K(int j)
    {
        return j switch
        {
            < 16 => 0x00000000,
            < 32 => 0x5a827999,
            < 48 => 0x6ed9eba1,
            _ => 0x8f1bbcdc
        };
    }

    private static uint Kp(int j)
    {
        return j switch
        {
            < 16 => 0x50a28be6,
            < 32 => 0x5c4dd124,
            < 48 => 0x6d703ef3,
            _ => 0x00000000
        };
    }

    private static uint RotateLeft(uint value, int bits)
    {
        return (value << bits) | (value >> (32 - bits));
    }
}
