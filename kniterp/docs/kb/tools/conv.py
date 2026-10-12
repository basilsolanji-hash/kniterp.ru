"""Компактный формат ответов → Knowledge/qa-<key>.json. id сохраняются по тексту вопроса."""
import json, sys, os, re
ROOT=os.path.abspath(os.path.join(os.path.dirname(__file__),'..','..','..'))
KN=os.path.join(ROOT,'src','KnitErp.Application','Workspace','Knowledge')
SECTIONS={
 'start':'Программа · Начало работы и вход','sales':'Программа · Продажи и счета','purchases':'Программа · Закупки',
 'stock':'Программа · Склад и партии','money':'Программа · Деньги','production':'Программа · Производство',
 'hr':'Программа · Сотрудники и кадры','catalog':'Программа · Каталог, цены, МойСклад','crm':'Программа · Контрагенты, договоры, CRM',
 'reports':'Программа · Отчёты, интеграции, 1С','yarn':'Отрасль · Сырьё и пряжа','knitting':'Отрасль · Оборудование и вязание',
 'sewing':'Отрасль · Швейное производство и отделка','quality':'Отрасль · Качество и дефекты','design':'Отрасль · Размеры и конструирование',
 'economy':'Отрасль · Нормирование и экономика','regulation':'Требования · Регламенты, ГОСТ, маркировка',
 'law':'Требования · Труд, персональные данные, налоги','errors':'Неполадки и сообщения об ошибках'}
REVIEWED='2026-10-12'
PROGRAM_SOURCE='экран и код knitERP, версия 12.10.2026'
def parse(text):
    items=[];cur=None
    for raw in text.split('\n'):
        line=raw.rstrip()
        if not line.strip():
            if cur: items.append(cur); cur=None
            continue
        if line.startswith('#'): continue
        m=re.match(r'^([QVALRSEN]):\s?(.*)$',line)
        if not m:
            # продолжение предыдущего абзаца
            cur['A'][-1]+=' '+line.strip(); continue
        k,v=m.groups()
        if k=='Q':
            if cur: items.append(cur)
            cur={'Q':v.strip(),'V':[],'A':[],'L':[],'R':[],'S':[],'E':'basic'}
        elif k=='V': cur['V']+= [x.strip() for x in v.split('|') if x.strip()]
        elif k=='A': cur['A'].append(v.strip())
        elif k=='L': cur['L']+= v.split()
        elif k=='R': cur['R']+= [x.strip() for x in v.split('|') if x.strip()]
        elif k=='S': cur['S'].append(v.strip())
        elif k=='E': cur['E']=v.strip()
    if cur: items.append(cur)
    return items
def main(key, *srcs):
    path=os.path.join(KN,f'qa-{key}.json')
    old={}
    if os.path.exists(path):
        for it in json.load(open(path))['items']: old[it['question']]=it['id']
    used=set(old.values()); n=max([int(i.split('-')[-1]) for i in used] or [0])
    out=[]
    for src in srcs:
        for it in parse(open(src).read()):
            q=it['Q']
            if q in old: id_=old[q]
            else:
                n+=1; id_=f'qa-{key}-{n:04d}'
            out.append({'id':id_,'section':SECTIONS[key],'question':q,'variants':it['V'],'answer':it['A'],'links':it['L'],
                        'roles':it['R'],'sources':it['S'] or [PROGRAM_SOURCE],'reviewed':REVIEWED,'level':it['E']})
    ids=[o['id'] for o in out]; assert len(ids)==len(set(ids)), 'dup'
    qs=[o['question'] for o in out]; d=[q for q in qs if qs.count(q)>1]; assert not d, d
    out.sort(key=lambda o:o['id'])
    data={'version':1,'language':'ru','section':SECTIONS[key],
          'note':'Готовые ответы помощника (D900). Формат и правила — docs/tz/knowledge-base.md §3. id не меняется; при изменении экрана правится ответ.',
          'items':out}
    s=json.dumps(data,ensure_ascii=False,indent=1)
    open(path,'w').write(s+'\n')
    print(key,len(out))
if __name__=='__main__': main(*sys.argv[1:])
