using System;
using System.Collections.Generic;
using System.Text;

namespace OpenPixel.Rendering
{
    public sealed class QrMatrix
    {
        public int Version { get; init; }
        public int Size { get; init; }
        public int Mask { get; init; }
        public bool[,] Modules { get; init; }
    }

    // ========================================================================
    // Minimal, dependency-free QR Code encoder (byte mode, ECC level M,
    // versions 1-10). Used for the local Wi-Fi PC Deck pairing payload so the
    // phone camera can actually decode the code drawn on the island/settings.
    // ========================================================================
    public static class QrCodeEncoder
    {
        // {ecCodewordsPerBlock, group1Blocks, group1DataCw, group2Blocks, group2DataCw}
        private static readonly int[][] EccM =
        {
            null,
            new[] { 10, 1, 16, 0, 0 },
            new[] { 16, 1, 28, 0, 0 },
            new[] { 26, 1, 44, 0, 0 },
            new[] { 18, 2, 32, 0, 0 },
            new[] { 24, 2, 43, 0, 0 },
            new[] { 16, 4, 27, 0, 0 },
            new[] { 18, 4, 31, 0, 0 },
            new[] { 22, 2, 38, 2, 39 },
            new[] { 22, 3, 36, 2, 37 },
            new[] { 26, 4, 43, 1, 44 }
        };

        private static readonly int[][] AlignmentCenters =
        {
            null,
            Array.Empty<int>(),
            new[] { 6, 18 },
            new[] { 6, 22 },
            new[] { 6, 26 },
            new[] { 6, 30 },
            new[] { 6, 34 },
            new[] { 6, 22, 38 },
            new[] { 6, 24, 42 },
            new[] { 6, 26, 46 },
            new[] { 6, 28, 50 }
        };

        private static readonly byte[] GfExp = new byte[512];
        private static readonly byte[] GfLog = new byte[256];

        static QrCodeEncoder()
        {
            int x = 1;
            for (int i = 0; i < 255; ++i)
            {
                GfExp[i] = (byte)x;
                GfLog[x] = (byte)i;
                x <<= 1;
                if ((x & 0x100) != 0) x ^= 0x11D;
            }
            for (int i = 255; i < GfExp.Length; ++i) GfExp[i] = GfExp[i - 255];
        }

        public static QrMatrix Encode(string text)
        {
            byte[] payload = Encoding.UTF8.GetBytes(text ?? "");
            int version = 0;
            for (int v = 1; v <= 10; ++v)
            {
                int countBits = v <= 9 ? 8 : 16;
                if (4 + countBits + payload.Length * 8 <= DataCodewords(v) * 8)
                {
                    version = v;
                    break;
                }
            }
            if (version == 0) throw new ArgumentException("Pairing payload is too long for a QR code.");

            int capacityBits = DataCodewords(version) * 8;
            var bits = new List<bool>(capacityBits);
            AppendBits(bits, 0b0100, 4); // byte mode
            AppendBits(bits, payload.Length, version <= 9 ? 8 : 16);
            foreach (byte b in payload) AppendBits(bits, b, 8);

            int terminator = Math.Min(4, capacityBits - bits.Count);
            for (int i = 0; i < terminator; ++i) bits.Add(false);
            while (bits.Count % 8 != 0) bits.Add(false);

            var dataBytes = new List<byte>(capacityBits / 8);
            for (int i = 0; i < bits.Count; i += 8)
            {
                int value = 0;
                for (int j = 0; j < 8; ++j) value = (value << 1) | (bits[i + j] ? 1 : 0);
                dataBytes.Add((byte)value);
            }
            byte padByte = 0xEC;
            while (dataBytes.Count < capacityBits / 8)
            {
                dataBytes.Add(padByte);
                padByte = (byte)(padByte == 0xEC ? 0x11 : 0xEC);
            }

            byte[] codewords = Interleave(dataBytes.ToArray(), version);
            bool[,] modules = BuildMatrix(codewords, version, out int mask);

            return new QrMatrix
            {
                Version = version,
                Size = 17 + version * 4,
                Mask = mask,
                Modules = modules
            };
        }

        private static int DataCodewords(int version)
        {
            int[] spec = EccM[version];
            return spec[1] * spec[2] + spec[3] * spec[4];
        }

        private static void AppendBits(List<bool> bits, int value, int count)
        {
            for (int i = count - 1; i >= 0; --i) bits.Add(((value >> i) & 1) != 0);
        }

        private static byte[] Interleave(byte[] data, int version)
        {
            int[] spec = EccM[version];
            int ecPerBlock = spec[0];
            int g1 = spec[1], d1 = spec[2], g2 = spec[3], d2 = spec[4];

            var dataBlocks = new List<byte[]>();
            int offset = 0;
            for (int i = 0; i < g1; ++i)
            {
                var block = new byte[d1];
                Array.Copy(data, offset, block, 0, d1);
                offset += d1;
                dataBlocks.Add(block);
            }
            for (int i = 0; i < g2; ++i)
            {
                var block = new byte[d2];
                Array.Copy(data, offset, block, 0, d2);
                offset += d2;
                dataBlocks.Add(block);
            }

            var ecBlocks = new List<byte[]>();
            foreach (byte[] block in dataBlocks) ecBlocks.Add(ComputeEc(block, ecPerBlock));

            var output = new List<byte>(data.Length + ecBlocks.Count * ecPerBlock);
            int maxData = 0;
            foreach (byte[] b in dataBlocks) maxData = Math.Max(maxData, b.Length);
            for (int i = 0; i < maxData; ++i)
                foreach (byte[] b in dataBlocks)
                    if (i < b.Length) output.Add(b[i]);
            for (int i = 0; i < ecPerBlock; ++i)
                foreach (byte[] b in ecBlocks) output.Add(b[i]);
            return output.ToArray();
        }

        private static byte[] ComputeEc(byte[] data, int ecCount)
        {
            var generator = new byte[ecCount + 1];
            generator[0] = 1;
            // Multiply the generator by (x + alpha^i); coefficients are stored
            // highest-power first, so only positions 1..degree shift upward.
            for (int i = 0; i < ecCount; ++i)
            {
                byte factor = GfExp[i];
                for (int j = i + 1; j >= 1; --j)
                    generator[j] = (byte)(generator[j] ^ GfMul(generator[j - 1], factor));
            }

            var remainder = new byte[ecCount];
            foreach (byte b in data)
            {
                int factor = b ^ remainder[0];
                for (int i = 0; i < ecCount - 1; ++i) remainder[i] = remainder[i + 1];
                remainder[ecCount - 1] = 0;
                for (int i = 0; i < ecCount; ++i)
                    remainder[i] ^= GfMul(generator[i + 1], (byte)factor);
            }
            return remainder;
        }

        private static byte GfMul(byte a, byte b)
            => (a == 0 || b == 0) ? (byte)0 : GfExp[GfLog[a] + GfLog[b]];

        private static bool[,] BuildMatrix(byte[] codewords, int version, out int chosenMask)
        {
            int size = 17 + version * 4;
            var modules = new bool[size, size];
            var isFunction = new bool[size, size];

            void SetFn(int row, int col, bool dark)
            {
                if (row < 0 || row >= size || col < 0 || col >= size) return;
                modules[row, col] = dark;
                isFunction[row, col] = true;
            }

            void PlaceFinder(int row, int col)
            {
                for (int dr = -1; dr <= 7; ++dr)
                {
                    for (int dc = -1; dc <= 7; ++dc)
                    {
                        bool inside = dr >= 0 && dr <= 6 && dc >= 0 && dc <= 6;
                        bool dark = inside &&
                                    (dr == 0 || dr == 6 || dc == 0 || dc == 6 ||
                                     (dr >= 2 && dr <= 4 && dc >= 2 && dc <= 4));
                        SetFn(row + dr, col + dc, dark);
                    }
                }
            }

            PlaceFinder(0, 0);
            PlaceFinder(0, size - 7);
            PlaceFinder(size - 7, 0);

            for (int i = 8; i < size - 8; ++i)
            {
                bool dark = (i % 2) == 0;
                SetFn(6, i, dark);
                SetFn(i, 6, dark);
            }

            int[] centers = AlignmentCenters[version];
            foreach (int ar in centers)
            {
                foreach (int ac in centers)
                {
                    bool overlapsFinder = (ar <= 8 && ac <= 8) ||
                                          (ar <= 8 && ac >= size - 9) ||
                                          (ar >= size - 9 && ac <= 8);
                    if (overlapsFinder) continue;
                    for (int dr = -2; dr <= 2; ++dr)
                        for (int dc = -2; dc <= 2; ++dc)
                            SetFn(ar + dr, ac + dc, Math.Max(Math.Abs(dr), Math.Abs(dc)) != 1);
                }
            }

            // Reserve only the exact format-information modules (the timing
            // patterns at row/column 6 must stay untouched).
            for (int i = 0; i <= 5; ++i)
            {
                SetFn(8, i, false);
                SetFn(i, 8, false);
            }
            SetFn(8, 7, false);
            SetFn(8, 8, false);
            SetFn(7, 8, false);
            for (int i = 0; i < 8; ++i)
            {
                SetFn(8, size - 1 - i, false);
                SetFn(size - 1 - i, 8, false);
            }
            SetFn(size - 8, 8, true); // Always-dark module

            if (version >= 7)
            {
                int rem = version;
                for (int i = 0; i < 12; ++i) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
                int bits = (version << 12) | rem;
                for (int i = 0; i < 18; ++i)
                {
                    bool bit = ((bits >> i) & 1) != 0;
                    int a = size - 11 + i % 3;
                    int b = i / 3;
                    SetFn(b, a, bit);
                    SetFn(a, b, bit);
                }
            }

            // Place interleaved codeword bits in the zig-zag pattern
            int bitIndex = 0;
            int totalBits = codewords.Length * 8;
            for (int right = size - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5;
                for (int vert = 0; vert < size; ++vert)
                {
                    for (int j = 0; j < 2; ++j)
                    {
                        int col = right - j;
                        bool upward = ((right + 1) & 2) == 0;
                        int row = upward ? size - 1 - vert : vert;
                        if (isFunction[row, col]) continue;
                        if (bitIndex < totalBits)
                        {
                            modules[row, col] = ((codewords[bitIndex >> 3] >> (7 - (bitIndex & 7))) & 1) != 0;
                            ++bitIndex;
                        }
                    }
                }
            }

            int bestMask = 0;
            int bestScore = int.MaxValue;
            bool[,] best = null;
            for (int mask = 0; mask < 8; ++mask)
            {
                var candidate = (bool[,])modules.Clone();
                for (int row = 0; row < size; ++row)
                    for (int col = 0; col < size; ++col)
                        if (!isFunction[row, col] && MaskBit(mask, row, col))
                            candidate[row, col] = !candidate[row, col];

                DrawFormatBits(candidate, mask);
                int score = Penalty(candidate);
                if (score < bestScore)
                {
                    bestScore = score;
                    bestMask = mask;
                    best = candidate;
                }
            }

            chosenMask = bestMask;
            return best;
        }

        private static bool MaskBit(int mask, int row, int col) => mask switch
        {
            0 => (row + col) % 2 == 0,
            1 => row % 2 == 0,
            2 => col % 3 == 0,
            3 => (row + col) % 3 == 0,
            4 => (row / 2 + col / 3) % 2 == 0,
            5 => (row * col) % 2 + (row * col) % 3 == 0,
            6 => ((row * col) % 2 + (row * col) % 3) % 2 == 0,
            _ => ((row + col) % 2 + (row * col) % 3) % 2 == 0
        };

        private static void DrawFormatBits(bool[,] matrix, int mask)
        {
            int size = matrix.GetLength(0);
            int data = mask; // ECC level M is 0b00
            int rem = data;
            for (int i = 0; i < 10; ++i) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
            int bits = ((data << 10) | rem) ^ 0x5412;

            bool Bit(int i) => ((bits >> i) & 1) != 0;

            for (int i = 0; i <= 5; ++i) matrix[i, 8] = Bit(i);
            matrix[7, 8] = Bit(6);
            matrix[8, 8] = Bit(7);
            matrix[8, 7] = Bit(8);
            for (int i = 9; i < 15; ++i) matrix[8, 14 - i] = Bit(i);

            for (int i = 0; i < 8; ++i) matrix[8, size - 1 - i] = Bit(i);
            for (int i = 8; i < 15; ++i) matrix[size - 15 + i, 8] = Bit(i);
            matrix[size - 8, 8] = true;
        }

        private static int Penalty(bool[,] matrix)
        {
            int size = matrix.GetLength(0);
            int penalty = 0;

            // Rule 1: runs of five or more same-colour modules
            for (int row = 0; row < size; ++row)
            {
                int run = 1;
                for (int col = 1; col <= size; ++col)
                {
                    if (col < size && matrix[row, col] == matrix[row, col - 1]) ++run;
                    else { if (run >= 5) penalty += 3 + (run - 5); run = 1; }
                }
            }
            for (int col = 0; col < size; ++col)
            {
                int run = 1;
                for (int row = 1; row <= size; ++row)
                {
                    if (row < size && matrix[row, col] == matrix[row - 1, col]) ++run;
                    else { if (run >= 5) penalty += 3 + (run - 5); run = 1; }
                }
            }

            // Rule 2: 2x2 blocks of one colour
            for (int row = 0; row < size - 1; ++row)
                for (int col = 0; col < size - 1; ++col)
                {
                    bool c = matrix[row, col];
                    if (c == matrix[row, col + 1] && c == matrix[row + 1, col] && c == matrix[row + 1, col + 1])
                        penalty += 3;
                }

            // Rule 3: finder-like 1:1:3:1:1 patterns with four light modules beside them
            bool[] p1 = { true, false, true, true, true, false, true, false, false, false, false };
            bool[] p2 = { false, false, false, false, true, false, true, true, true, false, true };
            for (int row = 0; row < size; ++row)
                for (int col = 0; col + 11 <= size; ++col)
                {
                    bool m1 = true, m2 = true;
                    for (int k = 0; k < 11; ++k)
                    {
                        bool v = matrix[row, col + k];
                        if (v != p1[k]) m1 = false;
                        if (v != p2[k]) m2 = false;
                    }
                    if (m1 || m2) penalty += 40;
                }
            for (int col = 0; col < size; ++col)
                for (int row = 0; row + 11 <= size; ++row)
                {
                    bool m1 = true, m2 = true;
                    for (int k = 0; k < 11; ++k)
                    {
                        bool v = matrix[row + k, col];
                        if (v != p1[k]) m1 = false;
                        if (v != p2[k]) m2 = false;
                    }
                    if (m1 || m2) penalty += 40;
                }

            // Rule 4: dark/light balance
            int dark = 0;
            for (int row = 0; row < size; ++row)
                for (int col = 0; col < size; ++col)
                    if (matrix[row, col]) ++dark;
            int percent = dark * 100 / (size * size);
            penalty += Math.Abs(percent - 50) / 5 * 10;

            return penalty;
        }
    }
}
