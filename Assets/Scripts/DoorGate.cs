using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

public class DoorGate : MonoBehaviour
{
    [Header("게이트 슬롯(문 바깥/중앙/안쪽 2~3개 권장)")]
    public Transform[] gateSlots;
    public bool isOpen;

    [Header("문 타입 (승차 전용/하차 전용)")]
    public bool isEntry = false;
    public bool isExit = false;

    [Header("감지/필터")]
    public float slotRadius = 0.12f;
    public LayerMask agentLayer;            // 비워두면 AllLayers
    public bool ignoreTriggerColliders = true;

    [Header("문 닫힘 보조")]
    public NavMeshObstacle[] doorBlockers;
    public OffMeshLink[] openLinks;
    public float passClearDistance = 0.4f;
    public int gateThroughput = 1;

    [Header("Admit/사이드 체크")]
    public float admitNearRadius = 0.7f;
    public bool admitEnabled = true;

    [Header("애니메이터")]
    public Animator animator;
    static readonly int ID_openTrig = Animator.StringToHash("open");
    static readonly int ID_closeTrig = Animator.StringToHash("close");
    static readonly int ID_OpenBool = Animator.StringToHash("Open");
    static readonly int ID_OpenedBool = Animator.StringToHash("Opened");
    static readonly int ID_ClosedBool = Animator.StringToHash("Closed");

    private readonly HashSet<object> openHolders = new();
    private readonly HashSet<PassengerAgent> inGate = new();
    private float clearSince = -1f;

    void Awake()
    {
        if (!animator) animator = GetComponent<Animator>();

        // 초기 상태: NavMesh Obstacle 비활성화 (문이 닫혀있더라도 초기 경로 문제가 없도록 함)
        SetDoorBlockers(false);
        SetDoorLinks(false);
    }
    void Update()
    {
        // 문이 열려있는 상태인데, Obstacle이 활성화되어 있다면 강제로 비활성화
        if (isOpen)
        {
            if (doorBlockers != null)
            {
                foreach (var obs in doorBlockers)
                {
                    if (obs != null && obs.enabled)
                    {
                        // 문이 열렸는데도 막고 있다면 강제 해제 (초기화 오류 방지)
                        SetDoorBlockers(false);
                        break;
                    }
                }
            }
        }
    }
    // StopController에서 문 타입을 설정하기 위한 함수
    public void SetEntryExit(bool isEntry, bool isExit) { this.isEntry = isEntry; this.isExit = isExit; }

    public void SetAdmitEnabled(bool enabled) => admitEnabled = enabled;
    public void HoldOpen(object owner) { openHolders.Add(owner); EnsureOpen(); }
    public void ReleaseHold(object owner) { openHolders.Remove(owner); }

    public void EnsureOpen()
    {
        if (isOpen) return;
        isOpen = true;
        PlayOpenAnim(); SetDoorBlockers(false); SetDoorLinks(true);
    }
    public void Open()
    {
        isOpen = true;
        PlayOpenAnim();
        SetDoorBlockers(false); // ★ 문이 열릴 때 Obstacle 비활성화
        SetDoorLinks(true);
    }
    public void Close()
    {
        if (openHolders.Count > 0) return;
        isOpen = false;
        PlayCloseAnim(); SetDoorBlockers(true); SetDoorLinks(false);
    }
    public void ForceCloseNow(string reason = "")
    {
        openHolders.Clear();
        isOpen = false;
        PlayCloseAnim(true);
        SetDoorBlockers(true); SetDoorLinks(false);
        admitEnabled = false;
        inGate.Clear();
    }

    void PlayOpenAnim()
    {
        if (!animator) return;
        if (HasParam(animator, ID_openTrig)) animator.SetTrigger(ID_openTrig);
        if (HasParam(animator, ID_OpenBool)) animator.SetBool(ID_OpenBool, true);
        if (HasParam(animator, ID_OpenedBool)) animator.SetBool(ID_OpenedBool, true);
        if (HasParam(animator, ID_ClosedBool)) animator.SetBool(ID_ClosedBool, false);
    }
    void PlayCloseAnim(bool force = false)
    {
        if (!animator) return;
        if (HasParam(animator, ID_closeTrig)) animator.SetTrigger(ID_closeTrig);
        if (HasParam(animator, ID_OpenBool)) animator.SetBool(ID_OpenBool, false);
        if (HasParam(animator, ID_OpenedBool)) animator.SetBool(ID_OpenedBool, false);
        if (HasParam(animator, ID_ClosedBool)) animator.SetBool(ID_ClosedBool, true);
    }
    static bool HasParam(Animator a, int id) { foreach (var p in a.parameters) if (p.nameHash == id) return true; return false; }

    void SetDoorBlockers(bool closed)
    {
        if (doorBlockers == null) return;
        foreach (var obs in doorBlockers)
        {
            if (!obs) continue;
            // closed가 true면 활성화 (닫힘), closed가 false면 비활성화 (열림)
            obs.enabled = closed; obs.carving = closed;
            var col = obs.GetComponent<Collider>(); if (col) col.enabled = closed;
        }
    }

    // NavMesh Link 제어 함수 (StopController에서 경로 차단 목적으로 재사용)
    public void SetDoorLinks(bool on) { if (openLinks == null) return; foreach (var link in openLinks) if (link) link.activated = on; }

    int LM() => (agentLayer.value != 0) ? agentLayer.value : Physics.AllLayers;

    bool HasAgentAt(Vector3 pos)
    {
        var hits = Physics.OverlapSphere(
            pos, slotRadius, LM(),
            ignoreTriggerColliders ? QueryTriggerInteraction.Ignore : QueryTriggerInteraction.Collide
        );
        if (hits == null) return false;
        foreach (var h in hits)
        {
            if (!h) continue;
            if (ignoreTriggerColliders && h.isTrigger) continue;
            if (h.GetComponentInParent<PassengerAgent>() != null) return true;
        }
        return false;
    }

    // ★ 문 주변 광역 감지(슬롯 바깥 반경까지): PassengerAgent가 반경 내에 하나라도 있으면 true
    public bool HasAgentsNear(Transform transform, float radius)
    {
        var hits = Physics.OverlapSphere(
            transform.position, radius, LM(),
            ignoreTriggerColliders ? QueryTriggerInteraction.Ignore : QueryTriggerInteraction.Collide
        );
        if (hits == null) return false;
        foreach (var h in hits)
        {
            if (!h) continue;
            if (ignoreTriggerColliders && h.isTrigger) continue;
            var ag = h.GetComponentInParent<PassengerAgent>();
            if (ag != null)
            {
                // 이미 바깥에서 제거 대기 중이어도 반경 안이면 true
                return true;
            }
        }
        return false;
    }

    bool IsOutsideSide(Vector3 p) { Vector3 to = p - transform.position; to.y = 0f; return Vector3.Dot(to, transform.forward) < 0f; }
    bool IsInsideSide(Vector3 p) { Vector3 to = p - transform.position; to.y = 0f; return Vector3.Dot(to, transform.forward) > 0f; }

    Transform PickMostCentralFreeSlot()
    {
        if (gateSlots == null || gateSlots.Length == 0) return null;
        Transform best = null; float bestScore = float.PositiveInfinity;
        foreach (var s in gateSlots)
        {
            if (!s) continue;
            if (HasAgentAt(s.position)) continue;
            Vector3 local = transform.InverseTransformPoint(s.position);
            float lateral = Mathf.Abs(local.x);  // 좌우 중앙성
            float depth = Mathf.Abs(local.z);
            float score = lateral * 10f + depth;
            if (score < bestScore) { bestScore = score; best = s; }
        }
        return best;
    }

    bool TryAdmitCore(PassengerAgent a)
    {
        if (!isOpen || a == null || !admitEnabled) return false;
        if (inGate.Count >= gateThroughput) return false;

        var slot = PickMostCentralFreeSlot();
        if (!slot) return false;

        a.GoToPoint(slot.position);
        inGate.Add(a);
        return true;
    }

    public bool TryAdmitBoard(PassengerAgent a)
    {
        if (!isEntry) return false; // 승차 전용 문이 아니면 거부
        if (a == null) return false;
        float dist = Vector3.Distance(a.transform.position, transform.position);
        if (dist > admitNearRadius && !IsOutsideSide(a.transform.position)) return false;
        return TryAdmitCore(a);
    }
    public bool TryAdmitAlight(PassengerAgent a)
    {
        if (!isExit) return false; // 하차 전용 문이 아니면 거부
        if (a == null) return false;
        float dist = Vector3.Distance(a.transform.position, transform.position);
        if (dist > admitNearRadius && !IsInsideSide(a.transform.position)) return false;
        return TryAdmitCore(a);
    }

    public void ReleaseAgent(PassengerAgent a) { if (a != null) inGate.Remove(a); }

    public void FlushGateOccupants()
    {
        if (gateSlots == null) return;
        var remove = new List<PassengerAgent>();
        foreach (var ag in inGate)
        {
            if (!ag) { remove.Add(ag); continue; }
            float best = float.PositiveInfinity;
            foreach (var s in gateSlots) if (s)
                {
                    float d = Vector3.Distance(ag.transform.position, s.position);
                    best = Mathf.Min(best, d);
                }
            if (best > passClearDistance) remove.Add(ag);
        }
        foreach (var r in remove) inGate.Remove(r);
    }

    public bool IsClear()
    {
        bool busy = false;
        if (gateSlots != null)
        {
            foreach (var s in gateSlots)
            {
                if (!s) continue;
                var hits = Physics.OverlapSphere(
                    s.position, slotRadius, LM(),
                    ignoreTriggerColliders ? QueryTriggerInteraction.Ignore : QueryTriggerInteraction.Collide
                );
                if (hits == null) continue;
                foreach (var h in hits)
                {
                    if (!h) continue;
                    if (ignoreTriggerColliders && h.isTrigger) continue;
                    if (h.GetComponentInParent<PassengerAgent>() != null) { busy = true; break; }
                }
                if (busy) break;
            }
        }
        if (busy) { clearSince = -1f; return false; }
        if (clearSince < 0f) clearSince = Time.time;
        return (Time.time - clearSince) > 0.15f;
    }

    public bool IsClearStrict()
    {
        FlushGateOccupants();
        if (!IsClear()) return false;
        return inGate.Count == 0;
    }
}