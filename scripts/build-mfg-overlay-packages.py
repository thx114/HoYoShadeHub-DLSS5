import hashlib, json, shutil, zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BASE = ROOT / 'build' / 'overlay-20260929-062923' / 'payload'
OLD = BASE / 'OptiScaler/mfg-ada/mfg-ada-0.1.6'
ACTIVE = Path(r'D:\APPS\HoYoShadeHub\OptiScaler\mfg-ada\mfg-ada-0.1.9')
ACTIVE_ADDONS = Path(r'D:\APPS\HoYoShadeHub\HoYoShade\reshade-shaders\Addons')
OUT = ROOT / 'build/overlay-mfg-ada-0.1.9-20261004'
DEPLOY = Path(r'D:\APPS\test\TEST')

for p in (BASE, OLD, ACTIVE, ACTIVE_ADDONS):
    if not p.is_dir():
        raise SystemExit(f'missing: {p}')


def write_json(p, obj):
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(json.dumps(obj, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')


def sha(p):
    h = hashlib.sha256()
    with p.open('rb') as f:
        for b in iter(lambda: f.read(1024 * 1024), b''):
            h.update(b)
    return h.hexdigest()


def common_profile():
    p = OLD / 'profiles/hkrpg_bilibili.ini'
    s = p.read_text(encoding='utf-8-sig')
    for a, b in {
        'Dx11Upscaler = dlss_12': 'Dx11Upscaler = dlss',
        'Dx12Upscaler = auto': 'Dx12Upscaler = dlss',
        'Dx12Upscaler = xess': 'Dx12Upscaler = dlss',
        'FGInput = auto': 'FGInput = Upscaler',
        'FGOutput = auto': 'FGOutput = DLSSG',
        'FGOutput = FSRFG': 'FGOutput = DLSSG',
        'AdaMfgUnlock = false': 'AdaMfgUnlock = true',
    }.items():
        s = s.replace(a, b)
    import re
    s = re.sub(r'(?m)^\s*OptiDllPath\s*=.*$', 'OptiDllPath = auto', s)
    for k in ('LogToFile', 'LogToConsole', 'LogToDebug', 'LogToNGX'):
        s = re.sub(rf'(?m)^\s*{k}\s*=.*$', f'{k} = false', s)
    return s

PROFILE = common_profile()


def refresh_mfg(stage):
    d = stage / 'OptiScaler/mfg-ada/mfg-ada-0.1.9'
    shutil.copytree(OLD, d)
    for rel in ('OptiScaler.dll', 'README.md', 'LICENSE', 'get_streamline.ps1', 'setup_windows.bat', 'SHA256SUMS.txt'):
        s = ACTIVE / rel
        if s.is_file(): shutil.copy2(s, d / rel)
    for rel in ('docs', 'redist'):
        s = ACTIVE / rel
        if s.is_dir():
            if (d / rel).exists(): shutil.rmtree(d / rel)
            shutil.copytree(s, d / rel)
    for rel in ('nvngx_dlssg.dll', 'nvngx_dlssnr.dll', 'OptiScaler/nvngx_dlssg.dll'):
        s = ACTIVE / rel
        if s.is_file(): shutil.copy2(s, d / rel)
    for s in (ACTIVE / 'OptiScaler/streamline').glob('*.dll'):
        shutil.copy2(s, d / 'OptiScaler/streamline' / s.name)
    for n in ('hk4e_cn.ini', 'hkrpg_cn.ini', 'hkrpg_bilibili.ini', 'nap_cn.ini'):
        (d / 'profiles' / n).write_text(PROFILE, encoding='utf-8')
    (d / 'OptiScaler.ini').write_text(PROFILE, encoding='utf-8')
    (stage / 'OptiScaler/state.json').write_text("{\"selected\":\"mfg-ada/mfg-ada-0.1.9\"}\n", encoding='utf-8')
    for p in d.rglob('*.log'):
        p.unlink()
    write_json(d / 'build.json', {'sourceId':'mfg-ada','version':'mfg-ada-0.1.9','assetName':'optiscaler-mfg-ada-fg-only-0.1.9.zip','installedAt':'2026-10-04T00:00:00+08:00'})


def write_gamepack(stage, game, name, preset):
    gp = stage / 'GamePack'
    gp.mkdir(parents=True, exist_ok=True)
    (gp / preset).write_text(PROFILE, encoding='utf-8')
    (stage / 'OptiScaler/presets' / preset).write_text(PROFILE, encoding='utf-8')
    (gp / 'README.txt').write_text(f'{name} 6 倍覆盖包\n\nOptiScaler MFG-Ada 0.1.9。统一配置：DLSS → Upscaler → DLSSG。\n使用当前 HoYoShade、RenoDX DLSS5、MFG Unlock。无机器绝对路径。\n覆盖前请退出游戏和启动器并备份。\n', encoding='utf-8')
    write_json(gp / 'auto.json', {'actions':[{'name':f'{name} 6x（MFG-Ada 0.1.9 / DLSSG）','runOnLaunch':True,'steps':[
        {'action':'set_window_mode','value':'borderless','once':True},
        {'action':'set_window_mode','value':'borderless','only_if':'fullscreen'},
        {'action':'set_addons','enabled':False,'once':True},
        {'action':'set_addon','enabled':True,'files':['renodx-dlss5.addon64'],'once':True},
        {'action':'set_optiscaler','enabled':True,'once':True},
        {'action':'set_opt_build','build':'mfg-ada/mfg-ada-0.1.9','once':True},
        {'action':'set_smooth_motion','enabled':False,'once':True},
        {'action':'set_launch_option','key':'usehoyoshade','enabled':True,'once':True},
        {'action':'set_launch_option','key':'useoptiscaler','enabled':True,'once':True},
        {'action':'import_opt_config','file':preset,'once':True},
        {'action':'set_opt_config','name':preset[:-4],'once':True},
    ]}]})


def manifest(stage, game, name, version):
    p = stage / 'filelist.json'
    obj = {'hysxOverlay':1,'name':name,'version':version,'game':game,
           'note':f'{name} DLSS5 + OptiScaler MFG-Ada 0.1.9；统一使用 DLSS、Upscaler→DLSSG 配置。',
           'targets':[{'from':'HoYoShade','to':'shade'},{'from':'OptiScaler','to':'optiscaler'}],
           'dlls':[{'family':'dlssnr','file':'nvngx_dlssnr.dll','version':'310.8.3.0'},
                   {'family':'streamline','file':'sl.interposer.dll','version':'2.14.1.0'},
                   {'family':'dlss','file':'nvngx_dlss.dll','version':'310.9.1.0'},
                   {'family':'dlssd','file':'nvngx_dlssd.dll','version':'310.9.1.0'},
                   {'family':'dlssg','file':'nvngx_dlssg.dll','version':'310.9.1.0'}],
           'optiscaler':{'sourceId':'mfg-ada','version':'mfg-ada-0.1.9','select':True},
           'addons':[{'file':'dlss5-feed.addon64','name':'DLSS 5 Feed','version':'1.17.0'},
                     {'file':'renodx-dlss.addon64','name':'RenoDX DLSS'},
                     {'file':'renodx-dlss5.addon64','name':'DLSS 5 Neural Rendering','version':'0.2026.0926.0210'},
                     {'file':'renodx-mfgunlock.addon64','name':'MFG Unlock'}]}
    obj['files'] = [{'size':p.stat().st_size,'path':p.relative_to(stage).as_posix()} for p in sorted(stage.rglob('*')) if p.is_file() and p != p]
    obj['files'] = [{'size':f.stat().st_size,'path':f.relative_to(stage).as_posix()} for f in sorted(stage.rglob('*')) if f.is_file() and f != p]
    write_json(p, obj)
    for f in obj['files']:
        assert (stage / f['path']).stat().st_size == f['size'], f['path']


def build(game, name, version, filename, preset):
    stage = OUT / game
    shutil.copytree(BASE, stage)
    old = stage / 'OptiScaler/mfg-ada/mfg-ada-0.1.6'
    shutil.rmtree(old)
    refresh_mfg(stage)
    dst = stage / 'HoYoShade/reshade-shaders/Addons'
    for pat in ('dlss5-feed.addon64','dlss5-feed.cfg','nvngx_dlss.dll','nvngx_dlssd.dll','nvngx_dlssg.dll','nvngx_dlssnr.dll','renodx-dlss.addon64','renodx-dlss5.addon64','renodx-mfgunlock.addon64','sl.*.dll'):
        for s in ACTIVE_ADDONS.glob(pat): shutil.copy2(s, dst / s.name)
    for p in dst.glob('*.log'): p.unlink()
    write_gamepack(stage, game, name, preset)
    manifest(stage, game, name, version)
    zpath = OUT / filename
    with zipfile.ZipFile(zpath,'w',zipfile.ZIP_DEFLATED,compresslevel=1) as z:
        for f in sorted(stage.rglob('*')):
            if f.is_file(): z.write(f, f.relative_to(stage).as_posix())
    with zipfile.ZipFile(zpath) as z:
        assert z.testzip() is None
        assert 'filelist.json' in z.namelist() and 'GamePack/auto.json' in z.namelist()
        assert any(x.endswith('mfg-ada-0.1.9/OptiScaler.dll') for x in z.namelist())
        assert not any('mfg-ada-0.1.8' in x for x in z.namelist())
    target = DEPLOY / filename
    DEPLOY.mkdir(parents=True, exist_ok=True)
    shutil.copy2(zpath, target)
    return {'game':game,'stage':str(stage),'zip':str(zpath),'deployedZip':str(target),'size':zpath.stat().st_size,'sha256':sha(zpath),'files':len(json.loads((stage/'filelist.json').read_text(encoding='utf-8'))['files']),'optVersion':'0.1.9.0'}

if OUT.exists(): shutil.rmtree(OUT)
OUT.mkdir(parents=True)
result = [build('hkrpg','星穹铁道','2.6','星穹铁道6倍覆盖包_2.6.zip','40-崩铁 x6.ini'), build('nap','绝区零','1.0','绝区零6倍覆盖包_1.0.zip','40-绝区零 x6.ini')]
write_json(OUT/'build-report.json', {'base':str(BASE),'mfgSource':str(ACTIVE),'packages':result})
print(json.dumps(result,ensure_ascii=False,indent=2))


