"""Publish an existing local commit through GitHub Git Data API when Git transport is unavailable."""
from pathlib import Path
import subprocess, json, base64, tempfile, concurrent.futures, sys, datetime
repoPath=Path(sys.argv[1]); remote=sys.argv[2]; branch=sys.argv[3]
def git(*args):return subprocess.check_output(['git',*args],cwd=repoPath)
def api(endpoint,body=None):
    command=['gh','api',endpoint]
    if body is None:return json.loads(subprocess.check_output(command))
    with tempfile.NamedTemporaryFile(mode='w',encoding='utf-8',suffix='.json',delete=False,dir=r'C:\Users\thx11\.codex\release-staging') as f:
        json.dump(body,f,ensure_ascii=False);name=f.name
    try:
        return json.loads(subprocess.check_output(command+['--method','POST','--input',name]))
    finally:Path(name).unlink()
sha=git('rev-parse','HEAD').decode().strip();parent=git('rev-parse','HEAD^').decode().strip()
parentData=api('repos/'+remote+'/git/commits/'+parent)
changes=[]
for row in git('diff-tree','--no-commit-id','--name-status','-r','--no-renames',sha).decode().splitlines():
    status,path=row.split('\t',1)
    if status=='D':changes.append({'path':path,'mode':'100644','type':'blob','sha':None});continue
    entry=git('ls-tree',sha,'--',path).decode().split('\t')[0].split()
    mode,typ,blob=entry
    assert typ=='blob',path
    changes.append({'path':path,'mode':mode,'type':'blob','sha':blob})
def upload(entry):
    if entry['sha'] is None:return
    data=git('cat-file','blob',entry['sha'])
    result=api('repos/'+remote+'/git/blobs',{'content':base64.b64encode(data).decode(),'encoding':'base64'})
    assert result['sha']==entry['sha']
with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
    list(pool.map(upload,changes))
tree=api('repos/'+remote+'/git/trees',{'base_tree':parentData['tree']['sha'],'tree':changes})
expectedTree=git('rev-parse','HEAD^{tree}').decode().strip();assert tree['sha']==expectedTree,(tree['sha'],expectedTree)
raw=git('cat-file','commit',sha).decode('utf-8');header,message=raw.split('\n\n',1)
import re
def identity(kind):
    line=next(l for l in header.splitlines() if l.startswith(kind+' '))
    m=re.match(r'\w+ (.*) <(.*)> (\d+) ([+-]\d{4})',line)
    offset=int(m[4][:3])*60+int(m[4][3:])*(1 if m[4][0]=='+' else -1)
    date=datetime.datetime.fromtimestamp(int(m[3]),datetime.timezone(datetime.timedelta(minutes=offset))).isoformat()
    return {'name':m[1],'email':m[2],'date':date}
created=api('repos/'+remote+'/git/commits',{'message':message,'tree':tree['sha'],'parents':[parent],'author':identity('author'),'committer':identity('committer')})
assert created['sha']==sha,(created['sha'],sha)
endpoint='repos/'+remote+'/git/refs'
result=api(endpoint,{'ref':'refs/heads/'+branch,'sha':sha})
print(json.dumps({'repository':remote,'branch':branch,'commit':sha,'changedFiles':len(changes),'url':result['url']},ensure_ascii=False))
