using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

public class StopController : MonoBehaviour
{
    [Header("�뷮/����")]
    public int capacity = 40;

    [Tooltip("ž�¹�(������) - forward�� ���� ������ ���ؾ� ��")]
    public DoorGate doorIn;

    [Tooltip("������ - forward�� ���� ������ ���ؾ� ��")]
    public DoorGate doorOut;

    [Tooltip("���� ������ ������ ���� ������ ���� ����Ʈ (NavMesh ��)")]
    public Transform exitPointOutside;

    [Tooltip("��⿭ �ʱ� ���� ����Ʈ ��Ʈ (�ڽ�: Q_00, Q_01 ��)")]
    public Transform queueRootOutside;

    [Tooltip("�� �ٷ� �� ���� ���ڸ� (������ queueRootOutside ù �ڽ� ���)")]
    public Transform entryPoint;

    public List<SeatSlot> seatSlots = new();

    [Header("������Ʈ Ǯ/����Ʈ (��Ÿ�� ����)")]
    [System.NonSerialized] public List<PassengerAgent> insideAgents = new();
    [System.NonSerialized, HideInInspector] public List<PassengerAgent> outsideQueue = new();

    [Header("Ÿ�̹�")]
    public float gateInterval = 0.25f;

    [Header("����(����)")]
    public float cruiseTimeMin = 3.0f;
    public float cruiseTimeMax = 7.0f;

    [Header("����(����)")]
    public Animator busAnimator;

    [Header("����")]
    public PassengerAgent agentPrefab;

    [Header("�����庰 ���� ž�� ��")]
    public int boardMinPerStop = 1;
    public int boardMaxPerStop = 5;

    [Header("���� �� ��⿭ ����(����)")]
    public int spawnMinPerStop = 0;
    public int spawnMaxPerStop = 5;
    public int outsideQueueMax = 30;
    public float spawnBackSpacing = 1f;

    [Header("���� ������(��)")]
    public float alightWindowSeconds = 6f; // ���� ��� �ð�
    public float boardWindowSeconds = 8f; // ž�� ��� �ð�

    // === ���� 1ȸ�� ���� ž�� ��ǥ/���� ===
    [System.NonSerialized] int boardingGoalThisStop = 0;
    [System.NonSerialized] int boardedThisStop = 0;
    [System.NonSerialized] bool boardingFinished = false;

    void Start()
    {
        if (entryPoint == null && queueRootOutside != null && queueRootOutside.childCount > 0)
            entryPoint = queueRootOutside.GetChild(0);

        outsideQueue.Clear();

        if (queueRootOutside != null && queueRootOutside.childCount > 0)
        {
            foreach (Transform q in queueRootOutside)
            {
                var a = SpawnAgentAt(q.position);
                AddToQueue(a);
            }
        }
        else
        {
            Vector3 basePos = entryPoint != null ? entryPoint.position : transform.position;
            for (int i = 0; i < 3; i++)
            {
                var a = SpawnAgentAt(basePos + (-transform.forward * (1.0f + 0.6f * i)));
                AddToQueue(a);
            }
        }

        StartCoroutine(MainLoop());
    }

    PassengerAgent SpawnAgentAt(Vector3 pos)
    {
        PassengerAgent a;
        if (agentPrefab != null)
        {
            a = Instantiate(agentPrefab, SampleOnNavMesh(pos), Quaternion.identity);
            a.name = $"Agent_{a.GetInstanceID()}";
        }
        else
        {
            var go = new GameObject($"Agent_{Random.Range(1000, 9999)}");
            var nav = go.AddComponent<NavMeshAgent>();
            nav.radius = 0.2f; nav.speed = 1.4f; nav.acceleration = 5f; nav.angularSpeed = 420f;
            go.transform.position = SampleOnNavMesh(pos);

            var vis = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            vis.name = "Visual";
            vis.transform.SetParent(go.transform, false);
            vis.transform.localPosition = Vector3.zero;
            vis.transform.localScale = new Vector3(0.5f, 0.9f, 0.5f);
            var col = vis.GetComponent<Collider>(); if (col) col.isTrigger = true;

            a = go.AddComponent<PassengerAgent>();
        }

        a.tag = "Agent";
        a.OnAgentDestroyed += HandleAgentDestroyed;
        return a;
    }

    Vector3 SampleOnNavMesh(Vector3 pos)
    {
        if (NavMesh.SamplePosition(pos, out var hit, 1.5f, NavMesh.AllAreas))
            return hit.position;
        Debug.LogWarning($"[StopController] NavMesh ���� ����: {pos} (NavMesh ���� �� ����)");
        return pos;
    }

    IEnumerator MainLoop()
    {
        while (true)
        {
            float cruise = Random.Range(cruiseTimeMin, cruiseTimeMax);
            SetDriving(true);
            yield return new WaitForSeconds(cruise);

            SetDriving(false);
            yield return ArriveStopOnce();
        }
    }

    void SetDriving(bool driving)
    {
        if (busAnimator != null) busAnimator.SetBool("isDriving", driving);
    }

    IEnumerator ArriveStopOnce()
    {
        // �� �� ���� + Admit ���
        if (doorOut) { doorOut.HoldOpen(this); doorOut.SetAdmitEnabled(true); doorOut.Open(); }
        if (doorIn) { doorIn.HoldOpen(this); doorIn.SetAdmitEnabled(true); doorIn.Open(); }

        // ��⿭ ���� ����
        if (spawnMaxPerStop >= spawnMinPerStop && spawnMaxPerStop > 0)
        {
            int spawnN = Random.Range(spawnMinPerStop, spawnMaxPerStop + 1);
            SpawnAndQueueN(spawnN);
        }

        // ������ ���� + �غ�
        SelectAlighters();
        insideAgents.RemoveAll(x => x == null);
        foreach (var a in insideAgents)
        {
            if (a != null && a.willAlightHere)
            {
                a.exitDoor = doorOut;
                a.BeginPrepareAlight();
            }
        }

        // �̹� ���� ��ǥ ž�� ��
        int wish = (boardMaxPerStop >= boardMinPerStop)
            ? Random.Range(boardMinPerStop, boardMaxPerStop + 1)
            : Mathf.Max(0, boardMinPerStop);

        int capacityLeft = Mathf.Max(0, capacity - insideAgents.Count);
        int possible = Mathf.Min(wish, capacityLeft, outsideQueue.Count);

        boardingGoalThisStop = possible;
        boardedThisStop = 0;
        boardingFinished = (possible == 0);

        // ���� ����: ����(������), ž��(��ǥ+������)
        var alightRoutine = StartCoroutine(Co_AlightFlowWindowed(alightWindowSeconds));
        var boardRoutine = StartCoroutine(Co_BoardFlow(boardingGoalThisStop, boardWindowSeconds));

        // ���� ������ ���� ���
        yield return alightRoutine;

        // ž�� ��ǥ �Ϸ� ���
        yield return new WaitUntil(() => boardingFinished);

        // ��� ž�°��� Riding ����(�¼� or �Լ� ��ġ) ���� üũ(ª��)
        yield return new WaitForSeconds(0.1f);

        // �� �ݱ�(��ȭ ��ƾ)
        float closeTimeout = 2.0f;
        yield return CloseDoorSafely(doorOut, closeTimeout);
        yield return CloseDoorSafely(doorIn, closeTimeout);
    }

    // ����: ������ ���� �� �㰡��, ���� ��� �� �ٷ� ���� ����Ʈ���� ����(�� �ݱ� ��� �� ��)
    IEnumerator Co_AlightFlowWindowed(float windowSeconds)
    {
        if (doorOut) { doorOut.SetAdmitEnabled(true); doorOut.EnsureOpen(); }

        insideAgents.RemoveAll(x => x == null);
        List<PassengerAgent> alighters = insideAgents.FindAll(x => x != null && x.willAlightHere);

        float deadline = Time.time + Mathf.Max(0.5f, windowSeconds);
        int idx = 0;

        while (idx < alighters.Count && Time.time < deadline)
        {
            var a = alighters[idx++];
            if (a == null) continue;

            if (doorOut != null)
            {
                doorOut.EnsureOpen();

                // �� �� ���� ��������� ���� ���̱�
                Vector3 innerWait = doorOut.transform.position + doorOut.transform.forward * 0.45f;
                a.GoToPoint(innerWait);
                yield return new WaitUntil(() => Vector3.Distance(a.transform.position, innerWait) < 0.35f || a.Reached(0.2f));

                // �� ���� ���� �㰡
                yield return new WaitUntil(() => doorOut.TryAdmitAlight(a));
                yield return new WaitUntil(() => a.Reached());

                // ���� ��� �� �� ��¦
                Transform nearestSlot = null; float best = 9e9f;
                foreach (var s in doorOut.gateSlots)
                {
                    float d = (s.position - a.transform.position).sqrMagnitude;
                    if (d < best) { best = d; nearestSlot = s; }
                }
                if (nearestSlot != null)
                {
                    Vector3 passOutside = nearestSlot.position - doorOut.transform.forward * 0.7f;
                    a.GoToPoint(passOutside);
                    yield return new WaitUntil(() =>
                        Vector3.Distance(a.transform.position, nearestSlot.position) > doorOut.passClearDistance || a.Reached(0.2f));
                    doorOut.ReleaseAgent(a);
                }
            }

            // �� ���⼭���ʹ� ���� ����: �ܺη� ������, ���� ���� ����Ʈ������ ��� ����
            if (exitPointOutside != null) a.BeginAlight(exitPointOutside.position);

            RemoveInside(a);      // ���� ��� ��� ����
            // �ı��� ����(������ ���� �ΰ� ���߿� ����)
            StartCoroutine(Co_RecycleWhenFar(a, 3f));

            yield return new WaitForSeconds(gateInterval);
        }

        // �� ���� �㰡 �ߴ�
        if (doorOut) doorOut.SetAdmitEnabled(false);
    }

    // �ٱ����� ����� �������� ����(�� �ݱ�� ����)
    IEnumerator Co_RecycleWhenFar(PassengerAgent a, float minDistance)
    {
        if (a == null || exitPointOutside == null) yield break;
        while (a != null && Vector3.Distance(a.transform.position, exitPointOutside.position) > 0.4f)
        {
            // ������ġ: �ʹ� ������ ��� �� ���� �ָ� ����
            if (Vector3.Distance(a.transform.position, exitPointOutside.position) < minDistance)
                a.GoToPoint(exitPointOutside.position);
            yield return null;
        }
        if (a) Destroy(a.gameObject);
    }

    // ž��: ��ǥ ��/������ �������� �㰡
    IEnumerator Co_BoardFlow(int targetCount, float windowSeconds)
    {
        if (doorIn) { doorIn.SetAdmitEnabled(true); doorIn.EnsureOpen(); }

        CleanOutsideQueue();

        int capacityLeft = Mathf.Max(0, capacity - insideAgents.Count);
        int canTake = Mathf.Min(targetCount, capacityLeft, outsideQueue.Count);

        float deadline = Time.time + Mathf.Max(0.5f, windowSeconds);
        int boarded = 0;

        while (Time.time < deadline && boarded < canTake &&
               insideAgents.Count < capacity && outsideQueue.Count > 0)
        {
            var p = outsideQueue[0];
            outsideQueue.RemoveAt(0);
            if (p == null) { RebindQueueAnchors(0); continue; }

            RebindQueueAnchors(0);

            p.entryDoor = doorIn;
            p.BeginBoard(doorIn);

            if (doorIn != null)
            {
                yield return new WaitUntil(() => doorIn.TryAdmitBoard(p));
                yield return new WaitUntil(() => p.Reached());

                // ���� �������� �� ��¦
                Transform nearestSlot = null; float best = 9e9f;
                foreach (var s in doorIn.gateSlots)
                {
                    float d = (s.position - p.transform.position).sqrMagnitude;
                    if (d < best) { best = d; nearestSlot = s; }
                }
                if (nearestSlot != null)
                {
                    Vector3 passThrough = nearestSlot.position + doorIn.transform.forward * 0.7f;
                    p.GoToPoint(passThrough);
                    yield return new WaitUntil(() =>
                        Vector3.Distance(p.transform.position, nearestSlot.position) > doorIn.passClearDistance || p.Reached(0.2f));
                    doorIn.ReleaseAgent(p);
                }
            }

            // �¼� ����
            var slot = FindNearestFreeSlot(p.transform.position);
            if (slot != null) p.BeginRide(slot);
            else
            {
                Vector3 safeInside = doorIn != null
                    ? doorIn.transform.position + doorIn.transform.forward * 0.9f
                    : p.transform.position + transform.forward * 0.9f;
                p.BeginRide(null);
                p.GoToPoint(safeInside);
            }
            insideAgents.Add(p);

            boarded++;
            boardedThisStop++;

            yield return new WaitForSeconds(gateInterval);
        }

        if (doorIn) doorIn.SetAdmitEnabled(false);
        boardingFinished = true;
    }

    // ====== ��ƿ ======
    SeatSlot FindNearestFreeSlot(Vector3 from)
    {
        float best = float.MaxValue; SeatSlot bestSlot = null;
        foreach (var s in seatSlots)
        {
            if (!s.isReserved)
            {
                float d = (s.transform.position - from).sqrMagnitude;
                if (d < best) { best = d; bestSlot = s; }
            }
        }
        return bestSlot;
    }

    void RemoveInside(PassengerAgent a)
    {
        if (a != null && a.mySeatOrStand) a.mySeatOrStand.Release(a);
        insideAgents.Remove(a);
    }

    void HandleAgentDestroyed(PassengerAgent a)
    {
        insideAgents.Remove(a);
        int idx = outsideQueue.IndexOf(a);
        if (idx >= 0)
        {
            outsideQueue.RemoveAt(idx);
            RebindQueueAnchors(idx);
        }
    }

    void SelectAlighters()
    {
        insideAgents.RemoveAll(x => x == null);
        foreach (var a in insideAgents)
            a.willAlightHere = (Random.value < 0.3f);
    }

    public void AddToQueue(PassengerAgent agent)
    {
        if (agent == null) return;

        outsideQueue.Add(agent);
        int idx = outsideQueue.Count - 1;

        if (idx == 0)
        {
            agent.ClearQueueFollow();
            agent.BeginQueue(entryPoint != null ? entryPoint : agent.transform);
        }
        else
        {
            var front = outsideQueue[idx - 1];
            agent.BeginQueue(entryPoint != null ? entryPoint : agent.transform);
            agent.SetQueueFollowTarget(front.transform);
        }
    }

    void CleanOutsideQueue()
    {
        bool changed = outsideQueue.RemoveAll(x => x == null) > 0;
        if (changed) RebindQueueAnchors(0);
    }

    void SpawnAndQueueN(int n)
    {
        if (n <= 0) return;
        if (outsideQueueMax > 0)
            n = Mathf.Min(n, Mathf.Max(0, outsideQueueMax - outsideQueue.Count));
        if (n <= 0) return;

        Vector3 basePos = (entryPoint != null ? entryPoint.position : transform.position);
        Vector3 backDir = (entryPoint != null ? -entryPoint.forward : -transform.forward);

        int startIndex = outsideQueue.Count;
        for (int i = 0; i < n; i++)
        {
            int queueIndex = startIndex + i;
            Vector3 pos = basePos + backDir * (1.0f + spawnBackSpacing * (queueIndex + 1));
            var a = SpawnAgentAt(pos);
            AddToQueue(a);
        }
    }

    void RebindQueueAnchors(int startIndex)
    {
        if (outsideQueue.Count == 0) return;

        for (int i = Mathf.Max(0, startIndex); i < outsideQueue.Count; i++)
        {
            var agent = outsideQueue[i];
            if (agent == null) continue;

            if (i == 0)
            {
                agent.ClearQueueFollow();
                agent.BeginQueue(entryPoint != null ? entryPoint : agent.transform);
                agent.SendMessage("OnQueueTargetRebound", SendMessageOptions.DontRequireReceiver);
            }
            else
            {
                var front = outsideQueue[i - 1];
                if (front != null)
                {
                    agent.SetQueueFollowTarget(front.transform);
                }
                else
                {
                    int j = i - 1;
                    while (j >= 0 && outsideQueue[j] == null) j--;
                    if (j >= 0 && outsideQueue[j] != null)
                        agent.SetQueueFollowTarget(outsideQueue[j].transform);
                    else
                    {
                        agent.ClearQueueFollow();
                        agent.BeginQueue(entryPoint != null ? entryPoint : agent.transform);
                    }
                }
                agent.SendMessage("OnQueueTargetRebound", SendMessageOptions.DontRequireReceiver);
            }
        }
    }

    // ===== �� �ݱ� ���� ��ƾ =====
    IEnumerator CloseDoorSafely(DoorGate door, float timeout)
    {
        if (door == null) yield break;

        float t0 = Time.time;

        while ((Time.time - t0) < timeout)
        {
            door.FlushGateOccupants();

            if (door.IsClearStrict()) break;

            NudgeAgentsAwayFromSlots(door, 0.4f);

            yield return null;
        }

        // ������ �õ�
        door.FlushGateOccupants();
        if (door.IsClearStrict())
        {
            door.Close();
        }
        else
        {
            // �� ���� ������ ���� �ݱ�
            door.ForceClose();
        }
        door.ReleaseHold(this);
    }

    void NudgeAgentsAwayFromSlots(DoorGate door, float distance)
    {
        if (door == null || door.gateSlots == null) return;

        for (int i = 0; i < door.gateSlots.Length; i++)
        {
            var s = door.gateSlots[i];
            var hits = Physics.OverlapSphere(s.position, door.slotRadius * 1.2f, ~0, QueryTriggerInteraction.Collide);
            if (hits == null) continue;

            foreach (var h in hits)
            {
                if (!h || !h.CompareTag("Agent")) continue;
                var a = h.GetComponentInParent<PassengerAgent>();
                if (a == null) continue;

                Vector3 toAgent = a.transform.position - door.transform.position; toAgent.y = 0f;
                bool isInside = Vector3.Dot(toAgent, door.transform.forward) > 0f;
                Vector3 dir = isInside ? door.transform.forward : -door.transform.forward;

                Vector3 target = a.transform.position + dir.normalized * distance;
                a.GoToPoint(target);
            }
        }
    }
}
