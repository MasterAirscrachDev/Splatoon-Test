using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Esc or Select (or G): the pause menu. It's an online game, so play carries on behind it; our
// player just stops taking input. Resume, Settings, Loadout (between matches), Leave lobby, Quit,
// with the Host column beside it (HostMenu: only the host can use it). The settings
// panel sets the look sensitivities (mouse, controller stick, gyro turn and up/down) and gyro
// aiming, saved when it closes. Cancel (B, Esc's other half, right-click) backs out of settings,
// then closes; Pause closes it from anywhere. Opens only when no other menu was open, so the Esc
// that closes one of those doesn't open this too.
public class PauseMenu : MonoBehaviour
{
    [SerializeField] GameObject screen;
    [SerializeField] GameObject mainPanel, settingsPanel;
    [SerializeField] Button resumeButton, settingsButton, loadoutButton, leaveButton, quitButton, backButton;
    [SerializeField] Slider mouseSlider, stickSlider, gyroXSlider, gyroYSlider;
    [SerializeField] TMP_Text mouseValue, stickValue, gyroXValue, gyroYValue;
    [SerializeField] Toggle gyroToggle;

    ControlLayer input;
    HostMenu host;          // the Host column, beside ours
    Vector2 mainAt;         // our column's place when it's alone
    const float ColumnGap = 240f;
    bool open, blockedLastFrame, settingsChanged;
    Vector2 quitAt, leaveAt; // Quit moves up into Leave's place when there's no lobby to leave

    public bool IsOpen => open;                                        // tests
    public bool SettingsOpen => open && settingsPanel.activeSelf;
    public Slider MouseSlider => mouseSlider;
    public Slider StickSlider => stickSlider;
    public Slider GyroXSlider => gyroXSlider;
    public Slider GyroYSlider => gyroYSlider;
    public Toggle GyroToggle => gyroToggle;
    public Button ResumeButton => resumeButton;
    public Button SettingsButton => settingsButton;
    public Button BackButton => backButton;

    void Awake()
    {
        input = new ControlLayer();
        input.GameControl.Enable();
        resumeButton.onClick.AddListener(() => SetOpen(false));
        settingsButton.onClick.AddListener(ShowSettings);
        backButton.onClick.AddListener(ShowMain);
        loadoutButton.onClick.AddListener(OpenLoadout);
        leaveButton.onClick.AddListener(Leave);
        quitButton.onClick.AddListener(Quit);
        quitAt = ((RectTransform)quitButton.transform).anchoredPosition;
        leaveAt = ((RectTransform)leaveButton.transform).anchoredPosition;

        SetUpSlider(mouseSlider, mouseValue, v => GameSettings.MouseSensitivity = v);
        SetUpSlider(stickSlider, stickValue, v => GameSettings.StickSensitivity = v);
        SetUpSlider(gyroXSlider, gyroXValue, v => GameSettings.GyroSensitivityX = v);
        SetUpSlider(gyroYSlider, gyroYValue, v => GameSettings.GyroSensitivityY = v);
        gyroToggle.onValueChanged.AddListener(on => { GameSettings.GyroEnabled = on; Changed(); });
        screen.SetActive(false);
        host = transform.parent != null ? transform.parent.GetComponentInChildren<HostMenu>(true) : null;
        mainAt = ((RectTransform)mainPanel.transform).anchoredPosition;
        NetGameManager.HostMenuToggled += ToggleFromKey;
    }

    void OnDestroy()
    {
        NetGameManager.HostMenuToggled -= ToggleFromKey;
        input?.Disable(); input?.Dispose();
    }

    // G (the old host menu's key) opens this too.
    void ToggleFromKey()
    {
        if (open) SetOpen(false);
        else if (!InputGate.Blocked) SetOpen(true);
    }

    void SetUpSlider(Slider slider, TMP_Text label, System.Action<float> set)
    {
        slider.minValue = GameSettings.MinSensitivity;
        slider.maxValue = GameSettings.MaxSensitivity;
        slider.wholeNumbers = false;
        slider.onValueChanged.AddListener(v =>
        {
            v = Mathf.Round(v * 20f) / 20f; // steps of 0.05
            set(v);
            label.text = $"{v:0.00}×";
            Changed();
        });
    }

    void Changed()
    {
        settingsChanged = true;
        GameSettings.Apply();
    }

    void Update()
    {
        bool pause = input.GameControl.Pause.WasPressedThisFrame();
        if (!open)
        {
            if (pause && !InputGate.Blocked && !blockedLastFrame) SetOpen(true);
            return;
        }
        if (pause) { SetOpen(false); return; }
        if (input.GameControl.Cancel.WasPressedThisFrame())
        {
            if (settingsPanel.activeSelf || host != null && host.SubPanelOpen) ShowMain(); else SetOpen(false);
            return;
        }
        NetGameManager gm = NetGameManager.Instance;
        loadoutButton.interactable = gm == null || !gm.InMatch;
    }

    void LateUpdate() => blockedLastFrame = InputGate.Blocked; // the Esc that closed something else this frame

    public void SetOpen(bool value)
    {
        if (value == open) return;
        open = value;
        screen.SetActive(value);
        InputGate.Blocked = value;
        if (!value)
        {
            SaveIfChanged();
            if (host != null) host.ShowColumn(false);
            GameCursor.Close(this);
            return;
        }
        NetGameManager gm = NetGameManager.Instance;
        bool canLeave = gm != null && gm.InLobby && !gm.DevMode;
        leaveButton.gameObject.SetActive(canLeave);
        ((RectTransform)quitButton.transform).anchoredPosition = canLeave ? quitAt : leaveAt;
        ShowMain();
    }

    public void ShowMain()
    {
        SaveIfChanged();
        mainPanel.SetActive(true);
        settingsPanel.SetActive(false);
        ((RectTransform)mainPanel.transform).anchoredPosition = mainAt - (host != null ? new Vector2(ColumnGap, 0f) : Vector2.zero);
        if (host != null) host.ShowColumn(true);
        GameCursor.Open(this, GameCursor.Use.Menu, resumeButton.gameObject);
        SelectForGamepad(resumeButton.gameObject);
    }

    // HostMenu: its team editor or map list takes the whole menu.
    public void HideColumns() => mainPanel.SetActive(false);

    public void ShowSettings()
    {
        mainPanel.SetActive(false);
        if (host != null) host.ShowColumn(false);
        settingsPanel.SetActive(true);
        mouseSlider.SetValueWithoutNotify(GameSettings.MouseSensitivity);
        stickSlider.SetValueWithoutNotify(GameSettings.StickSensitivity);
        gyroXSlider.SetValueWithoutNotify(GameSettings.GyroSensitivityX);
        gyroYSlider.SetValueWithoutNotify(GameSettings.GyroSensitivityY);
        gyroToggle.SetIsOnWithoutNotify(GameSettings.GyroEnabled);
        mouseValue.text = $"{GameSettings.MouseSensitivity:0.00}×";
        stickValue.text = $"{GameSettings.StickSensitivity:0.00}×";
        gyroXValue.text = $"{GameSettings.GyroSensitivityX:0.00}×";
        gyroYValue.text = $"{GameSettings.GyroSensitivityY:0.00}×";
        GameCursor.Open(this, GameCursor.Use.Menu, mouseSlider.gameObject);
        SelectForGamepad(mouseSlider.gameObject);
    }

    // When a gamepad's picking buttons, move the choice onto the panel now showing.
    static void SelectForGamepad(GameObject first)
    {
        var es = UnityEngine.EventSystems.EventSystem.current;
        if (es != null && GameCursor.CurrentMode == GameCursor.Mode.Select) es.SetSelectedGameObject(first);
    }

    void SaveIfChanged()
    {
        if (!settingsChanged) return;
        settingsChanged = false;
        _ = GameSettings.SaveNow();
    }

    void OpenLoadout()
    {
        SetOpen(false);
        LoadoutMenu loadout = FindFirstObjectByType<LoadoutMenu>();
        if (loadout != null) loadout.SetOpen(true);
    }

    void Leave()
    {
        SetOpen(false);
        NetGameManager.Instance?.LeaveLobby();
    }

    static void Quit()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }
}
