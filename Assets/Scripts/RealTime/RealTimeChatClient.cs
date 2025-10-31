﻿using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Unity.Burst.Intrinsics;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.UI;

public static class AudioMsgTypes
{
    public const string Start               = "audio.Start";            // server -> client
    public const string Finish              = "audio.Finish";           // server -> client
    public const string Logging             = "audio.Logging";          // server -> client

    public const string Send                = "audio.Send";             // client -> server
    public const string InterruptReceive    = "audio.InterruptReceive"; // client -> server
    public const string ReceiveAudio        = "audio.ReceiveAudio";     // server -> client
    public const string ReceiveText         = "audio.ReceiveText";      // server -> client
}

// 依照Server定義決定
public class AIMessage
{
    public string Type { get; set; }        = string.Empty;    
    public object? Payload { get; set; }    = null; // Base64 編碼的 PCM16 音訊資料 或是文字資料
}


public class RealTimeChatClient : MonoBehaviour
{
    [Header("Wiring")]
    [SerializeField] private Text aiText;   // 顯示 AI 回覆(含逐字&最終)
    [SerializeField] private Button micBtn; // 點擊=切換；長按=Push-To-Talk

    [Tooltip("Mic device name. Leave empty to use default device.")]
    public string microphoneDevice = string.Empty; // 麥克風 (2- Usb Audio Device)

    [Header("Optional: Typed input(非必接)")]
    [SerializeField] private Button interruptAIBtn;

    [Header("Playback")]
    public AudioSource playbackSource; // attach an AudioSource (optional for TTS playback)

    [Header("Audio Settings")]
    [Tooltip("Preferred sample rate for mic capture. Actual mic rate comes from _micClip.frequency.")]
    public int sampleRate = 24000;

    [Tooltip("Seconds per chunk when sending mic audio frames.")]
    [Range(0.05f, 0.5f)] public float sendChunkSeconds = 0.25f; // 250ms

    // Send loop flags
    private volatile bool bStreamingMic;

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
    private readonly ConcurrentQueue<float> _rxQueue    = new ConcurrentQueue<float>(); // 24k mono float
    private int _srcSampleRate                          = 24000;    // model output when pcm16
    private int _dspSampleRate                          = 48000;    // audio device output
    private float _holdSample;                                      // for 24k→48k duplication
    private int _dupState;                                          // 0/1 alternating

    // temp buffers
    private float[] _floatBuf   = Array.Empty<float>(); // multi-channel
    private float[] _monoBuf    = Array.Empty<float>();  // mono
    private byte[] _pcmBuf      = Array.Empty<byte>();   // PCM16 mono

    // ===============================
    // WS Client
    // ===============================
    // ==== 配置這裡 ====
    [Header("WebSocket")]
    [Tooltip("WebSocket 伺服器位址，例如 ws://127.0.0.1:5000/ws 或 wss://your.host/ws")]
    public string wsUrl = "ws://127.0.0.1:5000/ws";

    private ClientWebSocket _ws;
    private CancellationTokenSource _cts;
    private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
    private readonly ConcurrentQueue<Action> _main = new ConcurrentQueue<Action>(); // 主執行緒回撥佇列

    private void EmitOnMain(Action a) { if (a != null) _main.Enqueue(a); }

    private void Update()
    {
        while (_main.TryDequeue(out var a))
        {
            try { a?.Invoke(); } catch (Exception e) { Debug.LogError(e); }
        }
    }


    private void Awake()
    {
        if (micBtn != null)
        {
            micBtn.onClick.AddListener(OnMicClicked);
        }

        if (interruptAIBtn != null)
        {
            interruptAIBtn.onClick.AddListener(OnInterruptClicked);
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
    }

    private async void Start()
    {
        // 啟動時保險：把顯示清空
        if (aiText) aiText.text = "";

        RefreshMicUI();

        // 連 WebSocket
        await ConnectAsync();
    }

    private void OnDestroy()
    {
        if (_micClip != null && Microphone.IsRecording(microphoneDevice))
        {
            Microphone.End(microphoneDevice);
        }

        AudioSettings.OnAudioConfigurationChanged -= OnAudioConfigChanged;

        try { _cts?.Cancel(); } catch { }
        try { _ws?.Abort(); } catch { }
        try { _ws?.Dispose(); } catch { }
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
        if (bStreamingMic)
            StopRecord();
        else
            StartRecord();
    }

    // =============== interrupt ai talk ================
    private async void OnInterruptClicked()
    {
        // 中斷處理
        playbackSource.enabled = false;

        await SendInterruptAsync(); // 通知伺服器中斷目前的 AI 播放
    }

    // =============== Realtime Callbacks ===============

    private void HandleAIResposeStart()
    {
        playbackSource.enabled = true;
        _rxQueue.Clear();
    }

    private void HandleAIResposeFinish()
    {
        //playbackSource.enabled = false;
    }

    private void HandleAITranscript(string text)
    {
        if (aiText == null) return;

        // 逐字：即時顯示；最終：覆蓋並可加結尾標記
        aiText.text =  text + "▌"; // 小光標感
    }

    private void HandleAIAudio(byte[] bytes)
    {
        int sampleCount = bytes.Length / 2;
        var block       = new float[sampleCount];

        for (int i = 0, si = 0; i < bytes.Length; i += 2, si++)
        {
            short s     = (short)(bytes[i] | (bytes[i + 1] << 8));
            block[si]   = s / 32768f; // mono 24k
        }

        for (int i = 0; i < block.Length; i++)
        {
            _rxQueue.Enqueue(block[i]);
        }
    }

    private void HandleAILog(DebugLevel lv, string logStr)
    {
        switch (lv)
        {
            case DebugLevel.Log:
                Debug.Log(logStr);
                break;
            case DebugLevel.Warning:
                Debug.LogWarning(logStr);
                break;
            case DebugLevel.Error:
                Debug.LogError(logStr);
                break;
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
            if (bStreamingMic) 
                label.text = "收音中…(點擊關)";
            else 
                label.text = "按下說話";
        }

        // 視覺狀態（可自訂色塊/動畫）
        micBtn.interactable = true;
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
        bStreamingMic = false;
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

        bStreamingMic   = true;

        playbackSource.Play();

        Debug.Log($"[Mic] Device={microphoneDevice}, reqRate={sampleRate}, actualRate={_micClip.frequency}, samples={_clipSamples}, ch={_clipChannels}");
    }

    private async Task SendMicLoopAsync()
    {
        if (_micClip == null)
        {
            Debug.LogError("Mic not started.");
            return;
        }

        if (!IsConnected())
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

        while (bStreamingMic && IsConnected())
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
                float f         = Mathf.Clamp(_monoBuf[i], -1f, 1f);
                short s         = (short)Mathf.RoundToInt(f * 32767f);
                _pcmBuf[b]      = (byte)(s & 0xFF);
                _pcmBuf[b + 1]  = (byte)((s >> 8) & 0xFF);
            }

            string b64  = Convert.ToBase64String(_pcmBuf, 0, byteCount);
            _micReadPos = (_micReadPos + toSend) % _clipSamples;

            // 送到後端 WebSocket
            await SendAudioBase64Async(b64);
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
    // WebSocket client
    // ===============================
    public bool IsConnected() => _ws != null && _ws.State == WebSocketState.Open;

    private async Task ConnectAsync()
    {
        _cts    = new CancellationTokenSource();
        _ws     = new ClientWebSocket();

        try
        {
            var uri = new Uri(wsUrl);
            await _ws.ConnectAsync(uri, _cts.Token).ConfigureAwait(false);
            Debug.Log($"[WS] Connected: {wsUrl}");

            // 啟動收訊循環
            _ = Task.Run(ReceiveLoopAsync);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[WS] Connect failed: {ex.Message}");
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[64 * 1024];

        while (IsConnected() && !_cts.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            int offset = 0;

            try
            {
                do
                {
                    var seg = new ArraySegment<byte>(buffer, offset, buffer.Length - offset);
                    result  = await _ws.ReceiveAsync(seg, _cts.Token).ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", _cts.Token).ConfigureAwait(false);
                        return;
                    }

                    offset += result.Count;

                    // 防護：訊息過大則擴容
                    if (offset >= buffer.Length)
                    {
                        Array.Resize(ref buffer, buffer.Length * 2);
                    }

                } while (!result.EndOfMessage);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Debug.LogWarning($"[WS] Receive error: {ex.Message}");
                break;
            }

            if (result.MessageType == WebSocketMessageType.Text)
            {
                var json = Encoding.UTF8.GetString(buffer, 0, offset);
                HandleIncomingJson(json);
            }
            else if (result.MessageType == WebSocketMessageType.Binary)
            {
                // 目前後端是把音訊包在文字 JSON 的 Payload（Base64），理論上不會傳 Binary。
                // 若改成 Binary，這裡可以直接轉 float 塞 _rxQueue。
            }
        }
    }

    private void HandleIncomingJson(string json)
    {
        AIMessage env = null;
        try
        {
            env = JsonConvert.DeserializeObject<AIMessage>(json);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[WS] Invalid JSON: {ex.Message}");
            return;
        }

        if (env == null || string.IsNullOrWhiteSpace(env.Type)) return;

        switch (env.Type)
        {
            case AudioMsgTypes.Start:
                EmitOnMain(() => HandleAIResposeStart());
                break;

            case AudioMsgTypes.Finish:
                EmitOnMain(() => HandleAIResposeFinish());
                break;

            case AudioMsgTypes.ReceiveText:
                {
                    var payload = env.Payload ?? "";
                    EmitOnMain(() => HandleAITranscript(payload.ToString()));
                    break;
                }

            case AudioMsgTypes.ReceiveAudio:
                {
                    var payload = env.Payload ?? "";
                    try
                    {
                        var bytes = Convert.FromBase64String(payload.ToString());
                        // 這裡在音訊執行緒上做轉換較便宜，但為簡化，直接主緒列隊後再丟 RX 也可。
                        EmitOnMain(() => HandleAIAudio(bytes));
                    }
                    catch { }
                    break;
                }

            case AudioMsgTypes.Logging:
                {
                    var payload = env.Payload ?? "";
                    EmitOnMain(() => HandleAILog(DebugLevel.Log, payload.ToString()));
                    break;
                }

            default:
                // 其他型別視需要擴充
                break;
        }
    }

    private async Task SendAsync(object payload)
    {
        if (!IsConnected()) return;

        string json;
        try
        {
            json = JsonConvert.SerializeObject(payload);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[WS] Serialize error: {ex.Message}");
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(json);

        //await _sendLock.WaitAsync(_cts.Token).ConfigureAwait(false);
        try
        {
            if (IsConnected() && !_cts.IsCancellationRequested)
            {
                await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[WS] Send error: {ex.Message}");
        }
        finally
        {
           // _sendLock.Release();
        }
    }

    public Task SendAudioBase64Async(string b64)
    {
        var msg = new AIMessage { Type = AudioMsgTypes.Send, Payload = b64 };
        return SendAsync(msg);
    }

    public Task SendInterruptAsync()
    {
        var msg = new AIMessage { Type = AudioMsgTypes.InterruptReceive, Payload = "" };
        return SendAsync(msg);
    }
}
