# asset-editor-export

`tools/export_asset_editor.py` が `models.blend`（`Military_Assets` コレクション、
13体の低ポリモデル）から書き出した、Cities: Skylines 1 の Asset Editor 用
FBX + テクスチャ一式です。

| ファイル | 内容 |
|---|---|
| `<Name>.fbx` | 単一メッシュ、三角形化済み、1ユニット=1m |
| `<Name>_d.png` | パレットテクスチャ（マテリアルスロットごとに1色セル） |

## モデル一覧

| Blenderオブジェクト名 | 書き出しファイル名 |
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

## 色について

見た目の色は `_d.png` テクスチャだけが担っています（Asset Editor は
FBX内のマテリアル/シェーダーを読みません）。各モデルのマテリアルスロット
の色を横一列のセルに並べたパレット画像で、各三角形のUVはそのマテリアルの
セル中心1点を指しています。テクスチャが揃っていないと真っ灰色でインポート
されるので、`.fbx` と `_d.png` は必ずペアで同じフォルダに置いてください。

## Asset Editorでの使い方

1. Cities: Skylines を起動し、メインメニューから **アセットエディタ**
   （Asset Editor）を開く。
2. **新規アセット** → 対象種別に応じてテンプレートを選択
   （車両系モデル = ビークル、拠点系モデル = 建物）。
3. 左側の **Import** 欄で、書き出したモデル名（例: `Tank`）を選ぶ。
   このフォルダの内容は既に
   `%LOCALAPPDATA%\Colossal Order\Cities_Skylines\Addons\Import\` に
   コピー済みなので、Asset Editorを開けばそのまま一覧に出るはず。
4. 取り込んだら位置・スケールを確認し、**保存**でアセット化する。
5. CSWarfrontのモデル割当UI（種別 = 車両/建物）で、保存したアセットを選択する。

## 向きがおかしいとき

モデルがゲーム内で後ろ向き・鏡像で表示される場合は、
`tools/export_asset_editor.py` 冒頭の

```python
FRONT_ROTATION_DEG = -90.0
```

の符号を反転（`90.0`）してスクリプトを再実行してください
（Blenderの `+X` を書き出し規約の `-Y` 前方に合わせるための回転で、
一番ズレやすいポイントとしてコメント付きで定数化してあります）。

## 再生成

```
"C:\Program Files\Blender Foundation\Blender 5.1\blender.exe" -b models.blend -P tools/export_asset_editor.py
```

`models.blend` 自体は書き換えません（読み込み専用でヘッドレス実行、
保存オペレータは呼んでいません）。出力は毎回このフォルダに上書きされます。
再生成後は、`%LOCALAPPDATA%\Colossal Order\Cities_Skylines\Addons\Import\`
へ手動でコピーし直してください（自動コピーはしていません）。
