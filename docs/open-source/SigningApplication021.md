# Orbit Navigator no cost signing application preparation

Orbit Nav Pub can request a SignPath Foundation eligibility review for the
MIT-licensed Windows browser. No application has been submitted, no provider has
approved it, and no trusted certificate has been issued for this project.

## Project facts for maintainer review

- Project: Orbit Navigator, maintained by Orbit Nav Pub / Paradox
  (TheArranger / TheCodeArranger).
- Public source: https://github.com/TheArranger/Orbit-Navigator
- License: MIT for first-party source and artwork to the extent rights are held.
- Windows technology: C#/.NET, WPF, Microsoft WebView2, Inno Setup.
- Existing policies: CODE_SIGNING_POLICY.md, PRIVACY.md, GOVERNANCE.md,
  SECURITY.md, THIRD-PARTY-NOTICES.md.
- Build controls: pinned .NET SDK and dependencies, commit-pinned CI actions,
  deterministic managed builds, protected-source review and recorded hashes.
- New 0.2.1 source is not yet a published, reviewed release artifact.

## Questions that must be resolved

The maintainer must supply the application contact and confirm account MFA,
release rights, and named author/reviewer/signing-approver roles. The provider
must explicitly review the proprietary WebView2 SDK/runtime and offline runtime
redistribution under its system-library exception. Third-party binaries retain
their own signatures and must not be re-signed as Orbit-owned software.

Any future proprietary release requires a different eligible signing route;
the open-source-only program is not permission to sign closed-source versions.
Do not claim provider sponsorship or change the publisher identity before
approval. If approved, wire reviewed CI artifacts, manual signing approval,
timestamping and independent verification before producing update manifests.

## Official references

[SignPath conditions](https://signpath.org/terms.html) define approval,
open-source eligibility, build-origin and signing-role requirements.
[Apply](https://signpath.org/apply.html) is the provider's application entry.
Approval is discretionary; the project cannot grant itself a trusted signature.

[Microsoft SmartScreen guidance](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation)
distinguishes code signing from file/publisher reputation. A newly signed build
can still warn. Self-signing and a software license do not establish public
Windows trust. The update feed's ECDSA manifest signature protects update
metadata but is not Authenticode signing.
