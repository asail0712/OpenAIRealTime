using System.IO;
using Unity.VisualScripting;
using Unity.VisualScripting.Antlr3.Runtime;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;


public class ChatManager : MonoBehaviour
{
    [SerializeField] private string openAIApiKey = "";
    [SerializeField] private GameObject uiRoot;
    [SerializeField] private string queryStr;
    [SerializeField] private Text queryText;

    private ChatItem[] itemList;

    public void Start()
    {
        itemList = uiRoot.GetComponentsInChildren<ChatItem>();

        foreach(ChatItem item in itemList)
        {
            GPTClient client    = this.AddComponent<GPTClient>();
            client.openAIApiKey = openAIApiKey;

            item.SetClient(client);
        }

        queryText.text = queryStr;
    }

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    public void StartTalk()
    {
        foreach (ChatItem item in itemList)
        {
            item.Query(queryStr);
        }
    }
}
