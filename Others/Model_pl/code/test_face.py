"""顔テクスチャだけを生成してPNGに保存する確認用スクリプト。

使い方:
  Blender -b --factory-startup --python test_face.py -- <出力パス>
"""

import math
import os
import sys

import bpy

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import pl_face
import pl_spec

OUT = sys.argv[-1] if "--" in sys.argv else "/tmp/pl_face.png"

# pl_mesh と同じ換算式でテクスチャの実寸を求める。
rx = pl_spec.HEAD["radius_x"]
x_per_u = 2.0 * rx * math.sin(math.radians(pl_spec.FACE_UV_HALF_ANGLE)) / pl_spec.FACE_UV_SPAN
z_range = (pl_spec.LEVEL["chin"], pl_spec.LEVEL["skull_top"])

print("TEST_X_PER_U", x_per_u)
print("TEST_Z_RANGE", z_range)

rgba = pl_face.BuildFaceTexture(x_per_u, z_range)
size = pl_spec.FACE_TEX_SIZE

img = bpy.data.images.new("pl_face_test", width=size, height=size, alpha=True)
img.colorspace_settings.name = "sRGB"
img.pixels.foreach_set(rgba.ravel())
img.filepath_raw = OUT
img.file_format = "PNG"
img.save()
print("TEST_SAVED", OUT)
