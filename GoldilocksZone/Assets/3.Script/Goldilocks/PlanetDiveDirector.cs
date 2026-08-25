using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;
using UnityEngine.Video;

// 지구가 자리를 잡으면(OnStepSettled) 카메라가 행성 표면으로 빨려들어가고,
// 화이트아웃 뒤에서 행성 표면 이미지로 바뀌는 연출을 담당한다.
//
// 등속 이동이 밋밋한 이유는 모든 변화가 같은 타이밍에 같은 속도로 일어나기 때문이다.
// 여기서는 서로 다른 곡선을 가진 요소들을 겹쳐 쌓는다.
//   예비 동작  - 들어가기 전 살짝 뒤로 물러나며 시야가 넓어진다(웅크렸다 튀어나가는 동작).
//   곡선 경로  - 직선이 아니라 2차 베지어로 휘어 들어가 시차가 생긴다.
//   가속 이징  - 뒤로 갈수록 빨라져 빨려드는 느낌을 만든다.
//   FOV 축소   - 돌리 인과 겹쳐 압박감을 준다.
//   롤/흔들림  - 속도에 비례해 커졌다 사라진다.
//   비네트     - 화면 가장자리를 조여 터널처럼 보이게 한다.
//
// 원래 시점으로 되돌아가는 경우는 둘이다.
//   영상이 끝났을 때        - 행성은 옮겨진 위치를 그대로 유지한 채 시점만 돌아간다.
//   새 신호가 들어왔을 때   - 연출 도중이어도 즉시 걷고 돌아간다.
// 복귀 중에 정착 신호가 도착하면 예약해 두었다가 복귀 직후 이어서 연출한다.
[DefaultExecutionOrder(50)]
public class PlanetDiveDirector : MonoBehaviour
{
    private enum Phase { Idle, Anticipate, Dive, Whiteout, Surface, Return }

    [Header("대상 (비워두면 자동 탐색)")]
    [SerializeField] private Camera targetCamera;
    [SerializeField] private Transform planet;
    [SerializeField] private string planetPath = "Actor/Universal Rendering Pipeline Materials/Sun Sphere/Earth";

    [Header("실행")]
    [Tooltip("지구가 자리를 잡으면 자동으로 연출을 시작한다.")]
    [SerializeField] private bool playOnSettle = true;
    [Tooltip("연출 없이 확인만 하고 싶을 때 끈다.")]
    [SerializeField] private bool enableDirector = true;

    [Header("타이밍(초)")]
    [Tooltip("들어가기 전 뒤로 물러나며 숨을 고르는 시간")]
    [SerializeField] private float anticipateSeconds = 0.45f;
    [SerializeField] private float diveSeconds = 2.6f;
    [SerializeField] private float whiteoutSeconds = 0.5f;
    [SerializeField] private float returnSeconds = 1.3f;

    [Header("경로")]
    [Tooltip("행성 반지름의 몇 배 거리에서 멈출지. 화이트아웃이 덮으므로 표면에 닿을 필요는 없다.")]
    [SerializeField] private float surfaceStopRadiusScale = 1.6f;
    [Tooltip("예비 동작에서 뒤로 물러나는 양(전체 거리 대비 비율)")]
    [SerializeField, Range(0f, 0.3f)] private float anticipatePullback = 0.07f;
    // 아크를 키우면 초반에 카메라가 오히려 행성에서 멀어져 "빨려든다"는 인상이 흐려진다.
    // 시차가 느껴질 만큼만 주고, 거리는 계속 줄어들게 둔다.
    [Tooltip("경로가 위로 휘는 정도(전체 거리 대비 비율)")]
    [SerializeField, Range(-1f, 1f)] private float arcUp = 0.1f;
    [Tooltip("경로가 옆으로 휘는 정도(전체 거리 대비 비율)")]
    [SerializeField, Range(-1f, 1f)] private float arcSide = 0.16f;

    [Header("렌즈")]
    [Tooltip("예비 동작에서 넓어지는 각도")]
    [SerializeField] private float anticipateFovKick = 7f;
    [Tooltip("도착 시 FOV. 기본값보다 작아야 줌인으로 읽힌다.")]
    [SerializeField] private float endFov = 32f;

    [Header("흔들림")]
    [Tooltip("중간에 최대로 기울어지는 각도")]
    [SerializeField] private float rollDegrees = 5f;
    [Tooltip("행성까지 남은 거리 대비 흔들림 크기")]
    [SerializeField, Range(0f, 0.1f)] private float shakeAmount = 0.02f;
    [SerializeField] private float shakeFrequency = 7f;

    [Header("이징")]
    // 키 두 개(0->1)만 두고 끝 접선을 세우면 곡선이 초반에 0 아래로 살짝 내려가
    // 카메라가 뒤로 밀리고, 마지막 순간에 경로 절반을 몰아 처리해 순간이동처럼 보인다.
    // 중간 키를 하나 넣어 단조 증가시키면서 가속을 고르게 편다.
    [Tooltip("위치 진행 곡선. 끝으로 갈수록 가팔라야 빨려드는 느낌이 난다.")]
    [SerializeField] private AnimationCurve diveEase = new AnimationCurve(
        new Keyframe(0f, 0f, 0f, 0.15f),
        new Keyframe(0.6f, 0.28f, 0.95f, 0.95f),
        new Keyframe(1f, 1f, 2.2f, 2.2f));
    [Tooltip("시선이 행성을 향해 도는 곡선. 위치보다 먼저 잠겨야 안정적으로 보인다.")]
    [SerializeField] private AnimationCurve lookEase =
        new AnimationCurve(new Keyframe(0f, 0f, 0f, 0f), new Keyframe(0.5f, 0.85f), new Keyframe(1f, 1f, 0f, 0f));

    [Header("후처리 (기존 프로필은 건드리지 않고 런타임 볼륨을 덧씌운다)")]
    [SerializeField] private bool useVignette = true;
    [SerializeField, Range(0f, 1f)] private float vignetteIntensity = 0.5f;
    [SerializeField] private bool useMotionBlur = true;
    [SerializeField, Range(0f, 1f)] private float motionBlurIntensity = 0.65f;

    [Header("화면 전환")]
    [SerializeField] private Color flashColor = Color.white;

    // 영상은 프로젝트에 임포트하지 않고 StreamingAssets 에서 읽는다.
    // 빌드 후에도 폴더에 파일만 갈아 끼우면 되므로 현장에서 영상 교체가 쉽다.
    //   StreamingAssets/video/1/...  ~  StreamingAssets/video/9/...
    [Header("영상 (StreamingAssets/video/<단계>/ 안의 파일)")]
    [Tooltip("StreamingAssets 아래의 영상 루트 폴더 이름")]
    [SerializeField] private string videoRootFolder = "video";
    [Tooltip("한 폴더에 여러 개가 있을 때 무작위로 고른다. 끄면 이름순 첫 번째를 쓴다.")]
    [SerializeField] private bool pickRandomWhenMultiple = false;
    [Tooltip("반복 재생한다. 끄면 한 번 재생하고 끝난 뒤 원래 시점으로 돌아간다.")]
    [SerializeField] private bool loopVideo = false;
    [Tooltip("영상이 끝나면 자동으로 원래 시점으로 돌아간다. 행성 위치는 그대로 둔다.")]
    [SerializeField] private bool returnWhenVideoEnds = true;
    [SerializeField, Range(0f, 1f)] private float videoVolume = 1f;
    [Tooltip("시작할 때 찾은 영상 목록을 콘솔에 남긴다.")]
    [SerializeField] private bool logVideoScan = true;

    [Header("정지 이미지 (영상이 없을 때)")]
    [Tooltip("D1~D9 각각의 행성 표면 이미지. 비어 있으면 아래 존별 이미지를 쓴다.")]
    [SerializeField] private Sprite[] surfaceByStep = new Sprite[0];
    [Tooltip("순서대로 너무 뜨겁다 / 생명체 거주 가능 / 너무 춥다")]
    [SerializeField] private Sprite[] surfaceByZone = new Sprite[3];

    [Header("오버레이 (비워두면 런타임에 만든다)")]
    [SerializeField] private Canvas overlayCanvas;
    [SerializeField] private Image flashImage;
    [SerializeField] private Image surfaceImage;
    [SerializeField] private RawImage videoImage;

    private GoldilocksZoneModel model;

    // 연출 시작 전 카메라 자세. 되돌아올 목표이기도 하다.
    private Vector3 homePosition;
    private Quaternion homeRotation;
    private float homeFov;

    private Phase phase = Phase.Idle;
    private float phaseTime;

    private Vector3 p0, p1, p2;          // 베지어 제어점
    private Vector3 anticipateFrom, anticipateTo;
    private Quaternion diveStartRotation;
    private float diveStartFov;
    private float noiseSeed;

    private Vector3 returnFromPosition;
    private Quaternion returnFromRotation;
    private float returnFromFov;
    private float returnFromWeight;
    private float returnFromSurfaceAlpha;

    // 복귀 중에 정착 신호가 들어오면 그때는 연출을 시작할 수 없으므로 예약해 뒀다가
    // 복귀가 끝나는 순간 이어서 시작한다. (정착 1초 < 복귀 1.3초 라서 실제로 자주 겹친다)
    private bool divePending;
    private bool revealed;

    private VideoPlayer videoPlayer;
    private Dictionary<int, List<string>> videoPaths;
    private string currentVideoPath;

    private Volume runtimeVolume;
    private VolumeProfile runtimeProfile;
    private Vignette vignette;
    private MotionBlur motionBlur;

    public bool IsPlaying { get { return phase != Phase.Idle; } }

    // 대기영상/인트로 동안에는 연출이 끼어들지 않도록 ExperienceFlowDirector 가 꺼둔다.
    public bool DirectorEnabled
    {
        get { return enableDirector; }
        set { enableDirector = value; }
    }

    // 영상 루트 폴더는 대기영상도 같이 쓰므로 공유한다.
    public string VideoRootFolder { get { return videoRootFolder; } }

    private void Start()
    {
        if (targetCamera == null) targetCamera = Camera.main;
        if (planet == null)
        {
            var go = GameObject.Find(planetPath);
            if (go != null) planet = go.transform;
        }

        model = GoldilocksZoneModel.Instance;

        if (targetCamera == null || planet == null || model == null)
        {
            Debug.LogError($"[Goldilocks] 연출에 필요한 참조가 없습니다. " +
                           $"camera={targetCamera != null} planet={planet != null} model={model != null}");
            enabled = false;
            return;
        }

        CaptureHomePose();
        EnsureOverlay();
        EnsureVolume();
        EnsureVideoPlayer();
        ScanVideoFolders();

        model.OnStepSettled += OnStepSettled;
        model.OnStepChanged += OnStepChanged;
    }

    private void OnDestroy()
    {
        if (model != null)
        {
            model.OnStepSettled -= OnStepSettled;
            model.OnStepChanged -= OnStepChanged;
        }
        if (runtimeVolume != null) Destroy(runtimeVolume.gameObject);
        if (runtimeProfile != null) Destroy(runtimeProfile);
    }

    private void OnStepSettled(int step)
    {
        if (!enableDirector || !playOnSettle) return;

        if (phase == Phase.Idle)
        {
            StartDive();
            return;
        }

        // 아직 복귀 중이면 버리지 말고 예약한다. 버리면 바뀐 위치에서 연출이 아예 안 나온다.
        divePending = true;
    }

    // 체험자가 다시 모형을 움직이면 연출을 걷고 원래 시점으로 돌아간다.
    private void OnStepChanged(int step)
    {
        if (phase == Phase.Idle || phase == Phase.Return) return;
        StartReturn();
    }

    [ContextMenu("연출 시작")]
    public void StartDive()
    {
        if (phase != Phase.Idle) return;

        CaptureHomePose();

        // 예비 동작: 행성 반대 방향으로 살짝 물러난다.
        Vector3 planetCenter = planet.position;
        Vector3 away = (homePosition - planetCenter).normalized;
        float distance = Vector3.Distance(homePosition, planetCenter);

        anticipateFrom = homePosition;
        anticipateTo = homePosition + away * (distance * anticipatePullback);

        phase = Phase.Anticipate;
        phaseTime = 0f;
        noiseSeed = Random.value * 100f;
        divePending = false;
        revealed = false;

        // 영상은 미리 준비해 둔다. 도착까지 3초 넘게 걸리므로 그 사이에 디코딩이 끝난다.
        PrepareVideoFor(model.CurrentStep);
    }

    [ContextMenu("원래 시점으로")]
    public void StartReturn()
    {
        if (phase == Phase.Idle) return;

        // 매 프레임 현재 자세에서 보간하면 프레임레이트에 따라 속도가 달라진다.
        // 출발 자세를 한 번 잡아두고 그 사이를 보간한다.
        returnFromPosition = targetCamera.transform.position;
        returnFromRotation = targetCamera.transform.rotation;
        returnFromFov = targetCamera.fieldOfView;
        returnFromWeight = runtimeVolume != null ? runtimeVolume.weight : 0f;
        returnFromSurfaceAlpha = surfaceImage != null ? surfaceImage.color.a : 0f;

        phase = Phase.Return;
        phaseTime = 0f;
    }

    private void LateUpdate()
    {
        if (phase == Phase.Idle) return;

        float dt = model != null ? model.DeltaTime : Time.unscaledDeltaTime;
        phaseTime += dt;

        switch (phase)
        {
            case Phase.Anticipate: TickAnticipate(); break;
            case Phase.Dive: TickDive(); break;
            case Phase.Whiteout: TickWhiteout(); break;
            case Phase.Surface: TickSurface(); break;
            case Phase.Return: TickReturn(); break;
        }
    }

    private void TickAnticipate()
    {
        float t = Clamp01Progress(anticipateSeconds);
        float e = Mathf.SmoothStep(0f, 1f, t);

        targetCamera.transform.position = Vector3.Lerp(anticipateFrom, anticipateTo, e);
        targetCamera.transform.rotation = homeRotation;
        targetCamera.fieldOfView = homeFov + anticipateFovKick * e;

        if (t < 1f) return;
        BeginDive();
    }

    private void BeginDive()
    {
        Vector3 planetCenter = planet.position;
        Vector3 start = targetCamera.transform.position;
        Vector3 away = (start - planetCenter).normalized;

        // 표면에 완전히 닿기 전에 멈춘다. 그 뒤는 화이트아웃이 덮는다.
        float stop = PlanetRadius() * surfaceStopRadiusScale;
        Vector3 end = planetCenter + away * stop;

        float distance = Vector3.Distance(start, end);

        // 옆/위로 휘는 제어점. 직선으로 들어가면 원근 변화가 없어 밋밋하다.
        Vector3 side = Vector3.Cross(away, Vector3.up);
        if (side.sqrMagnitude < 0.0001f) side = Vector3.right;
        side.Normalize();

        p0 = start;
        p2 = end;
        p1 = (start + end) * 0.5f + Vector3.up * (distance * arcUp) + side * (distance * arcSide);

        diveStartRotation = targetCamera.transform.rotation;
        diveStartFov = targetCamera.fieldOfView;

        phase = Phase.Dive;
        phaseTime = 0f;
    }

    private void TickDive()
    {
        float t = Clamp01Progress(diveSeconds);

        float moveT = diveEase.Evaluate(t);
        Vector3 pos = Bezier(p0, p1, p2, moveT);

        // 흔들림은 남은 거리에 비례시켜, 가까워질수록 화면에서 차지하는 비중이 일정하게 보이도록 한다.
        float remaining = Vector3.Distance(pos, planet.position);
        float shake = shakeAmount * remaining * Mathf.Sin(t * Mathf.PI);   // 중간에 최대
        pos += new Vector3(
            (Mathf.PerlinNoise(noiseSeed, phaseTime * shakeFrequency) - 0.5f),
            (Mathf.PerlinNoise(noiseSeed + 13f, phaseTime * shakeFrequency) - 0.5f),
            (Mathf.PerlinNoise(noiseSeed + 27f, phaseTime * shakeFrequency) - 0.5f)) * (shake * 2f);

        targetCamera.transform.position = pos;

        // 시선은 위치보다 먼저 행성에 잠긴다.
        Quaternion look = Quaternion.LookRotation(planet.position - pos, Vector3.up);
        Quaternion rot = Quaternion.Slerp(diveStartRotation, look, lookEase.Evaluate(t));

        // 롤은 들어갔다 나오며 0 으로 복귀한다.
        rot *= Quaternion.AngleAxis(Mathf.Sin(t * Mathf.PI) * rollDegrees, Vector3.forward);
        targetCamera.transform.rotation = rot;

        targetCamera.fieldOfView = Mathf.Lerp(diveStartFov, endFov, moveT);

        SetEffectWeight(Mathf.Clamp01(t * 1.2f));

        if (t < 1f) return;

        phase = Phase.Whiteout;
        phaseTime = 0f;
    }

    // 앞 절반은 플래시로 덮고, 뒤 절반은 표면 이미지를 남긴 채 플래시를 걷는다.
    private void TickWhiteout()
    {
        float t = Clamp01Progress(whiteoutSeconds);

        if (t < 0.5f)
        {
            SetAlpha(flashImage, Mathf.SmoothStep(0f, 1f, t / 0.5f));
        }
        else
        {
            if (!revealed)
            {
                revealed = true;
                ShowContent();
            }
            SetAlpha(flashImage, Mathf.SmoothStep(1f, 0f, (t - 0.5f) / 0.5f));
        }

        if (t < 1f) return;

        SetAlpha(flashImage, 0f);
        phase = Phase.Surface;
        phaseTime = 0f;
    }

    private void TickReturn()
    {
        float t = Clamp01Progress(returnSeconds);
        float e = Mathf.SmoothStep(0f, 1f, t);

        // 표면 화면은 카메라보다 먼저 걷어야 되돌아가는 궤적이 보인다.
        float contentAlpha = Mathf.Lerp(returnFromSurfaceAlpha, 0f, Mathf.Clamp01(t * 2f));
        SetAlpha(surfaceImage, contentAlpha);
        SetAlpha(videoImage, contentAlpha);
        SetAlpha(flashImage, 0f);

        targetCamera.transform.position = Vector3.Lerp(returnFromPosition, homePosition, e);
        targetCamera.transform.rotation = Quaternion.Slerp(returnFromRotation, homeRotation, e);
        targetCamera.fieldOfView = Mathf.Lerp(returnFromFov, homeFov, e);

        SetEffectWeight(Mathf.Lerp(returnFromWeight, 0f, e));

        if (t < 1f) return;

        targetCamera.transform.position = homePosition;
        targetCamera.transform.rotation = homeRotation;
        targetCamera.fieldOfView = homeFov;
        SetAlpha(surfaceImage, 0f);
        SetAlpha(videoImage, 0f);
        SetEffectWeight(0f);

        if (videoPlayer != null && videoPlayer.isPlaying) videoPlayer.Stop();
        if (videoImage != null) videoImage.texture = null;

        phase = Phase.Idle;

        // 복귀 중에 들어온 정착 신호를 여기서 소화한다.
        if (divePending)
        {
            divePending = false;
            StartDive();
        }
    }

    // 표면 이미지를 띄운 채 다음 신호를 기다린다.
    // VideoPlayer.texture 는 첫 프레임이 나온 뒤에야 생기므로 그때 붙인다.
    private void TickSurface()
    {
        if (videoImage == null || videoPlayer == null) return;
        if (videoImage.texture == null && videoPlayer.texture != null)
            videoImage.texture = videoPlayer.texture;
    }

    private void ShowContent()
    {
        // 영상이 있으면 영상을 우선한다.
        if (videoPlayer != null && !string.IsNullOrEmpty(currentVideoPath))
        {
            videoPlayer.Play();
            if (videoImage != null)
            {
                videoImage.texture = videoPlayer.texture;
                SetAlpha(videoImage, 1f);
            }
            return;
        }

        if (surfaceImage == null) return;

        Sprite sprite = PickSurfaceSprite();
        surfaceImage.sprite = sprite;
        // 이미지도 영상도 없을 때 연출 자체는 확인할 수 있도록 색만 남긴다.
        surfaceImage.color = sprite != null
            ? new Color(1f, 1f, 1f, 1f)
            : new Color(0.12f, 0.14f, 0.18f, 1f);
    }

    private void PrepareVideoFor(int step)
    {
        currentVideoPath = null;
        if (videoImage != null) videoImage.texture = null;
        if (videoPlayer == null) return;

        currentVideoPath = PickVideoPath(step);
        if (string.IsNullOrEmpty(currentVideoPath))
        {
            videoPlayer.url = string.Empty;
            return;
        }

        videoPlayer.source = VideoSource.Url;
        videoPlayer.url = currentVideoPath;
        videoPlayer.isLooping = loopVideo;
        videoPlayer.Prepare();
    }

    private string PickVideoPath(int step)
    {
        List<string> files;
        if (videoPaths == null || !videoPaths.TryGetValue(step, out files) || files.Count == 0)
            return null;

        if (files.Count == 1) return files[0];
        return pickRandomWhenMultiple ? files[Random.Range(0, files.Count)] : files[0];
    }

    // StreamingAssets/video/<단계>/ 를 훑어 단계별 영상 목록을 만든다.
    // 재생 직전이 아니라 시작할 때 한 번 훑어서, 연출 도중에 디스크를 건드리지 않는다.
    [ContextMenu("영상 폴더 다시 스캔")]
    public void ScanVideoFolders()
    {
        videoPaths = new Dictionary<int, List<string>>();

        string root = Path.Combine(Application.streamingAssetsPath, videoRootFolder);
        if (!Directory.Exists(root))
        {
            Debug.LogWarning($"[Goldilocks] 영상 폴더가 없습니다: {root}");
            return;
        }

        int steps = model != null ? model.StepCount : 9;
        var found = new System.Text.StringBuilder();

        for (int step = 1; step <= steps; step++)
        {
            string dir = Path.Combine(root, step.ToString());
            if (!Directory.Exists(dir)) continue;

            var list = new List<string>();
            foreach (string file in Directory.GetFiles(dir))
            {
                // StreamingAssets 안에도 .meta 가 생기므로 확장자로 걸러야 한다.
                if (IsVideoFile(file)) list.Add(file);
            }
            if (list.Count == 0) continue;

            list.Sort(System.StringComparer.OrdinalIgnoreCase);
            videoPaths[step] = list;
            found.Append($"  D{step}: {list.Count}개 ({Path.GetFileName(list[0])}{(list.Count > 1 ? " 외" : "")})\n");
        }

        if (!logVideoScan) return;

        if (videoPaths.Count == 0)
            Debug.LogWarning($"[Goldilocks] {root} 아래에서 재생할 영상을 찾지 못했습니다.");
        else
            Debug.Log($"[Goldilocks] 영상 {videoPaths.Count}개 단계 발견\n{found}");
    }

    private static bool IsVideoFile(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext == ".mp4" || ext == ".mov" || ext == ".m4v" || ext == ".webm" || ext == ".avi";
    }

    private Sprite PickSurfaceSprite()
    {
        int step = model.CurrentStep;
        if (surfaceByStep != null && step - 1 >= 0 && step - 1 < surfaceByStep.Length && surfaceByStep[step - 1] != null)
            return surfaceByStep[step - 1];

        int zone = (int)model.Evaluate(step);
        if (surfaceByZone != null && zone >= 0 && zone < surfaceByZone.Length)
            return surfaceByZone[zone];

        return null;
    }

    private float PlanetRadius()
    {
        float radius = 0f;
        foreach (var r in planet.GetComponentsInChildren<Renderer>())
        {
            if (r is ParticleSystemRenderer) continue;   // 대기광까지 넣으면 너무 멀리서 멈춘다
            radius = Mathf.Max(radius, r.bounds.extents.y);
        }
        return radius > 0f ? radius : 1f;
    }

    private void CaptureHomePose()
    {
        homePosition = targetCamera.transform.position;
        homeRotation = targetCamera.transform.rotation;
        homeFov = targetCamera.fieldOfView;
    }

    private float Clamp01Progress(float duration)
    {
        return duration <= 0f ? 1f : Mathf.Clamp01(phaseTime / duration);
    }

    private static Vector3 Bezier(Vector3 a, Vector3 b, Vector3 c, float t)
    {
        float u = 1f - t;
        return u * u * a + 2f * u * t * b + t * t * c;
    }

    private static void SetAlpha(Graphic g, float a)
    {
        if (g == null) return;
        var c = g.color;
        c.a = a;
        g.color = c;
    }

    private void SetEffectWeight(float w)
    {
        if (runtimeVolume != null) runtimeVolume.weight = Mathf.Clamp01(w);
    }

    // 프로젝트의 PostFX 프로필을 수정하면 에셋이 더럽혀지므로,
    // 우선순위가 더 높은 런타임 볼륨을 따로 만들어 덧씌운다.
    private void EnsureVolume()
    {
        if (!useVignette && !useMotionBlur) return;

        runtimeProfile = ScriptableObject.CreateInstance<VolumeProfile>();
        runtimeProfile.hideFlags = HideFlags.DontSave;

        if (useVignette)
        {
            vignette = runtimeProfile.Add<Vignette>(true);
            vignette.intensity.overrideState = true;
            vignette.intensity.value = vignetteIntensity;
            vignette.smoothness.overrideState = true;
            vignette.smoothness.value = 0.6f;
        }
        if (useMotionBlur)
        {
            motionBlur = runtimeProfile.Add<MotionBlur>(true);
            motionBlur.intensity.overrideState = true;
            motionBlur.intensity.value = motionBlurIntensity;
        }

        var go = new GameObject("GoldilocksDiveVolume");
        go.transform.SetParent(transform, false);
        go.hideFlags = HideFlags.DontSave;

        runtimeVolume = go.AddComponent<Volume>();
        runtimeVolume.isGlobal = true;
        runtimeVolume.priority = 100f;      // 씬의 PostFX 볼륨보다 위
        runtimeVolume.profile = runtimeProfile;
        runtimeVolume.weight = 0f;
    }

    // APIOnly 로 두면 RenderTexture 를 직접 관리하지 않고 videoPlayer.texture 를 그대로 쓸 수 있다.
    private void EnsureVideoPlayer()
    {
        if (videoPlayer != null) return;

        videoPlayer = gameObject.AddComponent<VideoPlayer>();
        videoPlayer.playOnAwake = false;
        videoPlayer.renderMode = VideoRenderMode.APIOnly;
        videoPlayer.audioOutputMode = VideoAudioOutputMode.Direct;   // AudioSource 없이 바로 출력
        videoPlayer.waitForFirstFrame = true;
        videoPlayer.isLooping = loopVideo;
        videoPlayer.skipOnDrop = true;

        // 오디오 트랙 수는 준비가 끝나야 알 수 있다.
        videoPlayer.prepareCompleted += vp =>
        {
            for (ushort i = 0; i < vp.audioTrackCount; i++)
                vp.SetDirectAudioVolume(i, videoVolume);
        };

        // 반복하지 않을 때도 끝에 도달하면 호출된다. 여기서 원래 시점으로 되돌린다.
        // 행성은 건드리지 않으므로 옮겨진 위치를 그대로 유지한다.
        videoPlayer.loopPointReached += vp =>
        {
            if (!returnWhenVideoEnds || vp.isLooping) return;
            if (phase == Phase.Surface) StartReturn();
        };

        // 재생에 실패했는데 그대로 두면 표면 화면에서 빠져나오지 못한다.
        videoPlayer.errorReceived += (vp, message) =>
        {
            Debug.LogError($"[Goldilocks] 영상 재생 실패: {message}\n{currentVideoPath}");
            currentVideoPath = null;    // 아직 안 띄웠다면 정지 이미지로 넘어간다
            if (phase == Phase.Surface) StartReturn();
        };
    }

    // 전체 화면 플래시와 표면 화면. 인스펙터에서 지정하지 않았으면 만들어 쓴다.
    private void EnsureOverlay()
    {
        if (flashImage != null && surfaceImage != null && videoImage != null) return;

        if (overlayCanvas == null)
        {
            var go = new GameObject("GoldilocksDiveOverlay",
                typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
            go.transform.SetParent(transform, false);

            overlayCanvas = go.GetComponent<Canvas>();
            overlayCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            overlayCanvas.sortingOrder = 200;   // 설정창(100)보다 위

            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
        }

        // 자식 순서가 곧 그리는 순서다. 플래시가 가장 위에 와야 전환을 덮을 수 있다.
        if (surfaceImage == null)
            surfaceImage = CreateFullscreenImage("SurfaceImage", new Color(1f, 1f, 1f, 0f));
        if (videoImage == null)
            videoImage = CreateFullscreenRawImage("SurfaceVideo", new Color(1f, 1f, 1f, 0f));
        if (flashImage == null)
            flashImage = CreateFullscreenImage("Flash", new Color(flashColor.r, flashColor.g, flashColor.b, 0f));
    }

    private RawImage CreateFullscreenRawImage(string name, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(RawImage));
        StretchToOverlay(go.GetComponent<RectTransform>());

        var img = go.GetComponent<RawImage>();
        img.color = color;
        img.raycastTarget = false;
        return img;
    }

    private void StretchToOverlay(RectTransform rt)
    {
        rt.SetParent(overlayCanvas.transform, false);
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    private Image CreateFullscreenImage(string name, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        StretchToOverlay(go.GetComponent<RectTransform>());

        var img = go.GetComponent<Image>();
        img.color = color;
        img.raycastTarget = false;
        img.preserveAspect = false;
        return img;
    }
}
