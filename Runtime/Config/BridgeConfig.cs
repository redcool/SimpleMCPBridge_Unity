using UnityEngine;
namespace SimpleMCPBridge
{

    [System.Serializable]
    public class BridgeConfig
    {
        public string serverIp = "127.0.0.1";
        public int serverPort = 45678;


        public static bool LoadConfig(out string ip, out int port, string configName = "bridge-config")
        {
            ip = "127.0.0.1";
            port = 45678;

            var textAsset = Resources.Load<TextAsset>(configName);
            if (string.IsNullOrEmpty(textAsset?.text))
            {
                return false;
            }
            var config = JsonUtility.FromJson<BridgeConfig>(textAsset.text);
            ip = config.serverIp;
            port = config.serverPort;

            return true;
        }
    }

}
