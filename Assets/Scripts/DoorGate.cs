using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

public class DoorGate : MonoBehaviour
{
    public Transform[] gateSlots; // �� �ٷ� ��/�� ����Ʈ
    public bool isOpen;

    [Header("���� ����")]
    [Tooltip("����Ʈ ���� ���� �ݰ�(�ʹ� ũ�� �������� �°����� ����)")]
    public float slotRadius = 0.12f;

    [Tooltip("Agent�� �����Ϸ��� Agent ���̾ ����(����θ� Tag(\"Agent\")�� ����)")]
    public LayerMask agentLayer; // 0�̸� �±� ���

    [Tooltip("�������� Ʈ���� �ݶ��̴� ����")]
    public bool ignoreTriggerColliders = true;

    [Header("NavMesh �� ����(����)")]
    [Tooltip("���� ������ carve ON / ������ carve OFF ���� NavMeshObstacle(��ƴ ���ܿ�)")]
    public NavMeshObstacle[] doorBlockers;

    [Header("����Ʈ ����/����")]
    [Tooltip("���ÿ� �������� ���� ����� �ִ� �ο�(1 ����)")]
    public int gateThroughput = 1;

    [Tooltip("���Կ��� �� �Ÿ� �̻� �������� '��� �Ϸ�'�� ����")]
    public float passClearDistance = 0.4f;

    [Header("Door Links(����)")]
    [Tooltip("���� ���� ���� Ȱ��ȭ�� OffMeshLink(�ۡ�� ����)")]
    public OffMeshLink[] openLinks;

    [Header("Admit ����ġ")]
    [Tooltip("�� �������� �� ����(���� ����)�� ������� ����")]
    public bool admitEnabled = true;

    // ==== ��Ÿ�� ���� ���� ====
    [System.NonSerialized] private HashSet<object> openHolders = new HashSet<object>();
    [System.NonSerialized] private float clearSince = -1f;
    [System.NonSerialized] private readonly HashSet<PassengerAgent> inGate = new HashSet<PassengerAgent>();

    private Animator animator;

    void Awake()
    {
        animator = GetComponent<Animator>();
    }

    // ===== �ܺ� ���� =====
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
        if (openHolders.Count > 0) return; // Ȧ�� �߿� ���� ����
        isOpen = false;
        if (animator) animator.SetTrigger("close");
        SetDoorBlockers(true);
        SetDoorLinks(false);
    }

    // �� ���� �ݱ�(Ÿ�Ӿƿ� ��)
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

    // ===== ���� & ���� ���� =====
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

            // �±� ���͸� ���� �ʹٸ� ���⼭ a.CompareTag("Agent") Ȯ��
            return true;
        }
        return false;
    }

    // forward(�Ķ���)�� '���� ����'
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

    // DoorGate.cs �ʵ� �߰�
    [Header("Admit ���� ���")]
    public float admitNearRadius = 0.7f; // �� �߽� ~ ������Ʈ �Ÿ� �̳��� '����' ���� ����

    // DoorGate.cs TryAdmitAlight ����
    public bool TryAdmitAlight(PassengerAgent a)
    {
        if (a == null) return false;

        // �� ���� �ſ� ������ ��/�� ���� ��ȭ
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
