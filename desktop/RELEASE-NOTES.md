# Desktop 0.1.46-preview.1 - desktop-only source update

- Added passwordless account sign-in and explicit browser-to-desktop connection,
  with protected local session storage and sign-out recovery.
- Require sign-in for shared AI market analysis and authenticated submissions.
- Preserve existing desktop state before the native installer runs; abort when
  preservation cannot be verified. Added isolated account and installer tests.
- The installer contains no addon source. Addon installation and repair use the
  signed HTTPS feed and retain signature, archive and personal-data safeguards.
- This source preview remains unsigned. Signing is disabled pending provider
  approval and separate signed installation/update acceptance. No production
  feed or executable release is published by this repository's build workflow.
