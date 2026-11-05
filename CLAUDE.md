# ACP NINA plugin

A NINA plugin that connects NINA and its Target Scheduler to Astro Coverage Planner (ACP). The solution is `ACP.NINA.Plugin.sln`: the plugin in `ACP.NINA.Plugin/` and xunit tests in `ACP.NINA.Plugin.Tests/`. It targets .NET 8 on Windows. Build with `dotnet build ACP.NINA.Plugin.sln -c Release` and test with `dotnet test ACP.NINA.Plugin.sln`.

## Keeping private details out

This is a public repository, so nothing private goes into code, tests, fixtures, docs, commit messages or PR text. That covers real host and machine names, home network addresses and paths, NAS share names, personal identities and emails, real NINA profile ids, private science targets and collaborators, private pipelines and their names, and snapshots of real databases or gear lists. Use placeholders instead: RIG-A and LAPTOP for machines, "the home server" for servers, `/data/archive` for paths, SNR-A for targets, "an external grader" or "a processing pipeline" for pipelines, and `00000000-0000-4000-8000-000000000002` for a profile id. Test fixtures are generated from made-up data and never copied from a live database. Commits use the GitHub noreply identity. A private term list outside the repository backs this up: `scripts/check_private_terms.py` checks added lines and commit messages in CI and in the hooks under `.githooks/`, and it never prints the term it found.

Maintainers may keep private notes outside the repository. If `../acp-private/BOUNDARIES.md` exists next to the checkout, read it before writing, committing or pushing.
