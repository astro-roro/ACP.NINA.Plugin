using System.Reflection;
using System.Runtime.InteropServices;

[assembly: Guid("9F9EB062-B1CC-4622-A2FC-4362FE97CD08")]
[assembly: AssemblyTitle("Astro Coverage Planner (ACP)")]
[assembly: AssemblyDescription("See what you've imaged. Plan what's next. Push it to NINA.")]
[assembly: AssemblyCompany("astro-roro")]
[assembly: AssemblyProduct("Astro Coverage Planner (ACP)")]
[assembly: AssemblyCopyright("Copyright © 2026 Rohan Hinton")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]
[assembly: System.Runtime.Versioning.SupportedOSPlatformAttribute("windows")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

[assembly: AssemblyMetadata("MinimumApplicationVersion", "3.1.2.9001")]
[assembly: AssemblyMetadata("License", "MIT")]
[assembly: AssemblyMetadata("LicenseURL", "https://github.com/astro-roro/ACP.NINA.Plugin/blob/main/LICENSE.txt")]
[assembly: AssemblyMetadata("Repository", "https://github.com/astro-roro/ACP.NINA.Plugin/")]
[assembly: AssemblyMetadata("Homepage", "https://github.com/astro-roro/Astro-Coverage-Planner")]
[assembly: AssemblyMetadata("Tags", "Planning,Framing,TargetScheduler,Coverage,Mosaic")]
[assembly: AssemblyMetadata("LongDescription", @"NINA-side companion to [Astro Coverage Planner](https://github.com/astro-roro/Astro-Coverage-Planner). Pulls plans from a running ACP instance and pushes them into NINA's Framing Assistant and Target Scheduler.

ACP is the open-source coverage visualiser and planner for astrophotographers: scan your FITS/XISF archive, see what you've imaged on a sky map (coloured by telescope, badged by filter, with integration hours per target), find gaps, plan your next session or mosaic, and hand the plan to NINA. This plugin is the NINA-side surface — the ACP web UI does the planning, this plugin pushes the result into NINA.

## What it does (v1.0)

* **Push to Framing Assistant** — pick a plan from ACP's planner, push coordinates + rotation + mosaic geometry into NINA's Framing Wizard with one click.
* **Sync to Target Scheduler** — trigger ACP's bidirectional TS sync directly from NINA. No zip imports.

## Requirements

* NINA 3.1.2.9001 or newer.
* A running ACP instance reachable on `http://127.0.0.1:5555` (configurable). ACP must run on the same machine as NINA for v1.x — see ACP repo for setup.

## Status

v0.x scaffold — not yet shippable. The full ACP feature set (coverage maps, gap finder, public-survey overlays, friend manifests, NINA Target Scheduler sync engine) lives in the ACP repo.

## Links

* [ACP repository](https://github.com/astro-roro/Astro-Coverage-Planner)
* [Plugin source](https://github.com/astro-roro/ACP.NINA.Plugin)
* [Issues](https://github.com/astro-roro/ACP.NINA.Plugin/issues)

MIT licensed.")]

[assembly: ComVisible(false)]
