using System;
using System.Text;
using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

[Serializable]
public class ChatMessage
{
    public string role;
    public string content;
    public ChatMessage(string role, string content) { this.role = role; this.content = content; }
}

public class GPTStreamer : MonoBehaviour
{
    [Header("請填入 OpenAI API Key（建議用環境變數/遠端配置，勿硬編）")]
    [SerializeField] private string openAIApiKey = "";

    [Header("模型名稱，例如 gpt-4o / gpt-4o-mini")]
    [SerializeField] private string model = "gpt-4o";

    private UnityWebRequest _inflight; // 用於中止

    /// <summary>
    /// 啟動一個流式請求（SSE）。每次收到 token 會呼叫 onToken；完成時呼叫 onDone；錯誤時呼叫 onError。
    /// </summary>
    public IEnumerator ChatStreamCoroutine(
        IList<ChatMessage> messages,
        Action<string> onToken,
        Action onDone = null,
        Action<string> onError = null)
    {
        if (string.IsNullOrWhiteSpace(openAIApiKey))
        {
            onError?.Invoke("OpenAI API Key 未設定。");
            yield break;
        }

        var body = new
        {
            model = model,
            messages = messages,
            stream = true
        };

        string json = JsonConvert.SerializeObject(body);
        byte[] payload = Encoding.UTF8.GetBytes(json);

        _inflight = new UnityWebRequest("https://api.openai.com/v1/chat/completions", "POST");
        _inflight.uploadHandler = new UploadHandlerRaw(payload);
        _inflight.downloadHandler = new StreamingDownloadHandler(line =>
        {
            // 忽略 keep-alive 或註解（SSE 可能出現以 ":" 開頭）
            if (string.IsNullOrEmpty(line) || line.StartsWith(":")) return;

            // 只處理 data: 行
            if (!line.StartsWith("data:")) return;

            string data = line.Substring(5).Trim();
            if (data == "[DONE]")
            {
                // 最終行，不在這裡結束 Coroutine，交由外層 onDone
                return;
            }

            try
            {
                JObject parsed = JObject.Parse(data);
                // chat.completions SSE 格式：choices[0].delta.content
                string token = parsed["choices"]?[0]?["delta"]?["content"]?.ToString();
                if (!string.IsNullOrEmpty(token))
                    onToken?.Invoke(token);
            }
            catch (Exception ex)
            {
                onError?.Invoke($"JSON 解析錯誤：{ex.Message}\n原始：{data}");
            }
        });

        _inflight.SetRequestHeader("Authorization", $"Bearer {openAIApiKey}");
        _inflight.SetRequestHeader("Content-Type", "application/json");
        _inflight.SetRequestHeader("Accept", "text/event-stream");

        yield return _inflight.SendWebRequest();

        if (_inflight.result != UnityWebRequest.Result.Success)
        {
            onError?.Invoke($"HTTP 錯誤：{_inflight.responseCode} - {_inflight.error}");
        }
        else
        {
            onDone?.Invoke();
        }

        _inflight.Dispose();
        _inflight = null;
    }

    /// <summary>
    /// 方便呼叫：只給 user 的一句話就開始（會自帶 system 提示）。
    /// </summary>
    public Coroutine StartUserPrompt(string userText, Action<string> onToken, Action onDone = null, Action<string> onError = null)
    {
        var msgs = new List<ChatMessage>
        {
            new ChatMessage("system", "你是一位助理。"),
            new ChatMessage("user",   userText)
        };
        return StartCoroutine(ChatStreamCoroutine(msgs, onToken, onDone, onError));
    }

    /// <summary>
    /// 取消目前的流式請求（若有）。
    /// </summary>
    public void Abort()
    {
        if (_inflight != null && !_inflight.isDone)
        {
            _inflight.Abort();
            _inflight.Dispose();
            _inflight = null;
        }
    }
}
