using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

public class RealTimeChatManager : MonoBehaviour
{
    // ===============================
    // Config (Inspector)
    // ===============================
    [Header("OpenAI Settings")]
    [Tooltip("Your OpenAI API key (sk-...) – store securely for production.")]
    public string openAIApiKey = string.Empty;

    [Tooltip("Realtime model name. e.g. gpt-4o-mini-realtime-preview")]
    public string model = "gpt-4o-mini-realtime-preview";

    [Tooltip("Voice preset name (e.g., alloy, verse, aria)")]
    public string voice = "alloy";

    [Tooltip("Basic Instructions")]
    public string basicInstructions = "You are a helpful, concise voice assistant.";

    [Tooltip("If true, automatically commit after append and request a response when VAD completes.")]
    public bool bAutoCreateResponse = false;

    [Header("Wiring")]
    [SerializeField] private Text userText; // 顯示使用者語音轉文字(含逐字&最終)
    [SerializeField] private Text aiText;   // 顯示 AI 回覆(含逐字&最終)
    [SerializeField] private Button micBtn; // 點擊=切換；長按=Push-To-Talk

    [Tooltip("Mic device name. Leave empty to use default device.")]
    public string microphoneDevice = string.Empty; // 麥克風 (2- Usb Audio Device)

    [Header("Optional: Typed input(非必接)")]
    [SerializeField] private InputField typedInput; // 若你有打字輸入就接上
    [SerializeField] private Button sendBtn;

    [Header("Playback")]
    public AudioSource playbackSource; // attach an AudioSource (optional for TTS playback)

    [Header("Audio Settings")]
    [Tooltip("Preferred sample rate for mic capture. Actual mic rate comes from _micClip.frequency.")]
    public int sampleRate = 24000;

    [Tooltip("Seconds per chunk when sending mic audio frames.")]
    [Range(0.05f, 0.5f)] public float sendChunkSeconds = 0.25f; // 250ms

    // Send loop flags
    private volatile bool _streamingMic;
    public bool IsMicOn { get => _streamingMic; }

    // ===============================
    // OpenAI realtime
    // ===============================
    private OpenAIRealtime aiRealtime;

    // ===============================
    // Internals - Mic capture/send
    // ===============================
    private AudioClip _micClip;
    private int _micReadPos;
    private int _clipSamples;
    private int _clipChannels;

    // ===============================
    // Internals - RX/playback
    // ===============================
    private readonly ConcurrentQueue<float> _rxQueue = new ConcurrentQueue<float>(); // 24k mono float
    private int _srcSampleRate = 24000;  // model output when pcm16
    private int _dspSampleRate = 48000;  // audio device output
    private float _holdSample;           // for 24k→48k duplication
    private int _dupState;               // 0/1 alternating

    // temp buffers
    private float[] _floatBuf   = Array.Empty<float>(); // multi-channel
    private float[] _monoBuf    = Array.Empty<float>();  // mono
    private byte[] _pcmBuf      = Array.Empty<byte>();   // PCM16 mono

    // 內部狀態
    private bool pushToTalkHeld = false;

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

        if (!playbackSource)
        {
            playbackSource              = gameObject.AddComponent<AudioSource>();
            playbackSource.playOnAwake  = true;
            playbackSource.loop         = true;   // feed audio via OnAudioFilterRead
            playbackSource.spatialBlend = 0f;
        }

        // sample rate監控與設定
        _dspSampleRate  = AudioSettings.outputSampleRate;
        AudioSettings.OnAudioConfigurationChanged += OnAudioConfigChanged;

        // openai 物件
        aiRealtime      = new OpenAIRealtime(openAIApiKey, model, voice, basicInstructions, bAutoCreateResponse);        
    }

    private void OnEnable()
    {
        // ===== 事件訂閱（對應你的 OpenAIRealtimeUnity 事件命名調整一下） =====
        // 使用者逐字轉寫
        //aiRealtimeUnity.OnUserTranscript += HandleUserTranscript;                       // TODO: Action<string> partialOrFinal
        // AI 逐字/最終文字（帶 isFinal 旗標）
        aiRealtime.OnAssistantTextDelta    += HandleAITranscript;                           // TODO: Action<string,bool>
        // AI 語音資訊
        aiRealtime.OnAssistantAudioDelta   += HandleAIAudio;
        // 連線/工作階段狀態（可選）
        //aiRealtimeUnity.OnSessionStateChanged += HandleSessionStateChanged;             // TODO: Action<string>
    }

    private void OnDisable()
    {
        //aiRealtimeUnity.OnUserTranscript -= HandleUserTranscript;
        aiRealtime.OnAssistantTextDelta    -= HandleAITranscript;
        aiRealtime.OnAssistantAudioDelta   -= HandleAIAudio;
        //aiRealtimeUnity.OnSessionStateChanged -= HandleSessionStateChanged;
    }

    private async void Start()
    {
        // 啟動時保險：把顯示清空
        if (userText) userText.text = "";
        if (aiText) aiText.text = "";

        RefreshMicUI();

        await aiRealtime.ConnectAndConfigure();
    }

    private void Update()
    {
        aiRealtime.Update();
    }

    private async void OnDestroy()
    {
        try
        {
            _streamingMic = false;
        }
        catch { }

        if (_micClip != null && Microphone.IsRecording(microphoneDevice))
        {
            Microphone.End(microphoneDevice);
        }

        AudioSettings.OnAudioConfigurationChanged -= OnAudioConfigChanged;

        aiRealtime.Dispose();
    }

    private void OnAudioConfigChanged(bool deviceWasChanged)
    {
        _dspSampleRate = AudioSettings.outputSampleRate;
        Debug.Log($"[Audio] DSP sampleRate={_dspSampleRate}, deviceChanged={deviceWasChanged}");
    }

    // =============== UI Handlers ===============

    // 點一下：切換持續收音
    private void OnMicClicked()
    {
        if (IsMicOn)
            StopRecord();
        else
            StartRecord();
    }

    // 長按（Push-To-Talk）：可在 EventTrigger 綁定 PointerDown/PointerUp
    public void OnMicPointerDown()
    {
        pushToTalkHeld = true;
        if (!IsMicOn) StartRecord();
    }
    public void OnMicPointerUp()
    {
        pushToTalkHeld = false;
        if (IsMicOn) StopRecord();
    }

    private async void OnSendClicked()
    {
        if (typedInput == null || string.IsNullOrWhiteSpace(typedInput.text)) return;
        string msg = typedInput.text.Trim();
        typedInput.text = "";

        // 顯示在使用者視窗
        if (userText) userText.text = msg;

        // 丟給 Realtime（讓模型直接回）
        await SafeCall(async () => await aiRealtime.SendTextAsync(msg)); // TODO: 若你方法名不同，改成對應
    }

    // =============== Realtime Callbacks ===============

    private void HandleAITranscript(string text)
    {
        if (aiText == null) return;

        // 逐字：即時顯示；最終：覆蓋並可加結尾標記
        aiText.text =  text + "▌"; // 小光標感
    }

    private void HandleAIAudio(float[] f)
    {
        for (int i = 0; i < f.Length; i++)
        {
            _rxQueue.Enqueue(f[i]);
        }
    }

    // =============== Helpers ===============
    
    // ===============================
    // Mic controls
    // ===============================
    
    private void StartRecord()
    {
        MicStartButton();                                      // TODO
        RefreshMicUI();
    }

    private void StopRecord()
    {
        MicStopButton();                                       // TODO
        RefreshMicUI();
    }

    private void RefreshMicUI()
    {
        if (micBtn == null) return;

        var label = micBtn.GetComponentInChildren<Text>();
        if (label != null)
        {
            if (pushToTalkHeld) label.text = "PTT…(鬆開停止)";
            else if (IsMicOn) label.text = "收音中…(點擊關)";
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

    [ContextMenu("MIC: Start")]
    public void MicStartButton()
    {
        StartMic();
        _ = SendMicLoopAsync();
    }

    [ContextMenu("MIC: Stop")]
    public void MicStopButton()
    {
        _streamingMic = false;
        if (_micClip != null && Microphone.IsRecording(microphoneDevice))
        {
            Microphone.End(microphoneDevice);
        }
        Debug.Log("[Mic] Stop pressed");
    }

    private void StartMic()
    {
        if (Microphone.devices.Length == 0)
        {
            Debug.LogError("No microphone devices found.");
            return;
        }

        if (string.IsNullOrEmpty(microphoneDevice))
        {
            microphoneDevice = Microphone.devices[0];
        }

        _micClip = Microphone.Start(microphoneDevice, true, 10, sampleRate);

        while (Microphone.GetPosition(microphoneDevice) <= 0)
        { }

        _clipSamples    = _micClip.samples;
        _clipChannels   = _micClip.channels;
        _micReadPos     = 0;

        // allocate small; will be resized in loop as needed
        _floatBuf       = new float[1];
        _monoBuf        = new float[1];
        _pcmBuf         = new byte[1];

        playbackSource.Play();
        _streamingMic   = true;

        Debug.Log($"[Mic] Device={microphoneDevice}, reqRate={sampleRate}, actualRate={_micClip.frequency}, samples={_clipSamples}, ch={_clipChannels}");
    }

    private async Task SendMicLoopAsync()
    {
        if (_micClip == null)
        {
            Debug.LogError("Mic not started.");
            return;
        }

        if (!aiRealtime.IsConnect())
        {
            Debug.LogError("WebSocket not connected.");
            return;
        }

        int effectiveRate   = (_micClip.frequency > 0) ? _micClip.frequency : sampleRate;
        int chunkSamples    = Mathf.Max(1, (int)(sendChunkSeconds * effectiveRate));

        _floatBuf   = new float[chunkSamples * _clipChannels];
        _monoBuf    = new float[chunkSamples];
        _pcmBuf     = new byte[chunkSamples * 2];

        // warm up: wait until ~150ms data is available
        int warmupNeeded = (int)(effectiveRate * 0.15f);

        while (true)
        {
            int pos         = Microphone.GetPosition(microphoneDevice);
            int available   = (_micReadPos <= pos) ? (pos - _micReadPos) : (pos + _clipSamples - _micReadPos);

            if (available >= warmupNeeded) break;
            
            await Task.Delay(10);
        }

        while (_streamingMic && aiRealtime.IsConnect())
        {
            int micPos      = Microphone.GetPosition(microphoneDevice);
            if (micPos < 0)
            {
                await Task.Yield(); continue;
            }

            int available   = (_micReadPos <= micPos) ? (micPos - _micReadPos) : (micPos + _clipSamples - _micReadPos);
            int toSend      = Mathf.Min(available, chunkSamples);
            if (toSend <= 0)
            {
                await Task.Delay(8); continue;
            }

            // read (handle wrap)
            int neededFloats = toSend * _clipChannels;
            if (_floatBuf.Length != neededFloats)
            {
                _floatBuf = new float[neededFloats];
            }

            if (_micReadPos + toSend <= _clipSamples)
            {
                _micClip.GetData(_floatBuf, _micReadPos);
            }
            else
            {
                int firstPart   = _clipSamples - _micReadPos;
                int secondPart  = toSend - firstPart;
                var a           = new float[firstPart * _clipChannels];
                var b           = new float[secondPart * _clipChannels];

                _micClip.GetData(a, _micReadPos);
                _micClip.GetData(b, 0);

                Array.Copy(a, 0, _floatBuf, 0, a.Length);
                Array.Copy(b, 0, _floatBuf, a.Length, b.Length);
            }

            // downmix → mono
            if (_monoBuf.Length < toSend)
            {
                _monoBuf = new float[toSend];
            }

            if (_clipChannels == 1)
            {
                Array.Copy(_floatBuf, 0, _monoBuf, 0, toSend);
            }
            else
            {
                for (int i = 0; i < toSend; i++)
                {
                    double acc = 0; int baseIdx = i * _clipChannels;
                    for (int ch = 0; ch < _clipChannels; ch++)
                    {
                        acc += _floatBuf[baseIdx + ch];
                    }
                    _monoBuf[i] = (float)(acc / _clipChannels);
                }
            }

            // float [-1,1] → PCM16 LE
            int byteCount = toSend * 2;

            if (_pcmBuf.Length < byteCount)
            {
                _pcmBuf = new byte[byteCount];
            }

            for (int i = 0, b = 0; i < toSend; i++, b += 2)
            {
                float f = Mathf.Clamp(_monoBuf[i], -1f, 1f);
                short s = (short)Mathf.RoundToInt(f * 32767f);
                _pcmBuf[b] = (byte)(s & 0xFF);
                _pcmBuf[b + 1] = (byte)((s >> 8) & 0xFF);
            }

            string b64 = Convert.ToBase64String(_pcmBuf, 0, byteCount);
            _micReadPos = (_micReadPos + toSend) % _clipSamples;

            await aiRealtime.SendAudioBase64Async(b64);
        }
    }

    // ===============================
    // Playback (audio thread)
    // ===============================
    void OnAudioFilterRead(float[] data, int channels)
    {
        int dstRate = _dspSampleRate;

        if (dstRate == _srcSampleRate * 2)
        {
            // Fast path: 24k -> 48k (duplicate every sample)
            for (int i = 0; i < data.Length; i += channels)
            {
                float sample;
                if (_dupState == 0)
                {
                    if (!_rxQueue.TryDequeue(out sample))
                    {
                        sample = 0f;
                    }
                    _holdSample = sample; _dupState = 1;
                }
                else
                {
                    sample = _holdSample; _dupState = 0;
                }
                for (int c = 0; c < channels; c++) data[i + c] = sample;
            }
        }
        else
        {
            // Fallback: simple hold-based resampling (cheap and stable for speech)
            double step = _srcSampleRate / Math.Max(1, dstRate);
            double acc  = 0.0;
            for (int i = 0; i < data.Length; i += channels)
            {
                while (acc <= 0.0)
                {
                    if (_rxQueue.TryDequeue(out _holdSample)) { }
                    acc += 1.0;
                }
                acc -= step;
                for (int c = 0; c < channels; c++) data[i + c] = _holdSample;
            }
        }
    }

    // ===============================
    // Quick test action
    // ===============================
    [ContextMenu("Send Text Prompt")]
    public async void SendTextPrompt_Context()
    {
        if (!aiRealtime.IsConnect())
        {
            Debug.LogError("WebSocket not connected.");
            return;
        }

        await aiRealtime.SendTextAsync("請隨機念出一首唐詩");
    }
}
