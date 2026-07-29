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

    # --- wing / fin (arbitrary-planform flat panel, Task61) --------------
    def add_wing(self, x0, x0_z_back, x0_z_front, x1, x1_z_back, x1_z_front, y0, y1):
        """Flat panel extruded from y0 to y1, with independently swept leading/trailing edges at
        two spanwise stations x0 and x1. Generalizes add_tapered_box's index structure (same
        (i,j,k)-corner-key scheme, so the already-verified winding formulas in add_hexahedron
        carry over unchanged) to also vary Z per spanwise station, which a plain box or
        add_tapered_box (fixed X bounds) can't express - used for delta wings and tail fins.
        Caller MUST pass x0 < x1 numerically (same invariant add_box/add_tapered_box rely on:
        for a mirrored/left-side panel, swap which physical side is "x0" vs "x1" so the smaller
        value is always x0 - see build_wing_pair below). z_back/z_front follow the same
        'z_back < z_front, +Z is nose-forward' convention as add_tapered_box."""
        self.add_hexahedron({
            (0, 0, 0): (x0, y0, x0_z_back), (1, 0, 0): (x1, y0, x1_z_back),
            (0, 1, 0): (x0, y1, x0_z_back), (1, 1, 0): (x1, y1, x1_z_back),
            (0, 0, 1): (x0, y0, x0_z_front), (1, 0, 1): (x1, y0, x1_z_front),
            (0, 1, 1): (x0, y1, x0_z_front), (1, 1, 1): (x1, y1, x1_z_front),
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


def add_wing_pair(m, root_x, root_z_back, root_z_front, span, tip_z_back, tip_z_front, y0, y1):
    """Adds a mirrored pair of wing panels (right +X, left -X) via Mesh.add_wing, taking care of
    the x0<x1 ordering add_wing requires on both sides (see its docstring): for the right wing,
    root(smaller x) is x0 and tip(larger x) is x1; for the mirrored left wing the roles swap
    numerically (tip is more negative = smaller, so it becomes x0)."""
    tip_x = root_x + span
    m.add_wing(root_x, root_z_back, root_z_front, tip_x, tip_z_back, tip_z_front, y0, y1)  # right (+X)
    m.add_wing(-tip_x, tip_z_back, tip_z_front, -root_x, root_z_back, root_z_front, y0, y1)  # left (-X)


def build_destroyer():
    m = Mesh()
    # hull (waterline to main deck): y 0..8, ~120m long
    m.add_box(0.0, 4.0, 0.0, 14.0, 8.0, 120.0)
    # forward VLS deck (raised flat deck, missile silo cluster) toward the bow (+Z)
    m.add_box(0.0, 8.5, 40.0, 10.0, 1.0, 24.0)
    for cz in (32.0, 40.0, 48.0):
        for cx in (-3.0, 3.0):
            m.add_prism(center=(cx, 9.5, cz), axis_dir=(0.0, 1.0, 0.0), length=1.0, radius=0.6, sides=6)
    # bridge superstructure: y 8.5..16.5, amidships
    m.add_box(0.0, 12.5, -8.0, 9.0, 8.0, 26.0)
    # radar/comms mast atop the bridge, reaching the ~20m overall height
    m.add_prism(center=(0.0, 19.0, -8.0), axis_dir=(0.0, 1.0, 0.0), length=4.0, radius=0.6, sides=6)
    # aft flat deck (helipad marker)
    m.add_box(0.0, 8.5, -45.0, 8.0, 1.0, 16.0)
    # bow deck gun + barrel
    m.add_box(0.0, 9.0, 52.0, 3.0, 2.0, 4.0)
    m.add_prism(center=(0.0, 10.0, 58.0), axis_dir=(0.0, 0.0, 1.0), length=8.0, radius=0.25, sides=6)
    return m


def build_carrier():
    m = Mesh()
    # hull: y 0..14, ~250m long
    m.add_box(0.0, 7.0, 0.0, 32.0, 14.0, 250.0)
    # flight deck: overhangs the hull, thin
    m.add_box(0.0, 14.75, 0.0, 40.0, 1.5, 240.0)
    # island (starboard superstructure), offset toward the deck edge
    m.add_box(14.0, 20.5, -40.0, 8.0, 10.0, 30.0)
    # island mast, reaching the ~30m overall height
    m.add_prism(center=(14.0, 27.0, -40.0), axis_dir=(0.0, 1.0, 0.0), length=4.0, radius=0.6, sides=6)
    return m


def build_fighter():
    m = Mesh()
    # fuselage: y 0.2..1.8, nose toward +Z
    m.add_box(0.0, 1.0, -1.5, 1.8, 1.6, 13.0)
    # nose taper (wedge narrowing toward the tip)
    m.add_tapered_box(0.0, 1.8, 0.2, 1.8, 0.4, 5.0, 8.0)
    # tail fin (small vertical placeholder box)
    m.add_box(0.0, 2.7, -7.0, 0.15, 2.0, 2.2)
    # delta wings (root near mid-fuselage, swept tip)
    add_wing_pair(m, root_x=0.9, root_z_back=-3.5, root_z_front=3.0,
                  span=4.6, tip_z_back=-2.5, tip_z_front=1.0, y0=0.6, y1=1.0)
    return m


def build_bomber():
    m = Mesh()
    # fuselage: y 0.3..3.3, nose toward +Z
    m.add_box(0.0, 1.8, -2.0, 3.0, 3.0, 28.0)
    # nose taper
    m.add_tapered_box(0.0, 3.0, 0.6, 3.0, 1.0, 10.0, 14.0)
    # tail fin (small vertical placeholder box)
    m.add_box(0.0, 5.3, -14.0, 0.2, 4.0, 3.5)
    # straight, broad wings (root at fuselage, tip well outboard, ~32m span overall)
    add_wing_pair(m, root_x=1.5, root_z_back=-2.0, root_z_front=1.5,
                  span=14.5, tip_z_back=-0.5, tip_z_front=0.8, y0=1.4, y1=1.9)
    # 2 underwing engine nacelles
    for sign in (-1.0, 1.0):
        m.add_prism(center=(sign * 7.0, 1.0, -0.5), axis_dir=(0.0, 0.0, 1.0), length=4.0, radius=0.9, sides=8)
    return m


def build_suicide_drone():
    m = Mesh()
    # 4 tiny landing skids
    for sx, sz in ((-0.2, -0.2), (-0.2, 0.2), (0.2, -0.2), (0.2, 0.2)):
        m.add_box(sx, 0.05, sz, 0.05, 0.10, 0.05)
    # flat quad-copter-like body
    m.add_box(0.0, 0.15, 0.0, 2.6, 0.20, 2.6)
    # warhead bulge on the nose (+Z), a short 8-sided "barrel" pointing forward
    m.add_prism(center=(0.0, 0.20, 1.4), axis_dir=(0.0, 0.0, 1.0), length=0.6, radius=0.35, sides=8)
    # 4 small rotor discs at the corners
    for cx, cz in ((0.9, 0.9), (-0.9, 0.9), (0.9, -0.9), (-0.9, -0.9)):
        m.add_prism(center=(cx, 0.28, cz), axis_dir=(0.0, 1.0, 0.0), length=0.03, radius=0.35, sides=6)
    return m


def build_naval_base():
    m = Mesh()
    # main building (warehouse/hangar behind the quay)
    m.add_box(-8.0, 5.0, -10.0, 24.0, 10.0, 14.0)
    # quay/pier extending toward the water (+Z), flat and low
    m.add_box(0.0, 1.0, 8.0, 30.0, 2.0, 16.0)
    # crane: vertical mast + horizontal jib + counter-jib
    m.add_prism(center=(10.0, 7.0, 10.0), axis_dir=(0.0, 1.0, 0.0), length=12.0, radius=0.8, sides=6)
    m.add_box(10.0, 13.0, 17.0, 1.2, 1.2, 12.0)  # jib reaching out over the water
    m.add_box(10.0, 13.0, 4.0, 1.2, 1.2, 4.0)    # counter-jib over the quay
    return m


def build_air_base():
    m = Mesh()
    # main hangar block
    m.add_box(-8.0, 6.0, 0.0, 30.0, 12.0, 22.0)
    # hangar door taper (angled roofline facing the apron, +Z)
    m.add_tapered_box(-8.0, 30.0, 0.0, 12.0, 8.0, 9.0, 11.0)
    # control tower: base block + glazed cab + mast
    m.add_box(16.0, 5.0, -8.0, 6.0, 10.0, 6.0)
    m.add_box(16.0, 10.5, -8.0, 7.0, 1.0, 7.0)
    m.add_prism(center=(16.0, 11.5, -8.0), axis_dir=(0.0, 1.0, 0.0), length=1.0, radius=0.4, sides=6)
    # apron markers (low flat pads in front of the hangar)
    m.add_box(0.0, 0.15, 16.0, 24.0, 0.3, 8.0)
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
    # --- Task61: naval / air forces ---
    ("Unit_Destroyer", build_destroyer, (0.28, 0.30, 0.32), "Missile destroyer: hull+VLS deck+bridge+mast, ~120x14x20m"),
    ("Unit_Carrier", build_carrier, (0.32, 0.33, 0.34), "Aircraft carrier: hull+flat deck+island, ~250x40x30m"),
    ("Unit_Fighter", build_fighter, (0.22, 0.24, 0.26), "Air superiority fighter: fuselage+delta wings+fin, ~16x11x4m"),
    ("Unit_Bomber", build_bomber, (0.24, 0.26, 0.22), "Tactical bomber: fuselage+wings+nacelles, ~35x32x8m"),
    ("Unit_SuicideDrone", build_suicide_drone, (0.35, 0.12, 0.10), "Suicide drone: flat body+warhead bulge+4 rotors, ~3x3x0.8m"),
    ("Building_NavalBase", build_naval_base, (0.20, 0.28, 0.34), "Naval base: warehouse+quay+crane, ~40x30x14m"),
    ("Building_AirBase", build_air_base, (0.34, 0.34, 0.30), "Air base: hangar+control tower+apron, ~50x36x12m"),
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
