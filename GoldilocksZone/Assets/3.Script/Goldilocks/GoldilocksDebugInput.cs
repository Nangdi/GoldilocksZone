using UnityEngine;

// RS232 장비 없이 신호를 키보드로 넣어보기 위한 개발용 입력.
//   1~9 (숫자열/키패드)       : D1~D9  거리 단계
//   Shift + 1~3               : M1~M3  태양 세기 (숫자가 M 번호와 그대로 대응)
//   Z / X / C                 : M1~M3  태양 세기 (한 손으로 누르는 대체 키)
//   Q                         : 랜덤 단계
// 모두 실제 RS232 수신과 같은 경로(SetStep / SetSunLevel)를 탄다.
// 현장 배포 시에는 이 오브젝트를 꺼두면 된다.
// (GameManager 가 S/E 키를 쓰고 있어 겹치지 않는다.)
public class GoldilocksDebugInput : MonoBehaviour
{
    [SerializeField] private bool enableKeyboard = true;

    [Header("랜덤 수신")]
    [SerializeField] private KeyCode randomKey = KeyCode.Q;
    [Tooltip("현재 단계와 같은 값이 뽑히지 않게 한다. 껐을 때 같은 값이 나오면 " +
             "SetStep 이 무시하므로 눌러도 아무 일이 없는 것처럼 보인다.")]
    [SerializeField] private bool avoidSameStep = true;

    [Header("태양 세기 (M1~M3)")]
    [Tooltip("Shift 를 누른 채 숫자키를 누르면 그 번호의 태양 세기로 바꾼다. " +
             "Shift 없이 누르면 거리 단계(D)로 동작한다.")]
    [SerializeField] private bool shiftNumberSetsSunLevel = true;
    [Tooltip("Shift 조합 대신 쓸 수 있는 단축키. 순서대로 세기 1, 2, 3 에 대응한다.")]
    [SerializeField] private KeyCode[] sunLevelKeys = { KeyCode.Z, KeyCode.X, KeyCode.C };

    private void Update()
    {
        if (!enableKeyboard) return;

        var model = GoldilocksZoneModel.Instance;
        if (model == null) return;

        if (Input.GetKeyDown(randomKey))
        {
            model.SetStep(PickRandomStep(model));
            return;
        }

        // 태양 세기 1~3. 단계(D)는 그대로 두고 골디락스 존 범위만 이동한다.
        for (int i = 0; i < sunLevelKeys.Length; i++)
        {
            if (Input.GetKeyDown(sunLevelKeys[i]))
            {
                model.SetSunLevel(i + 1);
                return;
            }
        }

        // Shift + 숫자를 태양 세기로 쓰면 M 번호와 숫자가 그대로 대응돼 외우기 쉽다.
        // Shift 를 누른 동안에는 거리 단계로 새지 않도록 아래 D 처리를 건너뛴다.
        bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        if (shiftNumberSetsSunLevel && shift)
        {
            int levels = model.SunLevelCount;
            for (int level = 1; level <= levels && level <= 9; level++)
            {
                if (Input.GetKeyDown(KeyCode.Alpha0 + level) || Input.GetKeyDown(KeyCode.Keypad0 + level))
                {
                    model.SetSunLevel(level);
                    return;
                }
            }
            return;
        }

        for (int step = 1; step <= 9; step++)
        {
            if (Input.GetKeyDown(KeyCode.Alpha0 + step) || Input.GetKeyDown(KeyCode.Keypad0 + step))
            {
                model.SetStep(step);
                return;
            }
        }
    }

    private int PickRandomStep(GoldilocksZoneModel model)
    {
        int count = model.StepCount;

        if (!avoidSameStep || count < 2)
            return Random.Range(1, count + 1);

        // 현재 단계를 뺀 나머지 중에서 고른다.
        // 1..count-1 에서 뽑은 뒤 현재 단계 이상이면 하나 밀어 현재 값을 건너뛴다.
        int step = Random.Range(1, count);
        if (step >= model.CurrentStep) step++;
        return step;
    }
}
