using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;

// 전시 전체 흐름을 관리한다.
//
//   대기영상  체험자가 없을 때 반복 재생. 화면을 덮는다.
//      | D 신호(지구 모형을 움직임)
//   인트로    안내 멘트를 한 줄씩 넘긴다.
//      | 마지막 멘트가 끝남
//   체험      PlanetDiveDirector 가 동작한다. 신호 -> 지구 이동 -> 줌인 -> 영상.
//      | idleTimeoutSeconds 동안 신호 없음
//   대기영상 으로 복귀
//
// 대기영상과 인트로 동안에는 PlanetDiveDirector 를 꺼서 줌인 연출이 끼어들지 않게 한다.
[DefaultExecutionOrder(60)]
public class ExperienceFlowDirector : MonoBehaviour
{
    public enum FlowPhase { Attract, Intro, Experience }

    [Header("참조 (비워두면 자동 탐색)")]
    [SerializeField] private PlanetDiveDirector diveDirector;
    [Tooltip("인트로 멘트에 쓸 폰트. 비우면 TMP 기본 폰트를 쓴다.")]
    [SerializeField] private TMP_FontAsset messageFont;

    [Header("인트로 화면")]
    [SerializeField] private float messageFontSize = 46f;
    [Tooltip("멘트 뒤에 깔리는 어두운 배경의 진하기")]
    [SerializeField, Range(0f, 1f)] private float dimAlpha = 0.72f;
    [Tooltip("멘트 영역의 좌우 여백(1920 기준)")]
    [SerializeField] private float messageSideMargin = 260f;

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

    private Canvas canvas;
    private RawImage attractImage;
    private Image dimImage;
    private TextMeshProUGUI messageText;
    private VideoPlayer attractPlayer;

    private string attractVideoPath;

    private int messageIndex;
    private float messageTimer;
    private float idleTimer;

    // 인트로가 끝났을 때 이미 자리를 잡은 상태라면 바로 연출을 시작해 준다.
    // (인트로를 띄우는 동안 정착 이벤트가 지나가 버리기 때문)
    private bool settledDuringIntro;

    private float Dt { get { return model != null ? model.DeltaTime : Time.unscaledDeltaTime; } }
    private GoldilocksJson Config { get { return model.Config; } }

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

        BuildOverlay();
        ResolveAttractVideo();

        model.OnStepSignal += OnStepSignal;
        model.OnStepChanged += OnStepChanged;
        model.OnStepSettled += OnStepSettled;

        if (skipAttractOnStart) EnterExperience();
        else EnterAttract();
    }

    private void OnDestroy()
    {
        if (model != null)
        {
            model.OnStepSignal -= OnStepSignal;
            model.OnStepChanged -= OnStepChanged;
            model.OnStepSettled -= OnStepSettled;
        }
    }

    private void Update()
    {
        switch (Phase)
        {
            case FlowPhase.Intro: TickIntro(); break;
            case FlowPhase.Experience: TickExperience(); break;
        }
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

        SetAlpha(messageText, 0f);
        SetAlpha(dimImage, 1f);   // 영상이 없을 때도 3D 가 비치지 않도록 덮는다
        SetAlpha(attractImage, 1f);

        if (attractPlayer != null && !string.IsNullOrEmpty(attractVideoPath))
        {
            attractPlayer.url = attractVideoPath;
            attractPlayer.isLooping = true;
            attractPlayer.Play();
        }

        if (verboseLog) Debug.Log("[Goldilocks] 대기영상");
    }

    // ── 인트로 ────────────────────────────────────────────────────

    public void EnterIntro()
    {
        Phase = FlowPhase.Intro;
        messageIndex = 0;
        messageTimer = 0f;
        settledDuringIntro = false;

        if (diveDirector != null) diveDirector.DirectorEnabled = false;

        if (attractPlayer != null && attractPlayer.isPlaying) attractPlayer.Stop();
        SetAlpha(attractImage, 0f);
        SetAlpha(dimImage, dimAlpha);

        var messages = Config.introMessages;
        if (messages == null || messages.Count == 0)
        {
            EnterExperience();
            return;
        }

        messageText.text = messages[0];
        SetAlpha(messageText, 0f);

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

        messageTimer += Dt;

        float hold = Mathf.Max(0.1f, Config.introSecondsPerMessage);
        float fade = Mathf.Clamp(Config.introFadeSeconds, 0f, hold * 0.5f);

        // 한 멘트 = 페이드 인 -> 유지 -> 페이드 아웃
        float alpha;
        if (fade <= 0f) alpha = 1f;
        else if (messageTimer < fade) alpha = messageTimer / fade;
        else if (messageTimer > hold - fade) alpha = Mathf.Max(0f, (hold - messageTimer) / fade);
        else alpha = 1f;

        SetAlpha(messageText, alpha);

        if (messageTimer < hold) return;

        messageIndex++;
        messageTimer = 0f;

        if (messageIndex >= messages.Count)
        {
            EnterExperience();
            return;
        }

        messageText.text = messages[messageIndex];
        SetAlpha(messageText, 0f);
    }

    // ── 체험 ──────────────────────────────────────────────────────

    public void EnterExperience()
    {
        Phase = FlowPhase.Experience;
        idleTimer = 0f;

        if (attractPlayer != null && attractPlayer.isPlaying) attractPlayer.Stop();
        SetAlpha(attractImage, 0f);
        SetAlpha(dimImage, 0f);
        SetAlpha(messageText, 0f);

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

    private void BuildOverlay()
    {
        var go = new GameObject("GoldilocksFlowOverlay",
            typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
        go.transform.SetParent(transform, false);

        canvas = go.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 300;   // 줌인 오버레이(200)보다 위

        var scaler = go.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        // 자식 순서 = 그리는 순서. 어두운 배경 -> 대기영상 -> 멘트
        dimImage = CreateImage("Dim", new Color(0f, 0f, 0f, 0f));
        attractImage = CreateRawImage("AttractVideo", new Color(1f, 1f, 1f, 0f));

        var textGO = new GameObject("Message", typeof(RectTransform), typeof(TextMeshProUGUI));
        var rt = textGO.GetComponent<RectTransform>();
        rt.SetParent(canvas.transform, false);
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(messageSideMargin, 0f);
        rt.offsetMax = new Vector2(-messageSideMargin, 0f);

        messageText = textGO.GetComponent<TextMeshProUGUI>();
        if (messageFont != null) messageText.font = messageFont;
        messageText.fontSize = messageFontSize;
        messageText.alignment = TextAlignmentOptions.Center;
        messageText.enableWordWrapping = true;
        messageText.lineSpacing = 18f;
        messageText.color = new Color(1f, 1f, 1f, 0f);
        messageText.raycastTarget = false;

        attractPlayer = gameObject.AddComponent<VideoPlayer>();
        attractPlayer.playOnAwake = false;
        attractPlayer.renderMode = VideoRenderMode.APIOnly;
        attractPlayer.audioOutputMode = VideoAudioOutputMode.Direct;
        attractPlayer.source = VideoSource.Url;
        attractPlayer.isLooping = true;
        attractPlayer.skipOnDrop = true;
        attractPlayer.waitForFirstFrame = true;
    }

    private void LateUpdate()
    {
        // VideoPlayer.texture 는 첫 프레임이 나온 뒤에야 생긴다.
        if (attractImage == null || attractPlayer == null) return;
        if (attractImage.texture == null && attractPlayer.texture != null)
            attractImage.texture = attractPlayer.texture;
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
        rt.SetParent(canvas.transform, false);
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
