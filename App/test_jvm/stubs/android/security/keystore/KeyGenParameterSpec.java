package android.security.keystore;

import java.security.spec.AlgorithmParameterSpec;

public final class KeyGenParameterSpec implements AlgorithmParameterSpec {
    private final String alias;
    private final int keySize;

    private KeyGenParameterSpec(String alias, int keySize) {
        this.alias = alias;
        this.keySize = keySize;
    }

    public String getKeystoreAlias() {
        return alias;
    }

    public int getKeySize() {
        return keySize;
    }

    public static final class Builder {
        private final String alias;
        private int keySize = 256;

        public Builder(String alias, int purposes) {
            this.alias = alias;
        }

        public Builder setBlockModes(String... modes) { return this; }
        public Builder setEncryptionPaddings(String... paddings) { return this; }
        public Builder setKeySize(int size) { this.keySize = size; return this; }

        public KeyGenParameterSpec build() {
            return new KeyGenParameterSpec(alias, keySize);
        }
    }
}
