# Temporary Orbit Navigator Beta manifest signer

The Beta manifest key is an ECDSA P-256 release-signing key. Its private PKCS#8
material is protected with Windows DPAPI CurrentUser and stored temporarily at:

`%USERPROFILE%\Desktop\Orbit Navigator Beta Signer\beta-manifest-signing-key.dpapi.json`

The directory ACL grants the current Windows account access and disables inherited
access. The private key is never copied into the repository, Docker image, update
artifact roots, manifest, logs, or application payload. Only its public SPKI value
is compiled into Orbit Navigator.

## Mandatory transfer and retirement

This Desktop location is temporary custody, not long-term offline storage. Before
any public Beta manifest is published:

1. Move the entire `Orbit Navigator Beta Signer` directory to an encrypted flash
   drive owned by the release operator. Do not copy it and leave the Desktop copy.
2. Unmount the flash drive whenever a manifest is not actively being signed.
3. Confirm the Desktop directory is absent, empty the Recycle Bin if it was used,
   and verify neither ProgramData update root contains a signer document.
4. Keep a second encrypted offline recovery copy or accept that loss of this key
   requires shipping a manually installed client with a rotated public key.
5. If the flash drive is lost, exposed, or the Windows account is compromised,
   retire `beta-2026-01`; never publish another manifest under that key ID.

DPAPI CurrentUser protection means the encrypted file can be decrypted only by the
same Windows account on this Windows installation. Moving it to a flash drive does
not make it portable to a different computer. A future hardware-backed or dedicated
offline release-signing process must replace this temporary arrangement.

Primary uses a separate future key ring and remains disabled until its installer is
signed by a Windows-trusted Authenticode publisher. The Beta key must never be reused
for Primary.
