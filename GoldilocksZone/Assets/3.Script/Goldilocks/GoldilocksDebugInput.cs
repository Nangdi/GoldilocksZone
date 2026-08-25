using UnityEngine;

// RS232 장비 없이 D1~D9 를 키보드로 넣어보기 위한 개발용 입력.
//   숫자열 1~9 / 키패드 1~9 : 해당 단계로 직접 이동
//   Q                       : 랜덤 단계 수신 (실제 신호가 들어온 것과 동일한 경로)
// 현장 배포 시에는 이 오브젝트를 꺼두면 된다.
// (GameManager 가 S/E 키를 쓰고 있어 숫자키/Q 와는 겹치지 않는다.)
public class GoldilocksDebugInput : MonoBehaviour
{
    [SerializeField] private bool enableKeyboard = true;

    [Header("랜덤 수신")]
    [SerializeField] private KeyCode randomKey = KeyCode.Q;
    [Tooltip("현재 단계와 같은 값이 뽑히지 않게 한다. 껐을 때 같은 값이 나오면 " +
             "SetStep 이 무시하므로 눌러도 아무 일이 없는 것처럼 보인다.")]
    [SerializeField] private bool avoidSameStep = true;

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
