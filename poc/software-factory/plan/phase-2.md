# Phase 2 — The Python driver, built from scratch

**Goal.** A tested driver that creates runs, chains worktrees, dispatches roles, validates and writes handoffs, audits, resumes and derives state. Each increment is tested with the stand-in `copilot` first; the real CLI is used only in the increments that say so.

### Step 2.1 — Skeleton: CLI, config, alias resolver, run directory, preflight

1. `src/factory/errors.py`:

```
EXIT_OK, EXIT_USAGE, EXIT_BLOCKED, EXIT_AWAITING, EXIT_FAILED, EXIT_PREFLIGHT = 0, 2, 10, 20, 30, 40

class FactoryError(Exception):
    code = EXIT_FAILED

class UsageError(FactoryError):
    code = EXIT_USAGE

class UnknownPack(UsageError):
    pass

class PreflightError(FactoryError):
    code = EXIT_PREFLIGHT
```

2. `src/factory/config.py`:

```
import json, os
from importlib import resources

def load_json_data(name):
    return json.loads(resources.files('factory').joinpath('data', name).read_text(encoding='utf-8'))

def load_dispatch():
    override = os.environ.get('FACTORY_DISPATCH_JSON')
    if override:
        with open(override, encoding='utf-8') as fh:
            return json.load(fh)
    return load_json_data('dispatch.json')

def copilot_cmd(cfg):
    b = cfg['copilot_bin']
    return list(b) if isinstance(b, list) else [b]
```

Add `min_copilot_version` and `min_git_version` (values copied from `docs/baseline.md`) to `src/factory/data/dispatch.json`.

3. `src/factory/packs.py` — the alias resolver. Matching is whole-phrase and longest-first, and a matched span is blanked so `without deep flow` cannot also match `deep flow`:

```
from dataclasses import dataclass
from .config import load_json_data
from .errors import UnknownPack

@dataclass
class Resolution:
    pack: str
    matched_alias: str | None
    reason: str

def normalize(text):
    chars = [c if c.isalnum() else ' ' for c in (text or '').lower()]
    return ' '.join(''.join(chars).split())

def load_packs():
    return load_json_data('packs.json')

def resolve_pack(text, cfg=None, explicit=None):
    cfg = cfg or load_packs()
    entries = []
    for name, pack in cfg['packs'].items():
        for alias in pack['aliases']:
            entries.append((normalize(alias), name, alias))
    if explicit:
        n = normalize(explicit)
        for alias_n, name, _ in entries:
            if alias_n == n:
                return Resolution(name, explicit, 'explicit')
        raise UnknownPack(f'unknown pack or alias: {explicit}')
    work = ' ' + normalize(text) + ' '
    hits = {}
    for alias_n, name, alias in sorted(entries, key=lambda e: -len(e[0])):
        needle = ' ' + alias_n + ' '
        pos = work.find(needle)
        while pos >= 0:
            hits.setdefault(name, alias)
            work = work[:pos] + ' ' * len(needle) + work[pos + len(needle):]
            pos = work.find(needle)
    if not hits:
        return Resolution(cfg['default'], None, 'default')
    if len(hits) > 1:
        return Resolution(cfg['default'], None, 'conflict_defaulted')
    name, alias = next(iter(hits.items()))
    return Resolution(name, alias, 'alias')
```

4. `src/factory/runctx.py`:

```
import json, os, secrets
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path

@dataclass
class RunContext:
    repo: Path
    run_id: str
    run_dir: Path
    base_commit: str
    pack: str
    roles: list

def utcnow():
    return datetime.now(timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ')

def new_run_id():
    return datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ') + '-' + secrets.token_hex(2)

def run_root(toplevel):
    top = Path(toplevel).resolve()
    return top.parent / '.factory' / top.name

def claim_run_dir(root):
    root.mkdir(parents=True, exist_ok=True)
    for _ in range(50):
        run_id = new_run_id()
        d = root / run_id
        try:
            d.mkdir()
        except FileExistsError:
            continue
        for sub in ('wt', '_handoffs', 'approvals', 'logs', 'prompts'):
            (d / sub).mkdir()
        return run_id, d
    raise RuntimeError('could not claim a run directory')

def atomic_write_json(path, obj):
    tmp = path.with_name(path.name + '.tmp')
    with open(tmp, 'w', encoding='utf-8') as fh:
        json.dump(obj, fh, indent=2, sort_keys=True)
        print(file=fh)
        fh.flush()
        os.fsync(fh.fileno())
    os.replace(tmp, path)
```

5. `src/factory/events.py`:

```
import json, os
from .runctx import utcnow

def append_event(run_dir, type_, **data):
    rec = {'ts': utcnow(), 'type': type_, **data}
    with open(run_dir / 'events.jsonl', 'a', encoding='utf-8') as fh:
        print(json.dumps(rec, sort_keys=True), file=fh)
        fh.flush()
        os.fsync(fh.fileno())

def read_events(run_dir):
    p = run_dir / 'events.jsonl'
    if not p.exists():
        return []
    return [json.loads(line) for line in p.read_text(encoding='utf-8').splitlines() if line.strip()]
```

Event types used across the driver: `run_created`, `pack_resolved`, `preflight_ok`, `warning`, `step_started`, `worktree_creating`, `worktree_created`, `role_dispatched`, `role_exited`, `audit_ok`, `audit_violation`, `handoff_written`, `role_completed`, `approval_recorded`, `run_blocked`, `run_failed`, `run_completed`, `worktree_removed`, `worktree_missing`, `cleanup_dirty`, `wip_committed`, `resume_started`, `role_interrupted`.

6. `src/factory/preflight.py` — `preflight(pack_roles, agents_dir=None) -> versions dict`. It raises `PreflightError` naming the failed check, and checks in this order: (a) the working directory is inside a Git repo with at least one commit; (b) `git --version` is at or above `min_git_version`; (c) `<copilot_cmd> --version` runs and is at or above `min_copilot_version` (compare the numeric dotted parts as integer tuples); (d) every role in the pack has `<agents_dir>/<role>.agent.md` (default `$COPILOT_HOME/agents`) whose `name:` equals the filename stem; (e) the run root's parent directory is writable. A dirty main working tree is not an error: it records a `warning` event `main_tree_dirty`, because uncommitted changes are not part of the base commit. It returns `{copilot, git, python, factory}` version strings for `run.json`.
7. `src/factory/cli.py` replaces the skeleton. Subcommands and arguments:

```
factory run --request PATH [--pack NAME_OR_ALIAS]
factory resolve-pack --text TEXT [--pack X] [--json]
factory resume --run-id ID [--repo PATH]
factory approve --run-id ID [--reject --reason TEXT] [--note TEXT] [--repo PATH]
factory clean --run-id ID [--repo PATH]
factory status --run-id ID [--json] [--repo PATH]
```

`main(argv)` returns an exit code: it catches `FactoryError` and prints `factory: error: <message>` to stderr, returning the error's `code`. Commands not yet implemented print `not implemented` and return 2. In this step `run` does the following: read the request file (missing, unreadable or empty raises `UsageError('no_request')`); `preflight`; resolve the pack from `--pack` (no flag means the default); claim the run directory; set `base_commit` to `HEAD`; write `run.json` atomically (it must validate against `run.schema.json`, with `steps` built from `packs.json` and `started_at` from `utcnow()`); append `run_created`, `pack_resolved` and `preflight_ok`; print `factory: run <id> pack=<pack> reason=<reason> alias=<alias or ->`; return 0. `resolve-pack --json` prints `{"pack": ..., "matched_alias": ..., "reason": ...}`.

8. `tools/fake_copilot.py` (first version, extended in Step 2.3): if `--version` is in the arguments, print `fake-copilot 9.9.9` and exit 0.
9. Tests: `tests/test_packs.py`:

```
import pytest
from factory.packs import resolve_pack
from factory.errors import UnknownPack

CASES = [
    ('Implement requests/add-oauth.md', 'six-pack', 'default'),
    ('Fix the null check without deep flow', 'two-pack', 'alias'),
    ('please do it as a simple task', 'two-pack', 'alias'),
    ('run the deep flow', 'six-pack', 'alias'),
    ('without deep flow but use the six-pack', 'six-pack', 'conflict_defaulted'),
    ('Add a simple login feature', 'six-pack', 'default'),
    ('SIMPLE   TASK, please', 'two-pack', 'alias'),
    ('no deep flow', 'two-pack', 'alias'),
    ('use the two-pack', 'two-pack', 'alias'),
    ('a simple tasks list widget', 'six-pack', 'default'),
    ('', 'six-pack', 'default'),
    (None, 'six-pack', 'default'),
]

@pytest.mark.parametrize('text,pack,reason', CASES)
def test_text_resolution(text, pack, reason):
    r = resolve_pack(text)
    assert (r.pack, r.reason) == (pack, reason)

def test_explicit_alias():
    r = resolve_pack('ignored deep flow', explicit='simple task')
    assert (r.pack, r.reason) == ('two-pack', 'explicit')

def test_explicit_unknown():
    with pytest.raises(UnknownPack):
        resolve_pack('', explicit='bogus')
```

Also add `tests/test_runctx.py` (20 threads call `claim_run_dir` on one root; assert 20 distinct directories and no exception; `atomic_write_json` leaves valid JSON and no `.tmp` file) and `tests/test_cli_run.py` (runs `factory` through `main()` in a temporary Git repo, with `FACTORY_DISPATCH_JSON` pointing to a config whose `copilot_bin` is `[sys.executable, 'tools/fake_copilot.py']`, and a stub agents directory holding the needed profiles).

### Verifications — Step 2.1

- **V2.1-a** `python -m pytest tests/test_packs.py -q`. Pass: 14 passed.
- **V2.1-b** `python -m pytest tests/test_runctx.py -q`. Pass: all pass, including 20 distinct run directories claimed in parallel.
- **V2.1-c** `factory resolve-pack --text 'fix it without deep flow' --json`. Pass: prints pack `two-pack`, matched alias `without deep flow`, reason `alias`; exit 0.
- **V2.1-d** `python -m pytest tests/test_cli_run.py -q`. Pass: `run.json` validates against the `run` schema; `base_commit` equals `git rev-parse HEAD`; every `versions` value is non-empty; `events.jsonl` holds `run_created`, `pack_resolved`, `preflight_ok` in that order.
- **V2.1-e** Exit codes in `test_cli_run.py`. Pass: a missing or empty request gives 2; `--pack bogus` gives 2; no `--pack` gives pack `six-pack`; `--pack 'simple task'` gives `two-pack`; a version below the configured floor, a missing agent profile, and a directory outside a Git repo each give 40 and the message names the failed check.
- **V2.1-f** After `factory run` on a test repo, `git status --porcelain` in that repo is empty and the run directory is outside the repo.
- **V2.1-g** If probe D4 was `detach`: `factory run --detach` is specified for Step 2.4 and a placeholder test is added now (marked expected-fail). Pass: recorded in `docs/decisions.md`.

### Step 2.2 — Worktree lifecycle

1. `src/factory/gitops.py` — every Git call in the driver goes through this module:

```
import subprocess

IDENT = ['-c', 'user.name=factory', '-c', 'user.email=factory@localhost']

class GitError(Exception):
    pass

def git(cwd, *args, check=True):
    p = subprocess.run(['git', *IDENT, *args], cwd=str(cwd), capture_output=True, text=True)
    if check and p.returncode != 0:
        raise GitError(f'git {" ".join(args)}: {p.stderr.strip()}')
    return p

def out(cwd, *args):
    return git(cwd, *args).stdout.strip()

def toplevel(cwd):
    return out(cwd, 'rev-parse', '--show-toplevel')

def head(cwd):
    return out(cwd, 'rev-parse', 'HEAD')

def is_dirty(cwd, untracked=True):
    flag = 'all' if untracked else 'no'
    return bool(out(cwd, 'status', '--porcelain', f'--untracked-files={flag}'))

def untracked_files(cwd):
    lines = out(cwd, 'status', '--porcelain', '--untracked-files=all').splitlines()
    return [line[3:] for line in lines if line.startswith('??')]

def ref_snapshot(cwd):
    snap = {}
    for line in out(cwd, 'for-each-ref', '--format=%(objectname) %(refname)').splitlines():
        sha, ref = line.split(' ', 1)
        snap[ref] = sha
    return snap

def worktree_add(repo, path, branch, start):
    git(repo, 'worktree', 'add', '--lock', '-b', branch, str(path), start)

def worktree_list(repo):
    items, cur = [], {}
    for line in out(repo, 'worktree', 'list', '--porcelain').splitlines() + ['']:
        if not line:
            if cur:
                items.append(cur)
            cur = {}
            continue
        key, _, val = line.partition(' ')
        if key == 'worktree':
            cur['path'] = val
        elif key == 'HEAD':
            cur['head'] = val
        elif key == 'branch':
            cur['branch'] = val
        elif key == 'locked':
            cur['locked'] = True
    return items

def worktree_unlock(repo, path):
    git(repo, 'worktree', 'unlock', str(path))

def worktree_remove(repo, path):
    git(repo, 'worktree', 'remove', str(path))

def worktree_prune(repo):
    git(repo, 'worktree', 'prune')

def commit_all(cwd, message):
    git(cwd, 'add', '-A')
    git(cwd, 'commit', '-m', message)
    return head(cwd)

def is_ancestor(cwd, a, b):
    return git(cwd, 'merge-base', '--is-ancestor', a, b, check=False).returncode == 0

def diff_names(cwd, a, b):
    return out(cwd, 'diff', '--name-only', f'{a}..{b}').splitlines()

def commit_exists(cwd, sha):
    return git(cwd, 'cat-file', '-e', f'{sha}^{{commit}}', check=False).returncode == 0
```

The driver never passes `--force` to any Git command.

2. `src/factory/worktrees.py`:
   - `worktree_path(ctx, role, attempt)` is `<run_dir>/wt/<role>` for attempt 1 and `<run_dir>/wt/<role>-a<n>` otherwise; the branch is `factory/<run_id>/<role>` with the same `-a<n>` suffix.
   - `create_for_step(ctx, step, role, attempt, start_commit)` appends `worktree_creating`, calls `gitops.worktree_add` (locked), then appends `worktree_created` with `{role, attempt, path, branch, start}`. The first role starts from `ctx.base_commit`; every later role starts from the previous step's recorded commit.
   - `teardown(ctx)` walks the `worktree_created` events that have no matching `worktree_removed`. For a missing path: append `worktree_missing` and run `worktree_prune`. For a dirty path (`is_dirty` with untracked files): append `cleanup_dirty` with the path and **stop**; later worktrees are left untouched. Otherwise: `worktree_unlock`, `worktree_remove`, append `worktree_removed`. Branches are never deleted.
   - `clean(ctx)` does the same walk, but a dirty worktree first gets `commit_all(path, 'wip: factory clean')` (event `wip_committed`) and is then removed.
   - Clarification of F8: a stopped teardown does not change the run's pipeline status. The run finishes `completed` with `cleanup.dirty` non-empty, exit 0, and a final line telling the user to run `factory clean`.
3. `tests/conftest.py`:

```
import pytest
from factory import gitops

@pytest.fixture
def repo(tmp_path):
    top = tmp_path / 'proj'
    top.mkdir()
    gitops.git(top, 'init', '-b', 'main')
    (top / 'README.md').write_text('hello', encoding='utf-8')
    (top / '.gitignore').write_text('__pycache__/', encoding='utf-8')
    gitops.git(top, 'add', '-A')
    gitops.git(top, 'commit', '-m', 'init')
    return top
```

4. `tests/test_worktrees.py` (uses the `repo` fixture and a `RunContext` built over a claimed run directory):
   - `test_chain`: worktree 1 from the base commit; write a file and `commit_all`; worktree 2 from that commit contains the file.
   - `test_locked`: both worktrees show `locked` in `worktree_list`.
   - `test_teardown_clean`: after teardown only the main worktree remains and both branches still exist.
   - `test_teardown_dirty_stops`: an untracked file in worktree 1 gives a `cleanup_dirty` event, worktree 1 and worktree 2 both still exist.
   - `test_clean_commits_wip`: after the dirty case, `clean` removes both; the branch of worktree 1 has a commit whose subject is `wip: factory clean`.
   - `test_partial_creation_failure`: monkeypatch `worktree_add` to raise on its third call; afterwards two worktrees exist and are locked, events hold two `worktree_created` and three `worktree_creating`, and `clean` removes both.
   - `test_missing_path`: delete a worktree directory by hand; teardown appends `worktree_missing` and the worktree list shrinks to the main worktree.
   - `test_no_force_in_source`: no file under `src/factory/` contains `--force`.

### Verifications — Step 2.2

- **V2.2-a** `python -m pytest tests/test_worktrees.py -q`. Pass: 8 passed.
- **V2.2-b** After the test run, `git worktree list` in the test repos shows no leftover worktrees. In a scratch repo, repeat the teardown manually twice (the second call is a no-op). Pass: the second teardown raises no error and appends no `worktree_removed` event.
- **V2.2-c** `grep -rn -- '--force' src/factory`. Pass: no output.
- **V2.2-d** (Windows only, if Windows is a target) Open a file from a role worktree in an editor, then run teardown. Pass: either a clean removal, or a recorded `cleanup_dirty` or error event; no crash, no data loss, and `factory clean` succeeds after the editor is closed.

**Gate 2.2.** V2.1-a through V2.2-d pass (V2.2-d only if Windows is a target); `git tag phase-2.2`.

### Step 2.3 — Single-role dispatch, report extraction, audit, handoffs

This step builds the heart of the driver. It is tested against `tools/fake_copilot.py` first and against the real CLI for one `coder` run.

**2.3.0 Housekeeping.** Schemas must ship inside the package so an installed `factory` can read them. Run `git mv schemas src/factory/data/schemas`; add `'data/schemas/*.json'` to `package-data` in `pyproject.toml`; update the paths in `tests/test_schemas.py` and `tools/validate_json.py`; add to `config.py`:

```
def load_schema(kind):
    return load_json_data('schemas/' + kind + '.schema.json')
```

Also add `current_branch(cwd)` to `gitops.py`: `return out(cwd, 'symbolic-ref', '--short', 'HEAD')`. (Deviation 11: schemas live in package data.) Re-run `pytest tests/test_schemas.py -q` before continuing; it must still show 11 passed.

**2.3.1 `tools/fake_copilot.py` (full version).** Behaviour is chosen by `FAKE_SCENARIO`, overridable per role with `FAKE_SCENARIO_<ROLE>` (upper case, `-` becomes `_`). It runs in the worktree it is launched in. If `FAKE_ARGV_LOG` is set, it appends its argument list there as JSON.

```
import json, os, subprocess, sys, time

EDIT_ROLES = {'coder', 'hardender'}
GIT = ['git', '-c', 'user.name=fake', '-c', 'user.email=fake@localhost']

def report(status='completed', notes='fake run', artifacts=None, deliverable='fake deliverable', blocked=None):
    body = {'status': status, 'notes': notes, 'artifacts': artifacts or [],
            'deliverable': deliverable, 'blocked_reason': blocked}
    print('Done.')
    print('<<<FACTORY_REPORT>>>')
    print(json.dumps(body))
    print('<<<END_FACTORY_REPORT>>>')

def raw_block(text):
    print('<<<FACTORY_REPORT>>>')
    print(text)
    print('<<<END_FACTORY_REPORT>>>')

def main(argv):
    if '--version' in argv:
        print('fake-copilot 9.9.9')
        return 0
    log = os.environ.get('FAKE_ARGV_LOG')
    if log:
        with open(log, 'a', encoding='utf-8') as fh:
            print(json.dumps(argv), file=fh)
    role = [a.split('=', 1)[1] for a in argv if a.startswith('--agent=')][0]
    key = 'FAKE_SCENARIO_' + role.upper().replace('-', '_')
    scenario = os.environ.get(key, os.environ.get('FAKE_SCENARIO', 'ok'))
    if scenario == 'crash':
        print('boom', file=sys.stderr)
        return 3
    if scenario == 'hang':
        time.sleep(600)
        return 0
    if scenario == 'no_report':
        print('finished, but no report block')
        return 0
    if scenario == 'bad_json':
        raw_block('{not json')
        return 0
    if scenario == 'invalid_report':
        raw_block(json.dumps({'status': 'completed'}))
        return 0
    if scenario == 'unknown_status':
        report(status='weird')
        return 0
    if scenario == 'blocked':
        report(status='blocked', blocked={'code': 'review_failed', 'detail': 'fake blocker'})
        return 0
    if scenario == 'needs_approval':
        report(status='needs_approval')
        return 0
    arts = []
    if scenario == 'dirty_readonly':
        with open('README.md', 'a', encoding='utf-8') as fh:
            print('tamper', file=fh)
    elif scenario == 'untracked_readonly':
        with open('cache.tmp', 'w', encoding='utf-8') as fh:
            print('x', file=fh)
    elif scenario == 'ref_tamper':
        subprocess.run(GIT + ['branch', '-D', 'victim'])
    elif scenario == 'no_changes':
        pass
    elif role in EDIT_ROLES:
        name = role + '.txt'
        with open(name, 'w', encoding='utf-8') as fh:
            print('written by ' + role, file=fh)
        arts = [name]
        if scenario == 'agent_commit':
            subprocess.run(GIT + ['add', '-A'])
            subprocess.run(GIT + ['commit', '-m', 'agent commit'])
    report(artifacts=arts)
    return 0

if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
```

**2.3.2 `src/factory/report.py`.** The extractor from `tools/extract_probe.py`, strict and schema-checked:

````
import json
from jsonschema import Draft202012Validator
from .config import load_schema

class ReportError(Exception):
    def __init__(self, failure_class, message):
        super().__init__(message)
        self.failure_class = failure_class

def extract_report(text, start, end):
    i = text.rfind(start)
    if i < 0:
        raise ReportError('no_report', 'start marker not found')
    j = text.find(end, i + len(start))
    if j < 0:
        raise ReportError('no_report', 'end marker not found')
    body = text[i + len(start):j].strip()
    if body.startswith('```'):
        body = body.strip('`').strip()
        if body.startswith('json'):
            body = body[4:].strip()
    try:
        obj = json.loads(body)
    except ValueError as exc:
        raise ReportError('invalid_report', str(exc))
    errs = list(Draft202012Validator(load_schema('role-report')).iter_errors(obj))
    if errs:
        raise ReportError('invalid_report', errs[0].message)
    return obj
````

**2.3.3 `src/factory/audit.py`.** Snapshot before dispatch, compare after:

```
from dataclasses import dataclass
from pathlib import Path
from . import gitops

READ_ONLY = {'specifier', 'cleaner', 'architect', 'qa'}

@dataclass
class Snapshot:
    refs: dict
    main_head: str
    main_dirty: bool
    others: dict

def take_snapshot(repo, own_path):
    repo = Path(repo).resolve()
    own = Path(own_path).resolve()
    others = {}
    for wt in gitops.worktree_list(repo):
        p = Path(wt['path']).resolve()
        if p == repo or p == own:
            continue
        others[str(p)] = (wt.get('head'), gitops.is_dirty(p))
    return Snapshot(gitops.ref_snapshot(repo), gitops.head(repo),
                    gitops.is_dirty(repo, untracked=False), others)

def audit(role, before, after, repo, wt, start, branch):
    violations, warnings = [], []
    own_ref = 'refs/heads/' + branch
    for ref in sorted(set(before.refs) | set(after.refs)):
        if ref != own_ref and before.refs.get(ref) != after.refs.get(ref):
            violations.append('ref_changed:' + ref)
    if before.main_head != after.main_head:
        violations.append('main_head_changed')
    if before.others != after.others:
        violations.append('other_worktree_changed')
    if before.main_dirty != after.main_dirty:
        warnings.append('main_tree_changed')
    head = gitops.head(wt)
    if gitops.current_branch(wt) != branch:
        violations.append('branch_switched')
    if not gitops.is_ancestor(wt, start, head):
        violations.append('head_rewound')
    if role in READ_ONLY:
        if head != start:
            violations.append('readonly_commit')
        if gitops.is_dirty(wt, untracked=False):
            violations.append('readonly_modified')
    if gitops.untracked_files(wt):
        warnings.append('untracked_files')
    return violations, warnings
```

**2.3.4 `src/factory/handoffs.py`.**

- `init_handoff_repo(run_dir)`: `git init -b main` inside `_handoffs/`.
- `handoff_name(role, step, attempt)` returns `<role>-step-<NN>-a<attempt>.json` (the file name rule from Plan §5.2, with `task_id` = `step-<NN>`).
- `write_handoff(ctx, rec, deliverable_text)`: validate `rec` against `handoff.schema.json` (a failure here is a driver bug and raises); write the deliverable (if any) to `_handoffs/deliverables/<role>-step-<NN>-a<attempt>.md` and set `rec['deliverable']` to that relative path; write the JSON to a `.tmp` file, then move it into place only if the final name does not exist (raise `FileExistsError` otherwise); `git add -A` and commit `handoff <name>` inside `_handoffs`; append `handoff_written`.
- `scan_steps(run_dir)` returns `{step: {'latest': rec, 'attempts': [recs sorted by attempt]}}`. A file that fails to parse or validate is moved to `_handoffs/quarantine/` with a `warning` event and ignored.
- `load_deliverable(run_dir, rec)` returns the text or `'none'`.

**2.3.5 `src/factory/dispatch.py` — `dispatch_role(ctx, cfg, packs, step, role, attempt, start_commit, prior)`.** Ordered algorithm (each numbered action appends the stated event):

1. Append `step_started`; crash point `after_step_started`.
2. Create the worktree (`worktrees.create_for_step`); crash point `after_worktree_created`.
3. `before = take_snapshot(...)`.
4. Render the prompt with `string.Template` from `prompts/dispatch.md.tmpl`. `prior_handoff` is a compact JSON of the previous record (`role`, `status`, `notes`, `commit`, `artifacts`) or `none`; `prior_deliverable` is its text or `none`. If the prompt is longer than `prompt_inline_max`, write it to `<run>/prompts/<role>-step-<NN>-a<n>.md` and pass `Read the task from <absolute path> and follow it exactly.` instead.
5. Build argv: `copilot_cmd + ['--agent=' + role, '-p', prompt, '-s'] + allow_flags + deny_flags`.
6. Run it with `run_process` (below), stdout and stderr to `logs/<role>-step-<NN>-a<n>.stdout` and `.stderr`, limit `role_timeout_s`. Append `role_dispatched` (with the child pid, from `on_start`) and call crash point `after_dispatch_start` in `on_start`; after exit append `role_exited` `{code, timed_out, duration_s}`.
7. Classify, in this order: timed out → `failed/timeout`; nonzero exit → `failed/crashed`; otherwise `extract_report` on the stdout file, and a `ReportError` gives `failed/no_report` or `failed/invalid_report`.
8. If the report status is not one of `completed`, `blocked`, `needs_approval` → `failed/unknown_status`. If the role is in the pack's `approval_after` and the status is `completed`, record `needs_approval` and append the note `approval gate enforced by driver`.
9. Audit: `violations, warnings = audit(...)`. Warnings are appended as `warning` events. Any violation → `failed/audit_violation` with the violations in `notes`; append `audit_violation`, otherwise `audit_ok`.
10. Commit rule for `completed` and `needs_approval`: a read-only role records `commit = start_commit`. An edit role (`coder`, `hardender`): if the tree is dirty (untracked files included), `commit_all(wt, 'factory(<role>): <first 60 chars of notes>')`; else if `HEAD` differs from the start commit use `HEAD` (the agent committed); else `failed/no_changes`. Then require `commit_exists` and `is_ancestor(start, commit)`, otherwise `failed/missing_commit`. For `blocked` and `failed` records `commit` is null and the worktree is left as it is.
11. For edit roles, `artifacts` is `diff_names(start, commit)` (authoritative). If it differs from the report's list, append `warning` `artifacts_mismatch`. Read-only roles have `artifacts = []`.
12. Build the record with every field of `handoff.schema.json`: `written_by` is `driver`; `parent_handoff` and `supersedes` are file names or null; `worktree` is `wt/<dir>`; `exit` comes from step 6; `blocked_reason` is copied from the report for `blocked`. Call `write_handoff` (crash points `after_handoff_tmp`, `after_handoff_written`), append `role_completed`, return the record.

The process helper:

```
import os, signal, subprocess, time

def kill_tree(proc):
    if os.name == 'nt':
        subprocess.run(['taskkill', '/F', '/T', '/PID', str(proc.pid)], capture_output=True)
    else:
        try:
            os.killpg(proc.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
    proc.wait()

def run_process(argv, cwd, stdout_path, stderr_path, timeout_s, on_start=None):
    started = time.time()
    with open(stdout_path, 'wb') as so, open(stderr_path, 'wb') as se:
        kw = {'stdin': subprocess.DEVNULL, 'stdout': so, 'stderr': se, 'cwd': str(cwd)}
        if os.name == 'nt':
            kw['creationflags'] = subprocess.CREATE_NEW_PROCESS_GROUP
        else:
            kw['start_new_session'] = True
        proc = subprocess.Popen(argv, **kw)
        if on_start:
            on_start(proc.pid)
        timed_out = False
        try:
            proc.wait(timeout=timeout_s)
        except subprocess.TimeoutExpired:
            timed_out = True
            kill_tree(proc)
    return {'code': None if timed_out else proc.returncode,
            'timed_out': timed_out, 'duration_s': round(time.time() - started, 1)}
```

And the test-only crash hook (inert unless the environment variable is set):

```
import os

def crash_point(name, step):
    if os.environ.get('FACTORY_CRASH_AT') == f'{name}:{step}':
        os._exit(137)
```

**2.3.6 Tests, `tests/test_dispatch.py`** (fixtures: a test repo, a `RunContext`, a config whose `copilot_bin` is `[sys.executable, 'tools/fake_copilot.py']`, empty allow and deny lists, `role_timeout_s` of 5). Each case dispatches one role and checks the handoff:

- `ok` / coder: `completed`, `commit` differs from start, `artifacts == ['coder.txt']`.
- `ok` / cleaner: `completed`, `commit == start`, `artifacts == []`.
- `blocked` / cleaner: `blocked`, reason `review_failed`, `commit` null.
- `needs_approval` / specifier: `needs_approval`.
- `ok` / specifier in a six-pack: recorded `needs_approval`, notes contain `approval gate enforced`.
- `no_report`, `bad_json`, `invalid_report`, `unknown_status`: `failed` with `no_report`, `invalid_report`, `invalid_report`, `unknown_status`.
- `crash`: `failed/crashed`, `exit.code == 3`.
- `hang` with `role_timeout_s = 2`: `failed/timeout`, `exit.timed_out` true, and no `fake_copilot.py` process remains afterwards.
- `dirty_readonly` / cleaner: `failed/audit_violation` with `readonly_modified`.
- `untracked_readonly` / qa: `completed` plus a `warning` event for `untracked_files`.
- `ref_tamper` / cleaner (branch `victim` created beforehand): `failed/audit_violation` naming `ref_changed:refs/heads/victim`.
- `agent_commit` / coder: `completed`, `commit` equals the agent's own `HEAD`.
- `no_changes` / coder: `failed/no_changes`.
- Indirection: `prompt_inline_max = 100`; the `-p` value logged in `FAKE_ARGV_LOG` starts with `Read the task from`, and the named file exists.
- Every record validates against `handoff.schema.json`; the `_handoffs` repo has exactly one commit per handoff; writing the same role, step and attempt twice raises `FileExistsError`.
- Quarantine: a truncated handoff file is moved to `quarantine/` by `scan_steps` and a `warning` event is written.

Also create `tests/test_real_coder.py`, skipped unless `FACTORY_REAL=1`: it copies the fixture repo, dispatches the real `coder` with `requests/fix-null.md` as the request, and checks the result.

### Verifications — Step 2.3

- **V2.3-a** `python -m pytest tests/test_dispatch.py -q`. Pass: all cases pass (at least 17 tests).
- **V2.3-b** `FACTORY_REAL=1 python -m pytest tests/test_real_coder.py -q` on the fixture. Pass: handoff `completed`; in the coder worktree `python -m pytest -q` reports 3 passed; `artifacts` contains `src/parse.py`; `git -C <fixture> status --porcelain` is empty. Run it three times; at least 2 of 3 must pass, and any failure must be a defined `failed` or `blocked` handoff, never a traceback.
- **V2.3-c** Hang test: after the timeout case, `pgrep -f fake_copilot` (Windows: `tasklist | findstr python`) shows no leftover fake process.
- **V2.3-d** In a run directory produced by the tests, `git -C _handoffs log --oneline | wc -l` equals the number of handoff files, and every `logs/*.stdout` file exists.
- **V2.3-e** `grep -rn 'allow-all' src/factory` finds no hard-coded flag; flags only come from `dispatch.json`.

### Step 2.4 — Runner and the two-pack end to end

1. `src/factory/runner.py` — `execute_run(ctx, cfg, packs)` (simple form; Step 2.5 generalises it):
   - For each role in `ctx.roles` in order, the start commit is `base_commit` for step 1 and the previous record's `commit` afterwards. Call `dispatch_role` with attempt 1 and the previous record as `prior`.
   - `completed` continues. `needs_approval` stops the loop for now (Step 2.5 adds the approval handling and the `EXIT_AWAITING` return); no `run_blocked` event is written. `blocked` appends `run_blocked` and returns `EXIT_BLOCKED`. `failed` appends `run_failed` and returns `EXIT_FAILED`.
   - When every role has completed: append `run_completed` with the result branch (the last role's branch), call `worktrees.teardown`, and return `EXIT_OK`.
   - The final stdout line is `factory: run <id> status=<status> exit=<code> cleanup=<ok|dirty>`.
2. Wire `run` in `cli.py` so it creates the run (Step 2.1) and then calls `execute_run`.
3. If decision D4 was `detach`: implement `factory run --detach`. The parent claims the run, writes `run.json`, spawns `factory _execute --run-id <id>` as a detached process (`start_new_session=True` on POSIX; `DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP` on Windows) logging to `logs/driver.log`, prints the run id, and exits 0 within 5 seconds.
4. **Calibrate the timeout.** After three real two-pack runs, set `role_timeout_s` to twice the longest observed `exit.duration_s`, rounded up to a multiple of 300 seconds and never below 900. Record the numbers in `docs/decisions.md`.

### Verifications — Step 2.4

- **V2.4-a** (fake) Two-pack with `FAKE_SCENARIO=ok`. Pass: exit 0; two handoffs (`coder`, `cleaner`), both valid; the cleaner's `commit` equals the coder's; `git worktree list` shows only the main worktree; branches `factory/<id>/coder` and `factory/<id>/cleaner` exist; the final line is printed.
- **V2.4-b** (fake) Two-pack with `FAKE_SCENARIO_CODER=blocked`. Pass: exit 10; no cleaner handoff; the coder worktree still exists and is locked.
- **V2.4-c** (real) On a fresh fixture, `factory run --request requests/fix-null.md --pack 'without deep flow'`, three times. Pass in at least 2 of 3: exit 0; two valid handoffs; the main branch is unchanged (`git rev-parse main` equals the base); `git worktree add ../verify factory/<id>/cleaner` followed by `python -m pytest -q` in `../verify` reports 3 passed; `git diff --name-only main..factory/<id>/cleaner` lists only `src/` or `tests/` files; `git status --porcelain` in the fixture is empty. The other runs must end in exit 10 or 30 with a handoff, not a traceback.
- **V2.4-d** `docs/decisions.md` holds the calibrated `role_timeout_s` with the three measured durations.
- **V2.4-e** (only if D4 = `detach`) `time factory run --detach ...` returns in under 5 seconds with a run id, and the run finishes on its own.

### Step 2.5 — Six-pack, approval gate and resume

1. Replace `execute_run` with the unified loop used by both `run` and `resume`. For each planned step, in order:
   - If its latest handoff is `completed`, set `prev` to it and continue.
   - If it is `needs_approval`: with no `approvals/step-<NN>.json`, return `EXIT_AWAITING`; if the approval is `rejected`, append `run_blocked` (reason `approval_rejected`) and return `EXIT_BLOCKED`; if `approved`, treat the step as completed and continue.
   - If it is `blocked`: print the reason and return `EXIT_BLOCKED`. A blocked step is never retried automatically. For `escalate_pack` the message says to start a new run with `--pack 'six-pack'`.
   - If it is `failed`, or has no handoff after a `step_started` event: dispatch it as attempt `n+1`, with the same start commit and `supersedes` set to the previous attempt's file name.
   - Otherwise dispatch attempt 1. When all steps are complete: `run_completed`, teardown, `EXIT_OK`.
2. `factory approve --run-id ID`: allowed only while the latest handoff of the first unapproved step is `needs_approval` (otherwise `UsageError`). It writes `approvals/step-<NN>.json` containing `{step, role, decision, note, reason, at}` (`decision` is `approved` or `rejected`; `--reject` needs `--reason`), appends `approval_recorded`, prints the next command (`factory resume --run-id ID`), and exits 0.
3. `factory resume --run-id ID`: loads `run.json`, appends `resume_started`, and runs the unified loop.

### Verifications — Step 2.5

All with the fake unless stated:

- **V2.5-a** Six-pack, all scenarios `ok`. Pass: exit 20 after step 1; one handoff with status `needs_approval` and the gate note; `wt/` contains only the specifier worktree.
- **V2.5-b** `factory approve` then `factory resume`. Pass: `approvals/step-01.json` exists; resume exits 0; six valid handoffs; no leftover worktrees; the result branch is `factory/<id>/qa`.
- **V2.5-c** `factory approve --reject --reason no`, then `resume`. Pass: exit 10, reason `approval_rejected`, no coder worktree was ever created.
- **V2.5-d** `FAKE_SCENARIO_ARCHITECT=blocked`. Pass: exit 10; four handoffs; four worktrees remain.
- **V2.5-e** `FAKE_SCENARIO_HARDENDER=crash`, then re-run `resume` with the scenario removed. Pass: first exit 30; after resume the hardener has `-a1` (`failed`) and `-a2` (`completed`, `supersedes` set to the `-a1` file); branches `factory/<id>/hardender` and `factory/<id>/hardender-a2` both exist; the run completes exit 0.
- **V2.5-f** `resume` on the blocked run from V2.5-d. Pass: exit 10, no new attempt created.
- **V2.5-g** `approve` on a run that is not awaiting approval. Pass: exit 2.
- **V2.5-h** (real) Six-pack on a fresh fixture with `requests/add-greeting.md` and no `--pack`: run, approve, resume, three times. Pass in at least 2 of 3: the run ends exit 0 after approve and resume; six valid handoffs; on the result branch `tests/test_greeting.py` exists and `python -m pytest -q` passes; `parse_int` is unchanged (`git diff <base>..<result> -- src/parse.py` is empty). The other runs must end in a defined blocked or failed state.

### Step 2.6 — Recovery and crash safety

1. **Write-ahead events.** `step_started` is written before the worktree is created, `worktree_creating` before the add, `role_dispatched` (with pid) before waiting, and the handoff is committed before `role_completed`. Crash points exist at `after_step_started`, `after_worktree_created`, `after_dispatch_start`, `after_handoff_tmp`, `after_handoff_written`, and `before_teardown`.
2. **Run lock.** The driver creates `run.lock` exclusively (containing its pid) when it starts executing and removes it on exit. `resume` refuses with exit 2 if the lock exists and its pid is alive; a stale lock (dead pid) is replaced.
3. **Interrupted steps.** On resume, for each step with `step_started` and no handoff: if an earlier `role_dispatched` pid is still alive and its command line contains the role name, terminate its process tree and append `orphan_killed`; append `role_interrupted`; run `worktree_prune` to drop registrations whose directories are gone; dispatch a new attempt (never reuse the old worktree or branch). Old branches are kept.
4. **Handoff integrity.** `scan_steps` quarantines unparseable or schema-invalid handoffs (Step 2.3); an interrupted write leaves a `.tmp` file, which resume deletes.
5. `tests/test_recovery.py`: a subprocess helper starts `factory run` with the fake and `FACTORY_CRASH_AT=<point>:<step>`, expects exit 137, then runs `factory resume` with no crash variable.

### Verifications — Step 2.6

- **V2.6-a** Parametrised suite: each of the six crash points at steps 1 and 2 of a two-pack (12 cases) plus `after_dispatch_start`, `after_handoff_written` and `before_teardown` at step 3 of a six-pack run after approval (3 cases). Pass: 15 passed. After `resume` completes, each case satisfies: exit 0; exactly one latest `completed` handoff per step; no duplicate handoff file names; no `*.tmp` files; every branch from an earlier attempt still exists (`git branch --list 'factory/<id>/*'`); `git worktree list` shows only the main worktree.
- **V2.6-b** Orphan test: crash at `after_dispatch_start` with the role set to `hang`. Pass: after `resume`, `pgrep -f fake_copilot` finds nothing and an `orphan_killed` event exists.
- **V2.6-c** Lock tests. Pass: a stale lock does not block `resume`; a live lock makes a second `resume` exit 2.
- **V2.6-d** Quarantine test. Pass: a truncated handoff is moved to `_handoffs/quarantine/`, its step is retried as the next attempt, and a `warning` event is recorded.
- **V2.6-e** (real, manual) Start a real two-pack run, `kill -9` the driver while the coder is running, then `factory resume`. Pass: the run completes; `git branch --list 'factory/*'` shows the first attempt's branch and the second attempt's branch.

### Step 2.7 — State derivation and `factory status`

1. `src/factory/state.py` — `derive_state(run_dir)` is a pure function of `run.json`, `events.jsonl`, `_handoffs/` and `approvals/`. It writes nothing. Rules:
   - `roles`: one entry per planned step. With a latest handoff: `{step, role, status, attempt, commit, handoff}` plus `approval` (`approved`, `rejected` or `pending`) for `needs_approval` steps. Without one: `{step, role, status: 'pending'}`.
   - Run `status`, first rule that matches: any latest step `failed` → `failed`; any latest step `blocked`, or a rejected approval → `blocked`; any `needs_approval` without an approval → `awaiting_approval`; every step completed (or approved) and a `run_completed` event → `completed`; otherwise `running`. Only the latest attempt of a step counts.
   - `finished_at`: the timestamp of the last `run_completed`, `run_blocked` or `run_failed` event, else null. `result_branch`: the last step's branch when the run is `completed`, else null.
   - `cleanup`: `worktrees_created` is the count of `worktree_created` events; `worktrees_removed` the count of `worktree_removed`; `dirty` the paths of the latest `cleanup_dirty` events not later removed.
   - Keys are emitted in a fixed order; output validates against `state.schema.json`.
2. The runner calls `atomic_write_json(run_dir / 'state.json', derive_state(run_dir))` after every step and at every exit (including blocked and failed).
3. `factory status --run-id ID [--json]`: `--json` prints `derive_state` as JSON (sorted keys, indent 2). Text mode prints:

```
run <id>  pack=<pack>  status=<status>
step  role       status          attempt  commit
1     specifier  needs_approval  1        <7 chars>
cleanup: created=<n> removed=<n> dirty=<list>
```

4. `tests/test_state.py` with golden files in `tests/fixtures/golden/` (completed, awaiting\_approval, blocked, failed, retried-step) built from fixture run directories.

### Verifications — Step 2.7

- **V2.7-a** `python -m pytest tests/test_state.py -q`. Pass: all golden comparisons equal; each output validates against the `state` schema.
- **V2.7-b** Purity. Pass: `derive_state` twice gives equal results; deleting `state.json` and deriving again gives the same content; the run directory's file list and modification times are unchanged by a derivation call.
- **V2.7-c** Tamper. Pass: overwriting `state.json` with garbage is repaired by the next runner step (or by `factory status`, which re-derives and rewrites it).
- **V2.7-d** `factory status --run-id <id> --json` output equals `derive_state(run_dir)` byte for byte; the text mode lists every step.
- **V2.7-e** After a real two-pack run: `python tools/validate_json.py state <run>/state.json` exits 0, and each role's `commit` in `state.json` equals the commit in its handoff file.
- **V2.7-f** Re-run the recovery suite (V2.6-a). Pass: after each resumed run, `state.json` validates and reports `completed`.

**Gate 2.** All of V2.1 to V2.7 pass; the full suite `python -m pytest -q` is green (real-CLI tests excluded unless `FACTORY_REAL=1`; the real runs listed in V2.3-b, V2.4-c, V2.5-h and V2.6-e have been done and logged in `docs/probes.md`); `git tag phase-2`.
