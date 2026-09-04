# Changelog

## 1.0.1 (2026-09-04)

### Fixed

- Push to Framing now applies the rotation last and confirms it held. NINA's camera width and height setters defer their rectangle rebuild, and each rebuild landing after the rotation set reset it, so rotated plans showed 0 degrees or alternated between right and wrong on successive pushes.
- Rotation is always applied, including zero, so a plan at 0 degrees pushed after a rotated one no longer keeps the previous angle.
- The push no longer reloads the sky image a second time after NINA's own load, and it waits for NINA's work to finish before touching the optics and mosaic fields. The reload and the race made mosaic panels vanish until the overlap slider was nudged.
- The overlap value now shows in NINA's stepper after a push. ACP's 15 percent used to display as NINA's previous number.
- The first push after NINA starts waits for the Framing rectangle instead of silently skipping the rotation.

## 1.0.0 — 2026-05-19

First public release.

### Features

- **Plans dock panel** in NINA's Imaging tab. Lists every plan in your
  running ACP instance with the project, target, per-filter integration
  goals, mosaic shape, and gear. Live connection indicator + refresh button.
- **Push to Framing Assistant.** Select any plan, click one button. NINA's
  Framing Wizard loads with the target's coordinates, rotation, mosaic
  geometry (rows × cols × overlap), camera dimensions, and focal length
  all set. From there you save the framing as a sequencer target and image
  it however you like — simple sequencer, advanced sequencer, or just
  manual capture. No Target Scheduler required.
- **Sync All to TS.** If you do use Target Scheduler, the dockable's
  "Sync All to TS" button triggers ACP's bidirectional sync extension
  to push every plan into the TS database for the active NINA profile.
  No zip imports, no UI faff.
- **Options page** for the ACP server URL and behavior toggles.
- **Connection probe** runs automatically when the dockable opens.

### Requirements

- NINA 3.1.2.9001 or newer.
- A running ACP instance (default `http://127.0.0.1:5555`). ACP must run
  on the same machine as NINA for v1.x; cross-machine support is planned
  for a future major version.
- For the TS sync button: the private `nina_ts_sync` extension installed
  in the ACP instance.
