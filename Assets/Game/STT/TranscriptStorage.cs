using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace Game.STT
{
    public sealed class STTSessionStorage
    {
        private readonly DateTime _createdAt;

        public string RootDirectory { get; }
        public string SessionId { get; }
        public string SessionDirectory { get; }
        public string ResultTxtPath { get; }
        public string PublicRelativeDirectory { get; }
        public string PublicFolderDisplayPath { get; }
        public string PublicResultDisplayPath { get; }

        public STTSessionStorage(string language)
        {
            _createdAt = DateTime.Now;
            RootDirectory = BuildExternalRootDirectory();
            SessionId = "session_" + _createdAt.ToString("yyyyMMdd_HHmmss");
            SessionDirectory = Path.Combine(RootDirectory, "Sessions", SessionId);
            ResultTxtPath = Path.Combine(SessionDirectory, "result.txt");
            PublicRelativeDirectory = "Documents/PartTime/STT/" + SessionId;
            PublicFolderDisplayPath = "Internal Storage/" + PublicRelativeDirectory;
            PublicResultDisplayPath = PublicFolderDisplayPath + "/result.txt";

            Directory.CreateDirectory(RootDirectory);
            Directory.CreateDirectory(SessionDirectory);
            EnsureSessionFile(language);
        }

        public static string BuildExternalRootDirectory()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            using AndroidJavaClass unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using AndroidJavaObject activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
            using AndroidJavaObject documentsDir = activity.Call<AndroidJavaObject>("getExternalFilesDir", "Documents");
            string documentsPath = documentsDir.Call<string>("getAbsolutePath");
            return Path.Combine(documentsPath, "PartTime", "STT");
#else
            return Path.Combine(Application.persistentDataPath, "PartTime", "STT");
#endif
        }

        public void EnsureSessionFile(string language)
        {
            if (File.Exists(ResultTxtPath)) return;

            string text =
                $"SessionId: {SessionId}\n" +
                $"CreatedAt: {_createdAt:yyyy-MM-dd HH:mm:ss}\n" +
                $"Language: {language}\n\n";
            File.WriteAllText(ResultTxtPath, text, Encoding.UTF8);
        }
    }
}
