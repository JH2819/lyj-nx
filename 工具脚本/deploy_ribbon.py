# -*- coding: utf-8 -*-
"""
功能区维护脚本（当前规则，2026-11-05）：
    LYJ  = 图档导入组 + 图档导出组
    NX-编程   = 只有一个「加工助手」按钮
                （工序按钮/策略下拉、「刷新编程模板」按钮及其工具组均已移除，
                  工序插入改由加工助手「模板」页双击完成）
所有功能区文件按 NX 期望的 GBK 无 BOM 写回（CRLF）。

用法：python 工具脚本\\deploy_ribbon.py        # 重排功能区 + 覆盖 DLL
"""
import os, shutil, datetime

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
ST = r"D:\LYJ\startup"
DLL = os.path.join(ROOT, u"源码", "LYJ", "bin", "Release", "LYJ.dll")

GBK = "gbk"
CRLF = "\r\n"

CAM_ASSISTANT = (
    "BUTTON LYJ_APP__cam_assistant\r\n"
    "LABEL 加工助手\r\n"
    "BITMAP lyj_cam_assistant\r\n"
    "RIBBON_STYLE ALWAYS_LARGE_IMAGE\r\n"
)


def wr(p, text):
    data = text.replace("\r\n", "\n").replace("\n", CRLF).encode(GBK)
    with open(p, "wb") as f:
        f.write(data)
    print("写出", os.path.basename(p), len(data), "字节")


def backup():
    dst = os.path.join(ROOT, "build", "backup",
                       "startup_" + datetime.datetime.now().strftime("%Y-%m-%d_%H%M"))
    os.makedirs(dst, exist_ok=True)
    for n in os.listdir(ST):
        if n.lower().endswith((".rtb", ".grb", ".men")):
            shutil.copy2(os.path.join(ST, n), os.path.join(dst, n))
    return dst


print("备份 ->", backup())

# 1. LYJ 选项卡：导入 / 导出 / 加工（加工助手）
wr(os.path.join(ST, "rbn_lyj.rtb"),
   "TITLE LYJ\r\n\r\nVERSION 170\r\n\r\n"
   "GROUP rbn_lyj_import.grb\r\n\r\n"
   "GROUP rbn_lyj_export.grb\r\n\r\n"
   "GROUP rbn_lyj_cam.grb\r\n")

wr(os.path.join(ST, "rbn_lyj_cam.grb"),
   "TITLE 加工\r\n\r\nVERSION 170\r\n\r\n" + CAM_ASSISTANT)

# 2. 清理已废弃的功能区/菜单文件（NX-编程 选项卡已并入 LYJ）
for old in ("rbn_nxprogramming.rtb", "rbn_nxprogramming_tools.grb",
            "rbn_nxprogramming_insert.grb", "gen_cam_strategies.men",
            "rbn_lyj_systools.grb", "rbn_lyj_mach.grb"):
    p = os.path.join(ST, old)
    if os.path.exists(p):
        os.remove(p)
        print("已删除", old)

# 4. 覆盖 DLL（NX 必须先关闭，否则文件被占用）
if os.path.exists(DLL):
    shutil.copy2(DLL, os.path.join(ST, "LYJ.dll"))
    print("已部署 LYJ.dll", os.path.getsize(os.path.join(ST, "LYJ.dll")), "字节")
else:
    print("未找到 DLL：", DLL)
