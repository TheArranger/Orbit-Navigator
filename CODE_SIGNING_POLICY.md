# Code signing policy

## Current status

Orbit Navigator's first-party code and artwork are released under the MIT
License to the extent Orbit Nav Pub holds rights in them. No public-trust
signing provider has approved this project, and the project must not claim
Authenticode trust until an independently verified signed release exists.
Current installers are unsigned unless a release record explicitly proves
otherwise. Changing the license does not remove Windows warnings or provide
a certificate.

SignPath Foundation is a potential no-cost provider, not a promised or
approved service. Its [published conditions](https://signpath.org/terms)
require more than an OSI-approved license: no commercial dual licensing, no
proprietary components except qualifying System Libraries, active maintenance,
documented releases, verified build origin, MFA, and assigned review/signing
roles. Orbit's WebView2 SDK/runtime and offline prerequisite packaging require
an explicit eligibility review; do not assume they qualify as System Libraries.
All third-party notices and signatures must remain intact. Future closed-source
versions would not qualify under this open-source-only program.

The ECDSA signature on an Orbit update manifest authenticates update metadata;
it is separate from Windows Authenticode and does not establish a Windows
publisher identity.

## Intended release-signing controls

- Release artifacts are built from a reviewed, versioned commit by the
  checked-in build scripts.
- Origin verification restricts release signing to the protected `main` or an
  explicitly approved `release/*` branch.
- At least one designated release approver must approve each signing request.
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

Any future signing-provider acknowledgement will be added only after that
provider accepts the project and an independently verified signed release
exists.

## Compromise and revocation

A suspected signing-key, signing-account, build-origin, or release-account
compromise blocks releases immediately. Maintainers will notify the signing
provider, revoke affected credentials or certificates, remove compromised
artifacts, publish a security advisory, and rotate update-manifest trust only
through an independently authenticated client release.
