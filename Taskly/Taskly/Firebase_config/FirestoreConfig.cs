// FirestoreConfig.cs - Konfigurace a správa připojení k Firebase Firestore databázi
using Google.Cloud.Firestore;
namespace Taskly.Firebase_config
{
    // Statická třída poskytující přístup k Firebase Firestore databázi
    public static class FirestoreConfig
    {
        // KONFIGURACE FIREBASE
        // Singleton instance Firestore databáze
        private static FirestoreDb? firestoreDb;
        // ID projektu v Firebase Console
        private const string ProjectId = "taskly-4b495";

        // PŘÍSTUP K DATABÁZI
        // Vracíme instanci Firebase Firestore databáze - vytvoří ji, pokud ještě neexistuje
        public static FirestoreDb GetFirestoreDatabase()
        {
            if (firestoreDb == null)
            {
                try
                {
                    // Použijeme specifickou cestu k JSON kredenciálům podle platformy
#if ANDROID
                    // Na Androidu ukládáme kredenciály do cache adresáře
                    string credentialsPath = Path.Combine(FileSystem.CacheDirectory, "firebase-credentials.json");
#else
                    // Na ostatních platformách používáme standardní umístění
                    string credentialsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Firebase_config/firebase-credentials.json");
#endif

                    // Kontrola existence a validity credentials souboru
                    ValidateCredentialsFile(credentialsPath);

                    // Nastavení proměnné prostředí pro autentizaci Google Cloud
                    Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS", credentialsPath);
                    // Vytvoříme instance Firestore databáze s naším projektem ID
                    firestoreDb = FirestoreDb.Create(ProjectId);
                }
                catch (InvalidOperationException)
                {
                    // Pokud šlo o námi vyhozenou výjimku, předáme ji dál beze změny
                    throw;
                }
                catch (Exception ex)
                {
                    // Při jiném selhání vyhodíme výjimku s informativní zprávou
                    throw new InvalidOperationException("Nepodařilo se inicializovat Firebase Firestore databázi.", ex);
                }
            }
            // Vrátíme existující nebo nově vytvořenou instanci
            return firestoreDb;
        }

        // Pomocná meotda - Kontrola validity credentials souboru
        private static void ValidateCredentialsFile(string credentialsPath)
        {
            // Kontrola existence souboru
            if (!File.Exists(credentialsPath))
            {
                throw new InvalidOperationException(
                    "FIREBASE CREDENTIALS SOUBOR NENALEZEN!\n\n" +
                    $"Soubor neexistuje: {credentialsPath}\n\n" +
                    "Pro spuštění aplikace:\n" +
                    "1. Vytvořte Firebase projekt na console.firebase.google.com\n" +
                    "2. Aktivujte Firestore Database\n" +
                    "3. Stáhněte firebase-credentials.json z Project Settings → Service Accounts\n" +
                    "4. Umístěte soubor do Firebase_config/firebase-credentials.json\n\n" +
                    "Pro hodnotitele bakalářské práce: Kontaktujte autora pro testovací credentials nebo si je nahraďte vlastními"
                );
            }

            // Kontrola obsahu souboru - jestli neobsahuje placeholder hodnoty
            try
            {
                string credentialsContent = File.ReadAllText(credentialsPath);

                if (credentialsContent.Contains("nahraďte-") ||
                    credentialsContent.Contains("váš-projekt") ||
                    credentialsContent.Contains("vlastním-"))
                {
                    throw new InvalidOperationException(
                        "FIREBASE NENÍ NAKONFIGUROVÁN! \n\n" +
                        "Credentials soubor obsahuje placeholder hodnoty.\n\n" +
                        "Pro spuštění aplikace:\n" +
                        "1. Vytvořte Firebase projekt na console.firebase.google.com\n" +
                        "2. Aktivujte Firestore Database\n" +
                        "3. Stáhněte firebase-credentials.json z Project Settings → Service Accounts\n" +
                        "4. Nahraďte placeholder hodnoty reálnými údaji z Firebase\n" +
                        "5. Také nahraďte hodnoty v resources/raw/appsettings.json\n\n" +
                        "Pro hodnotitele bakalářské práce: Kontaktujte autora pro testovací credentials nebo si je nahraďte vlastními"
                    );
                }
            }
            catch (InvalidOperationException)
            {
               
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Nepodařilo se přečíst credentials soubor: {credentialsPath}", ex);
            }
        }
    }
}