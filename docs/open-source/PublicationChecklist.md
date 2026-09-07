# Open-source publication and SignPath checklist

## Completed locally

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

## Required before making the repository public

- Confirm that every original source and artwork contribution may be released
  under MPL-2.0. Resolve or remove any item with uncertain ownership.
- Select the public repository owner and URL; the current local repository has
  no remote and no initial commit.
- Assign named authors/committers, reviewers, and release approvers.
- Enable branch protection, required CI, MFA, and private GitHub Security
  Advisory reporting.
- Select donation destinations. Do not enable the existing in-app support
  affordance or add `.github/FUNDING.yml` until the exact destinations are
  owned and verified.
- Select and verify the final public HTTPS bug-report destination before wiring
  the in-app “Report a problem” command.
- Review the published privacy notice against every enabled network adapter.
- Run a clean public-clone build and compare managed payload hashes with the
  reviewed local build.

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
