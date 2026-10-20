# Desktop 0.1.43-preview.1 - desktop-only source preparation

- Prepared a standalone desktop source tree, desktop tests and locked build tooling.
- The installer contains no game-addon source. Initial addon install and repair
  use the existing signed HTTPS release feed; initial installation needs internet.
- Retained signature, expiry, size, archive, process-state and personal-data checks.
- Included proposed MIT licensing, dependency notices, privacy disclosure and a
  manual signing workflow for review. The preview uses a source-generated icon.
- This is an unsigned preparation build, not a published update. SignPath
  approval, source-publication approval and native installation acceptance are pending.
