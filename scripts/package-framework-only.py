"""Build matching portable ZIPs; publish the ordinary ZIP only."""
from pathlib import Path
import argparse, hashlib, json, re, zipfile


def digest(data):
    return hashlib.sha256(data).hexdigest()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--version', required=True)
    parser.add_argument('--app', type=Path, required=True)
    parser.add_argument('--stub', type=Path, required=True)
    parser.add_argument('--framework-zip', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--full-only', action='store_true', help='Keep the already published ordinary ZIP unchanged')
    args = parser.parse_args()
    assert (args.app / 'HoYoShadeHub.dll').is_file()
    assert args.stub.is_file()
    args.output.mkdir(parents=True, exist_ok=True)
    base = f'HoYoShadeHub_Portable_{args.version}_x64'
    ordinary = args.output / (base + '.zip')
    full = args.output / (base + '_Full.zip')
    app_prefix = f'app-{args.version}/'
    records = []
    common_readme = f'HoYoShadeHub {args.version}\n\nXXMI: per-game Manual / Official launch mode and global launcher EXE path are available from the gear beside Enable XXMI. Old launcher instances are closed before launch.\nKnown issue: XXMI compatibility with Genshin / Star Rail and OptiScaler frame generation remains unresolved. Disable XXMI if it prevents launching.\nNo OptiScaler, Bridge modules, extra addons, game packs or personal data are bundled.\n'
    if not args.full_only:
        with zipfile.ZipFile(ordinary, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as z:
            z.write(args.stub, 'HoYoShadeHub.exe')
            z.writestr('version.ini', f'exe_path=app-{args.version}\\HoYoShadeHub.exe\r\n')
            z.writestr('.portable', b'')
            z.writestr('README.txt', (common_readme + '\nOrdinary package: launcher only. Install HoYoShade framework and optional modules as needed.\n').encode('utf-8'))
            for file in sorted(args.app.rglob('*')):
                if file.is_file() and file.suffix.lower() != '.pdb':
                    z.write(file, app_prefix + file.relative_to(args.app).as_posix())
    with zipfile.ZipFile(ordinary) as source, zipfile.ZipFile(args.framework_zip) as framework, zipfile.ZipFile(full, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as z:
        for name in source.namelist():
            if name != 'README.txt':
                z.writestr(name, source.read(name))
        for name in framework.namelist():
            if not name.startswith('HoYoShade/') or name.endswith('/') or '/Addons/' in name:
                continue
            if name.lower().endswith(('.log', '.bak', '.dmp', '.db')):
                continue
            data = framework.read(name)
            if name.lower().endswith('.ini'):
                text = data.decode('utf-8-sig').replace('\r\r\n', '\n').replace('\r\n', '\n')
                if name == 'HoYoShade/ReShade.ini':
                    text = re.sub(r'(?mi)^Language\s*=[^\n]*\n?', '', text)
                    text = text.replace('[OVERLAY]\n', '[OVERLAY]\nLanguage=zh-CN\n', 1)
                for key, value in {'AddonPath':'.\\reshade-shaders\\Addons\\', 'EffectSearchPaths':'.\\reshade-shaders\\Shaders\\**', 'TextureSearchPaths':'.\\reshade-shaders\\Textures\\**'}.items():
                    text = re.sub(r'(?mi)^' + key + r'\s*=[^\r\n]*', lambda _, v=value, k=key: k + '=' + v, text)
                data = text.encode('utf-8')
            z.writestr(name, data)
        z.writestr('HoYoShade/reshade-shaders/Addons/', b'')
        marker = {'schema':1, 'layout':'full-portable-v1', 'moduleRoot':'Modules', 'optiscalerRoot':'OptiScaler', 'cacheRoot':'cache', 'bundleScope':'launcher-and-hoyoshade-framework-only'}
        z.writestr('portable-layout.json', json.dumps(marker, indent=2))
        z.writestr('README.txt', (common_readme + '\nFull package: launcher plus stock HoYoShade framework and shader/texture resources only.\n').encode('utf-8'))
    app_hash = digest((args.app / 'HoYoShadeHub.dll').read_bytes())
    for archive in (ordinary, full):
        with zipfile.ZipFile(archive) as z:
            assert z.testzip() is None
            names = z.namelist()
            assert digest(z.read(app_prefix + 'HoYoShadeHub.dll')) == app_hash
            assert z.read('version.ini').decode().strip() == f'exe_path=app-{args.version}\\HoYoShadeHub.exe'
            assert not any(n.startswith(('Modules/', 'modules/', 'OptiScaler/', 'cache/', '.hysx/', 'GamePack/')) for n in names)
            assert not any('/Addons/' in n and not n.endswith('/') for n in names)
            assert not any(n.lower().endswith(('.db', '.dmp', '.log')) for n in names)
            assert not any(Path(n).name.lower().startswith(('nvngx', 'sl.')) for n in names)
            if archive == ordinary:
                assert not any(n.startswith('HoYoShade/') for n in names)
            else:
                assert 'HoYoShade/ReShade64.dll' in names
                assert 'Language=zh-CN' in z.read('HoYoShade/ReShade.ini').decode('utf-8')
            records.append({'zip':str(archive.resolve()), 'size':archive.stat().st_size, 'sha256':digest(archive.read_bytes()), 'files':len(names), 'uploadToGithub':archive == ordinary})
    (args.output / 'package-report.json').write_text(json.dumps({'version':args.version, 'appSha256':app_hash, 'packages':records}, indent=2), encoding='utf-8')
    print(json.dumps(records, indent=2))


if __name__ == '__main__':
    main()
