"""addvar.py key-NNN 'вариант' ... — добавить вариант формулировки в исходник ответа по id."""
import json,sys,glob,re,os
ROOT=os.path.abspath(os.path.join(os.path.dirname(__file__),'..','..','..'))
KN=os.path.join(ROOT,'src','KnitErp.Application','Workspace','Knowledge')
args=sys.argv[1:]
for i in range(0,len(args),2):
    ref,var=args[i],args[i+1]
    key,num=ref.rsplit('-',1); id_=f'qa-{key}-{int(num):04d}'
    q=[it['question'] for it in json.load(open(f'{KN}/qa-{key}.json'))['items'] if it['id']==id_][0]
    done=False
    for f in sorted(glob.glob(os.path.join(ROOT,'docs','kb','src',f'{key}*.txt'))):
        s=open(f).read()
        if 'Q: '+q+'\n' in s:
            if re.search(re.escape('Q: '+q)+r'\nV: ',s):
                s=s.replace('Q: '+q+'\nV: ','Q: '+q+'\nV: '+var+' | ',1)
            else:
                s=s.replace('Q: '+q+'\n','Q: '+q+'\nV: '+var+'\n',1)
            open(f,'w').write(s); done=True; break
    print(id_, 'ok' if done else 'NOT FOUND')
