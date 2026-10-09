package com.orbitnav.navigator;

import android.content.Context;
import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import android.util.AtomicFile;
import com.orbitnav.navigator.core.BrowserState;
import java.io.File;
import java.io.FileOutputStream;
import java.io.FileInputStream;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.nio.ByteBuffer;
import java.nio.charset.StandardCharsets;
import java.security.GeneralSecurityException;
import java.security.KeyStore;
import java.util.Arrays;
import javax.crypto.Cipher;
import javax.crypto.KeyGenerator;
import javax.crypto.SecretKey;
import javax.crypto.spec.GCMParameterSpec;

/** Uncomposed native seam. No token or key is provisioned until a future reviewed account flow calls it. */
final class KeystoreSecretStore {
    enum Purpose { ACCOUNT_LINK, SYNC_AUTHORIZATION, SYNC_KEYSET }
    private static final String ALIAS = "orbit-navigator.local-wrapping.v1";
    private static final int MAX_SECRET = 64 * 1024;
    private final Context context;
    KeystoreSecretStore(Context context) { this.context = context.getApplicationContext(); }

    void put(BrowserState.Mode mode, Purpose purpose, String profileId, byte[] secret)
            throws GeneralSecurityException, IOException {
        requireNormal(mode);
        if (secret.length == 0 || secret.length > MAX_SECRET) throw new IllegalArgumentException("Secret size rejected.");
        byte[] aad = aad(purpose, profileId);
        Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
        cipher.init(Cipher.ENCRYPT_MODE, getKey(true));
        cipher.updateAAD(aad);
        byte[] encrypted = cipher.doFinal(secret);
        byte[] frame = ByteBuffer.allocate(1 + 12 + encrypted.length).put((byte) 1)
                .put(cipher.getIV()).put(encrypted).array();
        AtomicFile file = file(purpose, profileId);
        FileOutputStream output = null;
        try { output = file.startWrite(); output.write(frame); file.finishWrite(output); }
        catch (IOException ex) { if (output != null) file.failWrite(output); throw ex; }
        finally { Arrays.fill(encrypted, (byte) 0); Arrays.fill(frame, (byte) 0); }
    }

    byte[] get(BrowserState.Mode mode, Purpose purpose, String profileId)
            throws GeneralSecurityException, IOException {
        requireNormal(mode);
        byte[] aad = aad(purpose, profileId);
        AtomicFile file = file(purpose, profileId);
        byte[] frame;
        // Bound the actual stream, including AtomicFile's backup-recovery path.
        try (FileInputStream input = file.openRead(); ByteArrayOutputStream bytes = new ByteArrayOutputStream()) {
            byte[] buffer = new byte[4096]; int count;
            while ((count = input.read(buffer)) != -1) {
                if (bytes.size() + count > MAX_SECRET + 29) throw new IOException("Secret size rejected.");
                bytes.write(buffer, 0, count);
            }
            frame = bytes.toByteArray();
        }
        try {
            if (frame.length < 30 || frame.length > MAX_SECRET + 29 || frame[0] != 1)
                throw new IOException("Protected secret format rejected.");
            Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
            cipher.init(Cipher.DECRYPT_MODE, getKey(false), new GCMParameterSpec(128, frame, 1, 12));
            cipher.updateAAD(aad);
            return cipher.doFinal(frame, 13, frame.length - 13);
        } finally { Arrays.fill(frame, (byte) 0); }
    }

    void delete(BrowserState.Mode mode, Purpose purpose, String profileId) {
        requireNormal(mode); aad(purpose, profileId); file(purpose, profileId).delete();
    }
    private AtomicFile file(Purpose purpose, String profileId) {
        return new AtomicFile(new File(context.getNoBackupFilesDir(), "secret-" + profileId + "-" + purpose.name() + ".v1"));
    }
    private SecretKey getKey(boolean create) throws GeneralSecurityException, IOException {
        KeyStore store = KeyStore.getInstance("AndroidKeyStore"); store.load(null);
        if (store.containsAlias(ALIAS)) return (SecretKey) store.getKey(ALIAS, null);
        if (!create) throw new GeneralSecurityException("Local wrapping key is unavailable; relink/recover explicitly.");
        KeyGenerator generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore");
        generator.init(new KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_ENCRYPT | KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256).setRandomizedEncryptionRequired(true).build());
        return generator.generateKey();
    }
    private static byte[] aad(Purpose purpose, String profileId) {
        if (purpose == null || profileId == null || !profileId.matches("[a-z0-9-]{1,64}"))
            throw new IllegalArgumentException("Profile/purpose required.");
        return ("orbit.android.local-secret.v1|" + purpose.name() + "|" + profileId).getBytes(StandardCharsets.UTF_8);
    }
    private static void requireNormal(BrowserState.Mode mode) {
        if (mode != BrowserState.Mode.NORMAL) throw new SecurityException("Private secret access denied before platform access.");
    }
}
