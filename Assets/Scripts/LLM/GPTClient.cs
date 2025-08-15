using System;
using System.Text;
using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// 單一類別：支援 Chat Completions 的 Stream 與 Non-Stream 切換
/// 用法：StartCoroutine(SendChat(messages, stream: true/false, ...));
/// </summary>
public class GPTClient : MonoBehaviour
{
    [Header("請填入 OpenAI API Key（建議用環境變數/遠端配置）")]
    [SerializeField] public string openAIApiKey = "";

    [Header("模型名稱，例如 gpt-4o / gpt-4o-mini")]
    [SerializeField] public string model = "gpt-4o";

    private UnityWebRequest _inflight; // 目前進行中的請求（可用來 Abort）

    // ---- 巢狀型別：訊息結構 ----
    [Serializable]
    public class ChatMessage
    {
        public string role;
        public string content;
        public ChatMessage(string role, string content) { this.role = role; this.content = content; }
    }

    // ---- 巢狀型別：SSE 逐行下載處理器（僅在 stream=true 使用） ----
    private class StreamingDownloadHandler : DownloadHandlerScript
    {
        private readonly StringBuilder _buf = new StringBuilder(8 * 1024);
        private readonly Action<string> _onLine;

        public StreamingDownloadHandler(Action<string> onLine, int bufferSize = 4096)
            : base(new byte[bufferSize])
        {
            _onLine = onLine;
        }

        protected override bool ReceiveData(byte[] data, int dataLength)
        {
            if (data == null || dataLength <= 0) return false;

            string chunk = Encoding.UTF8.GetString(data, 0, dataLength);
            _buf.Append(chunk);

            // 逐行吐出：支援 \n 或 \r\n
            while (true)
            {
                string cur = _buf.ToString();
                int nl = cur.IndexOf('\n');
                if (nl == -1) break;

                string line = cur.Substring(0, nl).TrimEnd('\r');
                _buf.Remove(0, nl + 1);

                if (!string.IsNullOrEmpty(line))
                    _onLine?.Invoke(line);
            }
            return true;
        }

        protected override void CompleteContent()
        {
            string tail = _buf.ToString().Trim();
            if (!string.IsNullOrEmpty(tail))
                _onLine?.Invoke(tail);
            _buf.Clear();
        }
    }

    /// <summary>
    /// 單一 API：根據 bool stream 切換流程。
    /// - stream = true：逐字回傳（onToken 每次有片段時觸發，onComplete 最終觸發一次）
    /// - stream = false：一次性回傳（onComplete 收到完整回應）
    /// </summary>
    public IEnumerator SendChat(
        IList<ChatMessage> messages,
        bool stream,
        Action<string> onToken = null,
        Action<string> onComplete = null,
        Action<string> onError = null)
    {
        if (string.IsNullOrWhiteSpace(openAIApiKey))
        {
            onError?.Invoke("OpenAI API Key 未設定。");
            yield break;
        }

        // 組請求 body
        object body = stream
            ? new { model = model, messages = messages, stream = true }
            : new { model = model, messages = messages }; // 不帶 stream

        string json = JsonConvert.SerializeObject(body);
        byte[] payload = Encoding.UTF8.GetBytes(json);

        _inflight = new UnityWebRequest("https://api.openai.com/v1/chat/completions", "POST");
        _inflight.uploadHandler = new UploadHandlerRaw(payload);

        if (stream)
        {
            // Stream：使用自訂 DownloadHandlerScript 逐行解析 SSE
            _inflight.downloadHandler = new StreamingDownloadHandler(line =>
            {
                // 忽略 keep-alive 或註解
                if (string.IsNullOrEmpty(line) || line.StartsWith(":")) return;

                // 只處理 data:
                if (!line.StartsWith("data:")) return;

                string data = line.Substring(5).Trim();
                if (data == "[DONE]") return;

                try
                {
                    JObject parsed = JObject.Parse(data);
                    string token = parsed["choices"]?[0]?["delta"]?["content"]?.ToString();
                    if (!string.IsNullOrEmpty(token))
                        onToken?.Invoke(token);
                }
                catch (Exception ex)
                {
                    onError?.Invoke($"JSON 解析錯誤：{ex.Message}\n原始：{data}");
                }
            });

            // SSE 友善
            _inflight.SetRequestHeader("Accept", "text/event-stream");
        }
        else
        {
            // Non-Stream：一次拿完整字串
            _inflight.downloadHandler = new DownloadHandlerBuffer();
        }

        _inflight.SetRequestHeader("Authorization", $"Bearer {openAIApiKey}");
        _inflight.SetRequestHeader("Content-Type", "application/json");

        yield return _inflight.SendWebRequest();

        if (_inflight.result != UnityWebRequest.Result.Success)
        {
            onError?.Invoke($"HTTP 錯誤：{_inflight.responseCode} - {_inflight.error}");
        }
        else
        {
            if (!stream)
            {
                // Non-Stream：解析完整回應
                try
                {
                    JObject parsed = JObject.Parse(_inflight.downloadHandler.text);
                    string content = parsed["choices"]?[0]?["message"]?["content"]?.ToString();
                    if (!string.IsNullOrEmpty(content))
                        onComplete?.Invoke(content.Trim());
                    else
                        onError?.Invoke("回應中沒有 content 欄位。");
                }
                catch (Exception ex)
                {
                    onError?.Invoke($"JSON 解析錯誤：{ex.Message}");
                }
            }
            else
            {
                // Stream：完整結束
                onComplete?.Invoke("[STREAM_DONE]");
            }
        }

        _inflight.Dispose();
        _inflight = null;
    }

    /// <summary>
    /// 方便呼叫：只給 user 的一句話就開跑（自帶 system）。
    /// </summary>
    public Coroutine StartUserPrompt(string userText, bool stream,
        Action<string> onToken = null, Action<string> onComplete = null, Action<string> onError = null)
    {
        var msgs = new List<ChatMessage>
        {
            new ChatMessage("system", "你是一位助理。"),
            new ChatMessage("user",   userText)
        };
        return StartCoroutine(SendChat(msgs, stream, onToken, onComplete, onError));
    }

    /// <summary>中止目前的流式請求（若有）。</summary>
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
