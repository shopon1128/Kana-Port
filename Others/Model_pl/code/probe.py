"""Blender 実行環境の確認用スクリプト。numpy / bmesh / FBX エクスポータの有無を報告する。"""
import sys

import bpy

print("PROBE_BLENDER_VERSION", bpy.app.version_string)
print("PROBE_PYTHON", sys.version.split()[0])

try:
    import numpy as np

    print("PROBE_NUMPY", np.__version__)
except ImportError as e:  # numpy が無いと顔テクスチャ生成ができない
    print("PROBE_NUMPY_MISSING", e)

try:
    import bmesh

    print("PROBE_BMESH", "ok")
except ImportError as e:
    print("PROBE_BMESH_MISSING", e)

print("PROBE_FBX", hasattr(bpy.ops.export_scene, "fbx"))
print("PROBE_ENGINES", [e.bl_idname for e in bpy.types.RenderEngine.__subclasses__()])
print("PROBE_DONE")
