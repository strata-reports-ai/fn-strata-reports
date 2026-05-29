#!/usr/bin/env bash
set -euo pipefail

PR_NUMBER="${PR_NUMBER:?}"
HEAD_BRANCH="${HEAD_BRANCH:?}"
GH_DISPATCH_TOKEN="${GH_DISPATCH_TOKEN:?}"
PR_REPO="${GITHUB_REPOSITORY:-strata-reports-ai/fn-strata-reports}"

# Guard 1: claim the code-review slot on the orchestrator issue
ISSUE_N=$(gh pr view "${PR_NUMBER}" --repo "${PR_REPO}" --json body --jq '.body' \
  | grep -oP 'orchestrator-strata-reports#\K[0-9]+' | head -1 || true)
if [ -n "$ISSUE_N" ]; then
  CURRENT_LABELS=$(GH_TOKEN="$GH_DISPATCH_TOKEN" gh issue view "$ISSUE_N" \
    --repo strata-reports-ai/orchestrator-strata-reports \
    --json labels --jq '[.labels[].name]' 2>/dev/null || echo "[]")
  if echo "$CURRENT_LABELS" | grep -q '"code-review"'; then
    active_review=$(gh run list \
      --repo "${PR_REPO}" \
      --workflow=code-review.yml \
      --status in_progress \
      --json status --jq 'length' 2>/dev/null || echo "0")
    if [ "${active_review:-0}" -gt 1 ]; then
      echo "Issue #$ISSUE_N already in code-review with another active run — skipping duplicate dispatch"
      exit 0
    fi
    echo "Issue #$ISSUE_N in code-review but no active run — re-triggering review"
  fi
  GH_TOKEN="$GH_DISPATCH_TOKEN" gh issue edit "$ISSUE_N" \
    --repo strata-reports-ai/orchestrator-strata-reports \
    --remove-label in-progress --add-label code-review
fi

# Guard 2: skip if review cycle is terminal (passed or max retries reached)
# Does NOT block on "## Review findings" — that just means fixes are in progress and a re-review is expected.
TERMINAL_COMMENT=$(gh api "repos/${PR_REPO}/issues/${PR_NUMBER}/comments" \
  --jq '[.[] | select(.body | (startswith("✅ **Code review") or startswith("⚠️ Max revision")))] | length' \
  2>/dev/null || echo "0")
if [ "${TERMINAL_COMMENT:-0}" -gt "0" ]; then
  echo "PR #${PR_NUMBER} review cycle is complete (passed or max retries) — skipping"
  exit 0
fi

cat > /tmp/review-prompt.txt << ENDPROMPT
You are a code reviewer. Execute these steps in order. You MUST run the commands in step 4 or 5 before finishing.

STEP 1 — Get the PR diff:
  gh pr diff ${PR_NUMBER} --repo ${PR_REPO}

STEP 2 — Read only the files that appear in the diff output.

STEP 3 — Check ONLY the code introduced by this PR for:
  MAJOR: security vulnerabilities, auth bypass, data loss, breaking API changes, RLS bypass
  MODERATE: significant bugs, missing error handling, wrong HTTP status codes
  (Ignore minor style issues — they do not block merge)

STEP 4 — If NO major or moderate issues found, run ALL of these commands in order:

  gh pr comment ${PR_NUMBER} --repo ${PR_REPO} --body "✅ **Code review passed.** No major or moderate issues found. Merging."

  GH_TOKEN="\$GH_REVIEW_TOKEN" gh pr review ${PR_NUMBER} --repo ${PR_REPO} --approve --body "Code review passed." 2>/dev/null || true

  GH_TOKEN="\$GH_DISPATCH_TOKEN" gh pr merge ${PR_NUMBER} --repo ${PR_REPO} --squash --delete-branch

  ISSUE_N=\$(gh pr view ${PR_NUMBER} --repo ${PR_REPO} --json body --jq '.body' | grep -oP 'orchestrator-strata-reports#\K[0-9]+' | head -1)
  if [ -n "\$ISSUE_N" ]; then
    GH_TOKEN="\$GH_DISPATCH_TOKEN" gh issue edit "\$ISSUE_N" --repo strata-reports-ai/orchestrator-strata-reports --remove-label code-review --add-label in-test
  fi

STEP 5 — If major or moderate issues exist, run ALL of these commands in order:

  RETRIES=\$(gh pr view ${PR_NUMBER} --repo ${PR_REPO} --json labels --jq '[.labels[].name | select(startswith("review-retry-"))] | length')
  if [ "\$RETRIES" -ge 3 ]; then
    gh pr edit ${PR_NUMBER} --repo ${PR_REPO} --add-label "needs-human-review"
    gh pr comment ${PR_NUMBER} --repo ${PR_REPO} --body "⚠️ Max revision cycles reached. Human review required."
  else
    NEXT=\$((RETRIES + 1))
    gh pr edit ${PR_NUMBER} --repo ${PR_REPO} --add-label "review-retry-\$NEXT"
    ISSUE_N=\$(gh pr view ${PR_NUMBER} --repo ${PR_REPO} --json body --jq '.body' | grep -oP 'orchestrator-strata-reports#\K[0-9]+' | head -1)
    if [ -n "\$ISSUE_N" ]; then
      GH_TOKEN="\$GH_DISPATCH_TOKEN" gh issue edit "\$ISSUE_N" --repo strata-reports-ai/orchestrator-strata-reports --remove-label code-review --add-label in-progress
    fi
    gh pr comment ${PR_NUMBER} --repo ${PR_REPO} --body "## Review findings (attempt \$NEXT/3)

[replace with specific findings: file path, line number, issue, fix required]"
    ORIGINAL=\$(gh pr view ${PR_NUMBER} --repo ${PR_REPO} --json body --jq '.body')
    jq -n --arg p "REVISION REQUEST for ${HEAD_BRANCH} (attempt \$NEXT/3).

Original task: \$ORIGINAL

Review feedback: [replace with specific findings]

Push fixes to the EXISTING branch ${HEAD_BRANCH}. Do NOT create a new branch.

After pushing all fixes, run these commands to trigger a re-review (in this exact order):
  ISSUE_N_R=\$(gh pr view ${PR_NUMBER} --repo ${PR_REPO} --json body --jq '.body' | grep -oP 'orchestrator-strata-reports#\K[0-9]+' | head -1 || true)
  if [ -n \"\$ISSUE_N_R\" ]; then
    GH_TOKEN=\"\$GH_DISPATCH_TOKEN\" gh issue edit \"\$ISSUE_N_R\" --repo strata-reports-ai/orchestrator-strata-reports --remove-label code-review --add-label in-progress 2>/dev/null || true
  fi
  GH_TOKEN=\"\$GH_DISPATCH_TOKEN\" gh workflow run code-review.yml --repo ${PR_REPO} --field pr_number=${PR_NUMBER} --field head_branch=${HEAD_BRANCH}" \
      --arg b "${HEAD_BRANCH}" \
      '{"ref":"main","inputs":{"prompt":\$p,"branch":\$b}}' | \
    GH_TOKEN="\$GH_DISPATCH_TOKEN" gh api \
      repos/${PR_REPO}/actions/workflows/claude-code.yml/dispatches \
      --method POST --input -
  fi
ENDPROMPT

echo "Dispatching review for PR #${PR_NUMBER} (${HEAD_BRANCH}) in ${PR_REPO}"

jq -n --arg prompt "$(cat /tmp/review-prompt.txt)" --arg branch "main" \
  '{"ref":"main","inputs":{"prompt":$prompt,"branch":$branch}}' | \
GH_TOKEN="$GH_DISPATCH_TOKEN" gh api \
  repos/strata-reports-ai/fn-strata-reports/actions/workflows/claude-code.yml/dispatches \
  --method POST --input -

echo "Review dispatched."

