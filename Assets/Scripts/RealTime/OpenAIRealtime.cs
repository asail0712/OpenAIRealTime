using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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
    private volatile bool bConnected;    
    private readonly StringBuilder _userTranscript  = new();                // text/ASR    
    private readonly byte[] _recvBuffer             = new byte[1 << 16];    // buffers 64KB    
    private volatile bool bResponseInFlight;                                // response lifecycle (simple)

    // 保證 SendAsync 串行化
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    // 把背景執行緒事件丟回主執行緒
    private readonly ConcurrentQueue<Action> _mainThreadActions = new();

    // life cycle
    private bool bDisposed = false;

    // 當前response類別欄位
    private string _activeAssistantItemId;
    private int? _activeAudioContentIndex;
    private string _squelchItemId; // 要丟棄的舊 item（本地消音用）

    // events
    public event Action OnResposeStart;
    public event Action OnResposeFinish;
    public event Action<string> OnUserTranscriptDone;
    public event Action<string> OnAssistantTextDelta;
    public event Action<string> OnAssistantTextDone;
    public event Action<float[]> OnAssistantAudioDelta;
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

    public void Dispose()
    {
        if (bDisposed) return;
        bDisposed = true;

        try
        {
            // 最後一道防線：盡量避免在 UI/主緒卡住
            DisposeAsync().ConfigureAwait(false).GetAwaiter().GetResult();
        }
        catch { /* 最終防呆，避免例外往外拋 */ }
    }

    public async Task DisposeAsync()
    {
        if (bDisposed) return;

        // 停新活
        bConnected = false;

        try
        {
            if (_ws != null && _ws.State == WebSocketState.Open)
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", closeCts.Token).ConfigureAwait(false);
            }
        }
        catch { }
        finally
        {
            _ws.Dispose();
            _ws = null;

            _cts?.Cancel();
            _cts = null;
        }        
    }

    // ===============================
    // Connect & session
    // ===============================
    public async Task<bool> ConnectAndConfigure()
    {
        _uri    = new Uri($"wss://api.openai.com/v1/realtime?model={model}");
        _ws     = new ClientWebSocket();
        _ws.Options.SetRequestHeader("Authorization", $"Bearer {openAIApiKey}");
        _ws.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1");

        _cts    = new CancellationTokenSource();

        try
        {
            await _ws.ConnectAsync(_uri, _cts.Token);
            bConnected = _ws.State == WebSocketState.Open;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Error] Realtime connect failed: {ex.Message}");
            bConnected = false; 
        }

        if (bConnected)
        {
            // Minimal and valid session params
            await SendAsync(new
            {
                type    = "session.update",
                session = new
                {
                    input_audio_format  = "pcm16",
                    output_audio_format = "pcm16",
                    turn_detection      = new
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

        return bConnected;
    }

    public Task SendAudioBase64Async(string audioBase64)
    {
        if (string.IsNullOrEmpty(audioBase64)) return Task.CompletedTask;
        return SendAsync(new { type = "input_audio_buffer.append", audio = audioBase64 });
    }

    public Task InterruptAsync()
    {
        // 取消正在產生中（語音/文字）的回覆
        return SendAsync(new { type = "response.cancel" }); // 無參數即可
    }

    // audioEndMs：你本地端實際「已播放」到的毫秒數（用播放過的 sample 數換算）
    public Task TruncateAsync(int audioEndMs, int? contentIndex = null)
    {
        if (string.IsNullOrEmpty(_activeAssistantItemId)) return Task.CompletedTask;

        int idx = contentIndex ?? _activeAudioContentIndex ?? 0; // 最後才退回 0

        return SendAsync(new
        {
            type            = "conversation.item.truncate",
            item_id         = _activeAssistantItemId,
            content_index   = idx,
            audio_end_ms    = Math.Max(0, audioEndMs)
        });
    }

    /************************************
     * 中斷當前response
     * *********************************/
    public async Task BargeInAsync(float playedSeconds)
    {
        // 把秒轉毫秒
        int playedMsSoFar = (int)Math.Round(playedSeconds * 1000f);

        // 1) 停止助理的語音生成
        await InterruptAsync().ConfigureAwait(false);

        // 2) 告訴伺服器「我只聽到這裡」
        await TruncateAsync(playedMsSoFar).ConfigureAwait(false);

        // 3) 本地丟棄舊 item 的後續 delta
        _squelchItemId = _activeAssistantItemId;                              

        // 4) 清空本地播放緩衝，避免播放殘留
        EmitOnMain(() => OnAssistantAudioDelta?.Invoke(Array.Empty<float>()));

        Console.WriteLine($"[Log] Barge-in triggered at {playedMsSoFar} ms");
    }

    public async Task SendTextAsync(string inst)
    {
        if (!bConnected)
        {
            return;
        }

        var create              = new
        {
            type                = "response.create",
            response            = new
            {
                modalities      = new[] { "text", "audio" },
                instructions    = inst
            }
        };

        await SendAsync(create);
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
        catch (WebSocketException wse) { Console.WriteLine($"[Warning] Send error: {wse.Message}"); }
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

        while (bConnected && _ws.State == WebSocketState.Open)
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
                        Console.WriteLine($"[Warning] Realtime closed: {res.CloseStatus} {res.CloseStatusDescription}");
                        bConnected = false; 
                        break;
                    }
                    sb.Append(Encoding.UTF8.GetString(_recvBuffer, 0, res.Count));
                }
                while (!res.EndOfMessage);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Warning] Receive error: {ex.Message}");
                break;
            }

            if (!bConnected)
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
            if (jsonLine.Contains("\"error\"")) Console.WriteLine($"[Error] SERVER ERROR (raw): {jsonLine}");
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
                bResponseInFlight = true;

                EmitOnMain(() => OnResposeStart?.Invoke());
                return;
            case "response.completed":
            case "response.done":
                bResponseInFlight = false;

                EmitOnMain(() => OnResposeFinish?.Invoke());
                return;
            case "response.output_item.added":
                {
                    _activeAssistantItemId      = (string)jo["item"]?["id"];
                    _activeAudioContentIndex    = null;                     // 直到看到音訊 delta 才知道 index
                    _squelchItemId              = null;                     // 新 item 開始，取消消音
                    return;
                }
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
                    Console.WriteLine($"[Log] ASSISTANT TEXT DELTA: {txt}");

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
                    Console.WriteLine($"[Log] ASSISTANT TEXT: {txt}");
                    textBuilder.Clear();
                    return;
                }

            // --- Audio stream ---
            case "response.output_audio.delta":
            case "response.audio.delta":
                {
                    // 先丟棄被消音的舊 item
                    var itemId = (string)jo["item_id"];
                    if (!string.IsNullOrEmpty(_squelchItemId) && _squelchItemId == itemId) return;

                    // 紀錄 audio 的 content_index
                    if (jo["content_index"] != null)
                    {
                        _activeAudioContentIndex = (int?)jo["content_index"];
                    }

                    // 讀取音頻內容
                    string b64 = (string)jo["delta"];
                    if (string.IsNullOrEmpty(b64))
                    {
                        return;
                    }

                    try
                    {
                        var bytes       = Convert.FromBase64String(b64);
                        int sampleCount = bytes.Length / 2;
                        var block       = new float[sampleCount];

                        for (int i = 0, si = 0; i < bytes.Length; i += 2, si++)
                        {
                            short s = (short)(bytes[i] | (bytes[i + 1] << 8));
                            block[si] = s / 32768f; // mono 24k
                        }

                        EmitOnMain(() => OnAssistantAudioDelta?.Invoke(block));
                    }
                    catch (Exception e) { Console.WriteLine($"[Warning] Audio delta decode error: {e.Message}"); }
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
                        Console.WriteLine($"[Log] USER TRANSCRIPT: {text}");
                    }
                    _userTranscript.Clear();
                    if (bAutoCreateResponse && !bResponseInFlight)
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
                    Console.WriteLine($"[Error] SERVER ERROR: code={code}, message={msg}\n{jsonLine}");
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

    public bool IsConnect()
    {
        return _ws != null && _ws.State == WebSocketState.Open && bConnected;
    }
}
