
# Prompt — Refine the Software Factory Project Plan

**Purpose:** Use this prompt in a **new, context-free session** to review and improve the project plan above. It assumes no prior conversation.

---

## Instructions to the assistant

You are reviewing a software factory project plan. The plan is attached or pasted below. Your job is to **refine the plan**, not to praise it.

### Ground rules

1. **No assumptions, no "could work" ideas.** Every recommendation must be grounded in verified documentation or an explicit tradeoff you state. If you are unsure, say "verify X before adopting."
2. **Challenge the frozen decisions only with evidence.** The plan marks decisions as "frozen" (user-level config, worktree isolation, per-role JSON handoffs, Python driver, post-run dashboard v1). If you propose changing one, state the exact evidence that justifies the change and the cost of reversing it.
3. **Do not add features to v1.** Improvements must either (a) reduce risk, (b) remove ambiguity, (c) close a gap between the plan and real Copilot/Git behavior, or (d) make Phase 4 cheaper without expanding Phase 0–3.
4. **Cite the source for every claim about Copilot, Git, or VS Code behavior.** Documentation links, CLI version notes, or explicit "unverified — test in Phase 0" labels. No unlabeled speculation.
5. **Keep the plan's shape.** Output the refined plan in the same section structure (What / Intended Use / Goals / Requirements / Architecture / Guidelines / Plan / Risks / Success / References / Change Log). Do not restructure unless a structural change removes a concrete defect.

### What to review

**A. Correctness against real behavior**

- Does the plan rely on any Copilot CLI or VS Code behavior that is not actually stable? Flag it. The plan claims `copilot --agent=`, user-level agent discovery at `~/.copilot/agents`, and skill discovery at `~/.copilot/skills` are stable. Verify or flag each.
- Is the VS Code user-profile agent directory configuration accurate? If the path or setting name is wrong, correct it with a citation.
- Are `handoffs` and subagent delegation properties usable without experimental flags? If not, the six-pack's `needs_approval` gate needs a different mechanism — propose one grounded in stable features.

**B. Handoff and state model**

- Is the handoff schema complete? Identify missing fields that the driver will need to make a routing decision (e.g. task ordering, dependencies between tasks, retry count, originating request id).
- Is `state.json` derivable unambiguously from handoff files? Point out any field in `state.json` that cannot be reconstructed and specify the fix.
- Does the plan specify what happens when two handoffs for the same role and task exist (rerun, duplicate dispatch)? If not, propose a rule.

**C. Worktree lifecycle**

- Is the worktree creation/teardown sequence safe against partial failure (driver crashes mid-run, worktree creation fails partway through a six-pack)? Propose an idempotent recovery procedure.
- Is `../worktrees/<run-id>/<role>` safe against concurrent runs with the same date-based `run-id`? If not, propose a run-id scheme that is collision-safe.
- Does the "dirty → stop, don't force" rule leave worktrees that block the next run? Propose a cleanup policy that does not lose data.

**D. Driver design**

- Does the driver dispatch loop have a clear contract for each `status` value (`completed`, `blocked`, `needs_approval`) and for an unrecognized `status`? Specify the default behavior.
- What happens if an agent exits without writing a handoff (crash, timeout, tool failure)? The plan does not say. Propose a timeout and a failure classification.
- Is `copilot --agent=<role>` invocation with cwd set to a worktree actually sufficient to confine the agent to that worktree, or does it need an explicit prompt-level boundary in every dispatch? State which mechanism the plan should rely on and why.

**E. Pack selection**

- The orchestrator classifies two-pack vs six-pack from the request text. What is the failure mode if classification is wrong? Propose a cheap correction path (e.g. `--pack` override already exists, but is it surfaced in the VS Code flow?).
- Is there a case where neither pack fits? Specify the behavior.

**F. Risks**

- For each risk in Section 8, state whether the mitigation is verifiable in Phase 0–3 or only in Phase 4. Flag any risk whose mitigation depends on a deferred phase.
- Identify risks the plan does not list. Do not pad the list; only add risks with a concrete trigger and a concrete mitigation.

**G. Guidelines**

- Are any of the nine guidelines unenforceable by the driver or by agent tool restrictions? Flag them and propose either an enforcement mechanism or a rewrite that makes the guideline honest.

### What to output

Produce exactly three sections:

1. **Findings** — a numbered list. Each finding contains: what is wrong or missing, the evidence, the impact, and the proposed refinement (a specific edit to a specific section).
2. **Refined plan** — the full plan rewritten with the accepted refinements applied. Keep the section structure. Mark every change with an inline `[R<n>]` tag referencing the finding number. Do not include a finding you did not apply.
3. **Open verifications** — a short list of items the plan now says "verify in Phase 0" that were previously assumed. These are the only acceptable unverified claims in the refined plan.

Do not include praise, summaries of the original plan, or commentary outside these three sections.

### The plan to refine

```markdown
<paste the full contents of SOFTWARE-FACTORY-PLAN.md here>
```
