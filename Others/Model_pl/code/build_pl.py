"""pl モデルを生成し、検証して FBX とプレビュー画像を書き出す。

使い方:
  /Applications/Blender.app/Contents/MacOS/Blender -b --factory-startup \
      --python build_pl.py -- --out <FBX出力先> --preview <プレビュー出力ディレクトリ>

引数を省略した場合は Assets/Model/pl.fbx に書き出す。
"""

import argparse
import math
import os
import sys

import bpy

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import pl_face
import pl_mesh
import pl_rig
import pl_spec

PROJECT_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_FBX = os.path.join(PROJECT_ROOT, "Assets", "Model", "pl.fbx")
DEFAULT_TEXTURE = os.path.join(PROJECT_ROOT, "Assets", "Model", "pl_face.png")


# ---------------------------------------------------------------------------
# シーンとマテリアル
# ---------------------------------------------------------------------------


def ClearScene():
    """--factory-startup で残る初期オブジェクトを消す。"""
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    for block in (bpy.data.meshes, bpy.data.materials, bpy.data.images, bpy.data.armatures):
        for item in list(block):
            if item.users == 0:
                block.remove(item)


def MakeMaterial(a_name, a_color, a_roughness=0.62, a_specular=0.16):
    """単色のマテリアルを作る。色は sRGB 表記で受け取りリニアへ変換する。"""
    mat = bpy.data.materials.new(a_name)
    mat.use_nodes = True
    bsdf = mat.node_tree.nodes["Principled BSDF"]
    linear = pl_face.SrgbToLinear(a_color)
    bsdf.inputs["Base Color"].default_value = (linear[0], linear[1], linear[2], 1.0)
    bsdf.inputs["Roughness"].default_value = a_roughness
    bsdf.inputs["Specular"].default_value = a_specular
    return mat


def MakeFaceMaterial(a_name, a_image):
    """顔テクスチャを Base Color に繋いだマテリアルを作る。"""
    mat = bpy.data.materials.new(a_name)
    mat.use_nodes = True
    nodes = mat.node_tree.nodes
    bsdf = nodes["Principled BSDF"]
    bsdf.inputs["Roughness"].default_value = 0.70
    bsdf.inputs["Specular"].default_value = 0.10

    tex = nodes.new("ShaderNodeTexImage")
    tex.image = a_image
    tex.interpolation = "Cubic"
    tex.location = (-400, 200)
    mat.node_tree.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
    return mat


def BuildFaceImage():
    """顔テクスチャを生成し、Blender の画像データとして返す。"""
    z_range = pl_mesh.HeadZRange()
    rgba = pl_face.BuildFaceTexture(pl_mesh.FACE_X_PER_U, z_range)
    size = pl_spec.FACE_TEX_SIZE
    image = bpy.data.images.new("pl_face", width=size, height=size, alpha=True)
    image.colorspace_settings.name = "sRGB"
    image.pixels.foreach_set(rgba.ravel())
    image.pack()
    return image


def MakeAllMaterials(a_face_image):
    col = pl_spec.COLOR
    return {
        "face": MakeFaceMaterial("pl_face", a_face_image),
        # 素体の肌。顔テクスチャの地の色と同じ値を使う。
        # 一度これを消して顔マテリアルに統一したが、マテリアル数が変わって
        # Unity 側の割り当てがずれ、服が肌色になった。安易に増減させないこと。
        "skin": MakeMaterial("pl_skin", col["skin"]),
        "hair_top": MakeMaterial("pl_hair", col["hair_top"], 0.58),
        "hair_inner": MakeMaterial("pl_hair_inner", col["hair_inner"], 0.58),
        "coat": MakeMaterial("pl_coat", col["coat"], 0.75, 0.10),
        # 襟と袖口はコートと同色だとシルエットに溶けて形が読めないので、
        # 一段暗い色を当てて別パーツとして見せる。
        "coat_dark": MakeMaterial("pl_coat_dark", col["coat_dark"], 0.78, 0.08),
        "shirt": MakeMaterial("pl_shirt", col["shirt"], 0.70, 0.12),
        "skirt": MakeMaterial("pl_skirt", col["skirt"], 0.72, 0.10),
        "sock": MakeMaterial("pl_sock", col["sock"], 0.80, 0.06),
        "boot": MakeMaterial("pl_boot", col["boot"], 0.52, 0.22),
        "sole": MakeMaterial("pl_sole", col["boot_sole"], 0.60, 0.18),
        "bag": MakeMaterial("pl_bag", col["bag"], 0.50, 0.24),
        "buckle": MakeMaterial("pl_buckle", col["buckle"], 0.30, 0.60),
        "tie": MakeMaterial("pl_hair_tie", col["hair_tie"], 0.55),
    }


# ---------------------------------------------------------------------------
# 組み立て
# ---------------------------------------------------------------------------


def BuildAllParts(a_mats):
    """全パーツを作り、(オブジェクト, ウェイト関数) のリストを返す。"""
    parts = []

    head_obj, _z_range, head_weight = pl_mesh.BuildHead(a_mats["face"])
    parts.append((head_obj, head_weight))
    parts.append(pl_mesh.BuildEars(a_mats["skin"]))
    parts.append(pl_mesh.BuildNeck(a_mats["skin"]))

    parts.append(pl_mesh.BuildHairCap(a_mats["hair_top"], a_mats["hair_inner"]))
    # 前髪は1つのパーツ。毛束感は輪郭の切り込みではなく面の起伏で出す。
    parts.append(pl_mesh.BuildBangCurtain(a_mats["hair_top"]))
    parts.extend(pl_mesh.BuildSideTuft(a_mats["hair_top"], a_mats["tie"]))

    # 素体を先に作り、その上に服を着せる。
    parts.append(pl_mesh.BuildBody(a_mats["skin"]))
    parts.extend(pl_mesh.BuildBareArms(a_mats["skin"]))
    parts.extend(pl_mesh.BuildBareLegs(a_mats["skin"]))

    parts.append(pl_mesh.BuildShirt(a_mats["shirt"]))
    parts.append(pl_mesh.BuildCoat(a_mats["coat"]))
    parts.append(pl_mesh.BuildCollar(a_mats["coat_dark"]))
    parts.append(pl_mesh.BuildSkirt(a_mats["skirt"]))
    # 下着はシャツと同じ白マテリアルを共用する。
    # マテリアルを増やすと並びが変わり、Unity 側の割り当てがずれる。
    parts.append(pl_mesh.BuildUnderwear(a_mats["shirt"]))

    parts.extend(pl_mesh.BuildArms(a_mats["coat"], a_mats["coat_dark"], a_mats["skin"]))
    parts.extend(pl_mesh.BuildLegs(a_mats["sock"], a_mats["boot"], a_mats["sole"]))
    parts.extend(pl_mesh.BuildBag(a_mats["bag"], a_mats["buckle"]))

    return parts


def JoinParts(a_objects, a_name="pl"):
    """複数オブジェクトを1つに結合する。頂点グループは名前で統合される。"""
    bpy.ops.object.select_all(action="DESELECT")
    for obj in a_objects:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = a_objects[0]
    bpy.ops.object.join()
    joined = bpy.context.view_layer.objects.active
    joined.name = a_name
    joined.data.name = a_name
    return joined


def BuildCharacter():
    """pl 一体を組み立てて (メッシュ, アーマチュア) を返す。"""
    face_image = BuildFaceImage()
    mats = MakeAllMaterials(face_image)

    parts = BuildAllParts(mats)
    bone_names = set(pl_rig.BoneNames())

    # 結合すると頂点インデックスが変わるので、ウェイトは結合前に付ける。
    for obj, weight_func in parts:
        pl_rig.ApplyWeights(obj, weight_func, bone_names)

    mesh_obj = JoinParts([obj for obj, _ in parts])
    armature_obj = pl_rig.BuildArmature()
    pl_rig.BindToArmature(mesh_obj, armature_obj)

    return mesh_obj, armature_obj, face_image


# ---------------------------------------------------------------------------
# プレビュー用のレンダリング
# ---------------------------------------------------------------------------


def SetupPreviewScene():
    """正面・側面などの確認用に、素直な三点照明とワールドを組む。"""
    scene = bpy.context.scene
    scene.render.engine = "BLENDER_EEVEE"
    scene.render.film_transparent = False
    scene.render.image_settings.file_format = "PNG"
    scene.eevee.taa_render_samples = 32
    scene.eevee.use_gtao = True

    world = bpy.data.worlds.new("preview")
    world.use_nodes = True
    world.node_tree.nodes["Background"].inputs["Color"].default_value = (0.72, 0.74, 0.78, 1.0)
    world.node_tree.nodes["Background"].inputs["Strength"].default_value = 0.85
    scene.world = world

    def AddLight(a_name, a_location, a_energy, a_size):
        data = bpy.data.lights.new(a_name, type="AREA")
        data.energy = a_energy
        data.size = a_size
        obj = bpy.data.objects.new(a_name, data)
        obj.location = a_location
        bpy.context.collection.objects.link(obj)
        # 常に原点付近を向かせる
        direction = (-obj.location[0], -obj.location[1], 0.65 - obj.location[2])
        obj.rotation_mode = "QUATERNION"
        obj.rotation_quaternion = _LookAtQuaternion(direction)
        return obj

    AddLight("key", (-1.4, -1.8, 1.9), 260.0, 2.2)
    AddLight("fill", (1.8, -1.2, 0.9), 110.0, 2.6)
    AddLight("rim", (0.6, 2.0, 1.6), 160.0, 2.0)


def _LookAtQuaternion(a_direction):
    from mathutils import Vector

    vec = Vector(a_direction)
    if vec.length < 1e-6:
        vec = Vector((0.0, 0.0, -1.0))
    return vec.to_track_quat("-Z", "Y")


def RenderViews(a_out_dir, a_views, a_width=560, a_height=840):
    """指定した角度からの正投影プレビューを書き出す。

    a_views の各要素は (名前, 方位角, 仰角) か、
    寄りで見たいときは (名前, 方位角, 仰角, 注視点の高さ, 画角の幅)。
    顔や前髪の作り込みは全身ビューでは判断できないので、寄りを用意しておく。
    """
    from mathutils import Vector

    os.makedirs(a_out_dir, exist_ok=True)
    scene = bpy.context.scene
    scene.render.resolution_x = a_width
    scene.render.resolution_y = a_height

    cam_data = bpy.data.cameras.new("preview_cam")
    cam_data.type = "ORTHO"
    cam_obj = bpy.data.objects.new("preview_cam", cam_data)
    bpy.context.collection.objects.link(cam_obj)
    scene.camera = cam_obj

    distance = 3.0
    written = []
    for view in a_views:
        name, yaw_deg, pitch_deg = view[0], view[1], view[2]
        center_z = view[3] if len(view) > 3 else 0.62
        cam_data.ortho_scale = view[4] if len(view) > 4 else 1.45
        center = Vector((0.0, 0.0, center_z))
        yaw = math.radians(yaw_deg)
        pitch = math.radians(pitch_deg)
        offset = Vector(
            (
                math.sin(yaw) * math.cos(pitch),
                -math.cos(yaw) * math.cos(pitch),
                math.sin(pitch),
            )
        ) * distance
        cam_obj.location = center + offset
        cam_obj.rotation_mode = "QUATERNION"
        cam_obj.rotation_quaternion = (center - cam_obj.location).to_track_quat("-Z", "Y")

        path = os.path.join(a_out_dir, "pl_%s.png" % name)
        scene.render.filepath = path
        bpy.ops.render.render(write_still=True)
        written.append(path)
    return written


# ---------------------------------------------------------------------------
# 書き出し
# ---------------------------------------------------------------------------


def ExportFbx(a_path, a_mesh_obj, a_armature_obj):
    """Unity 向けの設定で FBX を書き出す。"""
    os.makedirs(os.path.dirname(a_path), exist_ok=True)
    bpy.ops.object.select_all(action="DESELECT")
    a_mesh_obj.select_set(True)
    a_armature_obj.select_set(True)
    bpy.context.view_layer.objects.active = a_armature_obj

    bpy.ops.export_scene.fbx(
        filepath=a_path,
        use_selection=True,
        apply_scale_options="FBX_SCALE_NONE",
        object_types={"ARMATURE", "MESH"},
        use_mesh_modifiers=False,
        mesh_smooth_type="FACE",
        add_leaf_bones=False,  # Unity で不要な末端ボーンが増えるのを防ぐ
        primary_bone_axis="Y",
        secondary_bone_axis="X",
        bake_anim=False,
        path_mode="COPY",
        embed_textures=True,
        axis_forward="-Z",
        axis_up="Y",
    )


def SaveFaceTexture(a_image, a_path):
    """顔テクスチャを Unity から直接参照できるよう PNG でも保存する。"""
    os.makedirs(os.path.dirname(a_path), exist_ok=True)
    a_image.filepath_raw = a_path
    a_image.file_format = "PNG"
    a_image.save()


# ---------------------------------------------------------------------------
# エントリポイント
# ---------------------------------------------------------------------------


def ParseArgs():
    argv = sys.argv
    argv = argv[argv.index("--") + 1 :] if "--" in argv else []
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", default=DEFAULT_FBX, help="FBX の出力先")
    parser.add_argument("--texture", default=DEFAULT_TEXTURE, help="顔テクスチャの出力先")
    parser.add_argument("--preview", default="", help="プレビュー画像の出力ディレクトリ")
    parser.add_argument("--blend", default="", help="デバッグ用に .blend も保存する")
    parser.add_argument("--no-export", action="store_true", help="FBX を書き出さない")
    return parser.parse_args(argv)


def Main():
    args = ParseArgs()
    ClearScene()

    mesh_obj, armature_obj, face_image = BuildCharacter()

    stats_tris = sum(len(p.vertices) - 2 for p in mesh_obj.data.polygons)
    print("BUILD_VERTS", len(mesh_obj.data.vertices))
    print("BUILD_TRIS", stats_tris)
    print("BUILD_MATERIALS", len(mesh_obj.data.materials))

    # LESSONS.md の教訓: Unity の警告を見るまで気づかないのを防ぐため、
    # 書き出し前にウェイトを機械的に検証する。
    problems = pl_rig.VerifyWeights(mesh_obj, set(pl_rig.BoneNames()))
    for problem in problems:
        print("VERIFY_PROBLEM", problem)
    if not problems:
        print("VERIFY_OK 全頂点にウェイトあり")

    if args.preview:
        SetupPreviewScene()
        views = [
            ("front", 0, 0),
            ("side", 90, 0),
            ("back", 180, 0),
            ("three_quarter", 35, 8),
            # 俯瞰。襟の内側やコートの内側が見えていないかの確認用。
            # 首まわりの穴は正面からは見えず、この角度で初めて分かる。
            ("top_down", 22, 48),
            # 顔と前髪の寄り。全身ビューでは毛束の良し悪しが判断できない。
            # あおり。首元や襟の内側が筒に見えていないかの確認用。
            ("under", 12, -34, 0.58, 0.95),
            ("face", 0, 6, 0.88, 0.70),
            ("face_angle", 32, 10, 0.88, 0.72),
        ]
        for path in RenderViews(args.preview, views):
            print("PREVIEW", path)

    if not args.no_export:
        SaveFaceTexture(face_image, args.texture)
        print("TEXTURE_SAVED", args.texture)
        ExportFbx(args.out, mesh_obj, armature_obj)
        print("FBX_SAVED", args.out)

    if args.blend:
        bpy.ops.wm.save_as_mainfile(filepath=args.blend)
        print("BLEND_SAVED", args.blend)

    print("BUILD_DONE")


if __name__ == "__main__":
    Main()
