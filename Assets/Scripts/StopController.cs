using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

public class StopController : MonoBehaviour
{
    [Header("용량/참조")]
    public int capacity = 40;

    [Tooltip("탑승문(승차문) - forward가 버스 안쪽을 향해야 함")]
    public DoorGate doorIn;

    [Tooltip("하차문 - forward가 버스 안쪽을 향해야 함")]
    public DoorGate doorOut;

    [Tooltip("버스 밖으로 완전히 나간 것으로 보는 포인트 (NavMesh 위)")]
    public Transform exitPointOutside;

    [Tooltip("대기열 초기 스폰 포인트 루트 (자식: Q_00, Q_01 …)")]
    public Transform queueRootOutside;

    [Tooltip("문 바로 앞 가장 앞자리 (없으면 queueRootOutside 첫 자식 사용)")]
    public Transform entryPoint;

    public List<SeatSlot> seatSlots = new();

    [Header("에이전트 풀/리스트 (런타임 전용)")]
    [System.NonSerialized] public List<PassengerAgent> insideAgents = new();
    [System.NonSerialized, HideInInspector] public List<PassengerAgent> outsideQueue = new();

    [Header("타이밍")]
    public float gateInterval = 0.25f;

    [Header("주행(랜덤)")]
    public float cruiseTimeMin = 3.0f;
    public float cruiseTimeMax = 7.0f;

    [Header("연출(선택)")]
    public Animator busAnimator;

    [Header("스폰")]
    public PassengerAgent agentPrefab;

    [Header("정류장별 랜덤 탑승 수")]
    public int boardMinPerStop = 1;
    public int boardMaxPerStop = 5;

    [Header("정차 시 대기열 스폰(랜덤)")]
    public int spawnMinPerStop = 0;
    public int spawnMaxPerStop = 5;
    public int outsideQueueMax = 30;
    public float spawnBackSpacing = 1f;

    [Header("정차 윈도우(초)")]
    public float alightWindowSeconds = 6f; // 하차 허용 시간
    public float boardWindowSeconds = 8f; // 탑승 허용 시간

    // === 정차 1회에 대한 탑승 목표/실적 ===
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
        Debug.LogWarning($"[StopController] NavMesh 샘플 실패: {pos} (NavMesh 밖일 수 있음)");
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
        // 두 문 오픈 + Admit 허용
        if (doorOut) { doorOut.HoldOpen(this); doorOut.SetAdmitEnabled(true); doorOut.Open(); }
        if (doorIn) { doorIn.HoldOpen(this); doorIn.SetAdmitEnabled(true); doorIn.Open(); }

        // 대기열 랜덤 스폰
        if (spawnMaxPerStop >= spawnMinPerStop && spawnMaxPerStop > 0)
        {
            int spawnN = Random.Range(spawnMinPerStop, spawnMaxPerStop + 1);
            SpawnAndQueueN(spawnN);
        }

        // 하차자 선정 + 준비
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

        // 이번 정차 목표 탑승 수
        int wish = (boardMaxPerStop >= boardMinPerStop)
            ? Random.Range(boardMinPerStop, boardMaxPerStop + 1)
            : Mathf.Max(0, boardMinPerStop);

        int capacityLeft = Mathf.Max(0, capacity - insideAgents.Count);
        int possible = Mathf.Min(wish, capacityLeft, outsideQueue.Count);

        boardingGoalThisStop = possible;
        boardedThisStop = 0;
        boardingFinished = (possible == 0);

        // 병렬 실행: 하차(윈도우), 탑승(목표+윈도우)
        var alightRoutine = StartCoroutine(Co_AlightFlowWindowed(alightWindowSeconds));
        var boardRoutine = StartCoroutine(Co_BoardFlow(boardingGoalThisStop, boardWindowSeconds));

        // 하차 윈도우 종료 대기
        yield return alightRoutine;

        // 탑승 목표 완료 대기
        yield return new WaitUntil(() => boardingFinished);

        // 모든 탑승객이 Riding 상태(좌석 or 입석 위치) 도달 체크(짧게)
        yield return new WaitForSeconds(0.1f);

        // 문 닫기(강화 루틴)
        float closeTimeout = 2.0f;
        yield return CloseDoorSafely(doorOut, closeTimeout);
        yield return CloseDoorSafely(doorIn, closeTimeout);
    }

    // 하차: 윈도우 내에 새 허가만, 슬롯 통과 후 바로 내부 리스트에서 제거(문 닫기 대기 안 함)
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

                // ★ 문 안쪽 대기점으로 먼저 붙이기
                Vector3 innerWait = doorOut.transform.position + doorOut.transform.forward * 0.45f;
                a.GoToPoint(innerWait);
                yield return new WaitUntil(() => Vector3.Distance(a.transform.position, innerWait) < 0.35f || a.Reached(0.2f));

                // 그 다음 슬롯 허가
                yield return new WaitUntil(() => doorOut.TryAdmitAlight(a));
                yield return new WaitUntil(() => a.Reached());

                // 슬롯 통과 후 한 발짝
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

            // ★ 여기서부터는 문과 무관: 외부로 보내되, 버스 내부 리스트에서는 즉시 제거
            if (exitPointOutside != null) a.BeginAlight(exitPointOutside.position);

            RemoveInside(a);      // 내부 목록 즉시 제거
            // 파괴는 지연(밖으로 가게 두고 나중에 정리)
            StartCoroutine(Co_RecycleWhenFar(a, 3f));

            yield return new WaitForSeconds(gateInterval);
        }

        // 새 하차 허가 중단
        if (doorOut) doorOut.SetAdmitEnabled(false);
    }

    // 바깥으로 충분히 떨어지면 삭제(문 닫기와 독립)
    IEnumerator Co_RecycleWhenFar(PassengerAgent a, float minDistance)
    {
        if (a == null || exitPointOutside == null) yield break;
        while (a != null && Vector3.Distance(a.transform.position, exitPointOutside.position) > 0.4f)
        {
            // 안전장치: 너무 가까우면 계속 한 걸음 멀리 보냄
            if (Vector3.Distance(a.transform.position, exitPointOutside.position) < minDistance)
                a.GoToPoint(exitPointOutside.position);
            yield return null;
        }
        if (a) Destroy(a.gameObject);
    }

    // 탑승: 목표 수/윈도우 내에서만 허가
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

                // 슬롯 안쪽으로 한 발짝
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

            // 좌석 배정
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

    // ====== 유틸 ======
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

    // ===== 문 닫기 안전 루틴 =====
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

        // 마지막 시도
        door.FlushGateOccupants();
        if (door.IsClearStrict())
        {
            door.Close();
        }
        else
        {
            // ★ 남아 있으면 강제 닫기
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
