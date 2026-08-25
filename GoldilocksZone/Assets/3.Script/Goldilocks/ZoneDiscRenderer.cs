using UnityEngine;
using UnityEngine.Rendering;

// 태양을 중심으로 하는 원판. 태양에 가까운 쪽은 적색, 골디락스 존은 초록으로 칠한다.
//
// 이 오브젝트는 Sun Sphere 의 자식으로 두고 localPosition 0 / localRotation 단위 / localScale 1 을 유지해야 한다.
// 그래야 태양 스케일(123.75)을 그대로 상속받아 min/max 의 로컬 반지름 값을 좌표로 쓸 수 있다.
//
// 색은 셰이더를 따로 쓰지 않고 "반지름 -> 색" 그라데이션을 256x1 텍스처로 구워서 입힌다.
// 메시의 UV.x 를 정규화된 반지름(0 = 태양 중심, 1 = 원판 바깥 끝)으로 깔아두었기 때문에
// 그라데이션 한 줄이 곧 원판의 단면이 된다.
[ExecuteAlways]
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class ZoneDiscRenderer : MonoBehaviour
{
    public enum DiscBlendMode { Alpha, Additive }

    [Header("반지름 (태양 로컬 단위)")]
    [Tooltip("원판 메시가 시작하는 반지름. 태양 표면(0.5)보다 안쪽에서 시작해야 " +
             "태양에 가려진 채로 서서히 나타난다. 0 으로 두면 중심에 겹친 정점이 생기므로 조금 띄운다.")]
    [SerializeField] private float innerRadius = 0.1f;
    [Tooltip("마지막 단계 바깥으로 얼마나 더 그릴지(단계 단위). 0.5 면 D9.5 까지 그린다.")]
    [SerializeField] private float outerMarginStep = 0.5f;

    [Header("태양 경계 블렌딩")]
    [Tooltip("태양 표면의 반지름. 태양은 반지름 0.5 인 구를 스케일한 것이라 보통 0.5.")]
    [SerializeField] private float sunSurfaceRadius = 0.5f;
    [Tooltip("태양 표면에서 원판이 서서히 진해지는 폭(태양 로컬 단위). " +
             "0 이면 태양 실루엣에서 원판이 딱 끊겨 경계선이 도드라진다.")]
    [SerializeField] private float sunBlendWidth = 0.5f;

    [Header("메시")]
    [SerializeField, Range(24, 512)] private int segments = 128;
    [SerializeField, Range(1, 32)] private int radialSegments = 4;
    // 원판을 지구 아래로 살짝 내리는 이유:
    // 지구는 queue 2450, 원판은 2990 이라 원판이 나중에 그려지고 ZWrite 도 꺼져 있다.
    // 둘 다 로컬 y=0 이면 원판이 지구 앞쪽 절반 픽셀 위에 덧칠돼 지구가 반반으로 갈려 보인다.
    // 지구 반지름보다 조금 더 내려 관통 자체를 없앤다.
    [Tooltip("궤도면에서 원판을 얼마나 내릴지(태양 로컬 단위). 음수가 아래.")]
    [SerializeField] private float localYOffset = -0.045f;
    [Tooltip("켜면 지구 렌더러 크기를 재서 원판이 지구를 관통하지 않을 만큼 자동으로 내린다. " +
             "localYOffset 은 무시된다.")]
    [SerializeField] private bool autoClearEarth = true;
    [Tooltip("지구 표면과 원판 사이에 남길 여유(지구 반지름 배수).")]
    [SerializeField, Range(0f, 1f)] private float earthClearance = 0.25f;
    [SerializeField] private string earthPath = "Actor/Universal Rendering Pipeline Materials/Sun Sphere/Earth";

    [Header("색")]
    [Tooltip("켜두면 골디락스 존 설정에서 그라데이션을 자동으로 만든다. 끄면 아래 gradient 를 손으로 편집한 그대로 사용한다.")]
    [SerializeField] private bool autoBuildGradient = true;
    // 알파를 0.35 근처로 낮게 잡은 이유:
    // 씬의 PostFX 볼륨에 Bloom 이 threshold 0.56 으로 켜져 있어서, 검은 우주 위에 합성된
    // 원판 색이 그보다 밝으면 통째로 블룸에 타서 흰 덩어리로 번진다.
    [Tooltip("태양에 너무 가까운 구간")]
    [SerializeField] private Color hotColor = new Color(1f, 0.24f, 0.14f, 0.33f);
    [Tooltip("생물이 살 수 있는 구간")]
    [SerializeField] private Color habitableColor = new Color(0.25f, 1f, 0.42f, 0.33f);
    [Tooltip("골디락스 존 바깥(너무 추운 구간). 기본은 알파 0 이라 보이지 않는다. 알파만 올리면 언제든 색을 입힐 수 있다.")]
    [SerializeField] private Color outerColor = new Color(0.3f, 0.62f, 1f, 0f);
    [Tooltip("적색->녹색 전이를 색상환(빨강-주황-노랑-연두-초록)을 따라 돌린다. " +
             "끄면 RGB 를 직선으로 섞는데, 그러면 중간이 탁한 올리브색으로 어두워져 " +
             "경계가 한 번 투명해졌다 돌아오는 것처럼 보인다.")]
    [SerializeField] private bool blendThroughHue = true;
    [Tooltip("왼쪽 0 = 태양 중심, 오른쪽 1 = 원판 바깥 끝. autoBuildGradient 가 켜져 있으면 자동으로 덮어쓴다.")]
    [SerializeField] private Gradient gradient = new Gradient();
    [SerializeField, Range(16, 512)] private int gradientResolution = 256;

    [Header("렌더링")]
    [SerializeField] private DiscBlendMode blendMode = DiscBlendMode.Alpha;
    [Tooltip("태양 파티클(Sun FX / Sun Glow)보다 먼저 그려지도록 3000 보다 살짝 낮게 둔다.")]
    [SerializeField] private int renderQueue = 2990;

    private MeshFilter meshFilter;
    private MeshRenderer meshRenderer;
    private Mesh mesh;
    private Material material;
    private Texture2D gradientTexture;
    private bool dirty = true;
    private float planeOffsetY;

    // 마지막으로 만들어진 원판의 바깥 반지름(태양 로컬 단위)
    public float OuterRadius { get; private set; }

    private void OnEnable()
    {
        meshFilter = GetComponent<MeshFilter>();
        meshRenderer = GetComponent<MeshRenderer>();
        dirty = true;

        // 태양 세기가 바뀌면 존 범위가 통째로 이동하므로 원판을 다시 굽는다.
        var model = ResolveModel();
        if (model != null) model.OnSunLevelChanged += OnSunLevelChanged;
    }

    private void OnDisable()
    {
        var model = GoldilocksZoneModel.Instance;
        if (model != null) model.OnSunLevelChanged -= OnSunLevelChanged;
    }

    private void OnSunLevelChanged(int level)
    {
        dirty = true;
    }

    private void OnValidate()
    {
        // OnValidate 안에서는 오브젝트 생성이 제한되므로 플래그만 세우고 Update 에서 처리한다.
        dirty = true;
    }

    private void Update()
    {
        if (!dirty) return;
        dirty = false;
        Rebuild();
    }

    [ContextMenu("원판 다시 만들기")]
    public void Rebuild()
    {
        var model = ResolveModel();
        if (model == null)
        {
            Debug.LogWarning("[Goldilocks] GoldilocksZoneModel 을 찾지 못해 원판을 만들 수 없습니다.");
            return;
        }

        model.EnsureInitialized();
        if (Mathf.Approximately(model.MinRadius, model.MaxRadius)) return;

        OuterRadius = model.RadiusAt(model.StepCount + outerMarginStep);
        if (OuterRadius <= innerRadius) return;

        if (autoBuildGradient)
            gradient = BuildGradient(model, OuterRadius);

        planeOffsetY = autoClearEarth ? -EarthClearOffset() : localYOffset;

        BuildMesh(innerRadius, OuterRadius);
        BuildTexture();
        BuildMaterial();
    }

    // 지구 렌더러의 월드 크기를 태양 로컬 단위로 환산해 반지름을 구한다.
    // (지구 모델은 정확한 단위 구가 아니라서 스케일값만으로는 반지름을 알 수 없다.)
    private float EarthClearOffset()
    {
        var go = GameObject.Find(earthPath);
        if (go == null) return Mathf.Abs(localYOffset);

        float worldRadius = 0f;
        foreach (var r in go.GetComponentsInChildren<Renderer>())
        {
            // 지구를 감싼 글로우 파티클까지 포함하면 원판이 과하게 내려간다.
            if (r is ParticleSystemRenderer) continue;
            worldRadius = Mathf.Max(worldRadius, r.bounds.extents.y);
        }
        if (worldRadius <= 0f) return Mathf.Abs(localYOffset);

        float scale = transform.lossyScale.y;
        if (Mathf.Approximately(scale, 0f)) return Mathf.Abs(localYOffset);

        return worldRadius / scale * (1f + earthClearance);
    }

    private GoldilocksZoneModel ResolveModel()
    {
        // 에디터에서는 Awake 가 돌지 않아 Instance 가 비어 있으므로 씬에서 직접 찾는다.
        return GoldilocksZoneModel.Instance != null
            ? GoldilocksZoneModel.Instance
            : FindObjectOfType<GoldilocksZoneModel>();
    }

    // 존 경계에서 fadeWidthStep 만큼 "바깥쪽으로" 색이 섞이게 만든다.
    // 존이 D4~D6 이고 폭이 1.0 이면 D3.5~D4.5 에서 적색->녹색, D6.5~D7.5 에서 녹색->바깥색.
    private Gradient BuildGradient(GoldilocksZoneModel model, float outer)
    {
        float fade = Mathf.Max(0f, model.Config.fadeWidthStep);
        float innerBoundary = model.ZoneInnerBoundaryStep;   // 예) 3.5
        float outerBoundary = model.ZoneOuterBoundaryStep;   // 예) 6.5

        // 키가 같은 위치에 겹치면 그라데이션이 뭉개지므로 최소 간격을 준다.
        const float minGap = 0.0005f;
        float uHotEnd = U(model, innerBoundary, outer);
        float uHabStart = Mathf.Max(U(model, innerBoundary + fade, outer), uHotEnd + minGap);
        float uHabEnd = Mathf.Max(U(model, outerBoundary, outer), uHabStart + minGap);
        float uOuterStart = Mathf.Max(U(model, outerBoundary + fade, outer), uHabEnd + minGap);

        // outerColor 가 투명이면 RGB 를 섞지 않고 초록이 그대로 사라지게 한다.
        // 그러지 않으면 알파가 0 인데도 페이드 중간 구간에 outerColor 의 색조가 비쳐
        // 원판 바깥에 엉뚱한 테두리가 생긴다. 알파를 올리는 순간부터는 지정한 색을 그대로 쓴다.
        Color outerRamp = outerColor.a <= 0f
            ? new Color(habitableColor.r, habitableColor.g, habitableColor.b, 0f)
            : outerColor;

        var colorKeys = new System.Collections.Generic.List<GradientColorKey>
        {
            new GradientColorKey(hotColor, 0f),
            new GradientColorKey(hotColor, uHotEnd),
        };
        var alphaKeys = new System.Collections.Generic.List<GradientAlphaKey>
        {
            new GradientAlphaKey(hotColor.a, 0f),
            new GradientAlphaKey(hotColor.a, uHotEnd),
        };

        // Gradient 는 키 사이를 RGB 직선으로 섞기 때문에, 적색과 녹색을 바로 이으면
        // 중간이 채도/명도가 함께 떨어진 탁한 올리브색이 된다.
        // 색상환을 따라간 중간색을 미리 찍어두면 빨강-주황-노랑-연두-초록으로 넘어간다.
        // (Gradient 의 키 상한이 8개라 중간점은 2개까지만 넣는다.)
        if (blendThroughHue && uHabStart - uHotEnd > 0.002f)
        {
            for (int i = 1; i <= 2; i++)
            {
                float t = i / 3f;
                float u = Mathf.Lerp(uHotEnd, uHabStart, t);
                Color mid = HueLerp(hotColor, habitableColor, t);
                colorKeys.Add(new GradientColorKey(mid, u));
                alphaKeys.Add(new GradientAlphaKey(mid.a, u));
            }
        }

        colorKeys.Add(new GradientColorKey(habitableColor, uHabStart));
        colorKeys.Add(new GradientColorKey(habitableColor, uHabEnd));
        colorKeys.Add(new GradientColorKey(outerRamp, uOuterStart));
        colorKeys.Add(new GradientColorKey(outerRamp, 1f));

        alphaKeys.Add(new GradientAlphaKey(habitableColor.a, uHabStart));
        alphaKeys.Add(new GradientAlphaKey(habitableColor.a, uHabEnd));
        alphaKeys.Add(new GradientAlphaKey(outerColor.a, uOuterStart));
        alphaKeys.Add(new GradientAlphaKey(outerColor.a, 1f));

        var g = new Gradient();
        g.mode = GradientMode.Blend;
        g.SetKeys(colorKeys.ToArray(), alphaKeys.ToArray());
        return g;
    }

    // 색상환의 짧은 쪽 호를 따라 섞는다. 채도와 명도는 따로 선형 보간하므로
    // 전이 구간에서도 원래 두 색만큼 선명하게 유지된다.
    private static Color HueLerp(Color a, Color b, float t)
    {
        float ha, sa, va, hb, sb, vb;
        Color.RGBToHSV(a, out ha, out sa, out va);
        Color.RGBToHSV(b, out hb, out sb, out vb);

        float delta = Mathf.Repeat(hb - ha + 0.5f, 1f) - 0.5f;
        float h = Mathf.Repeat(ha + delta * t, 1f);

        var c = Color.HSVToRGB(h, Mathf.Lerp(sa, sb, t), Mathf.Lerp(va, vb, t));
        c.a = Mathf.Lerp(a.a, b.a, t);
        return c;
    }

    private static float U(GoldilocksZoneModel model, float step, float outer)
    {
        return Mathf.Clamp01(model.RadiusAt(step) / outer);
    }

    private void BuildMesh(float inner, float outer)
    {
        int seg = Mathf.Max(24, segments);
        int rad = Mathf.Max(1, radialSegments);

        int ringVerts = seg + 1;                       // 이음매에서 UV 가 끊기지 않도록 한 바퀴 + 1
        var verts = new Vector3[ringVerts * (rad + 1)];
        var uvs = new Vector2[verts.Length];
        var normals = new Vector3[verts.Length];
        var tris = new int[seg * rad * 6];

        for (int r = 0; r <= rad; r++)
        {
            float radius = Mathf.Lerp(inner, outer, (float)r / rad);
            float u = radius / outer;                  // 0 = 태양 중심, 1 = 원판 바깥 끝
            for (int s = 0; s <= seg; s++)
            {
                float angle = (float)s / seg * Mathf.PI * 2f;
                int i = r * ringVerts + s;
                verts[i] = new Vector3(Mathf.Cos(angle) * radius, planeOffsetY, Mathf.Sin(angle) * radius);
                uvs[i] = new Vector2(u, 0.5f);
                normals[i] = Vector3.up;
            }
        }

        int t = 0;
        for (int r = 0; r < rad; r++)
        {
            for (int s = 0; s < seg; s++)
            {
                int a = r * ringVerts + s;
                int b = a + 1;
                int c = a + ringVerts;
                int d = c + 1;
                tris[t++] = a; tris[t++] = c; tris[t++] = b;
                tris[t++] = b; tris[t++] = c; tris[t++] = d;
            }
        }

        if (mesh == null)
            mesh = new Mesh { name = "GoldilocksZoneDisc", hideFlags = HideFlags.DontSave };

        mesh.Clear();
        mesh.indexFormat = verts.Length > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        mesh.vertices = verts;
        mesh.uv = uvs;
        mesh.normals = normals;
        mesh.triangles = tris;
        mesh.RecalculateBounds();

        meshFilter.sharedMesh = mesh;
    }

    private void BuildTexture()
    {
        int res = Mathf.Clamp(gradientResolution, 16, 512);

        if (gradientTexture == null || gradientTexture.width != res)
        {
            if (gradientTexture != null) SafeDestroy(gradientTexture);
            gradientTexture = new Texture2D(res, 1, TextureFormat.RGBA32, false)
            {
                name = "GoldilocksZoneGradient",
                wrapMode = TextureWrapMode.Clamp,   // 양 끝 색이 그대로 물리도록
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.DontSave,
            };
        }

        // 태양 경계 페이드는 Gradient 키로 넣지 않고 여기서 알파에 곱한다.
        // Gradient 는 색 키가 8개까지인데 존 색과 색상환 중간점으로 이미 다 차 있고,
        // 이건 "존 색"이 아니라 태양에 가려 보이지 않는 구간을 지우는 마스크에 가깝다.
        float uSunStart = sunSurfaceRadius / OuterRadius;
        float uSunEnd = (sunSurfaceRadius + sunBlendWidth) / OuterRadius;
        bool blendSun = sunBlendWidth > 0f && uSunEnd > uSunStart;

        var pixels = new Color[res];
        for (int i = 0; i < res; i++)
        {
            float u = (float)i / (res - 1);
            Color c = gradient.Evaluate(u);
            if (blendSun)
                c.a *= Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(uSunStart, uSunEnd, u));
            pixels[i] = c;
        }

        gradientTexture.SetPixels(pixels);
        gradientTexture.Apply(false);
    }

    private void BuildMaterial()
    {
        if (material == null)
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Unlit/Transparent");
            if (shader == null)
            {
                Debug.LogError("[Goldilocks] 원판용 Unlit 셰이더를 찾지 못했습니다.");
                return;
            }
            material = new Material(shader) { name = "GoldilocksZoneDisc", hideFlags = HideFlags.DontSave };
        }

        bool additive = blendMode == DiscBlendMode.Additive;

        material.SetTexture("_BaseMap", gradientTexture);
        material.SetColor("_BaseColor", Color.white);
        material.SetOverrideTag("RenderType", "Transparent");
        material.SetFloat("_Surface", 1f);                       // Transparent
        material.SetFloat("_Blend", additive ? 1f : 0f);
        material.SetFloat("_AlphaClip", 0f);
        material.SetFloat("_ZWrite", 0f);                        // 자기들끼리 깊이를 가리지 않게
        material.SetFloat("_Cull", (float)CullMode.Off);         // 아래에서 올려다봐도 보이게
        material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
        material.SetInt("_DstBlend", (int)(additive ? BlendMode.One : BlendMode.OneMinusSrcAlpha));
        material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        material.DisableKeyword("_ALPHATEST_ON");
        material.renderQueue = renderQueue;

        meshRenderer.sharedMaterial = material;
        meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
        meshRenderer.receiveShadows = false;
        meshRenderer.lightProbeUsage = LightProbeUsage.Off;
        meshRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
    }

    private void OnDestroy()
    {
        SafeDestroy(mesh);
        SafeDestroy(material);
        SafeDestroy(gradientTexture);
    }

    private static void SafeDestroy(Object obj)
    {
        if (obj == null) return;
        if (Application.isPlaying) Destroy(obj);
        else DestroyImmediate(obj);
    }
}
