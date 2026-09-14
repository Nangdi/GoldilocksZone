using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

// 태양을 중심으로 하는 원판. 태양에 가까운 쪽은 적색, 골디락스 존은 초록으로 칠한다.
//
// 이 오브젝트는 Sun Sphere 의 자식으로 두고 localPosition 0 / localRotation 단위 / localScale 1 을 유지해야 한다.
// 그래야 태양 스케일(123.75)을 그대로 상속받아 min/max 의 로컬 반지름 값을 좌표로 쓸 수 있다.
//
// 원판 평면은 지구 중심 높이에 맞추고, 지구는 원판보다 나중에 그린다(renderQueue + 1).
// 평면을 지구 아래로 피하면 카메라 각도 때문에 지구가 다른 칸 위에 있는 것처럼 보이고,
// 평면만 맞추고 순서를 그대로 두면 지구 아랫절반이 원판 색으로 물든다. 둘 다 필요하다.
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
    [Tooltip("마지막 단계 바깥으로 얼마나 더 그릴지(단계 단위). 0.5 면 D9.5 까지 그린다. " +
             "outerFadeWidthStep 이 여기까지 닿지 못하면 원판 끝에서 색이 딱 끊긴다.")]
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
    // 원판 평면을 지구 아래로 내려서는 안 된다.
    // 카메라가 궤도면을 비스듬히 내려다보기 때문에, 평면이 지구보다 낮으면 시차가 생겨
    // 지구가 화면상 원판의 엉뚱한 반지름 위에 겹쳐 보인다. 실측으로 0.77칸이나 밀렸다.
    // 그래서 평면은 지구 중심에 정확히 맞추고, 겹침은 drawEarthAboveDisc 로 해결한다.
    [Tooltip("alignPlaneToEarth 가 꺼져 있을 때 쓰는 원판의 로컬 Y. 음수가 아래. 태양 중심 기준.")]
    [SerializeField] private float localYOffset = -0.04f;
    [Tooltip("켜면 원판 평면을 지구 중심 높이에 정확히 맞춘다. 지구가 선 칸의 색이 곧 그 칸의 판정색이 된다. " +
             "localYOffset 은 무시된다.")]
    [FormerlySerializedAs("autoClearEarth")]
    [SerializeField] private bool alignPlaneToEarth = true;
    [Tooltip("켜면 지구를 원판보다 나중에 그려서 원판 색이 지구 위에 덧칠되지 않게 한다. " +
             "평면이 지구 중심을 지나므로 이걸 끄면 지구 아랫절반이 원판 색으로 물든다.")]
    [SerializeField] private bool drawEarthAboveDisc = true;
    [SerializeField] private string earthPath = "Actor/Universal Rendering Pipeline Materials/Sun Sphere/Earth";

    [Header("바깥 경계 블렌딩")]
    [Tooltip("존 바깥색(outerColor)이 우주로 서서히 사라지는 폭(단계 단위). " +
             "존 바깥 경계에서 시작해 이만큼 지나면 완전히 투명해진다. 0 이면 원판 끝까지 같은 농도로 칠한다.")]
    [SerializeField] private float outerFadeWidthStep = 2f;

    [Header("색")]
    [Tooltip("켜두면 골디락스 존 설정에서 그라데이션을 자동으로 만든다. 끄면 아래 gradient 를 손으로 편집한 그대로 사용한다.")]
    [SerializeField] private bool autoBuildGradient = true;
    // 색을 어둡고 탁하게, 알파는 0.5 근처로 잡은 이유:
    // 대기영상(video/idle)의 원판이 이런 톤이다. 적갈색 / 짙은 녹색 / 남색이 별이 비칠 만큼만
    // 깔리고, 경계는 궤도 한 칸 폭으로 넓게 번진다. 체험씬도 같은 인상을 주도록 맞춘다.
    // (처음엔 대기영상 픽셀값에 맞춰 0.6 이었는데 실제로 보니 원판이 조금 무거워서 0.5 로 내렸다.)
    // 씬의 PostFX 볼륨에 Bloom 이 threshold 0.56 으로 켜져 있어서, 합성된 원판 색(색 x 알파)이
    // 그보다 밝으면 통째로 블룸에 타서 흰 덩어리로 번진다. 색 자체가 어두워 여유가 있다.
    [Tooltip("태양에 너무 가까운 구간")]
    [SerializeField] private Color hotColor = new Color(0.38f, 0.11f, 0.12f, 0.5f);
    [Tooltip("생물이 살 수 있는 구간")]
    [SerializeField] private Color habitableColor = new Color(0.02f, 0.33f, 0.15f, 0.5f);
    [Tooltip("골디락스 존 바깥(너무 추운 구간). 알파를 0 으로 두면 초록 밖이 바로 투명해진다.")]
    [SerializeField] private Color outerColor = new Color(0.01f, 0.1f, 0.3f, 0.5f);
    // 기본은 끈다. 색상환을 따라 돌리면 중간에 노란 띠가 생겨 적색과 녹색이 거기서
    // 딱 나뉘어 보인다. 끄면 RGB 직선 보간인데, 예전에 그 중간이 어둡게 꺼져 보였던 건
    // 색이 아니라 sRGB(감마) 값끼리 섞어서 생긴 명도 꺼짐이었다. 지금은 BuildTexture 가
    // 선형광(linear) 공간에서 섞으므로 중간 명도가 양끝의 평균으로 유지되어
    // 녹색->남색 경계처럼 자연스럽게 이어진다. 자세한 건 EvaluateLinear 주석 참고.
    [Tooltip("적색->녹색 전이를 색상환(빨강-주황-노랑-연두-초록)을 따라 돌린다. " +
             "켜면 중간에 노란 띠가 생겨 경계가 또렷해지고, 끄면 RGB 를 직선으로 섞어 " +
             "녹색->바깥색 경계처럼 부드럽게 이어진다.")]
    [SerializeField] private bool blendThroughHue = false;
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
    // 바깥색이 완전히 칠해지기 시작하는 정규화 반지름(존 바깥 경계 + 전이 반폭). BuildTexture 의 바깥 페이드 기준점.
    private float uOuterFull = 1f;
    // 바깥색이 완전히 사라지는 정규화 반지름. 음수면 바깥 페이드를 쓰지 않는다.
    private float outerFadeU = -1f;

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

        uOuterFull = U(model, model.ZoneOuterBoundaryStep + Mathf.Max(0f, model.Config.fadeWidthStep) * 0.5f, OuterRadius);
        outerFadeU = outerFadeWidthStep > 0f
            ? U(model, model.ZoneOuterBoundaryStep + Mathf.Max(0f, model.Config.fadeWidthStep) * 0.5f + outerFadeWidthStep, OuterRadius)
            : -1f;

        planeOffsetY = ResolvePlaneOffsetY();

        BuildMesh(innerRadius, OuterRadius);
        BuildTexture();
        BuildMaterial();
        ApplyEarthDrawOrder();
    }

    // 원판 평면을 놓을 로컬 Y 를 정한다. 기준은 "태양 중심"이 아니라 "지구 중심"이다.
    //
    // 지구는 로컬 y=0 이 아니라 살짝 내려간 곳(현재 -0.04)에 있다. 태양 중심을 기준으로
    // 평면을 놓으면 지구와 높이가 어긋나고, 카메라가 궤도면을 비스듬히 내려다보는 탓에
    // 그 높이차가 그대로 화면상 반지름 오차로 바뀐다. 지구를 반지름만큼 피해 내렸을 때
    // 실측 오차가 0.77칸이었다. 그래서 높이는 무조건 지구 중심에 맞춘다.
    private float ResolvePlaneOffsetY()
    {
        if (!alignPlaneToEarth) return localYOffset;

        var go = GameObject.Find(earthPath);
        if (go == null) return localYOffset;

        var bounds = new Bounds();
        bool found = false;
        foreach (var r in go.GetComponentsInChildren<Renderer>())
        {
            // 글로우 파티클과 2D 스프라이트는 중심을 흐트러뜨리므로 본체 메시만 본다.
            if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) continue;
            if (!found) { bounds = r.bounds; found = true; }
            else bounds.Encapsulate(r.bounds);
        }
        if (!found) return localYOffset;

        // 원판은 localPosition 0 / 회전 없음 / 스케일 1 로 두기 때문에
        // 이 로컬 공간은 태양 로컬 공간과 같다. 지구가 어디 매달려 있든
        // 월드 중심을 다시 들여보므로 계층 구조가 바뀌어도 그대로 동작한다.
        return transform.InverseTransformPoint(bounds.center).y;
    }

    // 지구를 원판보다 나중에 그리게 한다.
    //
    // 평면이 지구 중심을 지나므로 지구의 아래쪽 절반은 물리적으로 평면보다 뒤에 있다.
    // 원판은 나중에 그려지고 ZWrite 가 꺼져 있어서, 깊이 테스트가 정상 동작한 결과가
    // 곧 "지구 아랫절반이 원판 색으로 덮이는" 현상이 된다. 평면을 옮겨 피하면 위의
    // 시차 문제가 되살아나므로, 대신 불투명한 지구를 원판 뒤에 그려 덮어버린다.
    //
    // 지구는 큐만 옮길 뿐 여전히 깊이를 쓰므로 태양 뒤로 돌아가면 정상적으로 가려진다.
    // 지구 글로우 파티클(3000)은 건드리지 않아 지금처럼 지구 위에 남는다.
    private void ApplyEarthDrawOrder()
    {
        if (!drawEarthAboveDisc) return;

        var go = GameObject.Find(earthPath);
        if (go == null) return;

        int target = renderQueue + 1;
        foreach (var r in go.GetComponentsInChildren<Renderer>())
        {
            // 행성 본체 메시만 대상으로 한다.
            // 글로우 파티클은 지구 위에 남아야 하고, PlanetSpriteVisual 이 만드는
            // SpriteRenderer 는 유니티 공용 머티리얼(Sprites-Default)을 쓰기 때문에
            // 여기서 큐를 건드리면 프로젝트의 모든 스프라이트에 영향이 간다.
            if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) continue;

            foreach (var m in r.sharedMaterials)
            {
                // 같은 값을 다시 쓰면 에디터에서 머티리얼이 매번 더티가 되므로 달라질 때만 건드린다.
                if (m != null && m.renderQueue != target) m.renderQueue = target;
            }
        }
    }

    private GoldilocksZoneModel ResolveModel()
    {
        // 에디터에서는 Awake 가 돌지 않아 Instance 가 비어 있으므로 씬에서 직접 찾는다.
        return GoldilocksZoneModel.Instance != null
            ? GoldilocksZoneModel.Instance
            : FindObjectOfType<GoldilocksZoneModel>();
    }

    // 존 경계를 가운데에 두고 fadeWidthStep 을 안팔/바깥으로 반씩 나눠 섞는다.
    // 경계는 칸과 칸 사이(D2.5 같은 반칸 지점)이므로, 이렇게 해야 칸 중심에 선 지구가
    // 전이 구간이 아닌 단색 위에 올라간다. 존이 D3~D5 이고 폭이 0.4 면
    // D2.3~D2.7 에서 적색->녹색, D5.3~D5.7 에서 녹색->바깥색이라
    // D2 는 순수한 빨강, D3~D5 는 순수한 초록, D6 부터는 바깥색이 된다.
    //
    // 전이 폭의 상한은 지구 크기가 정한다. 지구 반지름이 0.264칸이므로
    // 반폭(fade/2)이 0.5 - 0.264 = 0.236 칸을 넘으면 칸 중심의 지구도 전이 구간을 물기 시작한다.
    private Gradient BuildGradient(GoldilocksZoneModel model, float outer)
    {
        float fade = Mathf.Max(0f, model.Config.fadeWidthStep);
        float half = fade * 0.5f;
        float innerBoundary = model.ZoneInnerBoundaryStep;   // 예) 2.5
        float outerBoundary = model.ZoneOuterBoundaryStep;   // 예) 5.5

        // 키가 같은 위치에 겹치면 그라데이션이 뭉개지므로 최소 간격을 준다.
        const float minGap = 0.0005f;
        float uHotEnd = U(model, innerBoundary - half, outer);
        float uHabStart = Mathf.Max(U(model, innerBoundary + half, outer), uHotEnd + minGap);
        float uHabEnd = Mathf.Max(U(model, outerBoundary - half, outer), uHabStart + minGap);
        float uOuterStart = Mathf.Max(U(model, outerBoundary + half, outer), uHabEnd + minGap);

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

        // 바깥 페이드도 같은 이유로 마스크로 곱한다. 존 바깥 경계에서 outerFadeWidthStep 만큼
        // 지나는 동안 outerColor 가 우주로 녹아들어 원판 끝이 어디인지 보이지 않게 한다.
        bool fadeOuter = outerFadeU > uOuterFull;

        var pixels = new Color[res];
        for (int i = 0; i < res; i++)
        {
            float u = (float)i / (res - 1);
            Color c = EvaluateLinear(gradient, u);
            if (blendSun)
                c.a *= Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(uSunStart, uSunEnd, u));
            if (fadeOuter)
                c.a *= 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(uOuterFull, outerFadeU, u));
            pixels[i] = c;
        }

        gradientTexture.SetPixels(pixels);
        gradientTexture.Apply(false);
    }

    // Gradient.Evaluate 대신 색 키 사이를 선형광(linear) 공간에서 섞는다.
    //
    // 프로젝트는 Linear 색공간이고 인스펙터의 Color 값은 sRGB 다. Gradient 는 그 sRGB 값을
    // 그대로 직선 보간하는데, 감마 곡선 위에서 섞으면 중간 명도가 양끝 평균보다 푹 꺼진다.
    // 빨강(0.38,0.11,0.12)->초록(0.02,0.33,0.15)처럼 채널이 서로 반대인 색끼리는 이 꺼짐이
    // 심해서, 전이 구간이 우주 배경에 묻혀 마치 색이 비어 있는 것처럼 보였다.
    // 초록->남색은 G/B 채널이 겹쳐서 꺼짐이 작아 티가 안 났을 뿐 원리는 같다.
    //
    // 키를 .linear 로 바꿔 섞고 다시 .gamma 로 되돌리면 sRGB 텍스처에 넣어도
    // 셰이더가 보는 결과는 선형광에서 섞은 것과 같다. 알파는 감마 보정 대상이 아니므로
    // Gradient 의 알파 키 보간을 그대로 쓴다.
    private static Color EvaluateLinear(Gradient g, float u)
    {
        var keys = g.colorKeys;
        float alpha = g.Evaluate(u).a;

        if (keys.Length == 0) return new Color(0f, 0f, 0f, alpha);
        if (keys.Length == 1 || u <= keys[0].time) return WithAlpha(keys[0].color, alpha);
        if (u >= keys[keys.Length - 1].time) return WithAlpha(keys[keys.Length - 1].color, alpha);

        int hi = 1;
        while (hi < keys.Length - 1 && keys[hi].time < u) hi++;
        var a = keys[hi - 1];
        var b = keys[hi];

        // 키가 같은 위치에 겹치면 InverseLerp 가 0 을 돌려 앞 키 색이 쓰인다.
        float t = Mathf.InverseLerp(a.time, b.time, u);
        Color mixed = Color.Lerp(a.color.linear, b.color.linear, t).gamma;
        return WithAlpha(mixed, alpha);
    }

    private static Color WithAlpha(Color c, float a)
    {
        c.a = a;
        return c;
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
