"""コートの実際の外表面半径を測り、仕様値(COAT_RINGS)とのズレを報告する。

solidify の厚みが内側へ付くのか外側へ付くのかを実測で確かめるための確認用スクリプト。
"""

import math
import os
import sys

import bpy

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import pl_mesh
import pl_spec

bpy.ops.object.select_all(action="SELECT")
bpy.ops.object.delete(use_global=False)

mat = bpy.data.materials.new("tmp")
obj, _weight = pl_mesh.BuildCoat(mat)

# _Finish で180度回っているので、半径の比較には影響しない（xyの符号のみ反転）。
for z_probe in (0.340, 0.400, 0.460, 0.520):
    best = 0.0
    for vert in obj.data.vertices:
        if abs(vert.co.z - z_probe) < 0.006:
            # 正面付近(|x| が小さい)の頂点で y 方向の最大値を見る
            if abs(vert.co.x) < 0.02:
                best = max(best, abs(vert.co.y))
    spec_ry = pl_mesh.CoatRadiusAt(z_probe)[1]
    print(
        "COAT_PROBE z=%.3f 実測ry=%.4f 仕様ry=%.4f 差=%+.4f"
        % (z_probe, best, spec_ry, best - spec_ry)
    )

print("COAT_THICKNESS_SPEC", pl_spec.COAT_THICKNESS)
print("PROBE_DONE")
