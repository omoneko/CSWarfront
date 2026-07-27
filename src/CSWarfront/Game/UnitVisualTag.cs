using UnityEngine;
namespace CSWarfront.Game
{
    /// <summary>
    /// ユニットのルートGameObjectに付ける識別タグ（Task31）。クリック選択（Game/UI/UnitSelection）が
    /// raycastヒット先（子の可視性マーカーの場合を含む）から、どの論理ユニット(InstanceId)に属するかを
    /// 逆引きするためだけに使う純粋なデータタグ。ロジックは一切持たない。
    /// 2つ目の辞書（GameObject→InstanceId）を別途保持・同期する必要が無いよう、
    /// GameObject自身にID を持たせる方式にしている。
    /// </summary>
    public class UnitVisualTag : MonoBehaviour
    {
        public uint InstanceId;
    }
}
