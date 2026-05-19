# Astro Coverage Planner (ACP) — NINA Plugin

NINA-side companion to [Astro Coverage Planner](https://github.com/astro-roro/Astro-Coverage-Planner). Pulls plans from a running ACP instance and pushes them into NINA's Framing Assistant. From there you save the framing as a sequencer target and image it however you like — Simple Sequencer, Advanced Sequencer, or just manual capture. No Target Scheduler required.

The plugin also has an optional one-click "Sync All to TS" button for users who do run Target Scheduler — no zip imports, no faff.

## What it does

- **Plans dock panel** in the Imaging tab. Live list of every plan in your running ACP instance with project, target, per-filter integration goals, mosaic shape, and gear.
- **Push to Framing Assistant.** One click loads the selected target into NINA's Framing Wizard with the right coordinates, rotation, mosaic geometry (rows × cols × overlap), camera dimensions, and focal length. Save the framing as a sequencer target and you're done.
- **Sync All to TS** (optional). If you use Target Scheduler, this triggers ACP's bidirectional TS sync extension to push every plan into the TS database for the active NINA profile.
- **Options page** for the ACP server URL and behavior toggles.

## Requirements

- **NINA 3.1.2.9001 or newer.**
- **A running [Astro Coverage Planner](https://github.com/astro-roro/Astro-Coverage-Planner) instance** reachable on `http://127.0.0.1:5555` (configurable in plugin options). ACP must run on the same machine as NINA for v1.x.
- For the **Sync All to TS** button only: ACP's private `nina_ts_sync` extension installed. (Without it, the rest of the plugin still works — Framing push doesn't need it.)

## Installation

### Manual install (until the marketplace listing lands)

1. Download `ACP.NINA.Plugin-v<version>.zip` from the [latest release](https://github.com/astro-roro/ACP.NINA.Plugin/releases/latest).
2. Extract the three DLLs into `%localappdata%\NINA\Plugins\3.0.0\ACP.NINA.Plugin\` (create the folder if needed).
3. Restart NINA. The plugin appears under **Options → Plugins → Installed**.
4. Right-click the Imaging tab's panel area → add the **Astro Coverage Planner** dock panel to your layout.

### Marketplace install (coming soon)

Once listed on NINA's plugin marketplace, you'll be able to install directly from **Options → Plugins → Available**.

## First-time setup

1. Make sure ACP is running on the same machine. Open `http://127.0.0.1:5555/` in a browser to confirm.
2. In NINA, open the ACP dock panel. The status line at the top should flip from "Probing..." to a green dot + "Connected — http://127.0.0.1:5555".
3. If you see "Not connected", open **Options → Plugins → Astro Coverage Planner (ACP)** and check the Server URL. Click **Test** to verify.

## Using it

**To frame a target from ACP:**

1. Pick a plan from the list in the dockable.
2. Click **Push to Framing**.
3. Switch to the Framing Assistant view. The target's loaded with the right coordinates, mosaic, and rotation.
4. Click **Set as Sequence Target** (or whatever NINA calls it in your version) to send it to your sequencer.

**To sync everything to Target Scheduler:**

1. Make sure ACP's `nina_ts_sync` extension is installed.
2. In the dockable, confirm the "Will sync to profile: ..." line shows the right NINA profile (it auto-uses the active one).
3. Click **Sync All to TS**. Footer reports the counts (inserted / updated) once done.

## Troubleshooting

- **Plugin doesn't show in Installed list.** Restart NINA fully — plugin DLLs are loaded once at startup and aren't hot-reloaded.
- **Dock panel stays "Probing..." forever.** ACP isn't reachable. Confirm the server is running (`http://127.0.0.1:5555` in a browser), then click the refresh button in the dockable.
- **Push to Framing loads coords but no rectangles / wrong rotation.** Make sure you're on plugin v1.0.0.0 or newer — earlier builds had rendering bugs that v1.0.0.0 fixes.
- **Anything else.** Open an issue with the relevant chunk of `%localappdata%\NINA\Logs\<latest>.log` (search for `ACP:` to find plugin entries).

## Build from source

```
dotnet build ACP.NINA.Plugin.sln -c Release
```

Debug builds auto-deploy to `%localappdata%\NINA\Plugins\3.0.0\ACP.NINA.Plugin\` via a post-build xcopy. Restart NINA to pick up changes. See `RELEASING.md` for the full release process.

## License

MIT — see `LICENSE.txt`.
