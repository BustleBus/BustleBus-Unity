using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;

[Serializable]
public class BusSearchLocationRoot
{
    public bool success;
    public RouteLocationResult[] result;
}

[Serializable]
public class GoogleSheetLogRequest
{
    public string stopName;     // 정류장 이름
    public string busNumber;    // 노선 번호 (busNo)
    public string vehicleNo;    // 실제 차량 번호
    public int crowd;           // 혼잡도(탑승 인원)
    public string time;         // ISO 타임스탬프
}

[Serializable]
public class RouteLocationResult
{
    public string routeId;
    public string startNodeName;
    public string endNodeName;
    public int routeNo;
    public string routeTp;
    public string status;
    public BusOnRoute[] buses;
}

[Serializable]
public class BusOnRoute
{
    public int nodeOrd;
    public string nodeId;
    public string nodeName;
    public string vehicleNo;
    public string congestionLevel;
}

public class busApiController : MonoBehaviour
{
    [Header("조용 시간대(폴링 중지) 설정 (현재는 사용 안 함, MultiBus에서 사용 권장)")]
    [Tooltip("예: 0 이면 00시, 6 이면 06시 (끝 시간은 포함 X)")]
    [Range(0, 23)]
    public int quietStartHour = 0;

    [Range(0, 23)]
    public int quietEndHour = 6; // end는 '미만'으로 처리

    [Header("Google Sheet (Apps Script)")]
    [Tooltip("JS fetch에서 쓰던 Apps Script URL을 그대로 넣어주세요.")]
    public string googleSheetUrl =
        "https://script.google.com/macros/s/AKfycbzprzkNls7CYg9_gJnnHt5sEKMJYCWd66mPt-KDZoVWfxeh2c4lBB1e1-o8_TjHSWqm/exec";

    [Header("버스 위치 API 기본 설정(로그용)")]
    [Tooltip("예: https://bustlebus-api-198875705973.asia-northeast3.run.app/api/v1/searchLocation")]
    public string searchLocationBaseUrl =
        "https://bustlebus-api-198875705973.asia-northeast3.run.app/api/v1/searchLocation";

    [Tooltip("TAGO cityCode (예: 38030)")]
    public string cityCode = "38030";

    [Tooltip("버스 번호 (문자열로) 예: 160")]
    public string busNo = "160";

    [Header("어느 버스를 따라갈지 (MultiBus에서 세팅)")]
    [Tooltip("특정 차량 번호를 따라가고 싶으면 입력 (예: 경남71자5809).")]
    public string targetVehicleNo = "";

    [Header("정차/승하차 제어용 StopController")]
    public StopController stopController;

    [Header("UI 표시")]
    [Tooltip("현재 정류장 이름을 표시할 텍스트 (예: \"정촌면사무소\")")]
    public TextMeshProUGUI currentStopText;

    [Tooltip("노선/방향 등을 표시할 텍스트 (예: \"160번 버스 (예상 → 석교(회차지))\")")]
    public TextMeshProUGUI routeInfoText;

    [Tooltip("차량 번호를 표시할 텍스트 (예: \"경남71자5809\")")]
    public TextMeshProUGUI vehicleText;

    // 마지막으로 받은 route / bus를 기억해서 UI에서 참조 가능하게
    private RouteLocationResult _lastRoute;
    private BusOnRoute _lastBus;
    // 내부 상태
    private string _lastNodeId = null;
    private bool _isProcessingStop = false;

    void Start()
    {
        if (stopController == null)
        {
            stopController = GetComponentInChildren<StopController>();
        }

        if (stopController == null)
        {
            Debug.LogError("[BusLocationPoller] StopController가 설정되지 않았습니다.");
            enabled = false;
            return;
        }

        // ★ 예전에는 여기서 targetVehicleNo 확인 후 BeginPolling() 했지만,
        //    이제는 MultiBusSimulationManager가 위치를 넘겨주는 구조이므로
        //    별도로 코루틴을 돌리지 않습니다.
    }
    public void ClearUI()
    {
        if (currentStopText != null)
            currentStopText.text = "";

        if (routeInfoText != null)
            routeInfoText.text = "";

        if (vehicleText != null)
            vehicleText.text = "";
    }

    void OnDisable()
    {
        // 폴링 중지 & 상태 리셋
        StopAllCoroutines();

        _lastRoute = null;
        _lastBus = null;
        _lastNodeId = null;
        _isProcessingStop = false;

        // UI도 같이 지워주기
        ClearUI();
    }
    /// <summary>
    /// MultiBusSimulationManager에서 route/bus 정보를 전달해 줄 때 호출.
    /// 이 컴포넌트는 더 이상 직접 API를 호출하지 않고, 전달된 위치정보만 사용합니다.
    /// </summary>
    public void ApplyLocationFromManager(RouteLocationResult route, BusOnRoute bus)
    {
        if (!isActiveAndEnabled) return;
        if (route == null || bus == null) return;

        // 타겟 차량 번호가 지정되어 있고, 해당 vehicleNo가 아니면 무시
        if (!string.IsNullOrEmpty(targetVehicleNo) &&
            !string.Equals(targetVehicleNo, bus.vehicleNo, StringComparison.Ordinal))
        {
            return;
        }

        _lastRoute = route;
        _lastBus = bus;

        UpdateUIWithCurrentStop(route, bus);

        // 2) 정류장(nodeId) 변경 여부 체크
        bool stationChanged = false;

        if (_lastNodeId == null || _lastNodeId != bus.nodeId)
        {
            stationChanged = true;
        }

        if (!stationChanged)
        {
            // 같은 정류장에 머무르는 중이라면 아무것도 하지 않음
            return;
        }

        // 새 정류장으로 업데이트
        _lastNodeId = bus.nodeId;

        // 이미 정차 시퀀스를 처리 중이면 중복 실행 방지
        if (_isProcessingStop)
            return;

        StartCoroutine(HandleNewStop(route, bus));
    }

    /// <summary>
    /// 슬롯에 새로운 vehicle을 배정할 때(또는 재사용할 때) 상태 초기화용.
    /// MultiBusSimulationManager.AssignBusToSlot()에서 호출하면 됨.
    /// </summary>
    public void ResetBusState()
    {
        _lastRoute = null;
        _lastBus = null;
        _lastNodeId = null;
        _isProcessingStop = false;
    }

    // ==== 아래부터는 기존 로직 재사용 (UI + 정차/시트 + 시트 로그) ====

    private void UpdateUIWithCurrentStop(RouteLocationResult route, BusOnRoute bus)
    {
        // 현재 정류장 이름
        if (currentStopText != null)
        {
            currentStopText.text = $"현재 정류장 : {bus.nodeName}";
        }

        // 노선 / 방향 정보
        if (routeInfoText != null)
        {
            routeInfoText.text = $"{route.routeNo}번 버스 ({route.startNodeName} → {route.endNodeName})";
        }

        // 차량 번호
        if (vehicleText != null)
        {
            vehicleText.text = $"차량 : {bus.vehicleNo}";
        }
    }

    IEnumerator HandleNewStop(RouteLocationResult route, BusOnRoute bus)
    {
        _isProcessingStop = true;

        // 1) StopController에게 "정류장 도착 → 승하차 시퀀스" 실행 요청
        if (stopController != null)
        {
            yield return stopController.RunStopCycleOnce(this);

            // 2) 정차가 끝난 순간의 버스 내부 인원 수
            int passengerCount = stopController.insideAgents.Count;
            Debug.Log($"[BusLocationPoller] 정류장 {bus.nodeName} 승하차 후 탑승 인원: {passengerCount}");

            // 3) Google Sheet(Apps Script)로 로그 전송
            if (!string.IsNullOrEmpty(googleSheetUrl))
            {
                int crowd = GetCrowdCountForSheet();
                yield return SendGoogleSheetLog(bus, crowd);
            }
        }

        _isProcessingStop = false;
    }

    int GetCrowdCountForSheet()
    {
        int crowd = -1;

        // 1) 같은 프리팹 안의 BusViewSlot에서 YOLO 결과 가져오기
        var slot = GetComponentInParent<BusViewSlot>();
        if (slot != null && slot.yoloInference != null)
        {
            crowd = slot.yoloInference.CurrentDetectionCount;
        }

        // 2) YOLO 결과가 없거나 -1이면, 시뮬레이션 내부 탑승 인원으로 대체
        if (crowd < 0 && stopController != null)
        {
            crowd = stopController.insideAgents.Count;
        }

        return Mathf.Max(0, crowd);
    }

    IEnumerator SendGoogleSheetLog(BusOnRoute bus, int crowd)
    {
        GoogleSheetLogRequest payload = new GoogleSheetLogRequest
        {
            stopName = bus.nodeName,   // 정류장
            busNumber = busNo,         // 노선 번호
            vehicleNo = bus.vehicleNo, // 실제 차량 번호
            crowd = crowd,
            time = DateTime.UtcNow.ToString("o")
        };

        string json = JsonUtility.ToJson(payload);
        byte[] body = System.Text.Encoding.UTF8.GetBytes(json);

        using (UnityWebRequest www = new UnityWebRequest(googleSheetUrl, "POST"))
        {
            www.uploadHandler = new UploadHandlerRaw(body);
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");

            Debug.Log("[BusLocationPoller] POST GoogleSheet: " + json);

            yield return www.SendWebRequest();

            if (www.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning("[BusLocationPoller] GoogleSheet 전송 실패: " + www.error);
            }
            else
            {
                Debug.Log("[BusLocationPoller] GoogleSheet 전송 성공: " + www.downloadHandler.text);
            }
        }
    }

    [Serializable]
    public class PassengerLogRequest
    {
        public string cityCode;
        public string busNo;

        public string routeId;
        public int routeNo;
        public string startNodeName;
        public string endNodeName;

        public string vehicleNo;

        public int nodeOrd;
        public string nodeId;
        public string nodeName;
        public string congestionLevel;

        public int passengerCount;
        public long timestamp;
    }
}
