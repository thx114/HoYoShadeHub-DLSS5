"""Rebuild the existing Genshin overlay around the verified CPU-sync release."""
from pathlib import Path
import shutil, json, re, zipfile, hashlib, datetime
root = Path(__file__).resolve().parents[1]
import argparse
parser=argparse.ArgumentParser()
parser.add_argument('--stage',type=Path)
args=parser.parse_args()
if args.stage:
    out=args.stage.resolve()
    assert out.is_dir()
else:
    out=root/'build'/('overlay-genshin-6x-2.5-'+datetime.datetime.now().strftime('%Y%m%d-%H%M%S'))
    shutil.copytree(root/'build/overlay-genshin-6x-20261003',out)
    old=out/'OptiScaler/mfg-ada/mfg-ada-0.1.8'
    target=out/'OptiScaler/mfg-ada/mfg-ada-0.1.9'
    assert old.resolve().is_relative_to(out.resolve()) and target.resolve().is_relative_to(out.resolve())
    shutil.move(str(old),str(target))
new=out/'OptiScaler/mfg-ada/mfg-ada-0.1.9'
repo = root/'OptiScaler-MFG-Ada'
shutil.copy2(repo/'release/optiscaler-mfg-ada-fg-only-0.1.9/OptiScaler.dll', new/'OptiScaler.dll')
profile = (repo/'presets/Genshin-NR.ini').read_text(encoding='utf-8-sig')
for relative in ['OptiScaler.ini','profiles/hk4e_cn.ini']:
    path=new/relative;path.parent.mkdir(parents=True,exist_ok=True);path.write_text(profile,encoding='utf-8')
for path in [out/'GamePack/40-原神 x6.ini',out/'OptiScaler/presets/40-原神 x6.ini']:
    path.write_text(profile,encoding='utf-8')
(new/'build.json').write_text(json.dumps({'sourceId':'mfg-ada','version':'mfg-ada-0.1.9','assetName':'optiscaler-mfg-ada-fg-only-0.1.9.zip','installedAt':datetime.datetime.now().astimezone().isoformat()},ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
(out/'OptiScaler/state.json').write_text('{"selected":"mfg-ada/mfg-ada-0.1.9"}\n',encoding='utf-8')
autoPath=out/'GamePack/auto.json'
auto=json.loads(autoPath.read_text(encoding='utf-8-sig'))
for action in auto['actions']:
    action['name']='原神 6x 2.5（NR guide 对齐 / CPU 同步 / Bridge 实例隔离）'
    for step in action['steps']:
        if step['action']=='set_opt_build': step['build']='mfg-ada/mfg-ada-0.1.9'
        if step['action']=='set_ini_keys':
            for section,values in step.get('set',{}).items():
                if section=='RenoDX.DLSS5':
                    values.update(DX11Source='native',EnableHooks='2',NRHookPoint='2',NRPresentGuides='1',NRMVecScaleX='1',NRMVecScaleY='1')
autoPath.write_text(json.dumps(auto,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
bridge=out/'modules/genshin-fsr-bridge'
for p in (root/'build/release-genshin-bridge-2.3.2').iterdir():
    if p.is_file():shutil.copy2(p,bridge/p.name)
p=bridge/'Dx11FsrBridge.autoload.txt'
if p.exists():p.unlink()
readme='''原神6倍覆盖包 2.5

OptiScaler MFG Ada0.1.9 + Bridge2.3.2 + 外部RenoDX DLSS5。
保留整组NR depth/motion/color对齐、菜单/resize修正及CPU reader retirement。
原神预设明确NativeScreenSpaceGuides=true；副runtime不重复加载Present插件。
自动选择mfg-ada/mfg-ada-0.1.9、启用OptiScaler和Bridge、导入40-原神 x6。
Bridge默认关闭input dump，无机器绝对路径，AMD SDK沿用包内modules/AMD。

失败的GPU Wait优化没有打包。最近C/活动/进房间复测无挂起，原始帧率偏低仍需优化。
首次加载/切换可能有长帧；并非每个硬件/游戏组合保证。升级前退出游戏并备份。
建议使用配套启动器1.4.1.3（profile激活后维护原神guide开关且识别Bridge新小版本）。
'''
(out/'GamePack/README.txt').write_text(readme,encoding='utf-8')
m=json.loads((out/'filelist.json').read_text(encoding='utf-8-sig'))
m['version']='2.5';m['note']='原神NR guide对齐修复；OptiScaler0.1.9 CPU同步、菜单/resize修正；Bridge2.3.2实例隔离；未包含失败GPU Wait性能实验。'
m['optiscaler']['version']='mfg-ada-0.1.9'
m['files']=[{'path':p.relative_to(out).as_posix(),'size':p.stat().st_size} for p in sorted(out.rglob('*')) if p.is_file() and p.name!='filelist.json']
manifest=out/'filelist.json'
for i in range(8):
    manifest.write_text(json.dumps(m,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    entry=next((f for f in m['files'] if f['path']=='filelist.json'),None)
    if entry is None:m['files'].append({'path':'filelist.json','size':manifest.stat().st_size})
    elif entry['size']==manifest.stat().st_size:break
    else:entry['size']=manifest.stat().st_size
else:raise RuntimeError('Manifest self-size did not converge')
assert all((out/f['path']).stat().st_size==f['size'] for f in m['files'])
assert not any('mfg-ada-0.1.8' in f['path'] for f in m['files'])
assert 'NativeScreenSpaceGuides=true' in profile
zipPath=Path(r'D:\APPS\test\原神6倍覆盖包_2.5.zip')
with zipfile.ZipFile(zipPath,'w',zipfile.ZIP_DEFLATED,compresslevel=1) as z:
    for p in out.rglob('*'):
        if p.is_file():z.write(p,p.relative_to(out).as_posix())
with zipfile.ZipFile(zipPath) as z:
    assert z.testzip() is None
    assert len(z.namelist())==len(m['files'])
report={'stage':str(out),'zip':str(zipPath),'size':zipPath.stat().st_size,'sha256':hashlib.sha256(zipPath.read_bytes()).hexdigest(),'files':len(m['files']),'optVersion':'0.1.9.0','bridgeVersion':'2.3.2.0'}
(root/'build/release-genshin-overlay-2.5.json').write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(json.dumps(report,ensure_ascii=False,indent=2))
