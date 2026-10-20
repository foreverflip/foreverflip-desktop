# ForeverFlip Desktop - review candidate

Windows companion for WoW Forever. It manages addon installation/updates, reads
saved addon data, shares permitted market observations and downloads signed
research. Private profit/loss totals stay local. This repository contains the
desktop application, its tests and build/signing files only.

**Preparation only:** the owner selected MIT for this desktop-only source.
Source publication still awaits the owner's approval. SignPath has not approved this project; no signed release is
claimed. The addon, website, backend and operational records are not included.

## Build

Use Windows and the .NET SDK pinned in `global.json`. From PowerShell:

    ./scripts/Build.ps1 -Publish
    ./scripts/Pack.ps1

Build/tests use temporary synthetic fixtures. The normal build does not contact
the market service, upload player data, install into a real game folder, sign
files or publish a release. Running the app normally is different: review
[privacy and consent](PRIVACY.md) first. The addon is downloaded separately using
its existing signed feed; no addon implementation is built into this app.
An initial addon install therefore needs internet access. Signature, expiry,
download-size, archive, game-process and personal-data protections still apply.

The preview version is a new prerelease candidate, not a replacement for any
published immutable installer. It uses an original, code-generated placeholder
icon; existing project artwork remains outside this repository.

## Code signing policy

See [CODE-SIGNING.md](CODE-SIGNING.md) for the proposed SignPath policy, owner
approvals, signing order and remaining eligibility questions. CI builds unsigned
review artifacts. The separate manual signing workflow is disabled until
approved configuration is supplied and never publishes production feeds.

## License and support

The license for original source, scripts, docs and generated preview
artwork is MIT; see `LICENSE`. Third-party notices remain separate under
`licenses/` and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). SignPath is not
being asked to sign proprietary addon code or re-sign upstream libraries.

Contact: github@blahh.us. This address is a draft project contact, not evidence
that a GitHub or SignPath account is configured or that any message was sent.
WoW and related game marks belong to their respective owners; no affiliation
or endorsement is claimed. See [SECURITY.md](SECURITY.md) for private reporting.
