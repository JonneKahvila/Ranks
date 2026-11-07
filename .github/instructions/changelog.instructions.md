---
description: 'How to write CHANGELOG.md entries for this plugin. Trigger phrases: changelog, release notes, new release, version bump, what changed.'
applyTo: 'CHANGELOG.md'
---

# Changelog style

The changelog **is** the release body. `.github/workflows/release.yml` pulls the
section whose heading matches `PluginMetadata.Version` in `Ranks/src/Entrypoint.cs` and
publishes it verbatim, so write for the person reading the GitHub release page.

## Audience

A server admin deciding two things: *do I need this update*, and *what do I re-test
after installing it*. Not a reviewer, and not your future self debugging the fix.

## Shape

```markdown
## [1.1.0] - 2026-10-07

One-line summary, only when the release has a theme or a reason to hurry.

- What changed, from the admin's side.
- Another change.
```

- **Heading is `## [x.y.z] - YYYY-MM-DD`.** The version must equal
  `PluginMetadata.Version` exactly or the release fails — that check is deliberate.
- **Newest section first**, directly under the header comment.
- The optional lead sentence earns its place when it tells someone whether this
  release is for them: *"Worth updating if you run more than one server off one database."*
- Plain `-` bullets. No `Added` / `Changed` / `Fixed` sections, no bold lead-in labels.
- Wrap around 90 columns.
- Leave the `---` + "Older releases" footer at the bottom of the file.

## Content

1. **Outcome, not mechanism.** "Playtime stopped counting after a map change" — not
   which property the handler read. Internals belong in code comments.
2. **Name the symptom they saw.** The command, config key, cvar, table or error text,
   in `backticks`.
3. **One bullet per change.** If a bullet needs a semicolon and a subclause, it is two
   bullets, or it is too much detail.
4. **Call out anything that needs admin action** — a schema change, a renamed config
   key, a migration that runs on first boot.
5. **Say when a version was folded in**, e.g. *"Includes the work versioned 1.1.3,
   which was never released on its own."*
6. Link an issue as `([#N](url))` **only when that issue exists**. Never invent one.
7. Skip anything with no user-visible effect — refactors, comments, CI. A release with
   nothing worth saying does not need a release.

## Before committing

Read the bullets back as an admin. If one explains why the bug existed at the code
level, cut it to the symptom and the fix.

Then cut what the hand trims in `d2eb46c` and `1fc1fbe` removed every time
(`git show d2eb46c 1fc1fbe -- CHANGELOG.md`):

- behaviour that did not change — "still", "as before", "like every other statistic";
- reasons and mechanisms — "because", "so that", how the fix works;
- edge cases, second examples, and detail a wiki page already holds — link the page;
- a lead sentence that repeats a bullet;
- a consequence the instruction already implies — "until it exists, nothing is recorded".

A bullet is one or two lines, three with a link. For scale, 1.1.0 shipped seasons in six
bullets.
