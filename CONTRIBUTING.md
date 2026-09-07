# Contributing to Orbit Navigator

Thank you for helping improve Orbit Navigator. Contributions are accepted
under the Mozilla Public License 2.0; no commercial dual-license grant is
requested.

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
   change and that it may be distributed under MPL-2.0.

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
