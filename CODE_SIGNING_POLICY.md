# Code signing policy

## Current status

Orbit Navigator is preparing to apply for the free SignPath Foundation
open-source signing program. The project has not been accepted and must not
claim SignPath or publicly trusted Authenticode signing until acceptance and
an independently verified signed release exist. Current installers are
unsigned unless a release record explicitly proves otherwise.

The ECDSA signature on an Orbit update manifest authenticates update metadata;
it is separate from Windows Authenticode and does not establish a Windows
publisher identity.

## Intended release-signing controls

- Release artifacts are built from a reviewed, versioned commit in the public
  repository by the checked-in build scripts.
- Origin verification restricts release signing to the protected `main` or an
  explicitly approved `release/*` branch.
- At least one designated release approver must approve each signing request.
  Author, reviewer, and approver identities will be published before the
  SignPath application; those roles are not yet assigned.
- Managed assemblies are built deterministically. The release record captures
  source commit, tool versions, input hashes, output hashes, and test results.
- The launcher, application executables, eligible project DLLs, updater runner,
  uninstaller, and final installer are signed where the selected signing
  service supports their format.
- Signing is followed by RFC 3161 timestamping and independent Authenticode
  verification. No file is changed after its final signature.
- Update manifests are generated only after the final signed installer hash,
  size, version, and publisher identity are known.
- Signing credentials never enter the repository, application payload,
  container, public update roots, logs, or contributor machines.

If SignPath Foundation accepts the project, official release pages will carry
the required acknowledgement: “Free code signing provided by SignPath.io,
certificate by SignPath Foundation.” Until then, that sentence is a planned
acknowledgement rather than a claim of service.

## Compromise and revocation

A suspected signing-key, signing-account, build-origin, or release-account
compromise blocks releases immediately. Maintainers will notify the signing
provider, revoke affected credentials or certificates, remove compromised
artifacts, publish a security advisory, and rotate update-manifest trust only
through an independently authenticated client release.
