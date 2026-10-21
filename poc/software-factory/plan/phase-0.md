# Phase 0 — Foundation and hand-run probes

**Goal.** A working source repo skeleton, a recorded baseline, and measured answers to the remaining unknowns, all without any factory tool.

**Preconditions.** Python 3.10+, Git, authenticated Copilot CLI, VS Code 1.141 with the Copilot harness.

**Outputs.** Scaffold, `docs/baseline.md`, `docs/probes.md`, `docs/decisions.md`, `src/factory/data/dispatch.json`.

### Step 0.1 — Scaffold the source repo

1. `mkdir software-factory && cd software-factory && git init -b main`
2. Create `pyproject.toml`:

```
[build-system]
requires = ['setuptools>=68']
build-backend = 'setuptools.build_meta'

[project]
name = 'software-factory'
version = '0.1.0'
requires-python = '>=3.10'
dependencies = ['rich', 'jsonschema']

[project.optional-dependencies]
dev = ['pytest']

[project.scripts]
factory = 'factory.cli:main'

[tool.setuptools.packages.find]
where = ['src']

[tool.setuptools.package-data]
factory = ['data/*.json', 'prompts/*.tmpl']

[tool.pytest.ini_options]
testpaths = ['tests']
```

3. Create `src/factory/__init__.py` containing `__version__ = '0.1.0'`.
4. Create `src/factory/cli.py` (replaced in Phase 2.1):

```
import sys

def main(argv=None):
    print('factory 0.1.0 (skeleton)')
    return 0

if __name__ == '__main__':
    sys.exit(main())
```

5. Create `.gitignore` with these lines: `__pycache__/`, `.pytest_cache/`, `*.egg-info/`, `.venv/`.
6. Create `README.md` with the project name, a one-paragraph purpose, and the commands `pip install -e '.[dev]'` and `python scripts/install.py`.
7. Create `scripts/install.py`. It copies, never symlinks, and refuses a profile whose `name` differs from its filename:

```
#!/usr/bin/env python3
# Copy factory agents and the skill into the user-level Copilot directory.
import os, shutil
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
HOME = Path(os.environ.get('COPILOT_HOME', Path.home() / '.copilot'))

def frontmatter_name(path):
    lines = path.read_text(encoding='utf-8').splitlines()
    if not lines or lines[0].strip() != '---':
        raise SystemExit(f'{path.name}: missing frontmatter')
    for line in lines[1:]:
        if line.strip() == '---':
            break
        if line.startswith('name:'):
            return line.split(':', 1)[1].strip()
    raise SystemExit(f'{path.name}: no name field')

def main():
    agents_dst = HOME / 'agents'
    skill_dst = HOME / 'skills' / 'software-factory'
    for d in (agents_dst, skill_dst):
        if d.is_symlink():
            raise SystemExit(f'{d} is a symlink; refusing')
        d.mkdir(parents=True, exist_ok=True)
    for src in sorted((ROOT / 'agents').glob('*.agent.md')):
        stem = src.name[:-len('.agent.md')]
        if frontmatter_name(src) != stem:
            raise SystemExit(f'{src.name}: name != filename')
        shutil.copyfile(src, agents_dst / src.name)
        print('installed', agents_dst / src.name)
    for src in sorted((ROOT / 'skill' / 'software-factory').glob('*.md')):
        shutil.copyfile(src, skill_dst / src.name)
        print('installed', skill_dst / src.name)

if __name__ == '__main__':
    main()
```

8. `pip install -e '.[dev]'`, then commit: `git add -A && git commit -m 'scaffold'`.

### Step 0.2 — Record the baseline

Create `docs/baseline.md`, fill every field from the commands shown, and commit it:

```
# Baseline
Recorded: <date>   By: <name>

## Tool versions
- Copilot CLI: <output of `copilot --version`>
- Git: <output of `git --version`>
- VS Code: <first line of `code --version`>   (must start with 1.141)
- Python: <output of `python --version`>
- OS: <name and version>

## Verified facts (owner-verified; not re-probed)
- E1: user-level agents in ~/.copilot/agents load; `copilot --agent` works.
- E2: the same agents appear in the VS Code 1.141 agent list (Copilot harness).
- E3: the latest Git is in use and supports linked worktrees.
- E4: skills in ~/.copilot/skills are discovered.
- E5: headless dispatch without allow flags asks for access in every new session.
```

### Step 0.3 — Build the probe kit and run the probes

The probe kit uses only plain scripts and two throw-away agent profiles. No factory code is involved.

1. Create `tools/make_fixture_repo.py`. It writes the fixture project, commits it, and creates a bare `origin.git` next to it:

```
#!/usr/bin/env python3
# Usage: make_fixture_repo.py <dest>   creates <dest>/fx and <dest>/origin.git
import subprocess, sys
from pathlib import Path

FILES = {
    'README.md': '''# fixture-app
Tiny project used by software-factory probes and tests.
''',
    '.gitignore': '''__pycache__/
.pytest_cache/
''',
    'src/__init__.py': '',
    'src/parse.py': '''# Return int(value); None for None or an empty string.
def parse_int(value):
    return int(value.strip())
''',
    'tests/test_parse.py': '''from src.parse import parse_int

def test_basic():
    assert parse_int(' 42 ') == 42

def test_none():
    assert parse_int(None) is None

def test_empty():
    assert parse_int('') is None
''',
    'requests/fix-null.md': '''# Fix null handling in parse_int
parse_int must return None for None and for an empty string.
Tests in tests/test_parse.py describe the expected behaviour.
''',
    'requests/add-greeting.md': '''# Add greeting helper
Add src/greeting.py with greet(name) returning 'Hello, <name>!',
or 'Hello, world!' when name is None or empty.
Add tests in tests/test_greeting.py. Do not change parse_int.
''',
}

def git(cwd, *args):
    cmd = ['git', '-c', 'user.name=fixture', '-c', 'user.email=fixture@localhost', *args]
    subprocess.run(cmd, cwd=cwd, check=True)

def main():
    dest = Path(sys.argv[1]).resolve()
    repo, origin = dest / 'fx', dest / 'origin.git'
    repo.mkdir(parents=True)
    for rel, text in FILES.items():
        p = repo / rel
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_text(text, encoding='utf-8')
    subprocess.run(['git', 'init', '--bare', str(origin)], check=True)
    git(repo, 'init', '-b', 'main')
    git(repo, 'add', '-A')
    git(repo, 'commit', '-m', 'fixture: initial')
    git(repo, 'remote', 'add', 'origin', str(origin))
    git(repo, 'push', 'origin', 'main')
    print(repo)

if __name__ == '__main__':
    main()
```

2. Create `tools/slow_cmd.py` (heartbeat every 30 seconds for N minutes):

```
import sys, time

minutes = float(sys.argv[1])
end = time.time() + minutes * 60
i = 0
while time.time() < end:
    i += 1
    print(f'heartbeat {i}', flush=True)
    time.sleep(30)
print('DONE', flush=True)
```

3. Create `tools/make_prompt.py` (prompt of a given size with a token at the end):

```
import sys

n = int(sys.argv[1])
print('Filler follows, ignore it. ' + 'x ' * (n // 2))
print('Answer with the single word at the very end of this message, then stop. END-7Q2')
```

4. Create `tools/extract_probe.py`. This is the extractor the driver will adopt in Phase 2.3:

````
import json, sys

START = '<<<FACTORY_REPORT>>>'
END = '<<<END_FACTORY_REPORT>>>'

def extract(text):
    # Returns ('ok', dict) | ('no_report', msg) | ('invalid_report', msg)
    i = text.rfind(START)
    if i < 0:
        return 'no_report', 'start marker not found'
    j = text.find(END, i + len(START))
    if j < 0:
        return 'no_report', 'end marker not found'
    body = text[i + len(START):j].strip()
    if body.startswith('```'):
        body = body.strip('`').strip()
        if body.startswith('json'):
            body = body[4:].strip()
    try:
        obj = json.loads(body)
    except ValueError as exc:
        return 'invalid_report', str(exc)
    if not isinstance(obj, dict):
        return 'invalid_report', 'report is not a JSON object'
    return 'ok', obj

if __name__ == '__main__':
    state, val = extract(open(sys.argv[1], encoding='utf-8').read())
    print(json.dumps({'state': state, 'value': val}))
    sys.exit(0 if state == 'ok' else 1)
````

5. Create the two throw-away agents under `tools/probe_agents/` and copy them to `~/.copilot/agents/` by hand (they are removed at the end of Phase 0):

`probe-reporter.agent.md`

```
---
name: probe-reporter
description: Temporary Phase 0 probe agent. Not part of the factory.
tools: ['read', 'search', 'execute']
---
Do what the prompt asks. Then end your final message with exactly this block and nothing after it:
<<<FACTORY_REPORT>>>
{"status": "completed", "notes": "probe", "artifacts": []}
<<<END_FACTORY_REPORT>>>
```

`probe-launcher.agent.md`

```
---
name: probe-launcher
description: Temporary Phase 0 probe agent for the long-run test.
tools: ['execute', 'read']
---
When asked to run a probe, run exactly the command given in the prompt in the terminal, wait for it to finish, and reply with the last line of its output.
```

6. Create the fixture: `python tools/make_fixture_repo.py /tmp/p0` (Windows: use any scratch directory). Confirm the intended failures: `cd /tmp/p0/fx && python -m pytest -q` must report 2 failed, 1 passed.

#### Probe P1 — Do deny flags still work with allow flags?

```
git -C /tmp/p0/fx worktree add ../wt-p1 -b probe/p1
cd /tmp/p0/wt-p1
# control: no deny flag
copilot --agent=probe-reporter -p 'Run: git push origin HEAD:refs/heads/probe-pushed' --allow-all-tools --allow-all-paths -s
git -C /tmp/p0/origin.git branch --list probe-pushed
git -C /tmp/p0/origin.git branch -D probe-pushed
# test: deny flag
copilot --agent=probe-reporter -p 'Run: git push origin HEAD:refs/heads/probe-pushed' --allow-all-tools --allow-all-paths --deny-tool='shell(git push)' -s
git -C /tmp/p0/origin.git branch --list probe-pushed
```

Repeat the test run with `--allow-all` in place of the two allow flags, then repeat both runs using `shell(git branch)` as the denied pattern and the command `git branch probe-should-not-exist` (check with `git branch --list`).

- **Pass (probe is valid):** in the control run the branch exists on origin.
- **Pass (deny effective):** in the test run the branch does not exist. Record D2 = effective or not effective, per allow-flag set.
- **Consequence:** if not effective, `deny_flags` stays empty in `dispatch.json` and the audit is the only enforcement layer.

#### Probe P2 — What is the minimal allow set for headless dispatch?

For each flag set, in a fresh worktree path outside the repo (a new directory each time), with stdin closed and a 180-second limit:

- Set A: `--allow-all-tools`
- Set B: `--allow-all-tools --allow-all-paths`
- Set C: `--allow-all-tools --allow-all-paths --allow-all-urls`
- Set D: `--allow-all`

```
git -C /tmp/p0/fx worktree add ../wt-p2-A -b probe/p2-A
cd /tmp/p0/wt-p2-A
timeout 180 copilot --agent=probe-reporter -p 'Create p2.txt containing ok, run git status --porcelain, and report the output.' --allow-all-tools -s < /dev/null
echo exit=$?; ls p2.txt
```

- **Pass for a set:** exit code 0, `p2.txt` exists, and the process does not wait for input. This also proves that user-level agents load when the working directory is a linked worktree outside the repo.
- **Record D1** = the smallest passing set (lowest letter that passes). If none passes, stop: the plan needs an owner decision.

#### Probe P3 — Is the report block reliably extractable?

In one worktree, run ten times, saving each output to `out-N.txt`:

```
copilot --agent=probe-reporter -p 'List the files in the current directory.' <D1 flags> -s > out-1.txt
python /path/to/software-factory/tools/extract_probe.py out-1.txt
```

- **Pass:** at least 9 of 10 runs print `"state": "ok"`, and no run returns a different block than the last one printed.
- **If fewer than 9:** adjust the probe agent wording up to twice and re-run. If still below 9, stop: report delivery needs an owner decision (for example a file-based report).
- **Record D3** = pass count and the final wording.

#### Probe P3b — Exit codes and failure signatures

Record the exit code and the last stderr line for each case:

- (a) a successful run;
- (b) `--agent=does-not-exist`;
- (c) a run killed by `timeout 20` while the agent sleeps (`-p 'Run: sleep 60'`);
- (d) an invalid flag, `--no-such-flag`.
- **Pass:** each case has a recorded exit code and signature. **Record D5** as the driver map: nonzero exit means `crashed`; exit 0 without a report block means `no_report`; driver kill after the limit means `timeout`. If case (b) exits 0, note it: the missing report still yields `no_report`, so the map holds.

#### Probe P4 — Can a linked worktree change other refs?

Pure Git, no agent:

```
F=/tmp/p0/fx
git -C $F branch probe/victim
git -C $F worktree add ../p4-wt -b probe/p4
git -C ../p4-wt branch -D probe/victim
git -C ../p4-wt update-ref refs/heads/probe/other HEAD
git -C $F branch --list 'probe/*'
```

- **Pass:** the output is recorded. Expected: `probe/victim` is gone and `probe/other` exists, which confirms that the audit must snapshot all refs. If Git blocks either command, record it; the audit stays as designed.

#### Probe P5 — Does a long command survive the VS Code terminal tool?

1. In VS Code 1.141, select the Copilot harness and the `probe-launcher` agent.
2. Prompt: `Run: python /abs/path/tools/slow_cmd.py 5`. Then repeat with `20`, then `45` (45 minutes exceeds the longest expected six-pack run).

- **Pass for N:** the final reply contains `DONE`, the chat did not time out, and you approved at most one terminal prompt.
- **Record D4:** the largest passing N. If 45 fails, D4 = `detach`: Phase 2.1 adds `factory run --detach` (returns a run-id within 5 seconds) and the orchestrator polls `factory status`.

#### Probe P6 — How large a prompt can `-p` carry?

```
for n in 5000 12000 24000 60000; do
  copilot --agent=probe-reporter -p "$(python tools/make_prompt.py $n)" <D1 flags> -s | grep -c 7Q2
done
```

- **Pass for a size:** the count is at least 1 (the agent saw the end of the prompt).
- **Record D6:** `prompt_inline_max` = 75% of the largest passing size, capped at 24000 characters (the Windows command-line limit is about 32,000 characters). Longer prompts use file indirection (Phase 2.3).

### Step 0.4 — Record results and decisions

1. Write every probe's commands, raw outputs and verdict into `docs/probes.md`.
2. Create `docs/decisions.md` listing D1–D6 with the recorded values.
3. Create `src/factory/data/dispatch.json` from the decisions (the values below are the expected shape, not final):

```
{
  "copilot_bin": "copilot",
  "allow_flags": ["--allow-all-tools", "--allow-all-paths"],
  "deny_flags": ["--deny-tool=shell(git push)", "--deny-tool=shell(git worktree)"],
  "prompt_inline_max": 12000,
  "role_timeout_s": 1800,
  "report_start": "<<<FACTORY_REPORT>>>",
  "report_end": "<<<END_FACTORY_REPORT>>>"
}
```

`allow_flags` comes from D1, `deny_flags` from D2 (empty list if not effective), `prompt_inline_max` from D6. `role_timeout_s` is a placeholder calibrated in Phase 2.4.

4. Remove the probe agents from `~/.copilot/agents/`, run `git -C /tmp/p0/fx worktree prune`, commit.

### Verifications — Phase 0

- **V0.1-a** `pip install -e '.[dev]' && factory`. Pass: prints `factory 0.1.0 (skeleton)` and exits 0.
- **V0.1-b** Create a temporary `agents/bad.agent.md` whose `name:` is `other`, then run `python scripts/install.py`. Pass: exits non-zero with `name != filename`. Delete the temporary file.
- **V0.1-c** `python scripts/install.py` with no profiles. Pass: exits 0 and creates `~/.copilot/skills/software-factory/`.
- **V0.2-a** `docs/baseline.md` exists, no `<...>` placeholder remains, and the VS Code line starts with `1.141`.
- **V0.3-a** Fixture: `python -m pytest -q` in `/tmp/p0/fx`. Pass: exactly 2 failed, 1 passed.
- **V0.3-b** `docs/probes.md` contains a verdict for P1, P2, P3, P3b, P4, P5 and P6 with raw output.
- **V0.3-c** `docs/decisions.md` contains D1–D6, each with a concrete value.
- **V0.3-d** `dispatch.json` parses (`python -m json.tool`) and its flags equal D1/D2/D6.
- **V0.4-a** `ls ~/.copilot/agents | grep probe-` prints nothing; `git status` is clean.

**Gate 0.** V0.1-a through V0.4-a pass. If P2, P3 or P5 ended in an owner-decision stop, Phase 1 does not start until `docs/decisions.md` records the decision and the plan is amended.
