using UnityEngine;
using UnityEngine.AI;
using System;

public enum AgentState3D { IdleOutside, QueueOutside, Boarding, Riding, PrepareAlight, Alighting }

[RequireComponent(typeof(NavMeshAgent))]
public class PassengerAgent : MonoBehaviour
{
    public AgentState3D state = AgentState3D.IdleOutside;
    public bool willAlightHere;

    private NavMeshAgent agent;
    private Animator animator;

    public DoorGate entryDoor;     // 앞문
    public DoorGate exitDoor;      // 뒷문
    public Transform queueTarget;
    public SeatSlot mySeatOrStand;

    [Header("Move")]
    public float turnSpeed = 8f;
    public float arriveThreshold = 0.18f;
    [Tooltip("바깥 도착 판정 거리(조금 넉넉하게)")]
    public float outsideDoneDistance = 1.2f; // 뭉침 방지를 위해 증가

    [Header("Queue Follow")]
    [NonSerialized] private Transform followTarget;
    public float followDistance = 0.6f;
    public float queueRepathInterval = 0.15f;
    [NonSerialized] private float nextRepathTime;
    private float baseSpeed;

    [Header("Queue Idle")]
    public float queueSettleRadius = 0.15f;
    public float queueWakeDistance = 0.28f;
    [NonSerialized] private Vector3 lastFollowPos = Vector3.positiveInfinity;
    [NonSerialized] private bool queueSettled = false;

    // 워치독
    private float stateEnterTime;
    private Vector3 lastPos;
    private float lastPosTime;

    [Header("Despawn")]
    [Tooltip("하차 완료(바깥 도착)하면 이 에이전트를 제거")]
    public bool despawnAfterAlight = true;
    [Tooltip("제거 전에 줄 딜레이(시각적 여유)")]
    public float despawnDelay = 0.05f;

    // 컨트롤러 연동용 콜백(StopController에서 구독 가능)
    public Action<PassengerAgent> onDespawn;

    void ResetWatchdog()
    {
        stateEnterTime = Time.time;
        lastPos = transform.position;
        lastPosTime = Time.time;
    }

    void Awake()
    {
        agent = GetComponent<NavMeshAgent>();
        animator = GetComponentInChildren<Animator>(true);

        if (agent != null) { agent.updateRotation = false; baseSpeed = agent.speed; }
        if (animator != null) animator.applyRootMotion = false;
    }

    void OnEnable()
    {
        if (animator != null)
        {
            animator.Rebind(); animator.Update(0f);
            animator.ResetTrigger("Seat"); animator.ResetTrigger("Stand");
            animator.SetFloat("Speed", 0f); animator.SetBool("isWalking", false);
        }
        nextRepathTime = 0f;
        if (agent != null) agent.speed = baseSpeed;
        queueSettled = false; lastFollowPos = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        ResetWatchdog();
    }

    void Update()
    {
        HandleRotationAndAnim();

        // 위치 변화 추적
        if ((transform.position - lastPos).sqrMagnitude > 0.04f)
        {
            lastPos = transform.position;
            lastPosTime = Time.time;
        }

        // 줄 로직 (생략 없이 동작)
        if (state == AgentState3D.QueueOutside && agent && agent.isActiveAndEnabled)
        {
            Vector3 anchorPos; Vector3 anchorFwd;
            if (followTarget)
            {
                anchorFwd = followTarget.forward;
                anchorPos = followTarget.position - anchorFwd * followDistance;
            }
            else if (queueTarget)
            {
                anchorFwd = queueTarget.forward;
                anchorPos = queueTarget.position;
            }
            else { anchorFwd = transform.forward; anchorPos = transform.position; }

            float dist = Vector3.Distance(transform.position, anchorPos);
            float leaderMoved = 0f;
            if (followTarget)
            {
                if (lastFollowPos.x == float.PositiveInfinity) lastFollowPos = followTarget.position;
                leaderMoved = Vector3.Distance(lastFollowPos, followTarget.position);
            }

            float desired = baseSpeed;
            float stopZone = Mathf.Max(0.1f, queueSettleRadius - 0.03f);
            float slowZone = queueSettleRadius + 0.20f;

            if (dist <= stopZone) desired = 0f;
            else if (dist <= slowZone) desired = Mathf.Lerp(0f, baseSpeed * 0.6f, Mathf.InverseLerp(stopZone, slowZone, dist));

            if (!queueSettled)
            {
                if (Time.time >= nextRepathTime)
                {
                    agent.isStopped = false;
                    GoToPoint(anchorPos);
                    nextRepathTime = Time.time + queueRepathInterval;
                }

                if (dist <= queueSettleRadius || desired <= 0.01f)
                {
                    queueSettled = true; agent.isStopped = true; agent.ResetPath();
                    FaceForward(anchorFwd);
                }
            }
            else
            {
                bool shouldWake = (leaderMoved >= queueWakeDistance) || (dist > (queueSettleRadius + 0.06f));
                if (shouldWake)
                {
                    queueSettled = false; agent.isStopped = false;
                    GoToPoint(anchorPos);
                    nextRepathTime = Time.time + queueRepathInterval;
                    if (followTarget) lastFollowPos = followTarget.position;
                }
                else { FaceForward(anchorFwd); }
            }

            agent.speed = Mathf.Lerp(agent.speed, desired, Time.deltaTime * 6f);
            if (followTarget && !queueSettled) lastFollowPos = followTarget.position;
        }

        // 좌석/입석 도착 처리
        if (state == AgentState3D.Riding && mySeatOrStand && agent && agent.isActiveAndEnabled)
        {
            if (!agent.pathPending && agent.remainingDistance <= arriveThreshold)
            {
                transform.position = mySeatOrStand.Anchor.position;
                FaceForward(mySeatOrStand.Anchor.forward);

                if (animator)
                {
                    animator.SetBool("isWalking", false);
                    animator.SetFloat("Speed", 0f);
                    animator.ResetTrigger("Stand");
                    if (mySeatOrStand.kind == SlotKind.Seat) animator.SetTrigger("Seat");
                    else animator.SetTrigger("Stand");
                }

                agent.isStopped = true; agent.ResetPath();
            }
        }

        // 하차 준비: 문 위치로 이동 (StopController가 제어)
        if (state == AgentState3D.PrepareAlight)
        {
            if (animator) animator.SetBool("isWalking", true);

            // PrepareAlight 상태에서 갇힘 방지 워치독 (혹시 모를 상황에 대비해 약하게 유지)
            if (exitDoor)
            {
                float d = Vector3.Distance(transform.position, exitDoor.transform.position);
                if (d < 1.2f && Time.time - stateEnterTime > 0.75f)
                {
                    // 문에 가까워지면 admit 시도 (Co_AlightFlow_Instant에서 주로 호출되지만, 보험)
                    exitDoor.TryAdmitAlight(this);
                }
            }
        }

        // 하차 진행: 바깥 도착하면 제거
        if (state == AgentState3D.Alighting && agent && agent.isActiveAndEnabled)
        {
            if (!agent.pathPending && agent.remainingDistance <= outsideDoneDistance)
            {
                if (animator) { animator.SetBool("isWalking", false); animator.SetFloat("Speed", 0f); }
                state = AgentState3D.IdleOutside;

                if (despawnAfterAlight)
                {
                    if (agent)
                    {
                        agent.isStopped = true;
                        agent.ResetPath();
                        agent.enabled = false; // ★ 뭉침/복귀 방지: NavMesh Agent 비활성화
                    }
                    // 콜백 먼저 호출(컨트롤러가 카운터 조정 가능)
                    onDespawn?.Invoke(this);
                    Destroy(gameObject, Mathf.Max(0f, despawnDelay));
                }
            }
            else
            {
                if (animator) { animator.SetBool("isWalking", true); animator.SetFloat("Speed", agent.velocity.magnitude); }
            }
        }
    }

    void HandleRotationAndAnim()
    {
        if (agent == null || !agent.isActiveAndEnabled) return;

        float speed = agent.isOnNavMesh ? agent.velocity.magnitude : 0f;
        if (animator && state != AgentState3D.Alighting)
        {
            animator.SetFloat("Speed", speed);
            animator.SetBool("isWalking", speed > 0.1f);
        }

        if (speed > 0.05f && agent.desiredVelocity.sqrMagnitude > 0.0001f)
        {
            Vector3 moveDir = new(agent.desiredVelocity.x, 0f, agent.desiredVelocity.z);
            if (moveDir.sqrMagnitude > 0.0001f) SmoothLook(moveDir.normalized);
            return;
        }

        switch (state)
        {
            case AgentState3D.QueueOutside:
                if (followTarget) FaceForward(followTarget.forward);
                else if (queueTarget) FaceForward(queueTarget.forward);
                else if (entryDoor) FacePoint(entryDoor.transform.position);
                break;
            case AgentState3D.Riding:
                if (mySeatOrStand) FaceForward(mySeatOrStand.Anchor.forward);
                break;
            case AgentState3D.PrepareAlight:
                if (exitDoor) FacePoint(exitDoor.transform.position); // 뒷문 바라보기
                break;
            case AgentState3D.Alighting:
                if (speed < 0.05f && exitDoor) FacePoint(exitDoor.transform.position);
                break;
            default:
                if (entryDoor) FacePoint(entryDoor.transform.position);
                break;
        }
    }

    void SmoothLook(Vector3 forwardDir)
    {
        if (forwardDir.sqrMagnitude < 0.0001f) return;
        Quaternion target = Quaternion.LookRotation(forwardDir, Vector3.up);
        transform.rotation = Quaternion.Slerp(transform.rotation, target, Time.deltaTime * turnSpeed);
    }
    void FaceForward(Vector3 worldForward)
    {
        Vector3 f = new(worldForward.x, 0f, worldForward.z);
        if (f.sqrMagnitude < 0.0001f) return;
        SmoothLook(f.normalized);
    }
    void FacePoint(Vector3 worldPoint)
    {
        Vector3 to = worldPoint - transform.position; to.y = 0f;
        if (to.sqrMagnitude < 0.0001f) return;
        SmoothLook(to.normalized);
    }

    // ===== 외부 API =====
    public void GoToPoint(Vector3 worldPos)
    {
        if (agent == null || !agent.isActiveAndEnabled) return;
        if (NavMesh.SamplePosition(worldPos, out var hit, 1.5f, NavMesh.AllAreas))
            agent.SetDestination(hit.position);
        else
            Debug.LogWarning($"[PassengerAgent] 목적지({worldPos})가 NavMesh 밖입니다.");
    }

    public bool Reached(float threshold = 0.18f)
    {
        if (agent == null || !agent.isActiveAndEnabled || !agent.isOnNavMesh || agent.pathPending) return false;
        return agent.remainingDistance <= threshold;
    }

    public void BeginQueue(Transform queueEntryPoint)
    {
        state = AgentState3D.QueueOutside;
        queueTarget = queueEntryPoint;
        if (animator) animator.ResetTrigger("Seat");
        GoToPoint(queueEntryPoint.position);
        if (agent != null) agent.isStopped = false;
        queueSettled = false; lastFollowPos = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        ResetWatchdog();
    }

    public void SetQueueFollowTarget(Transform target) { followTarget = target; state = AgentState3D.QueueOutside; ResetWatchdog(); }
    public void ClearQueueFollow() { followTarget = null; }

    public void OnQueueTargetRebound()
    {
        queueSettled = false; lastFollowPos = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity); ResetWatchdog();
    }

    public void BeginBoard(DoorGate d)
    {
        entryDoor = d; state = AgentState3D.Boarding;
        if (animator) animator.SetBool("isWalking", true);
        followTarget = null;
        ResetWatchdog();
    }

    public void BeginRide(SeatSlot slot)
    {
        state = AgentState3D.Riding; mySeatOrStand = slot;
        if (slot != null && slot.TryReserve(this))
        {
            Transform t = slot.Anchor;
            if (animator) animator.ResetTrigger("Stand");
            GoToPoint(t.position);
        }
        ResetWatchdog();
    }

    // ★★★ 하차 시작: 문 위치를 목표로 설정
    public void BeginAlightPrepare(DoorGate d)
    {
        state = AgentState3D.PrepareAlight;
        exitDoor = d;

        if (mySeatOrStand) mySeatOrStand.Release(this);
        mySeatOrStand = null;

        if (animator)
        {
            animator.ResetTrigger("Seat");
            animator.SetTrigger("Stand"); // 일어서기
            animator.SetBool("isWalking", true);
        }

        // 뒷문 위치를 목표로 설정하여 이동 유도
        if (exitDoor != null) GoToPoint(exitDoor.transform.position);
        ResetWatchdog();
    }

    // ★★★ 최종 하차: 바깥 도착 지점을 목표로 설정
    public void BeginAlightMoveToFinal(Vector3 exitPoint)
    {
        state = AgentState3D.Alighting;
        if (animator) { animator.ResetTrigger("Seat"); animator.SetBool("isWalking", true); }
        GoToPoint(exitPoint);
        ResetWatchdog();
    }
}