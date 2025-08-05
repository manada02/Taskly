// App.xaml.cs - Hlavní třída aplikace Taskly definující chování a životní cyklus
using Taskly.Services.Auth;
using Taskly.Services.Notification.LocalNotification;

namespace Taskly
{
    public partial class App : Application
    {
        // PROMĚNNÉ A ZÁVISLOSTI

        // Služba pro autentizaci uživatele přes Firebase
        private readonly FirebaseAuthService _authService;

        // Příznak, jestli už byla obnovena uživatelská relace (zabrání opakovanému obnovování)
        private bool _sessionRestored = false;

        // Čas poslední kontroly notifikací (zatím nepoužito, ale připraveno)
        private DateTime _lastNotificationCheck = DateTime.MinValue;

        // INICIALIZACE APLIKACE

        // Konstruktor - inicializuje komponenty, ukládá platformu a nastavuje hlavní stránku
        public App(FirebaseAuthService authService)
        {
            InitializeComponent();

            _authService = authService;

            // Ukládáme aktuální platformu do preferencí aplikace pro případné pozdější použití
            Preferences.Set("DevicePlatform", DeviceInfo.Platform.ToString());
            System.Diagnostics.Debug.WriteLine($"Ukládám platformu do Preferences: {DeviceInfo.Platform}");

            // Nastavujeme hlavní stránku aplikace
            MainPage = new MainPage();

            // Pokud je okno aplikace vytvořeno, zaregistrujeme obsluhu události ukončení
            if (Current?.Windows != null && Current.Windows.Count > 0)
            {
                Current.Windows[0].Destroying += Window_Destroying;
            }

            // Inicializace platformně specifických handlerů pro zachycení událostí
            InitializePlatformSpecificHandlers();
        }

        // Vytvoření hlavního okna aplikace s nastavením minimální velikosti
        protected override Window CreateWindow(IActivationState? activationState)
        {
            var window = base.CreateWindow(activationState);

            window.MinimumWidth = 480;
            window.MinimumHeight = 320;

            return window;
        }

        // PLATFORMNĚ SPECIFICKÉ HANDLERY

        // Metoda pro inicializaci handlerů, které reagují na události specifické pro platformu
        private void InitializePlatformSpecificHandlers()
        {
#if DEBUG
            System.Diagnostics.Debug.WriteLine("Inicializace platformně specifických handlerů");
#endif

#if WINDOWS
            // Inicializace pro Windows - zachycení událostí okna (uzavření, aktivace)
            try
            {
                System.Diagnostics.Debug.WriteLine("Windows: Začátek inicializace handlerů");

                Microsoft.Maui.Handlers.WindowHandler.Mapper.AppendToMapping(nameof(IWindow), (handler, view) =>
                {
                    System.Diagnostics.Debug.WriteLine("Windows: Spuštěn mapper callback");

                    var nativeWindow = handler.PlatformView;

                    // Událost zavření okna - nastavíme uživatele offline a uzavřeme databázi
                    nativeWindow.Closed += async (s, e) =>
                    {
                        System.Diagnostics.Debug.WriteLine("Windows: Okno zavřeno, nastavuji uživatele offline");
                        await _authService.SetUserOffline();

                        var dbConfig = Handler?.MauiContext?.Services.GetService<Taskly.LocalStorage.LiteDbConfig>();
                        if (dbConfig != null)
                        {
                            dbConfig.CloseDatabase();
                            System.Diagnostics.Debug.WriteLine("Windows: Databáze uzavřena při zavření okna");
                        }
                    };

                    // Událost aktivace okna
                    nativeWindow.Activated += (s, e) =>
                    {
                        System.Diagnostics.Debug.WriteLine("Windows: Okno aktivováno");
                    };

                    System.Diagnostics.Debug.WriteLine("Windows: Handler úspěšně nastaven");
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Windows: Chyba při inicializaci handlerů: {ex.Message}");
            }
#else
            System.Diagnostics.Debug.WriteLine("Windows: Kód není spuštěn, protože aplikace neběží na Windows");
#endif

#if ANDROID
            // Inicializace handlerů pro Android - sledování změn stavu aktivity
            try
            {
                System.Diagnostics.Debug.WriteLine("Android: Začátek inicializace handlerů");

                Platform.ActivityStateChanged += async (sender, state) =>
                {
                    System.Diagnostics.Debug.WriteLine($"Android: Stav aktivity změněn na {state.State}");

                    switch (state.State)
                    {
                        case ActivityState.Destroyed:
                            // Při ukončení aktivity nastavíme uživatele offline a uzavřeme databázi
                            System.Diagnostics.Debug.WriteLine("Android: Aktivita zničena, uživatel offline");
                            await _authService.SetUserOffline();

                            var dbConfig = Handler?.MauiContext?.Services.GetService<Taskly.LocalStorage.LiteDbConfig>();
                            if (dbConfig != null)
                            {
                                dbConfig.CloseDatabase();
                                System.Diagnostics.Debug.WriteLine("Android: Databáze uzavřena při ukončení aktivity");
                            }
                            break;

                        case ActivityState.Stopped:
                            System.Diagnostics.Debug.WriteLine("Android: Aktivita zastavena");
                            break;

                        case ActivityState.Resumed:
                        case ActivityState.Created:
                            System.Diagnostics.Debug.WriteLine("Android: Aktivita obnovena nebo vytvořena");
                            break;
                    }
                };

                System.Diagnostics.Debug.WriteLine("Android: Handler úspěšně nastaven");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Android: Chyba při inicializaci handlerů: {ex.Message}");
            }
#else
            System.Diagnostics.Debug.WriteLine("Android: Kód není spuštěn, protože aplikace neběží na Androidu");
#endif

            System.Diagnostics.Debug.WriteLine("Inicializace platformně specifických handlerů dokončena");
        }

        // ŽIVOTNÍ CYKLUS APLIKACE

        // Událost volaná při ukončení okna aplikace - nastaví uživatele offline a uzavře databázi
        private async void Window_Destroying(object? sender, EventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("Událost Destroying - nastavujeme uživatele offline a uzavíráme databázi");
            await _authService.SetUserOffline();

            var dbConfig = Handler?.MauiContext?.Services.GetService<Taskly.LocalStorage.LiteDbConfig>();
            if (dbConfig != null)
            {
                dbConfig.CloseDatabase();
                System.Diagnostics.Debug.WriteLine("Databáze úspěšně uzavřena při ukončení aplikace");
            }
        }

        // Metoda volaná při spuštění aplikace
        protected override async void OnStart()
        {
            base.OnStart();

            // Pokus obnovit uživatelskou relaci pouze jednou, aby nedocházelo k duplicitnímu načítání
            if (!_sessionRestored)
            {
                _sessionRestored = true;
                await _authService.TryRestoreSessionAsync();
                System.Diagnostics.Debug.WriteLine("[App.OnStart] Obnovení session dokončeno.");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[App.OnStart] Session již obnovena, kontrola přeskočena.");
            }
        }
    }
}
