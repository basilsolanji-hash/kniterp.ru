#!/usr/bin/env python3
"""Добавляет переводы в src/KnitErp.Web/Localization/{be,kk,uz}.json.

Вход — JSON-файл {"русский текст": {"be": "...", "kk": "...", "uz": "..."}}. Новые ключи дописываются в конец файла
(как принято в репозитории), существующие — заменяются. --remove "ключ" удаляет устаревший ключ (можно несколько раз).
Плейсхолдеры {0}, {1} должны остаться в переводе — скрипт проверяет.
"""
import json, re, subprocess, sys, pathlib, argparse

ap = argparse.ArgumentParser()
ap.add_argument('file', nargs='?')
ap.add_argument('--remove', action='append', default=[])
a = ap.parse_args()
root = pathlib.Path(subprocess.check_output(['git', '-C', str(pathlib.Path(__file__).parent), 'rev-parse', '--show-toplevel'], text=True).strip())
loc = root / 'kniterp' / 'src' / 'KnitErp.Web' / 'Localization'
new = json.load(open(a.file, encoding='utf-8')) if a.file else {}
ph = re.compile(r'\{\d+(?::[^}]*)?\}')
for key, tr in new.items():
    for lang in ('be', 'kk', 'uz'):
        if lang not in tr or not tr[lang].strip():
            sys.exit(f'нет перевода {lang}: {key}')
        if sorted(ph.findall(key)) != sorted(ph.findall(tr[lang])):
            sys.exit(f'плейсхолдеры не совпадают ({lang}): {key}')
for lang in ('be', 'kk', 'uz'):
    p = loc / f'{lang}.json'
    d = json.loads(p.read_text(encoding='utf-8'))
    for k in a.remove:
        d.pop(k, None)
    for key, tr in new.items():
        d[key] = tr[lang]
    p.write_text(json.dumps(d, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
print(f'добавлено/обновлено: {len(new)}, удалено: {len(a.remove)}')
