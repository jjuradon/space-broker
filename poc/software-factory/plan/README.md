# Software Factory — Detailed Implementation Plan

Implements Project Plan v1.2 · Greenfield build · 2026-10-20

## 1. How to use this plan

Every phase has three parts: **Steps** (what to create, with file contents), **Verifications** (exact commands with explicit pass criteria) and a **Gate** (every verification must pass before the next phase starts).

- Verification IDs are `V<phase>.<step>-<letter>`. A verification passes only if every `Pass:` criterion holds. A failure is fixed in place, or the plan is amended and the amendment recorded in `docs/decisions.md` before continuing.
- Commands run from the source repo root (`software-factory/`) unless a step says otherwise. Shell examples are POSIX; on Windows use PowerShell equivalents.
- `copilot` means the GitHub Copilot CLI binary recorded in `docs/baseline.md`.
- Code in this plan is the intended file content. Where a module is described rather than listed, the description is its binding specification and the listed tests are its acceptance criteria.
- Nothing exists yet. Phases are ordered so that no step uses a tool that a later phase builds. Driver code is tested against a stand-in `copilot` script first and the real CLI second.

## 2. Deviations from Project Plan v1.2

Detailing the plan exposed gaps. These changes to v1.2 take effect when this plan is approved.

1. **`deliverable`.** Role reports and handoffs gain `deliverable`: markdown text in the report, and in the handoff a path under `_handoffs/deliverables/` or null. Read-only roles produce text (a specification, review findings) that has nowhere else to live.
2. **Sentinel-delimited reports.** The role's final message ends with a block between `<<<FACTORY_REPORT>>>` and `<<<END_FACTORY_REPORT>>>`. The driver extracts the last block strictly. Probe P3 checks that agents comply.
3. **Full 40-character SHAs** in handoffs and state.
4. **New blocked codes:** `review_failed`, `tests_failing`, `approval_rejected`, in addition to `escalate_pack`, `needs_human`, `spec_ambiguous`.
5. **`approval_after` in `packs.json`.** If a listed role reports `completed`, the driver records `needs_approval`. An agent cannot skip the gate.
6. **Edit roles are told not to commit.** The driver commits. An agent commit is still accepted.
7. **Audit refinements.** For read-only roles, tracked-file changes, staged changes and new commits are violations; untracked files are a warning (test runners write caches). Changes in the main repo's own working tree are a warning, not a violation, because you may be editing there. Any ref change other than the role's own branch is a violation.
8. **Teardown unlocks before removing.** Git needs `--force` twice to remove a locked worktree ([git-worktree docs](https://www.kernel.org/pub/software/scm/git/docs/git-worktree.html)); the driver never uses `--force`.
9. **New `factory status` command** (Phase 2.7) so Phase 3 does not depend on the Phase 4 dashboard. Exit code 2 means usage error.
10. **New `dispatch.json`** (flags and limits decided by Phase 0 probes) and **probe P6** (large-prompt delivery).

## 3. Repository layout (target state)

```
software-factory/
├── pyproject.toml  README.md  .gitignore
├── scripts/install.py
├── agents/                       six role profiles (orchestrator added in Phase 3)
├── skill/software-factory/       SKILL.md  CONSTITUTION.md  HANDOFF-SCHEMA.md
├── schemas/                      role-report  handoff  run  state  packs  (.schema.json)
├── src/factory/
│   ├── __init__.py  cli.py  errors.py  config.py  packs.py  preflight.py
│   ├── runctx.py  events.py  gitops.py  worktrees.py  report.py  audit.py
│   ├── handoffs.py  dispatch.py  runner.py  state.py  dashboard.py
│   ├── data/      packs.json  dispatch.json
│   └── prompts/   dispatch.md.tmpl
├── tools/   make_fixture_repo.py  fake_copilot.py  slow_cmd.py  make_prompt.py
│            extract_probe.py  validate_json.py  lint_agents.py  probe_agents/
├── tests/   conftest.py  fixtures/{valid,invalid}/  test_*.py
└── docs/    baseline.md  probes.md  decisions.md
```

## 4. Phase 0 - [phase-0](./phase-0.md)

## 5. Phase 1 - [phase-1](./phase-1.md)

## 6. Phase 2 - [phase-2](./phase-2.md)

## 7. Phase 3 - [phase-3](./phase-3.md)

## 8. Phase 4 - [phase-4](./phase-4.md)

## 9. Phase 5 — Deferred

Not planned in detail here and not started until Gate 4: real-time dashboard, Node front end consuming `state.json`, deeper Agents window integration, evaluating Copilot sandboxing as a prevention layer (Open verification 8), and Windows hardening if Windows becomes a target. None of it may change a Phase 0–4 schema except by adding optional fields.

## 10. Final acceptance

The project is accepted when every Success Criterion from Project Plan v1.2 has the evidence below, collected in `docs/acceptance.md`:

- **S1** (six-pack from VS Code, only the approval stop): V3.2-a through V3.2-f.
- **S2** (two-pack bug fix from the CLI): V3.3-a, V3.3-b.
- **S3** (isolated chained worktrees, clean teardown, recovery): V2.2-a, V2.4-a, V2.5-b, V2.6-a.
- **S4** (driver-written, git-committed handoffs; state from disk alone): V2.3-d, V2.7-a, V2.7-e, V4.1-d.
- **S5** (dashboard timeline, commits, cleanup, `--json`): V4.1-a through V4.1-c.
- **S6** (no experimental features, versions recorded): V0.2-a, V2.3-e, and `grep -rniE 'experimental|/fleet' src agents skill` printing nothing.
- **S7** (second repository, zero per-repo configuration): V4.3-a through V4.3-c.

**Release steps.** Update the README quick start (`pip install .`, `python scripts/install.py`, first run), tag `v0.1.0`, and record the baseline versions in the tag message.

**Upgrade procedure (risk R2).** When the Copilot CLI, Git or VS Code version changes: update `docs/baseline.md`; re-run probes P1, P2, P3 and P3b with the new versions; re-run `python -m pytest -q` and one real two-pack run (V2.4-c); only then raise `min_copilot_version` in `dispatch.json`.

## Appendix A — Reference tables

**Exit codes.** 0 completed · 2 usage error · 10 blocked · 20 awaiting approval · 30 failed · 40 preflight failure · 137 test-only crash injection.

**Handoff statuses.** `completed`, `blocked`, `needs_approval` (reported by agents); `failed` (assigned by the driver, with a `failure_class`).

**Failure classes.** `crashed`, `timeout`, `no_report`, `invalid_report`, `unknown_status`, `missing_commit`, `audit_violation`, `no_changes`.

**Blocked codes.** `escalate_pack`, `needs_human`, `spec_ambiguous`, `review_failed`, `tests_failing`, `approval_rejected`.

**Run directory.** `<repo>/../.factory/<repo-name>/<run-id>/` containing `run.json`, `events.jsonl`, `state.json`, `run.lock` (while executing), `wt/<role>[-a<n>]/`, `_handoffs/` (git repo with `deliverables/` and `quarantine/`), `approvals/step-<NN>.json`, `logs/`, `prompts/`.

**Branches.** `factory/<run-id>/<role>` and `factory/<run-id>/<role>-a<n>` for retries. The result branch is the last role's branch.

**Files by phase.** Phase 0: `pyproject.toml`, `scripts/install.py`, `tools/` probe kit, `docs/baseline.md`, `docs/probes.md`, `docs/decisions.md`, `dispatch.json`. Phase 1: schemas, `packs.json`, six agent profiles, `lint_agents.py`, prompt template, fixtures and tests. Phase 2: `errors`, `config`, `packs`, `runctx`, `events`, `preflight`, `cli`, `gitops`, `worktrees`, `report`, `audit`, `handoffs`, `dispatch`, `runner`, `state`, `fake_copilot.py` and their tests. Phase 3: orchestrator profile, skill files, schema doc generator. Phase 4: `dashboard.py` and its tests, `docs/acceptance.md`.
