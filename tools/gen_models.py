#!/usr/bin/env python3
"""
Throwaway generator for CSWarfront's built-in default unit/base models (Task57).

Not shipped with the mod. Run once, commit the resulting .obj/.mtl text under
src/CSWarfront/Models/. Coordinate convention matches CSWarfront.Core.ObjParser's
expectations (a straight port of MissileDisaster's Blender-oriented parser):
  - Standard right-handed OBJ authoring (X right, Y up, Z "out of screen"/forward).
  - Faces must be wound CCW when viewed from outside (outward-normal convention).
  - ObjParser negates X and reverses triangle winding on load, which is the
    well-known paired transform for right-handed-OBJ -> left-handed-Unity; as long
    as we author standard right-hand CCW-outward polygons here, the result renders
    correctly (RecalculateNormals resolves final shading in Unity).
  - Origin at each model's center in X/Z, base (lowest point) at y=0.
  - +Z = forward/nose direction for units.

Every shape helper below emits a single vertex per box corner (or per prism ring
point) and one polygon per face (ObjParser fan-triangulates n-gons itself), which
keeps the generator small while still producing correct low-poly geometry.
"""
import math
import os

# ----------------------------------------------------------------------------
# Small vector helpers (tuples of 3 floats)
# ----------------------------------------------------------------------------

def sub(a, b):
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])


def add(a, b):
    return (a[0] + b[0], a[1] + b[1], a[2] + b[2])


def scale(a, s):
    return (a[0] * s, a[1] * s, a[2] * s)


def dot(a, b):
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def cross(a, b):
    return (
        a[1] * b[2] - a[2] * b[1],
        a[2] * b[0] - a[0] * b[2],
        a[0] * b[1] - a[1] * b[0],
    )


def normalize(a):
    length = math.sqrt(dot(a, a))
    if length < 1e-9:
        return (0.0, 0.0, 0.0)
    return (a[0] / length, a[1] / length, a[2] / length)


def deg(d):
    return d * math.pi / 180.0


# ----------------------------------------------------------------------------
# Mesh builder
# ----------------------------------------------------------------------------

class Mesh:
    def __init__(self):
        self.verts = []  # list of (x, y, z)
        self.faces = []  # list of list[int] (0-based indices)

    def add_vertex(self, x, y, z):
        self.verts.append((x, y, z))
        return len(self.verts) - 1

    def add_face(self, indices):
        self.faces.append(list(indices))

    # --- box / hexahedron -----------------------------------------------
    def add_hexahedron(self, corners):
        """corners: dict[(0/1,0/1,0/1)] -> (x,y,z). Emits 6 quads, each wound
        so (v1-v0) x (v2-v0) points along the face's outward axis-aligned normal
        (derived by hand and verified against a plain axis-aligned box)."""
        c = {k: self.add_vertex(*v) for k, v in corners.items()}
        self.add_face([c[(0, 0, 0)], c[(0, 1, 0)], c[(1, 1, 0)], c[(1, 0, 0)]])  # -Z
        self.add_face([c[(0, 0, 1)], c[(1, 0, 1)], c[(1, 1, 1)], c[(0, 1, 1)]])  # +Z
        self.add_face([c[(0, 0, 0)], c[(0, 0, 1)], c[(0, 1, 1)], c[(0, 1, 0)]])  # -X
        self.add_face([c[(1, 0, 0)], c[(1, 1, 0)], c[(1, 1, 1)], c[(1, 0, 1)]])  # +X
        self.add_face([c[(0, 0, 0)], c[(1, 0, 0)], c[(1, 0, 1)], c[(0, 0, 1)]])  # -Y
        self.add_face([c[(0, 1, 0)], c[(0, 1, 1)], c[(1, 1, 1)], c[(1, 1, 0)]])  # +Y

    def add_box(self, cx, cy, cz, sx, sy, sz):
        x0, x1 = cx - sx / 2, cx + sx / 2
        y0, y1 = cy - sy / 2, cy + sy / 2
        z0, z1 = cz - sz / 2, cz + sz / 2
        self.add_hexahedron({
            (0, 0, 0): (x0, y0, z0), (1, 0, 0): (x1, y0, z0),
            (0, 1, 0): (x0, y1, z0), (1, 1, 0): (x1, y1, z0),
            (0, 0, 1): (x0, y0, z1), (1, 0, 1): (x1, y0, z1),
            (0, 1, 1): (x0, y1, z1), (1, 1, 1): (x1, y1, z1),
        })

    def add_tapered_box(self, cx, sx, y0, y1_back, y1_front, z_back, z_front):
        """Extruded along X. Bottom is flat (y0) for both z_back/z_front; the top
        slopes from y1_back (at z_back) to y1_front (at z_front). Used for glacis
        plates / wedge noses / spade shapes."""
        x0, x1 = cx - sx / 2, cx + sx / 2
        self.add_hexahedron({
            (0, 0, 0): (x0, y0, z_back), (1, 0, 0): (x1, y0, z_back),
            (0, 1, 0): (x0, y1_back, z_back), (1, 1, 0): (x1, y1_back, z_back),
            (0, 0, 1): (x0, y0, z_front), (1, 0, 1): (x1, y0, z_front),
            (0, 1, 1): (x0, y1_front, z_front), (1, 1, 1): (x1, y1_front, z_front),
        })

    # --- prism (N-sided, arbitrary axis) ---------------------------------
    def add_prism(self, center, axis_dir, length, radius, sides=6):
        """Extruded regular-polygon prism (used for gun barrels / missile
        canisters / rotor discs). axis_dir need not be normalized/unit-axis-
        aligned. Cap/side winding derived from the u,v,axis right-handed
        basis so the outward-normal convention holds for any orientation."""
        axis_dir = normalize(axis_dir)
        arbitrary = (0.0, 1.0, 0.0) if abs(axis_dir[1]) < 0.9 else (1.0, 0.0, 0.0)
        u = normalize(sub(arbitrary, scale(axis_dir, dot(arbitrary, axis_dir))))
        v = cross(axis_dir, u)  # u x v == axis_dir

        start = sub(center, scale(axis_dir, length / 2))
        end = add(center, scale(axis_dir, length / 2))

        start_idx = []
        end_idx = []
        for i in range(sides):
            theta = 2 * math.pi * i / sides
            offset = add(scale(u, radius * math.cos(theta)), scale(v, radius * math.sin(theta)))
            start_idx.append(self.add_vertex(*add(start, offset)))
            end_idx.append(self.add_vertex(*add(end, offset)))

        # Measured empirically against Newell-normal outward checks (tools/validate_winding.py):
        # with v = axis_dir x u (so u x v == +axis_dir), the increasing-theta vertex order
        # traces a face whose Newell normal is +axis_dir, not -axis_dir as a naive port of the
        # "cylinder along Y" derivation would suggest (that derivation happened to pick u,v with
        # u x v == -axis_dir). So the *end* cap (normal +axis_dir) uses the direct order and the
        # *start* cap (normal -axis_dir) uses the reversed order; side quads are likewise reversed
        # from the first draft.
        self.add_face(list(reversed(start_idx)))  # normal -axis_dir
        self.add_face(end_idx)  # normal +axis_dir

        for i in range(sides):
            j = (i + 1) % sides
            self.add_face([start_idx[j], end_idx[j], end_idx[i], start_idx[i]])


# ----------------------------------------------------------------------------
# OBJ/MTL writers
# ----------------------------------------------------------------------------

def write_model(out_dir, name, mesh, color, comment):
    obj_path = os.path.join(out_dir, name + ".obj")
    mtl_path = os.path.join(out_dir, name + ".mtl")
    mat_name = "Mat"

    with open(obj_path, "w", encoding="utf-8", newline="\n") as f:
        f.write("# " + comment + "\n")
        f.write("# generated by tools/gen_models.py (Task57) - do not hand-edit\n")
        f.write("mtllib " + name + ".mtl\n")
        for x, y, z in mesh.verts:
            f.write("v %.4f %.4f %.4f\n" % (x, y, z))
        f.write("usemtl " + mat_name + "\n")
        for face in mesh.faces:
            f.write("f " + " ".join(str(i + 1) for i in face) + "\n")

    with open(mtl_path, "w", encoding="utf-8", newline="\n") as f:
        f.write("# generated by tools/gen_models.py (Task57) - do not hand-edit\n")
        f.write("newmtl " + mat_name + "\n")
        f.write("Kd %.3f %.3f %.3f\n" % color)
        f.write("d 1.0\n")

    return len(mesh.verts), len(mesh.faces)


# ----------------------------------------------------------------------------
# Models
# ----------------------------------------------------------------------------

def build_infantry():
    m = Mesh()
    # legs: y 0..1.0
    m.add_box(-0.18, 0.50, 0.0, 0.28, 1.00, 0.28)
    m.add_box(0.18, 0.50, 0.0, 0.28, 1.00, 0.28)
    # torso: y 1.0..1.85
    m.add_box(0.0, 1.425, 0.0, 0.70, 0.85, 0.40)
    # head: y 1.85..2.2
    m.add_box(0.0, 2.025, 0.0, 0.35, 0.35, 0.35)
    return m


def build_mech_infantry():
    m = Mesh()
    # tracks: y 0..0.5
    m.add_box(-1.0, 0.25, 0.0, 0.4, 0.5, 5.0)
    m.add_box(1.0, 0.25, 0.0, 0.4, 0.5, 5.0)
    # hull: y 0.5..1.6
    m.add_box(0.0, 1.05, 0.0, 2.2, 1.1, 4.6)
    # figure on top: torso y1.6..2.15, head y2.15..2.5
    m.add_box(0.0, 1.875, 0.3, 0.5, 0.55, 0.4)
    m.add_box(0.0, 2.325, 0.3, 0.3, 0.35, 0.3)
    return m


def build_apc():
    m = Mesh()
    # tracks: y 0..0.5
    m.add_box(-1.3, 0.25, 0.0, 0.4, 0.5, 6.0)
    m.add_box(1.3, 0.25, 0.0, 0.4, 0.5, 6.0)
    # hull: y 0.5..2.1, back-weighted so the wedge nose extends the length
    m.add_box(0.0, 1.3, -0.75, 2.4, 1.6, 4.5)
    # wedge nose: full height at hull front (z=1.5), tapered low at the tip (z=3.25)
    m.add_tapered_box(0.0, 2.4, 0.5, 2.1, 0.9, 1.5, 3.25)
    return m


def build_tank():
    m = Mesh()
    # tracks: y 0..0.6
    m.add_box(-1.6, 0.3, 0.0, 0.5, 0.6, 7.0)
    m.add_box(1.6, 0.3, 0.0, 0.5, 0.6, 7.0)
    # hull: y 0.6..1.6
    m.add_box(0.0, 1.1, -0.7, 2.8, 1.0, 5.0)
    # glacis (sloped front plate): full height at hull front (z=1.8), low at tip (z=2.7)
    m.add_tapered_box(0.0, 2.8, 0.6, 1.6, 1.0, 1.8, 2.7)
    # turret: y 1.6..2.3
    m.add_box(0.0, 1.95, -0.5, 1.8, 0.7, 2.0)
    # gun barrel: 6-sided prism from the turret front out to the muzzle
    m.add_prism(center=(0.0, 1.95, 2.6), axis_dir=(0.0, 0.0, 1.0), length=4.2, radius=0.12, sides=6)
    return m


def build_artillery():
    m = Mesh()
    # tracks: y 0..0.6
    m.add_box(-1.3, 0.3, 0.0, 0.5, 0.6, 6.0)
    m.add_box(1.3, 0.3, 0.0, 0.5, 0.6, 6.0)
    # hull: y 0.6..1.6
    m.add_box(0.0, 1.1, 0.0, 2.6, 1.0, 5.0)
    # rear spade (digs into the ground behind the hull)
    m.add_box(0.0, 0.4, -2.75, 2.0, 0.8, 0.3)
    # gun cradle atop the hull front
    m.add_box(0.0, 1.9, 1.5, 1.0, 0.6, 1.0)
    # barrel angled ~25 degrees up from horizontal, muzzle forward-and-up
    angle = deg(25.0)
    axis = (0.0, math.sin(angle), math.cos(angle))
    barrel_len = 5.0
    start = (0.0, 1.95, 1.9)
    center = add(start, scale(axis, barrel_len / 2))
    m.add_prism(center=center, axis_dir=axis, length=barrel_len, radius=0.15, sides=6)
    return m


def build_antiair():
    m = Mesh()
    # tracks: y 0..0.6
    m.add_box(-1.3, 0.3, 0.0, 0.5, 0.6, 5.5)
    m.add_box(1.3, 0.3, 0.0, 0.5, 0.6, 5.5)
    # hull: y 0.6..1.6
    m.add_box(0.0, 1.1, 0.0, 2.6, 1.0, 4.5)
    # rotating mount: y 1.6..2.2
    m.add_box(0.0, 1.9, 0.0, 1.8, 0.6, 1.8)
    # 4 missile canisters, angled ~35 degrees up and slightly outward, mounted on the turret
    angle = deg(35.0)
    canister_len = 1.2
    for sign_x, sign_z in ((-1, -1), (-1, 1), (1, -1), (1, 1)):
        axis = normalize((sign_x * math.sin(angle) * 0.5, math.sin(angle), sign_z * 0.15 + math.cos(angle) * 0.6))
        base = (sign_x * 0.6, 2.2, sign_z * 0.5)
        center = add(base, scale(axis, canister_len / 2))
        m.add_prism(center=center, axis_dir=axis, length=canister_len, radius=0.12, sides=6)
    return m


def build_drone():
    m = Mesh()
    # 4 landing skids: y 0..0.2
    for sx, sz in ((-0.2, -0.2), (-0.2, 0.2), (0.2, -0.2), (0.2, 0.2)):
        m.add_box(sx, 0.10, sz, 0.06, 0.20, 0.06)
    # body: y 0.2..0.5
    m.add_box(0.0, 0.35, 0.0, 0.5, 0.30, 0.5)
    # 4 arms ("+" pattern, readable from above) at body mid-height
    arm_y = 0.35
    m.add_box(0.55, arm_y, 0.0, 0.7, 0.05, 0.08)
    m.add_box(-0.55, arm_y, 0.0, 0.7, 0.05, 0.08)
    m.add_box(0.0, arm_y, 0.55, 0.08, 0.05, 0.7)
    m.add_box(0.0, arm_y, -0.55, 0.08, 0.05, 0.7)
    # 4 rotor discs (thin hexagonal prisms) just above the arms
    rotor_y = 0.42
    for cx, cz in ((0.85, 0.0), (-0.85, 0.0), (0.0, 0.85), (0.0, -0.85)):
        m.add_prism(center=(cx, rotor_y, cz), axis_dir=(0.0, 1.0, 0.0), length=0.04, radius=0.32, sides=6)
    return m


def build_military_base():
    m = Mesh()
    # main hangar block
    m.add_box(-4.0, 4.5, 2.0, 20.0, 9.0, 14.0)
    # lower annex
    m.add_box(8.0, 2.5, -4.0, 14.0, 5.0, 10.0)
    # mast (radio/comms mast, tallest point, readable from above as a small dot)
    m.add_prism(center=(-12.5, 6.0, 8.5), axis_dir=(0.0, 1.0, 0.0), length=12.0, radius=0.3, sides=6)
    return m


MODELS = [
    ("Unit_Infantry", build_infantry, (0.30, 0.32, 0.22), "Infantry: torso+head+2 legs, ~0.8x0.8x2.2m"),
    ("Unit_MechInfantry", build_mech_infantry, (0.27, 0.30, 0.20), "Mechanized infantry carrier, ~5.5x2.4x2.6m"),
    ("Unit_Apc", build_apc, (0.25, 0.28, 0.20), "Wedge-fronted APC hull, ~6.5x2.8x2.6m"),
    ("Unit_Tank", build_tank, (0.22, 0.26, 0.18), "Tank: hull+glacis+turret+barrel, ~7.5x3.4x2.6m"),
    ("Unit_Artillery", build_artillery, (0.24, 0.24, 0.20), "Self-propelled artillery, ~8.0x3.0x2.8m"),
    ("Unit_AntiAir", build_antiair, (0.20, 0.24, 0.22), "Anti-air vehicle w/ 4 canisters, ~6.5x3.0x3.0m"),
    ("Unit_Drone", build_drone, (0.15, 0.15, 0.15), "Quad-rotor drone, ~2.0x2.0x0.6m"),
    ("Building_MilitaryBase", build_military_base, (0.30, 0.33, 0.24), "Military base: hangar+annex+mast, ~32x24x12m"),
]


def main():
    out_dir = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "src", "CSWarfront", "Models")
    os.makedirs(out_dir, exist_ok=True)

    print("%-24s %8s %8s" % ("model", "verts", "faces"))
    for name, builder, color, comment in MODELS:
        mesh = builder()
        nv, nf = write_model(out_dir, name, mesh, color, comment)
        print("%-24s %8d %8d" % (name, nv, nf))


if __name__ == "__main__":
    main()
