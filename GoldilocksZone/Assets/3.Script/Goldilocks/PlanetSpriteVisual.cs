using UnityEngine;

// 3D 지구 대신 2D 행성 스프라이트를 보여준다.
//
// 태양 세기마다 쓰는 그림이 다르다. 존 범위가 세기마다 옮겨가므로(1/3/5, 2/3/4, 3/3/3)
// 같은 D 칸이라도 세기에 따라 뜨거운 칸이 되기도 하고 거주 가능한 칸이 되기도 하기 때문이다.
// 원본 11장 중 9장을 골라 쓰며, 세기별로 고르는 조합이 다르다.
//
//   세기 1 (작은 태양, 1/3/5) : 1  3  5  6  7  8  9 10 11
//   세기 2 (기본,     2/3/4) : 1  2  4  5  6  7  8 10 11
//   세기 3 (큰 태양,  3/3/3) : 1  2  3  4  5  6  8 10 11
//
// 스프라이트는 카메라를 향하는 빌보드다. 구는 어느 각도에서 봐도 원이라
// 빌보드가 가장 잘 통하는 대상이고, 월드 공간에 두므로 원근에 따른 크기 변화는
// 카메라가 알아서 만든다(D1 대비 D9 가 2.2배).
//
// 칸이 바뀔 때 그림을 툭 갈아 끼우면 튄다. 지구는 SmoothDamp 로 칸 사이를
// 연속으로 미끄러지기 때문이다. 그래서 실제 반지름에서 실수 칸을 구해
// 양옆 두 장을 섞는다(crossfade). 멈춰 서면 정확히 한 장만 남는다.
[DefaultExecutionOrder(60)]
public class PlanetSpriteVisual : MonoBehaviour
{
    // 원본 11장 중 세기별로 쓰는 9장의 번호. 인덱스 0 이 D1.
    public static readonly int[] SmallSunNumbers = { 1, 3, 5, 6, 7, 8, 9, 10, 11 };
    public static readonly int[] MediumSunNumbers = { 1, 2, 4, 5, 6, 7, 8, 10, 11 };
    public static readonly int[] LargeSunNumbers = { 1, 2, 3, 4, 5, 6, 8, 10, 11 };

    [Header("대상 (비워두면 자동 탐색)")]
    [Tooltip("스프라이트를 따라다니게 할 행성. 이 오브젝트를 그 자식으로 두면 자동으로 따라간다.")]
    [SerializeField] private Transform planet;
    [SerializeField] private string planetPath = "Actor/Universal Rendering Pipeline Materials/Sun Sphere/Earth";
    [SerializeField] private Camera targetCamera;

    [Header("표시")]
    [Tooltip("켜면 3D 행성 메시를 숨긴다. 끄면 3D 와 스프라이트가 겹쳐 보인다(비교용).")]
    [SerializeField] private bool hide3DPlanet = true;
    [Tooltip("켜면 행성을 감싼 글로우 파티클도 숨긴다. " +
             "스프라이트에 대기광이 이미 그려져 있어서, 켜두지 않으면 그 위에 하얀 덩어리가 겹친다.")]
    [SerializeField] private bool hidePlanetGlow = true;
    [Tooltip("칸 사이를 이동하는 동안 앞뒤 그림을 섞는다. 끄면 칸이 바뀌는 순간 툭 갈아 끼운다.")]
    [SerializeField] private bool crossfade = true;

    [Header("크기")]
    [Tooltip("스프라이트 세로 길이에서 행성 본체가 차지하는 비율. " +
             "1024px 중 본체가 780px 이라 0.762 다. 잘라내기 규격이 바뀌면 같이 고쳐야 한다.")]
    [SerializeField] private float bodyRatio = 780f / 1024f;
    [Tooltip("행성 본체의 월드 지름. 0 이면 숨기기 직전의 3D 행성 크기를 그대로 물려받는다.")]
    [SerializeField] private float bodyDiameter = 0f;

    [Header("태양 세기별 그림 (인덱스 0 = D1)")]
    [SerializeField] private Sprite[] smallSun = new Sprite[9];
    [SerializeField] private Sprite[] mediumSun = new Sprite[9];
    [SerializeField] private Sprite[] largeSun = new Sprite[9];

    // 실수 칸이 정수에 이만큼 가까우면 한 칸에 서 있는 것으로 본다.
    // 1칸이 0.117 인 좌표계라 0.002칸은 0.00023 로컬 단위, 화면에서는 보이지 않는 차이다.
    private const float SnapTolerance = 0.002f;

    private GoldilocksZoneModel model;
    private SpriteRenderer backRenderer;    // 이전 칸 그림. 항상 불투명하게 깔린다.
    private SpriteRenderer frontRenderer;   // 현재 칸 그림. 알파로 덮어쓰며 섞인다.
    private Renderer[] bodyRenderers;    // 3D 행성 메시
    private Renderer[] glowRenderers;    // 행성을 감싼 파티클
    private bool captured;

    private void Start()
    {
        if (planet == null)
        {
            var go = GameObject.Find(planetPath);
            if (go != null) planet = go.transform;
        }
        if (targetCamera == null) targetCamera = Camera.main;

        model = GoldilocksZoneModel.Instance;

        if (planet == null || targetCamera == null || model == null)
        {
            Debug.LogError($"[Goldilocks] 행성 스프라이트에 필요한 참조가 없습니다. " +
                           $"planet={planet != null} camera={targetCamera != null} model={model != null}");
            enabled = false;
            return;
        }

        // 순서가 중요하다. 스프라이트를 만들기 전에 기존 렌더러를 먼저 잡아둬야 한다.
        // 이 오브젝트가 행성의 자식이라, 나중에 모으면 방금 만든 SpriteRenderer 까지
        // "3D 행성"으로 잡혀서 같이 꺼져 버린다.
        CaptureExistingRenderers();

        // 3D 메시를 숨기기 전에 크기를 재둔다. 숨긴 뒤에는 이 값이 기준이 된다.
        if (bodyDiameter <= 0f) bodyDiameter = Measure3DPlanetDiameter();

        CreateRenderers();
        HideOrShow3D();
        ApplyScale();
        Refresh(instant: true);
    }

    private void OnDisable()
    {
        // 컴포넌트를 꺼두면 3D 행성이 다시 보이도록 되돌린다.
        SetEnabled(bodyRenderers, true);
        SetEnabled(glowRenderers, true);
    }

    private void OnEnable()
    {
        if (captured) HideOrShow3D();
    }

    private float Measure3DPlanetDiameter()
    {
        float radius = 0f;
        foreach (var r in bodyRenderers)
        {
            if (r == null) continue;
            radius = Mathf.Max(radius, Mathf.Max(r.bounds.extents.x,
                     Mathf.Max(r.bounds.extents.y, r.bounds.extents.z)));
        }
        return radius > 0f ? radius * 2f : 1f;
    }

    // 스프라이트를 만들기 전에 한 번만 호출한다.
    // 이 오브젝트 아래에 있는 것(= 우리가 만든 스프라이트)은 절대 담지 않는다.
    private void CaptureExistingRenderers()
    {
        var body = new System.Collections.Generic.List<Renderer>();
        var glow = new System.Collections.Generic.List<Renderer>();

        foreach (var r in planet.GetComponentsInChildren<Renderer>(true))
        {
            if (r.transform.IsChildOf(transform)) continue;

            if (r is ParticleSystemRenderer) glow.Add(r);
            else if (r is MeshRenderer || r is SkinnedMeshRenderer) body.Add(r);
        }

        bodyRenderers = body.ToArray();
        glowRenderers = glow.ToArray();
        captured = true;
    }

    private void HideOrShow3D()
    {
        SetEnabled(bodyRenderers, !hide3DPlanet);
        SetEnabled(glowRenderers, !hidePlanetGlow);
    }

    private static void SetEnabled(Renderer[] list, bool value)
    {
        if (list == null) return;
        foreach (var r in list)
            if (r != null) r.enabled = value;
    }

    private void CreateRenderers()
    {
        backRenderer = CreateRenderer("__planetBack", 0);
        frontRenderer = CreateRenderer("__planetFront", 1);
    }

    private SpriteRenderer CreateRenderer(string name, int order)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        go.hideFlags = HideFlags.DontSave;

        var sr = go.AddComponent<SpriteRenderer>();
        sr.sortingOrder = order;
        // 스프라이트 기본 머티리얼은 queue 3000 이라 골디락스 원판(2990)보다 나중에 그려진다.
        // 덕분에 원판 색이 행성 위에 덧칠되지 않는다. ZTest 는 LEqual 이라 태양 뒤로 가면 가려진다.
        return sr;
    }

    // 스프라이트 한 장의 월드 크기를 재서, 본체 지름이 bodyDiameter 가 되도록 로컬 스케일을 잡는다.
    private void ApplyScale()
    {
        var probe = FirstAvailableSprite();
        if (probe == null) return;

        float spriteHeight = probe.bounds.size.y;               // PPU 를 반영한 로컬 크기
        if (spriteHeight <= 0f || bodyRatio <= 0f) return;

        float parentScale = transform.parent != null ? transform.parent.lossyScale.y : 1f;
        if (Mathf.Approximately(parentScale, 0f)) parentScale = 1f;

        float wanted = bodyDiameter / (spriteHeight * bodyRatio);
        transform.localScale = Vector3.one * (wanted / parentScale);
    }

    private Sprite FirstAvailableSprite()
    {
        var sets = new[] { mediumSun, smallSun, largeSun };
        foreach (var set in sets)
            if (set != null)
                foreach (var s in set)
                    if (s != null) return s;
        return null;
    }

    private Sprite[] CurrentSet()
    {
        switch (model.SunLevel)
        {
            case 1: return smallSun;
            case 3: return largeSun;
            default: return mediumSun;
        }
    }

    private Sprite SpriteForStep(int step)
    {
        var set = CurrentSet();
        if (set == null || set.Length == 0) return null;
        int i = Mathf.Clamp(step - 1, 0, set.Length - 1);
        return set[i];
    }

    private void LateUpdate()
    {
        if (model == null) return;

        // 빌보드: 항상 카메라를 마주 본다. 부모(지구)가 자전해도 여기서 덮어쓴다.
        Vector3 toCamera = transform.position - targetCamera.transform.position;
        if (toCamera.sqrMagnitude > 0.0001f)
            transform.rotation = Quaternion.LookRotation(toCamera, targetCamera.transform.up);

        Refresh(instant: false);
    }

    // 지구의 실제 반지름에서 실수 칸을 구해 양옆 두 장을 섞는다.
    // 예) D3 와 D4 사이 40% 지점이면 D3 그림 위에 D4 그림을 알파 0.4 로 덮는다.
    private void Refresh(bool instant)
    {
        float step = CurrentFractionalStep();

        // 칸에 정확히 서 있어도 나눗셈 오차 때문에 5.0 이 4.9999995 로 나오는 일이 있다.
        // 그대로 두면 아래에 D4 그림이 깔린 채 위의 D5 그림 알파로만 가려지는데,
        // 두 그림은 글로우 크기가 달라서 아래 그림의 빛무리가 밖으로 삐져나온다.
        // (실제로 D2 에서 1번 용암 행성의 주황 테두리가 보였다.)
        // 그래서 정수 칸에 붙으면 한 장으로 스냅한다.
        float nearest = Mathf.Round(step);
        if (Mathf.Abs(step - nearest) < SnapTolerance) step = nearest;

        int lo = Mathf.Clamp(Mathf.FloorToInt(step), 1, model.StepCount);
        int hi = Mathf.Clamp(lo + 1, 1, model.StepCount);
        float blend = Mathf.Clamp01(step - lo);

        if (!crossfade || instant)
        {
            lo = hi = Mathf.Clamp(Mathf.RoundToInt(step), 1, model.StepCount);
            blend = 0f;
        }

        backRenderer.sprite = SpriteForStep(lo);
        frontRenderer.sprite = SpriteForStep(hi);

        SetAlpha(backRenderer, 1f);
        SetAlpha(frontRenderer, blend);
    }

    // 현재 반지름을 칸 단위로 되돌린다. RadiusAt 의 역함수다.
    private float CurrentFractionalStep()
    {
        float width = model.StepWidth;
        if (Mathf.Approximately(width, 0f)) return model.CurrentStep;

        float radius = planet.localPosition.x;
        return 1f + (radius - model.MinRadius) / width;
    }

    private static void SetAlpha(SpriteRenderer sr, float a)
    {
        if (sr == null) return;
        var c = sr.color;
        c.a = a;
        sr.color = c;
    }

#if UNITY_EDITOR
    // 그림을 추가하거나 이름을 바꿨을 때 다시 채우기 위한 도구.
    // 파일 이름의 두 자리 번호(01~11)로 찾는다.
    [ContextMenu("스프라이트 자동 채우기")]
    private void AutoFill()
    {
        smallSun = Collect(SmallSunNumbers);
        mediumSun = Collect(MediumSunNumbers);
        largeSun = Collect(LargeSunNumbers);
        UnityEditor.EditorUtility.SetDirty(this);
        Debug.Log("[Goldilocks] 행성 스프라이트를 다시 채웠습니다.");
    }

    private static Sprite[] Collect(int[] numbers)
    {
        var result = new Sprite[numbers.Length];
        var guids = UnityEditor.AssetDatabase.FindAssets("t:Sprite", new[] { "Assets/4.Sprite/Planet" });

        for (int i = 0; i < numbers.Length; i++)
        {
            string tag = "_" + numbers[i].ToString("00") + "_";
            foreach (var g in guids)
            {
                string path = UnityEditor.AssetDatabase.GUIDToAssetPath(g);
                if (System.IO.Path.GetFileName(path).Contains(tag))
                {
                    result[i] = UnityEditor.AssetDatabase.LoadAssetAtPath<Sprite>(path);
                    break;
                }
            }
            if (result[i] == null)
                Debug.LogWarning($"[Goldilocks] {numbers[i]}번 행성 그림을 찾지 못했습니다.");
        }
        return result;
    }
#endif
}
