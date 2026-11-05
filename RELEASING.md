# Releasing a new version

Maintainer playbook. The path is: smoke test locally in NINA, cut a GitHub release, open a manifest PR against Stable. There is no beta step: the Beta channel did not work in practice, so the local smoke test is the gate.

Once the plugin is on NINA's marketplace, a bad release auto-updates onto everyone who has it, and a plugin that crashes NINA on load has to be deleted by hand before NINA will start. So do not skip the smoke test, and do not hurry the manifest PR.

## 1. Bump version and changelog

- Set `AssemblyVersion` and `AssemblyFileVersion` in `ACP.NINA.Plugin/Properties/AssemblyInfo.cs` to the new `X.Y.Z.W`.
- Turn the top `CHANGELOG.md` entry into the release entry, with the date.
- Grep the repo for the old version string in case anything else carries it.

## 2. Build and package

On the Windows imaging machine:

```powershell
dotnet build ACP.NINA.Plugin.sln -c Release
```

The post-build step copies the plugin into `%localappdata%\NINA\Plugins\3.0.0\ACP.NINA.Plugin\`. Zip the contents of that folder (not the folder itself) as `ACP.NINA.Plugin-vX.Y.Z.W.zip`. It must hold `ACP.NINA.Plugin.dll`, `Newtonsoft.Json.dll`, `CommunityToolkit.Mvvm.dll`, `Microsoft.Data.Sqlite.dll`, `SQLitePCLRaw.core.dll`, `SQLitePCLRaw.batteries_v2.dll`, `SQLitePCLRaw.provider.e_sqlite3.dll`, and `e_sqlite3.dll` both in the root and in `runtimes\win-x64\native\`.

## 3. Smoke test in NINA

Run the zip's contents (the folder you just built) in a real NINA session against a real ACP. Stop at the first failure, fix it, and start the list again.

- [ ] Plugin appears in **Options → Plugins → Installed** with the expected version.
- [ ] ACP dock panel auto-probes and shows Connected. Plans count matches ACP's web UI.
- [ ] Push to Framing with a 1x1 plan at rotation 0: coordinates right, FOV box visible.
- [ ] Push to Framing with a non-zero rotation: the rectangle is tilted to the right angle.
- [ ] Push to Framing with a 2x1 or 2x2 mosaic: panel count in the left menu and rectangles on the sky view both match.
- [ ] Sync All to TS: success message in the footer, plans appear in Target Scheduler.
- [ ] Sync for tonight (dock button, then the sequencer instruction): solve completes, plans that fit are loaded into Target Scheduler, and the report says what was loaded and left out.
- [ ] Stop buttons: press "✕ Stop" during Sync for tonight, Sync All to TS and Send TS to ACP. Each ends cleanly, and Target Scheduler and the profile are as they were before.
- [ ] Live progress: with Target Scheduler running (or a target started and finished), ACP's hours for the plan move, and the dock footer reads "Progress sent N s ago".
- [ ] Gear templates: after a sync, the exposure templates in Target Scheduler match ACP's gear templates, and moon, twilight, humidity and dither values you set in Target Scheduler are unchanged.
- [ ] Send TS to ACP, against an ACP with the bundled `nina-ts-sync` extension and a token set in the plugin options: upload completes, the dock shows ACP's counts, and "Review and apply in ACP" opens the review page. Repeat with the token cleared and confirm it stops at once with a matching message.
- [ ] Stop ACP, press Refresh in the dock: status goes red with a clear error.

## 4. Checksum

NINA checks the zip's SHA-256 against the manifest, so a wrong checksum means NINA refuses to install.

```powershell
Get-FileHash ACP.NINA.Plugin-vX.Y.Z.W.zip -Algorithm SHA256
```

## 5. GitHub release

Merge the release branch to `main` first, then:

```
git tag -a vX.Y.Z.W -m "ACP.NINA.Plugin vX.Y.Z.W"
git push origin main --tags
gh release create vX.Y.Z.W ACP.NINA.Plugin-vX.Y.Z.W.zip --title "vX.Y.Z.W" --notes-file release-notes.md
```

## 6. Manifest PR to Stable

Open a PR against `isbeorn/nina.plugin.manifests` adding:

```
manifests/a/Astro Coverage Planner (ACP)/3.1.2.9001/X.Y.Z.W/manifest.json
```

Start from `release/manifest-<version>.json` in this repo, or the previous version's manifest. There is no `Channel` field: that is Stable. Check:

- `Version` matches the build.
- `MinimumApplicationVersion` matches the `NINA.Plugin` package version in `ACP.NINA.Plugin.csproj`.
- `Installer.URL` points at the release zip you just uploaded, and `Checksum` is the hash from step 4.
- `LongDescription` describes this version, not the last one.

Wait for the manifest maintainers to merge. Manifest PRs are manual: never auto-publish from CI.

## Hotfix flow (critical bug in a published release)

1. Open a PR removing the broken manifest version, so new installs cannot get it. Users who already have it stay broken until they update.
2. Fix in code and bump the patch version (3.3.0.0 to 3.3.1.0).
3. Run the smoke test, then release as above. Say "hotfix for critical bug in vX.Y.Z.W" in the PR.

## What never to do

- **Never modify a published zip.** Once a checksum is in a manifest, that zip is immutable. To change the contents, bump the version and release again.
- **Never rely on unit tests alone.** The smoke test runs in NINA against a real ACP with real plans. Unit tests cannot catch rendering issues, integration races or UI threading.
