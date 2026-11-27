package com.openpixel.deck.qr;

/**
 * ISO/IEC 18004 tables for QR Versions 1-14 across ECC levels M, L, H, Q,
 * alignment pattern coordinates, 15-bit format BCH, and 18-bit version BCH.
 */
public final class QrVersionTables {
    private QrVersionTables() {}

    public static final int MAX_VERSION = 14;
    public static final int EC_M = 0; // format bits 00
    public static final int EC_L = 1; // format bits 01
    public static final int EC_H = 2; // format bits 10
    public static final int EC_Q = 3; // format bits 11

    // {ecPerBlock, g1Count, g1Data, g2Count, g2Data} indexed by [ecLevel][version]
    private static final int[][][] ECC_SPECS = {
        // EC_M = 0 (00)
        {
            null,
            {10, 1, 16, 0, 0},   // v1
            {16, 1, 28, 0, 0},   // v2
            {26, 1, 44, 0, 0},   // v3
            {18, 2, 32, 0, 0},   // v4
            {24, 2, 43, 0, 0},   // v5
            {16, 4, 27, 0, 0},   // v6
            {18, 4, 31, 0, 0},   // v7
            {22, 2, 38, 2, 39},  // v8
            {22, 3, 36, 2, 37},  // v9
            {26, 4, 43, 1, 44},  // v10
            {30, 1, 50, 4, 51},  // v11
            {22, 6, 36, 2, 37},  // v12
            {22, 8, 37, 1, 38},  // v13
            {24, 4, 40, 5, 41}   // v14
        },
        // EC_L = 1 (01)
        {
            null,
            {7,  1, 19, 0, 0},   // v1
            {10, 1, 34, 0, 0},   // v2
            {15, 1, 55, 0, 0},   // v3
            {20, 1, 80, 0, 0},   // v4
            {26, 1, 108, 0, 0},  // v5
            {18, 2, 68, 0, 0},   // v6
            {20, 2, 78, 0, 0},   // v7
            {24, 2, 97, 0, 0},   // v8
            {30, 2, 116, 0, 0},  // v9
            {18, 2, 68, 2, 69},  // v10
            {20, 4, 81, 0, 0},   // v11
            {24, 2, 92, 2, 93},  // v12
            {26, 4, 107, 0, 0},  // v13
            {30, 3, 115, 1, 116} // v14
        },
        // EC_H = 2 (10)
        {
            null,
            {17, 1, 9,  0, 0},   // v1
            {28, 1, 16, 0, 0},   // v2
            {22, 2, 13, 0, 0},   // v3
            {16, 4, 9,  0, 0},   // v4
            {22, 2, 11, 2, 12},  // v5
            {28, 4, 15, 0, 0},   // v6
            {26, 4, 13, 1, 14},  // v7
            {26, 4, 14, 2, 15},  // v8
            {24, 4, 12, 4, 13},  // v9
            {28, 6, 15, 2, 16},  // v10
            {24, 3, 12, 8, 13},  // v11
            {28, 7, 14, 4, 15},  // v12
            {22, 12, 11, 4, 12}, // v13
            {24, 11, 12, 5, 13}  // v14
        },
        // EC_Q = 3 (11)
        {
            null,
            {13, 1, 13, 0, 0},   // v1
            {22, 1, 22, 0, 0},   // v2
            {18, 2, 17, 0, 0},   // v3
            {26, 2, 24, 0, 0},   // v4
            {18, 2, 15, 2, 16},  // v5
            {24, 4, 19, 0, 0},   // v6
            {18, 2, 14, 4, 15},  // v7
            {22, 4, 18, 2, 19},  // v8
            {20, 4, 16, 4, 17},  // v9
            {24, 6, 19, 2, 20},  // v10
            {28, 4, 22, 4, 23},  // v11
            {26, 4, 20, 6, 21},  // v12
            {24, 8, 20, 4, 21},  // v13
            {20, 11, 16, 5, 17}  // v14
        }
    };

    private static final int[][] ALIGNMENT_CENTERS = {
        null,
        {},
        {6, 18},
        {6, 22},
        {6, 26},
        {6, 30},
        {6, 34},
        {6, 22, 38},
        {6, 24, 42},
        {6, 26, 46},
        {6, 28, 50},
        {6, 30, 54},
        {6, 32, 58},
        {6, 34, 62},
        {6, 26, 46, 66}
    };

    private static final int[] FORMAT_CODEWORDS = new int[32];
    private static final int[] VERSION_CODEWORDS = new int[MAX_VERSION + 1];

    static {
        for (int data = 0; data < 32; data++) {
            int rem = data;
            for (int i = 0; i < 10; i++) {
                rem = (rem << 1) ^ ((rem >> 9) * 0x537);
            }
            FORMAT_CODEWORDS[data] = ((data << 10) | rem) ^ 0x5412;
        }
        for (int v = 7; v <= MAX_VERSION; v++) {
            int rem = v;
            for (int i = 0; i < 12; i++) {
                rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
            }
            VERSION_CODEWORDS[v] = (v << 12) | rem;
        }
    }

    public static int[] getEccSpec(int ecLevel, int version) {
        if (ecLevel < 0 || ecLevel >= 4 || version < 1 || version > MAX_VERSION) return null;
        return ECC_SPECS[ecLevel][version];
    }

    public static int[] getAlignmentCenters(int version) {
        if (version < 1 || version > MAX_VERSION) return new int[0];
        return ALIGNMENT_CENTERS[version];
    }

    public static int totalCodewords(int ecLevel, int version) {
        int[] spec = getEccSpec(ecLevel, version);
        if (spec == null) return 0;
        return (spec[1] + spec[3]) * spec[0] + spec[1] * spec[2] + spec[3] * spec[4];
    }

    /**
     * Decodes a 15-bit raw format strip (already bit-assembled, NOT yet unmasked).
     * Returns 5-bit data (bits 4..3 = ecLevel, bits 2..0 = maskId), or -1 if > 3 bit errors.
     */
    public static int decodeFormatInfo(int raw15Bits) {
        int bestData = -1;
        int bestDist = 4;
        for (int data = 0; data < 32; data++) {
            int d = Integer.bitCount(raw15Bits ^ FORMAT_CODEWORDS[data]);
            if (d == 0) return data;
            if (d < bestDist) {
                bestDist = d;
                bestData = data;
            }
        }
        return bestData;
    }

    /**
     * Decodes an 18-bit version block (v >= 7). Returns version 7..14 or -1 if > 3 bit errors.
     */
    public static int decodeVersionInfo(int raw18Bits) {
        int bestVer = -1;
        int bestDist = 4;
        for (int v = 7; v <= MAX_VERSION; v++) {
            int d = Integer.bitCount(raw18Bits ^ VERSION_CODEWORDS[v]);
            if (d == 0) return v;
            if (d < bestDist) {
                bestDist = d;
                bestVer = v;
            }
        }
        return bestVer;
    }

    public static int encodeFormatInfo(int ecLevel, int maskId) {
        return FORMAT_CODEWORDS[((ecLevel & 3) << 3) | (maskId & 7)];
    }

    public static int encodeVersionInfo(int version) {
        if (version < 7 || version > MAX_VERSION) return 0;
        return VERSION_CODEWORDS[version];
    }
}
