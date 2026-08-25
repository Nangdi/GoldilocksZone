using UnityEngine;

// 지구를 천천히 자전시킨다.
// 위치는 EarthOrbitPositioner 가 localPosition 만 건드리므로 서로 간섭하지 않는다.
//
// 각도를 누적해서 localRotation 을 매번 새로 만든다.
// Transform.Rotate 로 조금씩 더하면 부동소수 오차가 쌓여 자전축이 서서히 기운다.
public class EarthSpin : MonoBehaviour
{
    [Header("대상 (비워두면 아래 경로로 자동 탐색)")]
    [SerializeField] private Transform target;
    [SerializeField] private string targetPath = "Actor/Universal Rendering Pipeline Materials/Sun Sphere/Earth";

    [Header("자전")]
    [Tooltip("초당 회전 각도. 음수면 반대 방향으로 돈다.")]
    [SerializeField] private float degreesPerSecond = 6f;
    [Tooltip("자전축 기울기(도). 실제 지구는 약 23.4도.")]
    [SerializeField] private float axialTiltDegrees = 23.4f;
    [Tooltip("Time.timeScale 의 영향을 받지 않게 한다. GameManager 가 timeScale 을 덮어쓰므로 기본 켬.")]
    [SerializeField] private bool useUnscaledTime = true;

    private Quaternion tilt = Quaternion.identity;
    private float angle;

    private void Start()
    {
        if (target == null && !string.IsNullOrEmpty(targetPath))
        {
            var go = GameObject.Find(targetPath);
            if (go != null) target = go.transform;
        }

        if (target == null)
        {
            Debug.LogError($"[Goldilocks] 자전시킬 지구를 찾지 못했습니다: {targetPath}");
            enabled = false;
            return;
        }

        tilt = Quaternion.AngleAxis(axialTiltDegrees, Vector3.forward);
        angle = target.localEulerAngles.y;
    }

    private void Update()
    {
        float dt = useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
        angle = Mathf.Repeat(angle + degreesPerSecond * dt, 360f);
        target.localRotation = tilt * Quaternion.AngleAxis(angle, Vector3.up);
    }
}
