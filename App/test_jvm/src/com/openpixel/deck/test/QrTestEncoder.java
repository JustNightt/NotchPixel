package com.openpixel.deck.test;

import com.openpixel.deck.qr.QrReedSolomon;
import com.openpixel.deck.qr.QrVersionTables;

import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;

/**
 * Genuine ISO/IEC 18004 QR test fixture encoder (Versions 1-14, ECC L/M/Q/H,
 * Byte/Alphanumeric/Numeric/ECI modes) and synthetic YUV luma frame renderer.
 */
public final class QrTestEncoder {
    public static final int MODE_NUMERIC = 1;
    public static final int MODE_ALPHANUMERIC = 2;
    public static final int MODE_BYTE = 4;

    private static final String ALPHA = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";

    public static final class LumaFrame {
        public final byte[] yPlane;
        public final int width;
        public final int height;

        public LumaFrame(byte[] yPlane, int width, int height) {
            this.yPlane = yPlane;
            this.width = width;
            this.height = height;
        }
    }

    private QrTestEncoder() {}

    public static boolean[][] encode(String text, int ecLevel, int mode, boolean prependUtf8Eci) {
        byte[] utf8 = text.getBytes(StandardCharsets.UTF_8);
        int version = 0;
        for (int v = 1; v <= QrVersionTables.MAX_VERSION; v++) {
            int dataCapBits = dataCodewords(ecLevel, v) * 8;
            int needed = neededDataBits(text, utf8, v, mode, prependUtf8Eci);
            if (needed <= dataCapBits) {
                version = v;
                break;
            }
        }
        if (version == 0) throw new IllegalArgumentException("Payload too large for test QR encoder");

        int dataCwCount = dataCodewords(ecLevel, version);
        int capBits = dataCwCount * 8;
        List<Boolean> bits = new ArrayList<>(capBits);

        if (prependUtf8Eci) {
            appendBits(bits, 0b0111, 4); // ECI mode
            appendBits(bits, 26, 8);     // ECI 26 = UTF-8
        }

        if (mode == MODE_BYTE) {
            appendBits(bits, 0b0100, 4);
            appendBits(bits, utf8.length, version <= 9 ? 8 : 16);
            for (byte b : utf8) appendBits(bits, b & 0xFF, 8);
        } else if (mode == MODE_ALPHANUMERIC) {
            appendBits(bits, 0b0010, 4);
            appendBits(bits, text.length(), version <= 9 ? 9 : 11);
            for (int i = 0; i < text.length(); i += 2) {
                int a = ALPHA.indexOf(text.charAt(i));
                if (i + 1 < text.length()) {
                    int b = ALPHA.indexOf(text.charAt(i + 1));
                    appendBits(bits, a * 45 + b, 11);
                } else {
                    appendBits(bits, a, 6);
                }
            }
        } else if (mode == MODE_NUMERIC) {
            appendBits(bits, 0b0001, 4);
            appendBits(bits, text.length(), version <= 9 ? 10 : 12);
            for (int i = 0; i < text.length(); i += 3) {
                int rem = text.length() - i;
                if (rem >= 3) {
                    int val = Integer.parseInt(text.substring(i, i + 3));
                    appendBits(bits, val, 10);
                } else if (rem == 2) {
                    int val = Integer.parseInt(text.substring(i, i + 2));
                    appendBits(bits, val, 7);
                } else {
                    int val = Integer.parseInt(text.substring(i, i + 1));
                    appendBits(bits, val, 4);
                }
            }
        } else {
            throw new IllegalArgumentException("Unsupported mode");
        }

        int term = Math.min(4, capBits - bits.size());
        for (int i = 0; i < term; i++) bits.add(false);
        while (bits.size() % 8 != 0) bits.add(false);

        int[] dataBytes = new int[dataCwCount];
        int byteIdx = 0;
        for (int i = 0; i < bits.size(); i += 8) {
            int v = 0;
            for (int j = 0; j < 8; j++) v = (v << 1) | (bits.get(i + j) ? 1 : 0);
            dataBytes[byteIdx++] = v;
        }
        int pad = 0xEC;
        while (byteIdx < dataCwCount) {
            dataBytes[byteIdx++] = pad;
            pad = (pad == 0xEC) ? 0x11 : 0xEC;
        }

        int[] codewords = interleave(dataBytes, ecLevel, version, 0);
        return buildMatrix(codewords, ecLevel, version, 2);
    }

    /**
     * Builds a QR matrix and flips {@code corruptBytesInBlock0} codeword bytes inside block 0
     * to test Reed-Solomon error correction through the full image decoder pipeline.
     */
    public static boolean[][] encodeWithInjectedByteErrors(String text, int ecLevel, int corruptBytesInBlock0) {
        byte[] utf8 = text.getBytes(StandardCharsets.UTF_8);
        int version = 0;
        for (int v = 1; v <= QrVersionTables.MAX_VERSION; v++) {
            if (neededDataBits(text, utf8, v, MODE_BYTE, false) <= dataCodewords(ecLevel, v) * 8) {
                version = v;
                break;
            }
        }
        int dataCwCount = dataCodewords(ecLevel, version);
        int capBits = dataCwCount * 8;
        List<Boolean> bits = new ArrayList<>(capBits);
        appendBits(bits, 0b0100, 4);
        appendBits(bits, utf8.length, version <= 9 ? 8 : 16);
        for (byte b : utf8) appendBits(bits, b & 0xFF, 8);
        int term = Math.min(4, capBits - bits.size());
        for (int i = 0; i < term; i++) bits.add(false);
        while (bits.size() % 8 != 0) bits.add(false);
        int[] dataBytes = new int[dataCwCount];
        int byteIdx = 0;
        for (int i = 0; i < bits.size(); i += 8) {
            int v = 0;
            for (int j = 0; j < 8; j++) v = (v << 1) | (bits.get(i + j) ? 1 : 0);
            dataBytes[byteIdx++] = v;
        }
        int pad = 0xEC;
        while (byteIdx < dataCwCount) {
            dataBytes[byteIdx++] = pad;
            pad = (pad == 0xEC) ? 0x11 : 0xEC;
        }
        int[] codewords = interleave(dataBytes, ecLevel, version, corruptBytesInBlock0);
        return buildMatrix(codewords, ecLevel, version, 3);
    }

    private static int neededDataBits(String text, byte[] utf8, int version, int mode, boolean eci) {
        int bits = eci ? 12 : 0;
        if (mode == MODE_BYTE) {
            return bits + 4 + (version <= 9 ? 8 : 16) + utf8.length * 8;
        } else if (mode == MODE_ALPHANUMERIC) {
            int pairs = text.length() / 2;
            int rem = text.length() % 2;
            return bits + 4 + (version <= 9 ? 9 : 11) + pairs * 11 + rem * 6;
        } else {
            int groups = text.length() / 3;
            int rem = text.length() % 3;
            int remBits = rem == 2 ? 7 : (rem == 1 ? 4 : 0);
            return bits + 4 + (version <= 9 ? 10 : 12) + groups * 10 + remBits;
        }
    }

    private static int dataCodewords(int ecLevel, int version) {
        int[] spec = QrVersionTables.getEccSpec(ecLevel, version);
        return spec[1] * spec[2] + spec[3] * spec[4];
    }

    private static void appendBits(List<Boolean> bits, int val, int count) {
        for (int i = count - 1; i >= 0; i--) {
            bits.add(((val >> i) & 1) != 0);
        }
    }

    private static int[] interleave(int[] dataBytes, int ecLevel, int version, int corruptFirstN) {
        int[] spec = QrVersionTables.getEccSpec(ecLevel, version);
        int ecPerBlock = spec[0];
        int g1 = spec[1], d1 = spec[2], g2 = spec[3], d2 = spec[4];
        int totalBlocks = g1 + g2;

        int[][] dataBlocks = new int[totalBlocks][];
        int[][] ecBlocks = new int[totalBlocks][];
        int off = 0;
        for (int i = 0; i < totalBlocks; i++) {
            int len = i < g1 ? d1 : d2;
            dataBlocks[i] = new int[len];
            System.arraycopy(dataBytes, off, dataBlocks[i], 0, len);
            off += len;
            ecBlocks[i] = QrReedSolomon.computeEc(dataBlocks[i], ecPerBlock);
        }

        // Inject errors into block 0 after RS computation
        for (int i = 0; i < corruptFirstN && i < dataBlocks[0].length; i++) {
            dataBlocks[0][i] ^= 0xA5;
        }

        int total = QrVersionTables.totalCodewords(ecLevel, version);
        int[] out = new int[total];
        int idx = 0;
        int maxData = Math.max(d1, d2);
        for (int i = 0; i < maxData; i++) {
            for (int b = 0; b < totalBlocks; b++) {
                if (i < dataBlocks[b].length) out[idx++] = dataBlocks[b][i];
            }
        }
        for (int i = 0; i < ecPerBlock; i++) {
            for (int b = 0; b < totalBlocks; b++) {
                out[idx++] = ecBlocks[b][i];
            }
        }
        return out;
    }

    private static boolean[][] buildMatrix(int[] codewords, int ecLevel, int version, int maskId) {
        int size = 17 + version * 4;
        boolean[][] m = new boolean[size][size];
        boolean[][] fn = new boolean[size][size];

        placeFinder(m, fn, 0, 0, size);
        placeFinder(m, fn, 0, size - 7, size);
        placeFinder(m, fn, size - 7, 0, size);

        for (int i = 8; i < size - 8; i++) {
            boolean dark = (i % 2) == 0;
            setFn(m, fn, 6, i, dark, size);
            setFn(m, fn, i, 6, dark, size);
        }

        int[] centers = QrVersionTables.getAlignmentCenters(version);
        for (int ar : centers) {
            for (int ac : centers) {
                boolean overlaps = (ar <= 8 && ac <= 8) || (ar <= 8 && ac >= size - 9) || (ar >= size - 9 && ac <= 8);
                if (overlaps) continue;
                for (int dr = -2; dr <= 2; dr++) {
                    for (int dc = -2; dc <= 2; dc++) {
                        setFn(m, fn, ar + dr, ac + dc, Math.max(Math.abs(dr), Math.abs(dc)) != 1, size);
                    }
                }
            }
        }

        for (int i = 0; i <= 5; i++) {
            setFn(m, fn, 8, i, false, size);
            setFn(m, fn, i, 8, false, size);
        }
        setFn(m, fn, 8, 7, false, size);
        setFn(m, fn, 8, 8, false, size);
        setFn(m, fn, 7, 8, false, size);
        for (int i = 0; i < 8; i++) {
            setFn(m, fn, 8, size - 1 - i, false, size);
            setFn(m, fn, size - 1 - i, 8, false, size);
        }
        setFn(m, fn, size - 8, 8, true, size);

        if (version >= 7) {
            int bits = QrVersionTables.encodeVersionInfo(version);
            for (int i = 0; i < 18; i++) {
                boolean bit = ((bits >> i) & 1) != 0;
                int a = size - 11 + (i % 3);
                int b = i / 3;
                setFn(m, fn, b, a, bit, size);
                setFn(m, fn, a, b, bit, size);
            }
        }

        int bitIdx = 0;
        int totalBits = codewords.length * 8;
        for (int right = size - 1; right >= 1; right -= 2) {
            if (right == 6) right = 5;
            for (int vert = 0; vert < size; vert++) {
                boolean upward = ((right + 1) & 2) == 0;
                int row = upward ? size - 1 - vert : vert;
                for (int j = 0; j < 2; j++) {
                    int col = right - j;
                    if (fn[row][col]) continue;
                    if (bitIdx < totalBits) {
                        boolean b = ((codewords[bitIdx >> 3] >> (7 - (bitIdx & 7))) & 1) != 0;
                        if (maskBit(maskId, row, col)) b = !b;
                        m[row][col] = b;
                        bitIdx++;
                    }
                }
            }
        }

        int fmtBits = QrVersionTables.encodeFormatInfo(ecLevel, maskId);
        for (int i = 0; i <= 5; i++) m[i][8] = ((fmtBits >> i) & 1) != 0;
        m[7][8] = ((fmtBits >> 6) & 1) != 0;
        m[8][8] = ((fmtBits >> 7) & 1) != 0;
        m[8][7] = ((fmtBits >> 8) & 1) != 0;
        for (int i = 9; i < 15; i++) m[8][14 - i] = ((fmtBits >> i) & 1) != 0;
        for (int i = 0; i < 8; i++) m[8][size - 1 - i] = ((fmtBits >> i) & 1) != 0;
        for (int i = 8; i < 15; i++) m[size - 15 + i][8] = ((fmtBits >> i) & 1) != 0;
        m[size - 8][8] = true;

        return m;
    }

    private static void setFn(boolean[][] m, boolean[][] fn, int r, int c, boolean dark, int size) {
        if (r < 0 || r >= size || c < 0 || c >= size) return;
        m[r][c] = dark;
        fn[r][c] = true;
    }

    private static void placeFinder(boolean[][] m, boolean[][] fn, int r0, int c0, int size) {
        for (int dr = -1; dr <= 7; dr++) {
            for (int dc = -1; dc <= 7; dc++) {
                boolean inside = dr >= 0 && dr <= 6 && dc >= 0 && dc <= 6;
                boolean dark = inside && (dr == 0 || dr == 6 || dc == 0 || dc == 6 ||
                        (dr >= 2 && dr <= 4 && dc >= 2 && dc <= 4));
                setFn(m, fn, r0 + dr, c0 + dc, dark, size);
            }
        }
    }

    private static boolean maskBit(int mask, int r, int c) {
        switch (mask) {
            case 0: return ((r + c) & 1) == 0;
            case 1: return (r & 1) == 0;
            case 2: return (c % 3) == 0;
            case 3: return ((r + c) % 3) == 0;
            case 4: return (((r / 2) + (c / 3)) & 1) == 0;
            case 5: return ((r * c) % 2) + ((r * c) % 3) == 0;
            case 6: return ((((r * c) % 2) + ((r * c) % 3)) & 1) == 0;
            default: return ((((r + c) % 2) + ((r * c) % 3)) & 1) == 0;
        }
    }

    public static boolean[][] rotateMatrix(boolean[][] m, int deg) {
        int n = m.length;
        boolean[][] out = new boolean[n][n];
        for (int r = 0; r < n; r++) {
            for (int c = 0; c < n; c++) {
                if (deg == 90) out[c][n - 1 - r] = m[r][c];
                else if (deg == 180) out[n - 1 - r][n - 1 - c] = m[r][c];
                else if (deg == 270) out[n - 1 - c][r] = m[r][c];
                else out[r][c] = m[r][c];
            }
        }
        return out;
    }

    public static LumaFrame renderToLuma(boolean[][] modules, int scalePx, int quietPx,
                                         int darkVal, int lightVal, double skewX, double skewY,
                                         boolean applyBoxBlur) {
        int n = modules.length;
        int qrPx = n * scalePx;
        int w = qrPx + 2 * quietPx + (int) Math.ceil(Math.abs(skewX) * qrPx) + 20;
        int h = qrPx + 2 * quietPx + (int) Math.ceil(Math.abs(skewY) * qrPx) + 20;
        byte[] y = new byte[w * h];
        for (int i = 0; i < y.length; i++) y[i] = (byte) lightVal;

        int ox = quietPx + 10;
        int oy = quietPx + 10;
        for (int py = 0; py < h; py++) {
            for (int px = 0; px < w; px++) {
                double ny = (py - oy) / (double) qrPx;
                double nx = (px - ox - skewX * (py - oy)) / (double) qrPx;
                double u = nx;
                double v = ny - skewY * nx;
                if (u >= 0 && u < 1 && v >= 0 && v < 1) {
                    int mc = Math.min(n - 1, (int) (u * n));
                    int mr = Math.min(n - 1, (int) (v * n));
                    y[py * w + px] = (byte) (modules[mr][mc] ? darkVal : lightVal);
                }
            }
        }

        if (applyBoxBlur) {
            byte[] blurred = new byte[w * h];
            System.arraycopy(y, 0, blurred, 0, y.length);
            for (int py = 1; py < h - 1; py++) {
                for (int px = 1; px < w - 1; px++) {
                    int sum = 0;
                    for (int dy = -1; dy <= 1; dy++) {
                        for (int dx = -1; dx <= 1; dx++) {
                            sum += y[(py + dy) * w + (px + dx)] & 0xFF;
                        }
                    }
                    blurred[py * w + px] = (byte) (sum / 9);
                }
            }
            return new LumaFrame(blurred, w, h);
        }
        return new LumaFrame(y, w, h);
    }
}
