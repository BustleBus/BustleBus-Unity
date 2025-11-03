using UnityEngine;

public enum SlotKind { Seat, Stand }

public class SeatSlot : MonoBehaviour
{
    public SlotKind kind = SlotKind.Seat;
    public Transform sitAnchor;

    // 입석일 때 바라볼 방향의 Transform 추가
    public Transform standFacing;

    public bool isReserved;
    public PassengerAgent reservedBy;

    public bool TryReserve(PassengerAgent a)
    {
        if (isReserved) return false;
        isReserved = true; reservedBy = a; return true;
    }

    public void Release(PassengerAgent a)
    {
        if (reservedBy == a) { isReserved = false; reservedBy = null; }
    }

    public Transform Anchor => sitAnchor ? sitAnchor : transform;

    // 입석 방향 Anchor를 가져올 때, standFacing이 있으면 사용하고 없으면 Slot의 forward를 사용
    public Vector3 StandingForward
    {
        get { return standFacing ? standFacing.forward : transform.forward; }
    }

    void OnDrawGizmos()
    {
        Gizmos.color = (kind == SlotKind.Seat) ? (isReserved ? Color.red : Color.green)
                                               : (isReserved ? new Color(1, 0.5f, 0) : new Color(0.2f, 0.8f, 1));
        Gizmos.DrawWireSphere(Anchor.position, 0.08f);

        // 입석일 때 Gizmo로 바라보는 방향을 표시
        if (kind == SlotKind.Stand && !isReserved)
        {
            Gizmos.color = new Color(0.2f, 0.8f, 1, 0.5f);
            Gizmos.DrawLine(Anchor.position, Anchor.position + StandingForward * 0.3f);
        }
    }
}