# Astro Coverage Planner (ACP) for NINA

The NINA side of [Astro Coverage Planner](https://github.com/astro-roro/Astro-Coverage-Planner). It reads your plans from ACP and puts them to work in NINA: it loads a target into the Framing Assistant, writes your plans into [Target Scheduler](https://tcpalmer.github.io/nina-scheduler/), and reports what you have imaged back to ACP while the night runs.

You can use it with or without Target Scheduler. Push to Framing needs nothing else. The Target Scheduler buttons need the Target Scheduler plugin installed in NINA.

## What it does

The plugin adds a dock panel called **Astro Coverage Planner** to NINA's Imaging tab. It lists every plan in ACP with its project, target, filter goals, mosaic shape and gear. The panel checks ACP once a minute, so a plan you change in ACP turns up without a refresh.

The panel has these buttons:

- **Push to Framing.** Loads the selected plan into the Framing Assistant with its coordinates, rotation, mosaic layout (rows, columns and overlap), camera dimensions and focal length. Save the framing as a sequencer target and image it however you like.
- **Sync All to TS.** Writes every plan in ACP into Target Scheduler for the active NINA profile. The plugin does the write itself, so ACP can run on another machine. It creates a project per ACP project, a target per mosaic panel, exposure plans from your filter goals, and exposure templates for each camera and filter.
- **Sync for tonight.** Does the start of the night in one click. It solves a frame from where the mount is pointing, works out the connected gear from the solve, asks ACP which plans fit, and loads them into Target Scheduler. If the solved focal length is more than 5 percent from your profile's, it corrects the profile (you can turn that off in the options).
- **Stop.** While Sync for tonight or Sync All to TS runs, its button reads **✕ Stop**. Stop ends the run before anything is written to Target Scheduler and leaves Target Scheduler and your profile as they were. Once the write has started it finishes. Push to Framing is instant, so it has no Stop.

Two more things happen without a button:

- **Live progress.** While Target Scheduler images, the plugin tells ACP how many hours each target has acquired, so the coverage map and the hours remaining stay current through the night. It reports when Target Scheduler starts or finishes a target, and at least every five minutes while a container runs. The dock footer shows "Progress sent 22 s ago", or the last error.
- **Activity.** The plugin also posts what happens at the scope to ACP's Activity tab: Sync for tonight, imaging progress per target, autofocus runs, meridian flips, park and unpark, safety monitor changes, and session start and end. This needs an ACP that has the Activity tab and an API token set. If ACP answers with a 404 or 403, the plugin stops posting until you restart NINA.

Both are controlled by **Report progress to ACP while imaging** in the options, which is on by default.

### Sync for tonight in a sequence

The same step is available as a sequencer instruction, **ACP: Sync for tonight**, in the ACP category. Put it before your Target Scheduler container, not inside it: the plugin writes nothing to Target Scheduler while a container is running. It can optionally slew to a target first.

### Send TS to ACP

The dock also has a **Send TS to ACP** button, which sends a read-only copy of Target Scheduler's database to ACP so you can review plans made in Target Scheduler. ACP shows what would change, and nothing changes until you press Apply there. The button needs the `nina-ts-sync` extension in ACP, which ACP bundles (see [ACP's README](https://github.com/astro-roro/Astro-Coverage-Planner#readme)), and the ACP API token set in the plugin options.

## Requirements

- **NINA 3.1.2.9001 or newer.**
- **A running Astro Coverage Planner server.** It can run on the same computer as NINA, or on another one you reach over your home network or a VPN such as Tailscale.
- **Target Scheduler**, only for Sync All to TS and Sync for tonight. The plugin supports Target Scheduler database schema versions 23 to 28 and refuses to write to anything else.

## Installation

### From NINA

Open **Options → Plugins → Available**, find **Astro Coverage Planner (ACP)** and install it. Restart NINA when it asks.

### Manual install

1. Download `ACP.NINA.Plugin-v<version>.zip` from the [latest release](https://github.com/astro-roro/ACP.NINA.Plugin/releases/latest).
2. Extract the contents into `%localappdata%\NINA\Plugins\3.0.0\ACP.NINA.Plugin\` (create the folder if needed).
3. Restart NINA. The plugin appears under **Options → Plugins → Installed**.

### Add the dock panel

Open NINA's Imaging tab and add the **Astro Coverage Planner** panel to your layout from the dock panel menu.

## Connecting to ACP

1. Open **Options → Plugins → Astro Coverage Planner (ACP)**.
2. Set **Server URL** to where ACP runs. The default is `http://127.0.0.1:5555`, which is ACP on the same computer. For another machine use its address, for example `http://192.168.1.20:5555`. `https://` addresses work, with normal certificate checks, so a self-signed certificate is refused.
3. If you set `ACP_API_TOKEN` on the ACP server, paste the same value into **API token**. The plugin stores it in Windows Credential Manager, not in its settings file. **Clear** removes it. If ACP runs on the same computer with no token set, leave the field blank.
4. Click **Test**.

The status line at the top of the dock panel then reads "Connected" with the server address. If the token is wrong, the dock says "ACP rejected the token" rather than a generic network error.

Do not port-forward ACP to the Internet. For a remote site, reach it over a VPN and set a token on both sides.

## Using it

### Frame a target

1. Pick a plan in the dock panel.
2. Click **Push to Framing**.
3. Open the Framing Assistant. The target is loaded with its coordinates, mosaic and rotation.
4. Save it as a sequencer target.

### Load plans into Target Scheduler

1. Check that the "Will sync to profile" line in the dock shows the NINA profile you want. It follows the active profile.
2. Click **Sync All to TS** to load every plan, or **Sync for tonight** to solve first and load the plans that suit tonight's gear.
3. The line under the buttons reports what was written, and what was skipped and why. For example, a plan on a filter that is not on your wheel is skipped and named.

A sync never lowers the acquired or accepted counts on a row Target Scheduler already has. Settings you change in Target Scheduler's own screens, such as moon avoidance, twilight, humidity and dither on a template, survive the next sync.

Which plans **Sync for tonight** loads is set by **Which plans to load into Target Scheduler**: *Everything* (the default) loads every plan, and *Only what fits tonight* loads the plans that match the gear the solve found. Sync All to TS always loads everything.

## Options

- **Server URL** and **API token**, as above.
- **Which plans to load into Target Scheduler.** Everything, or only what fits tonight.
- **Update the profile focal length from the plate solve.** On by default. Applies to Sync for tonight only.
- **Report progress to ACP while imaging.** On by default. Turn it off if you already sync acquired hours with ACP's Python extension.
- **Auto-refresh plans every N seconds.** Off by default.
- **Confirm before Sync to TS.** Off by default.
- **Solve attempts for Sync for tonight.** 3 by default, 1 to 20. NINA's own plate solve settings are left alone.
- **Time limit for ACP actions.** 10 minutes by default, 0 for none. An action that runs over stops as if you had pressed Stop.

## Troubleshooting

- **The plugin is not in the Installed list.** Restart NINA fully. Plugins load once at startup.
- **The dock says "Not connected" or stays on "Probing...".** ACP is not reachable. Open the Server URL in a browser from the NINA computer, check the URL in the options, then click **Test**.
- **"ACP rejected the token".** The token in the options does not match `ACP_API_TOKEN` on the server, or the server expects one and you left it blank.
- **Sync for tonight reports a failed solve.** It usually means the lens cover is on or the sky is cloudy. It tries 3 frames by default; raise that in the options if you need more.
- **A sync is refused because Target Scheduler is running.** Stop the Target Scheduler container first. The plugin does not write to the database while it is in use.
- **Anything else.** Open an issue with the relevant part of `%localappdata%\NINA\Logs\<latest>.log`. Search for `ACP:` to find the plugin's entries.

## Build from source

```
dotnet build ACP.NINA.Plugin.sln -c Release
```

Debug builds copy themselves to `%localappdata%\NINA\Plugins\3.0.0\ACP.NINA.Plugin\` after building. Restart NINA to pick up changes. See `RELEASING.md` for the release process.

## Changelog and licence

See `CHANGELOG.md` for what changed in each version. MIT licence, see `LICENSE.txt`.
