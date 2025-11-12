using UnityEngine;
using System.Collections;
using System.IO;
using System.Threading; 
public class ScreenshotHandler : MonoBehaviour
{
    [Header("수동 스크린샷")]
    [Tooltip("스크린샷을 찍을 키 (기본값: K)")]
    public KeyCode screenshotKey = KeyCode.K;

    [Header("자동 스크린샷")]
    [Tooltip("자동 스크린샷 기능을 사용할지 여부")]
    public bool enableAutoScreenshot = false;

    [Tooltip("스크린샷 간의 시간 간격 (초)")]
    public float autoScreenshotInterval = 5f;

    [Header("저장 설정")]
    [Tooltip("스크린샷이 저장될 폴더 이름 (프로젝트 폴더 기준)")]
    public string folderName = "Screenshots";
    private Coroutine autoScreenshotCoroutine;
    void Start()
    {
        if (enableAutoScreenshot)
        {
            StartAutoScreenshot();
        }
    }
    void Update()
    {
        // 지정된 키를 눌렀을 때 스크린샷 함수 호출
        if (Input.GetKeyDown(screenshotKey))
        {
            TakeScreenshot();
        }
    }
    // 자동 스크린샷 코루틴 시작 함수
    public void StartAutoScreenshot()
    {
        // 이미 코루틴이 실행 중이면 중지
        if (autoScreenshotCoroutine != null)
        {
            StopCoroutine(autoScreenshotCoroutine);
        }

        // 간격이 0보다 커야 실행
        if (autoScreenshotInterval > 0)
        {
            autoScreenshotCoroutine = StartCoroutine(AutoScreenshotRoutine());
            Debug.Log($"[ScreenshotHandler] Auto-screenshot started with interval: {autoScreenshotInterval}s");
        }
        else
        {
            Debug.LogWarning("[ScreenshotHandler] Auto-screenshot interval is zero or negative. Cannot start.");
        }
    }
    // 일정 시간마다 스크린샷을 찍는 코루틴
    IEnumerator AutoScreenshotRoutine()
    {
        // 무한 루프
        while (true)
        {
            // 지정된 시간만큼 대기 (Time.timeScale의 영향을 받음, 일시정지 시 멈춤)
            yield return new WaitForSeconds(autoScreenshotInterval);

            // 일시정지 상태가 아닐 때만 스크린샷 촬영
            if (Time.timeScale > 0)
            {
                TakeScreenshot();
            }
        }
    }
    void TakeScreenshot()
    {
        // 1. 저장 경로 설정
        string folderPath = Path.Combine(Application.dataPath, folderName);

        // 폴더가 없으면 생성
        if (!Directory.Exists(folderPath))
        {
            Directory.CreateDirectory(folderPath);
            Debug.Log($"[ScreenshotHandler] Created folder: {folderPath}");
        }

        // 2. 파일 이름 설정 (날짜와 시간을 포함하여 중복 방지)
        string timeStamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string fileName = $"Screenshot_{timeStamp}.png";
        string fullPath = Path.Combine(folderPath, fileName);

        // 3. 스크린샷 촬영 및 저장
        ScreenCapture.CaptureScreenshot(fullPath);

        Debug.Log($"[ScreenshotHandler] Screenshot saved to: {fullPath}");
    }

    void OnGUI()
    {
        // 런타임 시 기능을 쉽게 확인하기 위한 GUI 표시 (선택 사항)
        GUI.Label(new Rect(10, Screen.height - 30, 300, 20), $"Press '{screenshotKey.ToString()}' to take a screenshot.");
    }
}