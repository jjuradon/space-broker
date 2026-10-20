# Software Factory: Project Plan

**Version:** 1.1 (proposed refinement of the 1.0 frozen baseline)
**Date:** 2026-10-20
**Status:** Ready for Phase 0 (probes gate Phase 1)
**Primary surfaces:** VS Code Agents window, Copilot harness (primary); Copilot CLI (secondary) [R1]

---

## 1. What This Project Is

A **software factory** for the GitHub Copilot ecosystem: a request goes in, a reviewed and committed implementation comes out.

The factory is a **thin, deterministic orchestration layer** inspired by two reference designs:

- **Matt Pocock's Sandcastle**: role-based pipelines with per-agent branch/sandbox isolation.
- **Uncle Bob's SwarmForge**: six-role pipeline with durable, commit-backed JSON handoffs.

Neither reference is copied. The factory is built only on **documented, non-experimental, version-pinned** primitives: user-level custom agents, an optional skill, git worktrees, driver-authored JSON handoffs, and a Python driver and dashboard. [R4]

### 1.1 The one-sentence goal

> Given a feature request or bug fix, run it through a role-separated pipeline of Copilot agents, each in its own git worktree chained from the previous role's commit, each recorded as a driver-written, git-backed JSON handoff, and produce a reviewed, committed, mergeable result branch with a post-run dashboard. [R6][R8][R9]

---

## 2. Intended Use

### 2.1 Primary use: VS Code Agents window

1. Select the **Copilot** harness in Session Target and the `factory-orchestrator` agent. [R1]
2. Prompt, for example:
   - `Implement requests/add-oauth.md using the six-pack workflow.`
   - `Fix the null check in src/utils/parse.ts using the two-pack workflow.`
3. The orchestrator states the chosen pack and reason, then runs `factory run --request … --pack …`. [R5][R18]
4. Six-pack: when the specifier finishes, the driver exits with code 20 (`awaiting_approval`). Run `factory approve --run-id <id>` (or `--reject --reason …`), then `factory resume --run-id <id>`. [R5]
5. Inspect with `factory dashboard --run-id <id>`.

### 2.2 Secondary use: Copilot CLI

```bash
factory run --request requests/add-oauth.md --pack six-pack      # --pack is required [R18]
factory run --request requests/fix-null.md  --pack two-pack
factory approve   --run-id <id>
factory resume    --run-id <id>
factory clean     --run-id <id>
factory dashboard --run-id <id> [--json]
```

### 2.3 Workflows

| Pack | Roles | When |
|---|---|---|
| **Two-pack** | `coder → cleaner` | Single-file fix, localized bug, small test addition |
| **Six-pack** | `specifier → coder → cleaner → architect → hardender → qa` | New feature, multi-module change, public API or architecture |

Pack order lives in one driver-owned file, `packs.json`. It is not duplicated in agent profiles. [R11]

**Classification:** the orchestrator proposes a pack. The CLI never classifies, so `--pack` is mandatory. If neither pack fits (empty or unreadable request, non-code task), the orchestrator replies `no_pack` with a reason and runs nothing. A wrong pack is corrected by re-running with the other pack. A two-pack role can stop with `blocked/escalate_pack`. [R18]

### 2.4 Multi-repo

Config lives at user level:

```
~/.copilot/
├── agents/   (factory-orchestrator, specifier, coder, cleaner, architect, hardender, qa .agent.md)
└── skills/software-factory/ (SKILL.md, CONSTITUTION.md, HANDOFF-SCHEMA.md)
```

Agent filename must equal frontmatter `name`; no symlinked agent or skill directories. [R2] The skill is optional: the pipeline must work without it. [R3] Repos only need Git worktree capability and a writable parent directory.

---

## 3. Goals

### 3.1 Primary goals

- **G1 Request-to-result pipeline** with no manual orchestration in the happy path (the approval gate is the one deliberate stop).
- **G2 Role separation that is declared, constrained and audited.** Tool lists are declared per profile. `execute` is shell access and can write, so restrictions are backed by `--deny-tool` flags, a prompt boundary and a driver post-role audit. [R7]
- **G3 Durable, git-backed handoffs.** Every transition is a JSON file the driver commits to a driver-owned `_handoffs` repo. [R8]
- **G4 Session isolation via git worktrees**, one per role, chained from the previous role's commit. [R9]
- **G5 Two operational surfaces** sharing the same user-level agent definitions.
- **G6 Post-run observability** from disk artifacts alone.

### 3.2 Secondary goals (later phases)

G7 real-time dashboard; G8 Node front end on the stable JSON contract; G9 deeper VS Code Agents window integration.

### 3.3 Explicit non-goals (v1)

- No experimental Copilot features (`/fleet`, dynamic workflows, experimental subagent properties).
- No VS Code `handoffs` frontmatter and no agent-to-agent delegation; the driver is the only dispatcher. [R5]
- No container/sandbox isolation; no OS-level sandbox (the CLI has none that the docs claim). [R7]
- No hidden retry engine; resume is an explicit human command. [R15]
- No per-repository configuration.
- No custom GUI in v1.

---

## 4. Requirements

### 4.1 Functional requirements

| ID | Requirement |
|---|---|
| F1 | The orchestrator classifies a request and proposes two-pack or six-pack (or `no_pack`). The CLI requires `--pack`. [R18] |
| F2 | Each role is dispatched by the driver, lazily, in its own worktree created from the previous role's commit (first role from `base_commit`). [R9] |
| F3 | Each role attempt yields exactly one JSON handoff, **written by the driver** from the role's final JSON report. Fields are in §5.2. [R6][R11] |
| F4 | A `completed` handoff must reference a commit reachable on the role branch. Edit roles: the driver commits residual changes, or uses an advanced HEAD, and a clean unchanged tree is `failed/no_changes`. Read-only roles: the tree must be clean and `commit` equals the start SHA. [R6] |
| F5 | `blocked` requires a coded reason. The driver stops (exit 10) and leaves worktrees. |
| F6 | `needs_approval` (specifier) stops the run with exit 20. `factory approve` records `approvals/<step>.json`. `factory resume` continues. [R5] |
| F7 | `state.json` is a pure function of `run.json`, `events.jsonl`, `_handoffs/` and `approvals/`, regenerated on every driver step. Agents never write it. [R12] |
| F8 | At completion the driver removes worktrees with `git worktree remove`. A dirty worktree records `cleanup: dirty` and the driver stops without `--force`. `factory clean` is the explicit, lossless remedy. [R16] |
| F9 | The dashboard reconstructs the timeline from `state.json` and handoffs; `--json` emits the contract. |
| F10 | The skill loads on matching requests where the surface supports it. It is non-load-bearing. [R3] |
| F11 | Any status outside `completed/blocked/needs_approval`, a missing or invalid report, a crash, a timeout, or an audit violation yields `failed` with a `failure_class` (exit 30). [R14] |
| F12 | `factory resume` is idempotent: it skips completed steps, marks worktrees without a handoff `interrupted`, and starts a new attempt without deleting old branches. [R13][R15] |

### 4.2 Non-functional requirements

| ID | Requirement |
|---|---|
| N1 | Only documented, non-experimental primitives: `copilot --agent`, `-p`, user-level agent and skill discovery. No `--experimental`. CLI and Git versions are pinned after Phase 0. [R4] |
| N2 | Config is user-level under `~/.copilot/`; repos are not modified beyond worktree capability. |
| N3 | Run root: `<repo-toplevel>/../.factory/<repo-name>/<run-id>/` containing `run.json`, `events.jsonl`, `state.json`, `_handoffs/`, `approvals/`, `wt/<role>/`. [R10] |
| N4 | `_handoffs/` is a sibling of `wt/`, outside every working tree, and is a driver-owned git repo with one commit per handoff. Agents never read or write it; the driver injects the prior handoff into the next prompt. [R8] |
| N5 | Driver and dashboard in Python. `state.json` and handoffs carry `schema_version`; changes are additive-only; JSON Schemas ship with the driver. |
| N6 | Agents never manage worktrees, branches or `state.json`. The driver owns lifecycle and audits compliance after each role. [R7] |
| N7 | Branches are `factory/<run-id>/<role>` (aborted retries `…/<role>-a<n>`). [R9] |
| N8 | Run-id is `YYYYMMDDTHHMMSSZ-<4hex>`; the run directory is claimed with an atomic `mkdir`. [R10] |
| N9 | Driver exit codes: 0 completed, 10 blocked, 20 awaiting approval, 30 failed, 40 preflight failure. [R14] |

### 4.3 Environment requirements

| ID | Requirement |
|---|---|
| E1 | Copilot CLI at or above the minimum version recorded in Phase 0.3 (the version where `~/.copilot/agents` user agents load and `--agent` works headless). Preflight checks it. [R2] |
| E2 | VS Code with the **Copilot harness** selected in Session Target; user agents live in `~/.copilot/agents`. The `chat.agentFilesLocations` setting is not used (deprecated, Local harness only). [R1] |
| E3 | Git with `worktree add --lock`, `list --porcelain`, `remove`, `repair`, `prune`; the minimum version is set in Phase 0.3, not assumed. [R21] |
| E4 | Python 3.10+ with `rich` and a JSON-schema validator. |
| E5 | The repo's parent directory is writable and allows worktrees outside the repo. |

---

## 5. Architecture

### 5.1 Components

```
VS Code Agents window (Copilot harness)      Copilot CLI
              │                                   │
              ▼                                   ▼
 factory-orchestrator.agent.md  (read, search, execute: runs only `factory …`)
   classify → propose pack → run `factory run --pack …`
              │
              ▼
 factory.py  (the only dispatcher)
   creates run dir + run.json, worktrees (lazy, chained), dispatches roles,
   writes handoffs, audits, commits, derives state.json, tears down
      │ writes                              │ calls (cwd = role worktree)
      ▼                                     ▼
 _handoffs/ (git repo), approvals/,   copilot --agent=<role> -p <prompt> -s
 events.jsonl, state.json             + explicit --allow-tool / --deny-tool
      │ read by                             │ runs in
      ▼                                     ▼
 factory dashboard                    <run>/wt/<role>  (branch factory/<run-id>/<role>)
```
[R5][R17]

### 5.2 Handoff schema (v1, driver-written)

```json
{
  "schema_version": 1,
  "written_by": "driver",
  "run_id": "20261020T142000Z-a3f1",
  "request_id": {"path": "requests/add-oauth.md", "sha256": "…"},
  "pack": "six-pack",
  "step": 3,
  "task_id": "step-03",
  "role": "coder",
  "attempt": 1,
  "supersedes": null,
  "parent_handoff": "specifier-step-01-a1.json",
  "status": "completed",
  "base_commit": "abc1234",
  "commit": "def5678",
  "branch": "factory/20261020T142000Z-a3f1/coder",
  "worktree": "wt/coder",
  "artifacts": ["src/auth.ts", "test/auth.test.ts"],
  "notes": "Implements OAuth callback; acceptance test added.",
  "blocked_reason": null,
  "failure_class": null,
  "exit": {"code": 0, "timed_out": false, "duration_s": 412},
  "timestamp": "2026-10-20T14:32:11Z"
}
```

- `to_role` is derived from `packs.json`; `broadcast` is the driver's `role_completed` event. [R11]
- `artifacts` is verified against `git diff --name-only base_commit..commit`. [R7]
- `blocked_reason` is `{code, detail}`; codes include `escalate_pack`, `needs_human`, `spec_ambiguous`. [R18]
- Ordering is pack order; no inter-task dependencies in v1. `attempt` is the retry count. [R11]
- File name is `<role>-<task_id>-a<attempt>.json`, created exclusively. State uses the highest attempt for a step; earlier attempts remain. [R13]

**Role report** (the agent's final message, one JSON object, extracted and validated by the driver): `{status, notes, artifacts[], blocked_reason?}`. Missing or invalid → `failed/no_report` or `failed/invalid_report`. [R6]

### 5.3 State contract (Node-ready)

`state.json` (in the run root) is derived from `run.json` (request, pack, planned steps, `base_commit`, `started_at`), `events.jsonl` (lifecycle, cleanup), `_handoffs/` and `approvals/`. [R12]

```json
{
  "schema_version": 1,
  "run_id": "20261020T142000Z-a3f1",
  "pack": "six-pack",
  "request": "requests/add-oauth.md",
  "status": "completed",
  "started_at": "…", "finished_at": "…",
  "base_commit": "abc1234",
  "result_branch": "factory/20261020T142000Z-a3f1/qa",
  "roles": [
    {"step": 1, "role": "specifier", "status": "completed", "attempt": 1, "commit": "…", "handoff": "_handoffs/specifier-step-01-a1.json", "approval": "approved"},
    {"step": 2, "role": "coder", "status": "completed", "attempt": 1, "commit": "…", "handoff": "…"},
    {"step": 3, "role": "cleaner", "status": "pending"}
  ],
  "cleanup": {"worktrees_created": 6, "worktrees_removed": 6, "dirty": []}
}
```

Run `status` ∈ `running | awaiting_approval | blocked | failed | completed`. Planned-but-unrun roles appear as `pending`. [R12]

### 5.4 Role definitions

| Agent | Tools (declared) | Post-run driver audit |
|---|---|---|
| `factory-orchestrator` | `read`, `search`, `execute` (runs only `factory …`) | n/a |
| `specifier` | `read`, `search` | tree clean, no new commits |
| `coder` | `read`, `edit`, `search`, `execute` | changes allowed; driver commits |
| `cleaner` | `read`, `search`, `execute` | tree clean, no new commits (check-and-report role) |
| `architect` | `read`, `search` | tree clean, no new commits |
| `hardender` | `read`, `edit`, `search`, `execute` | changes allowed; driver commits |
| `qa` | `read`, `search`, `execute` | tree clean, no new commits |

Pack order is in `packs.json`. [R7][R11]

Every agent's instruction block contains:
- "Your final message must be one JSON object matching the role-report schema in `HANDOFF-SCHEMA.md`."
- "Work only inside your current worktree. Do not run `git worktree`, switch or delete branches, push, or touch other directories." [R7]
- Read-only roles: "Do not modify files." The driver audits this.

**Dispatch template (per role):** cwd = role worktree; `copilot --agent=<role> -p "<boundary + prior handoff + request + report schema>" -s`, with explicit `--allow-tool` and `--deny-tool` lists per role (including `shell(git push)`, `shell(git worktree)`, branch-deletion patterns), and no `--allow-all` unless Phase 0.3 shows scoped flags cannot work. [R17]

---

## 6. Baseline Guidelines

1. **User-level config, repo-level execution.** Agents and skills in `~/.copilot/`; the pipeline must not depend on the skill. [R3]
2. **One handoff per role per attempt, written by the driver.** `state.json` is derived and overwritten each step, so any agent edit is discarded. [R12][R20]
3. **Commit before completion, verified by the driver.** A `completed` handoff must reference a reachable commit, or the run fails with `missing_commit`. [R6][R20]
4. **Worktrees and `_handoffs/` live in the run root outside the repo**, chained from the previous commit. [R9][R10]
5. **Advance only on a validated handoff.** The driver advances when a handoff validates as `completed` and passes audit; there is no hidden retry engine. [R14][R20]
6. **Driver owns lifecycle; agents own code.** Agents are told not to manage worktrees or branches, with deny flags, and the driver audits that refs, other worktrees and the main tree are unchanged. This is detection, not prevention. [R7][R20]
7. **Documented primitives only, versions pinned.** `copilot --agent`, `-p`, `git worktree`, JSON files, Python. No experimental features. [R4][R20]
8. **`state.json` is the Node contract**, with `schema_version`, JSON Schema files, and additive-only evolution. [R20]
9. **Human gates are explicit.** `needs_approval` exits with code 20 and a visible message; `factory approve` / `resume` continue. `blocked` and `failed` stop and leave worktrees. [R5][R20]

---

## 7. Implementation Plan

### Phase 0: Foundation, scaffolding and contract probes

| Increment | Deliverable | Exit criterion |
|---|---|---|
| 0.1 | `~/.copilot/agents/` and `skills/software-factory/` scaffolds (filename = `name`, no symlinks) | Files exist [R2] |
| 0.2 | Discovery on both surfaces: VS Code Copilot harness dropdown; `copilot --agent=factory-orchestrator`; skill loading checked per surface | Agent visible and invocable on both; skill result recorded per surface (non-blocking) [R1][R3] |
| 0.3 | **Contract probes** (see §11 Open verifications) | Each probe has pass/fail recorded; minimum CLI and Git versions set; failures change the plan before Phase 1 [R21] |

### Phase 1: Role definitions and report protocol

| Increment | Deliverable | Exit criterion |
|---|---|---|
| 1.1 | `HANDOFF-SCHEMA.md` and JSON Schemas (handoff, role report, `state.json`, `run.json`) | Single source of truth [R11] |
| 1.2 | Seven agent profiles with declared tools and boundary instructions; `packs.json` | Each profile loads; routing lives only in `packs.json` [R11] |
| 1.3 | Orchestrator classification, `no_pack`, `--pack` surfacing | Two-pack run completes with valid driver-written handoffs; timeout value calibrated from measured runs [R14][R18] |

### Phase 2: Worktree isolation and Python driver

| Increment | Deliverable | Exit criterion |
|---|---|---|
| 2.1 | Run-dir claim, `run.json`, lazy chained worktree creation (`--lock`), teardown | Six-pack creates six worktrees from a chain and removes all cleanly [R9][R10][R15] |
| 2.2 | Dispatch loop: report extraction, schema validation, commit/audit, status contract, timeouts, exit codes | Driver never edits code; every failure class has a test [R6][R7][R14] |
| 2.3 | `approve`, `resume`, `clean` commands | Kill the driver mid-run; `resume` completes without duplicate handoffs or lost branches [R13][R15][R16] |
| 2.4 | `state.json` derivation and `_handoffs` git repo | State is valid, regenerated, and consumed by the dashboard unchanged [R8][R12] |

### Phase 3: Post-run dashboard (v1)

| Increment | Deliverable | Exit criterion |
|---|---|---|
| 3.1 | `factory dashboard` with `rich` and `--json` | Reconstructs any run from disk alone |
| 3.2 | Failure report: blocking role, `failure_class`, reason, worktree paths, next command (`resume`/`clean`) | Blocked and failed runs are actionable [R14] |
| 3.3 | Second-repo adoption check | Success 7 verified [R19] |

### Phase 4: Deferred (real-time, Node front end, deeper VS Code integration)

Unchanged. None of it requires revisiting Phases 0–3, provided the additive-only schema rule is followed. [R19]

---

## 8. Risks and Mitigations

| ID | Risk | L / I | Mitigation | Verifiable in |
|---|---|---|---|---|
| R1 | Agents invisible because the wrong harness is selected or the files are in the wrong folder | M / H | Copilot harness + `~/.copilot/agents`; no deprecated setting [R1] | Phase 0.2 |
| R2 | `copilot --agent` or user-agent loading fails on a CLI build | M / H | Version floor, preflight, filename = name [R2] | Phase 0.3 |
| R3 | Worktree teardown fails on Windows when an editor holds files | M / M | Stop-don't-force; `factory clean`; dashboard shows paths | Phase 2–3 (Windows run needed) |
| R4 | Agent acts outside its worktree (shell, `~`, interpreters) | M / M | Layered defences and driver audit; detection, not prevention [R7] | Phase 0.3 probes, Phase 2.2 |
| R5 | Approval gate skipped | L / M | Exit 20 plus required `approvals/` record; dashboard shows it | Phase 2.3 |
| R6 | Schema drift between agents and driver | M / M | Agents emit only the report; driver writes handoffs; schema validation | Phase 1–2 |
| R7 | Merge conflicts at human merge due to base drift | M / M | `base_commit` recorded; result branch chained [R9] | Phase 2.1 |
| R8 | `state.json` diverges from sources | L / L | Pure derivation, overwritten each step [R12] | Phase 2.4 |
| R9 | Multi-repo adoption needs per-repo changes | L / M | User-level only; writable parent dir required | Phase 3.3 |
| R10 | Future schema change breaks Node consumer | L / M | `schema_version`, additive-only, consumer ignores unknown fields; unverified until Phase 4 | Phase 2 test only; full proof in Phase 4 [R19] |
| R11 | CLI or agent-discovery drift between versions | M / M | Version pin, preflight, drift lint [R2] | Phase 0.3, each release |
| R12 | Headless dispatch stalls on a trust or permission prompt | M / H | Explicit flags per role; trust handling probed [R17] | Phase 0.3 |
| R13 | A long `factory run` in VS Code's terminal tool times out or blocks on approval | M / M | Probe first. If it fails, add `--detach` with `factory status` polling | Phase 0.3 [R19] |

---

## 9. Success Criteria

1. A feature request runs through the six-pack in VS Code (Copilot harness) with only the approval stop.
2. A bug fix runs through the two-pack in Copilot CLI.
3. Every role runs in its own chained worktree; teardown is clean for a completed run, and `resume` recovers a killed run.
4. Every handoff is a driver-written, git-committed JSON artifact; `state.json` reconstructs the run from disk alone.
5. The dashboard shows timeline, commits, approvals and cleanup; `--json` emits the contract.
6. No experimental Copilot features; CLI and Git versions pinned. [R4]
7. A second repository adopts the factory with no per-repo configuration.

---

## 10. References

- S1 VS Code custom agents: https://code.visualstudio.com/docs/agent-customization/custom-agents.md
- S2 GitHub Docs, create custom agents for CLI (Spanish edition): https://docs.github.com/es/copilot/how-tos/copilot-cli/customize-copilot/create-custom-agents-for-cli
- S3 GitHub Docs, custom agents configuration: https://docs.github.com/en/copilot/reference/custom-agents-configuration
- S4 CLI programmatic reference (Portuguese edition): https://docs.github.com/pt/copilot/reference/copilot-cli-reference/cli-programmatic-reference
- S5 Configure Copilot CLI: https://docs.github.com/en/copilot/how-tos/copilot-cli/set-up-copilot-cli/configure-copilot-cli
- S6 About Copilot CLI (security): https://docs.github.com/enterprise-cloud@latest/copilot/concepts/agents/about-copilot-cli
- S7 copilot-cli #452: https://github.com/github/copilot-cli/issues/452
- S8 copilot-cli #2475: https://github.com/github/copilot-cli/issues/2475
- S9 copilot-cli #2309 (bug report): https://github.com/github/copilot-cli/issues/2309
- S10 copilot-cli #3297: https://github.com/github/copilot-cli/issues/3297
- S11 About Agent Skills: https://docs.github.com/en/enterprise-cloud@latest/copilot/concepts/agents/about-agent-skills
- S12 Third-party note on VS Code skills paths (weak source): https://docs.tabulareditor.com/en/features/te-cli/te-cli-skill.html
- S13 git-worktree: https://www.kernel.org/pub/software/scm/git/docs/git-worktree.html
- Design inspirations: Sandcastle (Matt Pocock), SwarmForge (Uncle Bob), unchanged.

---

## 11. Change Log

| Version | Date | Change |
|---|---|---|
| 1.0 | 2026-10-20 | Frozen baseline |
| 1.1 | 2026-10-20 | Corrected VS Code/CLI discovery and harness model [R1–R4]; driver-only dispatch and explicit approval command [R5]; driver-written, git-backed handoffs with commit/audit rules [R6–R8]; chained worktrees, run-root layout, collision-safe run-id [R9–R10]; schema, state sources, duplicate and failure contracts [R11–R14]; recovery and cleanup [R15–R16]; dispatch template [R17]; pack-selection edge cases [R18]; risk table re-verified [R19]; guidelines rewritten honestly [R20]; Phase 0.3 probes [R21] |

