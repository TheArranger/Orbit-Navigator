# Orbit Navigator installer registration maintenance

The enduring canonical Inno Setup AppId is
`{C5104F72-0D7B-4DA9-B59C-BC02DF0478C2}`. It matches the active Orbit
Navigator 0.1.18 installer lineage.

An older `{0E774A5D-5BD0-4C93-A86E-DBCD81B6805D}` registration may still
exist on machines upgraded through the 0.1.17 package. It must not be deleted,
edited, or invoked implicitly by normal installation. A future, separately
authorized maintenance release should:

1. verify both registrations point to the same expected per-user Orbit
   Navigator installation directory;
2. verify the active canonical registration and installed payload are healthy;
3. remove only the obsolete registration metadata without running its
   uninstaller or deleting application/profile data; and
4. record an idempotent migration receipt so the cleanup cannot target an
   unrelated installation.

Until that maintenance migration is implemented and approved, installers use
only the canonical AppId and leave the obsolete entry untouched.
