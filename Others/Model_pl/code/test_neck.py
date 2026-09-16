"""首元に何のパーツが写っているかを切り分ける確認用スクリプト。

髪と頭を隠した状態で首元を寄りでレンダリングし、あわせて
その領域に頂点を持つパーツ名を列挙する。
「見えている変な形が何なのか」を推測で当てにいかないためのもの。
"""

import os
import sys

import bpy

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import build_pl

OUT = sys.argv[-1] if "--" in sys.argv else "/tmp/pl_neck.png"

build_pl.ClearScene()
image = build_pl.BuildFaceImage()
mats = build_pl.MakeAllMaterials(image)
parts = build_pl.BuildAllParts(mats)

# 首元の判定領域。書き出し座標なので正面は -Y 側。
Z_LOW, Z_HIGH = 0.52, 0.68
hidden = ("pl_head", "pl_hair", "pl_bang", "pl_ears", "pl_tuft", "pl_hair_tie", "pl_side")

print("--- 首元(z %.2f〜%.2f)に頂点を持つパーツ ---" % (Z_LOW, Z_HIGH))
for obj, _weight in parts:
    front = [
        v
        for v in obj.data.vertices
        if Z_LOW < v.co.z < Z_HIGH and v.co.y < 0.0 and abs(v.co.x) < 0.10
    ]
    if front:
        ys = [v.co.y for v in front]
        zs = [v.co.z for v in front]
        print(
            "NECKPART %-16s 頂点%3d  y %.3f..%.3f  z %.3f..%.3f"
            % (obj.name, len(front), min(ys), max(ys), min(zs), max(zs))
        )
    if obj.name.startswith(hidden):
        obj.hide_render = True

build_pl.SetupPreviewScene()
for path in build_pl.RenderViews(
    os.path.dirname(OUT), [(os.path.basename(OUT)[3:-4], 0, 4, 0.60, 0.34)], 700, 700
):
    print("NECK_RENDER", path)
print("NECK_DONE")
