# Contributing to Orbit Navigator

Thank you for helping improve Orbit Navigator. The project is open source
under the MIT License. Forks and modified distributions are permitted under
that license; distinguish unofficial builds from official Orbit Nav Pub
releases without implying endorsement.

## Before submitting work

1. Search existing issues and use the structured bug report for defects.
2. Do not include browsing history, private-window activity, page contents,
   passwords, tokens, cookies, authorization codes, local file paths, or
   personal information in an issue, test fixture, screenshot, or log.
3. Read `docs/architecture/Ownership.md` and preserve the contract and privacy
   boundaries described there.
4. Keep new network behavior opt-in, narrowly scoped, and covered by negative
   tests. Do not introduce telemetry.
5. Confirm that you have the right to submit every source and asset in the
   change. Contributions are submitted under the same MIT License as the
   project unless an existing third-party license is clearly identified and
   accepted during review. No copyright assignment or separate contributor
   license agreement is required. Retain applicable copyright and license
   notices for contributions and dependencies.

## Build and test

From a PowerShell prompt at the repository root:

```powershell
.\scripts\Bootstrap-Toolchain.ps1
.\scripts\Build.ps1
.\scripts\Test.ps1
.\scripts\Test-OpenSourceReadiness.ps1
```

Use focused tests while iterating, then run the complete Release solution and
test suite before requesting review. Tests that launch Orbit must use an
attested disposable acceptance profile and must leave no process running.
Never run test automation against another person's normal profile.

## Review and release integrity

Every change requires review. A release must be produced from a reviewed,
versioned commit by the documented build scripts. Signing approval is separate
from code review; contributors do not receive access to signing credentials.
See `CODE_SIGNING_POLICY.md`.

Add user-visible fixes, changes, and known limitations to the Unreleased section
of `CHANGELOG.md`. The browser bundles this file in Settings. Release preparation
moves verified notes into a dated version section; unreleased work must never be
described as already delivered.
