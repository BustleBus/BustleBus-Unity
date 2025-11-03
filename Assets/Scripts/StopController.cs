using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

public class StopController : MonoBehaviour
{
    [Header("용량/문/포인트")]
    public int capacity = 40;
    public DoorGate doorIn;                 // 앞문(승차 전용)
    public DoorGate doorOut;                // 뒷문(하차 전용)
    public Transform exitPointOutside;      // 하차 후 바깥 도착점
    public Transform queueRootOutside;      // 줄 포인트들
    public Transform entryPoint;            // 줄 맨앞

    [Header("슬롯들")]
    public List<SeatSlot> seatSlots = new();
    public List<SeatSlot> standSlots = new();

    [Header("연출/딜레이")]
    public float gateInterval = 0.2f;
    public float cruiseTimeMin = 3.0f;
    public float cruiseTimeMax = 7.0f;
    public Animator busAnimator;

    [Header("탑승 수")]
    public PassengerAgent agentPrefab;
    public int boardMinPerStop = 1;
    public int boardMaxPerStop = 5;

    [Header("뒷문 내부 하차 대기열")]
    public int exitQueueSlots = 6;
    public float exitQueueSpacing = 0.55f;
    public float exitQueueStartOffset = 0.35f;

    [System.NonSerialized] public List<PassengerAgent> insideAgents = new();
    [System.NonSerialized] public List<PassengerAgent> outsideQueue = new();

    int boardedThisStop = 0;
    int boardingGoalThisStop = 0;

    void Start()
    {
        if (entryPoint == null && queueRootOutside && queueRootOutside.childCount > 0)
            entryPoint = queueRootOutside.GetChild(0);

        // 문 설정 (승차/하차 전용)
        if (doorIn) doorIn.SetEntryExit(isEntry: true, isExit: false);
        if (doorOut) doorOut.SetEntryExit(isEntry: false, isExit: true);

        // 데모용 초기 줄 구성
        if (queueRootOutside && queueRootOutside.childCount > 0)
        {
            foreach (Transform q in queueRootOutside)
            {
                var a = SpawnAgentAt(q.position);
                AddToQueue(a);
            }
        }
        else
        {
            Vector3 basePos = entryPoint ? entryPoint.position : transform.position;
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
        if (agentPrefab)
        {
            a = Instantiate(agentPrefab, SampleOnNavMesh(pos), Quaternion.identity);
        }
        else
        {
            var go = new GameObject("Agent");
            var nav = go.AddComponent<NavMeshAgent>();
            nav.radius = 0.2f; nav.speed = 1.4f; nav.acceleration = 5f; nav.angularSpeed = 420f;
            go.transform.position = SampleOnNavMesh(pos);
            var vis = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            vis.transform.SetParent(go.transform, false);
            var col = vis.GetComponent<Collider>(); if (col) col.isTrigger = true;
            a = go.AddComponent<PassengerAgent>();
        }
        a.tag = "Agent";

        // 에이전트 제거 시 insideAgents 리스트에서 제거하도록 구독
        a.onDespawn += RemoveInside;

        return a;
    }

    Vector3 SampleOnNavMesh(Vector3 pos)
    {
        if (NavMesh.SamplePosition(pos, out var hit, 1.5f, NavMesh.AllAreas)) return hit.position;
        return pos;
    }

    IEnumerator MainLoop()
    {
        while (true)
        {
            // 주행
            SetDriving(true);
            yield return new WaitForSeconds(Random.Range(cruiseTimeMin, cruiseTimeMax));

            // 정차
            SetDriving(false);
            yield return ArriveStopOnce();
        }
    }

    void SetDriving(bool d) { if (busAnimator) busAnimator.SetBool("isDriving", d); }

    IEnumerator ArriveStopOnce()
    {
        // 1. 하차자 선정
        SelectAlighters();
        insideAgents.RemoveAll(x => x == null);
        int alightersCount = insideAgents.FindAll(x => x && x.willAlightHere).Count;

        // 2. 탑승 목표 산정
        int freeSeats = CountFree(seatSlots);
        int freeStands = CountFree(standSlots);
        int freeTotal = freeSeats + freeStands;
        int capacityLeft = Mathf.Max(0, capacity - insideAgents.Count);
        int wish = (boardMaxPerStop >= boardMinPerStop)
            ? Random.Range(boardMinPerStop, boardMaxPerStop + 1)
            : Mathf.Max(0, boardMinPerStop);
        boardingGoalThisStop = Mathf.Clamp(wish, 0, Mathf.Min(freeTotal, capacityLeft));

        // --- 문 개방 조건 확인 ---
        bool doorInOpen = boardingGoalThisStop > 0;
        bool doorOutOpen = alightersCount > 0;

        // 문 오픈 및 admit 설정
        if (doorIn && doorInOpen)
        {
            doorIn.HoldOpen(this); doorIn.SetAdmitEnabled(true); doorIn.Open();
        }
        if (doorOut && doorOutOpen)
        {
            doorOut.HoldOpen(this); doorOut.SetAdmitEnabled(true); doorOut.Open();
        }

        // 문이 열리지 않는다면 닫아두기
        if (doorIn && !doorInOpen) doorIn.ForceCloseNow();
        if (doorOut && !doorOutOpen) doorOut.ForceCloseNow();

        // 하차자 이동 시작 (뒷문이 열렸을 때만 명령)
        if (doorOutOpen)
        {
            foreach (var a in insideAgents)
            {
                if (a != null && a.willAlightHere)
                {
                    // 2단계 하차: 문 위치로 먼저 이동하도록 명령
                    a.BeginAlightPrepare(doorOut);
                }
            }
        }

        // 줄 모자라면 정확 수만큼만 스폰
        int need = Mathf.Max(0, boardingGoalThisStop - outsideQueue.Count);
        if (need > 0) SpawnExactForBoarding(need);

        boardedThisStop = 0;

        // 병렬 처리: 문이 열린 경우에만 코루틴 실행
        Coroutine coAlight = null;
        if (doorOutOpen) coAlight = StartCoroutine(Co_AlightFlow_Instant());

        Coroutine coBoard = null;
        if (doorInOpen) coBoard = StartCoroutine(Co_BoardFlowExactly(boardingGoalThisStop));

        // 코루틴 대기
        if (coAlight != null) yield return coAlight;

        // 탑승 목표가 있다면 대기
        if (doorInOpen)
        {
            yield return new WaitUntil(() => boardedThisStop >= boardingGoalThisStop);
            yield return coBoard;
        }

        // 더 이상 admit 금지 (문이 열렸던 경우에만)
        if (doorOut && doorOutOpen) doorOut.SetAdmitEnabled(false);
        if (doorIn && doorInOpen) doorIn.SetAdmitEnabled(false);

        // 문 닫기 (문이 열렸던 경우에만 닫기 로직 실행)
        if (doorOut && doorOutOpen) CloseDoorImmediately(doorOut);
        if (doorIn && doorInOpen) CloseDoorImmediately(doorIn);

        yield break;
    }

    void CloseDoorImmediately(DoorGate door)
    {
        if (!door) return;
        door.FlushGateOccupants();
        // 슬롯 및 게이트 통과 중인 승객이 완전히 없어야 Close 
        if (door.IsClearStrict()) door.Close();
        else door.ForceCloseNow();
        door.ReleaseHold(this);   // 재오픈 방지
        door.SetAdmitEnabled(false);
    }

    IEnumerator Co_AlightFlow_Instant()
    {
        if (!doorOut) yield break;
        doorOut.EnsureOpen(); doorOut.SetAdmitEnabled(true);

        insideAgents.RemoveAll(x => x == null);
        List<PassengerAgent> alighters = insideAgents.FindAll(x => x && x.willAlightHere);

        // 문 가까운 순
        alighters.Sort((a, b) =>
        {
            float da = (a.transform.position - doorOut.transform.position).sqrMagnitude;
            float db = (b.transform.position - doorOut.transform.position).sqrMagnitude;
            return da.CompareTo(db);
        });

        for (int i = 0; i < alighters.Count; i++)
        {
            var a = alighters[i];

            // MissingReferenceException 방지
            if (a == null) continue;

            // 1. Admit 시도 (문 슬롯 통과 권한 획득)
            float timeout = Time.time + 3.0f; // 최대 3초 대기
            bool admitted = false;

            // yield 대기 중 객체 파괴 방지 체크
            yield return new WaitUntil(() => a == null || (admitted = doorOut.TryAdmitAlight(a)) || Time.time > timeout);

            if (a == null) continue; // yield 대기 중 파괴 시 다음 루프로 이동

            if (!admitted)
            {
                // Admit 실패 시 강제 하차 (문 통과 대기 생략)
                Debug.LogWarning($"[Co_AlightFlow_Instant] Agent {a.name} failed to admit. Forcing final move.");
            }
            else
            {
                // 2. 문 슬롯 중앙 도착 대기
                timeout = Time.time + 3.0f;
                yield return new WaitUntil(() => a == null || a.Reached() || Time.time > timeout);

                if (a == null) continue; // yield 대기 중 파괴 시 다음 루프로 이동

                // ★★★ 3. 슬롯을 완전히 통과할 때까지 강제 대기
                var nearestSlot = NearestSlot(doorOut.gateSlots, a.transform.position);
                if (nearestSlot)
                {
                    // 문 안쪽으로 한 발짝 더 목표를 지정 (다음 승객에게 공간을 확보)
                    Vector3 throughPoint = nearestSlot.position - doorOut.transform.forward * 0.7f;
                    a.GoToPoint(throughPoint);

                    // doorOut.passClearDistance를 넘어서 문 슬롯을 완전히 벗어날 때까지 대기
                    float clearTimeout = Time.time + 2.0f;
                    yield return new WaitUntil(() =>
                        a == null ||
                        Vector3.Distance(a.transform.position, nearestSlot.position) > doorOut.passClearDistance ||
                        Time.time > clearTimeout
                    );
                }

                doorOut.ReleaseAgent(a); // 슬롯에서 해제하여 다음 승객에게 양보
            }

            // 4. 최종 하차 지점으로 이동 명령
            if (a != null && exitPointOutside) a.BeginAlightMoveToFinal(exitPointOutside.position);

            yield return new WaitForSeconds(gateInterval);
        }

        // 하차 admit 종료
        doorOut.SetAdmitEnabled(false);
    }

    IEnumerator Co_BoardFlowExactly(int target)
    {
        if (target <= 0 || !doorIn) yield break;
        doorIn.EnsureOpen(); doorIn.SetAdmitEnabled(true);

        CleanOutsideQueue();

        while (boardedThisStop < target && insideAgents.Count < capacity && outsideQueue.Count > 0)
        {
            var p = outsideQueue[0];
            outsideQueue.RemoveAt(0);
            RebindQueueAnchors(0);
            if (!p) continue;

            p.entryDoor = doorIn;
            p.exitDoor = doorOut;
            p.BeginBoard(doorIn);

            // 슬롯 허가 + 진입
            yield return new WaitUntil(() => doorIn.TryAdmitBoard(p));
            yield return new WaitUntil(() => p.Reached());

            // 슬롯 안쪽 한 발짝
            var nearestSlot = NearestSlot(doorIn.gateSlots, p.transform.position);
            if (nearestSlot)
            {
                Vector3 passThrough = nearestSlot.position + doorIn.transform.forward * 0.7f;
                p.GoToPoint(passThrough);
                yield return new WaitUntil(() =>
                    Vector3.Distance(p.transform.position, nearestSlot.position) > doorIn.passClearDistance || p.Reached(0.2f));
                doorIn.ReleaseAgent(p);
            }

            // 좌석 우선 → 없으면 입석
            var slot = FindNearestFreeSeat(p.transform.position);
            if (slot == null) slot = FindNearestFreeStand(p.transform.position);

            if (slot != null) p.BeginRide(slot);
            else p.GoToPoint(doorIn.transform.position + doorIn.transform.forward * 0.9f);

            insideAgents.Add(p);
            boardedThisStop++;

            yield return new WaitForSeconds(gateInterval);
        }
        if (boardedThisStop > 0 && doorIn)
        {
            // 문 주변에 승객이 없는지 DoorGate 자체에게 물어보며 최대 1초 대기
            float finalClearTimeout = Time.time + 1.0f;
            yield return new WaitUntil(() => doorIn.IsClearStrict() || Time.time > finalClearTimeout);

            if (Time.time > finalClearTimeout)
            {
                Debug.LogWarning("Front door clear timeout exceeded. Forcing closure check.");
            }
        }
        doorIn.SetAdmitEnabled(false);
    }

    // ===== 유틸 =====
    int CountFree(List<SeatSlot> list)
    { int c = 0; foreach (var s in list) if (s && !s.isReserved) c++; return c; }

    SeatSlot FindNearestFreeSeat(Vector3 from)
    {
        float best = float.MaxValue; SeatSlot bestSlot = null;
        foreach (var s in seatSlots)
        {
            if (!s || s.isReserved) continue;
            float d = (s.Anchor.position - from).sqrMagnitude;
            if (d < best) { best = d; bestSlot = s; }
        }
        return bestSlot;
    }
    SeatSlot FindNearestFreeStand(Vector3 from)
    {
        float best = float.MaxValue; SeatSlot bestSlot = null;
        foreach (var s in standSlots)
        {
            if (!s || s.isReserved) continue;
            float d = (s.Anchor.position - from).sqrMagnitude;
            if (d < best) { best = d; bestSlot = s; }
        }
        return bestSlot;
    }

    Transform NearestSlot(Transform[] arr, Vector3 from)
    {
        if (arr == null || arr.Length == 0) return null;
        float best = float.MaxValue; Transform bestT = null;
        foreach (var t in arr)
        {
            if (!t) continue;
            float d = (t.position - from).sqrMagnitude;
            if (d < best) { best = d; bestT = t; }
        }
        return bestT;
    }

    Vector3 ExitQueueAnchor(int index)
    {
        if (!doorOut) return transform.position;
        Vector3 basePos = doorOut.transform.position + doorOut.transform.forward * (exitQueueStartOffset + index * exitQueueSpacing);
        if (NavMesh.SamplePosition(basePos, out var hit, 0.8f, NavMesh.AllAreas)) return hit.position;
        return basePos;
    }

    void SelectAlighters()
    {
        insideAgents.RemoveAll(x => x == null);
        foreach (var a in insideAgents) a.willAlightHere = (Random.value < 0.3f);
    }

    public void AddToQueue(PassengerAgent agent)
    {
        if (!agent) return;
        outsideQueue.Add(agent);
        int idx = outsideQueue.Count - 1;

        if (idx == 0)
        {
            agent.ClearQueueFollow();
            agent.BeginQueue(entryPoint ? entryPoint : agent.transform);
        }
        else
        {
            var front = outsideQueue[idx - 1];
            agent.BeginQueue(entryPoint ? entryPoint : agent.transform);
            if (front) agent.SetQueueFollowTarget(front.transform);
        }
    }

    void CleanOutsideQueue()
    {
        bool changed = outsideQueue.RemoveAll(x => x == null) > 0;
        if (changed) RebindQueueAnchors(0);
    }

    void SpawnExactForBoarding(int n)
    {
        if (n <= 0) return;
        Vector3 basePos = (entryPoint ? entryPoint.position : transform.position);
        Vector3 backDir = (entryPoint ? -entryPoint.forward : -transform.forward);

        int startIndex = outsideQueue.Count;
        for (int i = 0; i < n; i++)
        {
            int queueIndex = startIndex + i;
            Vector3 pos = basePos + backDir * (1.0f + 1.0f * (queueIndex + 1));
            var a = SpawnAgentAt(pos);
            AddToQueue(a);
        }
    }

    void RebindQueueAnchors(int startIndex)
    {
        if (outsideQueue.Count == 0) return;
        for (int i = Mathf.Max(0, startIndex); i < outsideQueue.Count; i++)
        {
            var agent = outsideQueue[i]; if (!agent) continue;
            if (i == 0)
            {
                agent.ClearQueueFollow();
                agent.BeginQueue(entryPoint ? entryPoint : agent.transform);
                agent.SendMessage("OnQueueTargetRebound", SendMessageOptions.DontRequireReceiver);
            }
            else
            {
                var front = outsideQueue[i - 1];
                if (front) agent.SetQueueFollowTarget(front.transform);
                else
                {
                    int j = i - 1; while (j >= 0 && outsideQueue[j] == null) j--;
                    if (j >= 0 && outsideQueue[j]) agent.SetQueueFollowTarget(outsideQueue[j].transform);
                    else { agent.ClearQueueFollow(); agent.BeginQueue(entryPoint ? entryPoint : agent.transform); }
                }
                agent.SendMessage("OnQueueTargetRebound", SendMessageOptions.DontRequireReceiver);
            }
        }
    }

    void RemoveInside(PassengerAgent a)
    {
        if (a && a.mySeatOrStand) a.mySeatOrStand.Release(a);
        insideAgents.Remove(a);
    }
}