import os,json,subprocess,threading,queue,time
from pathlib import Path
base=Path.cwd()/'artifacts/host-validation'; env=os.environ.copy();env['CLAUDE_CONFIG_DIR']=str(base/'claude-home')
p=subprocess.Popen(['claude','--print','--input-format','stream-json','--output-format','stream-json','--verbose','--plugin-dir',str(base/'packages/claude'),'--no-session-persistence'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',env=env,cwd=base)
q=queue.Queue();errors=[]
def read():
 for line in p.stdout:
  try:q.put(json.loads(line))
  except ValueError:pass
threading.Thread(target=read,daemon=True).start();threading.Thread(target=lambda:errors.extend(p.stderr.readlines()),daemon=True).start()
report={'passed':False,'modelTurnsSent':0};seq=0
def rpc(request):
 global seq
 seq+=1;identity=str(seq);p.stdin.write(json.dumps({'type':'control_request','request_id':identity,'request':request})+'\n');p.stdin.flush();deadline=time.monotonic()+30
 while time.monotonic()<deadline:
  v=q.get(timeout=max(.1,deadline-time.monotonic()))
  if v.get('type')=='control_response' and v.get('response',{}).get('request_id')==identity:
   if v['response']['subtype']=='error':raise RuntimeError(v['response'])
   return v['response'].get('response',{})
try:
 initial=rpc({'subtype':'initialize','hooks':{}}); report['skillLoaded']=any(c.get('name')=='mission-control:developer-memory' for c in initial.get('commands',[])); report['agentLoaded']=any(a.get('name')=='mission-control:memory-reader' for a in initial.get('agents',[]))
 for attempt in range(15):
  time.sleep(1)
  status=rpc({'subtype':'mcp_status'});report['status']=status
  if any(s.get('status')=='connected' for s in status.get('mcpServers',[])):break
 report['passed']=any(s.get('status')=='connected' and len(s.get('tools',[]))==5 for s in status.get('mcpServers',[]))
finally:
 p.stdin.close()
 try:p.wait(timeout=10)
 except subprocess.TimeoutExpired:p.terminate();p.wait(timeout=10)
 report['toolExecutionTested']=False
 (base/'claude-native-mcp.json').write_text(json.dumps(report,indent=2),encoding='utf-8');print(json.dumps({'passed':report['passed'],'modelTurnsSent':0,'toolExecutionTested':False}))
