using System;
using System.Text;
using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;


public class GPTNonStream : MonoBehaviour
{
    [Header("請填入 OpenAI API Key（建議用環境變數/遠端配置，勿硬編）")]
    [SerializeField] private string openAIApiKey = "";

    [Header("模型名稱，例如 gpt-4o / gpt-4o-mini")]
    [SerializeField] private string model = "gpt-4o";

    /// <summary>
    /// 呼叫 ChatGPT 並一次性取得完整回覆
    /// </summary>
    public IEnumerator ChatOnceCoroutine(
        IList<ChatMessage> messages,
        Action<string> onComplete,
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
            messages = messages
            // 沒有 stream:true
        };

        string json = JsonConvert.SerializeObject(body);
        byte[] payload = Encoding.UTF8.GetBytes(json);

        using (UnityWebRequest request = new UnityWebRequest("https://api.openai.com/v1/chat/completions", "POST"))
        {
            request.uploadHandler = new UploadHandlerRaw(payload);
            request.downloadHandler = new DownloadHandlerBuffer();

            request.SetRequestHeader("Authorization", $"Bearer {openAIApiKey}");
            request.SetRequestHeader("Content-Type", "application/json");

            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                onError?.Invoke($"HTTP 錯誤：{request.responseCode} - {request.error}");
                yield break;
            }

            try
            {
                JObject parsed = JObject.Parse(request.downloadHandler.text);
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
    }

    /// <summary>
    /// 方便呼叫：只給 user 的一句話就開始（會自帶 system 提示）
    /// </summary>
    public Coroutine StartUserPrompt(string userText, Action<string> onComplete, Action<string> onError = null)
    {
        var msgs = new List<ChatMessage>
        {
            new ChatMessage("system", "你是一位助理。"),
            new ChatMessage("user",   userText)
        };
        return StartCoroutine(ChatOnceCoroutine(msgs, onComplete, onError));
    }
}
