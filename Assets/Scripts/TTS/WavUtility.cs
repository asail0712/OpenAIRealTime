using UnityEngine;
using System;
using System.IO;

public static class WavUtility
{
    // 解析 WAV byte[] → AudioClip
    public static AudioClip ToAudioClip(byte[] wavFile, string clipName = "wav")
    {
        using (MemoryStream stream = new MemoryStream(wavFile))
        using (BinaryReader reader = new BinaryReader(stream))
        {
            // 讀 "RIFF"
            string riff = new string(reader.ReadChars(4));
            if (riff != "RIFF") throw new Exception("Invalid WAV file: missing RIFF");

            int chunkSize = reader.ReadInt32();
            string wave = new string(reader.ReadChars(4));
            if (wave != "WAVE") throw new Exception("Invalid WAV file: missing WAVE");

            // 找 fmt chunk
            string fmtID = new string(reader.ReadChars(4));
            int fmtSize = reader.ReadInt32();
            int audioFormat = reader.ReadInt16();
            int channels = reader.ReadInt16();
            int sampleRate = reader.ReadInt32();
            int byteRate = reader.ReadInt32();
            int blockAlign = reader.ReadInt16();
            int bitsPerSample = reader.ReadInt16();

            // 跳過剩下的 fmt bytes
            if (fmtSize > 16)
                reader.ReadBytes(fmtSize - 16);

            // 找 data chunk
            string dataID = new string(reader.ReadChars(4));
            while (dataID != "data")
            {
                // 跳過其他 chunk
                int skipSize = reader.ReadInt32();
                reader.ReadBytes(skipSize);
                dataID = new string(reader.ReadChars(4));
            }

            int dataSize = reader.ReadInt32();
            byte[] data = reader.ReadBytes(dataSize);

            // 轉成 float[]
            float[] samples = Convert16BitToFloat(data);

            // 建立 AudioClip
            AudioClip audioClip = AudioClip.Create(clipName, samples.Length / channels, channels, sampleRate, false);
            audioClip.SetData(samples, 0);
            return audioClip;
        }
    }

    // 16-bit PCM → float
    private static float[] Convert16BitToFloat(byte[] data)
    {
        int sampleCount = data.Length / 2;
        float[] floatArr = new float[sampleCount];

        for (int i = 0; i < sampleCount; i++)
        {
            short value = BitConverter.ToInt16(data, i * 2);
            floatArr[i] = value / 32768f;
        }

        return floatArr;
    }
}
