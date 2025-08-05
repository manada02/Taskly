// ProjectService.cs - Služba pro práci s projekty
using Microsoft.Extensions.Logging;
using Taskly.LocalStorage;
using Taskly.Models;
using Taskly.Services.Auth;
using Taskly.Services.Connectivity;
using Taskly.Services.Tasks;
using Taskly.Services.Cache;
using Taskly.Services.Notification.LocalNotification;
using Taskly.Services.Notification;

namespace Taskly.Services.Projects
{
    public class ProjectService : IProjectService, IDisposable
    {
        // PROMĚNNÉ A ZÁVISLOSTI
        private readonly LiteDbConfig _dbConfig;
        private readonly ILogger<ProjectService> _logger;
        private readonly FirestoreProjectService _firestoreService;
        private readonly IAuthService _authService;
        private readonly ConnectivityService _connectivityService;
        private readonly ITaskService _taskService;
        private readonly INotificationService _notificationService;
        private readonly ILocalNotificationSchedulerService _localSchedulerService;

        private const string AUTO_SYNC_KEY = "app_auto_sync";

        // Předem načtené projekty pro rychlý přístup
        private List<ProjectItem>? _preloadedProjects;

        // Událost informující o změně projektů
        public event Action? OnProjectsChanged;

        // KONSTRUKTOR
        public ProjectService(
            LiteDbConfig dbConfig,
            ILogger<ProjectService> logger,
            FirestoreProjectService firestoreService,
            IAuthService authService,
            ConnectivityService connectivityService,
            ITaskService taskService,
            INotificationService notificationService,
            ILocalNotificationSchedulerService localSchedulerService)
        {
            _dbConfig = dbConfig;
            _logger = logger;
            _firestoreService = firestoreService;
            _authService = authService;
            _connectivityService = connectivityService;
            _taskService = taskService;
            _notificationService = notificationService;
            _localSchedulerService = localSchedulerService;

            // Registrujeme se na změny připojení
            _connectivityService.ConnectivityChanged += OnConnectivityChanged;

            // Registrujeme se na přihlášení uživatele
            _authService.UserLoggedIn += OnUserLoggedIn;

            _logger.LogInformation("ProjectService inicializován");
        }

        // ZÁKLADNÍ OPERACE
        // Vytvoříme nový projekt v databázi
        public async Task<ProjectItem> CreateProjectAsync(ProjectItem project)
        {
            // Nastavíme ID a timestamp, pokud chybí
            if (string.IsNullOrEmpty(project.Id))
                project.Id = Guid.NewGuid().ToString();

            if (project.CreatedAt == default)
                project.CreatedAt = DateTime.UtcNow;

            // Nastavíme userId, pokud je uživatel přihlášen
            if (await _authService.IsUserAuthenticated())
            {
                project.UserId = _authService.GetCurrentUserId();
            }

            try
            {
                _logger.LogInformation("Vytvářím projekt: {Id}, {Name}, IsAuth: {IsAuth}, IsConnected: {IsConn}",
                    project.Id, project.Name, await _authService.IsUserAuthenticated(), _connectivityService.IsConnected);

                // Zkontrolujeme, zda jsme online a přihlášeni
                bool isOnlineAndAuthenticated = _connectivityService.IsConnected && await _authService.IsUserAuthenticated();

                // Nastavíme příznak synchronizace podle stavu připojení
                project.NeedsSynchronization = await _authService.IsUserAuthenticated() && !_connectivityService.IsConnected;

                if (project.NeedsSynchronization)
                {
                    _logger.LogInformation("OFFLINE REŽIM: Nastavuji NeedsSynchronization=true pro projekt {Id}", project.Id);
                }

                // Pokud jsme online a přihlášeni, uložíme nejprve do Firestore
                if (isOnlineAndAuthenticated)
                {
                    _logger.LogInformation("ONLINE REŽIM: Ukládám projekt {Id} do Firestore", project.Id);
                    await _firestoreService.SaveProjectAsync(project);
                }

                // Vždy uložíme do lokální databáze
                var db = _dbConfig.GetDatabase();
                var collection = db.GetCollection<ProjectItem>("projects");
                collection.Insert(project);
                _logger.LogInformation("Projekt {Id} uložen do lokální LiteDB", project.Id);

                // Aktualizujeme předem načtená data
                if (_preloadedProjects != null)
                {
                    _preloadedProjects.Add(project);

                    // Zachováme stejné řazení jako při načítání
                    _preloadedProjects = _preloadedProjects.OrderByDescending(p => p.CreatedAt).ToList();
                    _logger.LogInformation("Projekt {Id} přidán do předem načtených dat", project.Id);
                }

                // Naplánujeme systémové notifikace pro nový projekt
                await _localSchedulerService.ScheduleRemindersForProjectAsync(project);

                OnProjectsChanged?.Invoke();
                return project;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při vytváření projektu");
                throw;
            }
        }

        // Získáme projekt podle ID
        public async Task<ProjectItem?> GetProjectAsync(string id)
        {
            try
            {
                // Nejprve zkusíme najít v cache
                if (_preloadedProjects != null)
                {
                    var cachedProject = _preloadedProjects.FirstOrDefault(p => p.Id == id);
                    if (cachedProject != null)
                    {
                        _logger.LogInformation("Projekt {Id} nalezen v předem načtených datech", id);
                        return cachedProject;
                    }
                }

                // Pokud není v cache, načteme z lokální databáze
                ProjectItem? project = null;
                var db = _dbConfig.GetDatabase();
                var collection = db.GetCollection<ProjectItem>("projects");

                // await Task.Run pro asynchronní provádění
                project = await Task.Run(() => collection.FindById(id));

                return project;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při načítání projektu {Id}", id);
                return null;
            }
        }

        // Získáme seznam všech projektů
        public async Task<List<ProjectItem>> GetProjectsAsync()
        {
            // Pokud máme předem načtená data, použijeme je
            if (_preloadedProjects != null)
            {
                _logger.LogInformation("Vracím {Count} předem načtených projektů", _preloadedProjects.Count);
                return _preloadedProjects.ToList(); // Vrátíme kopii, aby nedošlo k neočekávaným změnám
            }

            _logger.LogInformation("Načítám projekty");

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

                        // Uložíme do SecureStorage pouze pokud ID není null
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

                // Pokud nemáme ID, zkusíme ho získat z historie
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

                // Vytvoření proměnných jen jednou pro celou metodu
                var db = _dbConfig.GetDatabase();
                var collection = db.GetCollection<ProjectItem>("projects");
                _logger.LogInformation("Připojeno k lokální LiteDB databázi");

                // Načteme z lokální databáze
                List<ProjectItem> projects;
                IEnumerable<ProjectItem> query;

                if (string.IsNullOrEmpty(currentUserId))
                {
                    _logger.LogInformation("Filtrujeme pouze projekty bez UserId (lokální)");
                    query = collection.Find(p => p.UserId == null);
                }
                else
                {
                    _logger.LogInformation("Filtrujeme projekty pro uživatele {UserId} a lokální", currentUserId);
                    query = collection.Find(p => p.UserId == currentUserId || p.UserId == null);
                }

                projects = query.OrderByDescending(p => p.CreatedAt).ToList();
                _logger.LogInformation("Načteno {Count} projektů z lokální databáze", projects.Count);

                // Pokud jsme online a přihlášeni, aktualizujeme z Firestore
                if (_connectivityService.IsConnected && isAuthenticated && !string.IsNullOrEmpty(currentUserId))
                {
                    try
                    {
                        _logger.LogInformation("Aktualizuji data z Firestore");
                        var firestoreProjects = await _firestoreService.GetProjectsForUserAsync(currentUserId);

                        if (firestoreProjects.Count > 0)
                        {
                            // Aktualizujeme lokální databázi - použijeme stejnou instanci kolekce
                            foreach (var project in firestoreProjects)
                            {
                                if (collection.Exists(p => p.Id == project.Id))
                                {
                                    collection.Update(project);
                                }
                                else
                                {
                                    collection.Insert(project);
                                }
                            }

                            // Aktualizujeme seznam projektů
                            var projectIds = projects.Select(p => p.Id).ToHashSet();
                            foreach (var project in firestoreProjects)
                            {
                                if (!projectIds.Contains(project.Id))
                                {
                                    projects.Add(project);
                                }
                                else
                                {
                                    var existingProject = projects.FirstOrDefault(p => p.Id == project.Id);
                                    if (existingProject != null)
                                    {
                                        int index = projects.IndexOf(existingProject);
                                        projects[index] = project;
                                    }
                                }
                            }

                            projects = projects.OrderByDescending(p => p.CreatedAt).ToList();
                        }
                    }
                    catch (Exception fireEx)
                    {
                        _logger.LogWarning(fireEx, "Nepodařilo se aktualizovat projekty z Firestore");
                    }
                }

                return projects;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při načítání projektů");
                return new List<ProjectItem>();
            }
        }

        // Aktualizujeme existující projekt
        public async Task<ProjectItem> UpdateProjectAsync(ProjectItem project)
        {
            try
            {
                _logger.LogInformation("Aktualizuji projekt: {Id}, {Name}", project.Id, project.Name);

                // Deklarace proměnných pro celou metodu
                var db = _dbConfig.GetDatabase();
                var collection = db.GetCollection<ProjectItem>("projects");

                // Kontrola, zda projekt existuje v lokální databázi
                ProjectItem? existingProject = collection.FindById(project.Id);

                if (existingProject == null)
                {
                    _logger.LogWarning("Projekt {Id} nebyl nalezen v lokální databázi", project.Id);
                    throw new KeyNotFoundException($"Projekt s ID {project.Id} nebyl nalezen");
                }

                // Zkontrolujeme, zda jsme online a přihlášeni
                bool isOnlineAndAuthenticated = _connectivityService.IsConnected && await _authService.IsUserAuthenticated();

                // Nastavíme příznak synchronizace podle stavu připojení
                if (await _authService.IsUserAuthenticated() && !_connectivityService.IsConnected)
                {
                    project.NeedsSynchronization = true;
                    _logger.LogInformation("OFFLINE REŽIM: Nastavuji NeedsSynchronization=true pro projekt {Id}", project.Id);
                }

                // Pokud jsme online a přihlášeni, aktualizujeme nejprve v Firestore
                if (isOnlineAndAuthenticated && !string.IsNullOrEmpty(project.UserId))
                {
                    _logger.LogInformation("ONLINE REŽIM: Aktualizuji projekt {Id} ve Firestore", project.Id);
                    await _firestoreService.SaveProjectAsync(project);
                    project.NeedsSynchronization = false;
                }

                // Vždy aktualizujeme v lokální databázi - použijeme stejnou instanci
                collection.Update(project);
                _logger.LogInformation("Projekt {Id} aktualizován v lokální LiteDB", project.Id);

                // Aktualizujeme předem načtená data
                if (_preloadedProjects != null)
                {
                    var index = _preloadedProjects.FindIndex(p => p.Id == project.Id);
                    if (index != -1)
                    {
                        _preloadedProjects[index] = project;
                        // Zachováme stejné řazení jako v původní implementaci
                        _preloadedProjects = _preloadedProjects.OrderByDescending(p => p.CreatedAt).ToList();
                        _logger.LogInformation("Projekt {Id} aktualizován v předem načtených datech", project.Id);
                    }
                }

                // Naplánujeme systémové notifikace pro projekt
                await _localSchedulerService.ScheduleRemindersForProjectAsync(project);

                OnProjectsChanged?.Invoke();
                return project;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při aktualizaci projektu {Id}", project.Id);
                throw;
            }
        }

        // Smažeme projekt podle ID
        public async Task DeleteProjectAsync(string id)
        {
            try
            {
                _logger.LogInformation("Mažu projekt: {Id} a všechny jeho úkoly", id);

                //  Zrušení systémové notifikace před mazáním
                _localSchedulerService.CancelRemindersForProjectAsync(id);
                _logger.LogDebug("Zrušeny naplánované připomínky pro projekt {Id} (pokud existovaly).", id);

                // Deklarace proměnných pro celou metodu
                var db = _dbConfig.GetDatabase();
                var collection = db.GetCollection<ProjectItem>("projects");

                // Načteme projekt
                ProjectItem? project = collection.FindById(id);

                if (project == null)
                {
                    _logger.LogWarning("Projekt {Id} nebyl nalezen v lokální databázi", id);
                    return;
                }

                // 1. Nejprve smažeme všechny úkoly v projektu
                _logger.LogInformation("Mažu úkoly pro projekt {Id}", id);
                var tasks = await _taskService.GetTasksAsync(id);
                foreach (var task in tasks)
                {
                    await _taskService.DeleteTaskAsync(task.Id);
                    _logger.LogInformation("Úkol {TaskId} v projektu {ProjectId} byl smazán", task.Id, id);
                }

                // 2. Potom smažeme projekt z Firestore (pokud jsme online)
                if (_connectivityService.IsConnected && await _authService.IsUserAuthenticated() && !string.IsNullOrEmpty(project.UserId))
                {
                    _logger.LogInformation("Mažu projekt {Id} z Firestore", id);
                    await _firestoreService.DeleteProjectAsync(id, project.UserId);
                }
                else if (!_connectivityService.IsConnected && await _authService.IsUserAuthenticated() && !string.IsNullOrEmpty(project.UserId))
                {
                    _notificationService.ShowToast("Projekt byl smazán pouze lokálně. Synchronizace proběhne po připojení k internetu.", NotificationType.Warning);
                }

                // 3. Nakonec smažeme projekt z lokální databáze - použijeme stejnou instanci
                collection.Delete(id);
                _logger.LogInformation("Projekt {Id} smazán z lokální databáze", id);

                // Aktualizujeme předem načtená data
                if (_preloadedProjects != null)
                {
                    _preloadedProjects.RemoveAll(p => p.Id == id);
                    _logger.LogInformation("Projekt {Id} odstraněn z předem načtených dat", id);
                }

                OnProjectsChanged?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při mazání projektu {Id}", id);
                throw;
            }
        }

        // Smažeme všechny projekty
        public async Task ClearAllProjectsAsync()
        {
            try
            {
                _logger.LogInformation("Mažu všechny projekty");

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

                // 1. Nejprve získáme všechny projekty, které chceme smazat
                var db = _dbConfig.GetDatabase();
                var collection = db.GetCollection<ProjectItem>("projects");
                List<ProjectItem> projectsToDelete;
                if (string.IsNullOrEmpty(currentUserId))
                {
                    projectsToDelete = collection.Find(p => p.UserId == null).ToList();
                }
                else
                {
                    projectsToDelete = collection.Find(p => p.UserId == currentUserId || p.UserId == null).ToList();
                }

                _logger.LogInformation("Nalezeno {Count} projektů ke smazání", projectsToDelete.Count);

                // 2. Pro každý projekt smažeme jeho úkoly
                foreach (var project in projectsToDelete)
                {
                    // Nejprve zrušíme systémové notifikace pro každý projekt
                    _localSchedulerService.CancelRemindersForProjectAsync(project.Id);
                    _logger.LogDebug("Zrušeny naplánované připomínky pro projekt {Id} (pokud existovaly).", project.Id);

                    // Poté smažeme úkoly projektu 
                    var tasks = await _taskService.GetTasksAsync(project.Id);
                    foreach (var task in tasks)
                    {
                        await _taskService.DeleteTaskAsync(task.Id);
                        _logger.LogInformation("Úkol {TaskId} v projektu {ProjectId} byl smazán", task.Id, project.Id);
                    }
                }

                // 3. Smažeme projekty z Firestore
                if (_connectivityService.IsConnected && isAuthenticated && !string.IsNullOrEmpty(currentUserId))
                {
                    await _firestoreService.DeleteProjectsForUserAsync(currentUserId);
                    _logger.LogInformation("Všechny projekty uživatele {UserId} byly smazány z Firestore", currentUserId);
                }
                else if (!_connectivityService.IsConnected && isAuthenticated)
                {
                    _notificationService.ShowToast("Projekty byly smazány pouze lokálně. Synchronizace proběhne po připojení k internetu.", NotificationType.Warning);
                }

                // 4. Smažeme projekty z lokální databáze
                if (string.IsNullOrEmpty(currentUserId))
                {
                    collection.DeleteMany(p => p.UserId == null);
                }
                else
                {
                    collection.DeleteMany(p => p.UserId == currentUserId || p.UserId == null);
                }

                // Aktualizujeme cache
                if (_preloadedProjects != null)
                {
                    if (string.IsNullOrEmpty(currentUserId))
                    {
                        _preloadedProjects.RemoveAll(p => p.UserId == null);
                    }
                    else
                    {
                        _preloadedProjects.RemoveAll(p => p.UserId == currentUserId || p.UserId == null);
                    }
                    _logger.LogInformation("Předem načtená data aktualizována, zůstalo {Count} projektů", _preloadedProjects.Count);
                }

                OnProjectsChanged?.Invoke();
                _logger.LogInformation("Všechny projekty a jejich úkoly byly úspěšně smazány");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při mazání všech projektů");
                throw;
            }
        }

        // SYNCHRONIZACE A NAČÍTÁNÍ
        // Synchronizujeme projekty po přihlášení uživatele
        public async Task SynchronizeProjectsOnLoginAsync(string userId)
        {
            _logger.LogInformation("Synchronizuji projekty po přihlášení pro uživatele {UserId}", userId);

            try
            {
                // 1. Najdeme všechny lokální projekty bez userId
                var db = _dbConfig.GetDatabase();
                var collection = db.GetCollection<ProjectItem>("projects");
                List<ProjectItem> localProjectsWithoutUser = collection.Find(p => p.UserId == null).ToList();

                _logger.LogInformation("Nalezeno {Count} lokálních projektů bez uživatele", localProjectsWithoutUser.Count);

                // 2. Přiřadíme userId těmto projektům
                foreach (var project in localProjectsWithoutUser)
                {
                    project.UserId = userId;
                }

                // 3. Aktualizujeme v lokální databázi - použijeme stejné proměnné
                foreach (var project in localProjectsWithoutUser)
                {
                    collection.Update(project);
                }

                // 4. Nahrajeme do Firestore, pokud jsme online
                if (_connectivityService.IsConnected && localProjectsWithoutUser.Any())
                {
                    _logger.LogInformation("Nahrávám {Count} lokálních projektů do Firestore", localProjectsWithoutUser.Count);
                    foreach (var project in localProjectsWithoutUser)
                    {
                        await _firestoreService.SaveProjectAsync(project);
                    }
                }

                // 5. Stáhneme projekty uživatele z Firestore
                if (_connectivityService.IsConnected)
                {
                    _logger.LogInformation("Stahuji projekty uživatele z Firestore");
                    var firestoreProjects = await _firestoreService.GetProjectsForUserAsync(userId);
                    _logger.LogInformation("Staženo {Count} projektů z Firestore", firestoreProjects.Count);

                    // 6. Uložíme je do lokální databáze, pokud tam ještě nejsou - použijeme stejné proměnné
                    foreach (var project in firestoreProjects)
                    {
                        if (collection.Exists(p => p.Id == project.Id))
                        {
                            collection.Update(project);
                        }
                        else
                        {
                            collection.Insert(project);
                        }
                    }
                }

                // Explicitně resetujeme cache a načteme data přímo z DB
                _preloadedProjects = null; // Kompletně resetujeme cache
                _preloadedProjects = collection.FindAll().OrderByDescending(p => p.CreatedAt).ToList();
                _logger.LogInformation("Cache úplně resetována po přihlášení - načteno {Count} projektů přímo z DB", _preloadedProjects?.Count ?? 0);
                                      
                OnProjectsChanged?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při synchronizaci projektů po přihlášení");
            }
        }

        // Synchronizujeme projekty po obnovení připojení k internetu
        public async Task SynchronizeProjectsOnConnectionRestoredAsync()
        {
            if (!await _authService.IsUserAuthenticated())
            {
                _logger.LogInformation("Synchronizace přeskočena - uživatel není přihlášen");
                return;
            }

            try
            {
                _logger.LogInformation("=== ZAČÁTEK SYNCHRONIZACE PROJEKTŮ PO OBNOVENÍ PŘIPOJENÍ ===");

                // Najdeme všechny projekty, které potřebují synchronizaci
                var db = _dbConfig.GetDatabase();
                var collection = db.GetCollection<ProjectItem>("projects");
                List<ProjectItem> projectsToSync = collection.Find(p => p.NeedsSynchronization).ToList();

                _logger.LogInformation("Nalezeno {Count} projektů čekajících na synchronizaci", projectsToSync.Count);

                // Nahrajeme je do Firestore a aktualizujeme příznak - použijeme stejné proměnné z výše
                foreach (var project in projectsToSync)
                {
                    _logger.LogInformation("Synchronizuji projekt {Id}: {Name}", project.Id, project.Name);
                    await _firestoreService.SaveProjectAsync(project);

                    // Zde použijeme stejné proměnné místo nového using bloku
                    project.NeedsSynchronization = false;
                    collection.Update(project);
                    _logger.LogInformation("Příznak synchronizace odstraněn pro projekt {Id}", project.Id);

                    // Explicitně aktualizujeme projekt i v _preloadedProjects
                    if (_preloadedProjects != null)
                    {
                        var cachedProject = _preloadedProjects.FirstOrDefault(p => p.Id == project.Id);
                        if (cachedProject != null)
                        {
                            cachedProject.NeedsSynchronization = false;
                            _logger.LogInformation("Příznak synchronizace odstraněn pro projekt {Id} v cache", project.Id);
                        }
                    }
                }

                // Explicitně aktualizujeme celý _preloadedProjects pomocí skutečného dotazu do DB
                _preloadedProjects = null; // Kompletní reset cache
                _preloadedProjects = collection.FindAll().OrderByDescending(p => p.CreatedAt).ToList();
                _logger.LogInformation("Cache úplně resetována po synchronizaci - načteno {Count} projektů přímo z DB",
                                      _preloadedProjects.Count);

                _logger.LogInformation("=== KONEC SYNCHRONIZACE PROJEKTŮ PO OBNOVENÍ PŘIPOJENÍ ===");
                OnProjectsChanged?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při synchronizaci projektů po obnovení připojení");
            }
        }

        // Předběžně načteme všechny projekty do cache
        public async Task PreloadAllProjectsAsync()
        {
            _logger.LogInformation("Předběžné načítání všech projektů (eager loading)");

            try
            {
                // Vynulujeme mezipaměť, aby nedošlo k cyklickému volání GetProjectsAsync
                _preloadedProjects = null;

                // Načteme projekty z databáze
                _preloadedProjects = await GetProjectsAsync();
                _logger.LogInformation("Předběžně načteno {Count} projektů", _preloadedProjects.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při předběžném načítání projektů");
                _preloadedProjects = new List<ProjectItem>();
            }
        }

        // STATISTIKY
        // Získáme počet úkolů pro konkrétní projekt
        public async Task<int> GetTaskCountForProjectAsync(string projectId)
        {
            try
            {
                var tasks = await _taskService.GetTasksAsync(projectId);
                return tasks.Count;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při počítání úkolů pro projekt {Id}", projectId);
                return 0;
            }
        }

        // Efektivně získáme počty úkolů pro všechny projekty najednou
        public async Task<Dictionary<string, int>> GetTaskCountsForAllProjectsAsync()
        {
            _logger.LogInformation("Získávám počty úkolů pro všechny projekty");

            // Slovník pro uložení počtu úkolů (klíč: ID projektu, hodnota: počet úkolů)
            var result = new Dictionary<string, int>();

            try
            {
                // Načtení všech úkolů jedním dotazem místo opakovaných dotazů
                var allTasks = await _taskService.GetTasksAsync();

                // Použití předem načtených projektů nebo jejich získání
                var projectsToProcess = _preloadedProjects ?? await GetProjectsAsync();

                // Výpočet počtu úkolů pro každý projekt
                foreach (var project in projectsToProcess)
                {
                    // Spočítáme úkoly patřící k tomuto projektu
                    result[project.Id] = allTasks.Count(t => t.ProjectId == project.Id);
                }

                _logger.LogInformation("Získány počty úkolů pro {Count} projektů", result.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při získávání počtů úkolů pro projekty");
            }

            return result;
        }

        // POMOCNÉ METODY
        // Vymažeme interní cache služby
        public void ClearCache()
        {
            _preloadedProjects = null;
            _logger.LogInformation("ProjectService cache cleared.");
        }

        // Vyvoláme událost o změně projektů
        public void RaiseProjectsChangedEvent()
        {
            OnProjectsChanged?.Invoke();
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
                _logger.LogInformation("Internet obnoven, zahajuji synchronizaci projektů");
                await SynchronizeProjectsOnConnectionRestoredAsync();
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
                    _logger.LogInformation("Zahájení synchronizace projektů po přihlášení uživatele {UserId}", userId);
                    await SynchronizeProjectsOnLoginAsync(userId);
                }
                else
                {
                    _logger.LogInformation("Automatická synchronizace po přihlášení je vypnuta. UserId: {UserId}", userId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při synchronizaci projektů po přihlášení");
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