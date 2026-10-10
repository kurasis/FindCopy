# Vendored web-design-guidelines

This directory installs Vercel's skill in the repository as ordinary files.
`SKILL.md` is copied byte for byte from upstream. No global installation,
symlinks, package installation, or helper processes are required.

## Source

- Repository: https://github.com/vercel-labs/agent-skills
- Original directory: `skills/web-design-guidelines`
- Source commit: `063bee94c3f4df8453406c830b0a7df0f2860278`
- Pinned source: https://github.com/vercel-labs/agent-skills/tree/063bee94c3f4df8453406c830b0a7df0f2860278/skills/web-design-guidelines
- Imported on: 2026-10-10
- Upstream skill metadata: author `vercel`, version `1.0.0`.
- Upstream README declares the repository's license as MIT. This source commit
  does not include a separate license file in the skill directory.

The complete upstream directory at this commit contains only `SKILL.md`;
there are no helper scripts or reference files to copy. This README is local
provenance documentation, rather than part of the upstream skill.

| File | SHA-256 |
| --- | --- |
| `SKILL.md` | `f4647ca866a3accf763777f83e7682954f0187cd6bea7eea0399796652414e8f` |

## Live guidelines and network access

The skill fetches current rules before every review from:

https://raw.githubusercontent.com/vercel-labs/web-interface-guidelines/main/command.md

An HTTPS GET on 2026-10-10 returned HTTP 200 and a nonempty Markdown document
(8,055 bytes). This verifies availability at import time; it does not pin the
live rules or guarantee future access. No rules snapshot is bundled in this
directory. Fetch failures must be reported without claiming a completed review
against current rules.

Allow `raw.githubusercontent.com` for reviews. Updating the vendored skill
also requires `github.com`. The original skill mentions WebFetch; agents may
use their available HTTPS fetch tool to retrieve the same URL.

The application currently uses WPF. Keep HTML/browser rules scoped to web
content and identify which general UX/accessibility guidance applies to WPF.

## Reproduce or update the import

Run from the repository root. To reproduce this import, keep the exact commit
below. To update, first resolve and review a new full upstream commit SHA, then
replace `skill_revision` with it; never record only a moving branch name.

```sh
skill_source=$(mktemp -d)
skill_revision=063bee94c3f4df8453406c830b0a7df0f2860278
git clone --no-checkout https://github.com/vercel-labs/agent-skills.git "$skill_source"
git -C "$skill_source" ls-tree -r "$skill_revision" -- skills/web-design-guidelines
git -C "$skill_source" checkout "$skill_revision" -- skills/web-design-guidelines
```

Inspect the complete directory before copying. Accept only ordinary files
and directories, inspect any new scripts without executing them, and include
all upstream helper/reference files. Do not copy upstream root agent
instructions into this repository's `AGENTS.md`.

For the recorded commit, which has only one regular file:

```sh
mkdir -p .agents/skills/web-design-guidelines
cp "$skill_source/skills/web-design-guidelines/SKILL.md" .agents/skills/web-design-guidelines/SKILL.md
cmp "$skill_source/skills/web-design-guidelines/SKILL.md" .agents/skills/web-design-guidelines/SKILL.md
sha256sum .agents/skills/web-design-guidelines/SKILL.md
git diff --check
```

For a newer source, copy the full reviewed upstream directory, preserve this
local README, and remove only files confirmed to have been removed upstream.
Update the source commit/link, date, file inventory, checksums, and metadata
above. Validate YAML front matter (`name`, `description`, and `metadata`),
verify the live guidelines URL again, review the diff, and submit a separate
commit and pull request. Never replace the original skill instructions with
a locally rewritten version.
