#!/usr/bin/env bash
# Печатает тексты интерфейса без перевода (be/kk/uz) и устаревшие ключи по тесту TranslationCoverageTests.
# Запуск из любого места: bash .claude/skills/kniterp-translations/missing.sh
set -euo pipefail
root="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)/kniterp"
out="$(dotnet test "$root/tests/KnitErp.UnitTests" --filter "FullyQualifiedName~TranslationCoverageTests" 2>&1 || true)"
python3 - "$out" <<'PY'
import sys, re
text = sys.argv[1]
missing, stale, mode = set(), set(), None
for line in text.splitlines():
    s = line.strip()
    if re.search(r'\.json: нет перевода для', s): mode = 'm'; continue
    if re.search(r'\.json: ключи не используются', s): mode = 's'; continue
    if not s or s.startswith(('Stack Trace', 'at ', 'Failed ', 'Passed', 'Error Message', 'Assert.', 'Expected', 'Actual', 'Total tests', '[xUnit')):
        mode = None if s.startswith(('Stack Trace', 'Failed ', 'Passed', 'Total tests')) else mode
        continue
    if mode == 'm': missing.add(s)
    elif mode == 's': stale.add(s)
print("MISSING:", len(missing))
for k in sorted(missing): print(k)
print("STALE:", len(stale))
for k in sorted(stale): print(k)
PY
