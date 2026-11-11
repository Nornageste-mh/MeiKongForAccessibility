# -*- coding: utf-8 -*-
"""按 Framework 逐字节同步 L1 平台层 + L2 契约层（仅替换命名空间）。

用法：
    python tools/sync_l1.py            # 干跑，只报差异
    python tools/sync_l1.py --write    # 落盘

规则（Framework docs/无障碍补丁流水线.md §6.5）：
  · L1 platform/*.cs 逐字节复用，永不在逐作层改写 —— 只改 namespace；
  · L2 contract/A11yHost.cs 整份替换（只增不改）—— 只改 namespace；
  · 本脚本可重复执行，结果幂等。

实现要点：**按字节读写**，不做换行符 / BOM 转换 —— 否则「逐字节一致」这条
断言会被 CRLF↔LF 悄悄破坏（G33 的前车之鉴：哈希才是不说谎的证据）。
"""
import os
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(REPO, "mod", "src", "MeiKongA11y")
FW = os.environ.get("A11Y_FRAMEWORK_DIR", r"D:\DSHWorkSpace\Framework")

NS_FROM = b"A11yFramework"
NS_TO = b"MeiKongA11y"


def l1_files():
    """唯一真源：Framework tools/l1-files.txt。"""
    out = []
    with open(os.path.join(FW, "tools", "l1-files.txt"), encoding="utf-8-sig") as f:
        for line in f:
            line = line.strip()
            if line and not line.startswith("#"):
                out.append(line)
    return out


def convert(data):
    if NS_FROM not in data:
        raise SystemExit("源文件里没有预期的命名空间，拒绝改写")
    # 整体替换：`namespace A11yFramework` 与 `A11yFramework.` 两种用法都覆盖，
    # 但只在 token 边界上替换，避免误伤更长的标识符。
    return (
        data.replace(b"namespace " + NS_FROM, b"namespace " + NS_TO)
        .replace(b"using " + NS_FROM, b"using " + NS_TO)
        .replace(NS_FROM + b".", NS_TO + b".")
    )


def sync(pairs, write):
    changed = 0
    for src_rel, dst in pairs:
        src = os.path.join(FW, src_rel)
        with open(src, "rb") as f:
            want = convert(f.read())
        have = None
        if os.path.exists(dst):
            with open(dst, "rb") as f:
                have = f.read()
        if have == want:
            print("  =  %-14s 与 Framework 一致（逐字节）" % os.path.basename(dst))
            continue
        changed += 1
        print("  *  %-14s %s" % (os.path.basename(dst), "已写入" if write else "需替换"))
        if write:
            with open(dst, "wb") as f:
                f.write(want)
    return changed


def main():
    write = "--write" in sys.argv
    if not os.path.isdir(FW):
        raise SystemExit("找不到 Framework：%s（可用 A11Y_FRAMEWORK_DIR 覆盖）" % FW)
    pairs = [(os.path.join("platform", n), os.path.join(SRC, n)) for n in l1_files()]
    pairs.append((os.path.join("contract", "A11yHost.cs"), os.path.join(SRC, "A11yHost.cs")))
    print("=== L1/L2 同步（Framework HEAD -> 逐作仓）===")
    n = sync(pairs, write)
    print("=== %d 个文件%s ===" % (n, "已同步" if write else "待同步（加 --write 落盘）"))
    return 0


if __name__ == "__main__":
    sys.exit(main())
