using UnityEngine;
using UnityEngine.UI;

public class RealtimeManager : MonoBehaviour
{
    [SerializeField] private OpenAIRealtime aiRealtime;
    [SerializeField] private InputField inputText;
    [SerializeField] private Button inputBtn;
    [SerializeField] private Text replyText;

    private string replyStr;

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        inputBtn.onClick.AddListener(() => 
        {            
            string inputStr = inputText.text;

            if (inputStr == "")
            {
                return;
            }

            replyStr = "";
            aiRealtime.ReplyAction((s) => 
            {
                replyStr        += s;
                replyText.text  = replyStr;
                replyText.SetAllDirty(); // 標記所有屬性需要重繪
                Canvas.ForceUpdateCanvases(); // 立即刷新
            });

            aiRealtime.StartToSpeak(inputStr);
        });
    }
}
