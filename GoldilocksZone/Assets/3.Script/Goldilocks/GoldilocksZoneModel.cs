using System;
using UnityEngine;

// 태양에서 지구까지의 거리 단계(D1~D9)를 관리하는 단일 진실 공급원.
// 씬 오브젝트를 직접 움직이지 않고 "지금 몇 단계인지"와 "그 단계의 반지름/존 판정"만 책임진다.
// 실제 이동은 EarthOrbitPositioner, 원판 시각화는 ZoneDiscRenderer 가 이벤트를 구독해 처리한다.
//
// 이벤트가 두 갈래인 이유:
//   OnStepChanged - 신호가 들어오는 즉시. 체험자가 실물 지구를 밀면 화면 속 지구도 바로 따라가야 한다.
//   OnStepSettled - 값 변화가 멎고 settleDelay(기본 1초)가 지난 뒤. 동영상 재생 같은 후속 동작용.
public enum ZoneState
{
    TooHot,     // 태양에 너무 가까움
    Habitable,  // 골디락스 존
    TooCold,    // 태양에서 너무 멂
}

[DefaultExecutionOrder(-100)]
public class GoldilocksZoneModel : MonoBehaviour
{
    public static GoldilocksZoneModel Instance { get; private set; }

    [Header("기준 마커 (비워두면 아래 경로로 자동 탐색)")]
    [Tooltip("D1 위치. 태양 로컬 +X 축 위의 최소 거리 마커.")]
    [SerializeField] private Transform minMarker;
    [Tooltip("D9 위치. 태양 로컬 +X 축 위의 최대 거리 마커.")]
    [SerializeField] private Transform maxMarker;
    [SerializeField] private string minMarkerPath = "Actor/Universal Rendering Pipeline Materials/Sun Sphere/min";
    [SerializeField] private string maxMarkerPath = "Actor/Universal Rendering Pipeline Materials/Sun Sphere/max";

    [Header("도착 판정에 사용할 이동 담당자 (비워두면 자동 탐색)")]
    [SerializeField] private EarthOrbitPositioner positioner;

    [Header("디버그")]
    [Tooltip("단계 변경/정착을 콘솔에 남긴다. 현장 배포 시 꺼둘 것.")]
    [SerializeField] private bool verboseLog = true;

    // D 신호를 받을 때마다 발생. 값이 이전과 같아도 발생한다.
    // 위치가 아니라 "체험자가 조작했다"는 사실이 필요한 쪽이 쓴다.
    public event Action<int> OnStepSignal;
    // 신호가 들어와 단계가 바뀐 즉시 발생
    public event Action<int> OnStepChanged;
    // 값 변화가 멎고 settleDelay 가 지난 뒤 발생 (waitForArrival 이면 지구 도착까지 대기)
    public event Action<int> OnStepSettled;

    public GoldilocksJson Config { get; private set; }
    public int CurrentStep { get; private set; } = 1;
    public int LastSettledStep { get; private set; }
    public bool HasSettledOnce { get; private set; }

    // 태양 로컬 좌표 기준 반지름. 태양 스케일(123.75)을 상속받는 자식 좌표계라
    // 이 값을 그대로 지구의 localPosition.x 로 쓸 수 있다.
    public float MinRadius { get; private set; }
    public float MaxRadius { get; private set; }
    public float CurrentRadius => RadiusAt(CurrentStep);
    public ZoneState CurrentZone => Evaluate(CurrentStep);

    // 현재 태양 세기(1~3). 세기가 바뀌면 골디락스 존 범위가 통째로 이동한다.
    public int SunLevel { get; private set; } = 1;
    public event Action<int> OnSunLevelChanged;

    // 현재 세기에 해당하는 설정. 못 찾으면 예비값으로 만든 설정을 돌려준다.
    public SunLevelConfig CurrentSunLevel
    {
        get
        {
            var levels = Config.sunLevels;
            if (levels != null)
                for (int i = 0; i < levels.Count; i++)
                    if (levels[i] != null && levels[i].level == SunLevel)
                        return levels[i];

            return new SunLevelConfig
            {
                level = SunLevel,
                zoneMinStep = Config.zoneMinStep,
                zoneMaxStep = Config.zoneMaxStep,
                lightIntensity = -1f,
                glowScale = -1f,
            };
        }
    }

    public int ZoneMinStep => CurrentSunLevel.zoneMinStep;
    public int ZoneMaxStep => CurrentSunLevel.zoneMaxStep;

    // 원판 경계용. 예) 존이 D4~D6 이면 안쪽 경계는 D3.5, 바깥쪽 경계는 D6.5.
    public float ZoneInnerBoundaryStep => ZoneMinStep - 0.5f;
    public float ZoneOuterBoundaryStep => ZoneMaxStep + 0.5f;
    public float ZoneInnerRadius => RadiusAt(ZoneInnerBoundaryStep);
    public float ZoneOuterRadius => RadiusAt(ZoneOuterBoundaryStep);

    public float DeltaTime => Config.useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;

    private float settleTimer;
    private bool settlePending;
    private bool initialized;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;

        // JsonManager 의 Awake 순서를 기다리지 않도록 설정은 Start 에서 읽는다.
        // (모든 Awake 가 끝난 뒤 Start 가 돌기 때문에 Start 시점에는 항상 로드가 끝나 있다.)
        Config = new GoldilocksJson();
    }

    private void Start()
    {
        if (JsonManager.instance == null || JsonManager.instance.goldilocksJson == null)
            Debug.LogWarning("[Goldilocks] JsonManager 를 찾지 못해 기본 설정으로 동작합니다.");

        EnsureInitialized(force: true);

        CurrentStep = Mathf.Clamp(Config.initialStep, 1, StepCount);
        LastSettledStep = CurrentStep;

        if (verboseLog)
            Debug.Log($"[Goldilocks] 초기화 D1={MinRadius:F4} D{StepCount}={MaxRadius:F4} " +
                      $"1스텝={StepWidth:F4} 태양세기={SunLevel} 존=D{ZoneMinStep}~D{ZoneMaxStep} " +
                      $"시작=D{CurrentStep}");
    }

    // 설정 로드와 반지름 읽기를 한 번만 수행한다.
    // 플레이 중에는 Start 가 호출하고, 에디터에서는 ZoneDiscRenderer 가 원판 미리보기를 위해 호출한다.
    // (에디터에서는 JsonManager 가 없으므로 GoldilocksJson 기본값으로 동작한다.)
    public void EnsureInitialized(bool force = false)
    {
        if (initialized && !force) return;

        var loaded = JsonManager.instance != null ? JsonManager.instance.goldilocksJson : null;
        if (loaded != null) Config = loaded;
        else if (Config == null) Config = new GoldilocksJson();

        ResolveReferences();
        ReadRadiiFromMarkers();

        // 에디터에서 원판을 미리 보려면 세기도 여기서 정해져 있어야 한다.
        SunLevel = ClampSunLevel(Config.initialSunLevel);

        initialized = true;
    }

    // 태양 세기를 바꾼다. 존 범위가 통째로 이동하므로 현재 단계의 존 판정도 함께 바뀐다.
    // 단계(D)는 그대로 두고 "어디까지가 살 수 있는 구간인가"만 달라진다.
    public void SetSunLevel(int level)
    {
        level = ClampSunLevel(level);
        if (level == SunLevel) return;

        SunLevel = level;

        if (verboseLog)
            Debug.Log($"[Goldilocks] 태양 세기 {SunLevel} - 존 D{ZoneMinStep}~D{ZoneMaxStep} " +
                      $"(현재 D{CurrentStep}: {ZoneLabel(CurrentZone)})");

        OnSunLevelChanged?.Invoke(SunLevel);
    }

    public int SunLevelCount
    {
        get
        {
            var levels = Config.sunLevels;
            return levels != null && levels.Count > 0 ? levels.Count : 1;
        }
    }

    private int ClampSunLevel(int level)
    {
        var levels = Config.sunLevels;
        if (levels == null || levels.Count == 0) return level;

        int min = int.MaxValue, max = int.MinValue;
        for (int i = 0; i < levels.Count; i++)
        {
            if (levels[i] == null) continue;
            if (levels[i].level < min) min = levels[i].level;
            if (levels[i].level > max) max = levels[i].level;
        }
        if (min > max) return level;
        return Mathf.Clamp(level, min, max);
    }

    private void Update()
    {
        if (!settlePending) return;

        settleTimer -= DeltaTime;
        if (settleTimer > 0f) return;

        // 1초가 지나도 지구가 아직 미끄러지는 중이면 도착할 때까지 기다린다.
        if (Config.waitForArrival && positioner != null && !positioner.IsArrived) return;

        settlePending = false;

        if (Config.ignoreSameAsLastSettled && HasSettledOnce && LastSettledStep == CurrentStep)
            return;

        LastSettledStep = CurrentStep;
        HasSettledOnce = true;

        if (verboseLog)
            Debug.Log($"[Goldilocks] 정착 D{CurrentStep} ({ZoneLabel(CurrentZone)})");

        OnStepSettled?.Invoke(CurrentStep);
    }

    // RS232 수신부와 개발용 키보드 입력이 공통으로 호출하는 진입점.
    // 같은 값이 반복해서 들어와도 정착 타이머를 되돌리지 않는다.
    // (장비가 현재 위치를 계속 흘려보내는 방식이면 타이머가 영영 안 채워지기 때문)
    public void SetStep(int step)
    {
        step = Mathf.Clamp(step, 1, StepCount);

        // 값이 그대로여도 "신호가 들어왔다"는 사실 자체가 필요한 곳이 있다.
        // 대기영상에서는 지구가 이미 그 자리에 있어도 D 신호가 오면 체험을 시작해야 한다.
        OnStepSignal?.Invoke(step);

        if (step == CurrentStep) return;

        CurrentStep = step;
        settleTimer = Config.settleDelay;
        settlePending = true;

        if (verboseLog)
            Debug.Log($"[Goldilocks] 단계 변경 D{step} r={CurrentRadius:F4} ({ZoneLabel(CurrentZone)})");

        OnStepChanged?.Invoke(step);
    }

    public int StepCount => Mathf.Max(2, Config.stepCount);

    // 한 단계의 폭(태양 로컬 단위)
    public float StepWidth => (MaxRadius - MinRadius) / (StepCount - 1);

    // 실수 단계도 받는다. 원판 경계(D3.5)나 바깥 여유(D9.5) 계산에 쓰이므로
    // 1~StepCount 범위를 벗어나도 그대로 외삽한다.
    public float RadiusAt(float step)
    {
        float t = (step - 1f) / (StepCount - 1);
        return Mathf.LerpUnclamped(MinRadius, MaxRadius, t);
    }

    public ZoneState Evaluate(int step)
    {
        if (step < ZoneMinStep) return ZoneState.TooHot;
        if (step > ZoneMaxStep) return ZoneState.TooCold;
        return ZoneState.Habitable;
    }

    public static string ZoneLabel(ZoneState state)
    {
        switch (state)
        {
            case ZoneState.TooHot: return "너무 뜨겁다";
            case ZoneState.Habitable: return "생명체 거주 가능";
            default: return "너무 춥다";
        }
    }

    private void ResolveReferences()
    {
        if (minMarker == null) minMarker = FindByPath(minMarkerPath);
        if (maxMarker == null) maxMarker = FindByPath(maxMarkerPath);
        if (positioner == null) positioner = FindObjectOfType<EarthOrbitPositioner>();
    }

    // 반지름을 인스펙터에 박아두지 않고 씬의 min/max 마커에서 읽는다.
    // 나중에 에디터에서 마커를 눈으로 옮겨도 코드/설정 수정이 필요 없다.
    private void ReadRadiiFromMarkers()
    {
        if (minMarker == null || maxMarker == null)
        {
            Debug.LogError("[Goldilocks] min/max 마커를 찾지 못했습니다. 경로를 확인하세요.");
            return;
        }

        MinRadius = minMarker.localPosition.x;
        MaxRadius = maxMarker.localPosition.x;

        if (Mathf.Approximately(MinRadius, MaxRadius))
            Debug.LogError("[Goldilocks] min 과 max 의 로컬 X 가 같습니다. 마커 위치를 확인하세요.");
    }

    private static Transform FindByPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var go = GameObject.Find(path);
        return go != null ? go.transform : null;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }
}
