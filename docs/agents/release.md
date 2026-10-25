# Releasing a version

The steps for version X.Y.Z, in order. The order matters: the zip is named after the commit,
so it is built after the release commit.

1. **Notes**: `docs/releases/X.Y.Z.md`, following the previous release's file: the
   "own copy of the game" banner, What's new, Update, Known limits. If the release adds a
   player-facing feature, also add a README section.
2. **Bump**: `<Version>` in `launcher/Directory.Build.props`. It is the only version string.
3. **Commit** the notes and the bump as `Version X.Y.Z`. Check that `git diff HEAD
   --ignore-cr-at-eol` is empty, or the zip comes out `-dirty`.
4. **Build**: `pwsh ./scripts/build.ps1 -Action Build -Jobs 20`, then `-Action Launcher`
   (the launcher carries the version and the settings schema). If the settings changed,
   first run `pwsh ./scripts/export-settings-schema.ps1` and commit its result.
5. **Package**: `pwsh ./scripts/package.ps1 -SkipBuild`. It prints the zip path, the
   SHA-256 and "Release check passed". The zip is
   `out/release/WWEAllStarsRecomp-X.Y.Z-<commit>-win-x64.zip`, with its `.sha256` beside it.
6. **Public commit**: `pwsh ./scripts/publish-tree.ps1 -Message "WWE All Stars Recomp X.Y.Z"`
   (ADR-0023: the tree without `.scratch/`), then `git push origin public:main`. `main`
   itself is never pushed.
7. **GitHub release**, only on the owner's go: tag `vX.Y.Z` on the public commit, marked
   latest, with the notes as its body and the zip and `.sha256` attached. Then download the
   zip and check its SHA-256. The launcher's update check offers the update only after this
   step.
8. **Record it**: in the feature's release ticket, the private and public commits and the
   zip's SHA-256.
