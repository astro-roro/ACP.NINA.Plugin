# Changelog

## 3.3.0 (release date)

The first release since 1.0.2. Versions 3.0 to 3.2 were built and tested but never published, so everything from them is in this release. ACP no longer has to run on the same machine as NINA, the Target Scheduler sync runs inside the plugin, and ACP hears about the night as it happens.

### Added

- **Bearer token authentication.** The ACP options page gains an API token field for the value of `ACP_API_TOKEN` on the server. The token is stored in Windows Credential Manager, never in the plugin's settings file. Every request carries it, and a rejected token says "ACP rejected the token" in the dock rather than looking like the network is down.
- **Https server URLs** are accepted. Standard certificate validation applies, so a self signed certificate is refused.
- **Change polling.** The dock asks `GET /api/version` once a minute and refetches plans only when `plans_last_modified` moves, so a plan edited in ACP's web UI turns up without anyone pressing refresh.
- **A gear fingerprint** built from the connected camera, filter wheel, mount, site and a plate solve, so you no longer tell the plugin what rig is connected. The solved focal length comes from the pixel scale, which is the one number NINA's profile routinely gets wrong. The fingerprint also carries `profile_id` and `wheel_slots`, in slot order, whether or not the wheel is connected, so ACP knows the wheel on a daytime sync.
- **Profile focal length write-back.** When the plate solve says the focal length is more than 5 percent from what the profile claims, the profile is corrected, and the focal ratio with it when ACP knows the telescope's aperture. Every write logs the old and new values. This happens only from the Sync for tonight instruction and button, never from any other plate solve.
- **"ACP: Sync for tonight" sequencer instruction**, in a new ACP category. It optionally slews to a target, captures and solves a frame, builds the fingerprint, corrects the profile focal length, asks ACP which plans fit, loads them into Target Scheduler and reports what it did.
- **"Sync for tonight" dock button** doing the same from where the mount is already pointing, reusing a solve from the last hour rather than taking a fresh one.
- **"Which plans to load into Target Scheduler"**, a mode switch with two values. Everything, the default, loads every plan and names the ones that do not suit tonight in a warning line. Only what fits tonight loads the matching plans and the ones with no gear set, for people running several rigs, sites or computers.
- **Solve attempts for Sync for tonight.** 3 by default, 1 to 20, set in the options. NINA's own plate solve attempts, meant for centring, no longer apply here: a failed sync solve usually means the cover is closed or the sky is gone, and ten tries just holds the camera.
- **The Target Scheduler push runs in the plugin.** It opens `schedulerdb.sqlite` itself and writes a project per ACP project name, a target per mosaic panel, exposure plans from the filter goals and exposure templates deduplicated by camera and filter. Until now this only worked when ACP and NINA shared a machine, because the Python extension that did it opened the database from the ACP host. Schema versions 23 to 28 are supported, and anything outside that range is refused before a single row is written.
- **Gear templates are written by id.** When a camera filter in `GET /api/gear` carries `templates`, the writer finds each template's Target Scheduler row by the id ACP recorded, then by guid, then by an unambiguous name. Each field is decided from ACP's value, the value at the last sync and Target Scheduler's value now: a change made only in ACP is written, a change made only in Target Scheduler is left for ACP to pick up, and a change made in both is held and reported. A rename updates the same row, so no second template appears. A template with no row is inserted under the wheel slot's name.
- **Row sub rules.** A plan's exposure follows its row: -1 when the row follows its template's sub, the row's own sub when it deviates. A plan moves to another template only when ACP changed which template the row uses, or the template lists the plan's template in `repoint_from`.
- **Acquired hours are reported back to ACP during a session.** When Target Scheduler starts or finishes a target, the plugin reads that target's acquired counts, converts them to hours using each exposure plan's sub length, and posts them to `POST /api/plans/<id>/progress`. The coverage map and the hours remaining stay current through the night. A five minute fallback runs while a Target Scheduler container is running, so a missed event costs at most five minutes of staleness. "Report progress to ACP while imaging" in the options turns it off, and the dock footer shows "Progress sent 22 s ago" or the last error.
- **Activity events.** The plugin posts what happens at the scope to ACP's Activity tab: Sync for tonight, imaging progress per target, autofocus runs, meridian flips, park and unpark, safety monitor changes, and session start and end. Events are queued in memory and sent in batches every 60 seconds and when the Target Scheduler container stops. A failed batch is tried again on the next tick, and an event ACP rejects is logged and not retried. The queue holds 500 events and drops plain information events first when it is full. After a NINA restart mid-night, imaging totals are rebuilt from Target Scheduler's acquired image table (read only). It needs an ACP with the Activity tab and an access token set, turns itself off for the NINA session after the first 404 or 403, and follows the "Report progress to ACP" option.
- **"Send TS to ACP" in the dock.** Plans made or changed in a rig's Target Scheduler can go back into ACP. The button copies Target Scheduler's database with SQLite's backup API, so committed rows still in the WAL file come along and row ids stay the same, then uploads the copy to `POST /api/ext/nina-ts-sync/import/uploads` with the stored token. The dock shows copying, the upload percentage, then ACP's counts and a "Review and apply in ACP" button. ACP shows what would change and nothing changes until Apply is pressed there. The copy is deleted whether the upload worked or not, and nothing on the rig is written. The button needs an ACP with the `nina-ts-sync` extension and an access token set. It checks the token before copying anything and stops with a matching dock line if it is missing or rejected.
- **Push state goes back to ACP after every sync.** After Sync for tonight or Sync All to TS, the plugin posts each pushed plan's TS row ids and snapshot, per-template results and `capabilities` of `templates` and `repoint` to `POST /api/ext/nina-ts-sync/links`, so ACP's record of this rig's last sync is current. If that post fails, the sync still counts and the dock says ACP was not told. The writer reads per-rig links (`ts_links[<profile>]`) before falling back to `ts_refs`, so a plan used on two rigs keeps each rig's rows. Sync All to TS now sends the fingerprint before it pushes, and the template list (with ids, guids, moon avoidance, twilight, humidity, dither columns and the plans using each) is posted before gear is read.
- **Every long dock action can be stopped.** While Sync for tonight, Sync All to TS or Send TS to ACP runs, its button reads "✕ Stop". Stop ends the capture and solve, and anything else before the Target Scheduler write, and leaves Target Scheduler and the profile as they were. Once a write has started it finishes, and the reports of what it wrote still go to ACP. Push to Framing finishes at once, so it has no Stop.
- **A time limit for ACP actions.** "Time limit for ACP actions" in the options, 10 minutes by default, 0 for none. A dock action that runs over stops as if Stop were pressed and says so. The Sync for tonight instruction fails with the same message, so the sequence's own error handling decides what happens next instead of the night hanging.

### Changed

- The dock's four buttons (Sync for tonight, Sync All to TS, Push to Framing, Send TS to ACP) are an equal 2x2 grid, more compact once an upload result is showing so the plan list keeps most of the dock's height. The result line spells out what is waiting for review, or says nothing is.
- A filter that is not on the wheel is skipped, not pushed with a warning. The rows are named in the push result ("SII is not on the wheel of Foo yet, so 3 rows were skipped"). An empty slot list means the wheel is unknown: templates Target Scheduler already has are used, and rows on a template it lacks are skipped.
- `/api/gear` responses are read the way ACP actually sends them. Sensor size arrives as a two element `sensor_px` array, and the per filter capture settings under `cameras[].filters` are read for the first time. The Framing Assistant push was silently getting no sensor size from a real server and now gets one.
- The connection probe uses `GET /api/version` and falls back to `/api/plans` against an older ACP.

### Fixed

- A sync no longer resets the moon, twilight, humidity and dither tuning on an exposure template. Those columns belong to Target Scheduler's own screens. ACP still owns profileId, name, filtername, guid, defaultexposure, gain, offset, bin and readoutmode. A new template still gets the current defaults on insert.
- A sync no longer resets a project's flats handling. The default for a new project is now flats on, and a change made in Target Scheduler survives the next push.
- The help text under "Report progress to ACP" no longer says a sync can walk Target Scheduler's counts backwards. Ordinary syncs never change acquired or accepted on a row Target Scheduler already has. The one write is AAC's accepted figures, skipped once Target Scheduler has saved frames since the night they cover.

### Notes

- Hours reported to ACP only ever go up. ACP refuses to lower a stored `actual_hours` unless forced, and this plugin never forces it.
- A mosaic reports the hours from its 1,1 panel and only that panel, matching the Python extension.
- The Target Scheduler database is opened read only for progress reporting and Send TS to ACP, which is why they are safe to run mid session when the push deliberately refuses to.
- Send TS to ACP uploads the whole database file for now. Sending just the rows as JSON is a planned clean-up.

## 1.0.2 (2026-09-05)

### Fixed

- The "Will sync to profile" label under the Sync All to TS button now follows profile switches. It used to show the profile NINA started with while the sync used the current one. Reported by the manifest maintainers in review.
- "Confirm before Sync to TS" is honoured. The option was saved but never checked, so the sync went straight through. It now asks first, and reads the option fresh each time rather than the value loaded at startup.

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
