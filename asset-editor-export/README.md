# asset-editor-export

FBX + texture set for the Cities: Skylines 1 Asset Editor, exported by
`tools/export_asset_editor.py` from `models.blend` (the `Military_Assets` collection,
13 low-poly models).

| File | Contents |
|---|---|
| `<Name>.fbx` | Single mesh, triangulated, 1 unit = 1m |
| `<Name>_d.png` | Palette texture (one color cell per material slot) |

## Model list

| Blender object name | Exported file name |
|---|---|
| 01_Infantry_Squad | InfantrySquad |
| 02_Jeep | Jeep |
| 03_APC | APC |
| 04_Tank | Tank |
| 05_Drone_Operator | DroneOperator |
| 06_Fighter | Fighter |
| 07_Bomber | Bomber |
| 08_Destroyer | MissileDestroyer |
| 09_Carrier | Carrier |
| 10_SPG | SPG |
| 11_Base_Army | BaseArmy |
| 12_Base_Navy | BaseNavy |
| 13_Base_Air | BaseAir |

## About colors

The visible colors are carried entirely by the `_d.png` texture (the Asset Editor does
not read materials/shaders from the FBX). It is a palette image laying out each model's
material-slot colors as a single row of cells, and every triangle's UVs point at the
single center point of its material's cell. Without the texture the model imports as
plain gray, so always keep the `.fbx` and `_d.png` together in the same folder.

## Using them in the Asset Editor

1. Start Cities: Skylines and open the **Asset Editor** from the main menu.
2. **New Asset** → pick a template matching the target kind
   (vehicle-type models = Vehicle, base-type models = Building).
3. In the **Import** pane on the left, choose the exported model name (e.g. `Tank`).
   This folder's contents are already copied to
   `%LOCALAPPDATA%\Colossal Order\Cities_Skylines\Addons\Import\`, so they should
   appear in the list as soon as the Asset Editor opens.
4. After importing, check position/scale and **Save** to turn it into an asset.
5. In CSWarfront's model-assignment UI (kind = vehicle/building), select the saved
   asset.

## If the orientation looks wrong

If a model shows up backwards or mirrored in-game, flip the sign of

```python
FRONT_ROTATION_DEG = -90.0
```

at the top of `tools/export_asset_editor.py` (to `90.0`) and re-run the script
(this rotation aligns Blender's `+X` with the export convention's `-Y` forward; it is
the most drift-prone spot, so it is a commented constant).

## Regenerating

```
"C:\Program Files\Blender Foundation\Blender 5.1\blender.exe" -b models.blend -P tools/export_asset_editor.py
```

`models.blend` itself is never modified (read-only headless run; no save operator is
called). Output overwrites this folder every time. After regenerating, copy the files
to `%LOCALAPPDATA%\Colossal Order\Cities_Skylines\Addons\Import\` again by hand
(there is no auto-copy).
