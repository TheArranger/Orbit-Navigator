# Open-source publication and release checklist

The current first-party release uses the standard MIT License. This checklist
does not certify signing-provider approval or current release test results;
each release needs its own evidence.

## Completed

- Standard MIT License, package license expression, and repository-wide notice.
- Trademark, privacy, security, contribution, support, and code-signing
  policies.
- Third-party dependency inventory and distributed license/notice files.
- Structured privacy-safe bug-report issue form.
- Exact .NET SDK pin and committed NuGet lock files.
- Read-only GitHub Actions permissions and commit-pinned action dependencies.
- Scripts for Release build, non-WPF tests, WPF tests, and release-readiness
  checks; execution results must be recorded for each release.
- Public project identity selected as Orbit Nav Pub, with Paradox (aka
  TheArranger or TheCodeArranger) as the named maintainer.
- Public repository established at
  `https://github.com/TheArranger/Orbit-Navigator`.
- Protected `main` requires the strict `build-test` status check and linear
  history; force-pushes and branch deletion are disabled for administrators.
- Private vulnerability reporting, dependency alerts, and automated security
  fixes are enabled.
- The public clean-checkout CI build and portable test suites pass.
- The verified optional donation destination is
  `https://ko-fi.com/paradoxthecreator`; the verified maintainer portfolio is
  `https://iamtheparadox.com/`.

## Required before a public binary release

- Confirm that Orbit Nav Pub holds release rights for every original source
  and artwork contribution. Resolve or remove any uncertain item.
- Confirm that the maintainer's GitHub account uses MFA.
- Wire the in-app Report a problem command only to the verified structured
  GitHub Bug report URL and test the external-browser launch.
- Review the published privacy notice against every enabled network adapter.
- Compare release payload hashes with the reviewed source and CI record.
- Disclose clearly that unsigned installers can show a Windows unknown-publisher
  warning and require deliberate confirmation.

## Required before trusted Authenticode signing

- Apply for a no-cost signing provider and obtain approval; MIT licensing
  alone is not approval. For SignPath Foundation, review the current
  [conditions](https://signpath.org/terms), including no commercial dual
  licensing and no proprietary components except qualifying System Libraries.
- Resolve eligibility of the WebView2 SDK/runtime and embedded offline
  prerequisite with the provider. Do not relabel their licenses or assume an
  exception.
- Publish the named code-signing roles and any provider acknowledgement.
- Configure the trusted build-system and origin-verification policy for
  protected release branches.
- Prove that release artifacts originate from reviewed source and pass the full
  test suite.
- Independently verify signatures and RFC 3161 timestamps. Until then,
  installers remain unsigned and must not claim a trusted publisher.
- If future versions become proprietary, do not use an open-source-only
  signing service for them. Already released MIT copies keep their rights.

## Separate infrastructure gate

Source publication does not activate the Orbit update feed. Docker feed
deployment, signed manifests, Cloudflare origin health, and an end-to-end
Primary update test remain separately required.
