using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

public class GameSettingData
{
    public bool useUnityOnTop;
    // 마우스 커서 표시 여부. false면 커서를 숨긴다(키오스크/전시용).
    public bool showMouseCursor;
}

public class GameDynamicData
{
}

[Serializable]
public class PortConfig
{
    public int controllerId;
    public string com;
    public int baudLate;
    // RS232 사용 여부. false면 해당 컨트롤러는 포트를 열지 않는다.
    public bool enabled = true;
}

[Serializable]
public class PortJson
{
    // 컨트롤러 1~5 각각 전용 포트
    public List<PortConfig> ports = new List<PortConfig>
    {
        new PortConfig { controllerId = 1, com = "COM4", baudLate = 19200, enabled = true },
        new PortConfig { controllerId = 2, com = "COM5", baudLate = 19200, enabled = true },
        new PortConfig { controllerId = 3, com = "COM6", baudLate = 19200, enabled = true },
        new PortConfig { controllerId = 4, com = "COM7", baudLate = 19200, enabled = true },
        new PortConfig { controllerId = 5, com = "COM8", baudLate = 19200, enabled = true },
    };
}

[Serializable]
public class TcpJson
{
    // 메인 서버 접속 주소와 포트
    public string host = "127.0.0.1";
    public int port = 5000;
    // 끊겼을 때 재시도 간격(초). 0 이하이면 재연결 시도하지 않음.
    public float reconnectIntervalSeconds = 3f;
    // 송신 메시지 끝에 줄바꿈을 자동으로 붙일지 여부
    public bool appendOutgoingLineEnding = true;
}

// 태양 세기 1단계에 해당하는 설정.
// 태양이 밝을수록 생물이 살 수 있는 구간이 바깥으로 밀려난다.
[Serializable]
public class SunLevelConfig
{
    public int level = 1;
    // 이 세기에서의 골디락스 존 범위(양끝 포함)
    public int zoneMinStep = 2;
    public int zoneMaxStep = 4;
    // 태양 Point Light 밝기. 0 이하이면 조명을 건드리지 않는다.
    public float lightIntensity = 0.6f;
    // 태양 글로우 파티클 크기 배율. 0 이하이면 건드리지 않는다.
    public float glowScale = 0.8f;
}

[Serializable]
public class GoldilocksJson
{
    // D 신호를 받을 RS232 컨트롤러 번호 (port.json 의 controllerId 와 매칭된다)
    public int controllerId = 1;

    // 단계 개수. D1~D9 이면 9.
    // min 이 D1, max 가 D9 이므로 사이 간격은 (stepCount - 1) 개다.
    public int stepCount = 9;

    // 시작할 때 지구를 놓아둘 단계
    public int initialStep = 1;

    // 태양 세기 단계별 골디락스 존 범위. 세기가 올라갈수록 존이 바깥으로 밀려난다.
    public List<SunLevelConfig> sunLevels = new List<SunLevelConfig>
    {
        new SunLevelConfig { level = 1, zoneMinStep = 2, zoneMaxStep = 4, lightIntensity = 0.6f, glowScale = 0.8f },
        new SunLevelConfig { level = 2, zoneMinStep = 4, zoneMaxStep = 6, lightIntensity = 1.0f, glowScale = 1.0f },
        new SunLevelConfig { level = 3, zoneMinStep = 6, zoneMaxStep = 8, lightIntensity = 1.6f, glowScale = 1.3f },
    };

    // 시작할 때의 태양 세기 단계
    public int initialSunLevel = 2;

    // sunLevels 가 비어 있거나 현재 단계를 찾지 못할 때 쓰는 예비 존 범위(양끝 포함)
    public int zoneMinStep = 4;
    public int zoneMaxStep = 6;

    // 존 경계 그라데이션 폭(단계 단위). 1.0 이면 D3.5~D4.5 에서 색이 섞인다.
    public float fadeWidthStep = 1.0f;

    // 지구 이동 부드러움. SmoothDamp 시간상수(초). 작을수록 빠르게 따라붙는다.
    public float moveSmoothTime = 0.35f;

    // 목표 도달 판정 오차(태양 로컬 단위). 1단계 폭(약 0.117)의 4% 수준.
    public float arriveEpsilon = 0.005f;

    // 신호가 멈춘 뒤 "정착"으로 판정하기까지의 대기 시간(초).
    // 체험자가 실물 지구를 밀면 D1~D5 가 연속으로 들어오는데, 이 시간 동안
    // 값 변화가 없어야 동영상 재생 같은 후속 동작을 시작한다.
    public float settleDelay = 1.0f;

    // 정착 판정에 지구가 목표 위치에 도착했는지도 함께 볼지 여부
    public bool waitForArrival = true;

    // 직전 정착 단계와 같으면 정착 이벤트를 건너뛸지 여부.
    // false(기본)이면 D1 -> D5 -> D1 처럼 돌아와 멈춰도 이벤트가 다시 나간다.
    public bool ignoreSameAsLastSettled = false;

    // Time.timeScale 영향을 받지 않게 할지 여부.
    // GameManager 가 매 프레임 timeScale 을 덮어쓰므로 기본 true 를 권장한다.
    public bool useUnscaledTime = true;

    // ── 대기 / 인트로 ────────────────────────────────────────────
    // 전시 흐름: 대기영상 -> (D 신호) -> 인트로 멘트 -> 체험 -> 무입력 -> 대기영상

    // 체험이 끝나고 이 시간 동안 신호가 없으면 대기영상으로 돌아간다.
    public float idleTimeoutSeconds = 30f;

    // 대기영상 폴더. StreamingAssets/<videoRootFolder>/<이 이름>/ 안의 파일을 반복 재생한다.
    public string idleVideoFolder = "idle";

    // 인트로 멘트 한 줄이 머무는 시간(초)
    public float introSecondsPerMessage = 3f;

    // 멘트가 바뀔 때 흐려졌다 나타나는 시간(초)
    public float introFadeSeconds = 0.4f;

    // 인트로 멘트. 줄바꿈은 \n 으로 넣는다.
    public List<string> introMessages = new List<string>
    {
        "밤하늘의 수많은 별 중에서, 우리 지구처럼 생명체가 살 수 있는 곳은 어디에 있을까요?",
        "생명이 숨 쉬려면 단순히 따뜻한 것만으로는 부족해요.",
        "온도를 지켜주는 마법의 물질인 '액체 상태의 물',",
        "그리고 그 물이 도망가지 못하게 꽉 잡아주는 '대기'가 꼭 필요하답니다.",
        "자, 이제 직접 지구 모형을 움직여서\n생명체가 살아갈 수 있는 위치인\n골디락스 존을 찾아보세요!",
    };
}

public class JsonManager : MonoBehaviour
{
    public static JsonManager instance;
    public GameSettingData gameSettingData = new GameSettingData();
    public PortJson portJson = new PortJson();
    public TcpJson tcpJson = new TcpJson();
    public GoldilocksJson goldilocksJson = new GoldilocksJson();
    public GameDynamicData gameDynamicData = new GameDynamicData();

    private string gameDataPath;
    private string gameDynamicDataPath;
    private string portPath;
    private string tcpPath;
    private string goldilocksPath;

    public string GameDataPath => gameDataPath;

    // 싱글톤을 초기화하고 JSON 파일에서 런타임 설정 데이터를 불러옵니다.
    private void Awake()
    {
        if (instance == null)
        {
            instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else if (instance != this)
        {
            Destroy(gameObject);
            return;
        }

        portPath = Path.Combine(Application.streamingAssetsPath, "port.json");
        tcpPath = Path.Combine(Application.streamingAssetsPath, "tcp.json");
        goldilocksPath = Path.Combine(Application.streamingAssetsPath, "goldilocks.json");
        gameDynamicDataPath = Path.Combine(Application.streamingAssetsPath, "Setting.json");
        gameDataPath = Path.Combine(Application.persistentDataPath, "gameSettingData.json");

        gameSettingData ??= new GameSettingData();
        gameDynamicData ??= new GameDynamicData();
        portJson ??= new PortJson();
        tcpJson ??= new TcpJson();
        goldilocksJson ??= new GoldilocksJson();

        gameSettingData = LoadData(gameDataPath, gameSettingData);
        gameDynamicData = LoadData(gameDynamicDataPath, gameDynamicData);
        portJson = LoadData(portPath, portJson);
        tcpJson = LoadData(tcpPath, tcpJson);
        goldilocksJson = LoadData(goldilocksPath, goldilocksJson);
    }

    // 현재 게임 설정 데이터를 gameSettingData.json 파일에 저장합니다.
    public void SaveGameSettingData()
    {
        SaveData(gameSettingData, gameDataPath);
    }

    // 지정한 경로에 JSON 파일을 생성하거나 덮어씁니다.
    public static void SaveData<T>(T jsonObject, string path) where T : new()
    {
        if (jsonObject == null)
            jsonObject = new T();

        string directoryPath = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directoryPath) && !Directory.Exists(directoryPath))
            Directory.CreateDirectory(directoryPath);

        string json = JsonUtility.ToJson(jsonObject, true);
        File.WriteAllText(path, json);
        Debug.Log($"Saved JSON: {path}");
    }

    // JSON 파일을 읽고, 파일이 없으면 기본값으로 새 파일을 만듭니다.
    // FromJsonOverwrite를 사용해 json에 없는 필드는 인스턴스 기본값을 유지한다.
    // (예: 기존 port.json에 enabled 필드가 없어도 PortConfig.enabled = true 가 보존됨)
    public static T LoadData<T>(string path, T data) where T : new()
    {
        if (data == null) data = new T();

        if (!File.Exists(path))
        {
            Debug.LogWarning($"JSON file does not exist. Creating a new file: {path}");
            SaveData(data, path);
            return data;
        }

        Debug.Log($"Loaded JSON: {path}");
        string json = File.ReadAllText(path);
        JsonUtility.FromJsonOverwrite(json, data);
        return data;
    }
}
