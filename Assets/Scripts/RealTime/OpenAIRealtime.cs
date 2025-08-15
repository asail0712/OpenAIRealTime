using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Newtonsoft.Json.Linq; // 用 NuGet 或 Unity Package Manager 安裝 JSON.NET

public class OpenAIRealtime : MonoBehaviour
{
    public string apiKey    = "sk-...";
    public string model     = "gpt-4o-mini-realtime-preview";
    public string voice     = "alloy";
    public int sampleRate   = 24000;
    public int channels     = 1;

    private ClientWebSocket ws;
    private CancellationTokenSource cts;
    private FloatRingBuffer ring;
    private AudioClip clip;
    private AudioSource source;
    private Action<string> finishAction;
    private Queue<string> deltaMessage;

    async void Start()
    {
        ring            = new FloatRingBuffer(sampleRate * channels * 10); // 10秒緩衝
        source          = gameObject.AddComponent<AudioSource>();
        clip            = AudioClip.Create("RealtimeClip", sampleRate * channels * 60, channels, sampleRate, true, OnPCMRead);
        source.clip     = clip;
        deltaMessage    = new Queue<string>();

        await Connect();
        await SendSessionUpdate();
        //await SendSpeak("請自我介紹");

        source.Play();
    }

    private void Update()
    {
        while(deltaMessage != null || deltaMessage.Count > 0)
        {
            string msg = deltaMessage.Dequeue();

            finishAction?.Invoke(msg);
        }
    }

    async Task Connect()
    {
        cts = new CancellationTokenSource();
        ws  = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", "Bearer " + apiKey);
        ws.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1");

        var uri = new Uri($"wss://api.openai.com/v1/realtime?model={model}");
        await ws.ConnectAsync(uri, cts.Token);

        _ = Task.Run(ReceiveLoop);
    }

    async Task SendSessionUpdate()
    {
        var json = new JObject
        {
            ["type"]    = "session.update",
            ["session"] = new JObject
            {
                ["modalities"]          = new JArray("audio", "text"),
                ["voice"]               = voice,
                ["output_audio_format"] = "pcm16"            // 要用字串，不是物件
            }
        };
        await SendJson(json.ToString());
    }

    public void StartToSpeak(string toSpeak = "這是按鈕觸發的 Realtime TTS 測試")
    {
        // 你可以用 Inspector 填字，或在這裡固定一段
        //string toSpeak = "這是按鈕觸發的 Realtime TTS 測試";

        // 因為 SendSpeak 是 async，所以用 Task.Run 或直接丟給 Unity 的 async context
        _ = SendSpeak(toSpeak);
    }

    public void ReplyAction(Action<string> finishAction)
    {
        this.finishAction = finishAction;
    }

    async Task SendSpeak(string text)
    {
        var json = new JObject
        {
            ["type"]        = "response.create",
            ["response"]    = new JObject
            {
                ["instructions"]    = text,
                ["modalities"]      = new JArray("audio", "text"),
                //["conversation"] = "none",         // 不延續上下文，單次播放
                //["temperature"] = 0.6         // 不要有隨機性
            }
        };
        await SendJson(json.ToString());
    }

    async Task SendJson(string json)
    {
        var buf = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(new ArraySegment<byte>(buf), WebSocketMessageType.Text, true, cts.Token);
    }

    async Task ReceiveLoop()
    {
        var buf = new byte[8192];
        var sb  = new StringBuilder();

        while (ws.State == WebSocketState.Open)
        {
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buf), cts.Token);
            
            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            sb.Append(Encoding.UTF8.GetString(buf, 0, result.Count));

            if (result.EndOfMessage)
            {
                string msg  = sb.ToString();
                sb.Length   = 0;
                HandleMessage(msg);
            }
        }
    }

    void HandleMessage(string msg)
    {
        try
        {
            var obj     = JObject.Parse(msg);
            string type = (string)obj["type"];

            Debug.Log(msg);

            switch (type)
            {
                case "response.audio.delta":
                    string b64 = (string)obj["delta"];
                    if (!string.IsNullOrEmpty(b64))
                    {
                        byte[] pcm      = Convert.FromBase64String(b64);
                        int sampleCount = pcm.Length / 2;
                        float[] samples = new float[sampleCount];
                        for (int i = 0; i < sampleCount; i++)
                        {
                            short s     = BitConverter.ToInt16(pcm, i * 2);
                            samples[i]  = s / 32768f;
                        }
                        ring.Write(samples, 0, sampleCount);
                    }
                    break;
                case "response.audio_transcript.delta":
                    string deltaText = (string)obj["delta"];
                    if (!string.IsNullOrEmpty(deltaText))
                    {
                        // 累加到暫存變數（方便最後合併成完整文字）
                        //currentText += deltaText;
                        deltaMessage.Enqueue(deltaText);

                        Debug.Log("[文字片段] " + deltaText);
                    }
                    break;
                case "error":
                    Debug.LogError(msg);
                    break;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("Parse fail: " + e);
        }
    }

    void OnPCMRead(float[] data)
    {
        int got = ring.Read(data, 0, data.Length);
        if (got < data.Length)
        {
            for (int i = got; i < data.Length; i++) data[i] = 0;
        }
    }

    // 簡單 ring buffer
    public class FloatRingBuffer
    {
        private float[] buf;
        private int w, r, count;
        public FloatRingBuffer(int cap) { buf = new float[cap]; }
        public int Write(float[] src, int off, int len)
        {
            int n = Mathf.Min(len, buf.Length - count);
            for (int i = 0; i < n; i++) { buf[w] = src[off + i]; w = (w + 1) % buf.Length; }
            count += n; return n;
        }
        public int Read(float[] dst, int off, int len)
        {
            int n = Mathf.Min(len, count);
            for (int i = 0; i < n; i++) { dst[off + i] = buf[r]; r = (r + 1) % buf.Length; }
            count -= n; return n;
        }
    }
}
