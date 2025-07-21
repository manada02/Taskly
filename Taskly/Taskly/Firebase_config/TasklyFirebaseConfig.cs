// TasklyFirebaseConfig.cs - Konfigurační model pro Firebase služby v aplikaci Taskly
using Firebase.Auth;

namespace Taskly.Firebase_config
{
    // Tato třída obsahuje konfigurační údaje pro připojení k Firebase službám
    public class TasklyFirebaseConfig
    {
        // KONFIGURAČNÍ VLASTNOSTI

        // API klíč pro přístup k Firebase službám
        public string ApiKey { get; set; } = string.Empty;

        // Doména pro Firebase Authentication
        public string AuthDomain { get; set; } = string.Empty;

        // ID projektu v Firebase Console
        public string ProjectId { get; set; } = string.Empty;

        // Název úložiště pro Firebase Storage
        public string StorageBucket { get; set; } = string.Empty;
    }
}

