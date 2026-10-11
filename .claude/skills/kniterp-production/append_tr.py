# Новые ключи — в конец словарей (меньше конфликтов с другими сессиями).
# Пример файла: T = {"Ключ": ("be", "kk", "uz")}
import json, sys, importlib.util
spec = importlib.util.spec_from_file_location("tr", sys.argv[1]); m = importlib.util.module_from_spec(spec); spec.loader.exec_module(m)
for i, code in enumerate(["be", "kk", "uz"]):
    p = f"{sys.argv[2]}/{code}.json"
    d = json.load(open(p, encoding="utf-8"))
    for k, v in m.T.items():
        d.setdefault(k, v[i])
    open(p, "w", encoding="utf-8").write(json.dumps(d, ensure_ascii=False, indent=2) + "\n")
