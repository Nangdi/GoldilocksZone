using UnityEngine;

// 태양 세기 단계에 맞춰 태양의 밝기를 바꾼다.
// 존 범위 계산은 GoldilocksZoneModel 이 하고, 여기서는 보이는 것만 담당한다.
//
// goldilocks.json 의 sunLevels 에서 단계별 lightIntensity / glowScale 을 읽는다.
// 값이 0 이하이면 그 항목은 건드리지 않으므로, 조명만 쓰거나 글로우만 쓸 수도 있다.
public class SunLevelVisual : MonoBehaviour
{
    [Header("대상 (비워두면 아래 경로로 자동 탐색)")]
    [SerializeField] private Light sunLight;
    [SerializeField] private string sunLightPath = "Actor/Universal Rendering Pipeline Materials/Sun Sphere/Point Light";
    [SerializeField] private ParticleSystem sunGlow;
    [SerializeField] private string sunGlowPath = "Actor/Universal Rendering Pipeline Materials/Sun Sphere/Sun Glow";

    [Header("전환")]
    [Tooltip("세기가 바뀔 때 몇 초에 걸쳐 밝아지거나 어두워질지. 0 이면 즉시 바뀐다.")]
    [SerializeField] private float transitionSeconds = 0.6f;

    private GoldilocksZoneModel model;

    // 파티클 크기는 원본 대비 배율로 다루므로 시작값을 기억해 둔다.
    private float baseGlowSize = -1f;

    private float fromIntensity, toIntensity;
    private float fromGlow, toGlow;
    private float elapsed;
    private bool transitioning;

    private void Start()
    {
        if (sunLight == null) sunLight = FindComponent<Light>(sunLightPath);
        if (sunGlow == null) sunGlow = FindComponent<ParticleSystem>(sunGlowPath);

        model = GoldilocksZoneModel.Instance;
        if (model == null)
        {
            Debug.LogError("[Goldilocks] GoldilocksZoneModel 을 찾지 못해 태양 밝기를 제어할 수 없습니다.");
            enabled = false;
            return;
        }

        if (sunGlow != null) baseGlowSize = sunGlow.main.startSize.constant;

        model.OnSunLevelChanged += OnSunLevelChanged;

        // 시작 세기는 이벤트 없이 정해지므로 여기서 한 번 즉시 적용한다.
        Apply(TargetIntensity(), TargetGlowScale());
    }

    private void OnDestroy()
    {
        if (model != null) model.OnSunLevelChanged -= OnSunLevelChanged;
    }

    private void OnSunLevelChanged(int level)
    {
        float ti = TargetIntensity();
        float tg = TargetGlowScale();

        if (transitionSeconds <= 0f)
        {
            Apply(ti, tg);
            return;
        }

        fromIntensity = sunLight != null ? sunLight.intensity : 0f;
        fromGlow = CurrentGlowScale();
        toIntensity = ti;
        toGlow = tg;
        elapsed = 0f;
        transitioning = true;
    }

    private void Update()
    {
        if (!transitioning) return;

        elapsed += model.DeltaTime;
        float t = Mathf.Clamp01(elapsed / transitionSeconds);
        Apply(Mathf.Lerp(fromIntensity, toIntensity, t), Mathf.Lerp(fromGlow, toGlow, t));

        if (t >= 1f) transitioning = false;
    }

    private void Apply(float intensity, float glowScale)
    {
        if (sunLight != null && intensity > 0f)
            sunLight.intensity = intensity;

        if (sunGlow != null && glowScale > 0f && baseGlowSize > 0f)
        {
            var main = sunGlow.main;
            main.startSize = baseGlowSize * glowScale;
        }
    }

    // 설정값이 0 이하이면 "건드리지 않는다"는 뜻이므로 현재값을 그대로 목표로 삼는다.
    private float TargetIntensity()
    {
        float v = model.CurrentSunLevel.lightIntensity;
        if (v > 0f) return v;
        return sunLight != null ? sunLight.intensity : 0f;
    }

    private float TargetGlowScale()
    {
        float v = model.CurrentSunLevel.glowScale;
        return v > 0f ? v : CurrentGlowScale();
    }

    private float CurrentGlowScale()
    {
        if (sunGlow == null || baseGlowSize <= 0f) return 1f;
        return sunGlow.main.startSize.constant / baseGlowSize;
    }

    private static T FindComponent<T>(string path) where T : Component
    {
        if (string.IsNullOrEmpty(path)) return null;
        var go = GameObject.Find(path);
        return go != null ? go.GetComponent<T>() : null;
    }
}
