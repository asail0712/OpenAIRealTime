using System;
using UnityEngine;
using UnityEngine.UI;

public class ChatItem : MonoBehaviour
{
    [SerializeField] Text titleText;
    [SerializeField] Text replayTimeText;
    [SerializeField] Text contentText;
    [SerializeField] Toggle streamingToggle;

    [SerializeField] string modelName;    

    private GPTClient client;

    public void SetClient(GPTClient client)
    {
        this.client         = client;
        client.model        = modelName;
        titleText.text      = modelName;// + (streamingToggle.isOn? "_streaming":"");
        replayTimeText.text = "--";

        streamingToggle.onValueChanged.AddListener((b) => 
        {
            ChangeTitle(b);
        });
    }

    public void Query(string str)
    {
        if (client == null)
        {
            return;
        }

        string resultMsg    = "";
        float startTime     = Time.time;

        if (streamingToggle.isOn)
        {
            client.StartUserPrompt(str, true, (token) =>
            {
                if (resultMsg == "")
                {
                    replayTimeText.text = "等待 " + (Time.time - startTime).ToString("0.00") + "秒回覆";
                }

                resultMsg += token;
                contentText.text = resultMsg;

            });
        }
        else
        {
            client.StartUserPrompt(str, false, null, (reply) =>
            {
                replayTimeText.text = "等待 " + (Time.time - startTime).ToString("0.00") + "秒回覆";
                contentText.text    = reply;
            });
        }
    }

    private void ChangeTitle(bool b)
    {
        titleText.text = modelName;// + (b ? "_streaming" : "");
    }

    private void OnValidate()
    {
        ChangeTitle(streamingToggle.isOn);
    }
}
