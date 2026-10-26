import json,os,subprocess,threading,queue,time
from pathlib import Path
root=Path.cwd(); base=root/'artifacts/host-validation'; data=base/'data/live'; data.mkdir(parents=True,exist_ok=True)
import shutil
shutil.copy2(root/'artifacts/memory-retrieval-benchmark/memory.db',data/'workspace.db')
env=os.environ.copy(); env['CODEX_HOME']=str(base/'codex-home')
p=subprocess.Popen(['codex.cmd','app-server','--stdio','--strict-config'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',env=env,shell=False)
messages=queue.Queue(); errors=[]
def drain():
 for line in p.stdout:
  try: messages.put(json.loads(line))
  except ValueError: pass
threading.Thread(target=drain,daemon=True).start()
threading.Thread(target=lambda: errors.extend(p.stderr.readlines()),daemon=True).start()
seq=0
report={'modelTurnsSent':0,'accountAuthenticationTested':False,'passed':False}
def rpc(method,params):
 global seq
 seq+=1; p.stdin.write(json.dumps({'id':seq,'method':method,'params':params})+'\n'); p.stdin.flush(); deadline=time.monotonic()+35
 while time.monotonic()<deadline:
  item=messages.get(timeout=max(.1,deadline-time.monotonic()))
  if item.get('id')==seq:
   if 'error' in item: raise RuntimeError(str(item['error']))
   return item['result']
 raise TimeoutError(method)
try:
 report['initialize']=rpc('initialize',{'clientInfo':{'name':'mission-control-fixture-test','version':'0.4.0'},'capabilities':{'experimentalApi':True}})
 p.stdin.write(json.dumps({'method':'initialized'})+'\n'); p.stdin.flush()
 thread=rpc('thread/start',{'cwd':str(base),'ephemeral':True,'sandbox':'read-only','approvalPolicy':'never'}); thread_id=thread['thread']['id']
 status=rpc('mcpServerStatus/list',{'threadId':thread_id}); report['status']=status
 server=next(s for s in status['data'] if s['name']=='mission-control')
 tool_names=set(server['tools']); assert len(tool_names)==5,tool_names
 result=rpc('mcpServer/tool/call',{'threadId':thread_id,'server':server['name'],'tool':'search_history','arguments':{'query':'RESTRICT','limit':5}})
 assert result.get('isError') is not True,result
 payload=json.loads(next(c['text'] for c in result['content'] if c.get('type')=='text')); assert payload['records'][0]['id']=='migration',payload
 report['toolResult']=payload; report['passed']=True
finally:
 p.stdin.close()
 try:p.wait(timeout=10)
 except subprocess.TimeoutExpired:p.terminate();p.wait(timeout=10)
 report['exitCode']=p.returncode
 (base/'codex-native-mcp.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
 print(json.dumps({'passed':report['passed'],'modelTurnsSent':0,'exitCode':p.returncode}))
