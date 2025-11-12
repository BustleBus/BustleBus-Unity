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
    public List<PassengerAgent> agentPrefabs = new(); // ★★★ 랜덤 프리팹 리스트
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
    [Header("디버그/제어")]
    [Tooltip("즉시 정차를 발동시킬 키")]
    public KeyCode immediateStopKey = KeyCode.J; // J 키로 설정 (예시)
    void Start()
    {
        if (entryPoint == null && queueRootOutside && queueRootOutside.childCount > 0)
            entryPoint = queueRootOutside.GetChild(0);

        // 문 설정 (승차/하차 전용)
        if (doorIn) doorIn.SetEntryExit(isEntry: true, isExit: false);
        if (doorOut) doorOut.SetEntryExit(isEntry: false, isExit: true);

        // 초기 줄 생성 로직 (버스 밖 대기열에만 생성)
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
            // 기본 위치에 3명 생성
            Vector3 basePos = entryPoint ? entryPoint.position : transform.position;
            for (int i = 0; i < 3; i++)
            {
                // 버스 뒤쪽 방향으로 위치 조정하여 생성
                var a = SpawnAgentAt(basePos + (-transform.forward * (1.0f + 0.6f * i)));
                AddToQueue(a);
            }
        }

        StartCoroutine(MainLoop());
    }
    void Update()
    {
        // 즉시 정차 키 입력 처리
        if (Input.GetKeyDown(immediateStopKey))
        {
                StartCoroutine(ArriveStopOnce());
        }

        // ... (기존 Update() 로직)
    }
    PassengerAgent SpawnAgentAt(Vector3 pos)
    {
        PassengerAgent a = null;

        bool success = false;

        // 1. 랜덤 프리팹 선택 및 생성 시도
        if (agentPrefabs != null && agentPrefabs.Count > 0)
        {
            PassengerAgent selectedPrefab = agentPrefabs[Random.Range(0, agentPrefabs.Count)];

            if (selectedPrefab)
            {
                a = Instantiate(selectedPrefab, SampleOnNavMesh(pos), Quaternion.identity);
                success = true;
            }
        }

        // 2. 프리팹 생성에 실패했거나 리스트가 비어 있으면 기본 GameObject 생성
        if (!success)
        {
            if (agentPrefabs == null || agentPrefabs.Count == 0 || a == null)
            {
                Debug.LogWarning("[StopController] Failed to spawn random prefab or none defined. Creating basic agent.");
            }

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
        if (doorIn)
        {
            doorIn.SetDoorLinks(false); // 초기 Link 비활성화
            if (doorInOpen)
            {
                doorIn.HoldOpen(this); doorIn.SetAdmitEnabled(true); doorIn.Open();
            }
            else
            {
                doorIn.ForceCloseNow();
            }
        }

        if (doorOut)
        {
            doorOut.SetDoorLinks(false); // 초기 Link 비활성화
            if (doorOutOpen)
            {
                doorOut.HoldOpen(this); doorOut.SetAdmitEnabled(true); doorOut.Open();
            }
            else
            {
                doorOut.ForceCloseNow();
            }
        }

        // 뒷문 탑승 방지: 승차 시 뒷문의 NavMesh Link를 임시 차단 (앞문이 열린 경우만)
        if (doorOut && doorOutOpen && doorInOpen)
        {
            doorOut.SetDoorLinks(false);
        }

        // 하차자 이동 시작 (뒷문이 열렸을 때만 명령)
        if (doorOutOpen)
        {
            // 하차 지점 확보 (exitPointOutside가 없으면 문 바깥으로 강제 경로 설정)
            Vector3 finalExitPoint = exitPointOutside ? exitPointOutside.position : doorOut.transform.position - doorOut.transform.forward * 1.5f;
            foreach (var a in insideAgents)
            {
                if (a != null && a.willAlightHere)
                {
                    // Alighting 상태로 최종 지점 목표 설정
                    a.BeginAlight(doorOut, finalExitPoint);
                }
            }
        }

        // 줄 모자라면 정확 수만큼만 스폰 (버스 밖 대기열에 생성)
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

        // 뒷문 Link 복구 (문 닫기 전에 경로를 다시 연결)
        if (doorOut && doorOutOpen && doorInOpen)
        {
            doorOut.SetDoorLinks(true);
        }

        // 더 이상 admit 금지 (문이 열렸던 경우에만)
        if (doorOut && doorOutOpen) doorOut.SetAdmitEnabled(false);
        if (doorIn && doorInOpen) doorIn.SetAdmitEnabled(false);

        // 문 닫기 (문이 열렸던 경우에만 닫기 로직 실행)
        if (doorOut && doorOutOpen) CloseDoorImmediately(doorOut);
        if (doorIn && doorInOpen) CloseDoorImmediately(doorIn);

        // ★★★ 문 닫힘 애니메이션/장애물 적용 대기 시간 증가 ★★★
        if (doorOutOpen || doorInOpen)
        {
            float closeTimeout = Time.time + 6.0f; // 최대 6초로 증가

            if (doorOut && doorOutOpen)
            {
                yield return new WaitUntil(() => doorOut.isOpen == false || Time.time > closeTimeout);
            }

            if (doorIn && doorInOpen)
            {
                yield return new WaitUntil(() => doorIn.isOpen == false || Time.time > closeTimeout);
            }

            // 안전을 위해 닫힘 애니메이션 시간을 추가 대기
            yield return new WaitForSeconds(0.2f);
        }

        yield break;
    }

    void CloseDoorImmediately(DoorGate door)
    {
        if (!door) return;
        door.FlushGateOccupants();
        // 문 닫힘 로직을 분리하고, 강제 닫힘(ForceCloseNow) 시에도 Hold를 해제
        if (door.IsClearStrict())
        {
            door.Close(); // 일반 닫힘 (Hold 해제는 Close() 이후에)
        }
        else
        {
            // 문 슬롯이 비어있지 않다면 강제 닫힘. (NavMesh Obstacle 즉시 활성화)
            Debug.LogWarning($"[StopController] Door {door.name} is not clear. Forcing immediate closure.");
            door.ForceCloseNow();

        }
        door.ReleaseHold(this);   // 재오픈 방지
        door.SetAdmitEnabled(false);
    }

    // 하차 준비/대기열을 건너뛰는 즉시 하차 코루틴
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

            if (a == null) continue;

            // 1. Admit 시도 (문 슬롯 통과 권한 획득)
            float timeout = Time.time + 3.0f; // 최대 3초 대기
            bool admitted = false;

            yield return new WaitUntil(() => a == null || (admitted = doorOut.TryAdmitAlight(a)) || Time.time > timeout);

            if (a == null) continue;

            if (!admitted)
            {
                Debug.LogWarning($"[Co_AlightFlow_Instant] Agent {a.name} failed to admit. Forcing final move.");
            }
            else
            {
                // 2. 문 슬롯 중앙 도착 대기
                timeout = Time.time + 3.0f;
                yield return new WaitUntil(() => a == null || a.Reached() || Time.time > timeout);

                if (a == null) continue;

                // 3. 슬롯을 완전히 통과할 때까지 강제 대기 (낑김 방지 강화)
                var nearestSlot = NearestSlot(doorOut.gateSlots, a.transform.position);
                if (nearestSlot)
                {
                    Vector3 throughPoint = exitPointOutside ? exitPointOutside.position : nearestSlot.position - doorOut.transform.forward * 1.5f;
                    a.GoToPoint(throughPoint);

                    // doorOut.passClearDistance를 넘어서 문 슬롯을 완전히 벗어날 때까지 대기
                    float clearTimeout = Time.time + 3.0f;
                    yield return new WaitUntil(() =>
                        a == null ||
                        Vector3.Distance(a.transform.position, nearestSlot.position) > doorOut.passClearDistance * 2f ||
                        Time.time > clearTimeout
                    );
                }

                doorOut.ReleaseAgent(a); // 슬롯에서 해제하여 다음 승객에게 양보
            }

            // 4. 최종 하차 지점으로 이동 명령 (Admit 실패 시 경로 복구 용도)
            if (a != null && a.state != AgentState3D.Alighting)
            {
                Vector3 finalExitPoint = exitPointOutside ? exitPointOutside.position : doorOut.transform.position - doorOut.transform.forward * 1.5f;
                a.BeginAlight(doorOut, finalExitPoint);
            }

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

            // 1. 슬롯 허가 + 진입 대기
            yield return new WaitUntil(() => doorIn.TryAdmitBoard(p));
            // 2. 문 슬롯 중앙 도착 대기
            yield return new WaitUntil(() => p.Reached());

            // 3. 슬롯 안쪽 한 발짝 이동 명령
            var nearestSlot = NearestSlot(doorIn.gateSlots, p.transform.position);
            if (nearestSlot)
            {
                Vector3 passThrough = nearestSlot.position + doorIn.transform.forward * 1.0f;
                p.GoToPoint(passThrough);

                // ★★★ 수정: 문 슬롯 통과를 위한 최소 대기 시간 부여 (0.25초로 증가) ★★★
                yield return new WaitForSeconds(0.25f);

                doorIn.ReleaseAgent(p);
            }

            // 4. 좌석/입석 슬롯 찾아 이동 시작 (이동 완료까지 기다리지 않음)
            var slot = FindNearestFreeSeat(p.transform.position);
            if (slot == null) slot = FindNearestFreeStand(p.transform.position);

            if (slot != null) p.BeginRide(slot);
            else p.GoToPoint(doorIn.transform.position + doorIn.transform.forward * 0.4f);

            // 5. 탑승 완료 처리 및 카운트 증가 (다음 승객 처리 루프 즉시 재개)
            insideAgents.Add(p);
            boardedThisStop++;

            // 다음 승객을 위한 대기 시간 없음
        }

        // 최종 탑승 완료 후, 마지막 승객이 문 안쪽에서 좌석으로 이동할 시간을 확보 (닫힘 보장)
        if (boardedThisStop > 0 && doorIn)
        {
            float finalClearTimeout = Time.time + 4.0f;
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