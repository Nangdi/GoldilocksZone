using System.Text.RegularExpressions;
using UnityEngine;

// 골디락스존용 RS232 수신부.
//
//   D1~D9 : 태양-지구 거리 단계. 체험자가 실물 지구 모형을 밀면 순차적으로 들어온다.
//   M1~M3 : 태양 세기. 세기가 올라갈수록 골디락스 존이 바깥으로 밀려난다.
//
// SerialPortManager.ReceivedData 가 상속을 전제로 protected virtual 로 열려 있어 그 규약을 따른다.
// 씬의 SerialPortManager 컴포넌트를 이 클래스로 바꿔 달면 된다.
public class GoldilocksSerialManager : SerialPortManager
{
    // 한 번에 여러 메시지가 몰려 들어와도 최신값만 반영되도록 "마지막 매치"를 취한다.
    private static readonly Regex StepPattern = new Regex("[Dd]([1-9])");
    private static readonly Regex SunLevelPattern = new Regex("[Mm]([1-9])");

    [Header("디버그")]
    [Tooltip("수신한 문자열에서 해석한 값을 콘솔에 남긴다.")]
    [SerializeField] private bool verboseLog = true;

    // SerialPortChannel 의 콜백은 Task 연속 실행에서 올라오므로 Unity 메인 스레드라는 보장이 없다.
    // 값만 받아두고 실제 반영은 Update 에서 한다.
    private readonly object gate = new object();
    private int pendingStep = -1;
    private int pendingSunLevel = -1;

    protected override void ReceivedData(int controllerId, string data)
    {
        var model = GoldilocksZoneModel.Instance;
        if (model == null || string.IsNullOrEmpty(data)) return;
        if (controllerId != model.Config.controllerId) return;

        int step, level;
        bool gotStep = TryParseLast(StepPattern, data, out step);
        bool gotLevel = TryParseLast(SunLevelPattern, data, out level);

        if (!gotStep && !gotLevel)
        {
            if (verboseLog)
                Debug.LogWarning($"[Goldilocks] 해석하지 못한 수신 데이터: \"{data.Trim()}\"");
            return;
        }

        lock (gate)
        {
            if (gotStep) pendingStep = step;
            if (gotLevel) pendingSunLevel = level;
        }
    }

    private void Update()
    {
        int step, level;
        lock (gate)
        {
            step = pendingStep;
            level = pendingSunLevel;
            pendingStep = -1;
            pendingSunLevel = -1;
        }

        if (step < 0 && level < 0) return;

        var model = GoldilocksZoneModel.Instance;
        if (model == null) return;

        // 세기를 먼저 적용한다. 같은 묶음에 M 과 D 가 함께 왔다면
        // 새 존 범위를 기준으로 단계의 존 판정이 나와야 하기 때문이다.
        if (level >= 0)
        {
            if (verboseLog) Debug.Log($"[Goldilocks] RS232 수신 M{level}");
            model.SetSunLevel(level);
        }
        if (step >= 0)
        {
            if (verboseLog) Debug.Log($"[Goldilocks] RS232 수신 D{step}");
            model.SetStep(step);
        }
    }

    private static bool TryParseLast(Regex pattern, string data, out int value)
    {
        value = -1;
        var matches = pattern.Matches(data);
        if (matches.Count == 0) return false;

        var last = matches[matches.Count - 1];
        return int.TryParse(last.Groups[1].Value, out value);
    }
}
