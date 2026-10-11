#!/usr/bin/env python3
"""Разрешение типовых конфликтов слияния веток knitERP (модули добавляют строки в одни и те же места).

Запуск из корня репозитория после неудачного git merge: python3 .claude/skills/kniterp-tests/resolve-merge.py
Правила:
- Tags.cs, ObjectDirectory.cs, Abstractions.cs, KnitErpDbContext.cs, DependencyInjection.cs, Labels.cs, Permissions.cs,
  RoleMatrixP0.cs, AuditEntry.cs, MainLayout.razor, TranslationCoverageTests.cs — обе стороны (новые значения, ветки, строки);
- CHECK тегов и обсуждений — «BETWEEN 1 AND 29» (D202: диапазоны модулей, ограничение не переписывается);
- снапшот модели — обе стороны, кроме строк с этими CHECK.
Остальные файлы не трогаются — их разрешают вручную. После скрипта: сборка и
dotnet ef migrations has-pending-model-changes -p src/KnitErp.Infrastructure.
"""
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


files = subprocess.run(["git", "diff", "--name-only", "--diff-filter=U"], capture_output=True, text=True, check=True).stdout.split()
for path in files:
    name = path.rsplit("/", 1)[-1]
    if name not in UNION and name not in ("Configurations.cs", "KnitErpDbContextModelSnapshot.cs"):
        print("вручную:", path)
        continue
    text = open(path, encoding="utf-8").read()
    text = HUNK.sub(lambda m: resolve(m.group(1), m.group(2)), text)
    open(path, "w", encoding="utf-8").write(text)
    print("разрешено:", path, "(остались маркеры!)" if "<<<<<<<" in text else "")
