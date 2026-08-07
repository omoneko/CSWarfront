#!/usr/bin/env python3
"""
Task68: Exports every mesh in models.blend's Military_Assets collection to
FBX + a palette diffuse PNG that Cities: Skylines 1's in-game Asset Editor
can import directly.

Run headless (never opens/saves the .blend in a way that touches the source
file):

    "C:\\Program Files\\Blender Foundation\\Blender 5.1\\blender.exe" -b ^
        "<project>\\models.blend" -P tools\\export_asset_editor.py

Output: <project>\\asset-editor-export\\<AsciiName>.fbx and
        <project>\\asset-editor-export\\<AsciiName>_d.png

--------------------------------------------------------------------------
Why a palette texture
--------------------------------------------------------------------------
The source models are colored purely via material slots (Principled BSDF
Base Color, or - as turned out to be the case for every material in this
particular .blend - the material's viewport `diffuse_color`, since the
Base Color sockets were all left at Blender's untouched default 0.8 grey).
CS1's Asset Editor does not read materials/shaders from the FBX at all; the
only color input is a `<name>_d.png` texture sampled via the mesh's UVs.
There are no UVs on any of these objects.

So for each object we build a tiny "palette" texture with one solid-color
cell per material slot, and give every triangle a single-point UV at the
center of its material's cell. This reproduces the material-slot coloring
exactly, deterministically, with no Cycles/Eevee baking involved.

--------------------------------------------------------------------------
Orientation
--------------------------------------------------------------------------
Source objects are authored with +X = forward (base at Z=0, laid out along
Y in the scene for non-overlapping viewing/editing - that Y offset is
scene-layout only and is zeroed out on export so every FBX is centered on
the origin).

Blender's FBX exporter's default axis mapping (axis_forward='-Z',
axis_up='Y') is the conventional, broadly-documented-correct mapping for
Unity/CS1 import. Combined with that default, the established convention
for this pipeline is that the model's front should face **-Y** in Blender
space before export. Since the source models face +X, that's a -90 degree
rotation about Z.

If the Asset Editor shows a model facing backwards (or mirrored), the fix
is almost always to flip the sign of FRONT_ROTATION_DEG below and re-run.
"""

import bpy
import bmesh  # noqa: F401  (kept available for any future palette/UV tweak)
import math
import os
import re


# ============================================================================
# CONFIG - the knobs a user is most likely to need to touch
# ============================================================================

# Degrees to rotate each model about Z (world up in Blender) before export,
# to go from the source authoring convention (+X = forward) to this export
# pipeline's target convention (-Y = forward, matching Blender FBX exporter
# defaults for Unity/CS1). If the Asset Editor shows a model backwards or
# mirrored left/right, flip the sign (-90.0 <-> 90.0) and re-run.
FRONT_ROTATION_DEG = -90.0

# Blender FBX exporter axis settings. These are literally the exporter's own
# defaults (axis_forward='-Z', axis_up='Y'); kept explicit here rather than
# omitted so the export is reproducible even if Blender's defaults ever
# change. This is the half of the "+X forward -> Unity/CS1 forward" mapping
# that isn't controlled by FRONT_ROTATION_DEG.
FBX_AXIS_FORWARD = '-Z'
FBX_AXIS_UP = 'Y'

# Pixels per palette cell (one cell per material slot). Larger cells give
# more headroom against texture-filtering bleed at a distance in-game.
PALETTE_CELL_PX = 32

COLLECTION_NAME = 'Military_Assets'

# Japanese source concept -> ASCII export name, keyed by the object's name
# with digits/underscores/punctuation stripped and lowercased, so it matches
# regardless of the "NN_" numeric prefix or underscore spelling actually
# used in the .blend.
NAME_MAP = {
    # infantry squad
    'infantrysquad': 'InfantrySquad',
    # jeep
    'jeep': 'Jeep',
    # APC
    'apc': 'APC',
    # tank
    'tank': 'Tank',
    # self-propelled gun
    'spg': 'SPG',
    'selfpropelledgun': 'SPG',
    # drone operator
    'droneoperator': 'DroneOperator',
    # fighter
    'fighter': 'Fighter',
    # bomber
    'bomber': 'Bomber',
    # missile destroyer (source object is just named "Destroyer")
    'destroyer': 'MissileDestroyer',
    'missiledestroyer': 'MissileDestroyer',
    # carrier
    'carrier': 'Carrier',
    # added 2026-07-30 (the 3 bases got fallback names in Task68; explicit mappings from here on)
    'basearmy': 'BaseArmy',
    'basenavy': 'BaseNavy',
    'baseair': 'BaseAir',
    'spaag': 'SPAAG',
    'loiteringdrone': 'LoiteringDrone',
    'basemissile': 'BaseMissile',
}

DEFAULT_BASE_COLOR = (0.8, 0.8, 0.8)  # Blender's untouched Principled default


# ============================================================================
# Name mapping
# ============================================================================

def _normalize_key(name_no_prefix):
    return re.sub(r'[^a-zA-Z0-9]', '', name_no_prefix).lower()


def _sanitize_fallback(name_no_prefix):
    """ASCII CamelCase fallback for names not in NAME_MAP."""
    parts = re.split(r'[^a-zA-Z0-9]+', name_no_prefix)
    parts = [p for p in parts if p]
    ascii_parts = []
    for p in parts:
        ascii_p = p.encode('ascii', 'ignore').decode('ascii')
        if ascii_p:
            ascii_parts.append(ascii_p[0].upper() + ascii_p[1:])
    result = ''.join(ascii_parts)
    return result if result else 'Model'


def map_object_name(raw_name):
    """Returns (ascii_name, matched_known_entry: bool)."""
    stripped = re.sub(r'^\d+[_\-]*', '', raw_name)
    key = _normalize_key(stripped)
    if key in NAME_MAP:
        return NAME_MAP[key], True
    return _sanitize_fallback(stripped), False


# ============================================================================
# Material color
# ============================================================================

def _colors_close(a, b, tol=1e-3):
    return all(abs(a[i] - b[i]) < tol for i in range(3))


def get_material_color(mat):
    """Principled BSDF Base Color if it looks like it was actually set;
    otherwise material.diffuse_color (this .blend's real color source -
    every material's Base Color socket was left at the untouched 0.8 grey
    default, while diffuse_color carries the intended per-material color)."""
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
# Palette texture + UVs
# ============================================================================

def build_palette_and_uvs(obj, name):
    """Creates an N x 1 palette image (N = number of material slots), fills
    it with each slot's color, and assigns every triangle a single-point UV
    at the center of its material's cell. Returns the created Image."""
    mesh = obj.data
    slots = obj.material_slots
    n = max(1, len(slots))

    colors = [get_material_color(slot.material) for slot in slots] or [DEFAULT_BASE_COLOR]

    width = n * PALETTE_CELL_PX
    height = PALETTE_CELL_PX
    img = bpy.data.images.new(name + "_d", width=width, height=height, alpha=False)
    # Treat our manually-gamma-corrected floats as literal output bytes -
    # skip Blender's own color management transform on save.
    img.colorspace_settings.name = 'Non-Color'

    pixels = [0.0] * (width * height * 4)
    for i, col in enumerate(colors):
        r, g, b = (_linear_to_srgb(c) for c in col)
        x0 = i * PALETTE_CELL_PX
        for y in range(height):
            for x in range(x0, x0 + PALETTE_CELL_PX):
                idx = (y * width + x) * 4
                pixels[idx + 0] = r
                pixels[idx + 1] = g
                pixels[idx + 2] = b
                pixels[idx + 3] = 1.0
    img.pixels.foreach_set(pixels)

    uv_layer = mesh.uv_layers.new(name="palette_uv")
    for poly in mesh.polygons:
        mat_index = poly.material_index if poly.material_index < n else 0
        u = (mat_index + 0.5) / n
        v = 0.5
        for loop_index in poly.loop_indices:
            uv_layer.data[loop_index].uv = (u, v)

    return img


# ============================================================================
# Per-object export
# ============================================================================

def triangulate(obj):
    mod = obj.modifiers.new(name="Task68_Triangulate", type='TRIANGULATE')
    with bpy.context.temp_override(object=obj):
        bpy.ops.object.modifier_apply(modifier=mod.name)


def export_object(src_obj, out_dir, view_layer):
    ascii_name, matched = map_object_name(src_obj.name)

    # --- duplicate, never touch the original ---
    mesh_copy = src_obj.data.copy()
    dup = bpy.data.objects.new(src_obj.name + "_export", mesh_copy)
    bpy.context.scene.collection.objects.link(dup)

    for o in bpy.context.view_layer.objects:
        o.select_set(False)
    dup.select_set(True)
    view_layer.objects.active = dup

    # zero out scene-layout offset (the Y spacing between models in the
    # source file is layout-only, not part of the model) and apply the
    # front-facing rotation, then bake everything into the mesh data.
    dup.location = (0.0, 0.0, 0.0)
    dup.rotation_euler = (0.0, 0.0, math.radians(FRONT_ROTATION_DEG))
    dup.scale = (1.0, 1.0, 1.0)
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)

    triangulate(dup)

    tri_count = len(dup.data.polygons)
    dims = tuple(dup.dimensions)

    img = build_palette_and_uvs(dup, ascii_name)

    fbx_path = os.path.join(out_dir, ascii_name + ".fbx")
    png_path = os.path.join(out_dir, ascii_name + "_d.png")

    img.filepath_raw = png_path
    img.file_format = 'PNG'
    img.save()

    for o in bpy.context.view_layer.objects:
        o.select_set(False)
    dup.select_set(True)
    view_layer.objects.active = dup

    bpy.ops.export_scene.fbx(
        filepath=fbx_path,
        check_existing=False,
        use_selection=True,
        object_types={'MESH'},
        global_scale=1.0,
        apply_unit_scale=True,
        apply_scale_options='FBX_SCALE_NONE',
        bake_space_transform=True,
        use_mesh_modifiers=True,
        mesh_smooth_type='FACE',
        use_triangles=True,
        add_leaf_bones=False,
        bake_anim=False,
        path_mode='COPY',
        embed_textures=False,
        axis_forward=FBX_AXIS_FORWARD,
        axis_up=FBX_AXIS_UP,
    )

    # --- cleanup ---
    mesh_data = dup.data
    bpy.data.objects.remove(dup, do_unlink=True)
    bpy.data.meshes.remove(mesh_data)
    bpy.data.images.remove(img)

    return {
        'raw_name': src_obj.name,
        'ascii_name': ascii_name,
        'matched_known_mapping': matched,
        'tri_count': tri_count,
        'dimensions': dims,
        'material_slots': [s.material.name if s.material else None for s in src_obj.material_slots],
        'fbx_path': fbx_path,
        'png_path': png_path,
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
    out_dir = os.path.join(project_dir, "asset-editor-export")
    os.makedirs(out_dir, exist_ok=True)

    view_layer = bpy.context.view_layer

    print("=" * 78)
    print("Task68 export: %d mesh object(s) in %r" % (len(mesh_objects), COLLECTION_NAME))
    print("FRONT_ROTATION_DEG = %s  (flip sign if model faces backwards in-game)" % FRONT_ROTATION_DEG)
    print("FBX axis_forward=%s axis_up=%s" % (FBX_AXIS_FORWARD, FBX_AXIS_UP))
    print("Output dir: %s" % out_dir)
    print("=" * 78)

    print("\n%-22s -> %-20s %s" % ("blend object", "ascii name", "mapping"))
    print("-" * 60)
    results = []
    for obj in mesh_objects:
        r = export_object(obj, out_dir, view_layer)
        results.append(r)
        print("%-22s -> %-20s %s" % (
            r['raw_name'], r['ascii_name'],
            "known" if r['matched_known_mapping'] else "fallback(sanitized)"))

    print("\n%-20s %8s %10s %10s %10s  %s" % (
        "ascii name", "tris", "dim_x(m)", "dim_y(m)", "dim_z(m)", "material_slots"))
    print("-" * 100)
    for r in results:
        dx, dy, dz = r['dimensions']
        print("%-20s %8d %10.2f %10.2f %10.2f  %s" % (
            r['ascii_name'], r['tri_count'], dx, dy, dz, ", ".join(m or "-" for m in r['material_slots'])))

    print("\nVerifying output files:")
    ok = True
    for r in results:
        for path in (r['fbx_path'], r['png_path']):
            exists = os.path.isfile(path)
            size = os.path.getsize(path) if exists else 0
            status = "OK" if exists and size > 0 else "MISSING/EMPTY"
            if status != "OK":
                ok = False
            print("  %-70s %10d bytes  %s" % (path, size, status))

    print("\nAll files present and non-empty:" , ok)
    print("Done.")


if __name__ == "__main__":
    main()
