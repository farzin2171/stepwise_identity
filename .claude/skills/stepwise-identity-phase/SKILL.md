---
name: stepwise-identity-phase
description: House style for stepwise_identity, a from-scratch teaching port of Applications.IdentityGateway built one numbered phase at a time. Use whenever working in this repo — building a new phase, writing or updating a phase's README section, adding a test-phaseN.ps1 script, writing a docs/architecture/ doc, working in Mini.Infrastructure / Mini.UserService / Mini.AuthorizationService / Mini.AcmeApi, or comparing this sample's code against the real IdG/Apply/Services.User/Services.Authorization/ACME.API.Middleware.
---

# stepwise_identity phase conventions

This repo ports `Applications.IdentityGateway` (plus `Applications.Apply`,
`Services.User`, `Services.Authorization` and `ACME.API.Middleware` patterns) into a
small, from-scratch teaching sample, one numbered **Phase** at a time. See
[`CONTEXT.md`](../../../CONTEXT.md) for this project's own vocabulary (`Phase`,
`TenantContext`, `ExternalUserStore`, etc.) before using terms loosely.

## Every phase's README section includes

1. **`## Phase N — <title>`** heading — why this phase, what problem it solves.
2. The relevant `Config.cs`/`Program.cs` (or equivalent) snippet.
3. **An explicit comparison against the real IdG/Apply/User/Authorization counterpart** —
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
10. **Update `docs/architecture/`** for anything this phase changed that names more than
    one service — see "Where documentation goes" below. Not optional, and not deferred
    to a later documentation phase: an architecture doc written months after the fact
    loses the "things that broke" detail that makes it worth reading.
11. **If the phase adds branching decision logic, add a test case** to the single xunit
    project. Decision tables (which connector serves this tenant? which role provider
    answers first?) are documented far better by a table-driven test than by a
    black-box HTTP script. If the phase adds no branching logic, `test-phaseN.ps1` is
    sufficient on its own — don't add tests for their own sake.
12. Commit as `chore: phase NN`.

## Before starting a new phase

- Confirm the real counterpart's actual shape first (file paths, class names, config
  keys) — don't design a port from memory or assumption.
- Prefer the *smallest* faithful slice — this course consistently favors "the same
  types/behavior, simplified surface" over building a fully general framework.
- Check `CONTEXT.md` for whether this phase introduces or overloads any existing term;
  update it inline the moment a term resolves (see the `domain-modeling` skill). A term
  earns its `CONTEXT.md` entry when the code that uses it lands — `CONTEXT.md` describes
  what *exists*, never what's planned.

## Repo-wide rules

### Everything is built from scratch

No `DigitalInsuranceTools.*` package references, and no Equisoft NuGet feed. The sample
must stay buildable by anyone with nuget.org alone. When a real DIT library does the job
in production, port the *shape* by hand and link out — don't take the dependency.

### Shared concerns go in `Mini.Infrastructure`

A concern needed by more than one project belongs in `Mini.Infrastructure` (one csproj,
organised by folder), not copied into a fourth project. This rule exists because the repo
already grew two independent `TenantContext`/`Tenants`/`TenantResolutionMiddleware` copies
plus a third variation as `SampleApi`'s `IIdentityContext` before anyone noticed.

Split `Mini.Infrastructure` into multiple csprojs only when a phase genuinely forces it —
and if that happens, that's a "things that broke" entry, not a silent refactor. For *why*
the real `DIT.Connectors` is layered across four csprojs, link to the EqusoftInfra lesson
rather than re-explaining it here.

### Superseded artifacts are kept and marked, never deleted

When a phase replaces something an earlier phase built, the old thing stays in the tree
and stays in its original README section, marked superseded — a phase course's value *is*
the diff between phases, and deleting the predecessor retroactively erases what that
phase taught. `ExternalServicesStub` is the reference case: superseded by
`Mini.UserService`, still serving `test-phase7.ps1`, excluded from `run-all.ps1`'s default
set rather than removed.

The exception is a wholesale replacement with nothing left to compare against, where
keeping the old thing would only mislead — Phase 6's deletion of `Config.cs` in favour of
`IdentityServerConfig.json` was correct.

### Where documentation goes

| Location | Contains | Test |
| --- | --- | --- |
| `docs/reference/` | Vendor-neutral OAuth/OIDC protocol theory | Would this be true in a repo that had never heard of DIT? |
| `docs/architecture/` | Cross-cutting, current-state system docs | Does it name more than one of this repo's services? |
| `src/<Project>/docs/` | Wiring and configuration specific to one project | Does it only make sense inside that project? |
| `src/<Project>/README.md` | The phase-by-phase narrative | Does it describe the state *at phase N*? |

The last two rows are the drift risk: a phase README narrates *what it looked like then*
and must not be updated to current state, while an architecture doc describes *now* and
must be. When they disagree, the architecture doc is right about the present and the
phase README is right about the past. Don't "fix" a phase README to match.

### This sample's altitude

`stepwise_identity` answers *"which service calls which, carrying what token, and what
breaks."* The DIT library-internals course at `C:\MyWork\MyLearning\EqusoftInfra` answers
*"what's inside `AddConnectors()`."* Where they meet, link out — don't re-explain library
internals here. Relevant existing lessons: Series 4 (DIT.Connectors), Series 5 (DIT.Auth +
DIT.Authorization.Client), Series 7 (Redis / distributed caching), Series 2 (DIT.Identity).

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
