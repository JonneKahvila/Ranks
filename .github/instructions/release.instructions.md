---
description: 'Building, packaging and publishing releases: the CreateZip target, the release workflow, versions and changelog coupling. Trigger phrases: build, publish, release, workflow, zip, version bump, tag.'
applyTo: '.github/workflows/*.yml'
---

# Build & Release

> Part of the Ranks instruction set. `§N` cross-references resolve through the table in [`copilot-instructions.md`](../copilot-instructions.md), which is always loaded; `docs/` references are to the separate requirements and implementation guides.

### Build

```powershell
dotnet build Ranks/Ranks.csproj -c Release
dotnet publish Ranks/Ranks.csproj -c Release
```

`Ranks.csproj` emits `build/publish/Ranks/` and zips it to
`build/Ranks-v<version>.zip` via the `CreateZip` target, which reads
`PluginMetadata.Version` out of `Ranks/src/Entrypoint.cs` with the `ExtractPluginVersion`
inline task. The version lives in exactly one place; nothing else needs stamping.

### Cutting a release

`.github/workflows/release.yml` runs on `workflow_dispatch`, or on a push to `main`
whose commit message contains `[release]`. It takes no inputs.

**A run produces exactly one release: `v<version>`, titled `Ranks | v<version>`, with
five zips attached** — core plus all four modules, every one built from that same
commit. That is the whole shape; there is deliberately no per-module release and no
chooser. Attaching the modules to core's release is what stops an operator having to
work out which module zip goes with which core version.

The version, the notes and the tag all come from **core's** `CHANGELOG.md` and must
match `PluginMetadata.Version` in `Ranks/src/Entrypoint.cs`. Modules are versioned
independently and their zips are collected by glob, so a module keeps its own version
in its filename but never gets a tag or a release of its own.

An existing tag **fails** the run rather than overwriting it, which is what makes an
accidental re-release loud. So a release needs **both** a version bump and a matching
changelog section — a missing section fails by design. Style rules live in
`.github/instructions/changelog.instructions.md`.

`docs/verify-release-workflow.sh` runs that logic against the real repo with `gh`
stubbed (`docker run --rm -v "${PWD}:/src:ro" debian:bookworm-slim`), which is the
only way to exercise it without publishing something.

### Install layout

```
addons/swiftlys2/plugins/Ranks/
  Ranks.dll
  resources/
    translations/
    exports/Ranks.Contract.dll
```

Configs are generated on first load into the plugin's config directory.

### Verifying the contract actually ships

The export is gitignored and build-generated, so the failure is CI-only and
silent. Simulate a clean checkout — deleting build state is not enough, the
gitignored artifact must go too:

```powershell
Remove-Item 'Ranks/resources/exports','Ranks/build','Ranks/obj' -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish Ranks/Ranks.csproj -c Release
Test-Path 'Ranks/build/publish/Ranks/resources/exports/Ranks.Contract.dll'
```

Always run the control too — revert the packaging fix and confirm it **fails** —
otherwise you cannot tell the fix from a warm-cache effect.

### Release checklist

- [ ] Version bumped in `[PluginMetadata]` **and** `.csproj`
- [ ] `Ranks.Contract.dll` present in the publish output
- [ ] No `FluentMigrator*.dll` in any `resources/exports/` of the publish output
- [ ] `en.jsonc` complete; contributed translations key-complete or omitted
- [ ] Migrations verified on MySQL, PostgreSQL and SQLite — fresh and existing
- [ ] Breaking contract change → key bumped to `.v2`, `.v1` still registered
- [ ] `CHANGELOG.md` has a `## [x.y.z]` section matching the new version

---

