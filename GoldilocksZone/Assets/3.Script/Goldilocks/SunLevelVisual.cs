using UnityEngine;

// 태양 세기 단계에 맞춰 태양의 밝기와 크기를 바꾼다.
// 존 범위 계산은 GoldilocksZoneModel 이 하고, 여기서는 보이는 것만 담당한다.
//
// goldilocks.json 의 sunLevels 에서 단계별 lightIntensity / sunScale / glowScale 을 읽는다.
// 값이 0 이하이면 그 항목은 건드리지 않으므로, 조명만 쓰거나 크기만 쓸 수도 있다.
//
// [크기를 바꾸는 방식에 대하여]
// 1) Sun Sphere 트랜스폼은 min / max / Earth / ZoneDisc 의 부모다.
//    여기를 키우면 태양뿐 아니라 궤도 좌표계 전체가 같이 커져서 D1~D9 기준이 무너진다.
//    그래서 Sun Sphere 의 메시만 "Sun Body" 자식으로 옮겨 담고 그 자식만 키운다.
// 2) 화면에서 "태양"으로 보이는 것은 사실 본체 메시가 아니라 파티클이다.
//    본체는 납작한 주황 구(Unlit)라서 평소에는 글로우 안에 파묻혀 보이지 않는다.
//    따라서 본체만 키우면 글로우 밖으로 삐져나와 주황 원반이 그대로 드러난다.
//    파티클도 같은 비율로 키워야 한다.
// 3) 파티클 크기를 main.startSize 로 바꾸면 이미 살아 있는 입자에는 적용되지 않는다.
//    Sun FX 는 수명이 10초라 크기가 10초에 걸쳐 어정쩡하게 바뀐다.
//    게다가 Sun FX 는 Mesh shape 으로 표면에 뿌려지는데 그 shape 반지름은
//    startSize 를 따라가지 않아서, 입자 껍질만 원래 크기로 남는다.
//    두 문제 모두 파티클 오브젝트의 트랜스폼 스케일을 바꾸면 한 번에 해결된다.
//    (두 시스템 모두 scalingMode = Hierarchy 라 계층 스케일이 즉시 반영된다.)
public class SunLevelVisual : MonoBehaviour
{
    // 태양 본체 메시를 옮겨 담을 자식 이름
    private const string BodyChildName = "Sun Body";

    [Header("대상 (비워두면 아래 경로로 자동 탐색)")]
    [SerializeField] private Light sunLight;
    [SerializeField] private string sunLightPath = "Actor/Universal Rendering Pipeline Materials/Sun Sphere/Point Light";
    [SerializeField] private ParticleSystem sunGlow;
    [SerializeField] private string sunGlowPath = "Actor/Universal Rendering Pipeline Materials/Sun Sphere/Sun Glow";
    [SerializeField] private ParticleSystem sunFx;
    [SerializeField] private string sunFxPath = "Actor/Universal Rendering Pipeline Materials/Sun Sphere/Sun FX";

    [Header("태양 본체")]
    [Tooltip("메시를 가진 태양 오브젝트. 이 트랜스폼은 건드리지 않고 자식만 만들어 키운다.")]
    [SerializeField] private string sunSpherePath = "Actor/Universal Rendering Pipeline Materials/Sun Sphere";
    [Tooltip("끄면 크기는 그대로 두고 밝기만 단계에 따라 바뀐다.")]
    [SerializeField] private bool scaleSun = true;

    [Header("전환")]
    [Tooltip("세기가 바뀔 때 몇 초에 걸쳐 밝아지거나 어두워질지. 0 이면 즉시 바뀐다.")]
    [SerializeField] private float transitionSeconds = 0.6f;

    private GoldilocksZoneModel model;

    // 본체 메시를 담아 크기를 주는 자식. 원본은 꺼두고 이것만 보인다.
    private Transform sunBody;
    private MeshRenderer originalBodyRenderer;

    private float fromIntensity, toIntensity;
    private float fromGlow, toGlow;
    private float fromScale, toScale;
    private float elapsed;
    private bool transitioning;

    private void Start()
    {
        if (sunLight == null) sunLight = FindComponent<Light>(sunLightPath);
        if (sunGlow == null) sunGlow = FindComponent<ParticleSystem>(sunGlowPath);
        if (sunFx == null) sunFx = FindComponent<ParticleSystem>(sunFxPath);

        model = GoldilocksZoneModel.Instance;
        if (model == null)
        {
            Debug.LogError("[Goldilocks] GoldilocksZoneModel 을 찾지 못해 태양 밝기를 제어할 수 없습니다.");
            enabled = false;
            return;
        }

        if (scaleSun) EnsureSunBody();

        model.OnSunLevelChanged += OnSunLevelChanged;

        // 시작 세기는 이벤트 없이 정해지므로 여기서 한 번 즉시 적용한다.
        Apply(TargetIntensity(), TargetSunScale(), TargetGlowScale());
    }

    private void OnDestroy()
    {
        if (model != null) model.OnSunLevelChanged -= OnSunLevelChanged;

        // 에디터에서 플레이를 멈췄을 때 원본 메시가 꺼진 채로 남지 않게 되돌린다.
        if (originalBodyRenderer != null) originalBodyRenderer.enabled = true;
    }

    private void OnSunLevelChanged(int level)
    {
        float ti = TargetIntensity();
        float ts = TargetSunScale();
        float tg = TargetGlowScale();

        if (transitionSeconds <= 0f)
        {
            Apply(ti, ts, tg);
            return;
        }

        fromIntensity = sunLight != null ? sunLight.intensity : 0f;
        fromScale = CurrentSunScale();
        fromGlow = CurrentGlowScale();
        toIntensity = ti;
        toScale = ts;
        toGlow = tg;
        elapsed = 0f;
        transitioning = true;
    }

    private void Update()
    {
        if (!transitioning) return;

        elapsed += model.DeltaTime;
        float t = Mathf.Clamp01(elapsed / transitionSeconds);
        Apply(Mathf.Lerp(fromIntensity, toIntensity, t),
              Mathf.Lerp(fromScale, toScale, t),
              Mathf.Lerp(fromGlow, toGlow, t));

        if (t >= 1f) transitioning = false;
    }

    // 태양 본체 메시를 복제한 자식을 만들고 원본은 끈다.
    // 이미 만들어져 있으면 그대로 쓴다(컴포넌트를 껐다 켜도 중복 생성되지 않게).
    private void EnsureSunBody()
    {
        var sphere = GameObject.Find(sunSpherePath);
        if (sphere == null)
        {
            Debug.LogWarning($"[Goldilocks] 태양 본체를 찾지 못했습니다: {sunSpherePath}");
            return;
        }

        originalBodyRenderer = sphere.GetComponent<MeshRenderer>();

        var existing = sphere.transform.Find(BodyChildName);
        if (existing != null)
        {
            sunBody = existing;
            if (originalBodyRenderer != null) originalBodyRenderer.enabled = false;
            return;
        }

        var srcFilter = sphere.GetComponent<MeshFilter>();
        if (srcFilter == null || srcFilter.sharedMesh == null || originalBodyRenderer == null)
        {
            Debug.LogWarning("[Goldilocks] 태양 본체에 메시가 없어 크기를 바꿀 수 없습니다.");
            return;
        }

        var go = new GameObject(BodyChildName);
        go.transform.SetParent(sphere.transform, false);
        go.layer = sphere.layer;

        go.AddComponent<MeshFilter>().sharedMesh = srcFilter.sharedMesh;

        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterials = originalBodyRenderer.sharedMaterials;
        mr.shadowCastingMode = originalBodyRenderer.shadowCastingMode;
        mr.receiveShadows = originalBodyRenderer.receiveShadows;
        mr.lightProbeUsage = originalBodyRenderer.lightProbeUsage;
        mr.reflectionProbeUsage = originalBodyRenderer.reflectionProbeUsage;

        originalBodyRenderer.enabled = false;

        sunBody = go.transform;
    }

    private void Apply(float intensity, float sunScale, float glowScale)
    {
        if (sunLight != null && intensity > 0f)
            sunLight.intensity = intensity;

        if (sunScale <= 0f) return;

        // 본체 · 표면 불꽃 · 코로나가 같은 비율로 커져야 태양 하나로 보인다.
        if (sunBody != null) sunBody.localScale = Vector3.one * sunScale;
        if (sunFx != null) sunFx.transform.localScale = Vector3.one * sunScale;
        if (sunGlow != null)
        {
            // 코로나만 본체 대비 더/덜 퍼지게 하고 싶을 때 쓰는 여분의 배율.
            float extra = glowScale > 0f ? glowScale : 1f;
            sunGlow.transform.localScale = Vector3.one * (sunScale * extra);
        }
    }

    // 설정값이 0 이하이면 "건드리지 않는다"는 뜻이므로 현재값을 그대로 목표로 삼는다.
    private float TargetIntensity()
    {
        float v = model.CurrentSunLevel.lightIntensity;
        if (v > 0f) return v;
        return sunLight != null ? sunLight.intensity : 0f;
    }

    private float TargetSunScale()
    {
        if (!scaleSun) return CurrentSunScale();
        float v = model.CurrentSunLevel.sunScale;
        return v > 0f ? v : CurrentSunScale();
    }

    private float TargetGlowScale()
    {
        float v = model.CurrentSunLevel.glowScale;
        return v > 0f ? v : CurrentGlowScale();
    }

    private float CurrentSunScale()
    {
        if (sunBody != null) return sunBody.localScale.x;
        if (sunFx != null) return sunFx.transform.localScale.x;
        return 1f;
    }

    // 글로우의 "여분 배율"만 되돌려 준다(본체 배율을 나눈 값).
    private float CurrentGlowScale()
    {
        if (sunGlow == null) return 1f;
        float s = CurrentSunScale();
        if (Mathf.Approximately(s, 0f)) return 1f;
        return sunGlow.transform.localScale.x / s;
    }

    private static T FindComponent<T>(string path) where T : Component
    {
        if (string.IsNullOrEmpty(path)) return null;
        var go = GameObject.Find(path);
        return go != null ? go.GetComponent<T>() : null;
    }
}
