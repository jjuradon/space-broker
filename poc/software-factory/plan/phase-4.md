# Phase 4 — Post-run dashboard and second-repo adoption

**Goal.** Make any run readable from disk artifacts alone, make failures actionable, and prove the factory needs nothing in a second repository.

### Step 4.1 — `factory dashboard`

1. `src/factory/dashboard.py` — `render(run_dir, console, repo=None)` builds its view from `derive_state(run_dir)` plus handoff files and events. If `state.json` exists and differs from the derived state, it prints a `state.json differs from derived state` warning. Sections, in order:
   - **Header:** run id, pack and resolution (reason, matched alias), request path, status (coloured), start, finish and duration.
   - **Timeline:** one row per step with role, status, attempt, 7-character commit, duration from the handoff's `exit.duration_s`, and handoff file name. Retried steps show every attempt.
   - **Approvals:** step, decision, time.
   - **Checks:** counts of `warning` and `audit_violation` events, with the first three messages.
   - **Cleanup:** created, removed, dirty paths.
2. `factory dashboard --run-id ID [--json] [--repo PATH]`: `--json` prints exactly the output of `factory status --json`.
3. Git calls in the dashboard are optional. With the repo unavailable, the worktree-liveness lines print `unknown` and everything else still renders.
4. `tests/test_dashboard.py`: render with `Console(record=True, width=100)` and compare `export_text()` to golden files for six states (completed six-pack, awaiting approval, blocked `review_failed`, failed `timeout`, failed `audit_violation` with lingering worktrees, retried step), plus a `--json` equality test and an offline test (the repo directory removed).

### Verifications — Step 4.1

- **V4.1-a** `python -m pytest tests/test_dashboard.py -q`. Pass: 8 passed.
- **V4.1-b** After a real two-pack run, `factory dashboard --run-id <id>`. Pass: two roles are shown; each short commit equals `git rev-parse --short=7 factory/<id>/<role>`; each duration equals the handoff's `exit.duration_s`; cleanup shows created 2, removed 2.
- **V4.1-c** `factory dashboard --run-id <id> --json | python tools/validate_json.py state -`. Pass: exit 0.
- **V4.1-d** Offline. Copy a finished run directory elsewhere, remove the repo, run the dashboard on the copy. Pass: header, timeline, commits, approvals and cleanup render without error.

### Step 4.2 — Failure report

For runs whose status is `blocked` or `failed`, append a **Failure report** section:

- the blocking step, role and attempt; the `failure_class` or the blocked `code` and `detail`; the handoff's `notes`;
- the path of the deliverable, if any;
- the last 20 lines of that attempt's `.stderr` log;
- audit violations from events;
- lingering worktrees of this run (from the run's `wt/` directory and, when the repo is available, `git worktree list --porcelain`), each with a dirty flag;
- a **Next command** line from this table: `escalate_pack` → `factory run --request <request> --pack six-pack`; `review_failed` and `tests_failing` → read the deliverable, fix, start a new run; `needs_human` and `spec_ambiguous` → answer the question, start a new run; `approval_rejected` → edit the request, start a new run; `crashed`, `timeout`, `no_report`, `invalid_report`, `unknown_status`, `missing_commit`, `no_changes` → `factory resume --run-id <id>`; `audit_violation` → inspect the lingering worktree, then `factory resume --run-id <id>`; awaiting approval → `factory approve --run-id <id>`; dirty cleanup → `factory clean --run-id <id>`.

### Verifications — Step 4.2

- **V4.2-a** The blocked golden shows blocking role, reason code and detail, deliverable path and a Next command line. Pass: all four present.
- **V4.2-b** Completeness test: iterate every `failure_class` and every blocked `code` from the schemas. Pass: each yields a non-empty Next command.
- **V4.2-c** Real lingering worktree. Using the fake, create a failed run with a dirty worktree. Pass: every worktree path printed exists on disk, and the dirty one is flagged.
- **V4.2-d** `factory clean --run-id <id>` as printed. Pass: worktrees are removed, a `wip: factory clean` commit exists on the dirty branch, and the dashboard then shows cleanup with no dirty paths.

### Step 4.3 — Second-repository adoption and concurrency

1. Create a second fixture repo in a different parent directory (`python tools/make_fixture_repo.py /tmp/p4b`). Install nothing new: no per-repo files, no per-repo settings.
2. Touch a marker file (`touch /tmp/p4b/marker`), then run one two-pack and one six-pack (with approve and resume) on it with the real CLI.
3. Run three fake-driver runs at once on one repo (shell `&` or three terminals).

### Verifications — Step 4.3

- **V4.3-a** Both runs reach `completed`. Pass: `git status --porcelain` in the repo is empty; `git rev-parse main` is unchanged; no new tracked files exist on `main`.
- **V4.3-b** `find /tmp/p4b/fx -newer /tmp/p4b/marker -not -path '*/.git/*'` prints nothing (the working tree was never touched), and `.git/worktrees` is absent or empty after teardown.
- **V4.3-c** The run directories are under `/tmp/p4b/.factory/fx/`; `grep -rn 'factory' /tmp/p4b/fx --include='*' -l --exclude-dir=.git` finds no factory configuration files in the repo (the fixture's own request files excepted).
- **V4.3-d** Concurrency. Pass: three runs exit 0; run ids are distinct; branch sets `factory/<id>/*` are disjoint; no worktree path collides; each run's `state.json` validates.

**Gate 4.** V4.1 to V4.3 pass; `git tag phase-4`.
