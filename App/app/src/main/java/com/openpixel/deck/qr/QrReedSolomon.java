package com.openpixel.deck.qr;

/**
 * Pure-Java GF(256) (primitive polynomial 0x11D) Reed-Solomon decoder
 * using Berlekamp-Massey, Chien search, and Forney's algorithm.
 */
public final class QrReedSolomon {
    private static final int[] EXP = new int[512];
    private static final int[] LOG = new int[256];

    static {
        int x = 1;
        for (int i = 0; i < 255; i++) {
            EXP[i] = x;
            LOG[x] = i;
            x <<= 1;
            if ((x & 0x100) != 0) {
                x ^= 0x11D;
            }
        }
        for (int i = 255; i < EXP.length; i++) {
            EXP[i] = EXP[i - 255];
        }
    }

    private QrReedSolomon() {}

    public static int gfMul(int a, int b) {
        if (a == 0 || b == 0) return 0;
        return EXP[LOG[a] + LOG[b]];
    }

    public static int gfDiv(int a, int b) {
        if (b == 0) throw new ArithmeticException("GF division by zero");
        if (a == 0) return 0;
        int diff = LOG[a] - LOG[b];
        if (diff < 0) diff += 255;
        return EXP[diff];
    }

    public static int gfPow(int base, int exp) {
        if (exp == 0) return 1;
        if (base == 0) return 0;
        int e = (LOG[base] * exp) % 255;
        if (e < 0) e += 255;
        return EXP[e];
    }

    /**
     * Computes RS error correction bytes (highest-degree codeword first, matching ISO 18004).
     */
    public static int[] computeEc(int[] data, int ecCount) {
        int[] gen = new int[ecCount + 1];
        gen[0] = 1;
        for (int i = 0; i < ecCount; i++) {
            int factor = EXP[i];
            for (int j = i + 1; j >= 1; j--) {
                gen[j] ^= gfMul(gen[j - 1], factor);
            }
        }
        int[] rem = new int[ecCount];
        for (int b : data) {
            int factor = (b & 0xFF) ^ rem[0];
            System.arraycopy(rem, 1, rem, 0, ecCount - 1);
            rem[ecCount - 1] = 0;
            for (int i = 0; i < ecCount; i++) {
                rem[i] ^= gfMul(gen[i + 1], factor);
            }
        }
        return rem;
    }

    /**
     * Decodes and corrects {@code received} in-place (data + ecCount bytes, MSB first).
     * Returns true if zero errors or all errors were successfully corrected.
     */
    public static boolean decode(int[] received, int ecCount) {
        if (received == null || ecCount <= 0 || received.length <= ecCount || received.length > 255) {
            return false;
        }
        int n = received.length;
        int[] syndromes = new int[ecCount];
        boolean hasError = false;
        for (int k = 0; k < ecCount; k++) {
            int alpha = EXP[k];
            int val = 0;
            for (int i = 0; i < n; i++) {
                val = gfMul(val, alpha) ^ (received[i] & 0xFF);
            }
            syndromes[k] = val;
            if (val != 0) hasError = true;
        }
        if (!hasError) return true;

        // Berlekamp-Massey for error locator C(x) = 1 + C[1]x + ... + C[L]x^L
        int[] C = new int[ecCount + 1];
        int[] B = new int[ecCount + 1];
        C[0] = 1;
        B[0] = 1;
        int L = 0;
        int m = 1;
        int b = 1;

        for (int step = 0; step < ecCount; step++) {
            int d = syndromes[step];
            for (int i = 1; i <= L; i++) {
                d ^= gfMul(C[i], syndromes[step - i]);
            }
            if (d == 0) {
                m++;
            } else {
                int[] T = C.clone();
                int coef = gfDiv(d, b);
                for (int i = 0; i + m <= ecCount; i++) {
                    if (B[i] != 0) {
                        C[i + m] ^= gfMul(coef, B[i]);
                    }
                }
                if (2 * L <= step) {
                    L = step + 1 - L;
                    B = T;
                    b = d;
                    m = 1;
                } else {
                    m++;
                }
            }
        }

        if (L == 0 || L > ecCount / 2) return false;

        // Error evaluator polynomial Omega(x) = S(x) * C(x) mod x^ecCount (degree < L)
        int[] omega = new int[L];
        for (int k = 0; k < L; k++) {
            int sum = 0;
            for (int i = 0; i <= k; i++) {
                sum ^= gfMul(C[i], syndromes[k - i]);
            }
            omega[k] = sum;
        }

        // Chien search across all codeword positions
        int[] errPos = new int[L];
        int[] errX = new int[L];
        int[] errXInv = new int[L];
        int found = 0;

        for (int pos = 0; pos < n; pos++) {
            int power = n - 1 - pos;
            int x = EXP[power % 255];
            int xInv = EXP[(255 - (power % 255)) % 255];

            int eval = C[0];
            int xPow = 1;
            for (int i = 1; i <= L; i++) {
                xPow = gfMul(xPow, xInv);
                eval ^= gfMul(C[i], xPow);
            }
            if (eval == 0) {
                if (found >= L) return false;
                errPos[found] = pos;
                errX[found] = x;
                errXInv[found] = xInv;
                found++;
            }
        }
        if (found != L) return false;

        // Forney's algorithm (fcr = 0): e_j = X_j * Omega(X_j^-1) / C'(X_j^-1)
        for (int j = 0; j < L; j++) {
            int x = errX[j];
            int xInv = errXInv[j];

            int omVal = 0;
            int xp = 1;
            for (int i = 0; i < L; i++) {
                omVal ^= gfMul(omega[i], xp);
                xp = gfMul(xp, xInv);
            }

            int deriv = 0;
            int xInvSq = gfMul(xInv, xInv);
            xp = 1;
            for (int i = 1; i <= L; i += 2) {
                deriv ^= gfMul(C[i], xp);
                xp = gfMul(xp, xInvSq);
            }
            if (deriv == 0) return false;

            int mag = gfMul(x, gfDiv(omVal, deriv));
            received[errPos[j]] ^= mag;
        }

        // Verify all syndromes are now zero
        for (int k = 0; k < ecCount; k++) {
            int alpha = EXP[k];
            int val = 0;
            for (int i = 0; i < n; i++) {
                val = gfMul(val, alpha) ^ (received[i] & 0xFF);
            }
            if (val != 0) return false;
        }
        return true;
    }
}
