using UnityEngine;

// 지구를 태양 로컬 +X 축 위에서 현재 단계에 해당하는 반지름으로 이동시킨다.
//
// 코루틴으로 "0.8초 동안 이동"을 매번 새로 시작하지 않는 이유:
// 체험자가 실물 지구를 밀면 D1~D5 가 순식간에 연달아 들어오는데, 그때마다 코루틴을
// 다시 시작하면 이동이 끊기고 튄다. 여기서는 목표값만 갈아끼우고 하나의 추적 루프가
// SmoothDamp 로 계속 따라가므로, 신호가 몰려도 지구는 한 번에 매끄럽게 미끄러진다.
public class EarthOrbitPositioner : MonoBehaviour
{
    [Header("이동 대상 (비워두면 아래 경로로 자동 탐색)")]
    [SerializeField] private Transform earth;
    [SerializeField] private string earthPath = "Actor/Universal Rendering Pipeline Materials/Sun Sphere/Earth";

    // 목표 반지름에 도달했는지. 모델이 "정착" 판정에 참조한다.
    public bool IsArrived { get; private set; } = true;
    public float TargetRadius { get; private set; }
    public float CurrentRadius => earth != null ? earth.localPosition.x : 0f;

    private float velocity;
    private GoldilocksZoneModel model;

    private void Awake()
    {
        if (earth == null && !string.IsNullOrEmpty(earthPath))
        {
            var go = GameObject.Find(earthPath);
            if (go != null) earth = go.transform;
        }
    }

    private void Start()
    {
        model = GoldilocksZoneModel.Instance;
        if (model == null)
        {
            Debug.LogError("[Goldilocks] GoldilocksZoneModel 을 찾지 못해 지구 이동을 시작할 수 없습니다.");
            enabled = false;
            return;
        }
        if (earth == null)
        {
            Debug.LogError($"[Goldilocks] 지구 트랜스폼을 찾지 못했습니다: {earthPath}");
            enabled = false;
            return;
        }

        model.OnStepChanged += OnStepChanged;

        // 시작 단계 위치로는 보간 없이 바로 놓는다.
        TargetRadius = model.CurrentRadius;
        SetRadius(TargetRadius);
        velocity = 0f;
        IsArrived = true;
    }

    private void OnDestroy()
    {
        if (model != null) model.OnStepChanged -= OnStepChanged;
    }

    private void OnStepChanged(int step)
    {
        TargetRadius = model.RadiusAt(step);
        IsArrived = false;
    }

    private void Update()
    {
        if (IsArrived) return;

        float current = earth.localPosition.x;
        float next = Mathf.SmoothDamp(current, TargetRadius, ref velocity,
                                      model.Config.moveSmoothTime, Mathf.Infinity, model.DeltaTime);

        if (Mathf.Abs(TargetRadius - next) <= model.Config.arriveEpsilon)
        {
            next = TargetRadius;
            velocity = 0f;
            IsArrived = true;
        }

        SetRadius(next);
    }

    // y/z 는 건드리지 않는다. min/max/지구 모두 로컬 y=z=0 이지만,
    // 나중에 지구에 살짝 오프셋을 주더라도 이동이 그것을 지우지 않도록.
    private void SetRadius(float radius)
    {
        var p = earth.localPosition;
        p.x = radius;
        earth.localPosition = p;
    }
}
