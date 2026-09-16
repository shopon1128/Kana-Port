"""pl のメッシュを bmesh で手続き的に生成する。

各パーツは「リング（同じ頂点数の閉ループ）を積み上げて橋渡しする」ロフトで作る。
断面をデータで持てるのでシルエットを pl_spec 側から直接コントロールでき、
球や立方体を変形させる方式より結果が予測しやすい。

各ビルド関数は (オブジェクト, ウェイト関数) を返す。ウェイト関数は頂点座標から
{ボーン名: 重み} を返し、pl_rig 側がこれを使って頂点グループを明示的に作る。
Blender の自動ウェイトに任せると、離れた塊（胸・ひさし等）が拾われず
ウェイト無し頂点が出る（LESSONS.md 参照）ため、生成時に自分で決める。
"""

import math

import bmesh
import bpy

import pl_spec

TAU = math.pi * 2.0


# ---------------------------------------------------------------------------
# 汎用のメッシュ組み立て
# ---------------------------------------------------------------------------


def _NewMesh():
    return bmesh.new()


def _Finish(a_bm, a_name, a_materials, a_uv=None):
    """bmesh をオブジェクト化する。法線を再計算し、スムーズシェードにする。

    ここで作業座標(+Yが正面)から書き出し座標(-Yが正面)へ回す。
    全パーツがこの関数を通るので、変換の掛け忘れが起きない。
    UVは呼び出し側で既に張り終えているため、この回転の影響を受けない。

    a_uv を渡すと、全ループをその1点に張る。顔と同じテクスチャの無地の肌を
    参照させ、素体の肌色を顔と完全に一致させるために使う。
    """
    bmesh.ops.recalc_face_normals(a_bm, faces=a_bm.faces)
    if a_uv is not None:
        uv_layer = a_bm.loops.layers.uv.new("UVMap")
        for face in a_bm.faces:
            for loop in face.loops:
                loop[uv_layer].uv = a_uv
    for vert in a_bm.verts:
        vert.co = pl_spec.OrientForExport(vert.co)
    mesh = bpy.data.meshes.new(a_name)
    a_bm.to_mesh(mesh)
    a_bm.free()
    obj = bpy.data.objects.new(a_name, mesh)
    bpy.context.collection.objects.link(obj)
    for mat in a_materials:
        mesh.materials.append(mat)
    for poly in mesh.polygons:
        poly.use_smooth = True
    return obj


def Ring(a_cx, a_cy, a_cz, a_rx, a_ry, a_segments, a_phase=0.0):
    """z一定の楕円リングを返す。角度は正面(+Y)を0とし、+X（本人の左）へ進む。"""
    pts = []
    for i in range(a_segments):
        th = a_phase + TAU * i / a_segments
        pts.append((a_cx + a_rx * math.sin(th), a_cy + a_ry * math.cos(th), a_cz))
    return pts


def LoftRings(a_bm, a_rings, a_closed=True, a_cap_bottom=False, a_cap_top=False):
    """リング列を順に橋渡しして面を張る。全リングは同じ頂点数である必要がある。"""
    n = len(a_rings[0])
    for ring in a_rings:
        if len(ring) != n:
            raise ValueError("LoftRings: リングの頂点数が揃っていない")
    verts = [[a_bm.verts.new(p) for p in ring] for ring in a_rings]
    span = n if a_closed else n - 1
    for i in range(len(a_rings) - 1):
        for j in range(span):
            k = (j + 1) % n
            a_bm.faces.new((verts[i][j], verts[i][k], verts[i + 1][k], verts[i + 1][j]))
    if a_cap_bottom:
        a_bm.faces.new(verts[0])
    if a_cap_top:
        a_bm.faces.new(verts[-1])
    return verts


def TubeAlongPath(a_bm, a_path, a_radii, a_segments=10, a_cap=True):
    """折れ線に沿った円形断面のチューブを張る。毛束や紐に使う。"""
    if len(a_path) != len(a_radii):
        raise ValueError("TubeAlongPath: 点数と半径数が一致しない")
    rings = []
    for i, (px, py, pz) in enumerate(a_path):
        # 進行方向を求め、それに直交する2軸で断面を作る
        if i == 0:
            d = _Sub(a_path[1], a_path[0])
        elif i == len(a_path) - 1:
            d = _Sub(a_path[-1], a_path[-2])
        else:
            d = _Sub(a_path[i + 1], a_path[i - 1])
        d = _Normalize(d)
        up = (0.0, 0.0, 1.0) if abs(d[2]) < 0.9 else (1.0, 0.0, 0.0)
        side = _Normalize(_Cross(d, up))
        up2 = _Cross(side, d)
        r = a_radii[i]
        ring = []
        for j in range(a_segments):
            th = TAU * j / a_segments
            c, s = math.cos(th), math.sin(th)
            ring.append(
                (
                    px + r * (side[0] * c + up2[0] * s),
                    py + r * (side[1] * c + up2[1] * s),
                    pz + r * (side[2] * c + up2[2] * s),
                )
            )
        rings.append(ring)
    return LoftRings(a_bm, rings, True, a_cap, a_cap)


def BandAlongPath(a_bm, a_path, a_half_width, a_half_thickness, a_cap=True):
    """折れ線に沿った「平たいベルト」を張る。

    円形断面のチューブだと、鞄の紐が丸い棒に見えて体から浮く。
    断面を長方形にし、厚みの向きを体の中心軸からの半径方向に合わせることで、
    体の表面に貼り付いた帯として見せる。
    """
    rings = []
    for i, (px, py, pz) in enumerate(a_path):
        if i == 0:
            d = _Sub(a_path[1], a_path[0])
        elif i == len(a_path) - 1:
            d = _Sub(a_path[-1], a_path[-2])
        else:
            d = _Sub(a_path[i + 1], a_path[i - 1])
        d = _Normalize(d)
        # 体の軸(z)から見た半径方向。これを帯の「厚み」の向きにする。
        radial = _Normalize((px, py, 0.0))
        width_dir = _Normalize(_Cross(radial, d))
        ring = []
        for sw, st in ((1, 1), (1, -1), (-1, -1), (-1, 1)):
            ring.append(
                tuple(
                    p
                    + width_dir[k] * a_half_width * sw
                    + radial[k] * a_half_thickness * st
                    for k, p in enumerate((px, py, pz))
                )
            )
        rings.append(ring)
    return LoftRings(a_bm, rings, True, a_cap, a_cap)


def CoatRadiusAt(a_z):
    """その高さでのコートの断面半径(rx, ry)。COAT_RINGS を線形補間する。"""
    rings = pl_spec.COAT_RINGS
    if a_z <= rings[0][0]:
        return (rings[0][1], rings[0][2])
    if a_z >= rings[-1][0]:
        return (rings[-1][1], rings[-1][2])
    for i in range(len(rings) - 1):
        z0, rx0, ry0, _ = rings[i]
        z1, rx1, ry1, _ = rings[i + 1]
        if z0 <= a_z <= z1:
            t = (a_z - z0) / max(z1 - z0, 1e-9)
            return (_Lerp(rx0, rx1, t), _Lerp(ry0, ry1, t))
    return (rings[-1][1], rings[-1][2])


def CoatSurface(a_theta_deg, a_z, a_offset=0.0):
    """コートの外表面の1点。角度は正面0、+X側が正。a_offset だけ外へ押し出す。

    鞄や紐の位置をコートの寸法と二重に持たずに済ませるための関数。
    COAT_RINGS を変えても、乗っているものが自動で追従する。

    注意: BuildCoat の bmesh.ops.solidify(thickness=負) は **外側** に厚みを付ける。
    そのため実際の外表面は COAT_RINGS の半径より COAT_THICKNESS だけ外にある
    （test_coat_radius.py で実測確認済み）。ここを足し忘れると、
    乗せたものがコートに埋まって見えなくなる。
    """
    th = math.radians(a_theta_deg)
    rx, ry = CoatRadiusAt(a_z)
    grow = pl_spec.COAT_THICKNESS + a_offset
    return ((rx + grow) * math.sin(th), (ry + grow) * math.cos(th), a_z)


def _Sub(a, b):
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])


def _Cross(a, b):
    return (
        a[1] * b[2] - a[2] * b[1],
        a[2] * b[0] - a[0] * b[2],
        a[0] * b[1] - a[1] * b[0],
    )


def _Normalize(a):
    length = math.sqrt(a[0] ** 2 + a[1] ** 2 + a[2] ** 2)
    if length < 1e-9:
        return (0.0, 0.0, 1.0)
    return (a[0] / length, a[1] / length, a[2] / length)


def _Lerp(a, b, t):
    return a + (b - a) * t


def _SmoothStep(a_edge0, a_edge1, a_x):
    """a_edge0→a_edge1 の範囲で 0→1 に滑らかに遷移させる。

    a_edge1 < a_edge0（降順）も許す。「高いところから下がるほど1に近づく」
    という書き方をしたい場面が多いため。
    ゼロ除算対策で分母を max(..., 1e-9) とすると降順のとき符号ごと潰れ、
    常に0を返す静かな不具合になる（髪の開口部が閉じた実例あり）。
    絶対値でクランプして符号を保つこと。
    """
    denom = a_edge1 - a_edge0
    if abs(denom) < 1e-9:
        denom = 1e-9 if denom >= 0.0 else -1e-9
    t = min(1.0, max(0.0, (a_x - a_edge0) / denom))
    return t * t * (3.0 - 2.0 * t)


# ---------------------------------------------------------------------------
# 頭部の形状関数
# ---------------------------------------------------------------------------


def HeadChinTaper(a_z0):
    """頭蓋の高さパラメータ(-1=あご .. +1=頭頂)に対する、あご絞りの倍率。"""
    spec = pl_spec.HEAD
    start = spec["chin_start"]
    if a_z0 >= start:
        return 1.0
    t = (start - a_z0) / (start + 1.0)
    return 1.0 - spec["chin_taper"] * (t**1.6)


def HeadSurface(a_z0, a_theta):
    """頭蓋表面の1点を返す。

    a_z0    : -1(あご先) 〜 +1(頭頂)
    a_theta : 正面が0で、+X（本人の左）方向へ増える角度(ラジアン)
    """
    spec = pl_spec.HEAD
    r0 = math.sqrt(max(0.0, 1.0 - a_z0 * a_z0))
    z = spec["center_z"] + spec["radius_z"] * a_z0
    taper = HeadChinTaper(a_z0)

    sx = math.sin(a_theta)
    cy = math.cos(a_theta)

    # 顔の前面を立てる。
    # 球のままだと、赤道(目のあたり)から下へ行くほど r0 が小さくなって
    # 面が奥へ引っ込み、顔全体が下を向いて見える。
    # 目からあご手前までは半径を保ち、あご先の直前だけ一気にすぼめることで、
    # 「正面が垂直な面、あごだけ丸く収まる」アニメ的な顔になる。
    if a_z0 < 0.0:
        zone = _SmoothStep(-1.0, spec["face_upright_end"], a_z0)
        r0 = r0 + spec["face_upright"] * (1.0 - r0) * zone

    x = spec["radius_x"] * r0 * sx * taper
    # 正面の平坦化。cy の多項式なので側面で折れ目が出ない。
    y = spec["radius_y"] * r0 * cy * (1.0 + spec["face_flatten"] * (1.0 - cy * cy)) * taper
    # 後頭部だけ膨らませる。cy^2 なので側面で滑らかに0になる。
    if cy < 0.0:
        y -= spec["back_bulge"] * r0 * cy * cy

    # 頬の張り。あご絞りで細くなりすぎた頬を横に戻す。
    g = math.exp(-(((z - spec["cheek_z"]) / spec["cheek_falloff"]) ** 2))
    x += spec["cheek_bulge"] * g * sx * r0

    return (x, y, z)


def HeadHalfWidth(a_z):
    """指定した高さでの頭蓋の横半径。髪をぴったり被せるために使う。"""
    spec = pl_spec.HEAD
    z0 = (a_z - spec["center_z"]) / spec["radius_z"]
    z0 = max(-1.0, min(1.0, z0))
    return abs(HeadSurface(z0, math.pi * 0.5)[0])


def HeadHalfDepth(a_z):
    """指定した高さでの頭蓋の前後半径（後頭部側）。"""
    spec = pl_spec.HEAD
    z0 = (a_z - spec["center_z"]) / spec["radius_z"]
    z0 = max(-1.0, min(1.0, z0))
    return abs(HeadSurface(z0, math.pi)[1])


# 顔テクスチャの横方向スケール。u はワールドXに対して線形に張る。
FACE_X_PER_U = (
    2.0
    * pl_spec.HEAD["radius_x"]
    * math.sin(math.radians(pl_spec.FACE_UV_HALF_ANGLE))
    / pl_spec.FACE_UV_SPAN
)

# 背面の面をまとめて逃がす、テクスチャ上の「無地の肌」座標。
PLAIN_SKIN_UV = (0.5, 0.97)


def HeadZRange():
    """頭蓋メッシュのz範囲。顔テクスチャのv座標の基準になる。

    形状変形はzを動かさないので、メッシュを作らずに解析的に求まる。
    テクスチャ生成側とメッシュ側で同じ値を使うため、必ずここを経由する。
    """
    spec = pl_spec.HEAD
    return (spec["center_z"] - spec["radius_z"], spec["center_z"] + spec["radius_z"])


def BuildHead(a_material):
    """頭部を作り、顔テクスチャ用のUVを張る。"""
    spec = pl_spec.HEAD
    segs = spec["segments"]
    rings_n = spec["rings"]

    bm = _NewMesh()

    # 極を除いたリングを phi 一様で作る（極付近の頂点密度を素直にするため）。
    ring_verts = []
    ring_theta = []
    for i in range(1, rings_n):
        phi = math.pi * i / rings_n
        z0 = -math.cos(phi)
        row = []
        thetas = []
        for j in range(segs):
            th = TAU * j / segs
            # theta を (-pi, pi] に直す。0 が正面。
            th_signed = th if th <= math.pi else th - TAU
            row.append(bm.verts.new(HeadSurface(z0, th)))
            thetas.append(th_signed)
        ring_verts.append(row)
        ring_theta.append(thetas)

    bottom = bm.verts.new(HeadSurface(-1.0, 0.0))
    top = bm.verts.new(HeadSurface(1.0, 0.0))

    faces = []
    for i in range(len(ring_verts) - 1):
        for j in range(segs):
            k = (j + 1) % segs
            f = bm.faces.new(
                (
                    ring_verts[i][j],
                    ring_verts[i][k],
                    ring_verts[i + 1][k],
                    ring_verts[i + 1][j],
                )
            )
            faces.append((f, [ring_theta[i][j], ring_theta[i][k], ring_theta[i + 1][k], ring_theta[i + 1][j]]))
    # 極の三角ファン
    for j in range(segs):
        k = (j + 1) % segs
        f = bm.faces.new((bottom, ring_verts[0][k], ring_verts[0][j]))
        faces.append((f, [0.0, ring_theta[0][k], ring_theta[0][j]]))
        f = bm.faces.new((top, ring_verts[-1][j], ring_verts[-1][k]))
        faces.append((f, [0.0, ring_theta[-1][j], ring_theta[-1][k]]))

    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)

    # --- UV ---
    # 顔の正面は「作業座標Xの平行投影」で張る。こうすると顔テクスチャが
    # そのまま正面図として乗り、あご付近で横に伸びない。
    # 背面の面は無地の肌1点へ逃がす。頭の後ろは髪で隠れるうえ、
    # テクスチャの端も無地の肌なので継ぎ目が見えない。
    #
    # 注意: 書き出し時の180度回転でXの符号が反転するため、テクスチャは
    # 完成モデル上では左右反転して見える。今の顔は左右対称なので問題ないが、
    # ほくろや眼帯など非対称なものを描くときは、この反転を織り込むこと。
    z_min, z_max = HeadZRange()
    uv_layer = bm.loops.layers.uv.new("UVMap")
    for f, thetas in faces:
        front = (sum(abs(t) for t in thetas) / len(thetas)) <= math.radians(92.0)
        for loop in f.loops:
            if front:
                u = 0.5 + loop.vert.co.x / FACE_X_PER_U
                v = (loop.vert.co.z - z_min) / (z_max - z_min)
                loop[uv_layer].uv = (u, v)
            else:
                loop[uv_layer].uv = PLAIN_SKIN_UV

    obj = _Finish(bm, "pl_head", [a_material])
    return obj, (z_min, z_max), lambda co: {"Head": 1.0}


def BuildNeck(a_material):
    """首。あごの下から襟の中まで通す。

    これが無いと、襟の内側とコートの内側が真上から丸見えになり、
    頭が胴から浮いているように見える。上端はあごに、下端は襟の中に埋める。
    """
    spec = pl_spec.NECK
    bm = _NewMesh()
    rings = []
    steps = 5
    for i in range(steps):
        t = i / (steps - 1)
        z = _Lerp(spec["bottom_z"], spec["top_z"], t)
        # 上ほどわずかに細く。あごの丸みへ自然に入る。
        r = spec["radius"] * (1.0 - 0.12 * t)
        rings.append(Ring(0.0, 0.0, z, r, r * 0.92, 16))
    LoftRings(bm, rings, True, True, True)
    obj = _Finish(bm, "pl_neck", [a_material])
    # 首は頭と胴の中間。Neck ボーンに預け、上端だけ Head に渡す。
    def Weight(a_co):
        t = _SmoothStep(pl_spec.LEVEL["collar"], pl_spec.LEVEL["chin"], a_co.z)
        return {"Neck": 1.0 - t, "Head": t}

    return obj, Weight


def BuildEars(a_material):
    """耳。髪の隙間から覗く、縦長で薄い塊。

    横(x)方向に積んだリングを並べ、外へ行くほど細くすることで
    「頭から張り出した薄い耳たぶ」の形にする。
    """
    spec = pl_spec.EAR
    bm = _NewMesh()
    steps = 7
    for side in (-1, 1):
        base_x = spec["offset_x"] * side
        rings = []
        for i in range(steps):
            t = i / (steps - 1)
            # 付け根(t=0)と先端(t=1)で断面を潰し、中ほどを最も厚くする
            amp = math.sin(math.pi * (0.10 + 0.80 * t))
            x = base_x + spec["thickness"] * side * t
            ring = []
            for j in range(12):
                th = TAU * j / 12
                ring.append(
                    (
                        x,
                        spec["offset_y"] + spec["radius_y"] * math.sin(th) * amp,
                        spec["z"] + spec["radius_z"] * math.cos(th) * amp,
                    )
                )
            rings.append(ring)
        LoftRings(bm, rings, True, True, True)
    obj = _Finish(bm, "pl_ears", [a_material])
    return obj, lambda co: {"Head": 1.0}


# ---------------------------------------------------------------------------
# 髪
# ---------------------------------------------------------------------------


def HairShellAxes():
    """髪のシェルを表す楕円体の半径(x, y, z)。頭蓋を一回り大きくしたもの。"""
    head = pl_spec.HEAD
    shell = pl_spec.HAIR["shell"]
    return (
        head["radius_x"] + shell,
        head["radius_y"] + shell,
        head["radius_z"] + shell,
    )


def HairTopZ():
    """髪のシェルの頂点の高さ。pl_spec.LEVEL["hair_top"] と一致する。"""
    return pl_spec.HEAD["center_z"] + HairShellAxes()[2]


def _HairShellRadius(a_z):
    """その高さでの髪の外側半径(x, y)。

    「その高さの頭蓋の半径 ＋ 一定の浮き量」で求めてはいけない。
    頭頂では頭蓋の半径が0になるため、髪の半径が浮き量だけになって円錐状に尖る。
    正しくは **頭蓋を一回り大きくした楕円体** の、その高さでの半径を使う。

    頭蓋の最大幅より下では、ボブは頭に沿わず真下へ落ちるので半径を一定に保つ。
    """
    rx, ry, rz = HairShellAxes()
    z0 = (a_z - pl_spec.HEAD["center_z"]) / rz
    if z0 >= 1.0:
        return (0.0, 0.0)
    if z0 <= 0.0:
        return (rx, ry)
    k = math.sqrt(max(0.0, 1.0 - z0 * z0))
    return (rx * k, ry * k)


def _FaceOpening(a_theta, a_z):
    """髪本体（ボブ）に穴を開ける領域の内側なら True。

    この穴は前髪(BuildBangCurtain)がちょうど埋める。したがって穴の左右の広さは
    前髪の広さと同じ pl_spec.FACE_OPEN_HALF_DEG を使い、**ずらしてはいけない**。
    前髪のほうが広いと、前髪がサイドの髪の上に乗って別レイヤーに見える
    （Unityで「前髪がサイドの前に来ている」と指摘された状態）。

    高さは生え際で水平に切るだけにする。ここを高さに応じて滑らかに絞ると、
    前髪との重なり幅が高さごとに変わり、帯状の段差として見えてしまう。
    """
    if a_z >= pl_spec.HAIR["bang_top_z"]:
        return False
    return abs(a_theta) < math.radians(pl_spec.FACE_OPEN_HALF_DEG)


def BuildHairCap(a_mat_top, a_mat_inner):
    """ボブの本体。頭を覆い、あごより下まで下ろして裾をギザギザにする。"""
    spec = pl_spec.HAIR
    segs = spec["segments"]
    top_z = HairTopZ()
    bottom_z = spec["bob_bottom_z"]

    bm = _NewMesh()

    # 高さ方向のサンプル。t=0 は頂点(半径0)になり面が潰れるので、そこは
    # 三角ファンで塞ぐことにして、リング自体は少し下から始める。
    levels = []
    rings_n = spec["rings"]
    for i in range(1, rings_n + 1):
        t = i / rings_n
        # ドーム部分の曲率が急な上側を密にサンプルする
        levels.append(top_z - (top_z - bottom_z) * (t**1.45))

    rings = []
    widest_z = pl_spec.HEAD["center_z"]
    for z in levels:
        rx, ry = _HairShellRadius(z)
        # 頭のいちばん広いところから下は、裾へ向かって広がる。
        # 参考画像のボブは「下ほど広いベル型」で、ここが髪の量感を決める。
        if z < widest_z:
            t = (widest_z - z) / max(widest_z - bottom_z, 1e-9)
            flare = spec["hem_flare"] * (t**1.3)
            rx += flare
            ry += flare * 0.75
        ring = []
        for j in range(segs):
            th = TAU * j / segs
            th_signed = th if th <= math.pi else th - TAU
            ring.append((rx * math.sin(th), ry * math.cos(th), z, th_signed))
        rings.append(ring)

    # 裾のギザギザ。角度に応じて最下段の高さを上下させる。
    jag_n = spec["hem_jag_count"]
    jag_d = spec["hem_jag_depth"]
    last = rings[-1]
    for j in range(segs):
        th = TAU * j / segs
        # 三角波で毛束の先端を作る
        w = abs(((th * jag_n / TAU) % 1.0) * 2.0 - 1.0)
        x, y, z, ts = last[j]
        last[j] = (x, y, z + jag_d * (1.0 - w), ts)

    # 頂点を作りつつ、顔の開口部にかかる面は張らない。
    verts = [[bm.verts.new((p[0], p[1], p[2])) for p in ring] for ring in rings]
    for i in range(len(rings) - 1):
        for j in range(segs):
            k = (j + 1) % segs
            quad = [rings[i][j], rings[i][k], rings[i + 1][k], rings[i + 1][j]]
            th_mid = sum(abs(p[3]) for p in quad) / 4.0
            z_mid = sum(p[2] for p in quad) / 4.0
            if _FaceOpening(th_mid, z_mid):
                continue
            bm.faces.new((verts[i][j], verts[i][k], verts[i + 1][k], verts[i + 1][j]))

    # 頭頂のふさぎ
    fan_center = bm.verts.new((0.0, 0.0, top_z))
    for j in range(segs):
        k = (j + 1) % segs
        bm.faces.new((fan_center, verts[0][j], verts[0][k]))

    # 使われなかった頂点を削除する（開口部のふち）
    loose = [v for v in bm.verts if not v.link_faces]
    if loose:
        bmesh.ops.delete(bm, geom=loose, context="VERTS")

    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)

    # 髪に厚みを付ける。内側から見ても面が消えないようにする。
    bmesh.ops.solidify(bm, geom=list(bm.faces), thickness=-0.012)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)

    obj = _Finish(bm, "pl_hair_cap", [a_mat_top, a_mat_inner])
    _AssignHairMaterialByHeight(obj)
    return obj, lambda co: {"Head": 1.0}


def _AssignHairMaterialByHeight(a_obj):
    """インナーカラー（藤色）を、境界高さより下の面に割り当てる。"""
    boundary = pl_spec.HAIR["inner_color_z"]
    mesh = a_obj.data
    for poly in mesh.polygons:
        center_z = sum(mesh.vertices[i].co.z for i in poly.vertices) / len(poly.vertices)
        poly.material_index = 1 if center_z < boundary else 0


def ArcRing(a_th_center, a_th_half, a_z, a_r_outer, a_r_inner, a_segments=7, a_squash=1.0):
    """頭を囲む円弧状の断面（外側の弧＋内側の弧の閉ループ）を返す。毛束に使う。

    a_squash は前後(y)方向の潰し。頭の楕円に沿わせるために使う。
    """
    loop = []
    for i in range(a_segments):
        t = i / (a_segments - 1)
        th = a_th_center - a_th_half + 2.0 * a_th_half * t
        loop.append((a_r_outer * math.sin(th), a_r_outer * math.cos(th) * a_squash, a_z))
    for i in range(a_segments):
        t = 1.0 - i / (a_segments - 1)
        th = a_th_center - a_th_half + 2.0 * a_th_half * t
        loop.append((a_r_inner * math.sin(th), a_r_inner * math.cos(th) * a_squash, a_z))
    return loop


def _BangBottomZ(a_theta):
    """前髪の毛先の高さを角度から返す。

    中央がいちばん短く、端へ向かうほど長い（参考画像の中央分けの形）。
    そこへ三角波を重ねて毛先を尖らせる。左右対称にするため
    角度の絶対値で波を作る。
    """
    spec = pl_spec.HAIR
    # 符号付きで持つ。左右非対称にするには絶対値だけでは足りない。
    sgn = _BangSignedT(a_theta)
    t = abs(sgn)
    front = spec["bang_front_ratio"]

    # 左右で流れる長さを変える。完全な左右対称は作り物めいて見える。
    # tanh なので中央で滑らかにつながり、折れ目ができない。
    drop = spec["bang_side_drop"] * (1.0 + spec["bang_side_asym"] * math.tanh(sgn * 3.0))
    # 前髪ブロックの端の高さ。ここからサイドブロックが始まる。
    front_edge = spec["bang_base_z"] - drop

    if t <= front:
        # 前髪ブロック: 額を覆う緩い弧
        s = t / max(front, 1e-9)
        base = spec["bang_base_z"] - drop * (s**1.25)
    else:
        # サイドブロック: 頬に沿って一気に下ろし、顔の輪郭を縁取る
        s = (t - front) / max(1.0 - front, 1e-9)
        base = _Lerp(front_edge, spec["side_block_bottom_z"], _SmoothStep(0.0, 1.0, s))

    # 分け目。中央ではなく片側(bang_part_at)へ寄せる。
    # ここだけ毛先を持ち上げると、そこで髪が分かれて左右へ流れて見える。
    width = max(spec["bang_part_width"], 1e-9)
    base += spec["bang_part_lift"] * math.exp(
        -(((sgn - spec["bang_part_at"]) / width) ** 2)
    )
    return base - spec["bang_tip_depth"] * _BangWave(a_theta)


def _BangSignedT(a_theta):
    """開口部の中での左右位置を -1(片側) 〜 +1(反対側) で返す。0が正面。"""
    th_max = math.radians(pl_spec.HAIR["bang_half_deg"])
    return max(-1.0, min(1.0, a_theta / th_max))


def _BangLift(a_z, a_curtain_top_z):
    """前髪を髪シェルからどれだけ浮かせるかを **高さ** から返す。

    縦パラメータ(t)を基準にしてはいけない。毛先の高さは角度で大きく違うため
    （前髪ブロックとサイドブロック）、同じ t でも高さがまるで違い、
    穴のふちの高さで浮きが足りずに継ぎ目が線として出る。

    3段階に分ける:
      穴より上  : 負（髪本体の内側に潜り、上端の切り口を隠す）
      穴のふち  : 最も浮かせる（本体の切り口を確実に覆う）
      それより下: ほぼ面一（横で隣接する本体との段差を作らない）
    """
    spec = pl_spec.HAIR
    top = spec["bang_top_z"]
    if a_z >= top:
        t = _SmoothStep(a_curtain_top_z, top, a_z)
        return _Lerp(spec["bang_sink"], spec["bang_cover"], t)
    t = _SmoothStep(top, top - spec["bang_cover_fade"], a_z)
    return _Lerp(spec["bang_cover"], spec["bang_extra"], t)


def _HairBlockT(a_theta):
    """髪の開口部の中での左右位置を 0(正面) 〜 1(開口部の縁) で返す。"""
    th_max = math.radians(pl_spec.HAIR["bang_half_deg"])
    return min(1.0, abs(a_theta) / th_max)


def _BangWave(a_theta):
    """毛束の位置を表す 0..1 の波。1が毛束の中心、0が毛束の境目。

    三角波（|frac*2-1|）を使うと折れ点がそのままシルエットの角になり、
    深い切り込みと組み合わさって「牙の列」に見える。
    余弦なら継ぎ目で傾きが0になるので、輪郭が滑らかにつながる。
    """
    spec = pl_spec.HAIR
    th_max = math.radians(spec["bang_half_deg"])
    t = min(1.0, abs(a_theta) / th_max)
    u = t * spec["bang_tips"]
    phase = u % 1.0
    dip = 0.5 - 0.5 * math.cos(TAU * phase)
    # 毛束ごとに深さを変える。全部同じだと機械的に見える。
    scales = spec["bang_tip_scale"]
    scale = scales[min(int(u), spec["bang_tips"] - 1) % len(scales)]
    return dip * scale


def _BangLobe(a_theta):
    """前髪の毛束のふくらみ（半径に足す量）を角度から返す。

    毛束感はシルエットを切り込むのではなく、**面の起伏** で出す。
    輪郭を深く刻むと毛束ではなく牙に見えるが、手前への張り出しなら
    陰影の差として読まれるので、1枚のパーツのまま毛の流れを表現できる。

    加えてサイドブロックを少しだけ前へ出す。資料「3つのブロックに分ける」のとおり、
    髪は前髪・サイド・後ろ髪の3ブロックで読ませる。同じ面の上にあっても、
    わずかな張り出しの差があればサイドが独立した毛束として見える。
    """
    spec = pl_spec.HAIR
    t = _HairBlockT(a_theta)
    front = spec["bang_front_ratio"]
    side_bulge = spec["side_block_bulge"] * _SmoothStep(front - 0.12, front + 0.12, t)
    return spec["bang_lobe"] * _BangWave(a_theta) + side_bulge


def BuildBangCurtain(a_material):
    """前髪。1枚の連続した面として作り、毛先の高さを角度で変える。

    毛束を独立した塊で並べると、隙間から額が見えたり、
    輪郭が階段状の「板の集合」に見えたりする。
    面を繋げたまま下端だけ動かすほうが、破綻せず毛先も尖らせられる。
    """
    spec = pl_spec.HAIR
    bm = _NewMesh()

    th_max = math.radians(spec["bang_half_deg"])
    # 髪本体の穴より上から始めて、ふちを内側から隠す。
    top_z = spec["bang_top_z"] + spec["bang_overlap"]
    th_steps = 40
    t_steps = 12

    grid = []
    for i in range(t_steps + 1):
        t = i / t_steps
        # 生え際では髪本体の **内側** に潜り込ませ、下るにつれて外へ出す。
        # 上端を外に出したままだと、前髪の取り付けの切り口が
        # 帯になって頭頂を一周して見える（Unityで確認した「前髪のとりつけがおかしい」）。
        # 潜り込ませれば、髪本体の下から自然に生え出しているように見える。
        row = []
        for j in range(th_steps + 1):
            th = -th_max + 2.0 * th_max * j / th_steps
            z = _Lerp(top_z, _BangBottomZ(th), t)
            lift = _BangLift(z, top_z)
            rx, ry = _HairShellRadius(z)
            # 毛束のふくらみ。平らな1枚のままだと板に見えるので、
            # 毛先が長い位置ほど手前へ張り出させて筋を作る。
            # 毛先(t=1)に向かって効かせ、生え際では効かせない。
            lobe = _BangLobe(th) * _SmoothStep(0.15, 0.85, t)
            rx += lift + lobe
            ry += lift + lobe
            row.append((rx * math.sin(th), ry * math.cos(th), z))
        grid.append(row)

    verts = [[bm.verts.new(p) for p in row] for row in grid]
    for i in range(t_steps):
        for j in range(th_steps):
            bm.faces.new(
                (verts[i][j], verts[i][j + 1], verts[i + 1][j + 1], verts[i + 1][j])
            )

    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    bmesh.ops.solidify(bm, geom=list(bm.faces), thickness=-spec["bang_thickness"])
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)

    obj = _Finish(bm, "pl_bangs", [a_material])
    return obj, lambda co: {"Head": 1.0}


def BuildHairLocks(a_material, a_locks, a_top_z, a_name):
    """独立した毛束をまとめて作る。

    a_locks は (中心x, 幅, 毛先z, 外向きtilt, 追加の浮き) のリスト。
    追加の浮きで手前・奥を分けると、前髪が層になって見える。
    1枚の面だけだと板に見え、逆に細い束だけを並べると隙間から額が見えるので、
    「連続した面（BuildBangCurtain）＋その手前に重ねる数本」の構成にしている。
    """
    bm = _NewMesh()

    for cx, width, tip_z, tilt, extra in a_locks:
        push = 0.0
        top_z = a_top_z
        steps = 13
        rings = []
        for i in range(steps):
            t = i / (steps - 1)
            z = _Lerp(top_z, tip_z, t)
            rx, ry = _HairShellRadius(z)
            # 根元は髪本体の内側へ潜らせ、下るにつれて外へ出す。
            # 一定の浮きで生やすと、根元の断面が切り口として本体を突き抜ける。
            lift = _Lerp(-0.008, extra, _SmoothStep(0.0, 0.30, t))
            rx += lift + push * (1.0 - t)
            ry += lift + push * (1.0 - t)
            # 毛先へ向かって細くし、外へ流す。
            # 急に細らせると毛束が「角ばった板」に見え、細らせなさすぎると
            # 隣との境目が階段状に見える。1-t^n の緩い曲線が素直な三角形になる。
            taper = 1.0 - t**1.8
            x_center = cx + tilt * (t**1.4)
            th_center = math.asin(max(-0.99, min(0.99, x_center / max(rx, 1e-6))))
            th_half = (width / max(rx, 1e-6)) * 0.5 * taper
            thickness = (0.020 + extra) * (1.0 - 0.88 * (t**1.6))
            rings.append(
                ArcRing(th_center, th_half, z, rx, rx - thickness, 7, ry / max(rx, 1e-9))
            )
        LoftRings(bm, rings, True, True, True)

    obj = _Finish(bm, a_name, [a_material])
    return obj, lambda co: {"Head": 1.0}


def BuildSideTuft(a_mat_hair, a_mat_tie):
    """本人の左サイドの結んだ毛束と、赤いヘアゴム。"""
    spec = pl_spec.HAIR
    bm_hair = _NewMesh()
    TubeAlongPath(bm_hair, spec["tuft_path"], spec["tuft_radius"], 10, True)
    TubeAlongPath(bm_hair, spec["ahoge"], spec["ahoge_radius"], 8, True)
    hair_obj = _Finish(bm_hair, "pl_tuft", [a_mat_hair])

    # ヘアゴムは毛束の指定インデックス位置にリング状に巻く。
    bm_tie = _NewMesh()
    idx = spec["tie_index"]
    p0 = spec["tuft_path"][idx]
    p1 = spec["tuft_path"][idx + 1]
    direction = _Normalize(_Sub(p1, p0))
    half = spec["tie_thickness"] * 0.5
    path = [
        (p0[0] - direction[0] * half, p0[1] - direction[1] * half, p0[2] - direction[2] * half),
        (p0[0] + direction[0] * half, p0[1] + direction[1] * half, p0[2] + direction[2] * half),
    ]
    TubeAlongPath(bm_tie, path, [spec["tie_radius"], spec["tie_radius"]], 12, True)
    tie_obj = _Finish(bm_tie, "pl_hair_tie", [a_mat_tie])

    weight = lambda co: {"Head": 1.0}
    return [(hair_obj, weight), (tie_obj, weight)]


# ---------------------------------------------------------------------------
# 胴体・衣装
# ---------------------------------------------------------------------------


def _TorsoWeight(a_co):
    """胴体の高さからボーンへのウェイトを決める。隣り合う2ボーンで線形に混ぜる。"""
    z = a_co.z
    stops = [
        (pl_spec.LEVEL["coat_hem"], "Hips"),
        (pl_spec.LEVEL["hip"], "Hips"),
        (pl_spec.LEVEL["waist"], "Spine"),
        (pl_spec.LEVEL["chest"], "Chest"),
        (pl_spec.LEVEL["shoulder"], "Chest"),
        (pl_spec.LEVEL["collar"] + 0.010, "Neck"),
    ]
    if z <= stops[0][0]:
        return {stops[0][1]: 1.0}
    if z >= stops[-1][0]:
        return {stops[-1][1]: 1.0}
    for i in range(len(stops) - 1):
        z0, b0 = stops[i]
        z1, b1 = stops[i + 1]
        if z0 <= z <= z1:
            if b0 == b1:
                return {b0: 1.0}
            t = (z - z0) / max(z1 - z0, 1e-9)
            return {b0: 1.0 - t, b1: t}
    return {"Spine": 1.0}


def BuildBody(a_material):
    """素体の胴。首の付け根から股まで。

    服を胴体として兼用すると、前を開けた瞬間に中身が無くなる。
    肌色の芯を先に置き、服はこの上に少し大きい寸法で重ねる。
    """
    bm = _NewMesh()
    rings = [Ring(0.0, 0.0, z, rx, ry, 20) for (z, rx, ry) in pl_spec.BODY_RINGS]
    LoftRings(bm, rings, True, True, True)
    obj = _Finish(bm, "pl_body", [a_material])
    return obj, _TorsoWeight


def BuildBareArms(a_material):
    """素体の腕。袖の芯になる。袖より細くしないと肘で突き抜ける。"""
    results = []
    for side in (1, -1):
        name = pl_spec.SidePrefix(side)[0]
        bm = _NewMesh()
        path = [(x * side, y, z) for (x, y, z) in pl_spec.ARM_PATH]
        TubeAlongPath(bm, path, list(pl_spec.BARE_ARM_RADIUS), 12, True)
        obj = _Finish(bm, "pl_arm_" + name, [a_material])
        results.append((obj, _ArmWeight(side)))
    return results


def BuildBareLegs(a_material):
    """素体の脚。ニーソの芯になる。太ももが見える丈の服のときに効いてくる。"""
    spec = pl_spec.BARE_LEG
    results = []
    for side in (1, -1):
        name = pl_spec.SidePrefix(side)[0]
        ox = pl_spec.LEG_OFFSET_X * side
        bm = _NewMesh()
        profile = [
            (spec["top_z"], spec["top_radius"]),
            (pl_spec.LEVEL["knee"], spec["knee_radius"]),
            (spec["ankle_z"], spec["ankle_radius"]),
        ]
        rings = []
        for i in range(len(profile) - 1):
            z0, r0 = profile[i]
            z1, r1 = profile[i + 1]
            steps = 3
            for s in range(steps):
                t = s / steps
                rings.append(
                    Ring(ox, 0.0, _Lerp(z0, z1, t), _Lerp(r0, r1, t), _Lerp(r0, r1, t), 14)
                )
        rings.append(Ring(ox, 0.0, profile[-1][0], profile[-1][1], profile[-1][1], 14))
        LoftRings(bm, rings, True, True, True)
        obj = _Finish(bm, "pl_leg_" + name, [a_material])
        results.append((obj, _LegWeight(side)))
    return results


def BuildShirt(a_material):
    """コートの前開きから覗く白いシャツ。"""
    bm = _NewMesh()
    rings = [
        Ring(0.0, oy, z, rx, ry, 24)
        for (z, rx, ry, oy) in pl_spec.SHIRT_RINGS
    ]
    LoftRings(bm, rings, True, True, True)
    obj = _Finish(bm, "pl_shirt", [a_material])
    return obj, _TorsoWeight


def BuildCoat(a_material):
    """オーバーサイズのコート。前面の一部を開けてシャツを覗かせる。"""
    bm = _NewMesh()
    segs = 28
    open_half = math.radians(pl_spec.COAT_OPEN_DEG * 0.5)
    z_lo, z_hi = pl_spec.COAT_OPEN_Z

    rings = []
    for (z, rx, ry, oy) in pl_spec.COAT_RINGS:
        ring = []
        for j in range(segs):
            th = TAU * j / segs
            th_signed = th if th <= math.pi else th - TAU
            ring.append((rx * math.sin(th), oy + ry * math.cos(th), z, th_signed))
        rings.append(ring)

    verts = [[bm.verts.new((p[0], p[1], p[2])) for p in ring] for ring in rings]
    for i in range(len(rings) - 1):
        for j in range(segs):
            k = (j + 1) % segs
            quad = [rings[i][j], rings[i][k], rings[i + 1][k], rings[i + 1][j]]
            th_mid = sum(abs(p[3]) for p in quad) / 4.0
            z_mid = sum(p[2] for p in quad) / 4.0
            if th_mid < open_half and z_lo < z_mid < z_hi:
                continue
            bm.faces.new((verts[i][j], verts[i][k], verts[i + 1][k], verts[i + 1][j]))

    # 蓋はしない。首穴を蓋で塞ぐと、首を囲む板（バケツの縁）ができてしまう。
    # 穴から内側が覗かないようにするのは素体(BODY_RINGS)の役目。
    # 裾も前が開いているので塞がない（塞ぐと開いた前身頃が底板でつながる）。
    loose = [v for v in bm.verts if not v.link_faces]
    if loose:
        bmesh.ops.delete(bm, geom=loose, context="VERTS")
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    bmesh.ops.solidify(bm, geom=list(bm.faces), thickness=-pl_spec.COAT_THICKNESS)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)

    obj = _Finish(bm, "pl_coat", [a_material])
    return obj, _TorsoWeight


def BuildCollar(a_material):
    """外へ倒れ込む大きな襟。前は開ける。"""
    spec = pl_spec.COLLAR
    bm = _NewMesh()
    segs = 28
    open_half = math.radians(spec["open_deg"] * 0.5)

    rings = []
    steps = 4
    for i in range(steps):
        t = i / (steps - 1)
        z = _Lerp(spec["bottom_z"], spec["top_z"], t)
        rx = _Lerp(spec["bottom_radius"][0], spec["top_radius"][0], t)
        ry = _Lerp(spec["bottom_radius"][1], spec["top_radius"][1], t)
        ring = []
        for j in range(segs):
            th = TAU * j / segs
            th_signed = th if th <= math.pi else th - TAU
            ring.append((rx * math.sin(th), ry * math.cos(th), z, th_signed))
        rings.append(ring)

    verts = [[bm.verts.new((p[0], p[1], p[2])) for p in ring] for ring in rings]
    for i in range(len(rings) - 1):
        for j in range(segs):
            k = (j + 1) % segs
            quad = [rings[i][j], rings[i][k], rings[i + 1][k], rings[i + 1][j]]
            th_mid = sum(abs(p[3]) for p in quad) / 4.0
            if th_mid < open_half:
                continue
            bm.faces.new((verts[i][j], verts[i][k], verts[i + 1][k], verts[i + 1][j]))

    loose = [v for v in bm.verts if not v.link_faces]
    if loose:
        bmesh.ops.delete(bm, geom=loose, context="VERTS")
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    bmesh.ops.solidify(bm, geom=list(bm.faces), thickness=-spec["thickness"])
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)

    obj = _Finish(bm, "pl_collar", [a_material])
    return obj, lambda co: {"Chest": 1.0}


def BuildSkirt(a_material):
    """プリーツスカート。半径を角度で波打たせてひだを作る。"""
    spec = pl_spec.SKIRT
    bm = _NewMesh()
    segs = spec["pleats"] * 4

    rings = []
    steps = 4
    for i in range(steps):
        t = i / (steps - 1)
        z = _Lerp(spec["top_z"], spec["bottom_z"], t)
        base_r = _Lerp(spec["top_radius"], spec["bottom_radius"], t)
        ring = []
        for j in range(segs):
            th = TAU * j / segs
            # 裾に向かってひだを深くする
            depth = spec["pleat_depth"] * t
            r = base_r + depth * math.cos(th * spec["pleats"])
            ring.append((r * math.sin(th), r * math.cos(th) * 0.80, z))
        rings.append(ring)

    LoftRings(bm, rings, True, False, False)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    bmesh.ops.solidify(bm, geom=list(bm.faces), thickness=-spec["thickness"])
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)

    obj = _Finish(bm, "pl_skirt", [a_material])
    return obj, lambda co: {"Hips": 1.0}


def BuildUnderwear(a_material):
    """白い下着。スカートがめくれる角度でも素肌が見えないようにする。

    裾を水平に切ると、ただの筒にしか見えない。側面（脚の付け根）だけ
    裾を持ち上げて脚ぐりを作ると、履いているものとして読める。

    股の位置は素体の胴の底面より下にすること。上にすると、真下から見たとき
    下着の下に肌色の底板が覗いて、結局履いていないように見える。
    """
    spec = pl_spec.UNDERWEAR
    bm = _NewMesh()
    segs = 24
    steps = 5

    rings = []
    for i in range(steps):
        t = i / (steps - 1)
        ring = []
        for j in range(segs):
            th = TAU * j / segs
            # 脚ぐり: 側面(|sin|が大きい)ほど裾が高い
            bottom = spec["crotch_z"] + spec["leg_open"] * abs(math.sin(th)) ** 1.4
            z = _Lerp(spec["top_z"], bottom, t)
            k = _Lerp(1.0, spec["waist_taper"], t)
            ring.append(
                (
                    spec["radius_x"] * k * math.sin(th),
                    spec["radius_y"] * k * math.cos(th),
                    z,
                )
            )
        rings.append(ring)

    LoftRings(bm, rings, True, True, True)
    obj = _Finish(bm, "pl_underwear", [a_material])
    return obj, lambda co: {"Hips": 1.0}


# ---------------------------------------------------------------------------
# 腕
# ---------------------------------------------------------------------------


def _ArmWeight(a_side):
    """腕は肩→上腕→前腕→手の順に、経路上の位置でブレンドする。"""
    prefix = pl_spec.SidePrefix(a_side)
    path = pl_spec.ARM_PATH
    shoulder_z = path[0][2]
    elbow_z = path[2][2]
    wrist_z = path[-1][2]

    def Weight(a_co):
        z = a_co.z
        if z >= shoulder_z:
            # 肩の上側は Shoulder ボーンにも持たせる。全部 UpperArm にすると
            # 肩を回したときに袖の付け根が body から剥がれる。
            t = _SmoothStep(shoulder_z, shoulder_z + 0.050, z) * 0.75
            return {prefix + "UpperArm": 1.0 - t, prefix + "Shoulder": t}
        if z >= elbow_z:
            t = (shoulder_z - z) / max(shoulder_z - elbow_z, 1e-9)
            # 肘の手前から徐々に前腕へ渡す
            t = _SmoothStep(0.55, 1.0, t)
            return {prefix + "UpperArm": 1.0 - t, prefix + "LowerArm": t}
        if z >= wrist_z:
            t = (elbow_z - z) / max(elbow_z - wrist_z, 1e-9)
            t = _SmoothStep(0.70, 1.0, t)
            return {prefix + "LowerArm": 1.0 - t, prefix + "Hand": t}
        return {prefix + "Hand": 1.0}

    return Weight


def BuildArms(a_mat_coat, a_mat_cuff, a_mat_skin):
    """袖（コート）・袖口のリブ・手。左右を別オブジェクトにしてウェイトを分ける。"""
    results = []
    for side in (1, -1):
        name = pl_spec.SidePrefix(side)[0]

        bm = _NewMesh()
        path = [(x * side, y, z) for (x, y, z) in pl_spec.ARM_PATH]
        radii = list(pl_spec.ARM_RADIUS)
        TubeAlongPath(bm, path, radii, 14, True)
        sleeve = _Finish(bm, "pl_sleeve_" + name, [a_mat_coat])
        results.append((sleeve, _ArmWeight(side)))

        # 袖口のリブ。コートと同色だと形が溶けるので別マテリアルにする。
        bm = _NewMesh()
        wrist = path[-1]
        direction = _Normalize(_Sub(path[-1], path[-2]))
        cuff = [
            wrist,
            (
                wrist[0] + direction[0] * pl_spec.CUFF_HEIGHT,
                wrist[1] + direction[1] * pl_spec.CUFF_HEIGHT,
                wrist[2] + direction[2] * pl_spec.CUFF_HEIGHT,
            ),
        ]
        TubeAlongPath(bm, cuff, [pl_spec.CUFF_RADIUS, pl_spec.CUFF_RADIUS * 0.94], 14, True)
        cuff_obj = _Finish(bm, "pl_cuff_" + name, [a_mat_cuff])
        results.append((cuff_obj, _ArmWeight(side)))

        bm = _NewMesh()
        cx, cy, cz = pl_spec.HAND["center"]
        rx, ry, rz = pl_spec.HAND["radius"]
        rings = []
        steps = 9
        for i in range(steps):
            phi = math.pi * (i + 0.5) / steps
            k = math.sin(phi)
            rings.append(
                Ring(cx * side, cy, cz + rz * -math.cos(phi), rx * k, ry * k, 12)
            )
        LoftRings(bm, rings, True, True, True)
        hand = _Finish(bm, "pl_hand_" + name, [a_mat_skin])
        prefix = pl_spec.SidePrefix(side)
        results.append((hand, (lambda p: (lambda co: {p: 1.0}))(prefix + "Hand")))
    return results


# ---------------------------------------------------------------------------
# 脚・靴
# ---------------------------------------------------------------------------


def _LegWeight(a_side):
    prefix = pl_spec.SidePrefix(a_side)
    knee = pl_spec.LEVEL["knee"]
    ankle = pl_spec.BOOT["ankle_z"]

    def Weight(a_co):
        z = a_co.z
        if z >= knee:
            t = _SmoothStep(knee + 0.05, knee - 0.01, z)
            return {prefix + "UpperLeg": 1.0 - t, prefix + "LowerLeg": t}
        if z >= ankle:
            t = _SmoothStep(ankle + 0.03, ankle - 0.01, z)
            return {prefix + "LowerLeg": 1.0 - t, prefix + "Foot": t}
        return {prefix + "Foot": 1.0}

    return Weight


def BuildLegs(a_mat_sock, a_mat_boot, a_mat_sole):
    """黒ニーソとレースアップブーツ。"""
    spec_sock = pl_spec.SOCK
    spec_boot = pl_spec.BOOT
    results = []

    for side in (1, -1):
        name = pl_spec.SidePrefix(side)[0]
        ox = pl_spec.LEG_OFFSET_X * side

        # ニーソ。履き口に少し太いバンドを付けると、素肌との境目が
        # 「服の縁」として読める。バンドが無いと脚の色が変わっただけに見える。
        bm = _NewMesh()
        rings = []
        band_top = spec_sock["top_z"]
        band_bottom = band_top - spec_sock["band_height"]
        rings.append(Ring(ox, 0.0, band_top, spec_sock["band_radius"], spec_sock["band_radius"], 14))
        rings.append(
            Ring(ox, 0.0, band_bottom, spec_sock["band_radius"], spec_sock["band_radius"], 14)
        )
        steps = 5
        for i in range(steps):
            t = i / (steps - 1)
            z = _Lerp(band_bottom, spec_sock["bottom_z"], t)
            r = _Lerp(spec_sock["top_radius"], spec_sock["bottom_radius"], t)
            rings.append(Ring(ox, 0.0, z, r, r, 14))
        LoftRings(bm, rings, True, True, True)
        sock = _Finish(bm, "pl_sock_" + name, [a_mat_sock])
        results.append((sock, _LegWeight(side)))

        # ブーツ本体（履き口→足首→ソール上面）
        bm = _NewMesh()
        rings = []
        profile = [
            (spec_boot["top_z"], spec_boot["top_radius"], 0.0),
            (
                _Lerp(spec_boot["top_z"], spec_boot["ankle_z"], 0.5),
                (
                    _Lerp(spec_boot["top_radius"][0], spec_boot["ankle_radius"][0], 0.5),
                    _Lerp(spec_boot["top_radius"][1], spec_boot["ankle_radius"][1], 0.5),
                ),
                spec_boot["toe_y"] * 0.25,
            ),
            (spec_boot["ankle_z"], spec_boot["ankle_radius"], spec_boot["toe_y"] * 0.55),
            (spec_boot["sole_z"], spec_boot["sole_radius"], spec_boot["toe_y"]),
        ]
        for (z, (rx, ry), oy) in profile:
            ring = []
            for j in range(16):
                th = TAU * j / 16
                sy = math.cos(th)
                # つま先側（+Y）だけ前に伸ばす
                extend = spec_boot["toe_extend"] * max(0.0, sy) ** 1.5
                ring.append((ox + rx * math.sin(th), oy + ry * sy + extend, z))
            rings.append(ring)
        # 底も塞ぐ。ソールで隠れる位置だが、開いたままだと穴として残る。
        LoftRings(bm, rings, True, True, True)
        boot = _Finish(bm, "pl_boot_" + name, [a_mat_boot])
        results.append((boot, _LegWeight(side)))

        # 白いソール
        bm = _NewMesh()
        rings = []
        for i, z in enumerate((spec_boot["sole_z"], spec_boot["ground_z"] + 0.004, spec_boot["ground_z"])):
            k = (1.0, 1.0, 0.93)[i]
            ring = []
            for j in range(16):
                th = TAU * j / 16
                sy = math.cos(th)
                rx = spec_boot["sole_radius"][0] * k
                ry = spec_boot["sole_radius"][1] * k
                extend = spec_boot["toe_extend"] * max(0.0, sy) ** 1.5 * k
                ring.append((ox + rx * math.sin(th), spec_boot["toe_y"] + ry * sy + extend, z))
            rings.append(ring)
        LoftRings(bm, rings, True, True, True)
        sole = _Finish(bm, "pl_sole_" + name, [a_mat_sole])
        prefix = pl_spec.SidePrefix(side)
        results.append((sole, (lambda p: (lambda co: {p: 1.0}))(prefix + "Foot")))

    return results


# ---------------------------------------------------------------------------
# ショルダーバッグ
# ---------------------------------------------------------------------------


def BuildBag(a_mat_bag, a_mat_buckle):
    """斜めがけのショルダーバッグ。本体・留め具・ストラップ。"""
    spec = pl_spec.BAG
    sx, sy, sz = spec["size"]
    # 本体はコート表面から鞄の奥行きのぶんだけ外に置く。
    bx, by, _bz = CoatSurface(spec["angle_deg"], spec["z"], spec["offset"] + sy)
    cx, cy, cz = bx, by, spec["z"]
    results = []

    # 鞄は腰に対して「外向き」に付ける。
    # 箱をワールド軸に合わせて作ると、腰の横に下げているのに
    # 正面（キャラクターの向いている方向）を向いてしまい、貼り付けたように見える。
    # 掛けている角度の半径方向を鞄の正面にする。
    th = math.radians(spec["angle_deg"])
    out = (math.sin(th), math.cos(th), 0.0)  # 体から外へ向かう向き＝鞄の正面
    side = (math.cos(th), -math.sin(th), 0.0)  # 体に沿う向き＝鞄の幅方向

    def Place(a_u, a_v, a_z):
        """鞄のローカル座標(幅, 奥行き, 高さ)をワールド座標へ。"""
        return (
            cx + side[0] * a_u + out[0] * a_v,
            cy + side[1] * a_u + out[1] * a_v,
            a_z,
        )

    bm = _NewMesh()
    rings = []
    steps = 7
    for i in range(steps):
        t = i / (steps - 1)
        z = cz + sz * (1.0 - 2.0 * t)
        # 上下の端だけ細める
        k = 1.0 - 0.92 * max(0.0, abs(1.0 - 2.0 * t) - 0.80) / 0.20
        ring = []
        for j in range(16):
            ang = TAU * j / 16
            # 角丸の矩形断面（超楕円）
            s, c = math.sin(ang), math.cos(ang)
            p = 3.0
            norm = (abs(s) ** p + abs(c) ** p) ** (1.0 / p)
            ring.append(Place(sx * s / norm * k, sy * c / norm * k, z))
        rings.append(ring)
    LoftRings(bm, rings, True, True, True)
    body = _Finish(bm, "pl_bag", [a_mat_bag])
    results.append((body, lambda co: {"Chest": 1.0}))

    # ふたの留め具。鞄の正面（外向き）に付ける。
    bm = _NewMesh()
    buckle_z = cz + sz * (1.0 - 2.0 * spec["flap_ratio"]) - sz * 0.35
    path = [Place(0.0, sy * 0.92, buckle_z), Place(0.0, sy * 1.15, buckle_z)]
    TubeAlongPath(bm, path, [0.008, 0.008], 8, True)
    buckle = _Finish(bm, "pl_bag_buckle", [a_mat_buckle])
    results.append((buckle, lambda co: {"Chest": 1.0}))

    # ストラップ。コート表面に沿わせた平たい帯として張る。
    # 通過点をそのまま直線で結ぶと、弦がコートの楕円断面の内側へ潜り、
    # 帯が途切れ途切れに見える。通過点の間を細かく分割し、
    # すべての点を CoatSurface 上で取り直すことで曲面に貼り付ける。
    bm = _NewMesh()
    waypoints = spec["strap"]
    substeps = 6
    path = []
    for i in range(len(waypoints) - 1):
        a0, z0 = waypoints[i]
        a1, z1 = waypoints[i + 1]
        for s in range(substeps):
            t = s / substeps
            path.append(
                CoatSurface(_Lerp(a0, a1, t), _Lerp(z0, z1, t), spec["strap_offset"])
            )
    path.append(CoatSurface(waypoints[-1][0], waypoints[-1][1], spec["strap_offset"]))
    BandAlongPath(bm, path, spec["strap_width"] * 0.5, spec["strap_thickness"] * 0.5)
    strap = _Finish(bm, "pl_bag_strap", [a_mat_bag])
    results.append((strap, _TorsoWeight))

    return results
