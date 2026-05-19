# Releasing a new version

Maintainer playbook for cutting a release without breaking things for the hundreds of NINA users who might have the plugin installed. The TL;DR: **always ship to the Beta channel first; promote to Stable only after soak time**.

## Why this is scary, and why it isn't

Once you're on NINA's plugin marketplace, a bad release means:
- Anyone with the plugin auto-updates to it next time they open NINA
- A plugin that crashes NINA on load is hard to recover from (user has to manually delete the plugin folder before NINA will start)
- Bug reports come in from people you've never met, at midnight, mid-imaging-session

The mitigation isn't "test more" — it's **don't release directly to Stable**. NINA has a Beta channel built into its plugin system. Use it.

## The two channels

NINA's plugin manager has two channels, both controlled by what's in the `isbeorn/nina.plugin.manifests` repo:

- **Stable** — what every NINA user sees by default. Conservative. Only mature releases.
- **Beta** — opt-in via NINA Options → General → Plugin Repositories, where users can add the URL `https://nighttime-imaging.eu/wp-json/nina/v1/beta`. People who actively want to test pre-release builds.

The manifest JSON has a single field that picks the channel:

```json
"Channel": "Beta"
```

Omit it (or set to `"Stable"`) and it goes to the default channel.

## The release flow

For every release — bugfix or feature, no exceptions:

### 1. Smoke test locally first

Before bumping the version, run through this in a clean(ish) NINA session:

- [ ] Open NINA. Plugin appears in **Options → Plugins → Installed** with the expected version.
- [ ] ACP dock panel auto-probes and shows "Connected — http://127.0.0.1:5555".
- [ ] Plans count matches what ACP shows in its own web UI.
- [ ] Select a 1×1 plan with rotation 0. Push to Framing. Verify coords, FOV box visible.
- [ ] Select a 1×1 plan with non-zero rotation. Push to Framing. Verify rectangle is tilted to the right angle.
- [ ] Select a 2×1 or 2×2 mosaic plan. Push to Framing. Verify both panel count in left menu AND visible rectangles on the sky view.
- [ ] Click Sync All to TS. Verify success message in footer + plans appearing in TS Project Manager.
- [ ] Stop ACP. Click Refresh in the dockable. Verify status goes red with a clear error message.

Any failure → fix and re-test. Don't release until all eight steps pass.

### 2. Bump version, build, package

```powershell
# Update version in ACP.NINA.Plugin/Properties/AssemblyInfo.cs:
#   [assembly: AssemblyVersion("X.Y.Z.W")]
#   [assembly: AssemblyFileVersion("X.Y.Z.W")]
# Update CHANGELOG.md.

dotnet build ACP.NINA.Plugin.sln -c Release
# Bundle the three release DLLs into ACP.NINA.Plugin-v<version>.zip
# (see the pack snippet in the build folder; or just zip the deployed
# %localappdata%\NINA\Plugins\3.0.0\ACP.NINA.Plugin\ contents).
```

### 3. Compute the checksum

NINA validates the zip's SHA-256 against the manifest. Wrong checksum = NINA refuses to install.

```powershell
Get-FileHash ACP.NINA.Plugin-v1.0.0.0.zip -Algorithm SHA256
```

### 4. Tag + GitHub release

```
git tag -a vX.Y.Z.W -m "ACP.NINA.Plugin vX.Y.Z.W"
git push origin main --tags
gh release create vX.Y.Z.W ACP.NINA.Plugin-vX.Y.Z.W.zip \
    --title "vX.Y.Z.W - <short title>" \
    --notes-file release-notes.md \
    --prerelease     # ← include this flag for pre-release/beta builds
```

For beta releases, `--prerelease` tells GitHub to display the release with a yellow "Pre-release" badge so users know not to grab it directly.

### 5. Beta channel first

PR against `isbeorn/nina.plugin.manifests`:

```
manifests/a/Astro Coverage Planner (ACP)/3.0.0/manifest-3.1.json
```

```json
{
    "Name": "Astro Coverage Planner (ACP)",
    "Identifier": "9F9EB062-B1CC-4622-A2FC-4362FE97CD08",
    "Channel": "Beta",                                   ← ← ← key field
    "Version": { "Major": "1", "Minor": "0", "Patch": "0", "Build": "0" },
    "MinimumApplicationVersion": { "Major": "3", "Minor": "1", "Patch": "2", "Build": "9001" },
    "Author": "astro-roro",
    "Homepage": "https://github.com/astro-roro/Astro-Coverage-Planner",
    "Repository": "https://github.com/astro-roro/ACP.NINA.Plugin/",
    "License": "MIT",
    "LicenseURL": "https://github.com/astro-roro/ACP.NINA.Plugin/blob/main/LICENSE.txt",
    "ChangelogURL": "https://github.com/astro-roro/ACP.NINA.Plugin/blob/main/CHANGELOG.md",
    "Tags": ["Planning", "Framing", "TargetScheduler", "Coverage", "Mosaic"],
    "Descriptions": {
        "ShortDescription": "Push targets from Astro Coverage Planner into NINA's Framing Assistant and Target Scheduler.",
        "LongDescription": "..."
    },
    "Installer": {
        "URL": "https://github.com/astro-roro/ACP.NINA.Plugin/releases/download/vX.Y.Z.W/ACP.NINA.Plugin-vX.Y.Z.W.zip",
        "Type": "ARCHIVE",
        "Checksum": "<paste SHA-256 from step 3>",
        "ChecksumType": "SHA256"
    }
}
```

Open the PR. Wait for the manifest maintainers to merge.

### 6. Soak time

After merge, the release is live on the Beta channel. **Don't promote to Stable for at least a week**, ideally longer for breaking changes. Watch for issues. Ask in the NINA Discord plugin channel for testers if you want feedback faster.

### 7. Promote to Stable

If no issues during soak: open a second PR removing the `"Channel": "Beta"` line (or set to `"Stable"`). Same manifest entry, same version, same zip — just promoted.

If issues did surface: cut a new version with the fix, beta again, soak again. Never promote a broken release.

## Hotfix flow (for critical bugs in Stable)

If you discover a bug in a Stable release that needs urgent fixing:

1. Pull the manifest entry — submit a PR removing the broken manifest version. NINA will no longer offer that version for new installs. (Users who already installed it stay broken until they update, but at least new users can't hit it.)
2. Fix in code, bump patch version (e.g., 1.0.0 → 1.0.1).
3. Skip the Beta channel for the hotfix only — submit straight to Stable with the new version. Justify "hotfix for critical bug in v1.0.0" in the PR.

This is the only path that legitimately skips beta. Use it sparingly.

## What never to do

- **Never modify a published zip.** Once a checksum is in the manifest, that zip is immutable. If you need to change the contents, bump the version and re-release.
- **Never auto-publish to the marketplace from CI.** Manifest PRs are manual and intentional. CI auto-publishing makes it too easy to ship something broken.
- **Never rely on tests alone.** The smoke test above runs in NINA, against a real ACP instance, with real plans. Unit tests can't catch the bugs that matter most (rendering issues, integration races, UI threading).

## What's a safe day-to-day cadence

For solo development:

- **Patch (X.Y.Z+1):** small bugfix, no new behaviour → beta for 2-3 days, then promote.
- **Minor (X.Y+1.0):** new feature, no breaking changes → beta for 1 week, then promote.
- **Major (X+1.0.0):** breaking changes (e.g., schema bump, API redesign) → beta for 2-3 weeks, with explicit communication in the Discord. Promote only when you're confident.
