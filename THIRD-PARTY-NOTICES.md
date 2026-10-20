# Third-party components and review scope

| Component | Selected version | License / evidence | Distribution treatment |
| --- | --- | --- | --- |
| Velopack application library | 1.2.161 | MIT in exact NuGet package metadata; upstream source commit92d6a1c91716729d449034df5c50307dcce39493 | Retain license; do not re-sign upstream DLLs |
| Velopack vpk/build framework and generated updater/bootstrapper | 1.2.161 | MIT in exact tool package metadata; same upstream source | Tool is build-only; generated updater/bootstrapper scope and any embedded third-party notices need final provider/upstream review |
| .NET runtime / Windows Desktop / WPF / Windows Forms | .NET10, SDK pinned in global.json; exact runtime selected by locked restore | MIT and included third-party notices in redistributed runtime packages | Retain runtime LICENSE/THIRD-PARTY-NOTICES files; do not re-sign Microsoft binaries |
| Own generated preview icon/splash | This source tree | MIT with scripts/New-Icons.ps1 | Original geometric icon; no existing private project artwork copied |
| Separate game addon and its libraries | Not included | Outside this source/package license | Downloaded independently through its signed feed; not relicensed or signed here |

The owner selected the MIT LICENSE for this exported desktop-only source.
It does not license the private monorepo. The existing addon database library's MIT
notice does not license this desktop project or grant rights to every project
asset. No claim is made that keeping a closed-source addon/backend separate is
sufficient for SignPath Foundation eligibility.

Exact NuGet package identities/content hashes appear in packages.lock.json.
The application has one direct package and no additional package dependencies
under its selected net10 target before runtime packs. Runtime packs carry
additional upstream copyright notices, copied unmodified when redistributed.
The packaging tool can include native components; this is a remaining notice
review boundary, not an assertion of a completed transitive native-license audit.

Full notices retained under licenses/ plus runtime output notices. Upstream:
https://github.com/velopack/velopack/tree/92d6a1c91716729d449034df5c50307dcce39493,
https://github.com/dotnet/runtime, https://github.com/dotnet/wpf,
https://github.com/dotnet/winforms.
