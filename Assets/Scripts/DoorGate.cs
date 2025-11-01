using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

public class DoorGate : MonoBehaviour
{
    public Transform[] gateSlots; // 문 바로 안/밖 포인트
    public bool isOpen;

    [Header("감지 설정")]
    [Tooltip("게이트 슬롯 감지 반경(너무 크면 지나가는 승객까지 잡힘)")]
    public float slotRadius = 0.12f;

    [Tooltip("Agent만 감지하려면 Agent 레이어를 설정(비워두면 Tag(\"Agent\")로 필터)")]
    public LayerMask agentLayer; // 0이면 태그 기반

    [Tooltip("감지에서 트리거 콜라이더 제외")]
    public bool ignoreTriggerColliders = true;

    [Header("NavMesh 문 차단(선택)")]
    [Tooltip("문이 닫히면 carve ON / 열리면 carve OFF 해줄 NavMeshObstacle(문틈 차단용)")]
    public NavMeshObstacle[] doorBlockers;

    [Header("게이트 병목/판정")]
    [Tooltip("동시에 슬롯으로 진입 허용할 최대 인원(1 권장)")]
    public int gateThroughput = 1;

    [Tooltip("슬롯에서 이 거리 이상 떨어지면 '통과 완료'로 간주")]
    public float passClearDistance = 0.4f;

    [Header("Door Links(선택)")]
    [Tooltip("문이 열릴 때만 활성화할 OffMeshLink(밖↔안 연결)")]
    public OffMeshLink[] openLinks;

    [Header("Admit 스위치")]
    [Tooltip("이 문으로의 새 진입(슬롯 배정)을 허용할지 여부")]
    public bool admitEnabled = true;

    // ==== 런타임 전용 상태 ====
    [System.NonSerialized] private HashSet<object> openHolders = new HashSet<object>();
    [System.NonSerialized] private float clearSince = -1f;
    [System.NonSerialized] private readonly HashSet<PassengerAgent> inGate = new HashSet<PassengerAgent>();

    private Animator animator;

    void Awake()
    {
        animator = GetComponent<Animator>();
    }

    // ===== 외부 제어 =====
    public void SetAdmitEnabled(bool enabled) => admitEnabled = enabled;

    public void HoldOpen(object owner)
    {
        openHolders.Add(owner);
        isOpen = true;
        if (animator) animator.SetTrigger("open");
        SetDoorBlockers(false);
        SetDoorLinks(true);
    }

    public void ReleaseHold(object owner) => openHolders.Remove(owner);

    public void EnsureOpen()
    {
        if (!isOpen)
        {
            isOpen = true;
            if (animator) animator.SetTrigger("open");
            SetDoorBlockers(false);
            SetDoorLinks(true);
        }
    }

    public void Open()
    {
        isOpen = true;
        if (animator) animator.SetTrigger("open");
        SetDoorBlockers(false);
        SetDoorLinks(true);
    }

    public void Close()
    {
        if (openHolders.Count > 0) return; // 홀드 중엔 닫지 않음
        isOpen = false;
        if (animator) animator.SetTrigger("close");
        SetDoorBlockers(true);
        SetDoorLinks(false);
    }

    // ★ 강제 닫기(타임아웃 등)
    public void ForceClose()
    {
        openHolders.Clear();
        isOpen = false;
        if (animator) animator.SetTrigger("close");
        SetDoorBlockers(true);
        SetDoorLinks(false);
    }

    void SetDoorBlockers(bool closed)
    {
        if (doorBlockers == null) return;
        for (int i = 0; i < doorBlockers.Length; i++)
        {
            var obs = doorBlockers[i];
            if (!obs) continue;
            obs.carving = closed;
            obs.enabled = closed;
            var col = obs.GetComponent<Collider>();
            if (col) col.enabled = closed;
        }
    }

    void SetDoorLinks(bool on)
    {
        if (openLinks == null) return;
        for (int i = 0; i < openLinks.Length; i++)
        {
            var link = openLinks[i];
            if (!link) continue;
            link.activated = on;
        }
    }

    // ===== 감지 & 슬롯 배정 =====
    bool HasAgentAt(Vector3 pos)
    {
        int mask = (agentLayer.value != 0) ? agentLayer.value : Physics.AllLayers;
        var hits = Physics.OverlapSphere(pos, slotRadius, mask, QueryTriggerInteraction.Collide);
        if (hits == null || hits.Length == 0) return false;

        for (int i = 0; i < hits.Length; i++)
        {
            var h = hits[i];
            if (!h) continue;
            if (ignoreTriggerColliders && h.isTrigger) continue;

            var a = h.GetComponentInParent<PassengerAgent>();
            if (a == null) continue;

            // 태그 필터를 쓰고 싶다면 여기서 a.CompareTag("Agent") 확인
            return true;
        }
        return false;
    }

    // forward(파란축)가 '버스 안쪽'
    bool IsOutsideSide(Vector3 agentPos)
    {
        Vector3 toAgent = agentPos - transform.position; toAgent.y = 0f;
        return Vector3.Dot(toAgent, transform.forward) < 0f;
    }
    bool IsInsideSide(Vector3 agentPos)
    {
        Vector3 toAgent = agentPos - transform.position; toAgent.y = 0f;
        return Vector3.Dot(toAgent, transform.forward) > 0f;
    }

    bool TryAdmitCommon(PassengerAgent a)
    {
        if (!isOpen || a == null) return false;
        if (!admitEnabled) return false;
        if (inGate.Count >= gateThroughput) return false;

        foreach (var t in gateSlots)
        {
            if (!HasAgentAt(t.position))
            {
                a.GoToPoint(t.position);
                inGate.Add(a);
                return true;
            }
        }
        return false;
    }

    public bool TryAdmitBoard(PassengerAgent a)
    {
        if (!IsOutsideSide(a.transform.position)) return false;
        return TryAdmitCommon(a);
    }

    // DoorGate.cs 필드 추가
    [Header("Admit 근접 허용")]
    public float admitNearRadius = 0.7f; // 문 중심 ~ 에이전트 거리 이내면 '안쪽' 판정 생략

    // DoorGate.cs TryAdmitAlight 수정
    public bool TryAdmitAlight(PassengerAgent a)
    {
        if (a == null) return false;

        // ★ 문에 매우 가까우면 안/밖 판정 완화
        float dist = Vector3.Distance(a.transform.position, transform.position);
        if (dist > admitNearRadius)
        {
            if (!IsInsideSide(a.transform.position)) return false;
        }

        return TryAdmitCommon(a);
    }


    public bool TryAdmit(PassengerAgent a) => TryAdmitCommon(a);

    public void ReleaseAgent(PassengerAgent a)
    {
        if (a != null) inGate.Remove(a);
    }

    public void FlushGateOccupants()
    {
        var removeList = new List<PassengerAgent>();
        foreach (var a in inGate)
        {
            if (a == null) { removeList.Add(a); continue; }
            float best = float.PositiveInfinity;
            foreach (var s in gateSlots)
            {
                float d = Vector3.Distance(a.transform.position, s.position);
                if (d < best) best = d;
            }
            if (best > passClearDistance) removeList.Add(a);
        }
        for (int i = 0; i < removeList.Count; i++) inGate.Remove(removeList[i]);
    }

    public bool IsClear()
    {
        bool someone = false;
        foreach (var t in gateSlots)
            if (HasAgentAt(t.position)) { someone = true; break; }

        if (someone)
        {
            clearSince = -1f;
            return false;
        }
        if (clearSince < 0f) clearSince = Time.time;
        return (Time.time - clearSince) > 0.2f;
    }

    public bool IsClearStrict()
    {
        FlushGateOccupants();
        if (!IsClear()) return false;
        return inGate.Count == 0;
    }
}
