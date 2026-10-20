# Code signing policy - proposed, not approved

SignPath Foundation is the proposed free signing provider. No Foundation
certificate, service approval, credentials or signing result exists for this
candidate. The required provider attribution will be added as a live claim only
after acceptance; proposed wording: Free code signing provided by SignPath.io,
certificate by SignPath Foundation.

Project contact/owner contact: github@blahh.us. The owner must confirm the public
GitHub account/team and copyright holder before publication. Committers and
reviewers are owner-designated maintainers; signing approver is the owner or a
separately named trusted maintainer. All maintainers require MFA. Configure the
GitHub `signpath-production` environment with a required human reviewer and
protected main branch, then configure SignPath policies with manual approval.
Do not treat an AI review as a maintainer identity or a human signing approval.

## Scope and unresolved eligibility

- Only original desktop app binaries (`ForeverFlip.exe`, `ForeverFlip.dll`) and
  the generated desktop Setup executable are nominated for signing.
- Runtime/updater libraries keep upstream signatures or remain unsigned; they
  are not signed under this project's identity. Their license notices ship.
- No private addon implementation is in the source tree or installer. At the
  user's request/initial setup, the app installs a separately distributed addon
  from the signed HTTPS feed. Confirm with SignPath whether a companion managing
  this closed-source add-on qualifies. Separating packages is not proof of
  eligibility; do not represent it as a workaround to their requirements.
- Confirm the generated Velopack Setup artifact/metadata configuration and its
  original-framework code are acceptable, and confirm installer privacy notice
  requirements in PRIVACY.md. Upstream native/bootstrapper notice completeness
  needs checking before the first signed distribution.

## Workflow and signing order

`build.yml` runs on GitHub-hosted Windows workers for PR/main/manual builds with
read-only repository permissions. It tests and builds unsigned artifacts with no
signing secrets. No pull_request_target or untrusted branch signing is used.

`sign.yml` is manual only, main only, and requires `SIGNPATH_ENABLED=true` plus
the protected environment. Every job leading to signing runs on GitHub-hosted
workers. Actions are pinned to full commits. It:

1. Performs a clean, locked restore/build/tests and publishes the desktop.
2. Uploads only the two own app binaries as a GitHub workflow artifact.
3. Requests Foundation signing with the `own-app-binaries` artifact configuration.
4. Verifies trusted Authenticode identity and timestamp, and packages those signed
   binaries using pinned Velopack. It does not modify/re-sign upstream files.
5. Uploads only the generated Setup executable and requests its separate signing.
6. Verifies Setup's signature/timestamp and hashes the final installer/packages.
7. Saves signed review artifacts. It creates no GitHub Release and changes no
   production website, API, ES256 release feed or addon package.

Owner must configure `SIGNPATH_API_TOKEN` as an environment secret; organization,
project and app/setup policy slugs are environment/repository variables. No
production release/pack private key belongs in GitHub. The existing private
ES256 release process signs metadata only after final Authenticode bytes exist.
The provider must review/activate the XML configuration templates in
`scripts/signpath/`; XML parsing alone is not service acceptance.

## Acceptance and rollback

Before publication: approve the exact desktop-only diff/proposed MIT license;
confirm dependency/notices and contact identity; resolve the eligibility/privacy
questions; review the new repository's entire initial commit; then separately
approve making it public and enabling signing. A local build is not a verified
GitHub provenance run. A future initial signed candidate needs a real provider
run and clean-machine install/update verification; no such result is claimed now.

Retain previous signed/unsigned installers and existing private source backup.
To stop this integration, disable SIGNPATH_ENABLED/revoke only its token and
stop dispatching the signing workflow. Do not alter game saves or force an app
downgrade. Production release rollback remains the existing owner-controlled
feed/package process; this draft has not changed those feeds. Signing improves
publisher identity/integrity, not a guarantee against every SmartScreen warning.

Sources: https://signpath.org/terms,
https://docs.signpath.io/trusted-build-systems/github,
https://docs.signpath.io/artifact-configuration/examples,
https://docs.velopack.io/packaging/signing.
