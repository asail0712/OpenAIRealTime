using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// Refactored & trimmed version based on user's original file.
/// Focus: clear responsibilities, minimal state, safe defaults, and readable flow.
/// </summary>
public class OpenAIRealtime : IDisposable
{
    // ===============================
    // Config (Inspector)
    // ===============================
    public string openAIApiKey      = string.Empty;
    public string model             = "gpt-4o-mini-realtime-preview";
    public string voice             = "alloy";
    public string basicInstructions = "You are a helpful, concise voice assistant.";
    public bool bAutoCreateResponse = false;

    // ===============================
    // Internals - WS
    // ===============================
    private ClientWebSocket _ws;
    private CancellationTokenSource _cts;
    private Uri _uri;
    private volatile bool _connected;    
    private readonly StringBuilder _userTranscript = new();     // text/ASR    
    private readonly byte[] _recvBuffer = new byte[1 << 16];    // buffers 64KB    
    private volatile bool _responseInFlight;                    // response lifecycle (simple)

    // 保證 SendAsync 串行化
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    // 把背景執行緒事件丟回主執行緒
    private readonly ConcurrentQueue<Action> _mainThreadActions = new();
    
    // events
    public event Action<string> OnUserTranscriptDone;
    public event Action<string> OnAssistantTextDelta;
    public event Action<string> OnAssistantTextDone;
    public event Action<float> OnAssistantAudioDelta;
    public event Action<byte[]> OnAssistantAudioDone;

    // ===============================
    // lifecycle
    // ===============================    
    public OpenAIRealtime(string openAIApiKey, string model, string voice, string basicInstructions, bool bAutoCreateResponse)
    {
        this.openAIApiKey           = openAIApiKey;
        this.model                  = model;
        this.voice                  = voice;
        this.basicInstructions      = basicInstructions;
        this.bAutoCreateResponse    = bAutoCreateResponse;
    }

    public void Update()
    {
        while (_mainThreadActions.TryDequeue(out var action))
        {
            action?.Invoke();
        }
    }

    public async void Dispose()
    {        
        try
        {
            if (_ws != null && _ws.State == WebSocketState.Open)
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
        }
        catch { }

        _cts?.Cancel();
    }

    // ===============================
    // Connect & session
    // ===============================
    public async Task ConnectAndConfigure()
    {
        _uri    = new Uri($"wss://api.openai.com/v1/realtime?model={model}");
        _ws     = new ClientWebSocket();
        _ws.Options.SetRequestHeader("Authorization", $"Bearer {openAIApiKey}");
        _ws.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1");

        _cts    = new CancellationTokenSource();

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

            _ = ReceiveLoop();
        }
    }

    public Task SendAudioBase64Async(string audioBase64)
    {
        if (string.IsNullOrEmpty(audioBase64)) return Task.CompletedTask;
        return SendAsync(new { type = "input_audio_buffer.append", audio = audioBase64 });
    }

    public async Task SendAsync(object payload)
    {
        if (_ws == null || _ws.State != WebSocketState.Open)
        {
            return;
        }

        string json = JsonConvert.SerializeObject(payload);
        var bytes   = Encoding.UTF8.GetBytes(json);

        await _sendLock.WaitAsync().ConfigureAwait(false);

        try
        {
            if (_ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* ignore on shutdown */ }
        catch (WebSocketException wse) { Debug.LogWarning($"Send error: {wse.Message}"); }
        finally
        {
            _sendLock.Release();
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
                        _connected = false; 
                        break;
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
            case "response.audio_transcript.delta":
            case "response.output_text.delta":
            case "response.text.delta":
                {
                    string d = (string)jo["delta"];
                    if (!string.IsNullOrEmpty(d))
                    {
                        textBuilder.Append(d);
                    }
                    
                    string txt = textBuilder.ToString();
                    if (!string.IsNullOrEmpty(txt))
                    {
                        EmitOnMain(() => OnAssistantTextDelta?.Invoke(txt));
                    }
                    Debug.Log($"ASSISTANT TEXT DELTA: {txt}");

                    return;
                }
            case "response.audio_transcript.done":
            case "response.output_text.done":
            case "response.text.done":
                {
                    string txt = textBuilder.ToString();
                    if (!string.IsNullOrEmpty(txt))
                    {
                        EmitOnMain(() => OnAssistantTextDone?.Invoke(txt));
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
                            EmitOnMain(() => OnAssistantAudioDelta?.Invoke(f));
                        }
                    }
                    catch (Exception e) { Debug.LogWarning($"Audio delta decode error: {e.Message}"); }
                    return;
                }
            case "response.output_audio.done":
                {
                    EmitOnMain(() => OnAssistantAudioDone?.Invoke(System.Array.Empty<byte>()));
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
                        EmitOnMain(() => OnUserTranscriptDone?.Invoke(text));
                        Debug.Log($"USER TRANSCRIPT: {text}");
                    }
                    _userTranscript.Clear();
                    if (bAutoCreateResponse && !_responseInFlight)
                    {
                        _ = SendAsync(new { type = "input_audio_buffer.commit" });
                        // 建立回應 + 指令
                        _ = SendAsync(new
                        {
                            type        = "response.create",
                            response    = new
                            {
                                instructions = basicInstructions
                            }
                        });
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

    private void EmitOnMain(Action action)
    {
        if (action == null) return;

        _mainThreadActions.Enqueue(action);
    }

    public async Task SendTextAsync(string inst)
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
                instructions    = inst
            }
        };

        await SendAsync(create);
    }

    public bool IsConnect()
    {
        return _ws != null && _ws.State == WebSocketState.Open && _connected;
    }
}
