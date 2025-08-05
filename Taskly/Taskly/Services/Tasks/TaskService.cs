// TaskService.cs - Služba pro práci s úkoly
using Microsoft.Extensions.Logging;
using MudBlazor;
using Taskly.LocalStorage;
using Taskly.Models;
using Taskly.Services.Auth;
using Taskly.Services.Connectivity;
using Taskly.Services.Cache;
using Taskly.Services.Notification.LocalNotification;
using LiteDB;
using Taskly.Services.Notification;

namespace Taskly.Services.Tasks
{
    public class TaskService : ITaskService, IDisposable
    {
        // PROMĚNNÉ A ZÁVISLOSTI
        private readonly LiteDbConfig _dbConfig;
        private readonly ILogger<TaskService> _logger;
        private readonly FirestoreTaskService _firestoreService;
        private readonly IAuthService _authService;
        private readonly ConnectivityService _connectivityService;
        private readonly INotificationService _notificationService;
        private readonly ILocalNotificationSchedulerService _localSchedulerService;

        private const string AUTO_SYNC_KEY = "app_auto_sync";

        // Předem načtené úkoly pro rychlý přístup
        private List<TaskItem>? _preloadedTasks;

        // Událost informující o změně úkolů
        public event Action? OnTasksChanged;

        // KONSTRUKTOR
        public TaskService(
            LiteDbConfig dbConfig,
            ILogger<TaskService> logger,
            FirestoreTaskService firestoreService,
            IAuthService authService,
            ConnectivityService connectivityService,
            INotificationService notificationService,
            ILocalNotificationSchedulerService localSchedulerService)
        {
            _dbConfig = dbConfig;
            _logger = logger;
            _firestoreService = firestoreService;
            _authService = authService;
            _connectivityService = connectivityService;
            _notificationService = notificationService;
            _localSchedulerService = localSchedulerService;

            // Registrujeme se na změny připojení
            _connectivityService.ConnectivityChanged += OnConnectivityChanged;

            // Registrujeme se na přihlášení uživatele
            _authService.UserLoggedIn += OnUserLoggedIn;

            _logger.LogInformation("TaskService inicializován");
        }

        // ZÁKLADNÍ OPERACE
        // Vytvoříme nový úkol v databázi
        public async Task<TaskItem> CreateTaskAsync(TaskItem task)
        {
            // Nastavíme ID a timestamp, pokud chybí
            if (string.IsNullOrEmpty(task.Id))
                task.Id = Guid.NewGuid().ToString();

            if (task.CreatedAt == default)
                task.CreatedAt = DateTime.UtcNow;

            // Nastavíme userId, pokud je uživatel přihlášen
            if (await _authService.IsUserAuthenticated())
            {
                task.UserId = _authService.GetCurrentUserId();
            }

            try
            {
                // Zkontrolujeme, zda jsme online a přihlášeni
                bool isOnlineAndAuthenticated = _connectivityService.IsConnected && await _authService.IsUserAuthenticated();

                _logger.LogInformation("Vytvářím úkol: {Id}, {Title}, IsAuth: {IsAuth}, IsConnected: {IsConnected}",
                    task.Id, task.Title, await _authService.IsUserAuthenticated(), _connectivityService.IsConnected);

                // Nastavíme příznak synchronizace podle stavu připojení
                task.NeedsSynchronization = await _authService.IsUserAuthenticated() && !_connectivityService.IsConnected;

                if (task.NeedsSynchronization)
                {
                    _logger.LogInformation("OFFLINE REŽIM: Nastavuji NeedsSynchronization=true pro úkol {Id}", task.Id);
                }

                // Pokud jsme online a přihlášeni, uložíme nejprve do Firestore
                if (isOnlineAndAuthenticated)
                {
                    _logger.LogInformation("ONLINE REŽIM: Ukládám úkol {Id} do Firestore", task.Id);
                    await _firestoreService.SaveTaskAsync(task);
                }

                // Vždy uložíme do lokální databáze
                var db = _dbConfig.GetDatabase();
                var collection = db.GetCollection<TaskItem>("tasks");
                collection.Insert(task);
                _logger.LogInformation("Úkol {Id} uložen do lokální LiteDB", task.Id);


                // Aktualizujeme předem načtená data
                if (_preloadedTasks != null)
                {
                    _preloadedTasks.Add(task);

                    // Zachováme stejné řazení jako při načítání
                    _preloadedTasks = _preloadedTasks.OrderByDescending(t => t.CreatedAt).ToList();
                    _logger.LogInformation("Úkol {Id} přidán do předem načtených dat", task.Id);
                }

                // Naplánujeme systémové notifikace
                await _localSchedulerService.ScheduleRemindersForTaskAsync(task);

                OnTasksChanged?.Invoke();

                return task;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při vytváření úkolu");
                throw;
            }
        }

        // Získáme úkol podle ID
        public async Task<TaskItem?> GetTaskAsync(string id)
        {
            try
            {
                _logger.LogInformation("Načítám úkol s ID: {Id}", id);

                // Deklarace proměnných pro celou metodu
                var db = _dbConfig.GetDatabase();
                var collection = db.GetCollection<TaskItem>("tasks");
                TaskItem? task = null;

                // Nejprve zkusíme najít v předem načtených datech
                if (_preloadedTasks != null)
                {
                    var cachedTask = _preloadedTasks.FirstOrDefault(t => t.Id == id);
                    if (cachedTask != null)
                    {
                        _logger.LogInformation("Úkol {Id} nalezen v předem načtených datech", id);

                        // Pokud jsme online, přihlášeni a úkol má userId, zkusíme aktualizovat z Firestore
                        if (!string.IsNullOrEmpty(cachedTask.UserId) &&
                            _connectivityService.IsConnected && await _authService.IsUserAuthenticated())
                        {
                            try
                            {
                                var firebaseTask = await _firestoreService.GetTaskAsync(id, cachedTask.UserId);

                                // Pokud je úkol v Firestore aktuálnější, aktualizujeme lokální kopii
                                if (firebaseTask != null)
                                {
                                    collection.Update(firebaseTask);
                                    _logger.LogInformation("Úkol {Id} aktualizován z Firestore", id);

                                    // Aktualizujeme i v předem načtených datech
                                    int index = _preloadedTasks.FindIndex(t => t.Id == id);
                                    if (index != -1)
                                    {
                                        _preloadedTasks[index] = firebaseTask;
                                    }

                                    return firebaseTask;
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Nepodařilo se aktualizovat úkol {Id} z Firestore", id);
                            }
                        }

                        return cachedTask;
                    }
                }

                // Pokud není v cache, zkusíme načíst z lokální databáze
                task = collection.FindById(id);

                // Pokud jsme online, přihlášeni a úkol má userId, zkusíme aktualizovat z Firestore
                if (task != null && !string.IsNullOrEmpty(task.UserId) &&
                    _connectivityService.IsConnected && await _authService.IsUserAuthenticated())
                {
                    try
                    {
                        var firebaseTask = await _firestoreService.GetTaskAsync(id, task.UserId);

                        // Pokud je úkol v Firestore aktuálnější, aktualizujeme lokální kopii
                        if (firebaseTask != null)
                        {
                            collection.Update(firebaseTask);
                            _logger.LogInformation("Úkol {Id} aktualizován z Firestore", id);

                            // Aktualizujeme i v předem načtených datech, pokud existují
                            if (_preloadedTasks != null)
                            {
                                int index = _preloadedTasks.FindIndex(t => t.Id == id);
                                if (index != -1)
                                {
                                    _preloadedTasks[index] = firebaseTask;
                                }
                            }

                            return firebaseTask;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Nepodařilo se aktualizovat úkol {Id} z Firestore", id);
                        
                    }
                }

                return task;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při načítání úkolu {Id}", id);
                return null;
            }
        }

        // Získáme seznam úkolů, volitelně filtrovaný podle projektu
        public async Task<List<TaskItem>> GetTasksAsync(string? projectId = null)
        {
            // Pokud máme předem načtená data a není specifikován projekt, použijeme je
            if (_preloadedTasks != null && projectId == null)
            {
                _logger.LogInformation("Vracím {Count} předem načtených úkolů", _preloadedTasks.Count);
                return _preloadedTasks.ToList(); // Vrátíme kopii, aby nedošlo k neočekávaným změnám
            }

            // Pokud máme předem načtená data a je specifikován projekt, filtrujeme
            if (_preloadedTasks != null && projectId != null)
            {
                _logger.LogInformation("Filtruji předem načtené úkoly pro projekt {ProjectId}", projectId);
                return _preloadedTasks.Where(t => t.ProjectId == projectId).ToList();
            }

            _logger.LogInformation("Načítám úkoly" + (projectId != null ? $" pro projekt {projectId}" : ""));

            try
            {
                string? currentUserId = null;

                bool isAuthenticated = false;
                try
                {
                    isAuthenticated = await _authService.IsUserAuthenticated();
                    if (isAuthenticated)
                    {
                        currentUserId = _authService.GetCurrentUserId();
                        _logger.LogInformation("Uživatel je přihlášen, ID: {UserId}",
                            string.IsNullOrEmpty(currentUserId) ? "(null)" : currentUserId);

                        // Ukládáme do SecureStorage pouze pokud ID není null
                        if (!string.IsNullOrEmpty(currentUserId))
                        {
                            await SecureStorage.SetAsync("last_user_id", currentUserId);
                        }
                    }
                }
                catch (Exception authEx)
                {
                    _logger.LogWarning(authEx, "Problém při zjišťování autentizace");
                }

                // Pokud nemáme ID, zkusíme ho získat z historie (když jsme offline)
                if (string.IsNullOrEmpty(currentUserId))
                {
                    try
                    {
                        currentUserId = await SecureStorage.GetAsync("last_user_id");
                        _logger.LogInformation("Uživatel není přihlášen nebo je v offline režimu, použijeme poslední ID: {LastUserId}",
                            string.IsNullOrEmpty(currentUserId) ? "(žádné)" : currentUserId);
                    }
                    catch (Exception secEx)
                    {
                        _logger.LogWarning(secEx, "Problém při načítání last_user_id ze SecureStorage");
                    }
                }

                // Načteme z lokální databáze
                var db = _dbConfig.GetDatabase();
                var collection = db.GetCollection<TaskItem>("tasks");
                _logger.LogInformation("Připojeno k lokální LiteDB databázi");

                List<TaskItem> tasks;
                IEnumerable<TaskItem> query;

                // Filtr podle uživatele
                if (string.IsNullOrEmpty(currentUserId))
                {
                    _logger.LogInformation("Nepřihlášený režim: Načítáme všechny úkoly bez ohledu na UserId");
                    query = collection.FindAll(); // Načteme všechny úkoly
                }
                else
                {
                    _logger.LogInformation("Filtrujeme úkoly pro uživatele {UserId} a lokální", currentUserId);
                    query = collection.Find(t => t.UserId == currentUserId || t.UserId == null);
                }

                // Filtr podle projektu (pokud zadán)
                if (!string.IsNullOrEmpty(projectId))
                {
                    query = query.Where(t => t.ProjectId == projectId);
                }

                tasks = query.OrderByDescending(t => t.CreatedAt).ToList();
                _logger.LogInformation("Načteno {Count} úkolů z lokální databáze", tasks.Count);

                // Pokud jsme online a přihlášeni, zkusíme aktualizovat data z Firestore
                if (_connectivityService.IsConnected && isAuthenticated && !string.IsNullOrEmpty(currentUserId))
                {
                    try
                    {
                        _logger.LogInformation("Aktualizuji data z Firestore");
                        var firestoreTasks = await _firestoreService.GetTasksForUserAsync(currentUserId, projectId);

                        if (firestoreTasks.Count > 0)
                        {
                            // Aktualizujeme lokální databázi
                            foreach (var task in firestoreTasks)
                            {
                                // Aktualizujeme nebo přidáme nový úkol
                                if (collection.Exists(t => t.Id == task.Id))
                                {
                                    collection.Update(task);
                                }
                                else
                                {
                                    collection.Insert(task);
                                }
                            }

                            // Aktualizujeme zdejší seznam úkolů
                            var taskIds = tasks.Select(t => t.Id).ToHashSet();
                            foreach (var task in firestoreTasks)
                            {
                                if (!taskIds.Contains(task.Id))
                                {
                                    tasks.Add(task);
                                }
                                else
                                {
                                    // Aktualizujeme existující úkol v seznamu
                                    var existingTask = tasks.FirstOrDefault(t => t.Id == task.Id);
                                    if (existingTask != null)
                                    {
                                        int index = tasks.IndexOf(existingTask);
                                        tasks[index] = task;
                                    }
                                }
                            }

                            // Znovu seřadíme
                            tasks = tasks.OrderByDescending(t => t.CreatedAt).ToList();
                        }
                    }
                    catch (Exception fireEx)
                    {
                        _logger.LogWarning(fireEx, "Nepodařilo se aktualizovat úkoly z Firestore");
                    }
                }

                return tasks;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při načítání úkolů");
                return new List<TaskItem>();
            }
        }

        // Aktualizujeme existující úkol
        public async Task<TaskItem> UpdateTaskAsync(TaskItem task)
        {
            try
            {
                _logger.LogInformation("Aktualizuji úkol: {Id}, {Title}", task.Id, task.Title);

                // Deklarace proměnných pro celou metodu
                var db = _dbConfig.GetDatabase();
                var collection = db.GetCollection<TaskItem>("tasks");

                // Kontrola, zda úkol existuje v lokální databázi
                TaskItem? existingTask = collection.FindById(task.Id);

                if (existingTask == null)
                {
                    _logger.LogWarning("Úkol {Id} nebyl nalezen v lokální databázi", task.Id);
                    throw new KeyNotFoundException($"Úkol s ID {task.Id} nebyl nalezen");
                }

                // Zkontrolujeme, zda jsme online a přihlášeni
                bool isOnlineAndAuthenticated = _connectivityService.IsConnected && await _authService.IsUserAuthenticated();

                // Nastavíme příznak synchronizace podle stavu připojení
                if (await _authService.IsUserAuthenticated() && !_connectivityService.IsConnected)
                {
                    task.NeedsSynchronization = true;
                    _logger.LogInformation("OFFLINE REŽIM: Nastavuji NeedsSynchronization=true pro úkol {Id}", task.Id);
                }

                // Pokud jsme online a přihlášeni, aktualizujeme nejprve v Firestore
                if (isOnlineAndAuthenticated && !string.IsNullOrEmpty(task.UserId))
                {
                    _logger.LogInformation("ONLINE REŽIM: Aktualizuji úkol {Id} ve Firestore", task.Id);
                    await _firestoreService.SaveTaskAsync(task);
                    task.NeedsSynchronization = false;
                }

                // Vždy aktualizujeme v lokální databázi (použijeme stejnou instanci db a collection)
                collection.Update(task);
                _logger.LogInformation("Úkol {Id} aktualizován v lokální LiteDB", task.Id);

                // Naplánujeme systémové notifikace pro nový úkol
                await _localSchedulerService.ScheduleRemindersForTaskAsync(task);

                OnTasksChanged?.Invoke();


                // Aktualizujeme předem načtená data
                if (_preloadedTasks != null)
                {
                    var index = _preloadedTasks.FindIndex(t => t.Id == task.Id);
                    if (index != -1)
                    {
                        _preloadedTasks[index] = task;

                        // Zachováme stejné řazení jako v původní implementaci
                        _preloadedTasks = _preloadedTasks.OrderByDescending(t => t.CreatedAt).ToList();
                    }
                }

                return task;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při aktualizaci úkolu {Id}", task.Id);
                throw;
            }
        }

        // Smažeme úkol podle ID
        public async Task DeleteTaskAsync(string id)
        {
            try
            {
                _logger.LogInformation("Mažu úkol: {Id}", id);

                // Zrušení notifikace před mazáním
                _localSchedulerService.CancelRemindersForTaskAsync(id); // Zavoláme hned na začátku (nebo po ověření existence)
                _logger.LogDebug("Zrušeny naplánované připomínky pro úkol {Id} (pokud existovaly).", id);

                // Deklarace proměnných pro celou metodu
                var db = _dbConfig.GetDatabase();
                var collection = db.GetCollection<TaskItem>("tasks");

                // Nejprve načteme úkol, abychom měli informaci o userId
                TaskItem? task = collection.FindById(id);

                if (task == null)
                {
                    _logger.LogWarning("Úkol {Id} nebyl nalezen v lokální databázi", id);
                    return; // Úkol už neexistuje, není co mazat
                }

                // Pokud jsme online, přihlášeni a úkol má userId, smažeme z Firestore
                if (_connectivityService.IsConnected && await _authService.IsUserAuthenticated() &&
                    !string.IsNullOrEmpty(task.UserId))
                {
                    _logger.LogInformation("Mažu úkol {Id} z Firestore", id);
                    await _firestoreService.DeleteTaskAsync(id, task.UserId);
                }
                else if (!_connectivityService.IsConnected && await _authService.IsUserAuthenticated() &&
                         !string.IsNullOrEmpty(task.UserId))
                {
                    _logger.LogWarning("Nelze smazat úkol {Id} z Firestore - jsme offline", id);
                    _notificationService.ShowToast("Úkol byl smazán pouze lokálně. Synchronizace proběhne po připojení k internetu.", NotificationType.Warning);

                    // V tuto chvíli bychom ideálně měli označit úkol jako smazaný a synchronizovat později,
                    // ale pro jednoduchost ho jen smažeme lokálně
                }

                // Vždy smažeme z lokální databáze (použijeme stejnou instanci db a collection)
                collection.Delete(id);
                _logger.LogInformation("Úkol {Id} smazán z lokální databáze", id);

                // Aktualizujeme předem načtená data
                if (_preloadedTasks != null)
                {
                    _preloadedTasks.RemoveAll(t => t.Id == id);
                    _logger.LogInformation("Úkol {Id} odstraněn z předem načtených dat", id);
                }

                OnTasksChanged?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při mazání úkolu {Id}", id);
                throw;
            }
        }

        // Smažeme úkoly podle ID projektu
        public async Task DeleteTasksByProjectIdAsync(string projectId)
        {
            if (string.IsNullOrEmpty(projectId))
            {
                _logger.LogWarning("Pokus o smazání úkolů s neplatným ProjectId.");
                throw new ArgumentNullException(nameof(projectId), "ProjectId nemůže být null nebo prázdné.");
            }

            _logger.LogInformation("Mažu všechny úkoly pro projekt: {ProjectId}", projectId);

            try
            {
                string? currentUserId = null;
                bool isAuthenticated = false;
                try
                {
                    isAuthenticated = await _authService.IsUserAuthenticated();
                    if (isAuthenticated)
                    {
                        currentUserId = _authService.GetCurrentUserId();
                    }
                }
                catch (Exception authEx)
                {
                    _logger.LogWarning(authEx, "Problém při zjišťování autentizace při mazání úkolů projektu.");
                }
                // Pokud jsme offline, zkusíme získat poslední ID
                if (!isAuthenticated && string.IsNullOrEmpty(currentUserId))
                {
                    try { currentUserId = await SecureStorage.GetAsync("last_user_id"); } catch { /* Ignorujeme chybu zde */ }
                }


                var db = _dbConfig.GetDatabase();
                var collection = db.GetCollection<TaskItem>("tasks");

                // 1. Najdeme všechny lokální úkoly pro daný projekt a uživatele
                //    Použijeme přímý dotaz do DB místo GetTasksAsync, abychom se vyhnuli potenciálním problémům s cache
                List<TaskItem> tasksToDelete;
                if (string.IsNullOrEmpty(currentUserId))
                {
                    // Nepřihlášený uživatel maže jen své lokální (bez UserId) úkoly v projektu
                    tasksToDelete = collection.Find(t => t.ProjectId == projectId && t.UserId == null).ToList();
                }
                else
                {
                    // Přihlášený uživatel maže své úkoly (s jeho UserId nebo null) v projektu
                    tasksToDelete = collection.Find(t => t.ProjectId == projectId && (t.UserId == currentUserId || t.UserId == null)).ToList();
                }

                _logger.LogInformation("Nalezeno {Count} úkolů ke smazání pro projekt {ProjectId}", tasksToDelete.Count, projectId);

                if (!tasksToDelete.Any())
                {
                    _logger.LogInformation("Nebyly nalezeny žádné úkoly ke smazání pro projekt {ProjectId}", projectId);
                    return;
                }

                // 2. Zrušíme notifikace pro všechny tyto úkoly
                foreach (var task in tasksToDelete)
                {
                    _localSchedulerService.CancelRemindersForTaskAsync(task.Id);
                    _logger.LogDebug("Zrušeny naplánované připomínky pro úkol {Id} (pokud existovaly).", task.Id);
                }

                // 3. Smažeme úkoly z Firestore (pokud jsme online a přihlášeni)
                if (_connectivityService.IsConnected && isAuthenticated && !string.IsNullOrEmpty(currentUserId))
                {
                    _logger.LogInformation("Mažu {Count} úkolů projektu {ProjectId} z Firestore", tasksToDelete.Count, projectId);

                    // Firestore může mít optimalizaci pro batch delete, ale pokud ne, mažeme po jednom
                    // Pro jednoduchost zde použijeme cyklus s existující metodou
                    foreach (var task in tasksToDelete)
                    {
                        // Mažeme pouze úkoly, které mají UserId (byly synchronizovány)
                        if (!string.IsNullOrEmpty(task.UserId))
                        {
                            try
                            {
                                await _firestoreService.DeleteTaskAsync(task.Id, task.UserId);
                            }
                            catch (Exception fsEx)
                            {
                                _logger.LogError(fsEx, "Chyba při mazání úkolu {TaskId} z Firestore během hromadného mazání projektu {ProjectId}", task.Id, projectId);
                                // Pokračujeme s dalšími úkoly
                            }
                        }
                    }

                }
                else if (!_connectivityService.IsConnected && isAuthenticated && tasksToDelete.Any(t => !string.IsNullOrEmpty(t.UserId)))
                {
                    // Informujeme, že mazání proběhlo jen lokálně (pokud existovaly nějaké synchronizované úkoly)
                    _notificationService.ShowToast("Úkoly projektu byly smazány pouze lokálně. Synchronizace proběhne po připojení k internetu.", NotificationType.Warning);
                }

                // 4. Smažeme úkoly z lokální databáze
                _logger.LogInformation("Mažu {Count} úkolů projektu {ProjectId} z lokální LiteDB", tasksToDelete.Count, projectId);
                int deletedCount;
                if (string.IsNullOrEmpty(currentUserId))
                {
                    deletedCount = collection.DeleteMany(t => t.ProjectId == projectId && t.UserId == null);
                }
                else
                {
                    deletedCount = collection.DeleteMany(t => t.ProjectId == projectId && (t.UserId == currentUserId || t.UserId == null));
                }
                _logger.LogInformation("Skutečně smazáno {DeletedCount} úkolů z LiteDB.", deletedCount);


                // 5. Aktualizujeme cache (_preloadedTasks)
                if (_preloadedTasks != null)
                {
                    int removedFromCache;
                    if (string.IsNullOrEmpty(currentUserId))
                    {
                        removedFromCache = _preloadedTasks.RemoveAll(t => t.ProjectId == projectId && t.UserId == null);
                    }
                    else
                    {
                        removedFromCache = _preloadedTasks.RemoveAll(t => t.ProjectId == projectId && (t.UserId == currentUserId || t.UserId == null));
                    }
                    _logger.LogInformation("Odstraněno {RemovedCount} úkolů projektu {ProjectId} z cache.", removedFromCache, projectId);
                }

                // 6. Vyvoláme událost
                OnTasksChanged?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při mazání úkolů projektu {ProjectId}", projectId);
                throw; // Znovu vyhodíme výjimku, aby UI mohlo reagovat
            }
        }

        // Smažeme všechny úkoly, volitelně filtrované podle stavu
        public async Task ClearAllTasksAsync(string? filterStatus = null)
        {
            try
            {
                _logger.LogInformation("Mažu všechny úkoly {Filter}",
                string.IsNullOrEmpty(filterStatus) ? "bez filtru" : $"s filtrem {filterStatus}");

                string? currentUserId = null;
                bool isAuthenticated = false;

                try
                {
                    isAuthenticated = await _authService.IsUserAuthenticated();
                    if (isAuthenticated)
                    {
                        currentUserId = _authService.GetCurrentUserId();
                    }
                }
                catch (Exception authEx)
                {
                    _logger.LogWarning(authEx, "Problém při zjišťování autentizace");
                }

                // Pokud nemáme ID a jsme přihlášeni, zkusíme ho získat z historie
                if (string.IsNullOrEmpty(currentUserId) && !isAuthenticated)
                {
                    try
                    {
                        currentUserId = await SecureStorage.GetAsync("last_user_id");
                    }
                    catch (Exception secEx)
                    {
                        _logger.LogWarning(secEx, "Problém při načítání last_user_id ze SecureStorage");
                    }
                }

                // Optimalizace mazání z Firestore
                if (_connectivityService.IsConnected && isAuthenticated && !string.IsNullOrEmpty(currentUserId))
                {
                    // Optimalizace: pokud není filtr nebo je "all", použijeme hromadné mazání
                    if (string.IsNullOrEmpty(filterStatus) || filterStatus.ToLower() == "all")
                    {
                        // Efektivnější batch mazání všech úkolů uživatele
                        await _firestoreService.DeleteTasksForUserAsync(currentUserId);
                        _logger.LogInformation("Všechny úkoly uživatele byly hromadně smazány z Firestore");
                    }
                    else
                    {
                        // Pokud je zadán filtr, musíme úkoly nejprve načíst a pak smazat individuálně
                        try
                        {
                            var statusEnum = Enum.Parse<TaskItemStatus>(filterStatus);
                            var tasksToDelete = await _firestoreService.GetTasksByStatusAsync(currentUserId, statusEnum);

                            _logger.LogInformation("Nalezeno {Count} úkolů ve stavu {Status} ke smazání z Firestore",
                                tasksToDelete.Count, filterStatus);

                            foreach (var task in tasksToDelete)
                            {
                                if (!string.IsNullOrEmpty(task.UserId))
                                {
                                    await _firestoreService.DeleteTaskAsync(task.Id, task.UserId);
                                }
                            }
                        }
                        catch (ArgumentException)
                        {
                            _logger.LogWarning("Neplatný status filtr: {Status}", filterStatus);
                        }
                    }
                }
                else if (!_connectivityService.IsConnected && isAuthenticated)
                {
                    _notificationService.ShowToast("Úkoly byly smazány pouze lokálně. Synchronizace proběhne po připojení k internetu.", NotificationType.Warning);
                }

                // Smažeme všechny úkoly z lokální databáze
                var db = _dbConfig.GetDatabase();
                var collection = db.GetCollection<TaskItem>("tasks");

                // Získání seznamu úkolů k mazání podle filtrů
                List<TaskItem> localTasksToDelete;
                if (string.IsNullOrEmpty(filterStatus) || filterStatus.ToLower() == "all")
                {
                    if (string.IsNullOrEmpty(currentUserId))
                    {
                        localTasksToDelete = collection.Find(t => t.UserId == null).ToList();
                    }
                    else
                    {
                        localTasksToDelete = collection.Find(t => t.UserId == currentUserId || t.UserId == null).ToList();
                    }
                }
                else
                {
                    try
                    {
                        var statusEnum = Enum.Parse<TaskItemStatus>(filterStatus);

                        if (string.IsNullOrEmpty(currentUserId))
                        {
                            localTasksToDelete = collection.Find(t => t.UserId == null && t.Status == statusEnum).ToList();
                        }
                        else
                        {
                            localTasksToDelete = collection.Find(t => (t.UserId == currentUserId || t.UserId == null) &&
                                                         t.Status == statusEnum).ToList();
                        }
                    }
                    catch (ArgumentException)
                    {
                        _logger.LogWarning("Neplatný status filtr: {Status}", filterStatus);
                        localTasksToDelete = new List<TaskItem>();
                    }
                }

                _logger.LogInformation("Zrušení notifikací pro {Count} úkolů před jejich smazáním", localTasksToDelete.Count);
                foreach (var task in localTasksToDelete)
                {
                    _localSchedulerService.CancelRemindersForTaskAsync(task.Id);
                    _logger.LogDebug("Zrušeny naplánované připomínky pro úkol {Id} (pokud existovaly).", task.Id);
                }

                // Získáme seznam úkolů, které mají být smazány
                if (string.IsNullOrEmpty(filterStatus) || filterStatus.ToLower() == "all")
                {
                    // Smažeme všechny úkoly bez ohledu na status
                    if (string.IsNullOrEmpty(currentUserId))
                    {
                        collection.DeleteMany(t => t.UserId == null);
                    }
                    else
                    {
                        collection.DeleteMany(t => t.UserId == currentUserId || t.UserId == null);
                    }
                }
                else
                {
                    // Pro konkrétní status
                    try
                    {
                        var statusEnum = Enum.Parse<TaskItemStatus>(filterStatus);

                        if (string.IsNullOrEmpty(currentUserId))
                        {
                            collection.DeleteMany(t => t.UserId == null && t.Status == statusEnum);
                        }
                        else
                        {
                            collection.DeleteMany(t => (t.UserId == currentUserId || t.UserId == null) &&
                                                   t.Status == statusEnum);
                        }
                    }
                    catch (ArgumentException)
                    {
                        _logger.LogWarning("Neplatný status filtr: {Status}", filterStatus);
                        throw;
                    }
                }

                // Aktualizujeme předem načtená data
                if (_preloadedTasks != null)
                {
                    if (string.IsNullOrEmpty(filterStatus) || filterStatus.ToLower() == "all")
                    {
                        if (string.IsNullOrEmpty(currentUserId))
                        {
                            _preloadedTasks.RemoveAll(t => t.UserId == null);
                        }
                        else
                        {
                            _preloadedTasks.RemoveAll(t => t.UserId == currentUserId || t.UserId == null);
                        }
                    }
                    else
                    {
                        try
                        {
                            var statusEnum = Enum.Parse<TaskItemStatus>(filterStatus);

                            if (string.IsNullOrEmpty(currentUserId))
                            {
                                _preloadedTasks.RemoveAll(t => t.UserId == null && t.Status == statusEnum);
                            }
                            else
                            {
                                _preloadedTasks.RemoveAll(t => (t.UserId == currentUserId || t.UserId == null) &&
                                                        t.Status == statusEnum);
                            }
                        }
                        catch (ArgumentException)
                        {
                            _logger.LogWarning("Neplatný status filtr pro _preloadedTasks: {Status}", filterStatus);
                        }
                    }

                    _logger.LogInformation("Předem načtená data aktualizována, zůstalo {Count} úkolů", _preloadedTasks.Count);
                }

                OnTasksChanged?.Invoke();
                _logger.LogInformation("Všechny úkoly úspěšně smazány");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při mazání všech úkolů");
                throw;
            }
        }

        // OPERACE S ÚKOLY
        // Získáme úkoly podle jejich stavu
        public async Task<List<TaskItem>> GetTasksByStatusAsync(TaskItemStatus status)
        {
            try
            {
                _logger.LogInformation("Načítám úkoly se stavem: {Status}", status);

                string? currentUserId = null;
                if (await _authService.IsUserAuthenticated())
                {
                    currentUserId = _authService.GetCurrentUserId();
                }

                // Pokud máme předem načtená data, použijeme je pro filtrování
                if (_preloadedTasks != null)
                {
                    IEnumerable<TaskItem> filteredTasks;
                    if (string.IsNullOrEmpty(currentUserId))
                    {
                        filteredTasks = _preloadedTasks.Where(t => t.Status == status && t.UserId == null);
                    }
                    else
                    {
                        filteredTasks = _preloadedTasks.Where(t => t.Status == status &&
                                                       (t.UserId == currentUserId || t.UserId == null));
                    }

                    var result = filteredTasks.OrderByDescending(t => t.CreatedAt).ToList();
                    _logger.LogInformation("Filtrováno {Count} úkolů se stavem {Status} z předem načtených dat",
                                          result.Count, status);
                    return result;
                }

                // Načteme z lokální databáze
                var db = _dbConfig.GetDatabase();
                var collection = db.GetCollection<TaskItem>("tasks");
                List<TaskItem> tasks;

                IEnumerable<TaskItem> query;

                if (string.IsNullOrEmpty(currentUserId))
                {
                    query = collection.Find(t => t.Status == status && t.UserId == null);
                }
                else
                {
                    query = collection.Find(t => t.Status == status && (t.UserId == currentUserId || t.UserId == null));
                }
                tasks = query.OrderByDescending(t => t.CreatedAt).ToList();

                _logger.LogInformation("Načteno {Count} úkolů se stavem {Status} z lokální databáze", tasks.Count, status);
                return tasks;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při načítání úkolů podle stavu");
                return new List<TaskItem>();
            }
        }

        // Získáme úkoly po termínu
        public async Task<List<TaskItem>> GetOverdueTasks()
        {
            try
            {
                _logger.LogInformation("Načítám úkoly po termínu");

                string? currentUserId = null;
                if (await _authService.IsUserAuthenticated())
                {
                    currentUserId = _authService.GetCurrentUserId();
                }

                var now = DateTime.UtcNow;

                // Pokud máme předem načtená data, použijeme je pro filtrování
                if (_preloadedTasks != null)
                {
                    IEnumerable<TaskItem> filteredTasks;

                    if (string.IsNullOrEmpty(currentUserId))
                    {
                        filteredTasks = _preloadedTasks.Where(t => t.DueDate < now &&
                                                   t.Status != TaskItemStatus.Completed);
                    }
                    else
                    {
                        filteredTasks = _preloadedTasks.Where(t => t.DueDate < now &&
                                                   t.Status != TaskItemStatus.Completed &&
                                                   (t.UserId == currentUserId || t.UserId == null));
                    }

                    var result = filteredTasks.OrderBy(t => t.DueDate).ToList();
                    _logger.LogInformation("Filtrováno {Count} úkolů po termínu z předem načtených dat", result.Count);
                    return result;
                }

                // Načteme z lokální databáze
                var db = _dbConfig.GetDatabase();
                var collection = db.GetCollection<TaskItem>("tasks");
                List<TaskItem> tasks;

                IEnumerable<TaskItem> query;

                if (string.IsNullOrEmpty(currentUserId))
                {
                    query = collection.Find(t => t.DueDate < now && t.Status != TaskItemStatus.Completed);
                }
                else
                {
                    query = collection.Find(t => t.DueDate < now && t.Status != TaskItemStatus.Completed &&
                                            (t.UserId == currentUserId || t.UserId == null));
                }
                tasks = query.OrderBy(t => t.DueDate).ToList();

                _logger.LogInformation("Načteno {Count} úkolů po termínu z lokální databáze", tasks.Count);
                return tasks;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při načítání úkolů po termínu");
                return new List<TaskItem>();
            }
        }

        // Označíme úkol jako dokončený
        public async Task MarkTaskAsCompletedAsync(string id)
        {
            try
            {
                _logger.LogInformation("Označuji úkol jako dokončený: {Id}", id);

                // Načteme úkol
                var db = _dbConfig.GetDatabase();
                var collection = db.GetCollection<TaskItem>("tasks");
                TaskItem? task = collection.FindById(id);

                if (task == null)
                {
                    _logger.LogWarning("Úkol {Id} nebyl nalezen", id);
                    throw new KeyNotFoundException($"Úkol s ID {id} nebyl nalezen");
                }

                // Označíme jako dokončený
                task.Status = TaskItemStatus.Completed;
                task.CompletedAt = DateTime.UtcNow;

                // Aktualizujeme úkol
                await UpdateTaskAsync(task);
                _localSchedulerService.CancelRemindersForTaskAsync(id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při označování úkolu jako dokončeného {Id}", id);
                throw;
            }
        }

        // SYNCHRONIZACE A NAČÍTÁNÍ
        // Předběžně načteme všechny úkoly do cache
        public async Task PreloadAllTasksAsync()
        {
            _logger.LogInformation("Předběžné načítání všech úkolů (eager loading)");

            try
            {
                // Zde použijeme původní implementaci pro načtení
                _preloadedTasks = await GetTasksAsync();
                _logger.LogInformation("Předběžně načteno {Count} úkolů", _preloadedTasks.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při předběžném načítání úkolů");
                _preloadedTasks = new List<TaskItem>();
            }
        }

        // Synchronizujeme úkoly po přihlášení uživatele
        public async Task SynchronizeTasksOnLoginAsync(string userId)
        {
            _logger.LogInformation("Synchronizuji úkoly po přihlášení pro uživatele {UserId}", userId);
            try
            {
                // Deklarace databázových proměnných na začátku funkce
                var db = _dbConfig.GetDatabase();
                var collection = db.GetCollection<TaskItem>("tasks");

                // 1. Najdeme všechny lokální úkoly bez userId
                List<TaskItem> localTasksWithoutUser = collection
                    .Find(t => t.UserId == null)
                    .ToList();

                // 2. Přiřadíme userId těmto úkolům
                foreach (var task in localTasksWithoutUser)
                {
                    task.UserId = userId;
                }

                // 3. Aktualizujeme v lokální databázi - použijeme stejnou kolekci
                foreach (var task in localTasksWithoutUser)
                {
                    collection.Update(task);
                }

                // 4. Nahrajeme do Firestore, pokud jsme online
                if (_connectivityService.IsConnected && localTasksWithoutUser.Any())
                {
                    foreach (var task in localTasksWithoutUser)
                    {
                        await _firestoreService.SaveTaskAsync(task);
                    }
                }

                // 5. Stáhneme úkoly uživatele z Firestore
                if (_connectivityService.IsConnected)
                {
                    var firestoreTasks = await _firestoreService.GetTasksForUserAsync(userId);
                    // 6. Uložíme je do lokální databáze, pokud tam ještě nejsou - použijeme stejnou kolekci
                    foreach (var task in firestoreTasks)
                    {
                        if (collection.Exists(t => t.Id == task.Id))
                        {
                            collection.Update(task);
                        }
                        else
                        {
                            collection.Insert(task);
                        }
                    }
                }

                // Explicitně resetujeme cache a načteme data přímo z DB
                _preloadedTasks = null; // Kompletně resetujeme cache
                _preloadedTasks = collection.FindAll().OrderByDescending(t => t.CreatedAt).ToList();
                _logger.LogInformation("Cache úplně resetována po přihlášení - načteno {Count} úkolů přímo z DB", _preloadedTasks?.Count ?? 0);
                                    

                OnTasksChanged?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při synchronizaci úkolů po přihlášení");
            }
        }

        // Synchronizujeme úkoly po obnovení připojení k internetu
        public async Task SynchronizeTasksOnConnectionRestoredAsync()
        {
            if (!await _authService.IsUserAuthenticated())
            {
                _logger.LogInformation("Synchronizace přeskočena - uživatel není přihlášen");
                return;
            }
            try
            {
                _logger.LogInformation("=== ZAČÁTEK SYNCHRONIZACE ÚKOLŮ PO OBNOVENÍ PŘIPOJENÍ ===");

                // Deklarace databázových proměnných na začátku funkce
                var db = _dbConfig.GetDatabase();
                var collection = db.GetCollection<TaskItem>("tasks");

                // Najdeme všechny úkoly, které potřebují synchronizaci
                List<TaskItem> tasksToSync = collection
                    .Find(t => t.NeedsSynchronization)
                    .ToList();
                _logger.LogInformation("Nalezeno {Count} úkolů čekajících na synchronizaci", tasksToSync.Count);

                // Nahrajeme je do Firestore a aktualizujeme příznak
                foreach (var task in tasksToSync)
                {
                    _logger.LogInformation("Synchronizuji úkol {Id}: {Title}", task.Id, task.Title);
                    await _firestoreService.SaveTaskAsync(task);

                    // Použijeme stejnou kolekci pro aktualizaci
                    task.NeedsSynchronization = false;
                    collection.Update(task);
                    _logger.LogInformation("Příznak synchronizace odstraněn pro úkol {Id}", task.Id);

                    // Explicitně aktualizujeme úkol i v _preloadedTasks
                    if (_preloadedTasks != null)
                    {
                        var cachedTask = _preloadedTasks.FirstOrDefault(t => t.Id == task.Id);
                        if (cachedTask != null)
                        {
                            cachedTask.NeedsSynchronization = false;
                            _logger.LogInformation("Příznak synchronizace odstraněn pro úkol {Id} v cache", task.Id);
                        }
                    }
                }

                // Explicitně aktualizujeme celý _preloadedTasks pomocí skutečného dotazu do DB
                _preloadedTasks = null; // Kompletní reset cache
                _preloadedTasks = collection.FindAll().OrderByDescending(t => t.CreatedAt).ToList();
                _logger.LogInformation("Cache úplně resetována po synchronizaci - načteno {Count} úkolů přímo z DB",
                                      _preloadedTasks.Count);

                _logger.LogInformation("=== KONEC SYNCHRONIZACE ÚKOLŮ PO OBNOVENÍ PŘIPOJENÍ ===");
                OnTasksChanged?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při synchronizaci úkolů po obnovení připojení");
            }
        }

        // POMOCNÉ METODY
        // Vyvoláme událost o změně úkolů
        public void RaiseTasksChangedEvent()
        {
            OnTasksChanged?.Invoke();
        }

        // Vymažeme interní cache služby
        public void ClearCache()
        {
            _preloadedTasks = null;
            _logger.LogInformation("TaskService cache cleared.");
        }

        // EVENT HANDLERY
        // Reagujeme na změnu stavu připojení k internetu
        private async void OnConnectivityChanged(bool isConnected)
        {
            _logger.LogInformation("Detekována změna připojení: {State}", isConnected ? "Online" : "Offline");

            // Kontrolujeme nastavení
            bool autoSync = Preferences.Default.Get(AUTO_SYNC_KEY, true);

            if (isConnected && autoSync && await _authService.IsUserAuthenticated())
            {
                _logger.LogInformation("Internet obnoven, zahajuji synchronizaci úkolů");
                await SynchronizeTasksOnConnectionRestoredAsync();
            }
            else if (isConnected && !autoSync)
            {
                _logger.LogInformation("Internet obnoven, ale automatická synchronizace je vypnuta");
            }
        }

        // Reagujeme na přihlášení uživatele
        private async void OnUserLoggedIn(string userId)
        {
            try
            {
                // Kontrolujeme nastavení
                bool autoSync = Preferences.Default.Get(AUTO_SYNC_KEY, true);

                if (autoSync)
                {
                    _logger.LogInformation("Zahájení synchronizace úkolů po přihlášení uživatele {UserId}", userId);
                    await SynchronizeTasksOnLoginAsync(userId);
                }
                else
                {
                    _logger.LogInformation("Automatická synchronizace po přihlášení je vypnuta. UserId: {UserId}", userId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při synchronizaci úkolů po přihlášení");
            }
        }

        // UVOLNĚNÍ ZDROJŮ
        // Uvolní zdroje komponenty - odregistruje event handlery pro předejití memory leaků
        public void Dispose()
        {
            _connectivityService.ConnectivityChanged -= OnConnectivityChanged;
            _authService.UserLoggedIn -= OnUserLoggedIn;
        }
    }
}