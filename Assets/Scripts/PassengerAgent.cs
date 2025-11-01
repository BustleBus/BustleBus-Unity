using UnityEngine;
using UnityEngine.AI;

public enum AgentState3D { IdleOutside, QueueOutside, Boarding, Riding, PrepareAlight, Alighting }

[RequireComponent(typeof(NavMeshAgent))]
public class PassengerAgent : MonoBehaviour
{
    public AgentState3D state = AgentState3D.IdleOutside;
    public bool willAlightHere;

    private NavMeshAgent agent;

    public DoorGate entryDoor;     // 탑승문
    public DoorGate exitDoor;      // 하차문
    public Transform queueTarget;  // 줄의 기준 포인트(맨 앞)
    public SeatSlot mySeatOrStand; // 좌석/입석

    private Animator animator;

    [Header("Movement/Look")]
    public float turnSpeed = 8f;
    public float arriveThreshold = 0.18f;
    public bool snapExactOnSeat = true;

    [Header("Queue Follow")]
    [System.NonSerialized] private Transform followTarget;
    public float followDistance = 0.6f;
    public float queueRepathInterval = 0.15f;
    [System.NonSerialized] private float nextRepathTime;
    private float baseSpeed;

    [Header("Queue Idle (정지/재개 임계)")]
    public float queueSettleRadius = 0.15f;
    public float queueWakeDistance = 0.28f;
    [System.NonSerialized] private Vector3 lastFollowPos = Vector3.positiveInfinity;
    [System.NonSerialized] private bool queueSettled = false;

    [System.NonSerialized] public System.Action<PassengerAgent> OnAgentDestroyed;

    void Awake()
    {
        agent = GetComponent<NavMeshAgent>();
        animator = GetComponentInChildren<Animator>(true);

        if (agent != null)
        {
            agent.updateRotation = false;
            baseSpeed = agent.speed;
        }
        if (animator != null)
        {
            animator.applyRootMotion = false; // Y 흔들림 방지
        }
    }

    void OnEnable()
    {
        if (animator != null)
        {
            animator.Rebind();
            animator.Update(0f);
            animator.ResetTrigger("Seat");
            animator.ResetTrigger("Stand");
            animator.SetFloat("Speed", 0f);
            animator.SetBool("isWalking", false);
        }
        nextRepathTime = 0f;
        if (agent != null) agent.speed = baseSpeed;
        queueSettled = false;
        lastFollowPos = Vector3.positiveInfinity;
    }

    void Update()
    {
        HandleRotationAndAnim();

        // ===== 줄 서기 =====
        if (state == AgentState3D.QueueOutside && agent != null && agent.isActiveAndEnabled)
        {
            Vector3 anchorPos; Vector3 anchorFwd;
            if (followTarget != null)
            {
                anchorFwd = followTarget.forward;
                anchorPos = followTarget.position - anchorFwd * followDistance;
            }
            else if (queueTarget != null)
            {
                anchorFwd = queueTarget.forward;
                anchorPos = queueTarget.position;
            }
            else
            {
                anchorFwd = transform.forward;
                anchorPos = transform.position;
            }

            float dist = Vector3.Distance(transform.position, anchorPos);
            float leaderMoved = 0f;
            if (followTarget != null)
            {
                if (lastFollowPos.x == float.PositiveInfinity) lastFollowPos = followTarget.position;
                leaderMoved = Vector3.Distance(lastFollowPos, followTarget.position);
            }

            float desired = baseSpeed;
            float stopZone = Mathf.Max(0.1f, queueSettleRadius - 0.03f);
            float slowZone = queueSettleRadius + 0.20f;

            if (dist <= stopZone) desired = 0f;
            else if (dist <= slowZone)
            {
                float t = Mathf.InverseLerp(stopZone, slowZone, dist);
                desired = Mathf.Lerp(0f, baseSpeed * 0.6f, t);
            }

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
                    queueSettled = true;
                    agent.isStopped = true;
                    agent.ResetPath();
                    FaceForward(anchorFwd);
                }
            }
            else
            {
                bool shouldWake = (leaderMoved >= queueWakeDistance) || (dist > (queueSettleRadius + 0.06f));
                if (shouldWake)
                {
                    queueSettled = false;
                    agent.isStopped = false;
                    GoToPoint(anchorPos);
                    nextRepathTime = Time.time + queueRepathInterval;
                    if (followTarget != null) lastFollowPos = followTarget.position;
                }
                else
                {
                    FaceForward(anchorFwd);
                }
            }

            agent.speed = Mathf.Lerp(agent.speed, desired, Time.deltaTime * 6f);

            if (followTarget != null && !queueSettled)
                lastFollowPos = followTarget.position;
        }

        // ===== 좌석 도착 처리(앉기 전환) =====
        if (state == AgentState3D.Riding && mySeatOrStand != null && agent != null && agent.isActiveAndEnabled)
        {
            if (!agent.pathPending && agent.remainingDistance <= arriveThreshold)
            {
                if (snapExactOnSeat)
                {
                    transform.position = mySeatOrStand.transform.position;
                    FaceForward(mySeatOrStand.transform.forward);
                }
                if (animator)
                {
                    animator.SetBool("isWalking", false);
                    animator.SetFloat("Speed", 0f);
                    animator.ResetTrigger("Stand");
                    animator.SetTrigger("Seat"); // Seat → Sitting
                }
                agent.isStopped = true;
                agent.ResetPath();
            }
        }
    }

    void HandleRotationAndAnim()
    {
        if (agent == null || !agent.isActiveAndEnabled) return;

        float speed = agent.isOnNavMesh ? agent.velocity.magnitude : 0f;
        if (animator)
        {
            animator.SetFloat("Speed", speed);
            animator.SetBool("isWalking", speed > 0.1f);
        }

        if (speed > 0.05f && agent.desiredVelocity.sqrMagnitude > 0.0001f)
        {
            Vector3 moveDir = new Vector3(agent.desiredVelocity.x, 0f, agent.desiredVelocity.z);
            if (moveDir.sqrMagnitude > 0.0001f)
                SmoothLook(moveDir.normalized);
            return;
        }

        switch (state)
        {
            case AgentState3D.QueueOutside:
                if (followTarget != null) FaceForward(followTarget.forward);
                else if (queueTarget != null) FaceForward(queueTarget.forward);
                else if (entryDoor != null) FacePoint(entryDoor.transform.position);
                break;
            case AgentState3D.Riding:
                if (mySeatOrStand != null) FaceForward(mySeatOrStand.transform.forward);
                break;
            case AgentState3D.PrepareAlight:
                if (exitDoor != null) FacePoint(exitDoor.transform.position);
                break;
            default:
                if (entryDoor != null) FacePoint(entryDoor.transform.position);
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
        Vector3 f = new Vector3(worldForward.x, 0f, worldForward.z);
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
        if (animator) animator.SetBool("isWalking", false);
        return agent.remainingDistance <= threshold;
    }

    public void BeginQueue(Transform queueEntryPoint)
    {
        state = AgentState3D.QueueOutside;
        queueTarget = queueEntryPoint;
        if (animator) animator.ResetTrigger("Seat");
        GoToPoint(queueEntryPoint.position);
        if (agent != null) agent.speed = baseSpeed;
        queueSettled = false;
        lastFollowPos = Vector3.positiveInfinity;
    }

    public void SetQueueFollowTarget(Transform target) { followTarget = target; state = AgentState3D.QueueOutside; }
    public void ClearQueueFollow() { followTarget = null; }

    public void OnQueueTargetRebound()
    {
        queueSettled = false;
        lastFollowPos = Vector3.positiveInfinity;
    }

    public void BeginBoard(DoorGate d)
    {
        entryDoor = d; state = AgentState3D.Boarding;
        if (animator) animator.SetBool("isWalking", true);
        followTarget = null;
    }

    public void BeginRide(SeatSlot slot)
    {
        state = AgentState3D.Riding; mySeatOrStand = slot;
        if (animator) animator.ResetTrigger("Stand");
        if (slot != null && slot.TryReserve(this))
        {
            Transform t = (slot.sitAnchor != null) ? slot.sitAnchor : slot.transform;
            GoToPoint(t.position);
        }
    }

    public void BeginPrepareAlight()
    {
        state = AgentState3D.PrepareAlight;

        if (animator)
        {
            animator.ResetTrigger("Seat");
            animator.SetTrigger("Stand"); // 일어서기
        }

        // ★ 좌석 해제
        if (mySeatOrStand) mySeatOrStand.Release(this);
        mySeatOrStand = null;

        // ★ 여기 추가: 앉을 때 isStopped=true로 멈춰둔 에이전트를 다시 살림
        if (agent != null)
        {
            agent.isStopped = false;
            agent.ResetPath(); // 안전
        }

        // 하차문으로 이동 시작
        if (exitDoor != null) GoToPoint(exitDoor.transform.position);
    }

    public void BeginAlight(Vector3 exitPoint)
    {
        state = AgentState3D.Alighting;
        if (animator)
        {
            animator.ResetTrigger("Seat");
            animator.SetBool("isWalking", true);
        }
        GoToPoint(exitPoint);
    }

    void OnDestroy() { OnAgentDestroyed?.Invoke(this); }
}
