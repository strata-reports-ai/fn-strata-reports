#!/usr/bin/env python3
"""Reads test-prompt-template.txt, substitutes placeholders, writes to /tmp/test-prompt.txt."""
import os

pr_numbers = os.environ.get("PR_NUMBERS", "")
repo = os.environ.get("REPO", "")
issue_numbers = os.environ.get("ISSUE_NUMBERS", "")

# Use the first PR and issue for single-item substitution; batching handled in template
pr = pr_numbers.split(",")[0].strip() if pr_numbers else ""
issue = issue_numbers.split(",")[0].strip() if issue_numbers else ""

if issue:
    issue_ref = f"strata-reports-ai/orchestrator-strata-reports#{issue}"
    issue_step = f'  GH_TOKEN="$GH_DISPATCH_TOKEN" gh issue view {issue} --repo strata-reports-ai/orchestrator-strata-reports'
    issue_label_step = (
        f'  GH_TOKEN="$GH_DISPATCH_TOKEN" gh issue edit {issue} \\\n'
        f'    --repo strata-reports-ai/orchestrator-strata-reports \\\n'
        f'    --remove-label in-test \\\n'
        f'    --add-label done'
    )
else:
    issue_ref = "(no linked orchestrator issue)"
    issue_step = "  # No orchestrator issue linked — test based on PR diff only"
    issue_label_step = "  # No orchestrator issue linked — skipping label update"

script_dir = os.path.dirname(os.path.abspath(__file__))
template_path = os.path.join(script_dir, "test-prompt-template.txt")

with open(template_path) as f:
    prompt = f.read()

prompt = prompt.replace("{{PR}}", pr)
prompt = prompt.replace("{{PR_NUMBERS}}", pr_numbers)
prompt = prompt.replace("{{REPO}}", repo)
prompt = prompt.replace("{{ISSUE_REF}}", issue_ref)
prompt = prompt.replace("{{ISSUE_STEP}}", issue_step)
prompt = prompt.replace("{{ISSUE_LABEL_STEP}}", issue_label_step)

with open("/tmp/test-prompt.txt", "w") as f:
    f.write(prompt)

print(f"Test prompt built ({len(prompt)} chars, PR(s) {pr_numbers}, repo {repo})")
