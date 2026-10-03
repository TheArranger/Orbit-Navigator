# Temporary Orbit Navigator manifest signer

Orbit uses one Primary update channel. Its pinned ECDSA P-256 release-signing key
retains the legacy identifier `beta-2026-01`; that identifier and the historical
directory name do not enable a Beta channel. Its private PKCS#8
material is protected with Windows DPAPI CurrentUser and stored temporarily at:

`%USERPROFILE%\Desktop\Orbit Navigator Beta Signer\beta-manifest-signing-key.dpapi.json`

The directory ACL grants the current Windows account access and disables inherited
access. The private key is never copied into the repository, Docker image, update
artifact roots, manifest, logs, or application payload. Only its public SPKI value
is compiled into Orbit Navigator.

## Mandatory transfer and retirement

This Desktop location is temporary custody, not long-term offline storage. The
operator-requested encrypted offline transfer remains a separate custody action;
publishing a release does not perform or prove that transfer:

1. Move the entire `Orbit Navigator Beta Signer` directory to an encrypted flash
   drive owned by the release operator. Do not copy it and leave the Desktop copy.
2. Unmount the flash drive whenever a manifest is not actively being signed.
3. Confirm the Desktop directory is absent, empty the Recycle Bin if it was used,
   and verify the ProgramData update root contains no signer document.
4. Keep a second encrypted offline recovery copy or accept that loss of this key
   requires shipping a manually installed client with a rotated public key.
5. If the flash drive is lost, exposed, or the Windows account is compromised,
   retire `beta-2026-01`; never publish another manifest under that key ID.

DPAPI CurrentUser protection means the encrypted file can be decrypted only by the
same Windows account on this Windows installation. Moving it to a flash drive does
not make it portable to a different computer. A future hardware-backed or dedicated
offline release-signing process must replace this temporary arrangement.

The shipped Primary client pins this existing public key, and
`scripts/New-PrimaryUpdateManifest.ps1` uses the matching protected private key.
Do not initialize or replace the key during routine publication: rotation requires
a reviewed client trust migration. The signer must remain outside the repository,
container, and public artifact root.

Primary verifies signed manifests and installer hashes but current installers are
not Authenticode-signed. Every unsigned installation requires a clear
unknown-publisher warning and deliberate per-package confirmation; it must never
silently apply. See `deployment/update-feed/README.md` for the current publication
and isolation policy.
