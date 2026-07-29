"""Throwaway winding validator (Task57) - not shipped. Checks that Newell-computed
face normals for our generated boxes/prisms point outward from their local shape
center. Consumed via `python tools/validate_winding.py`."""


def parse_obj(path):
    verts = []
    faces = []
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if line.startswith("v "):
                parts = line.split()
                verts.append(tuple(float(x) for x in parts[1:4]))
            elif line.startswith("f "):
                parts = line.split()
                faces.append([int(x) - 1 for x in parts[1:]])
    return verts, faces


def newell_normal(poly):
    n = [0.0, 0.0, 0.0]
    cnt = len(poly)
    for i in range(cnt):
        a = poly[i]
        b = poly[(i + 1) % cnt]
        n[0] += (a[1] - b[1]) * (a[2] + b[2])
        n[1] += (a[2] - b[2]) * (a[0] + b[0])
        n[2] += (a[0] - b[0]) * (a[1] + b[1])
    return tuple(n)


def centroid(poly):
    n = len(poly)
    return tuple(sum(p[i] for p in poly) / n for i in range(3))


def sub(a, b):
    return tuple(a[i] - b[i] for i in range(3))


def add(a, b):
    return tuple(a[i] + b[i] for i in range(3))


def scl(a, s):
    return tuple(a[i] * s for i in range(3))


def dotp(a, b):
    return sum(a[i] * b[i] for i in range(3))


def check_infantry():
    verts, faces = parse_obj("src/CSWarfront/Models/Unit_Infantry.obj")
    box_centers = [(-0.18, 0.50, 0.0), (0.18, 0.50, 0.0), (0.0, 1.425, 0.0), (0.0, 2.025, 0.0)]
    bad = 0
    for bi, center in enumerate(box_centers):
        for fi in range(6):
            face = faces[bi * 6 + fi]
            poly = [verts[i] for i in face]
            c = centroid(poly)
            n = newell_normal(poly)
            outward = sub(c, center)
            d = dotp(n, outward)
            if d <= 0:
                bad += 1
                print("BAD infantry box face", bi, fi, "dot=", d)
    print("Infantry box faces checked:", len(box_centers) * 6, "bad:", bad)


def check_tank_barrel():
    verts, faces = parse_obj("src/CSWarfront/Models/Unit_Tank.obj")
    barrel_faces = faces[-8:]
    axis = (0.0, 0.0, 1.0)
    center = (0.0, 1.95, 2.6)
    bad = 0
    for fi, face in enumerate(barrel_faces):
        poly = [verts[i] for i in face]
        c = centroid(poly)
        n = newell_normal(poly)
        if fi < 2:
            # face 0 = start cap (normal -axis_dir), face 1 = end cap (normal +axis_dir)
            outward = tuple(-a for a in axis) if fi == 0 else axis
        else:
            t = dotp(sub(c, center), axis)
            axis_pt = add(center, scl(axis, t))
            outward = sub(c, axis_pt)
        d = dotp(n, outward)
        if d <= 0:
            bad += 1
            print("BAD tank barrel face", fi, "d=", d)
    print("Tank barrel faces checked:", len(barrel_faces), "bad:", bad)


def check_all_models_are_locally_outward():
    """Weaker global check for every model: every face's normal should point
    away from the overall mesh centroid on average (catches gross sign flips
    even for composite/asymmetric shapes)."""
    import os
    names = [
        "Unit_Infantry", "Unit_MechInfantry", "Unit_Apc", "Unit_Tank",
        "Unit_Artillery", "Unit_AntiAir", "Unit_Drone", "Building_MilitaryBase",
    ]
    for name in names:
        path = os.path.join("src", "CSWarfront", "Models", name + ".obj")
        verts, faces = parse_obj(path)
        mesh_centroid = tuple(sum(v[i] for v in verts) / len(verts) for i in range(3))
        pos = 0
        neg = 0
        for face in faces:
            poly = [verts[i] for i in face]
            c = centroid(poly)
            n = newell_normal(poly)
            outward = sub(c, mesh_centroid)
            d = dotp(n, outward)
            if d >= 0:
                pos += 1
            else:
                neg += 1
        print("%-24s faces=%3d outward-ish=%3d inward-ish=%3d" % (name, len(faces), pos, neg))


if __name__ == "__main__":
    check_infantry()
    check_tank_barrel()
    check_all_models_are_locally_outward()
