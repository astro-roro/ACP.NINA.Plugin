# Astro Coverage Planner (ACP) — NINA Plugin

NINA-side companion to [Astro Coverage Planner](https://github.com/astro-roro/Astro-Coverage-Planner). Pulls plans from a running ACP instance and pushes them into NINA's Framing Assistant and Target Scheduler.

> **Status:** v0.x scaffold. Not yet shippable. See `CHANGELOG.md` for release notes.

## Requirements

- **NINA 3.1.2.9001 or newer.**
- **A running ACP instance** reachable on `http://127.0.0.1:5555` (configurable). For v1.x, ACP must run on the same machine as NINA.

## What it does (v1.0)

- **Push to Framing Assistant** — pick a plan from ACP's planner, push coordinates + rotation + mosaic geometry into NINA's Framing Wizard with one click.
- **Sync to Target Scheduler** — trigger ACP's bidirectional TS sync directly from NINA. No zip imports, no UI faff.

For the full ACP feature set (coverage maps, gap finder, NINA Target Scheduler sync engine, public-survey overlays, friend manifests), see the main ACP repo.

## Build

```
dotnet build ACP.NINA.Plugin.sln -c Release
```

Output is copied automatically to `%localappdata%\NINA\Plugins\3.0.0\ACP.NINA.Plugin\`. Restart NINA to pick up changes.

## License

MIT — see `LICENSE.txt`.
