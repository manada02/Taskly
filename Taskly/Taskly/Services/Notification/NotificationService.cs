// NotificationService.cs - Služba pro správu notifikací
using LiteDB;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor;
using Taskly.Models;
using Taskly.LocalStorage;
using Taskly.Services.Auth;
using Taskly.Services.Connectivity;

namespace Taskly.Services.Notification
{
    public class NotificationService : INotificationService, IDisposable
    {
        // PROMĚNNÉ A ZÁVISLOSTI
        private readonly ISnackbar _snackbar;
        private readonly IJSRuntime _jsRuntime;
        private readonly LiteDbConfig _dbConfig;
        private readonly ILogger<NotificationService> _logger;
        private readonly FirestoreNotificationService _firestoreService;
        private readonly IAuthService _authService;
        private readonly ConnectivityService _connectivityService;
        private const string NOTIFICATIONS_KEY = "app_notifications_enabled";
        private dynamic? _currentPersistentToast;

        // Událost informující o změně notifikací
        public event Action? OnNotificationsChanged;

        // KONSTRUKTOR
        public NotificationService(
            ISnackbar snackbar,
            IJSRuntime jsRuntime,
            LiteDbConfig dbConfig,
            ILogger<NotificationService> logger,
            FirestoreNotificationService firestoreService,
            IAuthService authService,
            ConnectivityService connectivityService)
        {
            _snackbar = snackbar;
            _jsRuntime = jsRuntime;
            _dbConfig = dbConfig;
            _logger = logger;
            _firestoreService = firestoreService;
            _authService = authService;
            _connectivityService = connectivityService;

            // Registrujeme se na změny připojení
            _connectivityService.ConnectivityChanged += OnConnectivityChanged;

            // Registrujeme se na přihlášení uživatele
            _authService.UserLoggedIn += OnUserLoggedIn;

            _logger.LogInformation("NotificationService inicializován");
        }

        // SPRÁVA NOTIFIKACÍ
        // Přidáme novou notifikaci do historie
        public async Task AddNotificationAsync(NotificationItem notification, bool showAfterForceLoad = false, bool showToast = true)
        {
            try
            {
                bool notificationsEnabled = Preferences.Default.Get(NOTIFICATIONS_KEY, true);
                if (!notificationsEnabled)
                {
                    _logger.LogInformation("Přidání notifikace {Id} přeskočeno - notifikace jsou zakázány v nastavení.", notification?.Id ?? "null");
                    return; // Nic neukládáme ani nezobrazujeme
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při čtení nastavení povolení notifikací v AddNotificationAsync.");
            }

            // Nastavíme ID a timestamp
            if (string.IsNullOrEmpty(notification.Id))
                notification.Id = Guid.NewGuid().ToString();

            if (!notification.Timestamp.HasValue)
                notification.Timestamp = DateTime.UtcNow;  // Použijeme UtcNow místo Now

            // Nastavíme userId, pokud je uživatel přihlášen
            if (await _authService.IsUserAuthenticated())
            {
                notification.UserId = _authService.GetCurrentUserId();
            }

            try
            {
                // Zkontrolujeme, zda jsme online a přihlášeni
                bool isOnlineAndAuthenticated = _connectivityService.IsConnected && await _authService.IsUserAuthenticated();

                _logger.LogInformation("Přidávám notifikaci: {Id}, {Message}, IsAuth: {IsAuth}, IsConnected: {IsConnected}, ShowAfterForceLoad: {ShowAfterForceLoad}",
                    notification.Id, notification.Message,
                    await _authService.IsUserAuthenticated(), _connectivityService.IsConnected, showAfterForceLoad);

                // Nastavíme příznak synchronizace podle stavu připojení
                notification.NeedsSynchronization = await _authService.IsUserAuthenticated() && !_connectivityService.IsConnected;

                if (notification.NeedsSynchronization)
                {
                    _logger.LogInformation("OFFLINE REŽIM: Nastavuji NeedsSynchronization=true pro notifikaci {Id}", notification.Id);
                }

                // Pokud jsme online a přihlášeni, uložíme nejprve do Firestore
                if (isOnlineAndAuthenticated)
                {
                    _logger.LogInformation("ONLINE REŽIM: Ukládám notifikaci {Id} do Firestore", notification.Id);
                    await _firestoreService.SaveNotificationAsync(notification);
                }

                // Vždy uložíme do lokální databáze
                var db = _dbConfig.GetDatabase();
                var collection = db.GetCollection<NotificationItem>("notifications");
                collection.Insert(notification);
                LimitNotificationCount(collection);
                _logger.LogInformation("Notifikace {Id} uložena do lokální LiteDB", notification.Id);

                // Zobrazíme toast (pokud nejsme v režimu forceLoad A showToast je true)
                if (!string.IsNullOrEmpty(notification.Message) && !showAfterForceLoad && showToast)
                {
                    _snackbar.Add(notification.Message, MapToSeverity(notification.Type), config =>
                    {
                        config.ShowCloseIcon = true;
                        config.VisibleStateDuration = GetDurationByType(notification.Type);
                    });
                }

                // Pokud je požadováno zobrazení po forceLoad, uložíme do localStorage
                if (showAfterForceLoad)
                {
                    _logger.LogInformation("Nastavuji notifikaci {Id} pro zobrazení po forceLoad", notification.Id);
                    await _jsRuntime.InvokeVoidAsync("localStorage.setItem", "pendingToast",
                        System.Text.Json.JsonSerializer.Serialize(new { message = notification.Message, type = (int)notification.Type }));
                }

                OnNotificationsChanged?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při ukládání notifikace");
            }
        }

        // Získáme seznam všech notifikací
        public async Task<List<NotificationItem>> GetNotificationsAsync()
        {
            _logger.LogInformation("=== ZAČÁTEK NAČÍTÁNÍ NOTIFIKACÍ ===");

            try
            {
                string? currentUserId = null;
                bool isAuthenticated = false;
                bool isConnected = _connectivityService.IsConnected;

                // Získáme ID uživatele
                try
                {
                    isAuthenticated = await _authService.IsUserAuthenticated();
                    if (isAuthenticated)
                    {
                        currentUserId = _authService.GetCurrentUserId();
                        _logger.LogInformation("GetNotificationsAsync: Uživatel přihlášen: {UserId}", currentUserId ?? "null");
                    }
                    else
                    {
                        _logger.LogInformation("GetNotificationsAsync: Uživatel není přihlášen.");
                    }
                }
                catch (Exception authEx)
                {
                    _logger.LogWarning(authEx, "GetNotificationsAsync: Chyba při zjišťování autentizace");
                }

                if (!isAuthenticated || string.IsNullOrEmpty(currentUserId))
                {
                    // Pokud nejsme přihlášeni, můžeme načíst ID posledního uživatele pro zobrazení offline dat
                    _logger.LogInformation("GetNotificationsAsync: Pokusím se načíst ID posledního uživatele pro offline data.");
                    try
                    {
                        currentUserId = await SecureStorage.GetAsync("last_user_id");
                        _logger.LogInformation("GetNotificationsAsync: Používám poslední uložené User ID: {UserId}", currentUserId ?? "null");
                    }
                    catch (Exception secEx)
                    {
                        _logger.LogWarning(secEx, "GetNotificationsAsync: Chyba při čtení posledního User ID");
                    }
                }

                var db = _dbConfig.GetDatabase();
                var collection = db.GetCollection<NotificationItem>("notifications");
                _logger.LogInformation("GetNotificationsAsync: Připojeno k lokální LiteDB.");

                // Načteme z lokální databáze
                List<NotificationItem> localNotifications;
                IEnumerable<NotificationItem> query;
                if (string.IsNullOrEmpty(currentUserId))
                {
                    _logger.LogInformation("GetNotificationsAsync: Filtruji lokální notifikace bez User ID.");
                    query = collection.Find(n => n.UserId == null);
                }
                else
                {
                    _logger.LogInformation("GetNotificationsAsync: Filtruji lokální notifikace pro User ID: {UserId} a bez User ID.", currentUserId);
                    query = collection.Find(n => n.UserId == currentUserId || n.UserId == null);
                }
                localNotifications = query.ToList();
                _logger.LogInformation("GetNotificationsAsync: Načteno {Count} notifikací z lokální LiteDB.", localNotifications.Count);

                // Synchronizace s Firestore
                List<NotificationItem> finalNotifications = localNotifications; // Výchozí seznam = lokální

                // Synchronizujeme pouze pokud jsme online a máme platné ID přihlášeného uživatele
                if (isConnected && isAuthenticated && !string.IsNullOrEmpty(currentUserId))
                {
                    _logger.LogInformation("GetNotificationsAsync: Online a přihlášen, pokouším se synchronizovat notifikace z Firestore pro User ID: {UserId}", currentUserId);
                    try
                    {
                        var firestoreNotifications = await _firestoreService.GetNotificationsForUserAsync(currentUserId);
                        _logger.LogInformation("GetNotificationsAsync: Staženo {Count} notifikací z Firestore.", firestoreNotifications.Count);

                        // Provedeme merge a update pouze pokud jsme něco stáhli
                        if (firestoreNotifications.Any() || localNotifications.Any()) // Má smysl synchronizovat, jen pokud máme nějaká data
                        {
                            var combinedNotifications = new Dictionary<string, NotificationItem>();

                            // Přidáme lokální - mohou být novější nebo offline
                            foreach (var ln in localNotifications)
                            {
                                // Přidáme jen ty, co patří aktuálnímu uživateli nebo jsou bez ID (pokud je chceme)
                                if (ln.UserId == currentUserId)
                                {
                                    combinedNotifications[ln.Id] = ln;
                                }
                            }

                            // Po načtení notifikací z Firestore a před jejich uložením do LiteDB
                            foreach (var fn in firestoreNotifications)
                            {
                                _logger.LogDebug("Kontrola notifikace z Firestore před uložením do LiteDB: ID={Id}, Type={Type}, Category={Category}, Message={Message}",
                                    fn.Id, fn.Type, fn.Category, fn.Message);

                                if (fn.Message?.Contains("měl být dokončen") == true && fn.Category == NotificationCategory.System)
                                {
                                    _logger.LogWarning("Opravuji notifikaci o expiraci úkolu z nesprávné kategorie {Category} na {FixedCategory}",
                                        fn.Category, NotificationCategory.TaskReminder);

                                    fn.Category = NotificationCategory.TaskReminder;
                                    fn.Type = NotificationType.Warning; 
                                }
                            }

                            // Získáme pouze notifikace patřící aktuálnímu uživateli pro uložení
                            var notificationsToUpsert = combinedNotifications.Values.Where(n => n.UserId == currentUserId).ToList();

                            // Aktualizujeme existující nebo vložíme nové notifikace do LiteDB
                            if (notificationsToUpsert.Any())
                            {
                                int upsertedCount = collection.Upsert(notificationsToUpsert);
                                _logger.LogInformation("GetNotificationsAsync: Proveden Upsert {Count} notifikací do lokální LiteDB.", upsertedCount);
                            }
                            else if (localNotifications.Any(ln => ln.UserId == currentUserId))
                            {
                                // Pokud jsme měli lokální, ale po merge nejsou žádné pro uživatele smažeme je i lokálně
                                int deletedCount = collection.DeleteMany(n => n.UserId == currentUserId);
                                _logger.LogInformation("GetNotificationsAsync: Smazáno {Count} lokálních notifikací, které nebyly ve Firestore.", deletedCount);
                            }

                            // Omezíme počet notifikací v LiteDB po aktualizaci
                            LimitNotificationCount(collection);

                            // Aktualizujeme seznam, který metoda vrátí - znovu načteme z DB pro jistotu konzistence
                            _logger.LogInformation("GetNotificationsAsync: Znovu načítám finální seznam z LiteDB po synchronizaci.");
                            IEnumerable<NotificationItem> finalQuery;
                            if (string.IsNullOrEmpty(currentUserId))
                            {
                                finalQuery = collection.Find(n => n.UserId == null);
                            }
                            else
                            {
                                finalQuery = collection.Find(n => n.UserId == currentUserId);
                            }
                            finalNotifications = finalQuery.OrderByDescending(n => n.Timestamp).ToList();
                            _logger.LogInformation("GetNotificationsAsync: Finální seznam notifikací po synchronizaci má {Count} položek.", finalNotifications.Count);
                        }
                        else
                        {
                            _logger.LogInformation("GetNotificationsAsync: Nebyly nalezeny žádné notifikace (ani lokální, ani ve Firestore) pro merge.");
                            finalNotifications = new List<NotificationItem>(); // Zajistíme prázdný seznam
                        }
                    }
                    catch (Exception fireEx)
                    {
                        _logger.LogWarning(fireEx, "GetNotificationsAsync: Nepodařilo se synchronizovat notifikace z Firestore.");

                        // V případě chyby vrátíme alespoň lokální data seřazená
                        finalNotifications = localNotifications.OrderByDescending(n => n.Timestamp).ToList();
                    }
                }
                else // Offline nebo nepřihlášen
                {
                    _logger.LogInformation("GetNotificationsAsync: Přeskakuji synchronizaci notifikací z Firestore (Offline nebo nepřihlášen).");

                    // Seřadíme jen lokální data, která jsme načetli na začátku
                    finalNotifications = localNotifications.OrderByDescending(n => n.Timestamp).ToList();
                }

                _logger.LogInformation("=== KONEC NAČÍTÁNÍ NOTIFIKACÍ (Vracím {Count}) ===", finalNotifications.Count);
                return finalNotifications; // Vracíme finální (potenciálně synchronizovaný a seřazený) seznam
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Obecná chyba při načítání notifikací");
                _logger.LogInformation("=== KONEC NAČÍTÁNÍ NOTIFIKACÍ S CHYBOU ===");
                return new List<NotificationItem>();
            }
        }

        // Vymažeme všechny notifikace
        public async Task ClearNotificationsAsync()
        {
            _logger.LogInformation("Mažu všechny notifikace z historie");
            try
            {
                // Kontrola připojení k internetu
                if (!_connectivityService.IsConnected && await _authService.IsUserAuthenticated())
                {
                    ShowToast("Pro mazání notifikací je potřeba připojení k internetu.", NotificationType.Warning);
                    return;
                }

                // Získáme ID přihlášeného uživatele
                string? userId = null;
                if (await _authService.IsUserAuthenticated())
                {
                    userId = _authService.GetCurrentUserId();

                    // Pokud jsme online a userId není null nebo prázdné, smažeme také z Firestore
                    if (_connectivityService.IsConnected && !string.IsNullOrEmpty(userId))
                    {
                        _logger.LogInformation("Mažu notifikace z Firestore pro uživatele {UserId}", userId);
                        await _firestoreService.DeleteNotificationsForUserAsync(userId);
                    }
                }

                // Vždy smažeme z lokální databáze
                var db = _dbConfig.GetDatabase();
                var collection = db.GetCollection<NotificationItem>("notifications");

                if (string.IsNullOrEmpty(userId))
                {
                    // V nepřihlášeném režimu - získáme poslední použité ID uživatele
                    string? lastUserId = null;
                    try
                    {
                        lastUserId = await SecureStorage.GetAsync("last_user_id");
                        _logger.LogInformation("Získáno poslední ID uživatele pro mazání: {LastUserId}",
                            string.IsNullOrEmpty(lastUserId) ? "(žádné)" : lastUserId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Problém při načítání last_user_id ze SecureStorage");
                    }

                    if (string.IsNullOrEmpty(lastUserId))
                    {
                        // Pokud nemáme poslední ID, mažeme jen lokální
                        collection.DeleteMany(n => n.UserId == null);
                        _logger.LogInformation("Mažu pouze lokální notifikace bez UserId");
                    }
                    else
                    {
                        // Mažeme jak lokální, tak notifikace spojené s posledním přihlášeným uživatelem
                        collection.DeleteMany(n => n.UserId == null || n.UserId == lastUserId);
                        _logger.LogInformation("Mažu lokální notifikace a notifikace uživatele {LastUserId}", lastUserId);
                    }
                }
                else
                {
                    // V přihlášeném režimu smažeme notifikace aktuálního uživatele
                    collection.DeleteMany(n => n.UserId == userId);
                    _logger.LogInformation("Mažu notifikace přihlášeného uživatele {UserId}", userId);
                }

                // Zobrazíme potvrzení o úspěšném smazání
                ShowToast("Všechny notifikace byly úspěšně smazány", NotificationType.Success);

                OnNotificationsChanged?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při mazání notifikací");
                throw; // výjimka 
            }
        }

        // Smažeme konkrétní notifikaci podle ID
        public async Task DeleteNotificationAsync(string id)
        {
            _logger.LogInformation("Mažu notifikaci {Id}", id);

            try
            {
                // Kontrola připojení k internetu
                if (!_connectivityService.IsConnected && await _authService.IsUserAuthenticated())
                {
                    ShowToast("Pro mazání notifikací je potřeba připojení k internetu.", NotificationType.Warning);
                    return;
                }

                // Deklarace databázových proměnných jednou pro celou metodu
                var db = _dbConfig.GetDatabase();
                var collection = db.GetCollection<NotificationItem>("notifications");

                // Načteme notifikaci - použijeme deklarované proměnné
                NotificationItem? notification = collection.FindById(id);
                if (notification == null)
                {
                    _logger.LogWarning("Notifikace s ID {Id} nebyla nalezena", id);
                    return;
                }

                // Pokud jsme online a notifikace má UserId, smažeme ji z Firestore
                if (_connectivityService.IsConnected && !string.IsNullOrEmpty(notification.UserId))
                {
                    _logger.LogInformation("Mažu notifikaci {Id} z Firestore", id);
                    await _firestoreService.DeleteNotificationAsync(id, notification.UserId);
                }

                // Vždy smažeme z lokální databáze - použijeme stejné proměnné
                collection.Delete(id);
                _logger.LogInformation("Notifikace {Id} smazána z lokální databáze", id);

                OnNotificationsChanged?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při mazání notifikace {Id}", id);
            }
        }

        // Vymažeme interní cache služby
        public void ClearCache()
        {
            // Hlavním účelem volání ClearCache zde je signalizovat změnu
            // komponentám, které si drží načtená data (jako NotificationCenter).
            _logger.LogInformation("NotificationService ClearCache called. Invoking OnNotificationsChanged to signal data wipe.");
            OnNotificationsChanged?.Invoke(); // Vyvolání události donutí NotificationCenter znovu načíst data
        }

        // ZOBRAZENÍ TOASTŮ
        // Zobrazíme jednoduchý toast bez ukládání do historie
        public void ShowToast(string message, NotificationType type = NotificationType.Info)
        {
            try
            {
                // Výchozí hodnota true, pokud preference neexistuje
                bool notificationsEnabled = Preferences.Default.Get(NOTIFICATIONS_KEY, true);
                if (!notificationsEnabled)
                {
                    _logger.LogInformation("Zobrazení toastu přeskočeno - notifikace jsou zakázány.");
                    return; // Nezobrazíme toast, pokud jsou notifikace vypnuté
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při čtení nastavení povolení notifikací v ShowToast.");
            }

            if (!string.IsNullOrEmpty(message))
            {
                _logger.LogDebug("Zobrazuji toast: {Message}", message);
                _snackbar.Add(message, MapToSeverity(type), config =>
                {
                    config.ShowCloseIcon = true;
                    config.VisibleStateDuration = GetDurationByType(type);
                });
            }
        }

        // Zobrazíme trvalý toast
        public void ShowPersistentToast(string message, NotificationType type = NotificationType.Info, bool showCloseIcon = false)
        {
            // Zkontrolujeme nastavení povolení notifikací
            try
            {
                bool notificationsEnabled = Preferences.Default.Get(NOTIFICATIONS_KEY, true);
                if (!notificationsEnabled)
                {
                    _logger.LogInformation("Zobrazení perzistentního toastu '{Message}' přeskočeno - notifikace jsou zakázány.", message);
                    RemovePersistentToast();
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při čtení nastavení povolení notifikací v ShowPersistentToast.");
            }

            // Odstraníme předchozí perzistentní toast
            RemovePersistentToast();

            // Zobrazíme nový perzistentní toast
            try
            {
                _logger.LogDebug("Zobrazuji perzistentní toast: {Message}", message);
                _currentPersistentToast = _snackbar.Add(message, MapToSeverity(type), config =>
                {
                    config.ShowCloseIcon = showCloseIcon;
                    config.VisibleStateDuration = int.MaxValue;
                    config.RequireInteraction = true;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při zobrazování perzistentního toastu.");
                _currentPersistentToast = null;
            }
        }

        // Odstraníme trvalý toast
        public void RemovePersistentToast()
        {
            if (_currentPersistentToast != null)
            {
                try { _snackbar.Remove(_currentPersistentToast); } catch { }
                _currentPersistentToast = null;
            }
        }

        // Zobrazíme toast po přesměrování (force load) stránky
        public async Task ShowToastAfterForceLoadAsync(string message, NotificationType type = NotificationType.Info)
        {
            _logger.LogDebug("Ukládám toast pro forceLoad: {Message}", message);
            await _jsRuntime.InvokeVoidAsync("localStorage.setItem", "pendingToast",
                System.Text.Json.JsonSerializer.Serialize(new { message, type = (int)type }));
        }

        // Zkontrolujeme, zda existují čekající toasty po přesměrování
        public async Task CheckForPendingToastAsync()
        {
            try
            {
                var pendingToast = await _jsRuntime.InvokeAsync<string>("localStorage.getItem", "pendingToast");

                if (!string.IsNullOrEmpty(pendingToast))
                {
                    _logger.LogDebug("Nalezen čekající toast po forceLoad");
                    var data = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(pendingToast);
                    var message = data.GetProperty("message").GetString();
                    var type = (NotificationType)data.GetProperty("type").GetInt32();

                    if (!string.IsNullOrEmpty(message))
                    {
                        ShowToast(message, type);
                    }

                    await _jsRuntime.InvokeVoidAsync("localStorage.removeItem", "pendingToast");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při kontrole čekajících toastů");
            }
        }

        // SYNCHRONIZACE
        // Synchronizujeme notifikace po přihlášení uživatele
        public async Task SynchronizeNotificationsOnLoginAsync(string userId)
        {
            _logger.LogInformation("Synchronizuji notifikace po přihlášení pro uživatele {UserId}", userId);

            try
            {
                // Deklarace databázových proměnných pro celou metodu
                var db = _dbConfig.GetDatabase();
                var collection = db.GetCollection<NotificationItem>("notifications");

                // 1. Najdeme všechny lokální notifikace bez userId
                List<NotificationItem> localNotificationsWithoutUser = collection
                    .Find(n => n.UserId == null)
                    .ToList();

                // 2. Přiřadíme userId těmto notifikacím
                foreach (var notification in localNotificationsWithoutUser)
                {
                    notification.UserId = userId;
                }

                // 3. Aktualizujeme v lokální databázi - použijeme stejné proměnné
                foreach (var notification in localNotificationsWithoutUser)
                {
                    collection.Update(notification);
                }

                // 4. Nahrajeme do Firestore, pokud jsme online
                if (_connectivityService.IsConnected && localNotificationsWithoutUser.Any())
                {
                    foreach (var notification in localNotificationsWithoutUser)
                    {
                        await _firestoreService.SaveNotificationAsync(notification);
                    }
                }

                // 5. Stáhneme notifikace uživatele z Firestore
                if (_connectivityService.IsConnected)
                {
                    var firestoreNotifications = await _firestoreService.GetNotificationsForUserAsync(userId);

                    // 6. Uložíme je do lokální databáze, pokud tam ještě nejsou - použijeme stejné proměnné
                    foreach (var notification in firestoreNotifications)
                    {
                        if (!collection.Exists(n => n.Id == notification.Id))
                        {
                            collection.Insert(notification);
                        }
                    }
                }

                OnNotificationsChanged?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při synchronizaci notifikací po přihlášení");
            }
        }

        // Synchronizujeme notifikace po obnovení připojení k internetu
        public async Task SynchronizeNotificationsOnConnectionRestoredAsync()
        {
            if (!await _authService.IsUserAuthenticated())
            {
                _logger.LogInformation("Synchronizace přeskočena - uživatel není přihlášen");
                return;
            }

            try
            {
                _logger.LogInformation("=== ZAČÁTEK SYNCHRONIZACE PO OBNOVENÍ PŘIPOJENÍ ===");

                // Deklarace databázových proměnných jednou pro celou metodu
                var db = _dbConfig.GetDatabase();
                var collection = db.GetCollection<NotificationItem>("notifications");

                // Najdeme všechny notifikace, které potřebují synchronizaci
                List<NotificationItem> notificationsToSync = collection
                    .Find(n => n.NeedsSynchronization)
                    .ToList();

                _logger.LogInformation("Nalezeno {Count} notifikací čekajících na synchronizaci", notificationsToSync.Count);

                // Nahrajeme je do Firestore a aktualizujeme příznak
                foreach (var notification in notificationsToSync)
                {
                    _logger.LogInformation("Synchronizuji notifikaci {Id}: {Message}", notification.Id, notification.Message);
                    await _firestoreService.SaveNotificationAsync(notification);

                    // Použijeme stejné proměnné místo nového using bloku
                    notification.NeedsSynchronization = false;
                    collection.Update(notification);
                    _logger.LogInformation("Příznak synchronizace odstraněn pro notifikaci {Id}", notification.Id);
                }

                _logger.LogInformation("=== KONEC SYNCHRONIZACE PO OBNOVENÍ PŘIPOJENÍ ===");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při synchronizaci notifikací po obnovení připojení");
            }
        }

        // EVENT HANDLERY
        // Reagujeme na změnu připojení k internetu
        private async void OnConnectivityChanged(bool isConnected)
        {
            _logger.LogInformation("Detekována změna připojení: {State}", isConnected ? "Online" : "Offline");

            if (isConnected && await _authService.IsUserAuthenticated())
            {
                _logger.LogInformation("Internet obnoven, zahajuji synchronizaci notifikací");
                await SynchronizeNotificationsOnConnectionRestoredAsync();
            }
        }

        // Reagujeme na přihlášení uživatele
        private async void OnUserLoggedIn(string userId)
        {
            try
            {
                _logger.LogInformation("Zahájení synchronizace notifikací po přihlášení uživatele {UserId}", userId);
                await SynchronizeNotificationsOnLoginAsync(userId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při synchronizaci notifikací po přihlášení");
            }
        }

        // POMOCNÉ METODY
        // Určíme dobu zobrazení podle typu notifikace
        private int GetDurationByType(NotificationType type)
        {
            return type switch
            {
                NotificationType.Error => 6000,
                NotificationType.Warning => 5000,
                NotificationType.Success => 4000,
                NotificationType.Info => 3000,
                _ => 4000
            };
        }

        // Omezíme počet notifikací v databázi
        private void LimitNotificationCount(ILiteCollection<NotificationItem> collection, int limit = 100)
        {
            var count = collection.Count();
            if (count > limit)
            {
                _logger.LogDebug("Omezuji počet notifikací na {Limit}", limit);

                var oldestNotifications = collection
                    .Find(Query.All("Timestamp", Query.Ascending))
                    .Take(count - limit)
                    .ToList();

                foreach (var old in oldestNotifications)
                {
                    collection.Delete(old.Id);
                }
            }
        }

        // Mapujeme typ notifikace na MudBlazor Severity
        private static Severity MapToSeverity(NotificationType type) => type switch
        {
            NotificationType.Success => Severity.Success,
            NotificationType.Info => Severity.Info,
            NotificationType.Warning => Severity.Warning,
            NotificationType.Error => Severity.Error,
            _ => Severity.Info
        };

        // UVOLNĚNÍ ZDROJŮ
        // Uvolní zdroje komponenty - odregistruje event handlery pro předejití memory leaků
        public void Dispose()
        {
            // Odregistrujeme se z události připojení
            _connectivityService.ConnectivityChanged -= OnConnectivityChanged;

            // Odregistrujeme se z události přihlášení
            _authService.UserLoggedIn -= OnUserLoggedIn;
        }
    }
}
