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

    // 골디락스 존 범위(양끝 포함). 예) 4,6 이면 D4~D6 이 생물 거주 가능 구간
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
