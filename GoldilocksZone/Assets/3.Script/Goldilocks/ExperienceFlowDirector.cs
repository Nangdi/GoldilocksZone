using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;

// 전시 전체 흐름을 관리한다.
//
//   대기영상  체험자가 없을 때 반복 재생. 화면을 덮는다.
//             그 위에 제목과 "지구를 움직여 보세요" 안내를 띄워 체험을 유도한다.
//      | D 신호(지구 모형을 움직임)
//   인트로    대기영상은 그대로 흐르고, 그 위에 어두운 막을 깔아 안내 멘트를 한 줄씩 넘긴다.
//      | 마지막 멘트가 끝남
//   체험      대기영상·막·멘트가 함께 서서히 사라지며 3D 장면이 드러난다.
//             PlanetDiveDirector 가 동작한다. 신호 -> 지구 이동 -> 줌인 -> 영상.
//      | idleTimeoutSeconds 동안 신호 없음
//   대기영상 으로 복귀(서서히 덮인다)
//
// 화면을 덮는 요소들은 overlayAlpha 하나로 묶어서 같이 나타나고 같이 사라진다.
//   배경막(검정) -> 대기영상 -> 어두운 막 -> 대기 안내(제목/유도 문구) -> 인트로 멘트   순서로 그린다.
// UI 는 씬의 GoldilocksFlowOverlay 캔버스에 미리 만들어 두고 인스펙터로 연결한다.
// 위치·크기·폰트는 에디터에서 조절하고, 이 스크립트는 문구와 투명도만 다룬다.
// (연결이 비어 있으면 최소한의 것을 런타임에 만들어 동작은 하게 한다)
// 대기영상과 인트로 동안에는 PlanetDiveDirector 를 꺼서 줌인 연출이 끼어들지 않게 한다.
[DefaultExecutionOrder(60)]
public class ExperienceFlowDirector : MonoBehaviour
{
    public enum FlowPhase { Attract, Intro, Experience }

    [Header("참조 (비워두면 자동 탐색)")]
    [SerializeField] private PlanetDiveDirector diveDirector;

    [Header("UI (씬의 GoldilocksFlowOverlay 캔버스. 비워두면 런타임에 만든다)")]
    [SerializeField] private Canvas overlayCanvas;
    [Tooltip("영상이 없거나 첫 프레임 전에도 3D 가 비치지 않도록 깔리는 검은 배경")]
    [SerializeField] private Image backdropImage;
    [Tooltip("대기영상이 그려지는 RawImage")]
    [SerializeField] private RawImage attractImage;
    [Tooltip("대기영상 위에 깔리는 어두운 막")]
    [SerializeField] private Image dimImage;
    [Tooltip("대기 화면 - 제목 위 설명 줄 (문구는 goldilocks.json attractHeading)")]
    [SerializeField] private TextMeshProUGUI headingText;
    [Tooltip("대기 화면 - 제목 (goldilocks.json attractTitle)")]
    [SerializeField] private TextMeshProUGUI titleText;
    [Tooltip("대기 화면 - 체험 유도 문구 (goldilocks.json attractPrompt)")]
    [SerializeField] private TextMeshProUGUI promptText;
    [Tooltip("인트로 멘트 (goldilocks.json introMessages)")]
    [SerializeField] private TextMeshProUGUI messageText;

    [Header("대기영상 재생기 (비워두면 런타임에 만든다)")]
    [SerializeField] private VideoPlayer attractPlayer;
    [Tooltip("대기영상을 그릴 RenderTexture. 비워두면 영상 크기에 맞춰 런타임에 만든다.")]
    [SerializeField] private RenderTexture attractTexture;

    [Header("대기 화면")]
    [Tooltip("대기영상 위에 깔리는 어두운 막의 진하기. 글자가 잘 읽히도록 살짝만 덮는다.")]
    [SerializeField, Range(0f, 1f)] private float attractDimAlpha = 0.3f;
    [Tooltip("유도 문구가 숨 쉬듯 밝아졌다 어두워지는 속도(초당 반복 횟수)")]
    [SerializeField] private float promptPulseSpeed = 0.6f;
    [Tooltip("유도 문구가 가장 어두워졌을 때의 투명도")]
    [SerializeField, Range(0f, 1f)] private float promptPulseMinAlpha = 0.35f;

    [Header("인트로 화면")]
    [Tooltip("멘트 뒤에 깔리는 어두운 막의 진하기. 대기영상 위에 덮인다.")]
    [SerializeField, Range(0f, 1f)] private float dimAlpha = 0.72f;

    [Header("런타임 생성 폴백 (UI 연결이 비어 있을 때만 쓰인다)")]
    [Tooltip("런타임에 글자를 만들 때 쓸 폰트. 비우면 TMP 기본 폰트를 쓴다.")]
    [SerializeField] private TMP_FontAsset fallbackFont;

    [Header("체험 시작")]
    [Tooltip("인트로가 끝나자마자 인트로 중에 잡힌 위치로 연출을 시작한다. " +
             "끄면 체험자가 지구 모형을 다시 움직일 때까지 신호를 기다린다.")]
    [SerializeField] private bool autoPlayAfterIntro = false;

    [Header("디버그")]
    [Tooltip("단계 전환을 콘솔에 남긴다.")]
    [SerializeField] private bool verboseLog = true;
    [Tooltip("인트로를 건너뛰고 바로 체험으로 시작한다. 개발 중에만 사용.")]
    [SerializeField] private bool skipAttractOnStart = false;

    public FlowPhase Phase { get; private set; } = FlowPhase.Attract;

    private GoldilocksZoneModel model;

    private bool runtimeAttractTexture;   // attractTexture 를 우리가 만들었는지(정리 책임)

    private string attractVideoPath;

    private int messageIndex;
    private float messageTimer;
    private float messageAlpha;
    private float idleTimer;

    // 덮개 전체(배경막·대기영상·어두운 막·멘트)의 투명도. 1 이면 3D 가 완전히 가려진다.
    private float overlayAlpha;
    private float overlayTarget;
    // 어두운 막의 진하기. 대기에서는 살짝, 인트로에서는 진하게.
    private float dimLevel;
    private float dimTarget;
    // 대기 안내(제목/유도 문구)의 투명도. 대기 화면에서만 보인다.
    private float attractAlpha;
    private float attractTarget;
    private float pulseTime;

    // 인트로가 끝났을 때 이미 자리를 잡은 상태라면 바로 연출을 시작해 준다.
    // (인트로를 띄우는 동안 정착 이벤트가 지나가 버리기 때문)
    private bool settledDuringIntro;

    private float Dt { get { return model != null ? model.DeltaTime : Time.unscaledDeltaTime; } }
    private GoldilocksJson Config { get { return model.Config; } }
    private float SceneFade { get { return Mathf.Max(0.01f, Config.sceneFadeSeconds); } }

    private void Start()
    {
        model = GoldilocksZoneModel.Instance;
        if (diveDirector == null) diveDirector = FindObjectOfType<PlanetDiveDirector>();

        if (model == null)
        {
            Debug.LogError("[Goldilocks] GoldilocksZoneModel 을 찾지 못해 흐름을 시작할 수 없습니다.");
            enabled = false;
            return;
        }

        EnsureOverlay();
        EnsureAttractPlayer();
        ResolveAttractVideo();

        model.OnStepSignal += OnStepSignal;
        model.OnStepChanged += OnStepChanged;
        model.OnStepSettled += OnStepSettled;

        // 시작할 때는 페이드 없이 바로 그 상태로 놓는다.
        if (skipAttractOnStart) { EnterExperience(); SnapOverlay(); }
        else { EnterAttract(); SnapOverlay(); }
    }

    private void OnDestroy()
    {
        if (model != null)
        {
            model.OnStepSignal -= OnStepSignal;
            model.OnStepChanged -= OnStepChanged;
            model.OnStepSettled -= OnStepSettled;
        }
        if (runtimeAttractTexture && attractTexture != null)
        {
            attractTexture.Release();
            Destroy(attractTexture);
        }
    }

    private void Update()
    {
        switch (Phase)
        {
            case FlowPhase.Intro: TickIntro(); break;
            case FlowPhase.Experience: TickExperience(); break;
        }

        TickOverlay();
    }

    // ── 신호 ──────────────────────────────────────────────────────

    // 대기 중에는 지구가 이미 그 자리에 있어도(같은 D 값이어도) 신호만 오면 안내를 시작한다.
    // 체험자가 모형을 건드렸다는 사실 자체가 시작 조건이기 때문이다.
    private void OnStepSignal(int step)
    {
        if (Phase == FlowPhase.Attract) EnterIntro();
    }

    private void OnStepChanged(int step)
    {
        idleTimer = 0f;
    }

    private void OnStepSettled(int step)
    {
        idleTimer = 0f;
        if (Phase == FlowPhase.Intro) settledDuringIntro = true;
    }

    // ── 대기영상 ──────────────────────────────────────────────────

    public void EnterAttract()
    {
        Phase = FlowPhase.Attract;
        idleTimer = 0f;

        if (diveDirector != null) diveDirector.DirectorEnabled = false;

        messageAlpha = 0f;
        overlayTarget = 1f;
        dimTarget = attractDimAlpha;
        attractTarget = 1f;

        PlayAttractVideo();

        if (verboseLog) Debug.Log("[Goldilocks] 대기영상");
    }

    // ── 인트로 ────────────────────────────────────────────────────

    // 대기영상은 멈추지 않는다. 그 위로 어두운 막이 서서히 깔리고 멘트가 올라온다.
    public void EnterIntro()
    {
        Phase = FlowPhase.Intro;
        messageIndex = 0;
        messageTimer = 0f;
        messageAlpha = 0f;
        settledDuringIntro = false;

        if (diveDirector != null) diveDirector.DirectorEnabled = false;

        overlayTarget = 1f;
        dimTarget = dimAlpha;
        attractTarget = 0f;   // 제목/유도 문구가 먼저 걷히고, 다 사라진 뒤 멘트가 시작된다(TickIntro)
        PlayAttractVideo();   // 혹시 멈춰 있었다면 다시 돌린다

        var messages = Config.introMessages;
        if (messages == null || messages.Count == 0)
        {
            EnterExperience();
            return;
        }

        messageText.text = messages[0];

        if (verboseLog) Debug.Log($"[Goldilocks] 인트로 {messages.Count}개 멘트");
    }

    private void TickIntro()
    {
        var messages = Config.introMessages;
        if (messages == null || messages.Count == 0)
        {
            EnterExperience();
            return;
        }

        // 제목/유도 문구가 완전히 사라진 뒤에 멘트를 시작한다. 겹쳐 보이지 않게.
        if (attractAlpha > 0f) return;

        messageTimer += Dt;

        float hold = Mathf.Max(0.1f, Config.introSecondsPerMessage);
        float fade = Mathf.Clamp(Config.introFadeSeconds, 0f, hold * 0.5f);

        // 한 멘트 = 페이드 인 -> 유지 -> 페이드 아웃
        if (fade <= 0f) messageAlpha = 1f;
        else if (messageTimer < fade) messageAlpha = messageTimer / fade;
        else if (messageTimer > hold - fade) messageAlpha = Mathf.Max(0f, (hold - messageTimer) / fade);
        else messageAlpha = 1f;

        if (messageTimer < hold) return;

        messageIndex++;
        messageTimer = 0f;

        if (messageIndex >= messages.Count)
        {
            EnterExperience();
            return;
        }

        messageText.text = messages[messageIndex];
        messageAlpha = 0f;
    }

    // ── 체험 ──────────────────────────────────────────────────────

    // 덮개 전체를 서서히 걷어 3D 장면을 드러낸다. 대기영상은 완전히 사라진 뒤에 멈춘다.
    public void EnterExperience()
    {
        Phase = FlowPhase.Experience;
        idleTimer = 0f;

        messageAlpha = 0f;
        overlayTarget = 0f;
        dimTarget = 0f;
        attractTarget = 0f;

        if (diveDirector != null)
        {
            diveDirector.DirectorEnabled = true;

            // 기본은 신호 대기다. 마지막 멘트가 "직접 움직여서 찾아보세요" 이므로
            // 인트로를 띄우려고 움직인 위치가 그대로 선택돼 버리면 안 된다.
            // 체험자가 모형을 다시 움직여 새 신호가 와야 연출이 시작된다.
            if (autoPlayAfterIntro && settledDuringIntro && !diveDirector.IsPlaying)
                diveDirector.StartDive();
        }
        settledDuringIntro = false;

        if (verboseLog)
            Debug.Log(autoPlayAfterIntro ? "[Goldilocks] 체험 시작" : "[Goldilocks] 체험 시작 - 신호 대기");
    }

    private void TickExperience()
    {
        // 줌인/영상이 도는 동안은 세지 않는다. 영상이 끝나 원래 시점으로 돌아온 뒤부터 센다.
        if (diveDirector != null && diveDirector.IsPlaying)
        {
            idleTimer = 0f;
            return;
        }

        idleTimer += Dt;
        if (idleTimer >= Mathf.Max(1f, Config.idleTimeoutSeconds))
            EnterAttract();
    }

    // ── 덮개 페이드 ───────────────────────────────────────────────

    private void TickOverlay()
    {
        float step = Dt / SceneFade;
        overlayAlpha = Mathf.MoveTowards(overlayAlpha, overlayTarget, step);
        dimLevel = Mathf.MoveTowards(dimLevel, dimTarget, step);
        attractAlpha = Mathf.MoveTowards(attractAlpha, attractTarget, step);
        pulseTime += Dt;
        ApplyOverlay();

        // 완전히 사라진 뒤에야 영상을 멈춘다. 먼저 멈추면 마지막 프레임이 굳은 채 사라져 보인다.
        if (overlayTarget <= 0f && overlayAlpha <= 0f) StopAttractVideo();
    }

    // 페이드 없이 목표 상태로 바로 놓는다. 시작할 때만 쓴다.
    private void SnapOverlay()
    {
        overlayAlpha = overlayTarget;
        dimLevel = dimTarget;
        attractAlpha = attractTarget;
        ApplyOverlay();
        if (overlayTarget <= 0f) StopAttractVideo();
    }

    private void ApplyOverlay()
    {
        SetAlpha(backdropImage, overlayAlpha);   // 영상이 없거나 아직 첫 프레임 전이어도 3D 가 비치지 않도록
        SetAlpha(attractImage, overlayAlpha);
        SetAlpha(dimImage, overlayAlpha * dimLevel);
        SetAlpha(headingText, overlayAlpha * attractAlpha);
        SetAlpha(titleText, overlayAlpha * attractAlpha);
        // 유도 문구는 숨 쉬듯 밝기가 오르내려 "여기를 만져 보라"는 신호가 된다.
        float pulse = Mathf.Lerp(promptPulseMinAlpha, 1f,
            0.5f + 0.5f * Mathf.Sin(pulseTime * promptPulseSpeed * Mathf.PI * 2f));
        SetAlpha(promptText, overlayAlpha * attractAlpha * pulse);
        SetAlpha(messageText, overlayAlpha * messageAlpha);
    }

    private void PlayAttractVideo()
    {
        if (attractPlayer == null || string.IsNullOrEmpty(attractVideoPath)) return;
        if (attractPlayer.isPlaying) return;

        attractPlayer.url = attractVideoPath;
        attractPlayer.isLooping = true;
        attractPlayer.Play();
    }

    private void StopAttractVideo()
    {
        if (attractPlayer == null || !attractPlayer.isPlaying) return;
        attractPlayer.Stop();
        ClearAttractTexture();   // 마지막 프레임이 남아 다음 페이드인 때 먼저 비치지 않도록
    }

    // 영상 크기에 맞는 RenderTexture 를 준비한다. 인스펙터에서 지정한 것이 있으면 그대로 쓴다.
    private void EnsureAttractTexture(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        if (attractTexture != null && attractTexture.width == width && attractTexture.height == height) return;
        if (attractTexture != null && !runtimeAttractTexture) return;

        if (runtimeAttractTexture && attractTexture != null)
        {
            attractTexture.Release();
            Destroy(attractTexture);
        }

        attractTexture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32);
        attractTexture.name = "AttractVideoRT";
        attractTexture.Create();
        runtimeAttractTexture = true;
        BindAttractTexture(attractTexture);
    }

    private void BindAttractTexture(RenderTexture rt)
    {
        if (attractPlayer != null) attractPlayer.targetTexture = rt;
        if (attractImage != null) attractImage.texture = rt;
        ClearAttractTexture();
    }

    private void ClearAttractTexture()
    {
        if (attractTexture == null) return;
        var prev = RenderTexture.active;
        RenderTexture.active = attractTexture;
        GL.Clear(true, true, Color.black);
        RenderTexture.active = prev;
    }

    // ── 대기영상 파일 ─────────────────────────────────────────────

    [ContextMenu("대기영상 다시 찾기")]
    public void ResolveAttractVideo()
    {
        attractVideoPath = null;

        string root = diveDirector != null ? diveDirector.VideoRootFolder : "video";
        string dir = Path.Combine(Path.Combine(Application.streamingAssetsPath, root), Config.idleVideoFolder);

        if (!Directory.Exists(dir))
        {
            Debug.LogWarning($"[Goldilocks] 대기영상 폴더가 없습니다: {dir}");
            return;
        }

        var files = new List<string>();
        foreach (string file in Directory.GetFiles(dir))
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext == ".mp4" || ext == ".mov" || ext == ".m4v" || ext == ".webm" || ext == ".avi")
                files.Add(file);
        }

        if (files.Count == 0)
        {
            Debug.LogWarning($"[Goldilocks] 대기영상이 없습니다: {dir} (넣기 전까지는 검은 화면으로 대기)");
            return;
        }

        files.Sort(System.StringComparer.OrdinalIgnoreCase);
        attractVideoPath = files[0];

        if (verboseLog) Debug.Log($"[Goldilocks] 대기영상 {Path.GetFileName(attractVideoPath)}");
    }

    // ── UI ────────────────────────────────────────────────────────

    // 씬에 연결된 UI 를 쓰고, 빠진 것만 런타임에 만든다.
    // 씬 오브젝트의 위치·크기·폰트는 건드리지 않는다. 문구와 시작 투명도만 맞춘다.
    private void EnsureOverlay()
    {
        bool anyMissing = backdropImage == null || attractImage == null || dimImage == null ||
                          headingText == null || titleText == null || promptText == null || messageText == null;

        if (anyMissing && overlayCanvas == null)
        {
            var go = new GameObject("GoldilocksFlowOverlay",
                typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
            go.transform.SetParent(transform, false);

            overlayCanvas = go.GetComponent<Canvas>();
            overlayCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            overlayCanvas.sortingOrder = 300;   // 줌인 오버레이(200)보다 위, ESC 설정창(1000) 아래

            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
        }

        if (anyMissing)
            Debug.LogWarning("[Goldilocks] ExperienceFlowDirector 의 UI 연결이 비어 있어 런타임에 만듭니다. " +
                             "씬의 GoldilocksFlowOverlay 아래 오브젝트를 인스펙터에 연결하면 에디터에서 조절할 수 있습니다.");

        // 자식 순서 = 그리는 순서. 배경막 -> 대기영상 -> 어두운 막 -> 대기 안내 -> 멘트
        if (backdropImage == null) backdropImage = CreateImage("Backdrop", Color.black);
        if (attractImage == null) attractImage = CreateRawImage("AttractVideo", Color.white);
        if (dimImage == null) dimImage = CreateImage("Dim", Color.black);
        if (headingText == null) headingText = CreateText("AttractHeading", 44f, new Vector2(0f, 0.64f), new Vector2(1f, 0.72f));
        if (titleText == null) titleText = CreateText("AttractTitle", 110f, new Vector2(0f, 0.50f), new Vector2(1f, 0.64f));
        if (promptText == null) promptText = CreateText("AttractPrompt", 42f, new Vector2(0f, 0.16f), new Vector2(1f, 0.24f));
        if (messageText == null) messageText = CreateText("Message", 46f, Vector2.zero, Vector2.one);

        headingText.text = Config.attractHeading;
        titleText.text = Config.attractTitle;
        promptText.text = Config.attractPrompt;
        messageText.text = string.Empty;

        // 편집 중 보기 좋으라고 씬에서 켜 두었어도 시작은 투명하게. 실제 값은 ApplyOverlay 가 매 프레임 정한다.
        SetAlpha(backdropImage, 0f);
        SetAlpha(attractImage, 0f);
        SetAlpha(dimImage, 0f);
        SetAlpha(headingText, 0f);
        SetAlpha(titleText, 0f);
        SetAlpha(promptText, 0f);
        SetAlpha(messageText, 0f);
    }

    // 영상은 RenderTexture 에 그리고, UGUI RawImage 가 그 텍스처를 화면에 띄운다.
    private void EnsureAttractPlayer()
    {
        if (attractPlayer == null) attractPlayer = gameObject.AddComponent<VideoPlayer>();

        attractPlayer.playOnAwake = false;
        attractPlayer.renderMode = VideoRenderMode.RenderTexture;
        attractPlayer.audioOutputMode = VideoAudioOutputMode.Direct;
        attractPlayer.source = VideoSource.Url;
        attractPlayer.isLooping = true;
        attractPlayer.skipOnDrop = true;
        attractPlayer.waitForFirstFrame = true;

        // 재생 전에 그릴 곳이 있어야 한다. 크기는 준비가 끝나면 영상에 맞춰 다시 잡는다.
        if (attractTexture == null) attractTexture = attractPlayer.targetTexture;
        if (attractTexture != null) BindAttractTexture(attractTexture);
        else EnsureAttractTexture(1920, 1080);

        // 영상 크기는 준비가 끝나야 알 수 있다.
        attractPlayer.prepareCompleted += vp => EnsureAttractTexture((int)vp.width, (int)vp.height);
    }

    // 좌우 여백을 두고 가운데 정렬되는 글자 상자를 만든다(폴백).
    private TextMeshProUGUI CreateText(string name, float size, Vector2 anchorMin, Vector2 anchorMax)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(overlayCanvas.transform, false);
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.offsetMin = new Vector2(260f, 0f);
        rt.offsetMax = new Vector2(-260f, 0f);

        var text = go.GetComponent<TextMeshProUGUI>();
        if (fallbackFont != null) text.font = fallbackFont;
        text.fontSize = size;
        text.alignment = TextAlignmentOptions.Center;
        text.enableWordWrapping = true;
        text.raycastTarget = false;
        return text;
    }

    private Image CreateImage(string name, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        Stretch(go.GetComponent<RectTransform>());
        var img = go.GetComponent<Image>();
        img.color = color;
        img.raycastTarget = false;
        return img;
    }

    private RawImage CreateRawImage(string name, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(RawImage));
        Stretch(go.GetComponent<RectTransform>());
        var img = go.GetComponent<RawImage>();
        img.color = color;
        img.raycastTarget = false;
        return img;
    }

    private void Stretch(RectTransform rt)
    {
        rt.SetParent(overlayCanvas.transform, false);
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    private static void SetAlpha(Graphic g, float a)
    {
        if (g == null) return;
        var c = g.color;
        c.a = a;
        g.color = c;
    }
}
