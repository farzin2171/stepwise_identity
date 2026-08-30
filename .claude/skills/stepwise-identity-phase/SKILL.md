---
name: stepwise-identity-phase
description: House style for stepwise_identity, a from-scratch teaching port of Applications.IdentityGateway built one numbered phase at a time. Use whenever working in this repo — building a new phase, writing or updating a phase's README section, adding a test-phaseN.ps1 script, or comparing this sample's code against the real IdG/Apply/Services.Authorization.
---

# stepwise_identity phase conventions

This repo ports `Applications.IdentityGateway` (plus `Applications.Apply` and
`Services.Authorization` patterns) into a small, from-scratch teaching sample, one
numbered **Phase** at a time. See [`CONTEXT.md`](../../../CONTEXT.md) for this project's
own vocabulary (`Phase`, `TenantContext`, `ExternalUserStore`, etc.) before using terms
loosely.

## Every phase's README section includes

1. **`## Phase N — <title>`** heading — why this phase, what problem it solves.
2. The relevant `Config.cs`/`Program.cs` (or equivalent) snippet.
3. **An explicit comparison against the real IdG/Apply/Authorization counterpart** —
   cite real file paths, not just "the real system does X." If you don't know the real
   counterpart's actual shape, say so and go look (read the real repo, or dispatch a
   sub-agent) rather than guessing.
4. **"Where this sample simplifies"** callout(s) — be honest about what's a genuine
   simplification vs. what's a different-in-kind design (see Phase 3's
   `IdentityServerHost/README.md` for the model: a table of "real IdG" vs. "this
   sample").
5. **"Things that broke, and why they're worth knowing"** — real bugs hit while
   building this phase, reproduced by actually running it. Not hypothetical gotchas.
6. Update **"What's deliberately missing"** at the bottom of the README: remove what
   this phase just did, add whatever newly surfaced as missing.
7. Keep the roadmap comment block in sync in **both** the root `README.md` and the
   sub-project's own `README.md`:
   ```
   1. Foundation ✓
   2. Clients ✓ (MVC + React)
   ...
   N. <this phase> ← next
   ```
8. A matching **`test-phaseN.ps1`** at the repo root — raw HTTP, no browser, reusing the
   existing helper pattern (`NewClient` for a cookie-container `HttpClient` with
   redirects disabled, `Follow` to drive the redirect chain by hand, the PKCE
   verifier/challenge helpers). Look at `test-phase3.ps1`/`test-phase4.ps1` before
   writing a new one — don't reinvent the helpers.
9. A **"Try it yourself"** section suggesting a concrete experiment (break one thing on
   purpose, observe what actually happens).
10. Commit as `chore: phase NN`.

## Before starting a new phase

- Confirm the real counterpart's actual shape first (file paths, class names, config
  keys) — don't design a port from memory or assumption.
- Prefer the *smallest* faithful slice — this course consistently favors "the same
  types/behavior, simplified surface" over building a fully general framework.
- Check `CONTEXT.md` for whether this phase introduces or overloads any existing term;
  update it inline the moment a term resolves (see the `domain-modeling` skill).

## Known pre-existing gotcha (fixed in Phase 5, worth knowing about)

Phase 08 migrated every project from `http://` to `https://` launch URLs but didn't
update every `.ps1` script or doc reference to match — Phase 5 fixed the ones that broke
verification at the time (all `test-phase*.ps1` scripts), but if a *doc* still shows a
`http://localhost:50XX` URL, it's leftover drift from that same migration, not a new bug.
Fix it the same way: match whatever `launchSettings.json` actually pins for that project
(`5001` IdentityServerHost, `5011` ExternalIdp, `5006` MvcClient, `5007` SampleApi, `5173`
ReactSpa, all HTTPS except ReactSpa) — except inside a section that's explicitly
*narrating what a past phase looked like at the time* (e.g. a "here's what broke and
why" retrospective), where the old value is the point and shouldn't be changed.
