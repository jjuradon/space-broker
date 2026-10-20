# Software Factory — Project Plan

**Version:** 1.0 (frozen baseline)
**Date:** 2026-10-20
**Status:** Ready for execution
**Primary surfaces:** VS Code Agents window (primary), Copilot CLI (secondary)

---

## 1. What This Project Is

A **software factory** for the GitHub Copilot ecosystem: a request goes in, a reviewed and committed implementation comes out.

The factory is an agent-orchestration layer inspired by two reference designs:

- **Matt Pocock's Sandcastle** — role-based pipelines (planner → implementer → reviewer → merger) with branch/sandbox isolation per agent.
- **Uncle Bob's SwarmForge** — six-role pipeline with durable, commit-backed JSON handoffs and completion broadcasts.

Neither reference is Copied. The factory is a **thin, deterministic orchestration layer** built only on stable GitHub Copilot primitives: user-level custom agents, a Copilot skill, git worktrees for session isolation, per-role JSON handoff files, and a Python driver/dashboard.

### 1.1 The one-sentence goal

> Given a feature request or a bug fix, run it through a role-separated pipeline of Copilot agents, each isolated in its own git worktree, each handing off to the next through a committed JSON artifact, and produce a reviewed, committed, mergeable result with a post-run dashboard.

---

## 2. Intended Use

### 2.1 Primary use — VS Code Agents window

1. Open Copilot Chat in agent mode.
2. Select the `factory-orchestrator` agent.
3. Prompt:
   - `Implement requests/add-oauth.md using the six-pack workflow.`
   - `Fix the null check in src/utils/parse.ts using the two-pack workflow.`
4. Approve the specifier handoff when it appears (six-pack only).
5. Inspect the run with `factory dashboard --run-id <id>` in the terminal.

### 2.2 Secondary use — Copilot CLI

```bash
factory run --request requests/add-oauth.md --pack six-pack
factory run --request requests/fix-null.md     --pack two-pack
factory dashboard --run-id 2026-10-20-001
factory dashboard --run-id 2026-10-20-001 --json
```

### 2.3 Workflows

| Pack | Roles | When |
|---|---|---|
| **Two-pack** | `coder → cleaner` | Single-file fix, localized bug, small test addition |
| **Six-pack** | `specifier → coder → cleaner → architect → hardender → qa` | New feature, multi-module change, anything touching public API or architecture |

Pack selection is done by the orchestrator from the request text, and can be overridden with `--pack`.

### 2.4 Multi-repo

Configuration lives at the **user level** in the home folder, so every repository on the machine shares the same factory without per-repo copies:

```
~/.copilot/
├── agents/
│   ├── factory-orchestrator.agent.md
│   ├── specifier.agent.md
│   ├── coder.agent.md
│   ├── cleaner.agent.md
│   ├── architect.agent.md
│   ├── hardender.agent.md
│   └── qa.agent.md
└── skills/
    └── software-factory/
        ├── SKILL.md
        ├── CONSTITUTION.md
        └── HANDOFF-SCHEMA.md
```

Repositories stay clean; they only need Git worktree capability.

---

## 3. Goals

### 3.1 Primary goals

- **G1 — Request-to-result pipeline.** A feature request or fix produces a committed, reviewed, mergeable change without manual orchestration in the happy path.
- **G2 — Role separation that is enforceable, not merely prompted.** Each role has a narrow tool set (e.g. `planner`/`specifier` cannot edit files; `reviewer`/`cleaner` cannot write), declared in the agent profile.
- **G3 — Durable handoffs.** Every transition between roles is a committed JSON artifact, never a chat-history handoff.
- **G4 — Session isolation via git worktrees.** Each role works in its own worktree so agents cannot step on each other's uncommitted changes.
- **G5 — Two operational surfaces.** The same agent definitions are invocable from the VS Code Agents window and from Copilot CLI.
- **G6 — Post-run observability.** Any run can be reconstructed from disk artifacts alone.

### 3.2 Secondary goals (later phases)

- **G7 — Real-time dashboard** for a running factory run.
- **G8 — Node front end** consuming a stable Python-owned JSON contract.
- **G9 — VS Code Agents window integration** so factory runs appear alongside other Copilot sessions.

### 3.3 Explicit non-goals (v1)

- No dependency on experimental Copilot features (`/fleet`, dynamic workflows, experimental subagent properties).
- No container/sandbox isolation (Docker, Podman, Vercel). Worktree isolation only.
- No hidden retry/veto engine. Failure handling follows SwarmForge's explicit broadcast-and-stop model.
- No per-repository configuration. All factory config is user-level.
- No custom GUI in v1. VS Code's existing Agents window and the Python CLI dashboard are the surfaces.

---

## 4. Requirements

### 4.1 Functional requirements

| ID | Requirement |
|---|---|
| F1 | An orchestrator agent classifies a request and selects two-pack or six-pack, overridable by the user. |
| F2 | Each role in the selected pack is dispatched in its own git worktree. |
| F3 | Each role writes exactly one JSON handoff per task, containing run id, pack, role, task id, status, artifacts, commit SHA, worktree path, broadcast flag, notes, blocked reason, and timestamp. |
| F4 | A handoff with `status: completed` must reference a commit; the driver rejects completed handoffs without a commit SHA. |
| F5 | A handoff with `status: blocked` must include a reason; the driver stops the run and leaves worktrees in place. |
| F6 | A handoff with `status: needs_approval` (specifier in six-pack) pauses the run and prompts for human approval. |
| F7 | The driver derives `state.json` from the set of handoff files. `state.json` is never edited by agents. |
| F8 | The driver removes worktrees at run completion; if a worktree is dirty, it records `cleanup: dirty` and stops rather than force-removing. |
| F9 | A post-run dashboard reconstructs the full role timeline from `state.json` and handoff files, with `--json` emitting the raw payload. |
| F10 | The skill is discoverable and loadable when a request matches its description ("software factory", "six-pack workflow"). |

### 4.2 Non-functional requirements

| ID | Requirement |
|---|---|
| N1 | Only stable Copilot primitives are used: `copilot --agent=`, user-level agent discovery, skill discovery. No `--experimental`. |
| N2 | Configuration is user-level under `~/.copilot/`; no repository is modified to adopt the factory beyond enabling Git worktrees. |
| N3 | Worktrees are created outside the repository at `../worktrees/<run-id>/<role>`. |
| N4 | Handoff files live in `../worktrees/<run-id>/_handoffs/`, outside every role's working tree. |
| N5 | The driver and dashboard are written in Python. The state contract is a stable JSON schema so a Node front end can consume it without renegotiation. |
| N6 | Agents never manage worktrees or `state.json`; the driver owns lifecycle, agents own code. |
| N7 | Branch names follow `<run-id>/<role>` so `git branch` output stays readable. |

### 4.3 Environment requirements

| ID | Requirement |
|---|---|
| E1 | GitHub Copilot CLI installed, version supporting `--agent=` and user-level agent discovery at `~/.copilot/agents`. |
| E2 | VS Code Copilot Chat with the custom agent directory configured to point at `~/.copilot/agents`. |
| E3 | Git with worktree support (standard since Git 2.5). |
| E4 | Python 3.10+ with `rich` (and a JSON-schema validator) for the driver and dashboard. |
| E5 | Filesystem layout allows worktrees outside the repository. |

---

## 5. Architecture

### 5.1 Components

```
┌─────────────────────────────────────────────────────────────┐
│  VS Code Agents window (primary)   Copilot CLI (secondary)  │
└───────────────────────────┬─────────────────────────────────┘
                            │ invokes
┌───────────────────────────▼─────────────────────────────────┐
│  factory-orchestrator.agent.md  (user-level)                │
│  - classifies request, selects pack                         │
│  - delegates to role agents                                 │
└───────────────────────────┬─────────────────────────────────┘
                            │ dispatched by
┌───────────────────────────▼─────────────────────────────────┐
│  factory.py (Python driver)                                 │
│  - creates worktrees, dispatches roles, validates handoffs  │
│  - derives state.json, tears down worktrees                 │
└───────┬───────────────────────────────┬─────────────────────┘
        │ writes                        │ calls
┌───────▼──────────────┐     ┌──────────▼──────────────────────┐
│ _handoffs/*.json     │     │ copilot --agent=<role> ...      │
│ state.json           │     │ (cwd = role worktree)           │
└───────┬──────────────┘     └──────────┬──────────────────────┘
        │ read by                       │ runs in
┌───────▼──────────────┐     ┌──────────▼──────────────────────┐
│ factory dashboard    │     │ ../worktrees/<run-id>/<role>    │
│ (post-run, --json)   │     │ (git worktree per role)         │
└──────────────────────┘     └─────────────────────────────────┘
```

### 5.2 Handoff schema

```json
{
  "run_id": "2026-10-20-001",
  "pack": "six-pack",
  "role": "coder",
  "task_id": "task-3",
  "from_role": "coder",
  "to_role": "cleaner",
  "status": "completed",
  "artifacts": ["src/auth.ts", "test/auth.test.ts"],
  "commit": "abc1234",
  "worktree": "../worktrees/2026-10-20-001/coder",
  "broadcast": true,
  "notes": "Implements OAuth callback; acceptance test added.",
  "blocked_reason": null,
  "timestamp": "2026-10-20T14:32:11Z"
}
```

### 5.3 State contract (Node-ready)

`state.json` is written to `../worktrees/<run-id>/state.json` and is explicitly the backend contract for the future Node front end:

```json
{
  "run_id": "2026-10-20-001",
  "pack": "six-pack",
  "request": "requests/add-oauth.md",
  "status": "completed",
  "started_at": "2026-10-20T14:20:00Z",
  "finished_at": "2026-10-20T14:58:44Z",
  "roles": [
    {"role": "specifier", "status": "completed", "commit": "a1b2c3d", "handoff": "_handoffs/specifier-task-1.json"},
    {"role": "coder",     "status": "completed", "commit": "e4f5g6h", "handoff": "_handoffs/coder-task-3.json"},
    {"role": "cleaner",   "status": "completed", "commit": "i7j8k9l", "handoff": "_handoffs/cleaner-task-4.json"},
    {"role": "architect", "status": "completed", "commit": "m0n1o2p", "handoff": "_handoffs/architect-task-5.json"},
    {"role": "hardender", "status": "completed", "commit": "q3r4s5t", "handoff": "_handoffs/hardender-task-6.json"},
    {"role": "qa",        "status": "completed", "commit": "u6v7w8x", "handoff": "_handoffs/qa-task-7.json"}
  ],
  "cleanup": {"worktrees_created": 6, "worktrees_removed": 6, "dirty": []}
}
```

### 5.4 Role definitions

**Two-pack:**

| Agent | Tools | Handoff target |
|---|---|---|
| `factory-orchestrator` | `read`, `search`, `agent` | — |
| `coder` | `read`, `edit`, `search`, `execute` | `cleaner` |
| `cleaner` | `read`, `search`, `execute` | `factory-orchestrator` (completion) |

**Six-pack:**

| Agent | Tools | Handoff target |
|---|---|---|
| `specifier` | `read`, `search` | `coder` (with `needs_approval` first) |
| `coder` | `read`, `edit`, `search`, `execute` | `cleaner` |
| `cleaner` | `read`, `search`, `execute` | `architect` |
| `architect` | `read`, `search` | `hardender` |
| `hardender` | `read`, `edit`, `search`, `execute` | `qa` |
| `qa` | `read`, `search`, `execute` | `factory-orchestrator` (completion broadcast) |

Each agent's instruction block must contain:

- "Write your handoff to `_handoffs/<role>-<task_id>.json` using the schema in `HANDOFF-SCHEMA.md`."
- "Commit your work in your worktree before writing the handoff."
- "Do not read or write outside your assigned worktree and `_handoffs/`."

---

## 6. Baseline Guidelines

1. **User-level config, repo-level execution.** Agents and skills in `~/.copilot/`; repos only need Git worktree capability.
2. **One JSON handoff per role per task.** No chat-history handoffs. `state.json` is derived and never edited by agents.
3. **Commit before handoff.** A `completed` handoff without a commit SHA is invalid and the driver rejects it.
4. **Worktrees outside the repo.** `../worktrees/<run-id>/<role>`; `_handoffs/` is a sibling of role worktrees, outside every working tree.
5. **Broadcast, then advance.** A role's completion broadcast is what triggers the next dispatch; there is no hidden retry engine in v1.
6. **Driver owns lifecycle, agents own code.** The driver creates and removes worktrees, dispatches roles, validates handoffs, and writes `state.json`. Agents never touch worktree management or `state.json`.
7. **Stable primitives only.** `copilot --agent=`, `git worktree`, JSON files, Python. No experimental Copilot features.
8. **`state.json` is the Node contract.** Python owns the schema; the future Node front end consumes it without renegotiation.
9. **Human gates are explicit.** `needs_approval` produces a pause with a visible prompt; `blocked` stops the run and leaves worktrees in place.

---

## 7. Implementation Plan

### Phase 0 — Foundation and User-Level Scaffolding

| Increment | Deliverable | Exit criterion |
|---|---|---|
| 0.1 | Directory layout `~/.copilot/agents/` and `~/.copilot/skills/software-factory/` with `SKILL.md`, `CONSTITUTION.md`, `HANDOFF-SCHEMA.md` | Files exist and are discoverable |
| 0.2 | Verify discovery on both surfaces (VS Code dropdown, `copilot --agent=factory-orchestrator`, skill load) | A user-level agent is visible and invocable from both surfaces |

### Phase 1 — Role Definitions and Handoff Protocol

| Increment | Deliverable | Exit criterion |
|---|---|---|
| 1.1 | Handoff schema finalized and documented in `HANDOFF-SCHEMA.md`; agents instructed to write to `_handoffs/` | Schema is the single source of truth |
| 1.2 | Seven agent profiles in `~/.copilot/agents/` with tool restrictions and handoff targets | Each agent has narrow tools and a declared handoff target |
| 1.3 | Pack selection logic in `factory-orchestrator.agent.md` | Two-pack run completes end-to-end with valid `coder` and `cleaner` handoffs |

### Phase 2 — Worktree Isolation and Python Driver

| Increment | Deliverable | Exit criterion |
|---|---|---|
| 2.1 | Worktree provisioning and teardown in `factory.py` | Six-pack run creates six worktrees, six handoffs, and removes all cleanly |
| 2.2 | Driver dispatch loop with schema validation, approval pause, blocked stop | Driver never edits code; only dispatches, validates, records |
| 2.3 | `state.json` emission as the Node contract | `state.json` is valid, derived, and consumed by dashboard without renegotiation |

### Phase 3 — Post-Run Dashboard (v1)

| Increment | Deliverable | Exit criterion |
|---|---|---|
| 3.1 | `factory dashboard --run-id <id>` with `rich` output and `--json` | Reconstructs any run from disk artifacts alone |
| 3.2 | Post-run failure report showing blocking role, reason, and lingering worktree paths | Blocked runs are actionable from the dashboard |

### Phase 4 — Deferred (Real-Time and Node Front End)

| Increment | Deliverable | Notes |
|---|---|---|
| 4.1 | Real-time CLI dashboard streaming `copilot` output and session events | No schema change; dashboard becomes incremental |
| 4.2 | Node front end consuming `state.json` via HTTP or direct file | Python owns schema; Node consumes |
| 4.3 | VS Code Agents window integration for factory runs | No custom GUI |

None of Phase 4 requires revisiting Phase 0–3.

---

## 8. Risks and Mitigations

| ID | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| R1 | VS Code user-profile agent directory not configured, agents not visible | Medium | High (blocks Phase 0.2) | Document the one-time setting in skill README; verify in Increment 0.2 before Phase 1 |
| R2 | `copilot --agent=` unavailable in the installed CLI version | Low | High | Verify in Increment 0.2; fall back to direct agent invocation only if confirmed |
| R3 | Worktree teardown fails on Windows because an editor holds files open | Medium | Medium | Driver's "dirty → stop, don't force" rule; dashboard surfaces lingering worktree paths (3.2) |
| R4 | Agents write outside their worktree or into `_handoffs/` | Medium | Medium | Agent instructions forbid it; driver validates handoff paths; constitution restates the boundary |
| R5 | Specifier approval gate is skipped or auto-approved | Low | Medium | `needs_approval` status is required and driver pauses; dashboard shows approval as a distinct state |
| R6 | Handoff schema drifts between agents and driver | Medium | Medium | `HANDOFF-SCHEMA.md` is the single source; driver validates against it; agents reference it by path |
| R7 | Parallel worktree agents produce merge conflicts | Medium | Medium | Worktrees are per-role, not per-file; conflicts surface at merge time and are human territory in v1 |
| R8 | `state.json` and handoff files diverge | Low | Low | `state.json` is always derived by the driver; never edited by agents |
| R9 | Multi-repo adoption requires per-repo changes | Low | Medium | Design holds factory config entirely at user level; only Git worktree capability is required per repo |
| R10 | Future real-time dashboard requires schema changes | Low | Medium | `state.json` designed as the contract in Phase 2; Phase 4 adds fields non-destructively |

---

## 9. Success Criteria

The project is considered successful when:

1. A feature request runs end-to-end through the six-pack in VS Code without manual orchestration in the happy path.
2. A bug fix runs end-to-end through the two-pack in Copilot CLI.
3. Every role's work is isolated in its own worktree, and teardown is clean for a completed run.
4. Every handoff is a committed JSON artifact; `state.json` reconstructs the run from disk alone.
5. The post-run dashboard displays role timeline, commits, and cleanup status, and `--json` emits the contract for a future Node backend.
6. No experimental Copilot features are used in the core pipeline.
7. A second repository adopts the factory with zero per-repo configuration beyond Git worktree capability.

---

## 10. Reference Documents

- Matt Pocock — Sandcastle: role-based pipelines, sandbox/worktree isolation per agent.
- Uncle Bob — SwarmForge: six-role pipeline, commit-backed JSON handoffs, completion broadcasts.
- GitHub Copilot — user-level agent discovery at `~/.copilot/agents`, skill discovery at `~/.copilot/skills`, `copilot --agent=` invocation.
- Git — `git worktree add` / `git worktree remove` for session isolation.

---

## 11. Change Log

| Version | Date | Change |
|---|---|---|
| 1.0 | 2026-10-20 | Frozen baseline: user-level config, worktree isolation, per-role JSON handoffs, Python driver/dashboard, post-run dashboard v1, real-time and Node front end deferred |

---
