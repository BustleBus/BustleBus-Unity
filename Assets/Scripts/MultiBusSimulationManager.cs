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

    IEnumerator PollLoop()
    {
        var wait = new WaitForSeconds(pollIntervalSeconds);

        while (true)
        {
            yield return RequestAndUpdateBuses();
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

                    // 이미 슬롯이 있으면 텍스트만 갱신
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

            // 여기서 직접 폴링 시작 (Start에서 자동 시작 X)
            slot.apiController.BeginPolling();
        }

        // 화면/YOLO/API 켜기
        slot.ShowView();

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

        Debug.Log($"[MultiBus] 추적 노선 변경: {busNo} -> {newBusNo}");
        busNo = newBusNo;

        // 이전 노선의 vehicle → 슬롯 매핑/화면 초기화
        foreach (var kv in vehicleToSlot)
        {
            var slot = kv.Value;
            if (slot != null)
            {
                slot.HideView(clearTexts: true);
            }
        }
        vehicleToSlot.Clear();

        // 바로 한 번 갱신 돌리고 싶으면
        StartCoroutine(RequestAndUpdateBuses());
    }
    void UpdateSlot(BusViewSlot slot, RouteLocationResult route, BusOnRoute bus)
    {
        // 현재 정류장 텍스트만 갱신
        slot.SetStopName(bus.nodeName);
    }
}
