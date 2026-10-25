# Issue tracker: Local Markdown

Issues and specs for this repo live as markdown files in `.scratch/`.

## Repo rules

- `.scratch/` is committed to `main` like any other file. `main` is never pushed: the public GitHub history is built from it without `.scratch/` (`scripts/publish-tree.ps1`, ADR-0023).
- Ticket text follows CLAUDE.md's hard rules: no game data, generated code or logs pasted in; link to `analysis/` paths instead.

## Conventions

- One feature per directory: `.scratch/<feature-slug>/`
- The spec is `.scratch/<feature-slug>/spec.md`
- Implementation issues are one file per ticket at `.scratch/<feature-slug>/issues/<NN>-<slug>.md`, numbered from `01` — never a single combined tickets file
- Triage state is recorded as a `Status:` line near the top of each issue file (see `triage-labels.md` for the role strings)
- Comments and conversation history append to the bottom of the file under a `## Comments` heading

## Writing a spec or tickets

- **What the player sees.** For each player-facing option, the spec says what the player
  sees and hears when it is on: what shows, for how long, and what changes from the Xbox 360.
  The owner agrees to that before any ticket is `ready-for-agent`. The research behind the
  spec usually already knows it (a skip the game takes only 1 s in shows 1 s of every
  cinematic).
- **Testing game behaviour.** A ticket plans at most one short scripted game run per
  setting, as proof it works. Wider coverage (other arenas, match types, modes, online) is a
  `ready-for-human` check: the owner plays it by hand. Unit tests, builds and the launcher
  tests are always fine.
- **Prototypes.** A prototype script a spec rests on is committed under `scripts/` and named in
  its research doc, never left in a session scratchpad: tickets and reviews read it for details
  the doc doesn't record.

## When a skill says "publish to the issue tracker"

Create a new file under `.scratch/<feature-slug>/` (creating the directory if needed).

## When a skill says "fetch the relevant ticket"

Read the file at the referenced path. The user will normally pass the path or the issue number directly.

## Wayfinding operations

Used by `/wayfinder`. The **map** is a file with one **child** file per ticket.

- **Map**: `.scratch/<effort>/map.md` — the Notes / Decisions-so-far / Fog body.
- **Child ticket**: `.scratch/<effort>/issues/NN-<slug>.md`, numbered from `01`, with the question in the body. A `Type:` line records the ticket type (`research`/`prototype`/`grilling`/`task`); a `Status:` line records `claimed`/`resolved`.
- **Blocking**: a `Blocked by: NN, NN` line near the top. A ticket is unblocked when every file it lists is `resolved`.
- **Frontier**: scan `.scratch/<effort>/issues/` for files that are open, unblocked, and unclaimed; first by number wins.
- **Claim**: set `Status: claimed` and save before any work.
- **Resolve**: append the answer under an `## Answer` heading, set `Status: resolved`, then append a context pointer (gist + link) to the map's Decisions-so-far in `map.md`.
