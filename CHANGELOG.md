# Changelog

## 3.2.0 (unreleased)

ACP finds out what tonight actually acquired, while it is being acquired.

### Added

- **Acquired hours are reported back to ACP during a session.** When Target Scheduler starts or finishes a target, the plugin reads that target's acquired counts, converts them to hours using each exposure plan's sub length, and posts them to `POST /api/plans/<id>/progress`. The coverage map and the hours remaining stay current through the night instead of waiting for someone to run a sync. This replaces the Python extension's `sync-acquired` poll for people whose ACP is on another machine, and the two can run side by side.
- **A five minute fallback** while a Target Scheduler container is running, so a missed or unrecognised event costs at most five minutes of staleness rather than a whole night's worth.
- **"Report progress to ACP while imaging"** on the options page, on by default. Turning it off is honest about the cost: the Target Scheduler sync writes ACP's view of the counts back into Target Scheduler, so hours that go stale in ACP can walk the real counts backwards on a later sync.
- **A dock footer line** reading "Progress sent 22 s ago", or the last error when there is one.

### Notes

- Hours only ever go up. ACP refuses to lower a stored `actual_hours` unless it is told to force it, and this plugin never asks it to: a count that goes backwards in Target Scheduler is a culled frame or a reset project, and rewinding a plan someone has watched fill up is worse than being one session stale.
- A mosaic reports the hours from its 1,1 panel and only that panel, because ACP stores a mosaic's filter goals per panel rather than summed across them. This matches what the Python extension does, so both paths produce the same number.
- The Target Scheduler database is only ever opened read only here, which is why progress reporting is safe to run mid session when the 3.1 push deliberately refuses to.

## 3.1.0 (unreleased)

Target Scheduler sync moves inside the plugin, so it works when ACP is on another machine.

### Added

- **The Target Scheduler push runs in the plugin.** It opens `schedulerdb.sqlite` itself and writes a project per ACP project name, a target per mosaic panel, exposure plans from the filter goals and exposure templates deduplicated by camera and filter. Until now this only worked when ACP and NINA shared a machine, because the Python extension that did it opened the database from the ACP host.
- **Schema versions 23 to 28 are supported**, matching the Python extension. Anything outside that range is refused before a single row is written, with the same message the extension gives.
- **Sync for tonight now finishes the job.** The sequencer instruction and the dock button both load the matched plans into Target Scheduler, honouring the Everything and Only what fits modes, and report how many were loaded and what was left out and why.

### Changed

- `/api/gear` responses are read the way ACP actually sends them. Sensor size arrives as a two element `sensor_px` array, and the per filter capture settings under `cameras[].filters` are read for the first time. The Framing Assistant push was silently getting no sensor size from a real server and now gets one.

## 3.0.0 (unreleased)

ACP no longer has to run on the same machine as NINA, and you no longer have to tell the plugin what rig is connected. It works the gear out from the hardware and a plate solve.

### Added

- **Bearer token authentication.** The ACP options page gains an API token field for the value of `ACP_API_TOKEN` on the server. The token is stored in Windows Credential Manager, never in the plugin's settings file. Every request carries it, and a rejected token says "ACP rejected the token" in the dock rather than looking like the network is down.
- **Https server URLs** are accepted. Standard certificate validation applies, so a self signed certificate is refused.
- **Change polling.** The dock asks `GET /api/version` once a minute and refetches plans only when `plans_last_modified` moves, so a plan edited in ACP's web UI turns up without anyone pressing refresh.
- **A gear fingerprint** built from the connected camera, filter wheel, mount, site and a plate solve. The solved focal length comes from the pixel scale, which is the one number NINA's profile routinely gets wrong.
- **Profile focal length write-back.** When the plate solve says the focal length is more than 5 percent from what the profile claims, the profile is corrected, and the focal ratio with it when ACP knows the telescope's aperture. Every write logs the old and new values. This happens only from the Sync for tonight instruction and the Sync for tonight dock button, never from any other plate solve.
- **"ACP: Sync for tonight" sequencer instruction**, in a new ACP category. Optionally slews to a target, captures and solves a frame, builds the fingerprint, corrects the profile focal length, asks ACP which plans fit, and reports what it found. Loading those plans into Target Scheduler arrives in 3.1.
- **"Sync for tonight" dock button** doing the same from where the mount is already pointing, reusing a solve from the last hour rather than taking a fresh one.
- **A mode switch, "Which plans to load into Target Scheduler"**, with two values. Everything, the default, loads every plan and names the ones that do not suit tonight in a warning line. Only what fits tonight loads the matching plans and the ones with no gear set, for people running several rigs, sites or computers.

### Changed

- The connection probe uses `GET /api/version` and falls back to `/api/plans` against an older ACP.


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
