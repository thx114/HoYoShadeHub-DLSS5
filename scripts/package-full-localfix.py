from pathlib import Path
import zipfile,json,re,hashlib,shutil
root=Path(r'D:\CODE\HoyoDLSS5')
m=json.loads((root/'build/full-localfix-deployment.json').read_text(encoding='utf-8-sig'))
app=Path(m['app'])
old=zipfile.ZipFile(r'D:\APPS\test\HoYoShadeHub_Portable_1.4.0.3_x64_Full.zip')
overlay=zipfile.ZipFile(r'D:\APPS\test\原神6倍覆盖包_2.5.zip')
output=Path(r'D:\APPS\test\HoYoShadeHub_Portable_1.4.1.2_x64_Full_FirstLaunchFix.zip')
# Compose runtime/framework/bundled modules from pristine packages, never live user DB/logs.
entries={}
for name in old.namelist():
 if name.startswith('HoYoShade/') and not name.endswith('/'):
  if name.endswith(('.log','.bak')):continue
  entries[name]=('archive',old,name)
for name in overlay.namelist():
 if name.endswith('/'):continue
 if name.startswith(('HoYoShade/','OptiScaler/')):entries[name]=('archive',overlay,name)
 elif name.startswith('modules/'):entries['Modules/'+name[len('modules/'):]]=('archive',overlay,name)
 elif name.startswith('GamePack/'):
  entries['cache/games/.pending-gamepacks/hk4e_cn/'+name[len('GamePack/'):]]=('archive',overlay,name)
for path in app.rglob('*'):
 if path.is_file() and path.suffix.lower() not in ['.pdb']:
  entries['app-1.4.1.2/'+path.relative_to(app).as_posix()]=('file',path)
entries['HoYoShadeHub.exe']=('file',Path(m['root'])/'HoYoShadeHub.exe')
entries['.portable']=('bytes',b'')
entries['version.ini']=('bytes',b'exe_path=app-1.4.1.2\\HoYoShadeHub.exe\r\n')
entries['portable-layout.json']=('file',root/'build/portable-layout-localfix.json')
for p in (root/'catalog').rglob('*'):
 if p.is_file():entries['.hysx/catalog/'+p.relative_to(root/'catalog').as_posix()]=('file',p)
readme='''HoYoShadeHub 1.4.1.2 本地完整版测试包

此包仅供本地测试，不是在线新版本。包含启动器、完整HoYoShade资源、OptiScaler MFG Ada0.1.9、原神Bridge2.3.2和原神2.5游戏包动作。

修复：添加米家EXE时转入原游戏方案；XXMI手动唤起后等待1秒，继续启动器注入/起游戏；识别Bridge2.3.2和旧/新模块目录；正常完整便携布局不误弹“版本更新引导”。首次原神启动在创建进程前准备profile与运行库路径，避免先加载auto路径导致透明窗口。旧安装仍可手动迁移。

新测试请解压到新的空目录，从根HoYoShadeHub.exe启动。config.ini/数据库/游戏目录等会在本机创建，本包不带任何人的游戏路径、登录数据、数据库或日志。
已安装测试版请勿拿此Full包覆盖个人数据，建议保留旧目录做对照。
原神模块/预设随包放置；识别原神后可在插件页确认原神2.5包动作。启动器在Opt开启且已装合法Bridge时自动关联Bridge到原神。

测试：添加原神/星铁EXE应进入原游戏；XXMI开关启用时先手动启动注入器，约1秒后进入Hub启动流程；原神不应误报Bridge2.3.2缺失。完整便携包不应自动出现旧缓存升级引导。实际XXMI和游戏启动仍需用户复测。

应用代码构建成功，扩展805项测试通过。NR guide匹配/menu/resize/CPU同步修正保持，失败GPUWait优化未包含。不保证NR两pass性能提升。
'''
entries['README-LOCAL-TEST.txt']=('bytes',readme.encode('utf-8'))
checks=[]
with zipfile.ZipFile(output,'w',zipfile.ZIP_DEFLATED,compresslevel=1,allowZip64=True) as dest:
 for name,source in entries.items():
  assert '\\' not in name and not name.startswith('/') and '..' not in name.split('/')
  if source[0]=='archive':
   _,archive,member=source
   if name.endswith('.ini'):
    data=archive.read(member)
    try:
     text=data.decode('utf-8-sig')
     # Replace all source-machine absolute paths inside stock config with package-relative paths.
     if name.startswith('HoYoShade/'):
      replacements={'AddonPath':'.\\reshade-shaders\\Addons\\','EffectSearchPaths':r'.\reshade-shaders\Shaders\**','TextureSearchPaths':r'.\reshade-shaders\Textures\**'}
      for key,value in replacements.items():
       text=re.sub(r'(?mi)^'+key+r'\s*=[^\r\n]*',lambda _:key+'='+value,text)
     data=text.encode('utf-8')
    except UnicodeDecodeError:pass
    dest.writestr(name,data);checks.append((name,len(data),hashlib.sha256(data).hexdigest()))
   else:
    h=hashlib.sha256();size=0
    with archive.open(member) as reader,dest.open(name,'w',force_zip64=True) as writer:
     while chunk:=reader.read(1024*1024):writer.write(chunk);h.update(chunk);size+=len(chunk)
    checks.append((name,size,h.hexdigest()))
  elif source[0]=='file':
   path=source[1];h=hashlib.sha256();size=0
   with path.open('rb') as reader,dest.open(name,'w',force_zip64=True) as writer:
    while chunk:=reader.read(1024*1024):writer.write(chunk);h.update(chunk);size+=len(chunk)
   checks.append((name,size,h.hexdigest()))
  else:
   data=source[1];dest.writestr(name,data);checks.append((name,len(data),hashlib.sha256(data).hexdigest()))
 dest.writestr('PACKAGE-SHA256SUMS.txt',''.join(sha+'  '+name+'\n' for name,size,sha in checks))
old.close();overlay.close()
with zipfile.ZipFile(output) as verify:
 assert verify.testzip() is None
 forbidden=[n for n in verify.namelist() if n.endswith(('.db','.dmp','.log')) or 'login' in n.lower() or n=='config.ini']
 assert not forbidden,forbidden
 assert verify.read('version.ini')==b'exe_path=app-1.4.1.2\\HoYoShadeHub.exe\r\n'
 assert verify.read('app-1.4.1.2/HoYoShadeHub.dll')==(app/'HoYoShadeHub.dll').read_bytes()
 assert json.loads(verify.read('portable-layout.json'))['layout']=='full-portable-v1'
 assert 'Modules/genshin-fsr-bridge/Dx11FsrBridge.dll' in verify.namelist()
 assert 'cache/games/.pending-gamepacks/hk4e_cn/auto.json' in verify.namelist()
 for name,size,sha in checks:
  assert verify.getinfo(name).file_size==size
report={'zip':str(output),'size':output.stat().st_size,'sha256':hashlib.sha256(output.read_bytes()).hexdigest(),'files':len(checks)+1,'appDllSha256':m['dllSha256'],'tests':'805 pass0fail','published':False}
(root/'build/full-localfix-package.json').write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(json.dumps(report,ensure_ascii=False,indent=2))
