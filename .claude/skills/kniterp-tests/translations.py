#!/usr/bin/env python3
"""Совместимость: прежняя команда словарей knitERP, которой пользуются сессии (производство). Сама ничего не пишет —
передаёт работу единому скрипту skill kniterp-translations (merge.py: проверка плейсхолдеров, запись в конец файлов).

  translations.py add new.json      — {"Русский ключ": ["be", "kk", "uz"], ...} (формат merge.py {"ключ": {"be": …}} тоже принимается)
  translations.py remove "ключ" ... — удалить ключи, которые тест TranslationCoverageTests назвал лишними
Недостающие и лишние ключи: bash .claude/skills/kniterp-translations/missing.sh
"""
import json
import pathlib
import subprocess
import sys
import tempfile

MERGE = pathlib.Path(__file__).resolve().parent.parent / "kniterp-translations" / "merge.py"

if len(sys.argv) < 3 or sys.argv[1] not in ("add", "remove"):
    sys.exit(__doc__)

if sys.argv[1] == "add":
    with open(sys.argv[2], encoding="utf-8") as f:
        new = json.load(f)
    converted = {k: v if isinstance(v, dict) else dict(zip(("be", "kk", "uz"), v)) for k, v in new.items()}
    with tempfile.NamedTemporaryFile("w", suffix=".json", encoding="utf-8", delete=False) as tmp:
        json.dump(converted, tmp, ensure_ascii=False)
    args = [tmp.name]
else:
    args = [x for key in sys.argv[2:] for x in ("--remove", key)]

sys.exit(subprocess.call([sys.executable, "-I", str(MERGE), *args]))
