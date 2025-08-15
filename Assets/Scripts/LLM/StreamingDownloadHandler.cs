using System;
using System.Text;
using UnityEngine.Networking;

public class StreamingDownloadHandler : DownloadHandlerScript
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

        // 逐行吐給上層（支援 \n 或 \r\n）
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
