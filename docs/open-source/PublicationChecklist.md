# Open-source publication and SignPath checklist

## Completed

- MPL-2.0 canonical license and repository-wide scope notice.
- Trademark, privacy, security, contribution, support, and code-signing
  policies.
- Third-party dependency inventory and distributed license/notice files.
- Structured privacy-safe bug-report issue form.
- Exact .NET SDK pin and committed NuGet lock-file generation.
- Read-only GitHub Actions permissions and commit-pinned action dependencies.
- Release build, complete non-WPF tests, WPF tests, and open-source readiness
  check.
- Public project identity selected as Orbit Nav Pub, with Paradox (aka
  TheArranger or TheCodeArranger) as the named maintainer.
- Public repository established at
  `https://github.com/TheArranger/Orbit-Navigator` with MPL-2.0 detected by
  GitHub.
- Protected `main` requires the strict `build-test` status check and linear
  history; force-pushes and branch deletion are disabled for administrators.
- Private vulnerability reporting, dependency alerts, and automated security
  fixes are enabled.
- The public clean-checkout CI build and portable test suites pass.

## Required before the first public binary release

- Confirm that every original source and artwork contribution may be released
  under MPL-2.0. Resolve or remove any item with uncertain ownership.
- Assign named authors/committers, reviewers, and release approvers.
- Confirm that the maintainer's GitHub account uses MFA.
- Select donation destinations. Do not enable the existing in-app support
  affordance or add `.github/FUNDING.yml` until the exact destinations are
  owned and verified.
- Wire the in-app “Report a problem” command only to the verified structured
  GitHub Bug report URL and test the external-browser launch.
- Review the published privacy notice against every enabled network adapter.
- Compare release payload hashes with the reviewed public commit and CI record.

## Required before SignPath Foundation application

- Publish a documented release built from the public repository.
- Keep the project fully OSI-licensed without commercial dual licensing or
  project-owned proprietary components in the signed package.
- Publish the named code-signing roles and required SignPath acknowledgement.
- Configure SignPath's trusted build-system and origin-verification policy for
  protected release branches.
- Prove that public release artifacts originate from reviewed source and pass
  the full test suite.
- Obtain SignPath Foundation acceptance. Until acceptance, installers remain
  unsigned and must not claim a trusted publisher.

## Separate infrastructure gate

Open-source publication does not activate the Orbit update feed. Docker feed
deployment, signed manifests, Cloudflare origin health, and an end-to-end Beta
update test remain separately required.
