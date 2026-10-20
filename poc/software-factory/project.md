# Software Factory: Project Plan

**Version:** 1.2
**Date:** 2026-10-20
**Status:** Ready for Phase 0
**Primary surfaces:** VS Code 1.141 Agents window, Copilot harness (primary); Copilot CLI (secondary)

---

## 1. What This Project Is

A **software factory** for the GitHub Copilot ecosystem: a request goes in, a reviewed and committed implementation comes out. The whole system is built from scratch under this plan.

It is a thin, deterministic orchestration layer inspired by two reference designs:

- **Matt Pocock's Sandcastle:** role-based pipelines with per-agent branch/sandbox isolation.
- **Uncle Bob's SwarmForge:** six-role pipeline with durable, commit-backed JSON handoffs.

Neither is copied. The factory uses only documented, non-experimental, version-pinned primitives: user-level custom agents, a skill, git worktrees, driver-written JSON handoffs, and a Python driver and dashboard.

### 1.1 The one-sentence goal

> Given a feature request or bug fix, run it through a role-separated pipeline of Copilot agents, each in its own git worktree chained from the previous role's commit, each recorded as a driver-written, git-backed JSON handoff, and produce a reviewed, committed, mergeable result branch with a post-run dashboard.

---

## 2. Intended Use

### 2.1 Primary use: VS Code 1.141 Agents window

1. Select the **Copilot** harness and the `factory-orchestrator` agent.
2. Prompt, for example:
   - `Implement requests/add-oauth.md.` (no flow stated, so the six-pack runs)
   - `Fix the null check in src/utils/parse.ts without deep flow.` (two-pack)
   - `Implement requests/add-oauth.md as a simple task.` (two-pack)
3. The orchestrator passes your prompt text verbatim to `factory resolve-pack`, states the resolved pack and why, then runs `factory run --pack <resolved>`.
4. Six-pack: after the specifier, the driver exits with code 20 (`awaiting_approval`). Run `factory approve --run-id <id>` (or `--reject --reason …`), then `factory resume --run-id <id>`.
5. Inspect with `factory dashboard --run-id <id>`.

### 2.2 Secondary use: Copilot CLI

```bash
factory run --request requests/add-oauth.md                    # no --pack: six-pack default
factory run --request requests/fix-null.md --pack two-pack     # name or alias accepted
factory run --request requests/fix-null.md --pack "simple task"
factory resolve-pack --text "fix it without deep flow"         # prints the resolution as JSON
factory approve   --run-id <id>
factory resume    --run-id <id>
factory clean     --run-id <id>
factory dashboard --run-id <id> [--json]
```

### 2.3 Workflows and pack aliases

| Pack | Roles | When |
|---|---|---|
| **Six-pack (default)** | `specifier → coder → cleaner → architect → hardender → qa` | Anything not explicitly marked simple |
| **Two-pack** | `coder → cleaner` | Only when explicitly requested by name or alias |

**Alias resolution** is implemented once, in the driver, from `packs.json`. It applies to the `--pack` flag and to the user's prompt text, not to the contents of the request file, so a phrase inside a request cannot change the flow by accident.

Default alias table (editable in `packs.json`):

| Pack | Aliases |
|---|---|
| six-pack | `six-pack`, `six pack`, `deep flow`, `full flow` |
| two-pack | `two-pack`, `two pack`, `without deep flow`, `no deep flow`, `simple task`, `simple flow`, `light flow` |

Rules:

1. An explicit `--pack` value (name or alias) wins over text matching.
2. Matching is case-insensitive, whole-phrase, **longest phrase first**. A matched span is consumed, so `without deep flow` does not also match the six-pack alias `deep flow`.
3. Aliases are multi-word on purpose. Bare words like "simple" or "quick" are not aliases, because "simple login feature" must not silently drop the review stages.
4. No match gives the **six-pack**. If both packs match, the six-pack wins with reason `conflict_defaulted`.
5. The resolution (`pack`, `matched_alias`, `reason`) is recorded in `run.json` and shown at run start.

A wrong pack is corrected by re-running with the other alias. A two-pack role can also stop with `blocked/escalate_pack`; the user then re-runs on the six-pack, and branches from the aborted run are kept.

If the request file is missing, unreadable or empty, the orchestrator replies `no_request` and runs nothing.

### 2.4 Multi-repo

Config lives at user level:

```
~/.copilot/
├── agents/   (factory-orchestrator, specifier, coder, cleaner, architect, hardender, qa .agent.md)
└── skills/software-factory/ (SKILL.md, CONSTITUTION.md, HANDOFF-SCHEMA.md)
```

Agent filename must equal frontmatter `name`. Install by **copying**, never symlinking (a symlink-discovery bug has been reported for agents and skills). The skill is not load-bearing: the pipeline must work if it does not load. Repos need only Git worktree capability and a writable parent directory.

---

## 3. Goals

### 3.1 Primary goals

- **G1 Request-to-result pipeline** with no manual orchestration in the happy path. The approval gate is the one deliberate stop.
- **G2 Role separation that is declared, constrained and audited.** Tool lists are declared per profile. `execute` is shell access and can write, and dispatch runs with allow-all flags, so enforcement is **driver audit after every role**, not prevention.
- **G3 Durable, git-backed handoffs.** Every transition is a JSON file the driver commits to a driver-owned `_handoffs` repo.
- **G4 Session isolation via git worktrees**, one per role, chained from the previous role's commit.
- **G5 Two operational surfaces** sharing the same user-level agent definitions.
- **G6 Post-run observability** from disk artifacts alone.
- **G7 Simple flow selection** through the deterministic alias rules in §2.3.

### 3.2 Secondary goals (later phases)

Real-time dashboard; Node front end on the stable JSON contract; deeper VS Code Agents window integration; evaluating Copilot sandboxing.

### 3.3 Explicit non-goals (v1)

- No experimental Copilot features (`/fleet`, dynamic workflows, experimental subagent properties).
- No VS Code `handoffs` frontmatter and no agent-to-agent delegation. The driver is the only dispatcher, and the orchestrator has no `agent` tool.
- No container or OS-level sandbox in v1.
- No hidden retry engine. Resume is an explicit human command.
- No per-repository configuration.
- No custom GUI.
- No LLM-based pack classification. Selection is by alias rules.

---

## 4. Requirements

### 4.1 Functional requirements

| ID | Requirement |
|---|---|
| F1 | `factory resolve-pack` and `factory run` resolve the pack per §2.3. `--pack` is optional and defaults to the six-pack. The orchestrator always calls `resolve-pack` with the user's verbatim prompt. |
| F2 | The driver dispatches each role lazily in its own worktree, created from the previous role's commit (the first from `base_commit`). |
| F3 | Each role attempt yields exactly one JSON handoff **written by the driver** from the role's final JSON report. Fields are in §5.2. |
| F4 | A `completed` handoff must reference a reachable commit. Edit roles: the driver commits residual changes or uses an advanced HEAD, and a clean unchanged tree is `failed/no_changes`. Read-only roles: the tree must be clean and `commit` equals the start SHA. |
| F5 | `blocked` requires a coded reason. The driver stops (exit 10) and leaves worktrees. |
| F6 | `needs_approval` (specifier) stops the run (exit 20). `factory approve` records `approvals/<step>.json`; `factory resume` continues. |
| F7 | `state.json` is a pure function of `run.json`, `events.jsonl`, `_handoffs/` and `approvals/`, regenerated on every driver step. Agents never write it. |
| F8 | At completion the driver removes worktrees with `git worktree remove`. A dirty worktree records `cleanup: dirty` and the driver stops without `--force`. `factory clean` is the explicit, lossless remedy: it commits residual changes as a `wip` commit on the role branch, then removes the worktree. |
| F9 | The dashboard reconstructs the timeline from `state.json` and handoffs; `--json` emits the contract. |
| F10 | The skill loads on matching requests (verified), but the pipeline does not depend on it. |
| F11 | Any status outside `completed/blocked/needs_approval`, a missing or invalid report, a crash, a timeout or an audit violation yields `failed` with a `failure_class` (exit 30). |
| F12 | `factory resume` is idempotent: it skips completed steps, marks worktrees with no handoff `interrupted`, and starts a new attempt without deleting old branches. |

### 4.2 Non-functional requirements

| ID | Requirement |
|---|---|
| N1 | Only documented, non-experimental primitives. CLI, Git and VS Code versions are recorded in `docs/baseline.md` and in each `run.json`. |
| N2 | Config is user-level under `~/.copilot/`; repos are not modified beyond worktree capability. |
| N3 | Run root: `<repo-toplevel>/../.factory/<repo-name>/<run-id>/` containing `run.json`, `events.jsonl`, `state.json`, `_handoffs/`, `approvals/`, `wt/<role>/`. |
| N4 | `_handoffs/` is outside every working tree and is a driver-owned git repo with one commit per handoff. Agents never read or write it; the driver injects the prior handoff into the next prompt. |
| N5 | Driver and dashboard in Python. `state.json` and handoffs carry `schema_version`; changes are additive-only; JSON Schemas ship with the driver. |
| N6 | Agents never manage worktrees, branches or `state.json`. The driver audits compliance after each role. |
| N7 | Branches are `factory/<run-id>/<role>`; retry attempts use `…/<role>-a<n>`. |
| N8 | Run-id is `YYYYMMDDTHHMMSSZ-<4hex>`; the run directory is claimed with an atomic `mkdir`. |
| N9 | Driver exit codes: 0 completed, 10 blocked, 20 awaiting approval, 30 failed, 40 preflight failure. |
| N10 | Alias rules live only in `packs.json`. Agents and docs reference them and never redefine them. |

### 4.3 Environment requirements and verified baseline

The following were verified by the project owner and are not re-probed. They are baseline facts, recorded in `docs/baseline.md` in Phase 0.2.

| ID | Requirement / verified fact |
|---|---|
| E1 | Copilot CLI at the latest version at build time. User-level agents in `~/.copilot/agents` load and `copilot --agent` works (verified). The exact version is recorded; preflight fails below it. |
| E2 | VS Code **1.141**, Copilot harness selected. User agents in `~/.copilot/agents` appear in the agent list (verified). The deprecated `chat.agentFilesLocations` setting is not used. |
| E3 | Git at the latest version at build time (verified); the exact version is recorded. |
| E4 | Skills in `~/.copilot/skills` are discovered (verified). |
| E5 | **Headless dispatch needs allow flags.** Without allow-all, each new session triggers an access request (verified). The dispatch template therefore uses allow flags. Phase 0.3 finds the minimal set; the fallback is `--allow-all`. |
| E6 | Python 3.10+ with `rich` and a JSON-schema validator. |
| E7 | The repo's parent directory is writable, so worktrees can be created outside the repo. |

---

## 5. Architecture

### 5.1 Components

```
VS Code 1.141 Agents window (Copilot harness)    Copilot CLI
                │                                     │
                ▼                                     ▼
 factory-orchestrator.agent.md  (read, search, execute: runs only `factory …`)
   verbatim prompt → `factory resolve-pack` → `factory run --pack <resolved>`
                │
                ▼
 factory.py  (the only dispatcher)
   resolves pack, claims run dir, writes run.json, creates chained worktrees,
   dispatches roles, writes handoffs, audits, commits, derives state.json, tears down
      │ writes                                │ calls (cwd = role worktree)
      ▼                                       ▼
 _handoffs/ (git repo), approvals/,      copilot --agent=<role> -p <prompt> -s
 events.jsonl, state.json                + allow flags (E5) + deny flags
      │ read by                               │ runs in
      ▼                                       ▼
 factory dashboard                       <run>/wt/<role>  (branch factory/<run-id>/<role>)
```

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

- `to_role` is derived from `packs.json`; `broadcast` is the driver's `role_completed` event.
- `artifacts` is verified against `git diff --name-only base_commit..commit`.
- `blocked_reason` is `{code, detail}`; codes include `escalate_pack`, `needs_human`, `spec_ambiguous`.
- Ordering is pack order; there are no inter-task dependencies in v1. `attempt` is the retry count.
- File name is `<role>-<task_id>-a<attempt>.json`, created exclusively. State uses the highest attempt per step; earlier attempts stay on disk.

**Role report** is the agent's final message: one JSON object `{status, notes, artifacts[], blocked_reason?}`. The driver extracts and validates it. A missing or invalid report gives `failed/no_report` or `failed/invalid_report`.

### 5.3 State contract (Node-ready)

`state.json` is derived from `run.json` (request, pack, `pack_resolution`, planned steps, `base_commit`, `started_at`, tool versions), `events.jsonl`, `_handoffs/` and `approvals/`.

```json
{
  "schema_version": 1,
  "run_id": "20261020T142000Z-a3f1",
  "pack": "six-pack",
  "pack_resolution": {"matched_alias": null, "reason": "default"},
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

Run `status` is one of `running | awaiting_approval | blocked | failed | completed`. Planned-but-unrun roles appear as `pending`.

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

If you intended the cleaner to auto-fix rather than report, that is a scope change and needs your decision.

Every role's instructions include:
- "Your final message must be one JSON object matching the role-report schema in `HANDOFF-SCHEMA.md`."
- "Work only inside your current worktree. Do not run `git worktree`, switch or delete branches, push, or touch other directories."
- Read-only roles: "Do not modify files."

**Dispatch template (per role):** cwd = role worktree; `copilot --agent=<role> -p "<boundary + prior handoff + request + report schema>" -s` with the allow flags from E5 and the deny flags below.

**Residual risk, accepted for v1:** with allow flags, the CLI's path scoping no longer confines the agent. Confinement relies on cwd, the prompt boundary, deny flags (if Phase 0.3 shows they still apply), and the post-role audit, which detects problems rather than preventing them. The audit checks, for every role:
- main-repo HEAD and refs unchanged except the role branch;
- other worktrees unchanged;
- read-only roles left a clean tree and made no commits;
- `git diff --name-only` matches the reported artifacts.

A failure gives `failed/audit_violation`. The constitution tells users to run the factory only on repos and machines where this is acceptable.

---

## 6. Baseline Guidelines

1. **User-level config, repo-level execution.** The skill is helpful, not load-bearing.
2. **One handoff per role per attempt, written by the driver.** `state.json` is derived and overwritten each step, so any agent edit is discarded.
3. **Commit before completion, verified by the driver.** A `completed` handoff must reference a reachable commit, or the run fails with `missing_commit`.
4. **Worktrees and `_handoffs/` live in the run root outside the repo,** chained from the previous commit.
5. **Advance only on a validated, audited handoff.** There is no hidden retry engine.
6. **Driver owns lifecycle; agents own code.** Compliance is detected by audit, not prevented.
7. **Documented primitives only, versions recorded.** No experimental features.
8. **`state.json` is the Node contract,** with `schema_version`, JSON Schema files and additive-only evolution.
9. **Human gates are explicit.** `needs_approval` exits 20; `factory approve` and `resume` continue. `blocked` and `failed` stop and leave worktrees.
10. **Flow selection is deterministic.** Aliases are resolved by the driver from `packs.json`; default is the six-pack.

---

## 7. Implementation Plan

Build order: every phase depends only on earlier phases. Driver increments are tested against a stand-in `copilot` script first, then against the real CLI.

### Phase 0: Foundation and remaining probes (no factory tool exists yet)

| Increment | Deliverable | Exit criterion |
|---|---|---|
| 0.1 | Source repo layout: `factory/` (Python package), `agents/`, `skill/`, `schemas/`, `tests/`, `docs/`; an install script that **copies** agents and skill into `~/.copilot/` | Install is repeatable; filename = frontmatter `name` check passes |
| 0.2 | `docs/baseline.md` recording the verified facts E1–E5 plus exact CLI, Git, VS Code (1.141) and Python versions | File committed; later preflight reads from it |
| 0.3 | **Hand-run probes** using plain shell commands and stand-in scripts (see §11) | Each probe has a recorded result; the dispatch allow/deny flag set and the long-run launch method are decided |

### Phase 1: Contracts and role profiles (no driver)

| Increment | Deliverable | Exit criterion |
|---|---|---|
| 1.1 | JSON Schemas (handoff, role report, `run.json`, `state.json`, `packs.json`) plus fixture files and validator tests | Valid fixtures pass, invalid fixtures fail |
| 1.2 | Six role profiles with declared tools and boundary instructions; `packs.json` with aliases | Each profile loads; routing and aliases only in `packs.json` |
| 1.3 | Manual dry-run of each role in a hand-made worktree on a fixture repo | Every role returns a schema-valid report; read-only roles leave a clean tree |

### Phase 2: Python driver, built from scratch in increments

| Increment | Deliverable | Exit criterion |
|---|---|---|
| 2.1 | Skeleton: CLI (`run`, `resume`, `approve`, `clean`, `resolve-pack`), config, preflight, `packs.json` loader and alias resolver, atomic run-dir claim, `run.json`, `events.jsonl`, exit codes | Alias test table passes, including `without deep flow`, `simple task`, no-match default and conflict |
| 2.2 | Worktree lifecycle: lazy chained creation with `--lock`, teardown, dirty handling, `clean` | Works on temp repos, including a failure injected mid-chain |
| 2.3 | Single-role dispatch: template, timeout, report extraction and validation, commit and audit rules, handoff writing, `_handoffs` git repo | Every `failure_class` has a test against the stand-in; one real `coder` run passes |
| 2.4 | Two-pack end to end from the CLI | Real two-pack run on a fixture repo yields two valid handoffs and a clean teardown |
| 2.5 | Six-pack, approval gate, `blocked` and `failed` paths | Reject, approve and block cases behave per F5, F6 and F11 |
| 2.6 | `resume` and recovery | Killing the driver at each step, then `resume`, completes with no duplicate handoffs and no lost branches |
| 2.7 | `state.json` derivation | State is regenerated from sources only and validates against its schema |

### Phase 3: Orchestrator and surfaces

| Increment | Deliverable | Exit criterion |
|---|---|---|
| 3.1 | `factory-orchestrator` profile (calls `resolve-pack` with the verbatim prompt) and the skill files generated from the schemas | The three example prompts in §2.1 resolve to six-pack, two-pack, two-pack |
| 3.2 | VS Code 1.141 end to end: six-pack feature with approval stop | Success 1 met; the long-run launch method from Phase 0.3 is confirmed |
| 3.3 | CLI end to end: two-pack bug fix | Success 2 met |

### Phase 4: Post-run dashboard (v1)

| Increment | Deliverable | Exit criterion |
|---|---|---|
| 4.1 | `factory dashboard` with `rich` and `--json` | Reconstructs any run from disk alone |
| 4.2 | Failure report: blocking role, `failure_class`, reason, worktree paths, next command | Blocked and failed runs are actionable |
| 4.3 | Second-repo adoption check | Success 7 met |

### Phase 5: Deferred

Real-time dashboard, Node front end, deeper Agents window integration, and evaluating Copilot sandboxing. None of it requires revisiting Phases 0–4 if the additive-only schema rule holds.

---

## 8. Risks and Mitigations

| ID | Risk | L / I | Mitigation | Verifiable in |
|---|---|---|---|---|
| R1 | Wrong harness selected in VS Code, so agents are not used | L / M | Copilot harness documented in the skill and the run instructions; agent list verified | Phase 3.2 |
| R2 | A CLI update changes agent discovery or flags | M / M | Version recorded and floor-checked at preflight; re-run Phase 0.3 probes on upgrade | Phase 0.3, each upgrade |
| R3 | Worktree teardown fails on Windows when an editor holds files | M / M | Stop-don't-force; `factory clean`; dashboard shows paths | Phase 2.2, 4.2 |
| R4 | Allow-all dispatch lets an agent act outside its worktree | M / H | Prompt boundary, deny flags if effective, post-role audit (detection only); risk accepted and documented | Phase 0.3, 2.3 |
| R5 | Approval gate skipped | L / M | Exit 20 plus required `approvals/` record; dashboard shows it | Phase 2.5 |
| R6 | Schema drift between agents and driver | M / M | Agents emit only the report; the driver writes handoffs; schema validation | Phase 1.1, 2.3 |
| R7 | Merge conflicts at human merge due to base-branch drift | M / M | `base_commit` recorded; result branch chained | Phase 2.2 |
| R8 | `state.json` diverges from sources | L / L | Pure derivation, overwritten each step | Phase 2.7 |
| R9 | Multi-repo adoption needs per-repo changes | L / M | User-level only; writable parent directory required | Phase 4.3 |
| R10 | A future schema change breaks the Node consumer | L / M | `schema_version`, additive-only, consumer ignores unknown fields. Fully provable only in Phase 5; Phase 1.1 tests the rule | Phase 1.1 partial, Phase 5 full |
| R11 | A long `factory run` in the VS Code terminal tool times out or stalls on approval | M / M | Probe in Phase 0.3 using a stand-in. If it fails, add `--detach` and `factory status` polling in Phase 2 | Phase 0.3 |
| R12 | Alias rules misfire (an alias phrase appears by accident, or a wanted phrase is not an alias) | M / M | Multi-word aliases only; applied to the prompt and `--pack`, not the request file; resolution shown and recorded; test table | Phase 2.1 |
| R13 | Role JSON report is malformed or missing | M / M | `-s` output extraction probed; `invalid_report` and `no_report` failure classes stop the run safely | Phase 0.3, 2.3 |

---

## 9. Success Criteria

1. A feature request with no flow stated runs through the six-pack from the VS Code 1.141 Copilot harness, with only the approval stop.
2. A bug fix stated as "without deep flow" or "simple task" runs through the two-pack from the CLI.
3. Every role runs in its own chained worktree; teardown is clean for a completed run, and `resume` recovers a killed run.
4. Every handoff is a driver-written, git-committed JSON artifact; `state.json` reconstructs the run from disk alone.
5. The dashboard shows timeline, commits, approvals and cleanup; `--json` emits the contract.
6. No experimental Copilot features; CLI, Git and VS Code versions recorded.
7. A second repository adopts the factory with no per-repo configuration.

---

## 10. References

- VS Code custom agents: https://code.visualstudio.com/docs/agent-customization/custom-agents.md
- VS Code tools with agents (Copilot harness): https://code.visualstudio.com/docs/agents/run/tools.md
- GitHub Docs, custom agents configuration: https://docs.github.com/en/copilot/reference/custom-agents-configuration
- GitHub Docs, CLI programmatic reference: https://docs.github.com/pt/copilot/reference/copilot-cli-reference/cli-programmatic-reference
- GitHub Docs, configure Copilot CLI: https://docs.github.com/en/copilot/how-tos/copilot-cli/set-up-copilot-cli/configure-copilot-cli
- git-worktree: https://www.kernel.org/pub/software/scm/git/docs/git-worktree.html
- VS Code 1.141 coverage: https://visualstudiomagazine.com/articles/2026/10/13/vs-code-1-141-brings-copilot-codex-sessions-together-tightens-ai-agent-security.aspx
- copilot-cli issues #452, #2475, #2309, #3297 (agent discovery and path-scope bug reports): https://github.com/github/copilot-cli/issues
- Design inspirations: Sandcastle (Matt Pocock), SwarmForge (Uncle Bob).

---

## 11. Change Log

| Version | Date | Change |
|---|---|---|
| 1.0 | 2026-10-20 | Frozen baseline |
| 1.1 | 2026-10-20 | Review refinements: driver-only dispatch, driver-written git-backed handoffs, chained worktrees, run-root layout, failure and recovery contracts, rewritten guidelines |
| 1.2 | 2026-10-20 | Greenfield build order (driver built in Phase 2 with a stand-in `copilot` for tests; orchestrator moved to Phase 3; dashboard to Phase 4); verified items moved to baseline (E1–E5); VS Code 1.141 Copilot harness as target; allow-flag dispatch and audit-based confinement stated as accepted risk; pack aliases with default six-pack and deterministic longest-match resolution; probes reduced to the remaining unknowns |

---

# Open verifications

These are the only unverified claims. All run in Phase 0.3 as hand-run probes with no factory tool, except where noted.

1. **Deny flags with allow flags:** whether `--deny-tool` patterns (for example `git worktree`, `git push`, branch deletion) still block when allow flags are set. This decides whether confinement has any prevention layer or is audit-only.
2. **Minimal allow set:** whether `--allow-all-tools --allow-all-paths` (without `--allow-all-urls`) runs headless without prompts, or `--allow-all` is required.
3. **Report extraction:** whether `-s` output reliably gives the role's final JSON message, and what exit code and output the CLI produces on crash, tool failure and timeout. This defines `crashed` versus `no_report`.
4. **Linked-worktree ref mutation:** whether an agent in a linked worktree can alter other branches or the main repo's refs (expected from Git's design, not verified here). This drives the audit's ref snapshot.
5. **Long-run launch from VS Code:** whether the orchestrator can run a command lasting longer than a full six-pack run via the terminal tool without timeout or approval stalls. Probe with a stand-in `sleep` script. If it fails, add `--detach` and `factory status`.
6. **Per-role timeout default:** 30 minutes is a placeholder, calibrated from measured runs in Phase 2.4 and 2.5.
7. **Windows teardown behaviour:** verifiable only in a Windows run (Phase 2.2 and 4.2).
8. **Copilot sandboxing in 1.141:** whether the new sandbox applies to the Copilot CLI processes the driver spawns, and whether it could replace audit-only confinement. This is research for Phase 5, not a v1 dependency.
