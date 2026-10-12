import sys, os, collections
import zxingcpp
from PIL import Image
expected = {}
for line in open(os.environ.get('E2E_DIR', '/tmp/kniterp-e2e') + '/bc-items.txt', encoding='utf-8'):
    i, c = line.strip().split('|'); expected[i] = c
res = collections.defaultdict(lambda: [0, 0, 0])  # ok, wrong, notread
fails = []
d = os.environ.get('E2E_DIR', '/tmp/kniterp-e2e') + '/bcshots/'
for f in sorted(os.listdir(d)):
    dpi, rest = f.split('_', 1)
    tpl, item = rest.rsplit('_', 1); item = item[:-4]
    img = Image.open(d + f).convert('L')
    for mode in ('gray', '1bit'):
        im = img if mode == 'gray' else img.point(lambda v: 255 if v > 128 else 0)
        found = [r.text for r in zxingcpp.read_barcodes(im)]
        exp = expected[item]
        key = (dpi, tpl, mode, 'EAN' if exp.isdigit() and len(exp) == 13 else 'C128')
        if exp in found: res[key][0] += 1
        elif found: res[key][1] += 1; fails.append((f, mode, exp, found))
        else: res[key][2] += 1; fails.append((f, mode, exp, found))
for k in sorted(res): print(k, 'ok=%d wrong=%d unread=%d' % tuple(res[k]))
print('FAILS', len(fails)); [print(x) for x in fails[:15]]
