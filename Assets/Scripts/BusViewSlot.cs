using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class BusViewSlot : MonoBehaviour
{
    [Header("View 구성요소")]
    public Camera slotCamera;          // 이 슬롯 화면을 찍는 카메라
    public RenderTexture renderTexture;
    public RawImage rawImage;          // UI에 붙어있는 RawImage

    [Header("YOLO (선택 사항)")]
    public YoloInference yoloInference;

    [Header("버스 프리팹 컨트롤러")]
    // 이 슬롯이 담당하는 버스 프리팹 안에 붙어 있는 busApiController
    public busApiController apiController;

    [Header("부가 정보 UI")]
    public TextMeshProUGUI titleText;   // "160번 버스 (예상 → 석교)"
    public TextMeshProUGUI stopText;    // "현재 정류장 : 공단시장"

    [HideInInspector] public bool isUsed;
    [HideInInspector] public string vehicleNo;   // 이 슬롯이 담당하는 차량 번호

    // ==========================
    //  초기화 / 비우기
    // ==========================
    public void InitSlot()
    {
        isUsed = false;
        vehicleNo = null;

        // 카메라와 RT 연결만 해두고, 켜고 끄는 건 선택사항
        if (slotCamera != null && renderTexture != null)
            slotCamera.targetTexture = renderTexture;

        if (rawImage != null && renderTexture != null)
            rawImage.texture = renderTexture;
        // 처음에는 카메라도 꺼둔다
        if (slotCamera != null)
            slotCamera.enabled = false;
        // 처음엔 화면/YOLO/API 다 꺼져 있게
        if (rawImage != null) rawImage.enabled = false;
        if (yoloInference != null)
        {
            yoloInference.enabled = false;
            yoloInference.targetUIRect = null;
        }
        if (apiController != null)
        {
            apiController.enabled = false;
            apiController.targetVehicleNo = "";
        }

        SetTitle("");
        SetStopName("");
    }

    /// <summary>
    /// 이 슬롯을 더 이상 사용하지 않을 때 완전히 끄기
    /// </summary>
    public void HideView(bool clearTexts = true)
    {
        isUsed = false;
        vehicleNo = null;
        //  화면 관련 전부 OFF
        if (slotCamera != null)
            slotCamera.enabled = false;

        // UI / YOLO / API 비활성화
        if (rawImage != null) rawImage.enabled = false;

        if (yoloInference != null)
        {
            yoloInference.enabled = false;
            yoloInference.targetUIRect = null;
        }

        if (apiController != null)
        {
            apiController.enabled = false;
            apiController.targetVehicleNo = "";
        }

        if (clearTexts)
        {
            SetTitle("");
            SetStopName("");
        }
    }

    // ==========================
    //  슬롯 활성화
    // ==========================
    public void ShowView()
    {
        // 화면 켜기
        if (rawImage != null)
            rawImage.enabled = true;
        //다시 쓸 때 카메라를 꼭 켜준다 (rect는 건드리지 않음)
        if (slotCamera != null)
            slotCamera.enabled = true;

        // YOLO 켜기
        if (yoloInference != null)
        {
            yoloInference.enabled = true;
            if (rawImage != null)
                yoloInference.targetUIRect = rawImage.rectTransform;
        }

        // 이 슬롯에 연결된 busApiController도 켠다
        if (apiController != null)
            apiController.enabled = true;

        isUsed = true;
    }

    // ==========================
    //  텍스트
    // ==========================
    public void SetTitle(string text)
    {
        if (titleText != null)
            titleText.text = text;
    }

    public void SetStopName(string stopName)
    {
        if (stopText != null)
            stopText.text = $"현재 정류장 : {stopName}";
    }
}
