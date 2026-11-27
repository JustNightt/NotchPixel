package com.openpixel.deck.qr;

import java.io.ByteArrayOutputStream;
import java.nio.charset.Charset;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Comparator;
import java.util.List;

/**
 * Self-contained pure-Java ISO/IEC 18004 QR code decoder (Versions 1-14, ECC L/M/Q/H).
 * Supports adaptive 8x8 luma binarization, 1:1:3:1:1 finder detection, projective
 * homography sampling, BCH format/version decoding, GF(256) Reed-Solomon correction,
 * and Numeric/Alphanumeric/Byte/ECI segments.
 */
public final class QrDecoder {
    private static final String ALPHANUMERIC_TABLE = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";

    private QrDecoder() {}

    private static final class FinderCenter {
        final double x;
        final double y;
        final double moduleSize;
        int count;

        FinderCenter(double x, double y, double moduleSize, int count) {
            this.x = x;
            this.y = y;
            this.moduleSize = moduleSize;
            this.count = count;
        }
    }

    public static String decodeLuma(byte[] yPlane, int width, int height) {
        if (yPlane == null || width < 21 || height < 21 || yPlane.length < width * height) {
            return null;
        }
        boolean[] binary = binarizeAdaptive(yPlane, width, height);
        String res = decodeBinary(binary, width, height);
        if (res != null) return res;

        // Fallback: inverted polarity (light modules on dark background)
        boolean[] inverted = new boolean[binary.length];
        for (int i = 0; i < binary.length; i++) inverted[i] = !binary[i];
        return decodeBinary(inverted, width, height);
    }

    private static boolean[] binarizeAdaptive(byte[] yPlane, int width, int height) {
        final int block = 8;
        int bw = (width + block - 1) / block;
        int bh = (height + block - 1) / block;
        int[] blockMin = new int[bw * bh];
        int[] blockMax = new int[bw * bh];
        int[] blockAvg = new int[bw * bh];

        long globalSum = 0;
        int[] hist = new int[256];

        for (int by = 0; by < bh; by++) {
            int y0 = by * block;
            int y1 = Math.min(height, y0 + block);
            for (int bx = 0; bx < bw; bx++) {
                int x0 = bx * block;
                int x1 = Math.min(width, x0 + block);
                int min = 255;
                int max = 0;
                int sum = 0;
                int cnt = 0;
                for (int y = y0; y < y1; y++) {
                    int row = y * width;
                    for (int x = x0; x < x1; x++) {
                        int v = yPlane[row + x] & 0xFF;
                        if (v < min) min = v;
                        if (v > max) max = v;
                        sum += v;
                        hist[v]++;
                        cnt++;
                    }
                }
                globalSum += sum;
                int idx = by * bw + bx;
                blockMin[idx] = min;
                blockMax[idx] = max;
                blockAvg[idx] = cnt > 0 ? (sum / cnt) : 128;
            }
        }

        int otsu = computeOtsu(hist, width * height);
        int[] blockThresh = new int[bw * bh];
        for (int by = 0; by < bh; by++) {
            for (int bx = 0; bx < bw; bx++) {
                int idx = by * bw + bx;
                int min5 = 255;
                int max5 = 0;
                for (int dy = -2; dy <= 2; dy++) {
                    int ny = by + dy;
                    if (ny < 0 || ny >= bh) continue;
                    int nRow = ny * bw;
                    for (int dx = -2; dx <= 2; dx++) {
                        int nx = bx + dx;
                        if (nx < 0 || nx >= bw) continue;
                        int nIdx = nRow + nx;
                        if (blockMin[nIdx] < min5) min5 = blockMin[nIdx];
                        if (blockMax[nIdx] > max5) max5 = blockMax[nIdx];
                    }
                }
                if (max5 - min5 > 24) {
                    blockThresh[idx] = (min5 + max5) >> 1;
                } else {
                    blockThresh[idx] = otsu > 0 ? otsu : (int) (globalSum / Math.max(1, width * height));
                }
            }
        }

        boolean[] out = new boolean[width * height];
        for (int y = 0; y < height; y++) {
            int by = y / block;
            int row = y * width;
            int bRow = by * bw;
            for (int x = 0; x < width; x++) {
                int t = blockThresh[bRow + (x / block)];
                out[row + x] = (yPlane[row + x] & 0xFF) <= t;
            }
        }
        return out;
    }

    private static int computeOtsu(int[] hist, int total) {
        if (total <= 0) return 128;
        double sum = 0;
        for (int i = 0; i < 256; i++) sum += i * (double) hist[i];
        double sumB = 0;
        int wB = 0;
        double maxVar = -1;
        int threshold = 128;
        for (int t = 0; t < 256; t++) {
            wB += hist[t];
            if (wB == 0) continue;
            int wF = total - wB;
            if (wF == 0) break;
            sumB += t * (double) hist[t];
            double mB = sumB / wB;
            double mF = (sum - sumB) / wF;
            double var = (double) wB * (double) wF * (mB - mF) * (mB - mF);
            if (var > maxVar) {
                maxVar = var;
                threshold = t;
            }
        }
        return threshold;
    }

    private static String decodeBinary(boolean[] bin, int w, int h) {
        List<FinderCenter> centers = findFinderPatterns(bin, w, h);
        if (centers.size() < 3) return null;

        // Try top finder combinations if > 3 clusters detected
        int limit = Math.min(centers.size(), 8);
        List<FinderCenter[]> triples = new ArrayList<>();
        for (int i = 0; i < limit; i++) {
            for (int j = i + 1; j < limit; j++) {
                for (int k = j + 1; k < limit; k++) {
                    FinderCenter[] ordered = orientThreeFinders(centers.get(i), centers.get(j), centers.get(k));
                    if (ordered != null) triples.add(ordered);
                }
            }
        }
        if (triples.isEmpty()) return null;

        triples.sort(Comparator.comparingDouble(QrDecoder::scoreTriple));

        int maxTriples = Math.min(triples.size(), 6);
        for (int t = 0; t < maxTriples; t++) {
            FinderCenter[] f = triples.get(t);
            String decoded = tryDecodeFromOrderedFinders(bin, w, h, f[0], f[1], f[2]);
            if (decoded != null) return decoded;
        }
        return null;
    }

    private static List<FinderCenter> findFinderPatterns(boolean[] bin, int w, int h) {
        List<FinderCenter> list = new ArrayList<>();
        int rowStep = Math.max(1, Math.min(w, h) / 260);
        int[] sc = new int[5];

        for (int y = 0; y < h; y += rowStep) {
            sc[0] = sc[1] = sc[2] = sc[3] = sc[4] = 0;
            int state = 0;
            int row = y * w;
            for (int x = 0; x < w; x++) {
                if (bin[row + x]) {
                    if ((state & 1) == 1) state++;
                    sc[state]++;
                } else {
                    if ((state & 1) == 0) {
                        if (state == 4) {
                            if (foundRatio(sc)) {
                                handlePossibleFinder(bin, w, h, sc, x, y, list);
                            }
                            sc[0] = sc[2];
                            sc[1] = sc[3];
                            sc[2] = sc[4];
                            sc[3] = 1;
                            sc[4] = 0;
                            state = 3;
                        } else if (state > 0 || sc[0] > 0) {
                            state++;
                            sc[state]++;
                        }
                    } else {
                        sc[state]++;
                    }
                }
            }
        if (foundRatio(sc)) {
            handlePossibleFinder(bin, w, h, sc, w, y, list);
        }
        }

        Collections.sort(list, (a, b) -> Integer.compare(b.count, a.count));
        return list;
    }

    private static boolean foundRatio(int[] sc) {
        int total = sc[0] + sc[1] + sc[2] + sc[3] + sc[4];
        if (total < 7) return false;
        double mod = total / 7.0;
        double tol = mod * 0.55;
        return Math.abs(mod - sc[0]) < tol &&
               Math.abs(mod - sc[1]) < tol &&
               Math.abs(3.0 * mod - sc[2]) < 3.0 * tol &&
               Math.abs(mod - sc[3]) < tol &&
               Math.abs(mod - sc[4]) < tol;
    }

    private static double centerFromEnd(int[] sc, int end) {
        return (end - sc[4] - sc[3]) - sc[2] * 0.5;
    }

    private static void handlePossibleFinder(boolean[] bin, int w, int h, int[] sc, int endX, int y, List<FinderCenter> list) {
        double cx = centerFromEnd(sc, endX);
        int total = sc[0] + sc[1] + sc[2] + sc[3] + sc[4];
        int ix = (int) Math.round(cx);
        if (ix < 0 || ix >= w) return;

        double cy = crossCheckVertical(bin, w, h, y, ix, sc[2], total);
        if (Double.isNaN(cy)) return;
        int iy = (int) Math.round(cy);
        if (iy < 0 || iy >= h) return;

        double cx2 = crossCheckHorizontal(bin, w, h, iy, ix, sc[2], total);
        if (Double.isNaN(cx2)) return;

        if (!crossCheckDiagonal(bin, w, h, iy, (int) Math.round(cx2))) return;

        double estMod = total / 7.0;
        for (int i = 0; i < list.size(); i++) {
            FinderCenter existing = list.get(i);
            double dx = Math.abs(existing.x - cx2);
            double dy = Math.abs(existing.y - cy);
            double dist = Math.hypot(dx, dy);
            if (dist <= Math.max(10.0, estMod * 2.5) && Math.abs(estMod - existing.moduleSize) <= Math.max(1.5, existing.moduleSize)) {
                int c = existing.count + 1;
                FinderCenter merged = new FinderCenter(
                        (existing.x * existing.count + cx2) / c,
                        (existing.y * existing.count + cy) / c,
                        (existing.moduleSize * existing.count + estMod) / c,
                        c);
                list.set(i, merged);
                return;
            }
        }
        list.add(new FinderCenter(cx2, cy, estMod, 1));
    }

    private static double crossCheckVertical(boolean[] bin, int w, int h, int startY, int cx, int maxCenter, int origTotal) {
        int[] sc = new int[5];
        int y = startY;
        while (y >= 0 && bin[y * w + cx]) { sc[2]++; y--; }
        if (y < 0) return Double.NaN;
        while (y >= 0 && !bin[y * w + cx] && sc[1] <= maxCenter) { sc[1]++; y--; }
        if (y < 0 || sc[1] > maxCenter) return Double.NaN;
        while (y >= 0 && bin[y * w + cx] && sc[0] <= maxCenter) { sc[0]++; y--; }
        if (sc[0] > maxCenter) return Double.NaN;

        y = startY + 1;
        while (y < h && bin[y * w + cx]) { sc[2]++; y++; }
        if (y >= h) return Double.NaN;
        while (y < h && !bin[y * w + cx] && sc[3] <= maxCenter) { sc[3]++; y++; }
        if (y >= h || sc[3] > maxCenter) return Double.NaN;
        while (y < h && bin[y * w + cx] && sc[4] <= maxCenter) { sc[4]++; y++; }
        if (sc[4] > maxCenter) return Double.NaN;

        int total = sc[0] + sc[1] + sc[2] + sc[3] + sc[4];
        if (5 * Math.abs(total - origTotal) >= 2 * origTotal) return Double.NaN;
        return foundRatio(sc) ? centerFromEnd(sc, y) : Double.NaN;
    }

    private static double crossCheckHorizontal(boolean[] bin, int w, int h, int cy, int startX, int maxCenter, int origTotal) {
        int[] sc = new int[5];
        int row = cy * w;
        int x = startX;
        while (x >= 0 && bin[row + x]) { sc[2]++; x--; }
        if (x < 0) return Double.NaN;
        while (x >= 0 && !bin[row + x] && sc[1] <= maxCenter) { sc[1]++; x--; }
        if (x < 0 || sc[1] > maxCenter) return Double.NaN;
        while (x >= 0 && bin[row + x] && sc[0] <= maxCenter) { sc[0]++; x--; }
        if (sc[0] > maxCenter) return Double.NaN;

        x = startX + 1;
        while (x < w && bin[row + x]) { sc[2]++; x++; }
        if (x >= w) return Double.NaN;
        while (x < w && !bin[row + x] && sc[3] <= maxCenter) { sc[3]++; x++; }
        if (x >= w || sc[3] > maxCenter) return Double.NaN;
        while (x < w && bin[row + x] && sc[4] <= maxCenter) { sc[4]++; x++; }
        if (sc[4] > maxCenter) return Double.NaN;

        int total = sc[0] + sc[1] + sc[2] + sc[3] + sc[4];
        if (5 * Math.abs(total - origTotal) >= 2 * origTotal) return Double.NaN;
        return foundRatio(sc) ? centerFromEnd(sc, x) : Double.NaN;
    }

    private static boolean foundRatioRelaxed(int[] sc) {
        int total = sc[0] + sc[1] + sc[2] + sc[3] + sc[4];
        if (total < 7) return false;
        double mod = total / 7.0;
        double tol = mod * 0.78;
        return Math.abs(mod - sc[0]) < tol &&
               Math.abs(mod - sc[1]) < tol &&
               Math.abs(3.0 * mod - sc[2]) < 3.0 * tol &&
               Math.abs(mod - sc[3]) < tol &&
               Math.abs(mod - sc[4]) < tol;
    }

    private static boolean crossCheckDiagonal(boolean[] bin, int w, int h, int cy, int cx) {
        int[] sc = new int[5];
        int i = 0;
        while (cy - i >= 0 && cx - i >= 0 && bin[(cy - i) * w + (cx - i)]) { sc[2]++; i++; }
        if (cy - i < 0 || cx - i < 0) return false;
        while (cy - i >= 0 && cx - i >= 0 && !bin[(cy - i) * w + (cx - i)]) { sc[1]++; i++; }
        if (cy - i < 0 || cx - i < 0) return false;
        while (cy - i >= 0 && cx - i >= 0 && bin[(cy - i) * w + (cx - i)]) { sc[0]++; i++; }

        i = 1;
        while (cy + i < h && cx + i < w && bin[(cy + i) * w + (cx + i)]) { sc[2]++; i++; }
        if (cy + i >= h || cx + i >= w) return false;
        while (cy + i < h && cx + i < w && !bin[(cy + i) * w + (cx + i)]) { sc[3]++; i++; }
        if (cy + i >= h || cx + i >= w) return false;
        while (cy + i < h && cx + i < w && bin[(cy + i) * w + (cx + i)]) { sc[4]++; i++; }

        return foundRatioRelaxed(sc);
    }

    private static FinderCenter[] orientThreeFinders(FinderCenter a, FinderCenter b, FinderCenter c) {
        double dab = dist(a, b);
        double dbc = dist(b, c);
        double dac = dist(a, c);
        if (dab < 8 || dbc < 8 || dac < 8) return null;

        FinderCenter tl, p1, p2;
        if (dbc >= dab && dbc >= dac) {
            tl = a; p1 = b; p2 = c;
        } else if (dac >= dab && dac >= dbc) {
            tl = b; p1 = a; p2 = c;
        } else {
            tl = c; p1 = a; p2 = b;
        }

        double d1 = dist(tl, p1);
        double d2 = dist(tl, p2);
        double ratio = Math.max(d1, d2) / Math.max(1.0, Math.min(d1, d2));
        if (ratio > 1.55) return null;

        double vx1 = p1.x - tl.x, vy1 = p1.y - tl.y;
        double vx2 = p2.x - tl.x, vy2 = p2.y - tl.y;
        double cos = Math.abs((vx1 * vx2 + vy1 * vy2) / (d1 * d2));
        if (cos > 0.38) return null;

        // Cross product (TR - TL) x (BL - TL) > 0 in screen coords (+x right, +y down)
        double cross = vx1 * vy2 - vy1 * vx2;
        FinderCenter tr = cross > 0 ? p1 : p2;
        FinderCenter bl = cross > 0 ? p2 : p1;
        return new FinderCenter[]{tl, tr, bl};
    }

    private static double scoreTriple(FinderCenter[] f) {
        double d1 = dist(f[0], f[1]);
        double d2 = dist(f[0], f[2]);
        double ratioDiff = Math.abs(d1 - d2) / Math.max(1.0, Math.min(d1, d2));
        double vx1 = f[1].x - f[0].x, vy1 = f[1].y - f[0].y;
        double vx2 = f[2].x - f[0].x, vy2 = f[2].y - f[0].y;
        double cos = Math.abs((vx1 * vx2 + vy1 * vy2) / Math.max(1.0, d1 * d2));
        double countBonus = -(f[0].count + f[1].count + f[2].count) * 0.01;
        return ratioDiff + cos + countBonus;
    }

    private static double dist(FinderCenter a, FinderCenter b) {
        return Math.hypot(a.x - b.x, a.y - b.y);
    }

    private static String tryDecodeFromOrderedFinders(boolean[] bin, int w, int h,
                                                      FinderCenter tl, FinderCenter tr, FinderCenter bl) {
        double modSize = (tl.moduleSize + tr.moduleSize + bl.moduleSize) / 3.0;
        if (modSize < 1.0) return null;

        double estDim = ((dist(tl, tr) + dist(tl, bl)) / (2.0 * modSize)) + 7.0;
        int snapped = ((int) Math.round((estDim - 17.0) / 4.0)) * 4 + 17;
        snapped = Math.max(21, Math.min(17 + 4 * QrVersionTables.MAX_VERSION, snapped));

        int[] dimCandidates = {snapped, snapped - 4, snapped + 4};
        for (int dim : dimCandidates) {
            if (dim < 21 || dim > 17 + 4 * QrVersionTables.MAX_VERSION) continue;
            int version = (dim - 17) / 4;

            double alignSrcX = dim - 3.5;
            double alignSrcY = dim - 3.5;
            double alignDstX = tr.x - tl.x + bl.x;
            double alignDstY = tr.y - tl.y + bl.y;

            if (version >= 2) {
                double scale = (dim - 10.0) / (dim - 7.0);
                double predAx = tl.x + ((tr.x - tl.x) + (bl.x - tl.x)) * scale;
                double predAy = tl.y + ((tr.y - tl.y) + (bl.y - tl.y)) * scale;
                double[] foundAlign = findAlignmentPattern(bin, w, h, predAx, predAy, modSize);
                if (foundAlign != null) {
                    double[] H = solveHomography(
                            3.5, 3.5, tl.x, tl.y,
                            dim - 3.5, 3.5, tr.x, tr.y,
                            3.5, dim - 3.5, bl.x, bl.y,
                            dim - 6.5, dim - 6.5, foundAlign[0], foundAlign[1]);
                    if (H != null) {
                        boolean[][] modules = sampleGrid(bin, w, h, dim, H);
                        String res = decodeMatrix(modules);
                        if (res != null) return res;
                    }
                }
            }

            double[] H = solveHomography(
                    3.5, 3.5, tl.x, tl.y,
                    dim - 3.5, 3.5, tr.x, tr.y,
                    3.5, dim - 3.5, bl.x, bl.y,
                    alignSrcX, alignSrcY, alignDstX, alignDstY);
            if (H == null) continue;

            boolean[][] modules = sampleGrid(bin, w, h, dim, H);
            String res = decodeMatrix(modules);
            if (res != null) return res;
        }
        return null;
    }

    private static double[] findAlignmentPattern(boolean[] bin, int w, int h,
                                                 double predX, double predY, double modSize) {
        int radius = Math.max(8, (int) Math.ceil(modSize * 4.5));
        int minX = Math.max(0, (int) Math.floor(predX - radius));
        int maxX = Math.min(w - 1, (int) Math.ceil(predX + radius));
        int minY = Math.max(0, (int) Math.floor(predY - radius));
        int maxY = Math.min(h - 1, (int) Math.ceil(predY + radius));

        double bestDist = Double.MAX_VALUE;
        double[] best = null;
        int[] sc = new int[3]; // [white, black_center, white]

        for (int y = minY; y <= maxY; y++) {
            sc[0] = sc[1] = sc[2] = 0;
            int state = 0;
            int row = y * w;
            for (int x = minX; x <= maxX; x++) {
                if (!bin[row + x]) { // white
                    if (state == 1) state = 2;
                    sc[state]++;
                } else { // black
                    if (state == 0) {
                        if (sc[0] > 0) {
                            state = 1;
                            sc[1] = 1;
                        }
                    } else if (state == 2) {
                        if (alignRatio(sc, modSize)) {
                            double cx = x - sc[2] - sc[1] * 0.5;
                            int leftRingX = x - sc[2] - sc[1] - sc[0] - 1;
                            if (leftRingX >= 0 && bin[row + leftRingX]) {
                                double cy = crossCheckAlignVertical(bin, w, h, y, (int) Math.round(cx), modSize);
                                if (!Double.isNaN(cy)) {
                                    double d = Math.hypot(cx - predX, cy - predY);
                                    if (d < bestDist) {
                                        bestDist = d;
                                        best = new double[]{cx, cy};
                                    }
                                }
                            }
                        }
                        sc[0] = sc[2];
                        sc[1] = 1;
                        sc[2] = 0;
                        state = 1;
                    } else {
                        sc[1]++;
                    }
                }
            }
        }
        return best;
    }

    private static boolean alignRatio(int[] sc, double modSize) {
        int total = sc[0] + sc[1] + sc[2];
        if (total < 3) return false;
        double est = total / 3.0;
        if (Math.abs(est - modSize) > modSize * 0.6) return false;
        double tol = est * 0.6;
        return Math.abs(est - sc[0]) < tol && Math.abs(est - sc[1]) < tol && Math.abs(est - sc[2]) < tol;
    }

    private static double crossCheckAlignVertical(boolean[] bin, int w, int h, int startY, int cx, double modSize) {
        if (cx < 0 || cx >= w) return Double.NaN;
        int maxRun = (int) Math.ceil(modSize * 2.5);
        int[] sc = new int[3]; // [top_white, black_center, bottom_white]
        int y = startY;
        while (y >= 0 && bin[y * w + cx] && sc[1] <= maxRun) { sc[1]++; y--; }
        if (y < 0 || sc[1] > maxRun) return Double.NaN;
        while (y >= 0 && !bin[y * w + cx] && sc[0] <= maxRun) { sc[0]++; y--; }
        if (y < 0 || sc[0] > maxRun || !bin[y * w + cx]) return Double.NaN;

        y = startY + 1;
        while (y < h && bin[y * w + cx] && sc[1] <= maxRun) { sc[1]++; y++; }
        if (y >= h || sc[1] > maxRun) return Double.NaN;
        while (y < h && !bin[y * w + cx] && sc[2] <= maxRun) { sc[2]++; y++; }
        if (y >= h || sc[2] > maxRun || !bin[y * w + cx]) return Double.NaN;

        if (!alignRatio(sc, modSize)) return Double.NaN;
        return (y - sc[2]) - sc[1] * 0.5;
    }

    private static double[] solveHomography(
            double u0, double v0, double x0, double y0,
            double u1, double v1, double x1, double y1,
            double u2, double v2, double x2, double y2,
            double u3, double v3, double x3, double y3) {
        double[][] a = {
                {u0, v0, 1, 0, 0, 0, -u0 * x0, -v0 * x0, x0},
                {0, 0, 0, u0, v0, 1, -u0 * y0, -v0 * y0, y0},
                {u1, v1, 1, 0, 0, 0, -u1 * x1, -v1 * x1, x1},
                {0, 0, 0, u1, v1, 1, -u1 * y1, -v1 * y1, y1},
                {u2, v2, 1, 0, 0, 0, -u2 * x2, -v2 * x2, x2},
                {0, 0, 0, u2, v2, 1, -u2 * y2, -v2 * y2, y2},
                {u3, v3, 1, 0, 0, 0, -u3 * x3, -v3 * x3, x3},
                {0, 0, 0, u3, v3, 1, -u3 * y3, -v3 * y3, y3}
        };

        for (int col = 0; col < 8; col++) {
            int piv = col;
            double maxVal = Math.abs(a[col][col]);
            for (int row = col + 1; row < 8; row++) {
                double val = Math.abs(a[row][col]);
                if (val > maxVal) {
                    maxVal = val;
                    piv = row;
                }
            }
            if (maxVal < 1e-9) return null;
            if (piv != col) {
                double[] tmp = a[col];
                a[col] = a[piv];
                a[piv] = tmp;
            }
            double inv = 1.0 / a[col][col];
            for (int j = col; j < 9; j++) a[col][j] *= inv;
            for (int row = 0; row < 8; row++) {
                if (row == col) continue;
                double f = a[row][col];
                if (f == 0) continue;
                for (int j = col; j < 9; j++) a[row][j] -= f * a[col][j];
            }
        }
        double[] h = new double[8];
        for (int i = 0; i < 8; i++) h[i] = a[i][8];
        return h;
    }

    private static boolean[][] sampleGrid(boolean[] bin, int w, int h, int dim, double[] H) {
        boolean[][] modules = new boolean[dim][dim];
        for (int r = 0; r < dim; r++) {
            double v = r + 0.5;
            for (int c = 0; c < dim; c++) {
                double u = c + 0.5;
                double denom = H[6] * u + H[7] * v + 1.0;
                if (Math.abs(denom) < 1e-6) continue;
                int x = (int) Math.round((H[0] * u + H[1] * v + H[2]) / denom);
                int y = (int) Math.round((H[3] * u + H[4] * v + H[5]) / denom);
                if (x >= 0 && x < w && y >= 0 && y < h) {
                    modules[r][c] = bin[y * w + x];
                }
            }
        }
        return modules;
    }

    public static String decodeMatrix(boolean[][] modules) {
        if (modules == null || modules.length < 21 || modules.length != modules[0].length) {
            return null;
        }
        int size = modules.length;
        if ((size - 17) % 4 != 0) return null;
        int version = (size - 17) / 4;
        if (version < 1 || version > QrVersionTables.MAX_VERSION) return null;

        if (version >= 7) {
            int v1 = 0, v2 = 0;
            for (int i = 0; i < 18; i++) {
                int a = size - 11 + (i % 3);
                int b = i / 3;
                if (modules[b][a]) v1 |= (1 << i);
                if (modules[a][b]) v2 |= (1 << i);
            }
            int dv = QrVersionTables.decodeVersionInfo(v1);
            if (dv < 0) dv = QrVersionTables.decodeVersionInfo(v2);
            if (dv >= 7 && dv != version) return null;
        }

        // Read format info from primary (TL) and backup (BL/TR) strips
        int fmt1 = 0;
        for (int i = 0; i <= 5; i++) if (modules[i][8]) fmt1 |= (1 << i);
        if (modules[7][8]) fmt1 |= (1 << 6);
        if (modules[8][8]) fmt1 |= (1 << 7);
        if (modules[8][7]) fmt1 |= (1 << 8);
        for (int i = 9; i < 15; i++) if (modules[8][14 - i]) fmt1 |= (1 << i);

        int fmt2 = 0;
        for (int i = 0; i < 8; i++) if (modules[8][size - 1 - i]) fmt2 |= (1 << i);
        for (int i = 8; i < 15; i++) if (modules[size - 15 + i][8]) fmt2 |= (1 << i);

        int formatData = QrVersionTables.decodeFormatInfo(fmt1);
        if (formatData < 0) formatData = QrVersionTables.decodeFormatInfo(fmt2);
        if (formatData < 0) return null;

        int ecLevel = (formatData >> 3) & 3;
        int maskId = formatData & 7;

        boolean[][] isFunction = buildFunctionMask(version, size);
        int totalCw = QrVersionTables.totalCodewords(ecLevel, version);
        if (totalCw <= 0) return null;
        int[] rawCodewords = new int[totalCw];
        int cwIdx = 0;
        int bitAccum = 0;
        int bitCount = 0;

        for (int right = size - 1; right >= 1; right -= 2) {
            if (right == 6) right = 5;
            for (int vert = 0; vert < size; vert++) {
                boolean upward = ((right + 1) & 2) == 0;
                int row = upward ? (size - 1 - vert) : vert;
                for (int j = 0; j < 2; j++) {
                    int col = right - j;
                    if (isFunction[row][col]) continue;
                    boolean bit = modules[row][col];
                    if (maskBit(maskId, row, col)) bit = !bit;
                    bitAccum = (bitAccum << 1) | (bit ? 1 : 0);
                    bitCount++;
                    if (bitCount == 8) {
                        if (cwIdx < totalCw) rawCodewords[cwIdx++] = bitAccum;
                        bitAccum = 0;
                        bitCount = 0;
                    }
                }
            }
        }
        if (cwIdx != totalCw) return null;

        int[] spec = QrVersionTables.getEccSpec(ecLevel, version);
        if (spec == null) return null;
        int ecPerBlock = spec[0];
        int g1 = spec[1], d1 = spec[2];
        int g2 = spec[3], d2 = spec[4];
        int totalBlocks = g1 + g2;

        int[][] blocks = new int[totalBlocks][];
        for (int i = 0; i < g1; i++) blocks[i] = new int[d1 + ecPerBlock];
        for (int i = 0; i < g2; i++) blocks[g1 + i] = new int[d2 + ecPerBlock];

        int src = 0;
        int maxData = Math.max(d1, d2);
        for (int i = 0; i < maxData; i++) {
            for (int b = 0; b < totalBlocks; b++) {
                int dataLen = (b < g1) ? d1 : d2;
                if (i < dataLen) {
                    blocks[b][i] = rawCodewords[src++];
                }
            }
        }
        for (int i = 0; i < ecPerBlock; i++) {
            for (int b = 0; b < totalBlocks; b++) {
                int dataLen = (b < g1) ? d1 : d2;
                blocks[b][dataLen + i] = rawCodewords[src++];
            }
        }

        byte[] dataBytes = new byte[g1 * d1 + g2 * d2];
        int dst = 0;
        for (int b = 0; b < totalBlocks; b++) {
            if (!QrReedSolomon.decode(blocks[b], ecPerBlock)) return null;
            int dataLen = (b < g1) ? d1 : d2;
            for (int i = 0; i < dataLen; i++) {
                dataBytes[dst++] = (byte) blocks[b][i];
            }
        }

        return decodeDataBitStream(dataBytes, version);
    }

    private static boolean[][] buildFunctionMask(int version, int size) {
        boolean[][] fn = new boolean[size][size];
        markSquare(fn, 0, 0, 9, 9, size);
        markSquare(fn, 0, size - 8, 9, 8, size);
        markSquare(fn, size - 8, 0, 8, 9, size);

        for (int i = 8; i < size - 8; i++) {
            fn[6][i] = true;
            fn[i][6] = true;
        }

        int[] centers = QrVersionTables.getAlignmentCenters(version);
        for (int ar : centers) {
            for (int ac : centers) {
                boolean overlaps = (ar <= 8 && ac <= 8) ||
                                   (ar <= 8 && ac >= size - 9) ||
                                   (ar >= size - 9 && ac <= 8);
                if (overlaps) continue;
                for (int dr = -2; dr <= 2; dr++) {
                    for (int dc = -2; dc <= 2; dc++) {
                        fn[ar + dr][ac + dc] = true;
                    }
                }
            }
        }

        if (version >= 7) {
            for (int i = 0; i < 6; i++) {
                for (int j = size - 11; j < size - 8; j++) {
                    fn[i][j] = true;
                    fn[j][i] = true;
                }
            }
        }
        return fn;
    }

    private static void markSquare(boolean[][] fn, int r0, int c0, int rows, int cols, int size) {
        for (int r = r0; r < r0 + rows; r++) {
            if (r < 0 || r >= size) continue;
            for (int c = c0; c < c0 + cols; c++) {
                if (c < 0 || c >= size) continue;
                fn[r][c] = true;
            }
        }
    }

    private static boolean maskBit(int mask, int row, int col) {
        switch (mask) {
            case 0: return ((row + col) & 1) == 0;
            case 1: return (row & 1) == 0;
            case 2: return (col % 3) == 0;
            case 3: return ((row + col) % 3) == 0;
            case 4: return (((row / 2) + (col / 3)) & 1) == 0;
            case 5: return ((row * col) % 2) + ((row * col) % 3) == 0;
            case 6: return ((((row * col) % 2) + ((row * col) % 3)) & 1) == 0;
            default: return ((((row + col) % 2) + ((row * col) % 3)) & 1) == 0;
        }
    }

    private static final class BitReader {
        private final byte[] bytes;
        private int byteOff = 0;
        private int bitOff = 0;

        BitReader(byte[] bytes) {
            this.bytes = bytes;
        }

        int available() {
            return (bytes.length - byteOff) * 8 - bitOff;
        }

        int readBits(int n) {
            if (n <= 0 || n > 32 || n > available()) return -1;
            int res = 0;
            for (int i = 0; i < n; i++) {
                int b = (bytes[byteOff] >> (7 - bitOff)) & 1;
                res = (res << 1) | b;
                bitOff++;
                if (bitOff == 8) {
                    bitOff = 0;
                    byteOff++;
                }
            }
            return res;
        }
    }

    private static String decodeDataBitStream(byte[] dataBytes, int version) {
        BitReader br = new BitReader(dataBytes);
        StringBuilder sb = new StringBuilder();
        Charset currentCharset = StandardCharsets.UTF_8;

        while (br.available() >= 4) {
            int mode = br.readBits(4);
            if (mode == 0) {
                break; // Terminator
            } else if (mode == 7) { // ECI
                int first = br.readBits(8);
                if (first < 0) return null;
                int eciValue;
                if ((first & 0x80) == 0) {
                    eciValue = first & 0x7F;
                } else if ((first & 0xC0) == 0x80) {
                    int second = br.readBits(8);
                    if (second < 0) return null;
                    eciValue = ((first & 0x3F) << 8) | second;
                } else {
                    int rest = br.readBits(16);
                    if (rest < 0) return null;
                    eciValue = ((first & 0x1F) << 16) | rest;
                }
                if (eciValue == 26) currentCharset = StandardCharsets.UTF_8;
                else if (eciValue == 3) currentCharset = StandardCharsets.ISO_8859_1;
            } else if (mode == 4) { // Byte mode
                int countBits = version <= 9 ? 8 : 16;
                int count = br.readBits(countBits);
                if (count < 0 || count * 8 > br.available()) return null;
                ByteArrayOutputStream bos = new ByteArrayOutputStream(count);
                for (int i = 0; i < count; i++) {
                    int b = br.readBits(8);
                    if (b < 0) return null;
                    bos.write(b);
                }
                sb.append(new String(bos.toByteArray(), currentCharset));
            } else if (mode == 2) { // Alphanumeric mode
                int countBits = version <= 9 ? 9 : 11;
                int count = br.readBits(countBits);
                if (count < 0) return null;
                while (count >= 2) {
                    int val = br.readBits(11);
                    if (val < 0 || val / 45 >= 45) return null;
                    sb.append(ALPHANUMERIC_TABLE.charAt(val / 45));
                    sb.append(ALPHANUMERIC_TABLE.charAt(val % 45));
                    count -= 2;
                }
                if (count == 1) {
                    int val = br.readBits(6);
                    if (val < 0 || val >= 45) return null;
                    sb.append(ALPHANUMERIC_TABLE.charAt(val));
                }
            } else if (mode == 1) { // Numeric mode
                int countBits = version <= 9 ? 10 : 12;
                int count = br.readBits(countBits);
                if (count < 0) return null;
                while (count >= 3) {
                    int val = br.readBits(10);
                    if (val < 0 || val >= 1000) return null;
                    sb.append((char) ('0' + val / 100));
                    sb.append((char) ('0' + (val / 10) % 10));
                    sb.append((char) ('0' + val % 10));
                    count -= 3;
                }
                if (count == 2) {
                    int val = br.readBits(7);
                    if (val < 0 || val >= 100) return null;
                    sb.append((char) ('0' + val / 10));
                    sb.append((char) ('0' + val % 10));
                } else if (count == 1) {
                    int val = br.readBits(4);
                    if (val < 0 || val >= 10) return null;
                    sb.append((char) ('0' + val));
                }
            } else {
                return null;
            }
        }
        return sb.toString();
    }
}
