using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

public class OpenAI_TTS_Split : MonoBehaviour
{
    [Header("OpenAI")]
    [Tooltip("從 https://platform.openai.com 取得")]
    public string apiKey = "sk-...";

    [Tooltip("建議使用 gpt-4o-mini-tts")]
    public string model = "gpt-4o-mini-tts";

    [Tooltip("官方提供的 voice 名稱，例如 alloy, verse, aria...")]
    public string voice = "alloy";

    [Header("Audio")]
    public AudioSource audioSource;          // 指到場上的 AudioSource
    [Tooltip("每段文字最大字數（避免一次請求太大）")]
    public int chunkSize = 120;

    // ====== 測試用 ======
    [ContextMenu("Speak Demo")]
    private void SpeakDemo()
    {
        SpeakStreamed("請簡單自我介紹 謝謝!!請簡單自我介紹 謝謝!!請簡單自我介紹 謝謝!!請簡單自我介紹 謝謝!!請簡單自我介紹 謝謝!!");
    }

    void Reset()
    {
        audioSource = gameObject.AddComponent<AudioSource>();
        audioSource.playOnAwake = false;
    }

    // 對外：高層 API
    public void SpeakStreamed(string text)
    {
        StopAllCoroutines();
        StartCoroutine(SpeakRoutine(text));
    }

    IEnumerator SpeakRoutine(string text)
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            Debug.LogError("[TTS] API Key 未設定。");
            yield break;
        }

        // 1) 斷句：先以標點切，再做 chunkSize 控制
        var pieces = SplitTextSmart(text, chunkSize);

        foreach (var piece in pieces)
        {
            yield return StartCoroutine(FetchAndPlayClip(piece));
            // 可依需要調整段落間停頓
            yield return null;
        }
    }

    List<string> SplitTextSmart(string text, int maxLen)
    {
        // 以常見標點切段，再把過長的段落做二次切分
        char[] seps = new[] { '。', '！', '？', '，', '.', '!', '?', ';', '；', '\n' };
        var raw = text.Split(seps, StringSplitOptions.RemoveEmptyEntries)
                      .Select(s => s.Trim())
                      .Where(s => s.Length > 0)
                      .ToList();

        var result = new List<string>();
        foreach (var seg in raw)
        {
            if (seg.Length <= maxLen) { result.Add(seg); continue; }
            // 把過長段落再切
            for (int i = 0; i < seg.Length; i += maxLen)
                result.Add(seg.Substring(i, Math.Min(maxLen, seg.Length - i)));
        }
        return result;
    }

    IEnumerator FetchAndPlayClip(string piece)
    {
        // 2) 準備 JSON body（Audio API：/v1/audio/speech）
        var payload = new TTSRequest
        {
            model = model,
            voice = voice,
            input = piece,
            response_format = "pcm" // 請求直接回傳 WAV 二進位
        };
        string json = JsonUtility.ToJson(payload);

        var req = new UnityWebRequest("https://api.openai.com/v1/audio/speech", "POST");
        byte[] bodyRaw = Encoding.UTF8.GetBytes(json);
        req.uploadHandler = new UploadHandlerRaw(bodyRaw);
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        req.SetRequestHeader("Authorization", "Bearer " + apiKey);

        // 3) 發送請求
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[TTS] HTTP Error: {req.responseCode} {req.error}\n{req.downloadHandler.text}");
            yield break;
        }

        // 你可以把這些做成 Inspector 參數
        const int sampleRate = 24000;  // 常見預設：24kHz
        const int channels = 1;      // 常見預設：單聲道

        byte[] pcmBytes = req.downloadHandler.data;
        if (pcmBytes == null || pcmBytes.Length == 0)
        {
            Debug.LogError("[TTS] 收到空的 PCM 音訊資料。");
            yield break;
        }

        // 將 16-bit 小端 PCM 轉成 [-1,1] 的 float[]
        int sampleCount = pcmBytes.Length / 2; // 16-bit = 2 bytes
        float[] samples = new float[sampleCount];

        int p = 0;
        for (int i = 0; i < sampleCount; i++)
        {
            short s = (short)(pcmBytes[p] | (pcmBytes[p + 1] << 8));
            p += 2;
            samples[i] = Mathf.Clamp(s / 32768f, -1f, 1f);
        }

        // 產生並播放 AudioClip
        var clip = AudioClip.Create($"TTS_PCM_{Guid.NewGuid():N}",
                                    sampleCount / channels, channels, sampleRate, false);
        clip.SetData(samples, 0);

        // 串接播放（保持你原本的邏輯）
        if (audioSource.isPlaying)
        {
            while (audioSource.isPlaying && audioSource.timeSamples < audioSource.clip.samples - 1024)
                yield return null;
        }
        audioSource.clip = clip;
        audioSource.Play();
        while (audioSource.isPlaying) yield return null;
    }

    [Serializable]
    class TTSRequest
    {
        public string model;
        public string voice;
        public string input;
        public string response_format;
    }
}
