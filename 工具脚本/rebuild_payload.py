# -*- coding: utf-8 -*-
"""重建 源码\\安装器\\payload.zip（安装包里内嵌的部署文件）。

与 打包输出\\build_release.ps1 的「2/5 收集」「3/5 生成 payload」两段规则一致：
    startup\\      rbn_lyj.rtb + 5 个组 + lyj.men + 现编译的 LYJ.dll
    application\\  lyj_*.bmp（固定按钮图标）+ *.dlx（Block Styler 对话框）
    config\\       测试.prt（加工模板）

只在改了这些资源、又不想整包重新打包时单独跑一次。正式出包仍用 build_release.ps1。

用法：python 工具脚本/rebuild_payload.py
"""
import os
import sys
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)

DLL = os.path.join(ROOT, "源码", "LYJ", "bin", "Release", "LYJ.dll")
ZIP = os.path.join(ROOT, "源码", "安装器", "payload.zip")

RIBBON = ["rbn_lyj.rtb", "rbn_lyj_import.grb", "rbn_lyj_export.grb",
          "rbn_lyj_color.grb", "rbn_lyj_cam.grb", "lyj.men"]


def main():
    items = []
    ribbon_dir = os.path.join(ROOT, "资源", "菜单功能区")
    for name in RIBBON:
        items.append((os.path.join(ribbon_dir, name), "startup/" + name))

    icon_dir = os.path.join(ROOT, "资源", "按钮图标")
    for f in sorted(os.listdir(icon_dir)):
        if f.startswith("lyj_") and f.endswith(".bmp"):
            items.append((os.path.join(icon_dir, f), "application/" + f))

    dlx_dir = os.path.join(ROOT, "资源", "对话框")
    if os.path.isdir(dlx_dir):
        for f in sorted(os.listdir(dlx_dir)):
            if f.lower().endswith(".dlx"):
                items.append((os.path.join(dlx_dir, f), "application/" + f))

    items.append((os.path.join(ROOT, "资源", "加工模板", "测试.prt"), "config/测试.prt"))
    items.append((DLL, "startup/LYJ.dll"))

    missing = [s for s, _ in items if not os.path.exists(s)]
    if missing:
        print("缺少文件（先编译 LYJ.csproj）：")
        for m in missing:
            print("  " + m)
        return 1

    if os.path.exists(ZIP):
        os.remove(ZIP)
    with zipfile.ZipFile(ZIP, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for src, arc in items:
            z.write(src, arc)

    with zipfile.ZipFile(ZIP) as z:
        print("payload.zip = %d 字节, %d 条" % (os.path.getsize(ZIP), len(z.infolist())))
        for i in z.infolist():
            print("  %8d  %s" % (i.file_size, i.filename))
    return 0


if __name__ == "__main__":
    sys.exit(main())
