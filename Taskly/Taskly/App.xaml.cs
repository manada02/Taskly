// App.xaml.cs - Hlavní třída aplikace Taskly definující chování a životní cyklus
using Taskly.Services.Auth;
using Taskly.Services.Notification.LocalNotification;

namespace Taskly
{
    public partial class App : Application
    {
        // PROMĚNNÉ A ZÁVISLOSTI

        // Služba pro autentizaci uživatele
        private readonly FirebaseAuthService _authService;

        // Příznak pro sledování, zda byla obnovena uživatelská relace
        private bool _sessionRestored = false;

        // Čas poslední kontroly notifikací
        private DateTime _lastNotificationCheck = DateTime.MinValue;

        // INICIALIZACE

        // Konstruktor aplikace - inicializuje komponenty a služby
        public App(FirebaseAuthService authService)
        {
            InitializeComponent();
            _authService = authService;

            // Ukládáme informaci o platformě do globálních preferencí aplikace
            Preferences.Set("DevicePlatform", DeviceInfo.Platform.ToString());
            System.Diagnostics.Debug.WriteLine($"Ukládám platformu do Preferences: {DeviceInfo.Platform}");

            // Nastavení hlavní stránky aplikace
            MainPage = new MainPage();

            // Registrace události při ukončení aplikace
            if (Current?.Windows != null && Current.Windows.Count > 0)
            {
                Current.Windows[0].Destroying += Window_Destroying;
            }

            // Inicializace handlerů specifických pro jednotlivé platformy
            InitializePlatformSpecificHandlers();
        }

        // Vytváříme a konfigurujeme hlavní okno aplikace
        protected override Window CreateWindow(IActivationState? activationState)
        {
            var window = base.CreateWindow(activationState);

            // Nastavení minimální velikosti okna pro lepší uživatelskou zkušenost
            window.MinimumWidth = 480;
            window.MinimumHeight = 320;
            return window;
        }

        // PLATFORMNĚ SPECIFICKÉ HANDLERY

        // Inicializuje handlery pro zachycení událostí specifických pro danou platformu
        private void InitializePlatformSpecificHandlers()
        {
#if DEBUG
            System.Diagnostics.Debug.WriteLine("Inicializace platform-specific handlerů");
#endif

#if WINDOWS
            // Inicializace handlerů pro Windows platformu
            try 
            {
                System.Diagnostics.Debug.WriteLine("Windows: Začátek inicializace");
                Microsoft.Maui.Handlers.WindowHandler.Mapper.AppendToMapping(nameof(IWindow), (handler, view) =>
                {
                    System.Diagnostics.Debug.WriteLine("Windows: Mapper callback spuštěn");
                    var nativeWindow = handler.PlatformView;
                    
                    // Zachycení události při zavření okna
                    nativeWindow.Closed += async (s, e) =>
                    {
                        System.Diagnostics.Debug.WriteLine("Windows: Event Closed zachycen");
                        await _authService.SetUserOffline();
        
                        // Uzavření databáze při ukončení aplikace
                        var dbConfig = Handler?.MauiContext?.Services.GetService<Taskly.LocalStorage.LiteDbConfig>();
                        if (dbConfig != null)
                        {
                            dbConfig.CloseDatabase();
                            System.Diagnostics.Debug.WriteLine("Windows: Databáze uzavřena při události Closed");
                        }
                    };
                    
                    // Zachycení události při aktivaci okna  
                    nativeWindow.Activated += (s, e) =>
                    {
                        System.Diagnostics.Debug.WriteLine("Windows: Event Activated zachycen");
                    };
                    System.Diagnostics.Debug.WriteLine("Windows: Handler úspěšně nastaven");
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Windows: Chyba při inicializaci: {ex.Message}");
            }
#else
            System.Diagnostics.Debug.WriteLine("Windows: Kód se nekompiluje - není na Windows platformě");
#endif

#if ANDROID
            // Inicializace handlerů pro Android platformu
            try
            {
                System.Diagnostics.Debug.WriteLine("Android: Začátek inicializace");
                Platform.ActivityStateChanged += async (sender, state) =>
                {
                    System.Diagnostics.Debug.WriteLine($"Android: Změna stavu na {state.State}");
                    switch (state.State)
                    {
                        case ActivityState.Destroyed:
                             // Akce při zničení aktivity - nastavení uživatele jako offline a uzavření databáze
                             System.Diagnostics.Debug.WriteLine("Android: Zastaveno/Zničeno");
                             await _authService.SetUserOffline();
        
                             // Uzavření databáze při ukončení aplikace
                             var dbConfig = Handler?.MauiContext?.Services.GetService<Taskly.LocalStorage.LiteDbConfig>();
                             if (dbConfig != null)
                             {
                                dbConfig.CloseDatabase();
                                System.Diagnostics.Debug.WriteLine("Android: Databáze uzavřena při události Destroyed");
                             }
                             break;
                        case ActivityState.Stopped:
                            System.Diagnostics.Debug.WriteLine("Android: Zastaveno/Zničeno");
                            break;
                        case ActivityState.Resumed:
                        case ActivityState.Created:
                            System.Diagnostics.Debug.WriteLine("Android: Obnoveno/Vytvořeno");
                            break;
                    }
                };
                System.Diagnostics.Debug.WriteLine("Android: Handler úspěšně nastaven");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Android: Chyba při inicializaci: {ex.Message}");
            }
#else
            System.Diagnostics.Debug.WriteLine("Android: Kód se nekompiluje - není na Android platformě");
#endif

            System.Diagnostics.Debug.WriteLine("=== Inicializace handlerů dokončena ===");
        }

        // ŽIVOTNÍ CYKLUS APLIKACE

        // Zachycujeme událost při zničení okna aplikace
        private async void Window_Destroying(object? sender, EventArgs e)
        {
            // Nastavíme uživatele jako offline před ukončením aplikace
            await _authService.SetUserOffline();

            // Uzavřeme databázi pro prevenci poškození dat
            var dbConfig = Handler?.MauiContext?.Services.GetService<Taskly.LocalStorage.LiteDbConfig>();
            if (dbConfig != null)
            {
                dbConfig.CloseDatabase();
                System.Diagnostics.Debug.WriteLine("Databáze úspěšně uzavřena při ukončení aplikace");
            }
        }

        // tato metoda volaná při spuštění aplikace
        protected override async void OnStart()
        {
            base.OnStart();

            // Pokusíme se obnovit uživatelskou relaci, ale pouze jednou při spuštění
            if (!_sessionRestored)  // Kontrolujeme, zda už relace nebyla obnovena
            {
                _sessionRestored = true;
                await _authService.TryRestoreSessionAsync();
                System.Diagnostics.Debug.WriteLine("[App.OnStart] Session restore finished.");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[App.OnStart] Session already restored, skipping initial checks.");
                // Pokud už byla relace obnovena (např. při návratu z pozadí), přeskočíme kontrolu
            }
        }
    }
}
