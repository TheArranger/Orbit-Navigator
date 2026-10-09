package com.orbitnav.navigator.core;

import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.util.Arrays;
import java.util.Base64;
import java.util.HexFormat;
import javax.crypto.AEADBadTagException;
import javax.crypto.Cipher;
import javax.crypto.Mac;
import javax.crypto.SecretKeyFactory;
import javax.crypto.spec.GCMParameterSpec;
import javax.crypto.spec.PBEKeySpec;
import javax.crypto.spec.SecretKeySpec;

/** TEST ONLY: independent JVM primitives against public .NET/Python fixture bytes; no network/client implementation. */
public final class InteropContractTests {
    private static int assertions;
    public static void main(String[] a) throws Exception {
        if (a[0].equals("record")) record(a);
        else if (a[0].equals("recovery")) recovery(a);
        else throw new IllegalArgumentException("Expected fixture mode.");
        System.out.println("PASS: " + assertions + " JVM sync " + a[0] + " known-answer assertions.");
    }
    private static void record(String[] a) throws Exception {
        byte[] root = hex(a[1]), expectedKey = hex(a[2]);
        String suppliedAad = a[3]; byte[] nonce = b64(a[4]), plaintext = hex(a[5]);
        byte[] encrypted = concat(b64(a[6]), b64(a[7]));
        String keyset = a[8], epoch = a[9], category = a[10];
        String cryptoCategory = category.equals("open_tabs") ? "opentabs" : "history";
        String canonical = "orbit-navigator|sync-aad|protocol=1|schema=1|profile=" + a[11]
                + "|device=" + a[12] + "|keyset=" + keyset + "|keyepoch=" + epoch
                + "|kind=" + a[13] + "|envelope=" + a[14] + "|category=" + cryptoCategory
                + "|entity=" + a[15] + "|operation=-|clientgeneration=" + a[16] + "|clientsequence=" + a[17];
        same(utf8(suppliedAad), utf8(canonical));
        byte[] salt = concat(utf8("orbit-navigator|sync-key-derivation|v1"), hex(keyset),
                ByteBuffer.allocate(8).order(ByteOrder.BIG_ENDIAN).putLong(Long.parseLong(epoch)).array());
        byte[] prk = hmac(salt, root);
        byte[] key = hmac(prk, concat(utf8("orbit-navigator|sync-category|"
                + (category.equals("open_tabs") ? "open-tabs" : "history") + "|v1"), new byte[] { 1 }));
        same(expectedKey, key);
        same(encrypted, aes(Cipher.ENCRYPT_MODE, key, nonce, utf8(canonical), plaintext));
        same(plaintext, aes(Cipher.DECRYPT_MODE, key, nonce, utf8(canonical), encrypted));
        rejected(key, nonce, utf8(canonical + "x"), encrypted);
        byte[] altered = encrypted.clone(); altered[0] ^= 1; rejected(key, nonce, utf8(canonical), altered);
        ByteBuffer payload = ByteBuffer.wrap(plaintext).order(ByteOrder.LITTLE_ENDIAN);
        boolean tombstone = a[13].equals("tombstone");
        truth(payload.getInt() == (tombstone ? 0x4F4E5354 : 0x4F4E5352));
        truth(payload.get() == 1); truth(payload.get() == (category.equals("open_tabs") ? 2 : 0));
        byte[] entity = new byte[16]; payload.get(entity); same(hex(a[15]), entity);
        if (tombstone) truth(payload.getLong() == 0);
        else {
            truth(payload.getLong() == 7); long ticks = payload.getLong(); truth(ticks > 621355968000000000L);
            truth(readText(payload).equals("https://fixture.invalid/never-upload-plaintext"));
            truth(readText(payload).equals("Fixture title Ω 🚀"));
            if (category.equals("history")) { truth(payload.getLong() == ticks); truth(payload.getInt() == 4); }
            else { truth(payload.getInt() == 3); truth(payload.get() == 1); truth(readText(payload).equals("Fixture group")); }
        }
        truth(!payload.hasRemaining());
    }
    private static void recovery(String[] a) throws Exception {
        byte[] expectedRoot = hex(a[1]); char[] code = a[2].toCharArray();
        int iterations = Integer.parseInt(a[6]); byte[] salt = b64(a[7]), nonce = b64(a[8]);
        PBEKeySpec specification = new PBEKeySpec(code, salt, iterations, 256);
        byte[] key;
        try { key = SecretKeyFactory.getInstance("PBKDF2WithHmacSHA256").generateSecret(specification).getEncoded(); }
        finally { specification.clearPassword(); Arrays.fill(code, '\0'); }
        String aad = "orbit-navigator|recovery-key-wrap|version=1|profile=" + a[3] + "|keyset=" + a[4]
                + "|generation=" + a[5] + "|wrap=recoverycode|kdf=pbkdf2sha256|iterations=" + iterations
                + "|memorykib=0|parallelism=1|derivedbytes=32|salt=" + HexFormat.of().withUpperCase().formatHex(salt);
        byte[] encrypted = concat(b64(a[9]), b64(a[10]));
        same(expectedRoot, aes(Cipher.DECRYPT_MODE, key, nonce, utf8(aad), encrypted));
        same(encrypted, aes(Cipher.ENCRYPT_MODE, key, nonce, utf8(aad), expectedRoot));
        rejected(key, nonce, utf8(aad + "tampered"), encrypted);
        byte[] altered = encrypted.clone(); altered[altered.length - 1] ^= 1;
        rejected(key, nonce, utf8(aad), altered); Arrays.fill(key, (byte) 0);
    }
    private static byte[] aes(int mode, byte[] key, byte[] nonce, byte[] aad, byte[] bytes) throws Exception {
        Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
        cipher.init(mode, new SecretKeySpec(key, "AES"), new GCMParameterSpec(128, nonce)); cipher.updateAAD(aad);
        return cipher.doFinal(bytes);
    }
    private static void rejected(byte[] key, byte[] nonce, byte[] aad, byte[] bytes) throws Exception {
        assertions++;
        try { aes(Cipher.DECRYPT_MODE, key, nonce, aad, bytes); }
        catch (AEADBadTagException expected) { return; }
        throw new AssertionError("Unauthenticated fixture was accepted.");
    }
    private static byte[] hmac(byte[] key, byte[] bytes) throws Exception {
        Mac mac = Mac.getInstance("HmacSHA256"); mac.init(new SecretKeySpec(key, "HmacSHA256")); return mac.doFinal(bytes);
    }
    private static String readText(ByteBuffer payload) {
        int count = payload.getInt(); if (count < 0 || count > payload.remaining()) throw new AssertionError("Invalid fixture text.");
        byte[] bytes = new byte[count]; payload.get(bytes); return new String(bytes, StandardCharsets.UTF_8);
    }
    private static byte[] hex(String s) { return HexFormat.of().parseHex(s); }
    private static byte[] b64(String s) { return Base64.getUrlDecoder().decode(s); }
    private static byte[] utf8(String s) { return s.getBytes(StandardCharsets.UTF_8); }
    private static byte[] concat(byte[]... pieces) {
        int size = 0; for (byte[] piece : pieces) size += piece.length;
        ByteBuffer result = ByteBuffer.allocate(size); for (byte[] piece : pieces) result.put(piece); return result.array();
    }
    private static void same(byte[] expected, byte[] actual) { assertions++; if (!MessageDigest.isEqual(expected, actual)) throw new AssertionError("Fixture bytes differ."); }
    private static void truth(boolean value) { assertions++; if (!value) throw new AssertionError("Fixture field differs."); }
}
