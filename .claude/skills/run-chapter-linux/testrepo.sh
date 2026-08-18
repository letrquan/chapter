#!/usr/bin/env bash
#
# Builds a scratch repository at /tmp/chapter-testrepo holding every state Chapter draws:
# staged and unstaged edits, an untracked file, a deletion, a rename, a file with two
# separate hunks, extra branches, a stash, a tag and a linked worktree.
#
# Drive mutations — staging, committing, discarding, branch and tag operations — against
# this rather than a real repository. Re-run it to reset; it is destructive to its own
# directories only.
set -euo pipefail

ROOT=/tmp/chapter-testrepo
LINKED=/tmp/chapter-testrepo-spinner

rm -rf "$ROOT" "$ROOT.git" "$LINKED"
mkdir -p "$ROOT"

git init --bare -q "$ROOT.git"
cd "$ROOT"
git init -q -b main
git config user.email "test@example.com"
git config user.name "Chapter Test"
git remote add origin "$ROOT.git"

mkdir -p src docs

cat > README.md <<'EOF'
# Widget

A scratch project for exercising Chapter.

## Usage

Run `widget --help`. See [the notes](docs/notes.md).
EOF

cat > src/Widget.cs <<'EOF'
namespace Widget;

/// <summary>Does the widget thing.</summary>
public sealed class Widget
{
    public string Name { get; init; } = "widget";

    public int Count(IEnumerable<string> items)
    {
        var total = 0;
        foreach (var item in items) total += item.Length;
        return total;
    }

    public override string ToString() => $"Widget({Name})";
}
EOF

cat > src/helper.ts <<'EOF'
export interface Options {
  verbose: boolean
  retries: number
}

export function describe(options: Options): string {
  return `verbose=${options.verbose} retries=${options.retries}`
}

export const DEFAULTS: Options = { verbose: false, retries: 3 }
EOF

cat > src/theme.css <<'EOF'
:root {
  --brand: #5b8cff;
  --bg: #0d0f15;
}

.button {
  color: var(--brand);
  background: var(--bg);
}
EOF

cat > docs/notes.md <<'EOF'
# Notes

Some **bold** text and a list:

- first
- second
EOF

cat > config.json <<'EOF'
{ "name": "widget", "version": "1.0.0" }
EOF

echo "This file gets deleted in the working tree." > doomed.txt
echo "This file gets renamed." > oldname.txt

# Long enough that two edits stay separate hunks rather than merging under the three
# lines of context git shows either side. Without this there is no way to test hunk
# navigation or single-hunk staging.
python3 - <<'PY'
lines = ['namespace Widget;', '', 'public static class Long', '{']
for n in range(1, 60):
    lines.append(f'    public const int Value{n} = {n};')
lines.append('}')
open('src/Long.cs', 'w').write('\n'.join(lines) + '\n')
PY

git add -A
git commit -qm "feat: initial widget"
git push -q origin main
git branch -u origin/main main >/dev/null 2>&1 || true

# A second commit, so the Committed and Last scopes have something to show.
cat >> src/Widget.cs <<'EOF'

public static class WidgetFactory
{
    public static Widget Create(string name) => new() { Name = name };
}
EOF
git commit -qam "feat: add a factory"

git branch feature/spinner
git branch feature/abandoned HEAD~1
git tag v1.0.0 HEAD~1

echo "stashed work in progress" >> README.md
git stash push -qm "wip: half-finished readme"

# --- working tree states -------------------------------------------------
printf '\n## Installation\n\nrun the installer.\n' >> README.md
git add README.md                                    # staged edit

sed -i 's/retries: 3/retries: 5/' src/helper.ts       # unstaged edit
sed -i '3s|.*|    // edited near the top|' src/Long.cs
sed -i '56s|.*|    // edited near the bottom|' src/Long.cs

echo 'export const brandNew = true' > src/untracked.ts  # untracked
rm doomed.txt                                           # deletion
git mv oldname.txt newname.txt                          # rename

git worktree add -q "$LINKED" feature/spinner

echo "test repo ready at $ROOT (linked worktree at $LINKED)"
git status --short
echo "--- branches ---"; git branch -vv
echo "--- tags ---";     git tag
echo "--- stashes ---";  git stash list
