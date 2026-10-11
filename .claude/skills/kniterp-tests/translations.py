#!/usr/bin/env python3
"""Словари интерфейса knitERP (be, kk, uz) без перестановки существующих ключей — меньше конфликтов слияния.

  translations.py add new.json      — {"Русский ключ": ["be", "kk", "uz"], ...}: добавить отсутствующие ключи в конец файлов
  translations.py remove "ключ" ... — удалить ключи, которые тест TranslationCoverageTests назвал лишними
Запуск из корня репозитория. Заполненность и лишние ключи проверяет dotnet test tests/KnitErp.UnitTests --filter TranslationCoverage.
"""
import collections
import json
import sys

BASE = "kniterp/src/KnitErp.Web/Localization/"
LANGS = ["be", "kk", "uz"]


def load(lang):
    with open(BASE + lang + ".json", encoding="utf-8") as f:
        return json.load(f, object_pairs_hook=collections.OrderedDict)


def save(lang, data):
    with open(BASE + lang + ".json", "w", encoding="utf-8") as f:
        f.write(json.dumps(data, ensure_ascii=False, indent=2) + "\n")


if len(sys.argv) < 3 or sys.argv[1] not in ("add", "remove"):
    sys.exit(__doc__)

if sys.argv[1] == "add":
    with open(sys.argv[2], encoding="utf-8") as f:
        new = json.load(f, object_pairs_hook=collections.OrderedDict)
    for i, lang in enumerate(LANGS):
        data = load(lang)
        for key, values in new.items():
            data.setdefault(key, values[i])
        save(lang, data)
    print(f"добавлено ключей: {len(new)}")
else:
    for lang in LANGS:
        data = load(lang)
        for key in sys.argv[2:]:
            data.pop(key, None)
        save(lang, data)
    print(f"удалено ключей: {len(sys.argv) - 2}")
