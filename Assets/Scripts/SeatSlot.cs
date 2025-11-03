using UnityEngine;

public enum SlotKind { Seat, Stand }

public class SeatSlot : MonoBehaviour
{
    public SlotKind kind = SlotKind.Seat;
    public Transform sitAnchor;

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

    void OnDrawGizmos()
    {
        Gizmos.color = (kind == SlotKind.Seat) ? (isReserved ? Color.red : Color.green)
                                               : (isReserved ? new Color(1, 0.5f, 0) : new Color(0.2f, 0.8f, 1));
        Gizmos.DrawWireSphere(Anchor.position, 0.08f);
    }
}
