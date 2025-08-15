using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

public class RealTimeChatManager : MonoBehaviour
{
    [Header("Wiring")]
    [SerializeField] private OpenAIRealtimeUnity aiRealtimeUnity;
    [SerializeField] private Text userText; // 顯示使用者語音轉文字(含逐字&最終)
    [SerializeField] private Text aiText;   // 顯示 AI 回覆(含逐字&最終)
    [SerializeField] private Button micBtn; // 點擊=切換；長按=Push-To-Talk

    [Header("Optional: Typed input(非必接)")]
    [SerializeField] private InputField typedInput; // 若你有打字輸入就接上
    [SerializeField] private Button sendBtn;

    // 內部狀態
    private bool pushToTalkHeld = false;
    private bool micOn => aiRealtimeUnity != null && aiRealtimeUnity.IsMicOn; // TODO: 若你用別名，改成對應的屬性

    private void Awake()
    {
        if (micBtn != null)
        {
            micBtn.onClick.AddListener(OnMicClicked);
        }
        if (sendBtn != null)
        {
            sendBtn.onClick.AddListener(OnSendClicked);
        }
    }

    private void OnEnable()
    {
        // ===== 事件訂閱（對應你的 OpenAIRealtimeUnity 事件命名調整一下） =====
        // 使用者逐字轉寫
        //aiRealtimeUnity.OnUserTranscript += HandleUserTranscript;                       // TODO: Action<string> partialOrFinal
        // AI 逐字/最終文字（帶 isFinal 旗標）
        aiRealtimeUnity.OnAssistantTextDelta += HandleAITranscript;                           // TODO: Action<string,bool>
        // 麥克風狀態（UI 同步）
        //aiRealtimeUnity.OnMicStateChanged += HandleMicStateChanged;                     // TODO: Action<bool>
        // 連線/工作階段狀態（可選）
        //aiRealtimeUnity.OnSessionStateChanged += HandleSessionStateChanged;             // TODO: Action<string>
    }

    private void OnDisable()
    {
        //aiRealtimeUnity.OnUserTranscript -= HandleUserTranscript;
        aiRealtimeUnity.OnAssistantTextDelta -= HandleAITranscript;
        //aiRealtimeUnity.OnMicStateChanged -= HandleMicStateChanged;
        //aiRealtimeUnity.OnSessionStateChanged -= HandleSessionStateChanged;
    }

    private void Start()
    {
        // 啟動時保險：把顯示清空
        if (userText) userText.text = "";
        if (aiText) aiText.text = "";
        RefreshMicUI();
    }

    // =============== UI Handlers ===============

    // 點一下：切換持續收音
    private void OnMicClicked()
    {
        if (micOn)
            StopMic();
        else
            StartMic();
    }

    // 長按（Push-To-Talk）：可在 EventTrigger 綁定 PointerDown/PointerUp
    public void OnMicPointerDown()
    {
        pushToTalkHeld = true;
        if (!micOn) StartMic();
    }
    public void OnMicPointerUp()
    {
        pushToTalkHeld = false;
        if (micOn) StopMic();
    }

    private async void OnSendClicked()
    {
        if (typedInput == null || string.IsNullOrWhiteSpace(typedInput.text)) return;
        string msg = typedInput.text.Trim();
        typedInput.text = "";

        // 顯示在使用者視窗
        if (userText) userText.text = msg;

        // 丟給 Realtime（讓模型直接回）
        await SafeCall(async () => await aiRealtimeUnity.SendTextAsync(msg)); // TODO: 若你方法名不同，改成對應
    }

    // =============== Realtime Callbacks ===============

    private void HandleUserTranscript(string text)
    {
        if (userText == null) return;
        userText.text = text; // 逐字/最終都直接覆蓋
    }

    private void HandleAITranscript(string text)
    {
        if (aiText == null) return;

        // 逐字：即時顯示；最終：覆蓋並可加結尾標記
        aiText.text =  text + "▌"; // 小光標感
    }

    private void HandleMicStateChanged(bool isOn)
    {
        RefreshMicUI();
    }

    private void HandleSessionStateChanged(string state)
    {
        // 你若有狀態列可顯示（e.g., Connecting/Ready/Disconnected），可在這裡更新
        // Debug.Log($"Realtime session: {state}");
    }

    // =============== Helpers ===============

    private void StartMic()
    {
        aiRealtimeUnity.MicStartButton();                                      // TODO
        RefreshMicUI();
    }

    private void StopMic()
    {
        aiRealtimeUnity.MicStopButton();                                       // TODO
        RefreshMicUI();
    }

    private void RefreshMicUI()
    {
        if (micBtn == null) return;

        var label = micBtn.GetComponentInChildren<Text>();
        if (label != null)
        {
            if (pushToTalkHeld) label.text = "PTT…(鬆開停止)";
            else if (micOn) label.text = "收音中…(點擊關)";
            else label.text = "按下說話";
        }

        // 視覺狀態（可自訂色塊/動畫）
        micBtn.interactable = true;
    }

    private async Task SafeCall(Func<Task> fn)
    {
        try { await fn(); }
        catch (Exception e)
        {
            Debug.LogError($"[RealTimeChatManager] {e.Message}\n{e.StackTrace}");
        }
    }
}
