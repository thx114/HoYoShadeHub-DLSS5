"""把覆盖包里 GamePack/auto.json 的「启用插件」步骤补上 FSR Bridge Depth Provider。

为什么需要这个补丁
------------------
覆盖包 GamePack/auto.json 的开头两步是：

    { "action": "set_addons", "enabled": false, "once": true },                       // 先全关
    { "action": "set_addon",  "enabled": true,  "files": ["renodx-dlss5.addon64"] },  // 只把 NR 开回来

于是同包的 FsrBridgeDepthAddon.addon64 被留在主 runtime 的 [ADDON] DisabledAddons 里：

* 主 runtime（游戏目录 ReShade.ini）不加载深度桥 → 拿不到 DX11 深度；
* 最终 DX12 runtime 也拿不到，因为按设计**深度由主 runtime 的 addon 提供**——
  GameIniBootstrap.EnsureFinalDx12 只把主 runtime 的 [ADDON] 管理态镜像给最终 runtime
  （src\\HoYoShadeHub.Extensions\\Games\\GameIniBootstrap.cs:227-232 的注释：
  「插件管理态同步，但最终 runtime 不重复加载 Present/NR 插件。主 runtime 的 addon 全局事件
  已经为最终 DX12 runtime 提供深度。」）。
* 插件自己的 README（D:\\CODE\\FsrBridgeDepthAddon\\README.md:13）也要求：
  「明确单套模式依赖此插件，因此维护其启用态并保留其他插件禁用项」。

补丁只做一件事：把 set_addon(enabled=true) 那一步的 files 里加上 FsrBridgeDepthAddon.addon64，
其余步骤、其余插件开关一律不动（其他插件继续按原设计禁用）。

副作用（正向）
--------------
once 步骤的记账键是「动作名 + 步骤内容哈希」（LauncherActionRunner.cs:770-773），
所以改过内容的这一步在**已装过的机器上会再跑一次**（把桥开回来），旧的其余步骤不会重跑。

用法
----
    python scripts\\patch-overlay-enable-fsr-bridge.py <包.zip> [<包.zip> ...] [--dry-run]

就地改写 zip（同名同目录），原文件先备份到 --backup-dir（默认
.build-temp\\overlay-fsrbridge-enable-<日期>\\）。除 GamePack/auto.json 与 filelist.json
（尺寸字段）外，所有条目的内容逐字节保持不变。
"""
from __future__ import annotations

import argparse
import datetime as _dt
import hashlib
import json
import shutil
import sys
import zipfile
from pathlib import Path

DEFAULT_ADDON = "FsrBridgeDepthAddon.addon64"
AUTO = "GamePack/auto.json"
MANIFEST = "filelist.json"
ENABLE_ACTIONS = ("set_addon", "set_addons")


def _load_json(raw: bytes):
    return json.loads(raw.decode("utf-8-sig"))


def _dump_json(obj) -> bytes:
    return (json.dumps(obj, ensure_ascii=False, indent=2) + "\n").encode("utf-8")


def _patch_auto(auto: dict, addon: str) -> tuple[dict, str, bool]:
    """把 addon 加进「启用插件」步骤；返回 (内容, 说明, 是否改动)。"""
    actions = auto.get("actions")
    if not isinstance(actions, list) or not actions:
        raise SystemExit("auto.json 里没有 actions")
    for action in actions:
        steps = action.get("steps")
        if not isinstance(steps, list):
            continue
        for step in steps:
            if str(step.get("action", "")).strip().lower() not in ENABLE_ACTIONS:
                continue
            if step.get("enabled") is not True:
                continue
            files = step.get("files")
            if files is None:
                # 没有 files = 该游戏包目录里所有 addon 全开，本来就包含桥
                return auto, f"已有「启用全部插件」步骤（{step.get('action')}），无需改动", False
            files = [str(f) for f in files]
            if any(f.lower() == addon.lower() for f in files):
                return auto, f"「启用插件」步骤里已经有 {addon}", False
            files.append(addon)
            step["files"] = files
            return auto, f"已在「启用插件」步骤里加上 {addon}（现有 {len(files)} 项：{'、'.join(files)}）", True
    raise SystemExit("auto.json 里找不到 set_addon(enabled=true) 步骤（结构变了，请人工确认）")


def _patch_manifest(manifest: dict, sizes: dict[str, int]) -> dict:
    """同步 filelist.json 的 files[] 尺寸（含它自己的自尺寸收敛）。"""
    files = manifest.get("files")
    if not isinstance(files, list):
        raise SystemExit("filelist.json 里没有 files[]")
    known = {str(f.get("path")) for f in files}
    for entry in files:
        path = str(entry.get("path"))
        if path in sizes:
            entry["size"] = sizes[path]
    if MANIFEST not in known:
        files.append({"path": MANIFEST, "size": sizes.get(MANIFEST, 0)})
    for _ in range(8):
        raw = _dump_json(manifest)
        entry = next(f for f in files if str(f.get("path")) == MANIFEST)
        if entry["size"] == len(raw):
            break
        entry["size"] = len(raw)
    else:
        raise SystemExit("filelist.json 自尺寸不收敛")
    return manifest


def patch_zip(zip_path: Path, addon: str, backup_dir: Path, dry_run: bool) -> dict:
    before = hashlib.sha256(zip_path.read_bytes()).hexdigest()
    before_size = zip_path.stat().st_size

    with zipfile.ZipFile(zip_path) as zin:
        names = zin.namelist()
        for required in (AUTO, MANIFEST):
            if required not in names:
                raise SystemExit(f"{zip_path.name}: 缺 {required}")
        old_auto_raw = zin.read(AUTO)
        old_manifest_raw = zin.read(MANIFEST)
        auto = _load_json(old_auto_raw)
        manifest = _load_json(old_manifest_raw)
        new_auto, note, auto_changed = _patch_auto(auto, addon)
        if not auto_changed:
            return {"zip": zip_path.name, "changed": False, "note": note,
                    "sha256_before": before, "sha256_after": before, "size_before": before_size,
                    "size_after": before_size, "changed_entries": []}

        new_auto_raw = _dump_json(new_auto)
        sizes = {AUTO: len(new_auto_raw)}
        for entry in manifest.get("files", []):
            path = str(entry.get("path"))
            if path == AUTO:
                sizes[path] = len(new_auto_raw)
            elif path != MANIFEST and path not in sizes:
                sizes[path] = int(entry.get("size", 0))
        manifest = _patch_manifest(manifest, sizes)
        new_manifest_raw = _dump_json(manifest)
        # 自尺寸再收敛一次（以真实条目为准；用 ZipInfo.file_size，不解压）
        real_sizes = {i.filename: i.file_size for i in zin.infolist() if not i.filename.endswith("/")}
        real_sizes[AUTO] = len(new_auto_raw)
        manifest = _patch_manifest(manifest, real_sizes)
        new_manifest_raw = _dump_json(manifest)

        if dry_run:
            return {"zip": zip_path.name, "changed": True, "note": note,
                    "sha256_before": before, "sha256_after": before, "size_before": before_size,
                    "size_after": before_size, "changed_entries": [AUTO, MANIFEST], "dry_run": True}

        backup_dir.mkdir(parents=True, exist_ok=True)
        backup = backup_dir / zip_path.name
        shutil.copy2(zip_path, backup)

        tmp = zip_path.with_suffix(zip_path.suffix + ".patching")
        replaced = {AUTO, MANIFEST}
        with zipfile.ZipFile(backup) as zsrc, \
                zipfile.ZipFile(tmp, "w", zipfile.ZIP_DEFLATED, compresslevel=1) as zout:
            for info in zsrc.infolist():
                if info.filename == AUTO:
                    zout.writestr(info, new_auto_raw)
                elif info.filename == MANIFEST:
                    zout.writestr(info, new_manifest_raw)
                else:
                    zout.writestr(info, zsrc.read(info.filename))

    with zipfile.ZipFile(tmp) as zchk:
        assert zchk.testzip() is None, "新 zip 校验失败"
        assert zchk.namelist() == names, "条目顺序/数量变了"
        for name in names:
            if name in replaced or name.endswith("/"):
                continue
            with zipfile.ZipFile(backup) as zold:
                assert zchk.read(name) == zold.read(name), f"{name} 内容被改动了"
        chk_auto = _load_json(zchk.read(AUTO))
        chk_manifest = _load_json(zchk.read(MANIFEST))
        assert any(addon.lower() in [str(f).lower() for f in step.get("files", [])]
                   for action in chk_auto["actions"] for step in action.get("steps", [])
                   if step.get("enabled") is True), "补丁后仍未启用"
        for entry in chk_manifest["files"]:
            path = str(entry.get("path"))
            actual = len(zchk.read(path))
            assert int(entry["size"]) == actual, f"清单尺寸不符：{path} 写 {entry['size']} 实际 {actual}"

    tmp.replace(zip_path)
    after = hashlib.sha256(zip_path.read_bytes()).hexdigest()
    return {"zip": zip_path.name, "changed": True, "note": note,
            "sha256_before": before, "sha256_after": after,
            "size_before": before_size, "size_after": zip_path.stat().st_size,
            "changed_entries": sorted(replaced), "backup": str(backup)}


def main() -> int:
    ap = argparse.ArgumentParser(description="覆盖包：把 FSR Bridge Depth Provider 加进启用插件步骤")
    ap.add_argument("zips", nargs="+", type=Path)
    ap.add_argument("--addon-file", default=DEFAULT_ADDON)
    ap.add_argument("--backup-dir", type=Path, default=None)
    ap.add_argument("--dry-run", action="store_true")
    args = ap.parse_args()

    backup_dir = args.backup_dir or (
        Path(__file__).resolve().parents[1] / ".build-temp"
        / f"overlay-fsrbridge-enable-{_dt.datetime.now():%Y%m%d}")

    report = []
    for zip_path in args.zips:
        if not zip_path.is_file():
            print(f"跳过（不存在）：{zip_path}", file=sys.stderr)
            continue
        result = patch_zip(zip_path, args.addon_file, backup_dir, args.dry_run)
        report.append(result)
        tag = "DRY" if args.dry_run else ("改了" if result["changed"] else "没动")
        print(f"[{tag}] {result['zip']}")
        print(f"       {result['note']}")
        if result.get("changed"):
            print(f"       大小 {result['size_before']:,} → {result['size_after']:,} B")
            print(f"       sha256 {result['sha256_before'][:16]}… → {result['sha256_after'][:16]}…")
            if result.get("backup"):
                print(f"       备份 {result['backup']}")
    out = Path(__file__).resolve().parents[1] / ".build-temp" / "overlay-fsrbridge-enable-report.json"
    out.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"\n报告：{out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
