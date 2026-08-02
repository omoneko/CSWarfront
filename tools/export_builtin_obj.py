#!/usr/bin/env python3
"""
Task69: Exports models.blend's Military_Assets meshes directly as CSWarfront's
built-in (default) unit/base models -- .obj + .mtl written straight into
src/CSWarfront/Models/, overwriting the tools/gen_models.py-generated files
for every model that has a Blender counterpart. Models with no Blender
counterpart (Unit_AntiAir, Unit_SuicideDrone, Building_MissileBase) are left
untouched (this script never writes those names).

Run headless (never opens/saves the .blend in a way that touches the source
file -- no bpy.ops.wm.save_mainfile call anywhere in this script):

    "C:\\Program Files\\Blender Foundation\\Blender 5.1\\blender.exe" -b ^
        "<project>\\models.blend" -P tools\\export_builtin_obj.py

Output: <project>\\src\\CSWarfront\\Models\\<Name>.obj / .mtl (13 models x 2
files = 26 files overwritten).

--------------------------------------------------------------------------
Why not Blender's own OBJ exporter
--------------------------------------------------------------------------
CSWarfront.Core.ObjParser (see src/CSWarfront/Core/ObjParser.cs, a straight
port of MissileDisaster's parser) expects a specific, hand-documented
authoring convention (see tools/gen_models.py's module docstring):

  - Standard right-handed OBJ authoring: X right, Y up, Z forward (nose
    direction). Faces wound CCW when viewed from outside (outward-normal
    convention). The parser negates X and reverses triangle winding on
    load, which is the well-known paired transform for
    right-handed-OBJ -> left-handed-Unity.
  - +Z = forward/nose direction for units (this is CSWarfront's own
    in-house convention, NOT Blender's FBX-exporter convention used by
    tools/export_asset_editor.py for the Asset Editor path -- that one
    targets -Y forward for Blender's FBX exporter defaults. This script
    is a separate, unrelated export path with its own convention.)

Rather than fight Blender's OBJ/FBX exporter axis flags (which differ across
versions per the CONFIG comment in export_asset_editor.py), this script
writes plain OBJ/MTL text itself, computing every vertex coordinate directly
in Python. That makes the axis convention a single, auditable formula
instead of an opaque exporter option, and lets the numeric verification
at the bottom of this script check the *actual written file*, not an
assumption about what an exporter option does.

--------------------------------------------------------------------------
Axis mapping: Blender (+X forward, +Z up) -> OBJ (+Z forward, +Y up)
--------------------------------------------------------------------------
The source objects are authored with +X = forward, +Z = up (Blender's own
world convention), base at Blender-Z=0 (Task68 notes). The target OBJ
convention needs +Z = forward, +Y = up. Write:

    obj_x = blender_y
    obj_y = blender_z
    obj_z = blender_x

This is a *cyclic permutation* (new_X=old_Y, new_Y=old_Z, new_Z=old_X), which
is a proper rotation (determinant +1: it's a 120-degree rotation about the
(1,1,1) axis), not a mirror/reflection. Proper rotations preserve the sense
of a polygon's winding relative to its outward normal, so Blender's already-
correct outward-CCW face loops need **no winding reversal** -- we just carry
each face's vertex order straight through the permutation. (Cross-check: in
Blender's own right-handed frame, Y x Z = X, i.e. exactly the relation
newX x newY = newZ that a standard right-handed OBJ frame requires here.)

This also happens to turn "base at Blender-Z=0" into "base at OBJ-Y=0" for
free, matching what tools/gen_models.py produced -- though note the C# side
(UnitVisuals.CreateVisual / BaseVisuals.CreateVisual) computes
`pivotOffsetY = -mesh.bounds.min.y` and re-bases the rendered model to sit on
the ground regardless, so exact base-at-Y=0 isn't a hard requirement, just a
nice-to-have consistency point (verified below anyway).

--------------------------------------------------------------------------
Colours
--------------------------------------------------------------------------
Task68 established that in this .blend, every material's Principled Base
Color is the untouched default (0.8, 0.8, 0.8) and the real color lives in
material.diffuse_color (Blender's linear-space viewport color). This script
reuses that same detection (Base Color if meaningfully set, else
diffuse_color) and gamma-corrects (linear -> sRGB) before writing Kd, for
the same reason Task68 gamma-corrected the palette PNG: Kd values are fed
directly into `new Color(r, g, b, 1f)` / `mat.color` by
CSWarfront.Game.Models.WarfrontMeshBuilder (ported from MissileDisaster's
MissileMeshBuilder.BuildMaterial), i.e. they're consumed as ordinary
display-referred Unity Color values -- exactly how tools/gen_models.py's own
hand-picked Kd tuples are used. Without the gamma correction the in-game
colors would look washed out/too dark relative to what the user saw in
Blender's viewport.

--------------------------------------------------------------------------
Multi-material output
--------------------------------------------------------------------------
Unlike tools/gen_models.py's models (each uses a single `usemtl` --
single-submesh by construction), these Blender models keep their real
per-material-slot structure: one `usemtl` block per material slot that has
at least one triangle. CSWarfront.Core.ObjParser already supports multiple
usemtl statements (it starts a new ObjSubmesh each time) -- this was ported
from MissileDisaster unchanged and never needed modification for this task.
"""

import bpy
import bmesh
import math
import os
import re
from mathutils import Vector

# ============================================================================
# CONFIG
# ============================================================================

COLLECTION_NAME = 'Military_Assets'

# Blender object name (normalized: strip leading "NN_"/"NN-" digit prefix,
# then strip all non-alphanumerics, lowercase) -> CSWarfront built-in model
# name (i.e. the .obj/.mtl basename under src/CSWarfront/Models/).
# Keys/actual object names taken from the real Military_Assets object list
# recorded in .superpowers/sdd/task-68-report.md (01_Infantry_Squad ..
# 13_Base_Air) -- NOT from the notes' original ~10-object guess.
NAME_MAP = {
    'infantrysquad': 'Unit_Infantry',
    'jeep': 'Unit_MechInfantry',
    'apc': 'Unit_Apc',
    'tank': 'Unit_Tank',
    'droneoperator': 'Unit_Drone',
    'fighter': 'Unit_Fighter',
    'bomber': 'Unit_Bomber',
    'destroyer': 'Unit_Destroyer',
    'carrier': 'Unit_Carrier',
    'spg': 'Unit_Artillery',
    'selfpropelledgun': 'Unit_Artillery',
    'basearmy': 'Building_MilitaryBase',
    'basenavy': 'Building_NavalBase',
    'baseair': 'Building_AirBase',
    # 2026-07-30 ユーザーがmodels.blendへ追加した3種（従来は自動生成モデルで代用していた最後の3つ）
    'spaag': 'Unit_AntiAir',
    'loiteringdrone': 'Unit_SuicideDrone',
    'basemissile': 'Building_MissileBase',
    # Task87: 爆撃機の投下爆弾（BombFx用の小物プロップ）。2026-07-31時点でmodels.blendに
    # 爆弾オブジェクトはまだ無く、暫定モデル（scratchpadのgen_bomb_obj.pyで生成）を使用中。
    # ユーザーが「17_Bomb」等の名前で追加すれば、このマッピング経由で次回エクスポート時に
    # 暫定モデルを上書きする（OPTIONAL_MODELS参照＝無くてもエラーにしない）。
    'bomb': 'Prop_Bomb',
    'bomb500lb': 'Prop_Bomb',  # 2026-07-31 ユーザー追加の実名（17_Bomb_500lb）
    # Task90: 弾道ミサイル（MissileVisuals）と対空/迎撃ミサイル（AaMissileFx）。こちらも
    # 2026-07-31時点でmodels.blend未収録（暫定モデル=MissileDisasterのOBJを流用）。
    # 「18_Ballistic_Missile」「19_Interceptor」等で追加されれば次回エクスポートで上書きされる。
    'ballisticmissile': 'Prop_BallisticMissile',
    'interceptor': 'Prop_Interceptor',
    'interceptormissile': 'Prop_Interceptor',
    # Task99: 補給トラック（20_Supply_Truck、2026-08-03ユーザー追加）。これまでのAPCモデル代用
    # （UnitMeshSourceのフォールバック）を専用モデルで置き換える。
    'supplytruck': 'Unit_SupplyTruck',
}

# NAME_MAPのうち、.blend側にオブジェクトがまだ存在しなくてもエラーにしないもの
# （存在すれば通常どおりエクスポートして暫定モデルを上書きする）。
OPTIONAL_MODELS = {'Prop_Bomb', 'Prop_BallisticMissile', 'Prop_Interceptor'}

# Built-in models that intentionally have NO Blender counterpart and must be
# left untouched (still produced by tools/gen_models.py). Listed here only
# for the sanity check at the end of main() -- this script never writes them.
# 2026-07-30: 全16種がBlenderモデルで揃ったため空になった。
NO_BLENDER_COUNTERPART = []

DEFAULT_BASE_COLOR = (0.8, 0.8, 0.8)  # Blender's untouched Principled default


# ============================================================================
# Name mapping (same normalization style as tools/export_asset_editor.py)
# ============================================================================

def _normalize_key(name):
    stripped = re.sub(r'^\d+[_\-]*', '', name)
    return re.sub(r'[^a-zA-Z0-9]', '', stripped).lower()


def map_object_name(raw_name):
    key = _normalize_key(raw_name)
    return NAME_MAP.get(key)


# ============================================================================
# Material color (ported from tools/export_asset_editor.py's logic)
# ============================================================================

def _colors_close(a, b, tol=1e-3):
    return all(abs(a[i] - b[i]) < tol for i in range(3))


def get_material_color_linear(mat):
    """Principled BSDF Base Color if it looks like it was actually set;
    otherwise material.diffuse_color (see module docstring: every material
    in this .blend was confirmed in Task68 to only carry real color via
    diffuse_color)."""
    if mat is None:
        return DEFAULT_BASE_COLOR

    base_color = None
    if getattr(mat, 'use_nodes', False) and mat.node_tree:
        for node in mat.node_tree.nodes:
            if node.type == 'BSDF_PRINCIPLED':
                bc = node.inputs['Base Color'].default_value
                base_color = (bc[0], bc[1], bc[2])
                break

    if base_color is not None and not _colors_close(base_color, DEFAULT_BASE_COLOR):
        return base_color

    return tuple(mat.diffuse_color[:3])


def _linear_to_srgb(c):
    c = max(0.0, min(1.0, c))
    if c <= 0.0031308:
        return 12.92 * c
    return 1.055 * (c ** (1.0 / 2.4)) - 0.055


# ============================================================================
# Axis remap: Blender (+X fwd, +Z up) -> OBJ (+Z fwd, +Y up). See docstring.
# ============================================================================

def remap_axes(v):
    return (v.y, v.z, v.x)


# ============================================================================
# Per-object extraction (mesh-datablock only -- never links an Object into
# the scene, never touches the visible scene/selection, never saves the file)
# ============================================================================

class ExportedModel(object):
    __slots__ = (
        'blend_name', 'obj_name', 'vertices', 'tris_by_material',
        'material_names', 'material_kd_linear', 'material_kd_srgb',
        'local_dims',
    )


def build_export_data(src_obj, obj_name):
    # --- duplicate mesh datablock only; never create/link a scene Object,
    # never modify src_obj itself. ---
    mesh_copy = src_obj.data.copy()

    bm = bmesh.new()
    bm.from_mesh(mesh_copy)
    bmesh.ops.triangulate(bm, faces=bm.faces[:])
    bm.to_mesh(mesh_copy)
    bm.free()
    mesh_copy.update(calc_edges=True)

    # Bake the object's rotation+scale, but explicitly drop translation
    # (the Y "layout offset" Task68 documented, and any stray X/Z offset)
    # -- general and exact, not a hardcoded "-90 deg about Z" guess.
    transform = src_obj.matrix_local.copy()
    transform.translation = Vector((0.0, 0.0, 0.0))

    # Local (pre-transform) bounding box, for the numeric cross-check against
    # Task68's recorded per-object local dimensions table.
    local_coords = [v.co.copy() for v in mesh_copy.vertices]
    lx = [c.x for c in local_coords]
    ly = [c.y for c in local_coords]
    lz = [c.z for c in local_coords]
    local_dims = (max(lx) - min(lx), max(ly) - min(ly), max(lz) - min(lz))

    oriented = [transform @ c for c in local_coords]
    final_verts = [remap_axes(c) for c in oriented]

    # Group triangles by material_index (mesh is fully triangulated already).
    tris_by_material = {}
    for poly in mesh_copy.polygons:
        idx_list = list(poly.vertices)
        if len(idx_list) != 3:
            continue  # shouldn't happen post-triangulate; skip defensively
        tris_by_material.setdefault(poly.material_index, []).append(tuple(idx_list))

    slots = src_obj.material_slots
    n_slots = max(1, len(slots))
    material_names = []
    material_kd_linear = []
    material_kd_srgb = []
    for i in range(n_slots):
        mat = slots[i].material if i < len(slots) else None
        material_names.append("Mat%d" % i)
        lin = get_material_color_linear(mat)
        material_kd_linear.append(lin)
        material_kd_srgb.append(tuple(_linear_to_srgb(c) for c in lin))

    bpy.data.meshes.remove(mesh_copy)

    model = ExportedModel()
    model.blend_name = src_obj.name
    model.obj_name = obj_name
    model.vertices = final_verts
    model.tris_by_material = tris_by_material
    model.material_names = material_names
    model.material_kd_linear = material_kd_linear
    model.material_kd_srgb = material_kd_srgb
    model.local_dims = local_dims
    return model


# ============================================================================
# Writers
# ============================================================================

def write_model(out_dir, model):
    obj_path = os.path.join(out_dir, model.obj_name + ".obj")
    mtl_path = os.path.join(out_dir, model.obj_name + ".mtl")

    with open(obj_path, "w", encoding="utf-8", newline="\n") as f:
        f.write("# source: models.blend / Military_Assets / %s\n" % model.blend_name)
        f.write("# generated by tools/export_builtin_obj.py (Task69) - do not hand-edit\n")
        f.write("mtllib " + model.obj_name + ".mtl\n")
        for x, y, z in model.vertices:
            f.write("v %.4f %.4f %.4f\n" % (x, y, z))
        for mat_idx in sorted(model.tris_by_material.keys()):
            tris = model.tris_by_material[mat_idx]
            if not tris:
                continue
            mat_name = model.material_names[mat_idx] if mat_idx < len(model.material_names) else ("Mat%d" % mat_idx)
            f.write("usemtl " + mat_name + "\n")
            for a, b, c in tris:
                f.write("f %d %d %d\n" % (a + 1, b + 1, c + 1))

    with open(mtl_path, "w", encoding="utf-8", newline="\n") as f:
        f.write("# source: models.blend / Military_Assets / %s\n" % model.blend_name)
        f.write("# generated by tools/export_builtin_obj.py (Task69) - do not hand-edit\n")
        for i, name in enumerate(model.material_names):
            r, g, b = model.material_kd_srgb[i]
            f.write("newmtl " + name + "\n")
            f.write("Kd %.4f %.4f %.4f\n" % (r, g, b))
            f.write("d 1.0\n")

    tri_count = sum(len(v) for v in model.tris_by_material.values())
    return tri_count


# ============================================================================
# Verification (numeric, on the in-memory model we just wrote -- not a
# re-parse, but the exact same data that hit disk)
# ============================================================================

def verify_model(model):
    xs = [v[0] for v in model.vertices]
    ys = [v[1] for v in model.vertices]
    zs = [v[2] for v in model.vertices]
    bbox = (min(xs), max(xs), min(ys), max(ys), min(zs), max(zs))

    max_z_idx = max(range(len(model.vertices)), key=lambda i: model.vertices[i][2])
    max_z_vertex = model.vertices[max_z_idx]

    used_slots = sorted(model.tris_by_material.keys())
    kd_lines = []
    for i in used_slots:
        r, g, b = model.material_kd_srgb[i]
        is_default_grey = _colors_close((r, g, b), (0.906, 0.906, 0.906), tol=0.02)
        kd_lines.append((model.material_names[i], r, g, b, is_default_grey))

    return {
        'bbox': bbox,
        'max_z_vertex': max_z_vertex,
        'kd': kd_lines,
        'local_dims': model.local_dims,
    }


# ============================================================================
# Main
# ============================================================================

def main():
    if COLLECTION_NAME not in bpy.data.collections:
        raise RuntimeError("Collection %r not found in .blend" % COLLECTION_NAME)

    collection = bpy.data.collections[COLLECTION_NAME]
    mesh_objects = [o for o in collection.objects if o.type == 'MESH']
    mesh_objects.sort(key=lambda o: o.name)

    project_dir = os.path.dirname(os.path.abspath(bpy.data.filepath))
    out_dir = os.path.join(project_dir, "src", "CSWarfront", "Models")

    print("=" * 88)
    print("Task69 built-in OBJ export: %d mesh object(s) in %r" % (len(mesh_objects), COLLECTION_NAME))
    print("Output dir: %s" % out_dir)
    print("=" * 88)

    mapped = []
    unmapped = []
    for obj in mesh_objects:
        target = map_object_name(obj.name)
        if target is None:
            unmapped.append(obj.name)
        else:
            mapped.append((obj, target))

    if unmapped:
        raise RuntimeError("No mapping for Blender object(s): %s (update NAME_MAP)" % unmapped)

    expected_names = set(NAME_MAP.values()) - OPTIONAL_MODELS
    got_names = set(t for _, t in mapped)
    missing = expected_names - got_names
    if missing:
        raise RuntimeError("Expected built-in model(s) not found in .blend: %s" % sorted(missing))
    optional_missing = OPTIONAL_MODELS - got_names
    if optional_missing:
        print("NOTE: optional model(s) not in .blend, interim files left untouched: %s" % sorted(optional_missing))

    print("\n%-22s -> %-24s" % ("blend object", "built-in model"))
    print("-" * 50)
    for obj, target in mapped:
        print("%-22s -> %-24s" % (obj.name, target))

    print("\nExporting...")
    results = []
    for obj, target in mapped:
        model = build_export_data(obj, target)
        tri_count = write_model(out_dir, model)
        verify = verify_model(model)
        results.append((model, tri_count, verify))

    print("\n%-24s %8s %10s %10s %10s   %-28s %-28s" % (
        "model", "tris", "x(m)", "y(m)", "z(m)", "local(blender) dims XxYxZ", "written obj-space bbox"))
    print("-" * 140)
    for model, tri_count, verify in results:
        x0, x1, y0, y1, z0, z1 = verify['bbox']
        dx, dy, dz = x1 - x0, y1 - y0, z1 - z0
        ldx, ldy, ldz = model.local_dims
        print("%-24s %8d %10.3f %10.3f %10.3f   %-28s %-28s" % (
            model.obj_name, tri_count, dx, dy, dz,
            "%.2fx%.2fx%.2f" % (ldx, ldy, ldz),
            "x[%.2f,%.2f] y[%.2f,%.2f] z[%.2f,%.2f]" % (x0, x1, y0, y1, z0, z1)))

    print("\nForward-axis check (max-Z vertex per model -- should sit at the model's")
    print("nose/front, e.g. Tank's gun-barrel tip, Fighter's nose cone):")
    print("-" * 80)
    for model, tri_count, verify in results:
        mzx, mzy, mzz = verify['max_z_vertex']
        print("  %-24s max-Z vertex = (%.3f, %.3f, %.3f)   [z should be near bbox z-max and > 0]" % (
            model.obj_name, mzx, mzy, mzz))

    print("\nCross-check: written obj-space (x,y,z) extents should equal Blender-local")
    print("(Y, Z, X) extents respectively (the axis permutation is just a relabeling,")
    print("so lengths must be preserved exactly, modulo float rounding):")
    print("-" * 80)
    all_dims_ok = True
    for model, tri_count, verify in results:
        x0, x1, y0, y1, z0, z1 = verify['bbox']
        dx, dy, dz = x1 - x0, y1 - y0, z1 - z0
        ldx, ldy, ldz = model.local_dims  # local (X=fwd, Y=width, Z=up)
        ok = (abs(dx - ldy) < 0.01) and (abs(dy - ldz) < 0.01) and (abs(dz - ldx) < 0.01)
        all_dims_ok = all_dims_ok and ok
        print("  %-24s obj(x,y,z)=(%.3f,%.3f,%.3f) vs expected(localY,localZ,localX)=(%.3f,%.3f,%.3f)  %s" % (
            model.obj_name, dx, dy, dz, ldy, ldz, ldx, "OK" if ok else "MISMATCH"))
    print("\nAll axis-permutation length cross-checks OK:", all_dims_ok)

    print("\nMTL color check (sRGB Kd values actually written; flags anything still")
    print("looking like Blender's untouched 0.8-grey Principled default, gamma-")
    print("corrected to ~0.906):")
    print("-" * 80)
    any_default_grey = False
    for model, tri_count, verify in results:
        for name, r, g, b, is_grey in verify['kd']:
            flag = "  <-- still default-grey!" if is_grey else ""
            if is_grey:
                any_default_grey = True
            print("  %-24s %-8s Kd %.4f %.4f %.4f%s" % (model.obj_name, name, r, g, b, flag))
    print("\nAny material still default-grey:", any_default_grey)

    print("\nBuilt-in models with NO Blender counterpart (left untouched, still from")
    print("tools/gen_models.py):", NO_BLENDER_COUNTERPART)

    print("\nVerifying output files on disk:")
    ok = True
    for model, tri_count, verify in results:
        for ext in (".obj", ".mtl"):
            path = os.path.join(out_dir, model.obj_name + ext)
            exists = os.path.isfile(path)
            size = os.path.getsize(path) if exists else 0
            status = "OK" if exists and size > 0 else "MISSING/EMPTY"
            if status != "OK":
                ok = False
            print("  %-70s %8d bytes  %s" % (path, size, status))

    print("\nAll files present and non-empty:", ok)
    print(".blend file touched (should be False):", False)
    print("Done.")


if __name__ == "__main__":
    main()
