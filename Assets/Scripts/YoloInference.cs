// YoloInference_Sentis20_Fixed.cs  (Unity Sentis 2.0)
// - using Unity.Sentis;
// - Tensor<float>, Schedule(), PeekOutput(), ReadbackAndClone() 사용
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Sentis;

public class YoloInference : MonoBehaviour
{
    public enum InputLayout { NHWC, NCHW }

    [Header("Camera / Model")]
    public Camera sourceCam;
    public ModelAsset modelAsset;             // .onnx 임포트한 Sentis ModelAsset
    public BackendType backend = BackendType.GPUCompute;

    [Header("Input shape (학습 크기와 동일)")]
    public InputLayout layout = InputLayout.NHWC; // 흔히 1xHxWx3
    public int inputW = 640;
    public int inputH = 640;

    [Header("YOLO parse")]
    [Range(0, 1f)] public float confThreshold = 0.05f;
    [Range(0, 1f)] public float nmsIoU = 0.45f;
    public bool applySigmoid = true;

    // 내부
    Worker worker;
    Model runtimeModel;
    RenderTexture rt;
    List<Det> finalDets = new();
    GUIStyle label; Texture2D white;

    void Awake()
    {
        if (sourceCam == null) sourceCam = Camera.main;
        runtimeModel = ModelLoader.Load(modelAsset);
        worker = new Worker(runtimeModel, backend);

        rt = new RenderTexture(inputW, inputH, 0, RenderTextureFormat.ARGB32);
        label = new GUIStyle { fontSize = 18, normal = { textColor = Color.white } };
        white = new Texture2D(1, 1); white.SetPixel(0, 0, Color.white); white.Apply();
    }

    void OnDestroy()
    {
        worker?.Dispose();
        if (rt) rt.Release();
        if (white) Destroy(white);
    }

    void OnEnable() => StartCoroutine(InferLoop());
    void OnDisable() => StopAllCoroutines();

    IEnumerator InferLoop()
    {
        var wait = new WaitForSeconds(1f / 6f);
        while (true) { RunOnce(); yield return wait; }
    }

    // 유틸: 원시 4값 -> Rect 변환 (cxcywh 또는 x1y1x2y2, 정규화/픽셀 자동 판별)
    Rect MakeRectFromRaw(float a, float b, float c, float d, int W, int H)
    {
        // 1) “x1,y1,x2,y2”로 보이는지 판별 (x2>x1, y2>y1, 차이가 보통 1보다 큼)
        bool looksXYXY = (c > a && d > b && (c - a) > 1f && (d - b) > 1f);

        float x, y, w, h;

        if (looksXYXY)
        {
            // x1,y1,x2,y2
            float x1 = a, y1 = b, x2 = c, y2 = d;
            // 2) 정규화로 보이면 픽셀 변환
            bool norm = (x2 <= 1.5f && y2 <= 1.5f && x1 >= 0f && y1 >= 0f);
            if (norm) { x1 *= W; x2 *= W; y1 *= H; y2 *= H; }
            x = x1; y = y1; w = Mathf.Max(0f, x2 - x1); h = Mathf.Max(0f, y2 - y1);
        }
        else
        {
            // cx,cy,w,h
            float cx = a, cy = b, ww = c, hh = d;
            bool norm = (Mathf.Max(Mathf.Abs(cx), Mathf.Abs(cy), ww, hh) <= 1.5f);
            if (norm) { cx *= W; cy *= H; ww *= W; hh *= H; }
            x = cx - ww * 0.5f; y = cy - hh * 0.5f; w = ww; h = hh;
        }

        // 3) 화면 밖 값 클램프
        x = Mathf.Clamp(x, 0, W); y = Mathf.Clamp(y, 0, H);
        if (x + w > W) w = W - x;
        if (y + h > H) h = H - y;

        return new Rect(x, y, w, h);
    }

    void RunOnce()
    {
        // 1) 카메라 캡처
        sourceCam.targetTexture = rt;
        sourceCam.Render();
        sourceCam.targetTexture = null;

        // 2) RT -> Tensor<float> (NCHW + RGB 3채널로 강제)
        var tfm = new TextureTransform()
            .SetTensorLayout(TensorLayout.NCHW)    // 모델이 (1,3,H,W) 기대
            .SetDimensions(width: inputW, height: inputH, channels: 3)  // ★ 3채널
            .SetChannelSwizzle(ChannelSwizzle.RGBA); // 채널 순서 명시

        using Tensor<float> input = TextureConverter.ToTensor(rt, tfm);

        // 3) 추론
        worker.Schedule(input);

        // 4) output0만 읽기
        var tGpu = worker.PeekOutput("output0") as Tensor<float>;
        if (tGpu == null) { Debug.LogError("[Sentis] output0 is null"); return; }

        // ★ GPU→CPU로 복제 후 사용
        using var t = tGpu.ReadbackAndClone();

        // shape 확인 (rank와 축 길이 안전하게)
        var s = t.shape;
        int r = s.rank;
        int n0 = (r > 0) ? s[0] : 1;
        int n1 = (r > 1) ? s[1] : 1;
        int n2 = (r > 2) ? s[2] : 1;
        int n3 = (r > 3) ? s[3] : 1;
        Debug.Log($"[Sentis] output0 shape (rank={r}): ({n0},{n1},{n2}{(r > 3 ? ("," + n3) : "")})");

        // 5) 파싱
        List<Det> dets;
        if (r == 3 && n0 == 1 && n1 == 6)
        {
            // ★ 1x6xN 형태 전용 파서
            dets = ParseYolo_1x6xN(t, inputW, inputH, confThreshold, applySigmoid);
        }
        else if (r == 4 && n0 == 1 && n1 >= 6)
        {
            // 1x(5+nc)xSxS 형태 등
            dets = ParseYolo_1xCxSxS(t, inputW, inputH, confThreshold, applySigmoid);
        }
        else
        {
            // 모양이 애매하면 범용 파서 시도 (예: 1x5x8400x1, 1x8400x5x1 등)
            dets = ParseYolo(t, inputW, inputH, confThreshold, applySigmoid);
        }

        // 6) NMS
        finalDets = NMS(dets, nmsIoU);
    }

    // ===== YOLO parsing =====
    struct Det { public Rect box; public float conf; public int cls; }
    float Sigmoid(float x) => 1f / (1f + Mathf.Exp(-x));

    Rect ToRect(float cx, float cy, float w, float h, int W, int H)
    {
        bool pixel = (cx > 1f || cy > 1f || w > 1f || h > 1f);
        float sx = pixel ? 1f : W, sy = pixel ? 1f : H;
        return new Rect((cx - w / 2f) * sx, (cy - h / 2f) * sy, w * sx, h * sy);
    }

    // (1,6,N) 전용 파서
    List<Det> ParseYolo_1x6xN(Tensor<float> t, int W, int H, float thr, bool doSigmoid)
    {
        var list = new List<Det>();
        var s = t.shape;            // rank=3, dims: [1,6,N]
        int D0 = (s.rank > 0) ? s[0] : 1;   // =1
        int D1 = (s.rank > 1) ? s[1] : 1;   // =6
        int D2 = (s.rank > 2) ? s[2] : 1;   // =8400 등
        if (D0 != 1 || D1 < 6 || D2 <= 0) return list;

        // 선형 인덱서: idx = (c * D2) + i  (n=0 가정)
        static float Read3D(Tensor<float> T, int D1, int D2, int n, int c, int i)
        {
            if (n != 0) return 0f;
            if (c < 0 || c >= D1 || i < 0 || i >= D2) return 0f;
            int idx = (c * D2) + i;
            return T[idx];
        }

        for (int i = 0; i < D2; i++)
        {
            float cx = Read3D(t, D1, D2, 0, 0, i);
            float cy = Read3D(t, D1, D2, 0, 1, i);
            float ww = Read3D(t, D1, D2, 0, 2, i);
            float hh = Read3D(t, D1, D2, 0, 3, i);
            float obj = Read3D(t, D1, D2, 0, 4, i);
            float cls = Read3D(t, D1, D2, 0, 5, i);

            if (doSigmoid) { obj = Sigmoid(obj); cls = Sigmoid(cls); }
            float conf = obj * Mathf.Clamp01(cls);
            if (conf < thr) continue;

            var r = MakeRectFromRaw(cx, cy, ww, hh, W, H);
            list.Add(new Det { box = r, conf = conf, cls = 0 });
        }
        return list;
    }

    // 1x(5+nc)xSxS 계열
    List<Det> ParseYolo_1xCxSxS(Tensor<float> t, int W, int H, float thr, bool doSigmoid)
    {
        var list = new List<Det>();
        var s = t.shape; // (1,C,H,W)
        int C = s[1], HH = s[2], WW = s[3];
        int clsCount = Mathf.Max(0, C - 5);

        for (int y = 0; y < HH; y++)
        {
            for (int x = 0; x < WW; x++)
            {
                float cx = t[0, 0, y, x];
                float cy = t[0, 1, y, x];
                float ww = t[0, 2, y, x];
                float hh = t[0, 3, y, x];
                float obj = t[0, 4, y, x];
                if (doSigmoid) obj = Sigmoid(obj);

                int best = 0; float clsProb = 1f;
                if (clsCount > 0)
                {
                    float bp = float.NegativeInfinity;
                    for (int c = 0; c < clsCount; c++)
                    {
                        float lg = t[0, 5 + c, y, x];
                        float p = doSigmoid ? Sigmoid(lg) : lg;
                        if (p > bp) { bp = p; best = c; clsProb = p; }
                    }
                }
                float conf = obj * Mathf.Clamp01(clsProb);
                if (conf >= thr)
                    list.Add(new Det { box = MakeRectFromRaw(cx, cy, ww, hh, W, H), conf = conf, cls = best });

            }
        }
        return list;
    }

    // 범용 파서: (1,C,N,1) / (1,N,C,1) / (1,5,8400,1) 등
    // 범용 파서: (1,C,N,1) / (1,N,C,1) / (1,5,8400,1) / (1,5,8400) / (1,6,8400) 등
    List<Det> ParseYolo(Tensor<float> t, int W, int H, float thr, bool doSigmoid)
    {
        var list = new List<Det>();

        var s = t.shape;
        Tensor<float> T = t; // 원본 참조
        int N0 = (s.rank > 0) ? s[0] : 1;
        int A = (s.rank > 1) ? s[1] : 1;
        int B = (s.rank > 2) ? s[2] : 1;
        int C4 = 1, Hh = 1, Ww = 1;

        // rank=3 → 4D로 reshape
        if (s.rank == 3 && N0 == 1)
        {
            bool channelsFirst = (A <= B);
            int Ccand = channelsFirst ? A : B;
            int Ncand = channelsFirst ? B : A;

            // Sentis 2.0의 Reshape()은 void → 그냥 호출만 함
            T.Reshape(new TensorShape(1, Ccand, Ncand, 1));

            N0 = 1; C4 = Ccand; Hh = Ncand; Ww = 1;
        }
        else
        {
            C4 = (s.rank > 1) ? s[1] : 1;
            Hh = (s.rank > 2) ? s[2] : 1;
            Ww = (s.rank > 3) ? s[3] : 1;
        }

        // CASE 1) [1, (5+nc), S, S]
        if (N0 == 1 && C4 >= 6 && Hh >= 4 && Ww >= 4)
        {
            int clsCount = C4 - 5;
            for (int y = 0; y < Hh; y++)
            {
                for (int x = 0; x < Ww; x++)
                {
                    float cx = T[0, 0, y, x], cy = T[0, 1, y, x];
                    float ww = T[0, 2, y, x], hh = T[0, 3, y, x];
                    float obj = T[0, 4, y, x]; if (doSigmoid) obj = Sigmoid(obj);

                    int best = 0; float clsProb = 1f;
                    float bp = float.NegativeInfinity;
                    for (int c = 0; c < clsCount; c++)
                    {
                        float lg = T[0, 5 + c, y, x];
                        float p = doSigmoid ? Sigmoid(lg) : lg;
                        if (p > bp) { bp = p; best = c; clsProb = p; }
                    }
                    float conf = obj * Mathf.Clamp01(clsProb);
                    if (conf >= thr)
                        list.Add(new Det { box = MakeRectFromRaw(cx, cy, ww, hh, W, H), conf = conf, cls = best });

                }
            }
            return list;
        }

        // CASE 2) wrapped "3D": [1, C, N, 1]
        bool wrapped3D = (Ww == 1) && (N0 == 1) && (Hh > 4) && (C4 >= 5);
        if (wrapped3D)
        {
            int candidates = Hh;
            int feat = C4;
            int clsCount = Mathf.Max(0, feat - 5);

            for (int i = 0; i < candidates; i++)
            {
                float cx = T[0, 0, i, 0];
                float cy = T[0, 1, i, 0];
                float ww = T[0, 2, i, 0];
                float hh = T[0, 3, i, 0];
                float obj = T[0, 4, i, 0];
                if (doSigmoid) obj = Sigmoid(obj);

                int best = 0; float clsProb = 1f;
                if (clsCount > 0)
                {
                    float bp = float.NegativeInfinity;
                    for (int c = 0; c < clsCount; c++)
                    {
                        float lg = T[0, 5 + c, i, 0];
                        float p = doSigmoid ? Sigmoid(lg) : lg;
                        if (p > bp) { bp = p; best = c; clsProb = p; }
                    }
                }

                float conf = obj * Mathf.Clamp01(clsProb);
                if (conf >= thr)
                    list.Add(new Det { box = ToRect(cx, cy, ww, hh, W, H), conf = conf, cls = best });
            }
            return list;
        }

        Debug.LogWarning($"[YOLO] unsupported output shape (generic): rank={t.shape.rank} dims=({N0},{C4},{Hh},{Ww})");
        return list;
    }




    List<Det> NMS(List<Det> dets, float iouTh)
    {
        dets.Sort((a, b) => b.conf.CompareTo(a.conf));
        var kept = new List<Det>();
        for (int i = 0; i < dets.Count; i++)
        {
            bool keep = true;
            for (int j = 0; j < kept.Count; j++)
            {
                var a = dets[i].box; var b = kept[j].box;
                float x1 = Mathf.Max(a.xMin, b.xMin), y1 = Mathf.Max(a.yMin, b.yMin);
                float x2 = Mathf.Min(a.xMax, b.xMax), y2 = Mathf.Min(a.yMax, b.yMax);
                float inter = Mathf.Max(0, x2 - x1) * Mathf.Max(0, y2 - y1);
                float uni = a.width * a.height + b.width * b.height - inter;
                float iou = (uni <= 0) ? 0 : (inter / uni);
                if (iou > iouTh) { keep = false; break; }
            }
            if (keep) kept.Add(dets[i]);
        }
        return kept;
    }

    void OnGUI()
    {
        if (finalDets == null) return;
        GUI.Label(new Rect(10, 10, 500, 24), $"Detections: {finalDets.Count}", label);
        float sx = (float)Screen.width / inputW, sy = (float)Screen.height / inputH;
        foreach (var d in finalDets)
        {
            var r = d.box;
            DrawRect(new Rect(r.x * sx, r.y * sy, r.width * sx, r.height * sy), 2);
        }
    }

    void DrawRect(Rect r, int th)
    {
        GUI.DrawTexture(new Rect(r.x, r.y, r.width, th), white);
        GUI.DrawTexture(new Rect(r.x, r.y, th, r.height), white);
        GUI.DrawTexture(new Rect(r.x + r.width - th, r.y, th, r.height), white);
        GUI.DrawTexture(new Rect(r.x, r.y + r.height - th, r.width, th), white);
    }
}
