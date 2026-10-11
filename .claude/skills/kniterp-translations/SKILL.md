---
name: kniterp-translations
description: Переводы интерфейса knitERP на be/kk/uz. Использовать после любого изменения строк L("…") в KnitErp.Web, сообщений BusinessRuleException в Domain/Application или названий из домена, которые экраны выводят через L(...), — чтобы прошёл TranslationCoverageTests.
---

# Переводы интерфейса knitERP (be, kk, uz)

Тест `tests/KnitErp.UnitTests/TranslationCoverageTests.cs` требует перевод каждого текста интерфейса и не терпит устаревших ключей.
Ключ — русский текст. Строки `$"…{x}…"` в сообщениях правил превращаются в шаблон с `{0}`, `{1}`.

## Порядок
1. Собрать решение (`dotnet build kniterp/src/KnitErp.Web`).
2. `bash .claude/skills/kniterp-translations/missing.sh` — печатает `MISSING` (нет перевода) и `STALE` (ключ больше не используется).
3. Написать JSON во временный файл (scratchpad), формат:
   `{"Русский текст": {"be": "…", "kk": "…", "uz": "…"}}`. Узбекский — латиница (o‘, g‘), казахский и белорусский — кириллица.
   Плейсхолдеры `{0}` сохранять; термины как в существующих файлах (НДС → ПДВ / ҚҚС / QQS, склад → склад / қойма / ombor).
4. `python3 .claude/skills/kniterp-translations/merge.py <файл.json> [--remove "устаревший ключ" …]` — дописывает ключи в конец
   `kniterp/src/KnitErp.Web/Localization/{be,kk,uz}.json` (форматирование как в репозитории) и проверяет плейсхолдеры.
5. Повторить шаг 2 — должно быть `MISSING: 0`, `STALE: 0`.

## Подводные камни
- Тест находит только литералы `L("…")`. `L(cond ? "а" : "б")` и `L(Метод())` он не видит — пишите `cond ? L("а") : L("б")`
  или добавляйте значения домена в список тестa (как `ItemTypes.All`).
- Новый `ItemType` автоматически попадает в ключи через `ItemTypes.Name`.
