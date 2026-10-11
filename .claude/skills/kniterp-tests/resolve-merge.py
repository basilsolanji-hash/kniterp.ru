#!/usr/bin/env python3
"""Разрешение типовых конфликтов слияния веток knitERP (модули добавляют строки в одни и те же места).

Запуск из корня репозитория после неудачного git merge: python3 .claude/skills/kniterp-tests/resolve-merge.py
Правила:
- Tags.cs, ObjectDirectory.cs, Abstractions.cs, KnitErpDbContext.cs, DependencyInjection.cs, Labels.cs, Permissions.cs,
  RoleMatrixP0.cs, AuditEntry.cs, MainLayout.razor, TranslationCoverageTests.cs — обе стороны (новые значения, ветки, строки);
- CHECK тегов и обсуждений — «BETWEEN 1 AND 29» (D202: диапазоны модулей, ограничение не переписывается);
- снапшот модели — обе стороны, кроме строк с этими CHECK;
- словари Localization/{be,kk,uz}.json — трёхстороннее слияние ключей: наша версия + ключи, добавленные их стороной,
  − ключи, удалённые их стороной (порядок наших ключей сохраняется, их новые — в конце).
Остальные файлы не трогаются — их разрешают вручную. После скрипта: сборка и
dotnet ef migrations has-pending-model-changes -p src/KnitErp.Infrastructure.
"""
import collections
import json
import re
import subprocess

UNION = ("Tags.cs", "ObjectDirectory.cs", "Abstractions.cs", "KnitErpDbContext.cs", "DependencyInjection.cs", "Labels.cs",
         "Permissions.cs", "RoleMatrixP0.cs", "AuditEntry.cs", "MainLayout.razor", "TranslationCoverageTests.cs")
CHECK = re.compile(r"BETWEEN 1 AND \d+( OR \[\w+\] BETWEEN 20 AND 29)?")
HUNK = re.compile(r"<<<<<<< [^\n]*\n(.*?)=======\n(.*?)>>>>>>> [^\n]*\n", re.S)


def resolve(ours: str, theirs: str) -> str:
    if "ck_tag_links_entity" in ours + theirs or "ck_discussion_comments_target" in ours + theirs:
        return CHECK.sub("BETWEEN 1 AND 29", ours)
    return theirs + ours


def stage(n: int, path: str) -> dict:
    out = subprocess.run(["git", "show", f":{n}:{path}"], capture_output=True, text=True)
    return json.loads(out.stdout, object_pairs_hook=collections.OrderedDict) if out.returncode == 0 else collections.OrderedDict()


def merge_json(path: str) -> None:
    base, ours, theirs = stage(1, path), stage(2, path), stage(3, path)
    merged = collections.OrderedDict((k, v) for k, v in ours.items() if not (k in base and k not in theirs))
    for k, v in theirs.items():
        if k not in base and k not in merged:
            merged[k] = v
    with open(path, "w", encoding="utf-8") as f:
        f.write(json.dumps(merged, ensure_ascii=False, indent=2) + "\n")
    print("разрешено:", path, f"(ключей {len(merged)})")


files = subprocess.run(["git", "diff", "--name-only", "--diff-filter=U"], capture_output=True, text=True, check=True).stdout.split()
for path in files:
    name = path.rsplit("/", 1)[-1]
    if "/Localization/" in path and name.endswith(".json"):
        merge_json(path)
        continue
    if name not in UNION and name not in ("Configurations.cs", "KnitErpDbContextModelSnapshot.cs"):
        print("вручную:", path)
        continue
    text = open(path, encoding="utf-8").read()
    text = HUNK.sub(lambda m: resolve(m.group(1), m.group(2)), text)
    open(path, "w", encoding="utf-8").write(text)
    print("разрешено:", path, "(остались маркеры!)" if "<<<<<<<" in text else "")
