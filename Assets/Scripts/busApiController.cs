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
    [Header("조용 시간대(폴링 중지) 설정")]
    [Tooltip("예: 0 이면 00시, 6 이면 06시 (끝 시간은 포함 X)")]
    [Range(0, 23)]
    public int quietStartHour = 0;

    [Range(0, 23)]
    public int quietEndHour = 6; // end는 '미만'으로 처리
    [Header("Google Sheet (Apps Script)")]
    [Tooltip("JS fetch에서 쓰던 Apps Script URL을 그대로 넣어주세요.")]
    public string googleSheetUrl =
    "https://script.google.com/macros/s/AKfycbzprzkNls7CYg9_gJnnHt5sEKMJYCWd66mPt-KDZoVWfxeh2c4lBB1e1-o8_TjHSWqm/exec";

    private bool _pollingStarted = false;   // 이미 시작했는지 체크
    [Header("버스 위치 API 기본 설정")]
    [Tooltip("예: https://bustlebus-api-198875705973.asia-northeast3.run.app/api/v1/searchLocation")]
    public string searchLocationBaseUrl =
           "https://bustlebus-api-198875705973.asia-northeast3.run.app/api/v1/searchLocation";

    [Tooltip("TAGO cityCode (예: 38030)")]
    public string cityCode = "38030";

    [Tooltip("버스 번호 (문자열로) 예: 160")]
    public string busNo = "160";

    [Header("어느 버스를 따라갈지")]
    [Tooltip("특정 차량 번호를 따라가고 싶으면 입력 (예: 경남71자5809). 비워두면 첫 번째 버스를 사용.")]
    public string targetVehicleNo = "";

    [Header("폴링 주기 (초)")]
    public float pollIntervalSeconds = 10f;

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

        // ★ 아직 MultiBus에서 targetVehicleNo를 안 넣어줬으면
        //    여기서는 아무 것도 안 하고 대기한다.
        if (string.IsNullOrEmpty(targetVehicleNo))
        {
            // MultiBusSimulationManager.AssignBusToSlot()에서
            // BeginPolling()을 호출해 줄 것.
            return;
        }

        BeginPolling();
    }

    //  외부(MultiBus)에서 호출해서 폴링을 시작하는 함수
    public void BeginPolling()
    {
        if (_pollingStarted)
            return; // 이미 시작했으면 또 시작 안 함

        _pollingStarted = true;
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
            if (IsQuietHour(hour))
            {
              
            }
            else
            {
                yield return RequestAndProcessLocation();
            }

            yield return wait;
        }
    }

    IEnumerator RequestAndProcessLocation()
    {
        // 쿼리 붙이기
        string url = $"{searchLocationBaseUrl}?cityCode={cityCode}&busNo={busNo}";
        Debug.Log("[BusLocationPoller] GET " + url);

        using (UnityWebRequest www = UnityWebRequest.Get(url))
        {
            www.timeout = 10;
            yield return www.SendWebRequest();

            if (www.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning("[BusLocationPoller] 위치 조회 실패: " + www.error);
                yield break;
            }

            string json = www.downloadHandler.text;
            //Debug.Log("[BusLocationPoller] 응답: " + json);

            BusSearchLocationRoot root = null;
            try
            {
                root = JsonUtility.FromJson<BusSearchLocationRoot>(json);
            }
            catch (Exception e)
            {
                Debug.LogError("[BusLocationPoller] JSON 파싱 에러: " + e);
                yield break;
            }

            if (root == null || !root.success || root.result == null || root.result.Length == 0)
            {
                Debug.LogWarning("[BusLocationPoller] 유효한 위치 정보가 없습니다.");
                yield break;
            }

            // 1) 어떤 route (상행/하행) + 어떤 bus를 따라갈지 결정
            RouteLocationResult chosenRoute = null;
            BusOnRoute chosenBus = null;

            foreach (var route in root.result)
            {
                if (route.buses == null || route.buses.Length == 0)
                    continue;

                foreach (var bus in route.buses)
                {
                    if (!string.IsNullOrEmpty(targetVehicleNo))
                    {
                        if (bus.vehicleNo == targetVehicleNo)
                        {
                            chosenRoute = route;
                            chosenBus = bus;
                            break;
                        }
                    }
                    else
                    {
                        // 타겟 차량 미설정이면 제일 먼저 찾은 버스 사용
                        chosenRoute = route;
                        chosenBus = bus;
                        break;
                    }
                }

                if (chosenBus != null) break;
            }

            if (chosenBus == null)
            {
                Debug.LogWarning("[BusLocationPoller] 조건에 맞는 버스를 찾지 못했습니다.");
                yield break;
            }
            _lastRoute = chosenRoute;
            _lastBus = chosenBus;

            UpdateUIWithCurrentStop(chosenRoute, chosenBus);
            // 디버그
            Debug.Log($"[BusLocationPoller] 선택된 버스: routeId={chosenRoute.routeId}, " +
                      $"vehicleNo={chosenBus.vehicleNo}, nodeOrd={chosenBus.nodeOrd}, nodeId={chosenBus.nodeId}, nodeName={chosenBus.nodeName}");

            // 2) 정류장(nodeId) 변경 여부 체크
            bool stationChanged = false;

            if (_lastNodeId == null || _lastNodeId != chosenBus.nodeId)
            {
                stationChanged = true;
            }

            if (!stationChanged)
            {
                // 같은 정류장에 머무르는 중이라면 아무것도 하지 않음
                yield break;
            }

            // 새 정류장으로 업데이트
            _lastNodeId = chosenBus.nodeId;

            // 이미 정차 시퀀스를 처리 중이면 중복 실행 방지
            if (_isProcessingStop)
                yield break;

            StartCoroutine(HandleNewStop(chosenRoute, chosenBus));
        }
    }
    private void UpdateUIWithCurrentStop(RouteLocationResult route, BusOnRoute bus)
    {
        // 현재 정류장 이름
        if (currentStopText != null)
        {
            currentStopText.text = $"현재 정류장 : {bus.nodeName}";
            // 예: 색을 잠깐 바꾸거나 DOFade로 연출 주고싶으면 여기서 처리
        }

        // 노선 / 방향 정보
        if (routeInfoText != null)
        {
            // startNodeName → endNodeName 방향 텍스트
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
            // StopController에 public Coroutine RunStopCycleOnce(MonoBehaviour caller) 추가해두셨다는 가정
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
            crowd = stopController.insideAgents.Count;  // 버스 내부 탑승 인원 리스트 :contentReference[oaicite:5]{index=5}
        }

        return Mathf.Max(0, crowd);
    }


    IEnumerator SendGoogleSheetLog(BusOnRoute bus, int crowd)
    {
        GoogleSheetLogRequest payload = new GoogleSheetLogRequest
        {
            stopName = bus.nodeName,      // 정류장
            busNumber = busNo,            // 기존 busNo (노선)
            vehicleNo = bus.vehicleNo,    // 실제 차량 번호
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
