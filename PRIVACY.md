# Privacy and consent - draft for owner/provider review

The companion contacts `https://forever.blahh.us` for releases, market service
capabilities, permitted uploads, support reports and signed research downloads.
It uses NuGet/GitHub only during development/CI, not as a hidden player-data
destination. Windows/.NET and upstream updater components have their own
operating-system/update behavior; see upstream notices and policies.

On first launch, a notice explains market sharing and offers an off switch
before application sync begins. Settings can turn it off later. Eligible market
data includes item identifiers, asking prices/stock/times, recipes, observed
vendor supplies and anonymous check outcomes. These are observations and
untrusted feedback, not proof of sales or profit. Normal market uploads exclude
account paths, character names, balances and personal purchase/sale ledgers.
The service receives ordinary connection metadata and a per-installation
registration credential. No personal game credentials are requested.

Private support reports are separate: an explicit addon issue/feedback submission
requests the diagnostic upload. They can include error text, settings and
transaction amounts, are private, and are retained for up to30 days. Do not put
personal details into report text. Turning market sharing off does not disable
an explicitly submitted support report or the app's update/research downloads.

Local settings, protected registration credentials, cache and diagnostic logs are
stored under the user's local application-data folder. The application reads the
selected game's saved addon file without executing Lua. Private bookkeeping is
displayed locally. Code replacement retains addon backups and preserves saved
game data. No game input or automated reload is sent by this companion.

**SignPath review gap:** the existing notice is on first launch, not a custom
installer page. Foundation terms call for disclosure/options during installation
when data is transferred to a service not chosen by the user. Ask whether this
pre-sync choice is acceptable or an installer consent step must be implemented
before approval. Do not claim that the current flow already satisfies that
requirement. No change to existing users' consent/settings is part of this draft.

Reference: https://signpath.org/terms. Contact: github@blahh.us.
