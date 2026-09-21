#!/usr/bin/env python3
"""Exercise device enrollment against a local built gateway without model inference."""
import json, tempfile, subprocess, socket, time, os
from pathlib import Path
from urllib.request import Request, urlopen
from urllib.error import HTTPError, URLError
root=Path(__file__).resolve().parents[1]
work=Path(tempfile.mkdtemp(prefix='openclaw-enroll-smoke-'))
with socket.socket() as s: s.bind(('127.0.0.1',0)); port=s.getsockname()[1]
bootstrap=os.urandom(32).hex()
config={'OpenClaw':{'BindAddress':'127.0.0.1','Port':port,'AuthToken':bootstrap,'Security':{'AlwaysRequireAuth':True},'Llm':{'Provider':'ollama','Model':'test'},'Memory':{'Provider':'file','StoragePath':str(work/'memory'),'Sqlite':{'DbPath':str(work/'memory/db.sqlite')},'Retention':{'ArchivePath':str(work/'archive')}},'Tooling':{'EnableBrowserTool':False}}}
p=work/'gateway.json'; p.write_text(json.dumps(config))
env={k:v for k,v in os.environ.items() if not k.lower().startswith('openclaw')}
env['OPENCLAW_WORKSPACE']=str(work)
env['ASPNETCORE_ENVIRONMENT']='Development'
def request(path,body=None,token=None,method=None):
 headers={'Content-Type':'application/json'}
 if token: headers['Authorization']='Bearer '+token
 req=Request(f'http://127.0.0.1:{port}'+path,data=json.dumps(body).encode() if body is not None else None,headers=headers,method=method)
 try:
  with urlopen(req,timeout=8) as response: return response.status,json.load(response)
 except HTTPError as e:
  raw=e.read()
  try: return e.code,json.loads(raw)
  except ValueError: return e.code,{}
with (work/'gateway.log').open('w') as log:
 proc=subprocess.Popen([str(root/'src/OpenClaw.Gateway/bin/Debug/net10.0/OpenClaw.Gateway'),'--config',str(p)],cwd=root/'src/OpenClaw.Gateway',env=env,stdout=log,stderr=subprocess.STDOUT)
 try:
  for _ in range(60):
   if proc.poll() is not None: raise RuntimeError('Gateway exited: '+str(work))
   try:
    if request('/auth/session',token=bootstrap)[0]==200: break
   except (URLError,OSError): pass
   time.sleep(.5)
  status,result=request('/admin/operator-accounts',{'username':'demoowner','password':'test-password-long-enough','role':'operator','enabled':True},bootstrap)
  assert status==201,(status,'create account',str(work))
  account=result['account']['id']
  assert request('/auth/devices/enroll',{'AccountId':account,'DeviceName':'Laptop'})[0]==401
  status,code=request('/auth/devices/enroll',{'AccountId':account,'DeviceName':'Laptop'},bootstrap)
  assert status==200,(status,'enroll',code,str(work))
  status,issued=request('/auth/devices/exchange',{'Code':code.get('code',code.get('Code'))})
  assert status==200,(status,'exchange',str(work))
  token=issued['token']; token_id=issued['tokenInfo']['id']
  assert request('/auth/devices/exchange',{'Code':code.get('code',code.get('Code'))})[0]==401
  assert request('/auth/session',token=token)[0]==200
  assert request('/auth/devices/enroll',{'AccountId':account,'DeviceName':'Second'},token)[0]==403
  assert request(f'/admin/operator-accounts/{account}/tokens/{token_id}',token=bootstrap,method='DELETE')[0]==200
  assert request('/auth/session',token=token)[0]==401
  print('Live enrollment passed: admin-only issue, one-time redemption, account role, authentication and revocation.')
 finally:
  proc.terminate()
  try: proc.wait(timeout=10)
  except subprocess.TimeoutExpired: proc.kill();proc.wait()
