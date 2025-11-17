using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

public class MultiBusSimulationManager : MonoBehaviour
{
    [Header("API 설정")]
    public string searchLocationBaseUrl =
        "https://bustlebus-api-198875705973.asia-northeast3.run.app/api/v1/searchLocation";
    public string cityCode = "38030";
    public string busNo = "160";
    public float pollIntervalSeconds = 10f;

    [Header("조용 시간대(전체 폴링 중지)")]
    [Range(0, 23)]
    public int quietStartHour = 0;   // 00시
    [Range(0, 23)]
    public int quietEndHour = 6;     // 06시 (미만)

    [Header("슬롯들 (위에서 아래, 왼쪽에서 오른쪽 순서로 넣기)")]
    public BusViewSlot[] slots;

    // vehicleNo -> slot 매핑
    private readonly Dictionary<string, BusViewSlot> vehicleToSlot = new();

    void Start()
    {
        if (slots != null)
        {
            foreach (var s in slots)
            {
                if (s != null) s.InitSlot();
            }
        }

        StartCoroutine(PollLoop());
    }

    bool IsQuietHour(int hour)
    {
        // ex) 0~6 같은 일반 케이스
        if (quietStartHour < quietEndHour)
            return hour >= quietStartHour && hour < quietEndHour;

        // ex) 22~5 같이 자정을 넘는 케이스
        if (quietStartHour > quietEndHour)
            return hour >= quietStartHour || hour < quietEndHour;

        // start == end 인 경우: 조용 시간대 없음으로 처리
        return false;
    }

    IEnumerator PollLoop()
    {
        var wait = new WaitForSeconds(pollIntervalSeconds);

        while (true)
        {
            int hour = DateTime.Now.Hour;
            if (!IsQuietHour(hour))
            {
                yield return RequestAndUpdateBuses();
            }

            yield return wait;
        }
    }

    IEnumerator RequestAndUpdateBuses()
    {
        string url = $"{searchLocationBaseUrl}?cityCode={cityCode}&busNo={busNo}";

        using (UnityWebRequest www = UnityWebRequest.Get(url))
        {
            www.timeout = 10;
            yield return www.SendWebRequest();

            if (www.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning("[MultiBus] searchLocation 실패: " + www.error);
                yield break;
            }

            string json = www.downloadHandler.text;
            BusSearchLocationRoot root = null;
            try
            {
                root = JsonUtility.FromJson<BusSearchLocationRoot>(json);
            }
            catch
            {
                Debug.LogError("[MultiBus] JSON 파싱 실패");
                yield break;
            }

            if (root == null || !root.success || root.result == null)
            {
                Debug.LogWarning("[MultiBus] 결과 없음");
                yield break;
            }

            // 이번 폴링에서 발견된 vehicleNo 목록
            HashSet<string> aliveVehicles = new HashSet<string>();
            // 중복 vehicleNo 필터
            HashSet<string> uniqueThisPoll = new HashSet<string>();

            foreach (var route in root.result)
            {
                if (route == null || route.buses == null) continue;

                foreach (var bus in route.buses)
                {
                    if (bus == null || string.IsNullOrEmpty(bus.vehicleNo))
                        continue;

                    // 한 폴링에서 같은 번호가 여러 번 내려오면 한 번만 처리
                    if (!uniqueThisPoll.Add(bus.vehicleNo))
                        continue;

                    aliveVehicles.Add(bus.vehicleNo);

                    // 이미 슬롯이 있으면 갱신
                    if (vehicleToSlot.TryGetValue(bus.vehicleNo, out var slot))
                    {
                        UpdateSlot(slot, route, bus);
                    }
                    else
                    {
                        // 새 차량 → 빈 슬롯 하나 찾기
                        var freeSlot = FindFreeSlot();
                        if (freeSlot != null)
                        {
                            AssignBusToSlot(freeSlot, route, bus);
                        }
                    }
                }
            }

            // 응답에서 사라진 vehicleNo → 슬롯 해제
            List<string> toRemove = new List<string>();
            foreach (var kv in vehicleToSlot)
            {
                if (!aliveVehicles.Contains(kv.Key))
                {
                    BusViewSlot slot = kv.Value;
                    if (slot != null)
                    {
                        slot.HideView(clearTexts: true);
                    }
                    toRemove.Add(kv.Key);
                }
            }
            foreach (var v in toRemove)
            {
                vehicleToSlot.Remove(v);
            }
        }
    }

    BusViewSlot FindFreeSlot()
    {
        if (slots == null) return null;

        foreach (var s in slots)
        {
            if (s != null && !s.isUsed)
                return s;
        }
        return null;
    }

    void AssignBusToSlot(BusViewSlot slot, RouteLocationResult route, BusOnRoute bus)
    {
        slot.isUsed = true;
        slot.vehicleNo = bus.vehicleNo;

        // 타이틀 / 정류장 텍스트
        slot.SetTitle(
            $"{route.routeNo}번 버스 ({route.startNodeName} → {route.endNodeName})\n차량 : {bus.vehicleNo}"
        );
        slot.SetStopName(bus.nodeName);

        // 이 슬롯에 연결된 busApiController 에 타겟 차량 번호 지정
        if (slot.apiController != null)
        {
            slot.apiController.cityCode = cityCode;
            slot.apiController.busNo = busNo;
            slot.apiController.targetVehicleNo = bus.vehicleNo;

            // 새 vehicle 배정 시 상태 초기화
            slot.apiController.ResetBusState();
        }

        // 화면/YOLO/API 켜기
        slot.ShowView();

        // ★ 여기서도 한 번 위치 업데이트 전달 (초기 정류장 표시 + 정차 시퀀스 반영)
        if (slot.apiController != null)
        {
            slot.apiController.ApplyLocationFromManager(route, bus);
        }

        if (!vehicleToSlot.ContainsKey(bus.vehicleNo))
            vehicleToSlot.Add(bus.vehicleNo, slot);
        else
            vehicleToSlot[bus.vehicleNo] = slot;

        Debug.Log($"[MultiBus] 버스 배정: vehicle={bus.vehicleNo} -> slot={slot.name}");
    }

    public void ChangeBusNo(string newBusNo)
    {
        if (string.IsNullOrEmpty(newBusNo))
        {
            Debug.LogWarning("[MultiBus] 빈 노선번호는 적용할 수 없습니다.");
            return;
        }

        if (newBusNo == busNo)
        {
            Debug.Log($"[MultiBus] 동일 노선 ({busNo}) 이므로 변경 생략");
            return;
        }

        Debug.Log($"[MultiBus] 노선 변경: {busNo} → {newBusNo}");
        busNo = newBusNo;

        // ========================================
        // 1. 모든 슬롯 완전 초기화
        // ========================================
        foreach (var slot in slots)
        {
            if (slot == null) continue;

            slot.isUsed = false;
            slot.vehicleNo = "";
            slot.HideView(clearTexts: true);

            // apiController 초기화
            if (slot.apiController != null)
            {
                slot.apiController.targetVehicleNo = "";
                slot.apiController.ResetBusState();
                slot.apiController.enabled = false;     // 완전히 꺼두기
            }



            // StopController 내부 승객도 초기화
            if (slot.apiController != null && slot.apiController.stopController != null)
            {
                var sc = slot.apiController.stopController;
                sc.insideAgents.Clear();
            }
        }

        // ========================================
        // 2. vehicleToSlot 사전 초기화
        // ========================================
        vehicleToSlot.Clear();

        // ========================================
        // 3. UI 즉시 갱신
        // ========================================
        // 필요하면 “로드 중…” 같은 UI 넣어도 가능

        // ========================================
        // 4. 새로운 노선 버스 목록 즉시 갱신
        // ========================================
        StopAllCoroutines();
        StartCoroutine(PollLoop());                // PollLoop 다시 시작
        StartCoroutine(RequestAndUpdateBuses());   // 첫 API 바로 실행
    }


    void UpdateSlot(BusViewSlot slot, RouteLocationResult route, BusOnRoute bus)
    {
        // 현재 정류장 텍스트 갱신
        slot.SetStopName(bus.nodeName);

        // ★ 여기서도 버스 내부 로직에 위치 업데이트 전달
        if (slot.apiController != null)
        {
            slot.apiController.ApplyLocationFromManager(route, bus);
        }
    }
}
