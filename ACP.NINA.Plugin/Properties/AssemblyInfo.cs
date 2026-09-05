using System.Reflection;
using System.Runtime.InteropServices;

[assembly: Guid("9F9EB062-B1CC-4622-A2FC-4362FE97CD08")]
[assembly: AssemblyTitle("Astro Coverage Planner (ACP)")]
[assembly: AssemblyDescription("See what you've imaged. Plan what's next. Push it to NINA.")]
[assembly: AssemblyCompany("Astro With RoRo")]
[assembly: AssemblyProduct("Astro Coverage Planner (ACP)")]
[assembly: AssemblyCopyright("Copyright © 2026 Astro With RoRo")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]
[assembly: System.Runtime.Versioning.SupportedOSPlatformAttribute("windows")]

// TargetSchedulerDb keeps its SQLite connection internal so nothing outside
// the assembly can write to schedulerdb.sqlite around the schema version gate.
// The tests need it to check what actually landed in the tables.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("ACP.NINA.Plugin.Tests")]

[assembly: AssemblyVersion("3.2.0.0")]
[assembly: AssemblyFileVersion("3.2.0.0")]

[assembly: AssemblyMetadata("MinimumApplicationVersion", "3.1.2.9001")]
[assembly: AssemblyMetadata("License", "MIT")]
[assembly: AssemblyMetadata("LicenseURL", "https://github.com/astro-roro/ACP.NINA.Plugin/blob/main/LICENSE.txt")]
[assembly: AssemblyMetadata("Repository", "https://github.com/astro-roro/ACP.NINA.Plugin/")]
[assembly: AssemblyMetadata("Homepage", "https://github.com/astro-roro/Astro-Coverage-Planner")]
[assembly: AssemblyMetadata("Tags", "Planning,Framing,TargetScheduler,Coverage,Mosaic")]
[assembly: AssemblyMetadata("LongDescription", @"NINA-side companion to [Astro Coverage Planner](https://github.com/astro-roro/Astro-Coverage-Planner). Pulls plans from a running ACP instance and pushes them into NINA's Framing Assistant and Target Scheduler.

ACP is the open-source coverage visualiser and planner for astrophotographers: scan your FITS/XISF archive, see what you've imaged on a sky map (coloured by telescope, badged by filter, with integration hours per target), find gaps, plan your next session or mosaic, and hand the plan to NINA. This plugin is the NINA-side surface. The ACP web UI does the planning, this plugin pushes the result into NINA.

## What it does

* **Sync for tonight.** One sequencer instruction and one dock button. Solves a frame, works out from the solve what camera, filters, mount and focal length are actually connected, corrects the profile focal length when it is more than 5 percent out, and asks ACP which of your plans fit tonight's rig. You never have to tell it what rig is attached.
* **Push to Framing Assistant.** Pick a plan from ACP's planner and push coordinates, rotation and mosaic geometry into NINA's Framing Wizard with one click.
* **Sync to Target Scheduler.** Trigger ACP's bidirectional TS sync directly from NINA. No zip imports.
* **Two ways to work.** Load every plan in ACP, or only the ones that fit the gear the plate solve found. One setting, and Everything is the default.

## Requirements

* NINA 3.1.2.9001 or newer.
* A running ACP instance. It can be on this machine or on another one reachable over your LAN or a VPN such as Tailscale. Set an API token on both sides when it is elsewhere, and do not port forward ACP to the Internet.

## Links

* [ACP repository](https://github.com/astro-roro/Astro-Coverage-Planner)
* [Plugin source](https://github.com/astro-roro/ACP.NINA.Plugin)
* [Issues](https://github.com/astro-roro/ACP.NINA.Plugin/issues)

MIT licensed.")]

[assembly: ComVisible(false)]
