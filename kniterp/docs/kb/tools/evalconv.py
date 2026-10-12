import json,sys,glob
items=[]
for f in sorted(glob.glob('/tmp/claude-0/kb/eval-*.txt')):
    for line in open(f):
        line=line.strip()
        if not line or line.startswith('#'): continue
        q,rest=line.split('=>')
        parts=[p.strip() for p in rest.split('|')]
        key,num=parts[0].rsplit('-',1)
        it={'q':q.strip(),'id':f'qa-{key}-{int(num):04d}'}
        if len(parts)>1: it['route']=parts[1]
        items.append(it)
json.dump({'version':1,'note':'Тестовый набор поиска помощника (D915): вопросы, как их задают люди, и ожидаемый ответ. Порог: первым — 85 %, в тройке — 95 %. route — экран, с которого спрашивают.','items':items},
          open('/home/user/kniterp.ru/kniterp/docs/kb/eval.json','w'),ensure_ascii=False,indent=1)
print(len(items))
