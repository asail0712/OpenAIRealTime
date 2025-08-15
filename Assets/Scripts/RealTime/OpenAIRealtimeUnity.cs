using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// Refactored & trimmed version based on user's original file.
/// Focus: clear responsibilities, minimal state, safe defaults, and readable flow.
/// </summary>
public class OpenAIRealtimeUnity : MonoBehaviour
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

    [Header("Audio Settings")]
    [Tooltip("Preferred sample rate for mic capture. Actual mic rate comes from _micClip.frequency.")]
    public int sampleRate = 24000;

    [Tooltip("Mic device name. Leave empty to use default device.")]
    public string microphoneDevice = string.Empty;

    [Tooltip("Seconds per chunk when sending mic audio frames.")]
    [Range(0.05f, 0.5f)] public float sendChunkSeconds = 0.25f; // 250ms

    [Header("Playback")]
    public AudioSource playbackSource; // attach an AudioSource (optional for TTS playback)

    [Header("Behavior")]
    [Tooltip("If true, automatically commit after append and request a response when VAD completes.")]
    public bool autoCreateResponse = false;

    // ===============================
    // Internals - WS
    // ===============================
    private ClientWebSocket _ws;
    private CancellationTokenSource _cts;
    private Uri _uri;
    private volatile bool _connected;

    // ===============================
    // Internals - Mic capture/send
    // ===============================
    private AudioClip _micClip;
    private int _micReadPos;
    private int _clipSamples;
    private int _clipChannels;

    // temp buffers
    private float[] _floatBuf   = Array.Empty<float>(); // multi-channel
    private float[] _monoBuf    = Array.Empty<float>();  // mono
    private byte[] _pcmBuf      = Array.Empty<byte>();   // PCM16 mono

    // Send loop flags
    private volatile bool _streamingMic;

    // ===============================
    // Internals - RX/playback
    // ===============================
    private readonly ConcurrentQueue<float> _rxQueue = new ConcurrentQueue<float>(); // 24k mono float
    private int _srcSampleRate = 24000;  // model output when pcm16
    private int _dspSampleRate = 48000;  // audio device output
    private float _holdSample;           // for 24k→48k duplication
    private int _dupState;               // 0/1 alternating

    // text/ASR
    private readonly StringBuilder _userTranscript = new StringBuilder();

    // buffers
    private readonly byte[] _recvBuffer = new byte[1 << 16]; // 64KB

    // events
    public event Action<string> OnUserTranscriptDone;
    public event Action<string> OnAssistantTextDone;
    public event Action<byte[]> OnAssistantAudioDone;

    // response lifecycle (simple)
    private volatile bool _responseInFlight;

    // ===============================
    // Unity lifecycle
    // ===============================
    void Awake()
    {
        if (!playbackSource)
        {
            playbackSource              = gameObject.AddComponent<AudioSource>();
            playbackSource.playOnAwake  = true;
            playbackSource.loop         = true;   // feed audio via OnAudioFilterRead
            playbackSource.spatialBlend = 0f;
        }

        _dspSampleRate                              = AudioSettings.outputSampleRate;
        AudioSettings.OnAudioConfigurationChanged   += OnAudioConfigChanged;
    }

    async void Start()
    {
        await ConnectAndConfigure();
        if (_connected)
        {
            _ = ReceiveLoop();
        }
    }

    async void OnDestroy()
    {
        AudioSettings.OnAudioConfigurationChanged -= OnAudioConfigChanged;
        try
        {
            _streamingMic = false;
            if (_ws != null && _ws.State == WebSocketState.Open)
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
        }
        catch { }

        if (_micClip != null && Microphone.IsRecording(microphoneDevice))
        {
            Microphone.End(microphoneDevice);
        }
        _cts?.Cancel();
    }

    private void OnAudioConfigChanged(bool deviceWasChanged)
    {
        _dspSampleRate = AudioSettings.outputSampleRate;
        Debug.Log($"[Audio] DSP sampleRate={_dspSampleRate}, deviceChanged={deviceWasChanged}");
    }

    // ===============================
    // Connect & session
    // ===============================
    private async Task ConnectAndConfigure()
    {
        _uri    = new Uri($"wss://api.openai.com/v1/realtime?model={model}");
        _ws     = new ClientWebSocket();
        _ws.Options.SetRequestHeader("Authorization", $"Bearer {openAIApiKey}");
        _ws.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1");

        _cts = new CancellationTokenSource();
        try
        {
            await _ws.ConnectAsync(_uri, _cts.Token);
            _connected = _ws.State == WebSocketState.Open;
        }
        catch (Exception ex)
        {
            Debug.LogError($"Realtime connect failed: {ex.Message}");
            _connected = false; return;
        }

        if (_connected)
        {
            // Minimal and valid session params
            await SendAsync(new
            {
                type    = "session.update",
                session = new
                {
                    input_audio_format  = "pcm16",
                    output_audio_format = "pcm16",
                    turn_detection = new
                    {
                        type                = "server_vad",
                        threshold           = 0.5,
                        prefix_padding_ms   = 300,
                        silence_duration_ms = 500
                    },
                    input_audio_transcription   = new { model = "gpt-4o-mini-transcribe" },
                    instructions                = basicInstructions,
                    voice                       = voice
                }
            });
        }
    }

    private Task SendAsync(object payload)
    {
        if (_ws == null || _ws.State != WebSocketState.Open)
        {
            return Task.CompletedTask;
        }

        string json = JsonConvert.SerializeObject(payload);
        var bytes   = Encoding.UTF8.GetBytes(json);
        return _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts.Token);
    }

    // ===============================
    // Mic controls
    // ===============================
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
        _floatBuf   = new float[1];
        _monoBuf    = new float[1];
        _pcmBuf     = new byte[1];

        playbackSource.Play();
        _streamingMic = true;

        Debug.Log($"[Mic] Device={microphoneDevice}, reqRate={sampleRate}, actualRate={_micClip.frequency}, samples={_clipSamples}, ch={_clipChannels}");
    }

    private async Task SendMicLoopAsync()
    {
        if (_micClip == null) 
        { 
            Debug.LogError("Mic not started."); 
            return; 
        }

        if (_ws == null || _ws.State != WebSocketState.Open || !_connected) 
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
            if (available >= warmupNeeded)
            {
                break;
            }
            await Task.Delay(10);
        }

        while (_streamingMic && _connected && _ws != null && _ws.State == WebSocketState.Open)
        {
            int micPos = Microphone.GetPosition(microphoneDevice);
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
                    double acc  = 0; int baseIdx = i * _clipChannels;
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
                float f         = Mathf.Clamp(_monoBuf[i], -1f, 1f);
                short s         = (short)Mathf.RoundToInt(f * 32767f);
                _pcmBuf[b]      = (byte)(s & 0xFF);
                _pcmBuf[b + 1]  = (byte)((s >> 8) & 0xFF);
            }

            string b64  = Convert.ToBase64String(_pcmBuf, 0, byteCount);
            _micReadPos = (_micReadPos + toSend) % _clipSamples;

            await SendAsync(new { type = "input_audio_buffer.append", audio = b64 });
            // If you want auto-commit+respond each chunk, uncomment below lines
            // await SendAsync(new { type = "input_audio_buffer.commit" });
            // if (!_responseInFlight && autoCreateResponse) await SendAsync(new { type = "response.create" });
        }
    }

    // ===============================
    // Receive & events
    // ===============================
    private async Task ReceiveLoop()
    {
        var textBuilder = new StringBuilder();

        while (_connected && _ws.State == WebSocketState.Open)
        {
            WebSocketReceiveResult res;
            var sb = new StringBuilder();
            try
            {
                do
                {
                    res = await _ws.ReceiveAsync(new ArraySegment<byte>(_recvBuffer), _cts.Token);
                    if (res.MessageType == WebSocketMessageType.Close)
                    {
                        Debug.LogWarning($"Realtime closed: {res.CloseStatus} {res.CloseStatusDescription}");
                        _connected = false; break;
                    }
                    sb.Append(Encoding.UTF8.GetString(_recvBuffer, 0, res.Count));
                }
                while (!res.EndOfMessage);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Receive error: {ex.Message}");
                break;
            }

            if (!_connected)
            {
                break;
            }

            var payload = sb.ToString();
            var lines   = payload.Split('\n');
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line) || !line.Contains("\"type\""))
                {
                    continue;
                }

                HandleServerEvent(line, textBuilder);
            }
        }
    }

    private void HandleServerEvent(string jsonLine, StringBuilder textBuilder)
    {
        JObject jo;
        try { jo = JObject.Parse(jsonLine); }
        catch
        {
            if (jsonLine.Contains("\"error\"")) Debug.LogError($"SERVER ERROR (raw): {jsonLine}");
            return;
        }

        string type = (string)jo["type"] ?? string.Empty;
        if (string.IsNullOrEmpty(type))
        {
            return;
        }

        switch (type)
        {
            // --- Response lifecycle ---
            case "response.created":
                _responseInFlight = true;
                return;
            case "response.completed":
            case "response.done":
                _responseInFlight = false;
                return;

            // --- Text stream ---
            case "response.output_text.delta":
            case "response.text.delta":
                {
                    string d = (string)jo["delta"];
                    if (!string.IsNullOrEmpty(d))
                    {
                        textBuilder.Append(d);
                    }
                    return;
                }
            case "response.output_text.done":
            case "response.text.done":
                {
                    string txt = textBuilder.ToString();
                    if (!string.IsNullOrEmpty(txt))
                    {
                        OnAssistantTextDone?.Invoke(txt);
                    }
                    Debug.Log($"ASSISTANT TEXT: {txt}");
                    textBuilder.Clear();
                    return;
                }

            // --- Audio stream ---
            case "response.output_audio.delta":
            case "response.audio.delta":
                {
                    string b64 = (string)jo["delta"];
                    if (string.IsNullOrEmpty(b64))
                    {
                        return;
                    }

                    try
                    {
                        var bytes = Convert.FromBase64String(b64);
                        for (int i = 0; i < bytes.Length; i += 2)
                        {
                            short s = (short)(bytes[i] | (bytes[i + 1] << 8));
                            float f = s / 32768f; // mono 24k
                            _rxQueue.Enqueue(f);
                        }
                    }
                    catch (Exception e) { Debug.LogWarning($"Audio delta decode error: {e.Message}"); }
                    return;
                }
            case "response.output_audio.done":
                {
                    OnAssistantAudioDone?.Invoke(Array.Empty<byte>()); // signal done; raw bytes not stored here
                    return;
                }

            // --- Assistant ASR of user audio (optional hooks) ---
            case "conversation.item.input_audio_transcription.delta":
                {
                    string d = (string)jo["delta"]; if (!string.IsNullOrEmpty(d)) _userTranscript.Append(d);
                    return;
                }
            case "conversation.item.input_audio_transcription.completed":
                {
                    string text = (string)jo["text"];
                    if (!string.IsNullOrEmpty(text))
                    {
                        OnUserTranscriptDone?.Invoke(text);
                        Debug.Log($"USER TRANSCRIPT: {text}");
                    }
                    _userTranscript.Clear();
                    if (autoCreateResponse && !_responseInFlight)
                    {
                        _ = SendAsync(new { type = "input_audio_buffer.commit" });
                        _ = SendAsync(new { type = "response.create" });
                    }
                    return;
                }

            // --- Errors ---
            case "error":
                {
                    string code = (string)jo["error"]?["code"];
                    string msg  = (string)jo["error"]?["message"];
                    Debug.LogError($"SERVER ERROR: code={code}, message={msg}\n{jsonLine}");
                    return;
                }

            default:
                // Unhandled events are fine for now
                return;
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
            double step = 24000.0 / Math.Max(1, dstRate);
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
    public async void SendTextPrompt()
    {
        if (!_connected)
        {
            return;
        }

        var create = new
        {
            type        = "response.create",
            response    = new
            {
                modalities      = new[] { "text", "audio" },
                instructions    = "請隨機念出一首唐詩"
            }
        };

        await SendAsync(create);
    }
}
