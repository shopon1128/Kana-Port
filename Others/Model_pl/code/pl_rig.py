"""pl のアーマチュア（Unity Humanoid 互換）と頂点グループを作る。

Blender の自動ウェイト(ARMATURE_AUTO)はボーンからの距離で推定するため、
ボーンから離れた独立した塊がウェイト無しのまま残ることがある（LESSONS.md 参照）。
ここでは各パーツの生成側が渡してくるウェイト関数で明示的に割り当て、
書き出し前に「全頂点にウェイトが付いているか」をスクリプト内で検証する。

ボーン名は Unity の Humanoid 自動マッピングが拾いやすい標準名にしてある。
"""

import bpy

import pl_spec

# 左半身のボーン定義。(名前, 始点, 終点, 親)。右半身は x を反転して生成する。
# 作業座標（+Yが正面）ではキャラクターの左は -X 側なので、x は負で持つ。
# 書き出し時の180度回転でこれが +X 側へ移る（pl_spec.SidePrefix 参照）。
_LEFT_BONES = [
    ("LeftShoulder", (-0.030, 0.000, 0.556), (-0.105, 0.000, 0.556), "Chest"),
    ("LeftUpperArm", (-0.105, 0.000, 0.556), (-0.179, 0.004, 0.468), "LeftShoulder"),
    ("LeftLowerArm", (-0.179, 0.004, 0.468), (-0.252, 0.002, 0.381), "LeftUpperArm"),
    ("LeftHand", (-0.252, 0.002, 0.381), (-0.300, 0.002, 0.325), "LeftLowerArm"),
    ("LeftUpperLeg", (-0.058, 0.000, 0.330), (-0.058, 0.000, 0.170), "Hips"),
    ("LeftLowerLeg", (-0.058, 0.000, 0.170), (-0.058, 0.000, 0.062), "LeftUpperLeg"),
    ("LeftFoot", (-0.058, 0.000, 0.062), (-0.058, 0.078, 0.012), "LeftLowerLeg"),
]

# 体の中心軸のボーン。
_CENTER_BONES = [
    ("Hips", (0.000, 0.000, 0.344), (0.000, 0.000, 0.400), None),
    ("Spine", (0.000, 0.000, 0.400), (0.000, 0.000, 0.470), "Hips"),
    ("Chest", (0.000, 0.000, 0.470), (0.000, 0.000, 0.560), "Spine"),
    ("Neck", (0.000, 0.000, 0.560), (0.000, 0.000, 0.640), "Chest"),
    ("Head", (0.000, 0.000, 0.640), (0.000, 0.000, 0.920), "Neck"),
]


def _MirrorName(a_name):
    return a_name.replace("Left", "Right", 1)


def _Mirror(a_point):
    return (-a_point[0], a_point[1], a_point[2])


def BoneTable():
    """(名前, 始点, 終点, 親) のリストを、親が先に来る順で返す。

    座標はメッシュと同じ OrientForExport() を通してから返す。
    ここを通し忘れるとボーンだけ180度ずれる。
    """
    table = list(_CENTER_BONES)
    for name, head, tail, parent in _LEFT_BONES:
        table.append((name, head, tail, parent))
    for name, head, tail, parent in _LEFT_BONES:
        mirrored_parent = _MirrorName(parent) if parent and "Left" in parent else parent
        table.append((_MirrorName(name), _Mirror(head), _Mirror(tail), mirrored_parent))
    return [
        (
            name,
            pl_spec.OrientForExport(head),
            pl_spec.OrientForExport(tail),
            parent,
        )
        for (name, head, tail, parent) in table
    ]


def BoneNames():
    return [row[0] for row in BoneTable()]


def BuildArmature(a_name="pl_armature"):
    """アーマチュアオブジェクトを作って返す。"""
    armature = bpy.data.armatures.new(a_name)
    obj = bpy.data.objects.new(a_name, armature)
    bpy.context.collection.objects.link(obj)

    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.mode_set(mode="EDIT")

    created = {}
    for name, head, tail, parent in BoneTable():
        bone = armature.edit_bones.new(name)
        bone.head = head
        bone.tail = tail
        bone.use_connect = False
        if parent:
            bone.parent = created[parent]
        created[name] = bone

    bpy.ops.object.mode_set(mode="OBJECT")
    return obj


def ApplyWeights(a_obj, a_weight_func, a_valid_bones):
    """オブジェクトの全頂点に、ウェイト関数の結果を頂点グループとして割り当てる。

    存在しないボーン名が返ってきたら、静かに無視せず例外にする。
    黙って落とすとウェイト無し頂点になり、Unity 取り込み時まで気づけない。
    """
    groups = {}
    mesh = a_obj.data
    for vert in mesh.vertices:
        weights = a_weight_func(vert.co)
        total = sum(weights.values())
        if total <= 1e-6:
            raise ValueError(
                "%s: 頂点 %d にウェイトが割り当てられなかった" % (a_obj.name, vert.index)
            )
        for bone_name, weight in weights.items():
            if bone_name not in a_valid_bones:
                raise ValueError(
                    "%s: 未定義のボーン '%s' が指定された" % (a_obj.name, bone_name)
                )
            if bone_name not in groups:
                groups[bone_name] = a_obj.vertex_groups.new(name=bone_name)
            # 合計1に正規化してから入れる
            groups[bone_name].add([vert.index], weight / total, "REPLACE")


def VerifyWeights(a_obj, a_bone_names):
    """全頂点にウェイトが付いているかを検証し、問題点のリストを返す。

    Unity の「N vertices with no weight」警告を見るまで気づけない事態を防ぐため、
    書き出し前にスクリプト内で機械的に確認する。
    """
    problems = []
    mesh = a_obj.data
    index_to_name = {g.index: g.name for g in a_obj.vertex_groups}

    unweighted = []
    unknown_group = set()
    for vert in mesh.vertices:
        total = 0.0
        for g in vert.groups:
            name = index_to_name.get(g.group)
            if name not in a_bone_names:
                unknown_group.add(name)
                continue
            total += g.weight
        if total <= 1e-6:
            unweighted.append(vert.index)

    if unweighted:
        problems.append(
            "ウェイト無しの頂点が %d 個ある (例: %s)"
            % (len(unweighted), unweighted[:8])
        )
    if unknown_group:
        problems.append("ボーンに対応しない頂点グループ: %s" % sorted(unknown_group))

    missing = [n for n in a_bone_names if n not in index_to_name.values()]
    if missing:
        # ボーンはあるが誰も使っていない状態。致命的ではないので警告として残す。
        problems.append("どの頂点にも使われていないボーン: %s" % missing)

    return problems


def BindToArmature(a_mesh_obj, a_armature_obj):
    """既存の頂点グループを使って、メッシュをアーマチュアに紐づける。

    bpy.ops.object.parent_set(type='ARMATURE_NAME') 相当を、
    モディファイアと親子付けの直接操作で行う（コンテキスト依存を避けるため）。
    """
    a_mesh_obj.parent = a_armature_obj
    a_mesh_obj.matrix_parent_inverse = a_armature_obj.matrix_world.inverted()
    modifier = a_mesh_obj.modifiers.new(name="Armature", type="ARMATURE")
    modifier.object = a_armature_obj
    modifier.use_vertex_groups = True
    return modifier
