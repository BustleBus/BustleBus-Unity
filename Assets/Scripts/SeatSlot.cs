using UnityEngine;

public class SeatSlot : MonoBehaviour
{
    public bool isReserved;
    public PassengerAgent reservedBy;
    public Transform sitAnchor; // 없으면 본인 트랜스폼 사용

    public bool TryReserve(PassengerAgent a)
    {
        if (isReserved) return false;
        isReserved = true; reservedBy = a; return true;
    }

    public void Release(PassengerAgent a)
    {
        if (reservedBy == a) { isReserved = false; reservedBy = null; }
    }

    void OnDrawGizmos()
    {
        Gizmos.color = isReserved ? Color.red : Color.green;
        Gizmos.DrawWireSphere(transform.position, 0.08f);
    }
}
