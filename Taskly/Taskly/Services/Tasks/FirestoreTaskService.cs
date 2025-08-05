// FirestoreTaskService.cs - Služba pro práci s úkoly v databázi Firestore
using Google.Cloud.Firestore;
using Microsoft.Extensions.Logging;
using Taskly.Firebase_config;
using Taskly.Models;

namespace Taskly.Services.Tasks
{
    public class FirestoreTaskService
    {
        // PROMĚNNÉ A ZÁVISLOSTI
        private readonly FirestoreDb _firestoreDb;
        private readonly ILogger<FirestoreTaskService> _logger;
        private const string TASKS_COLLECTION = "tasks";
        private const string USER_TASKS_COLLECTION = "userTasks"; // Název subkolekce

        // KONSTRUKTOR
        public FirestoreTaskService(ILogger<FirestoreTaskService> logger)
        {
            _logger = logger;
            try
            {
                var db = FirestoreConfig.GetFirestoreDatabase();
                if (db == null)
                {
                    _logger.LogError("FirestoreDb je null - inicializace selhala");
                    throw new InvalidOperationException("Nepodařilo se inicializovat Firestore databázi");
                }

                _firestoreDb = db;
                _logger.LogInformation("FirestoreDb úspěšně inicializován, ProjectId: {ProjectId}",
                    _firestoreDb.ProjectId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kritická chyba při inicializaci FirestoreDb");

                // Převedeme výjimku na InvalidOperationException s původní výjimkou jako vnitřní
                throw new InvalidOperationException("Kritická chyba při inicializaci Firestore", ex);
            }
        }

        // OPERACE S ÚKOLY
        // Uložíme úkol do Firestore
        public async Task<TaskItem> SaveTaskAsync(TaskItem task)
        {
            try
            {
                // Kontrola, zda úkol má všechny potřebné hodnoty
                if (string.IsNullOrEmpty(task.UserId))
                {
                    _logger.LogWarning("Ukládání úkolu do Firestore selhalo: Chybí UserId");
                    return task; // Nelze uložit úkol bez uživatele
                }

                // Zajistíme, aby timestamps byly v UTC
                if (task.CreatedAt.Kind != DateTimeKind.Utc)
                {
                    task.CreatedAt = task.CreatedAt.ToUniversalTime();
                }

                if (task.DueDate.HasValue && task.DueDate.Value.Kind != DateTimeKind.Utc)
                {
                    task.DueDate = task.DueDate.Value.ToUniversalTime();
                }

                if (task.CompletedAt.HasValue && task.CompletedAt.Value.Kind != DateTimeKind.Utc)
                {
                    task.CompletedAt = task.CompletedAt.Value.ToUniversalTime();
                }

                _logger.LogInformation("Začínám ukládat úkol do Firestore: ID={Id}, Title={Title}, UserId={UserId}",
                    task.Id, task.Title, task.UserId);

                // Uložíme úkol do subkolekce uživatele
                await _firestoreDb
                    .Collection(TASKS_COLLECTION)
                    .Document(task.UserId)
                    .Collection(USER_TASKS_COLLECTION)
                    .Document(task.Id)
                    .SetAsync(task);

                _logger.LogInformation("Úkol úspěšně uložen do Firestore: {Id} pro uživatele {UserId}",
                    task.Id, task.UserId);

                return task;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při ukládání úkolu do Firestore: {Id}, Typ chyby: {ExType}, Zpráva: {ExMessage}",
                    task.Id, ex.GetType().Name, ex.Message);
                throw;
            }
        }

        // Získáme jeden úkol podle ID
        public async Task<TaskItem?> GetTaskAsync(string taskId, string userId)
        {
            try
            {
                _logger.LogInformation("Načítám úkol {TaskId} pro uživatele {UserId}", taskId, userId);

                var docRef = _firestoreDb
                    .Collection(TASKS_COLLECTION)
                    .Document(userId)
                    .Collection(USER_TASKS_COLLECTION)
                    .Document(taskId);

                var snapshot = await docRef.GetSnapshotAsync();

                if (!snapshot.Exists)
                {
                    _logger.LogWarning("Úkol {TaskId} pro uživatele {UserId} nebyl nalezen", taskId, userId);
                    return null;
                }

                var task = snapshot.ConvertTo<TaskItem>();
                _logger.LogInformation("Úkol {TaskId} úspěšně načten z Firestore", taskId);
                return task;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při načítání úkolu {TaskId} z Firestore", taskId);
                return null;
            }
        }

        // Získáme úkoly pro uživatele (volitelně filtrované podle projectId)
        public async Task<List<TaskItem>> GetTasksForUserAsync(string userId, string? projectId = null)
        {
            _logger.LogInformation("Začínám načítat úkoly z Firestore pro uživatele {UserId}", userId);

            if (string.IsNullOrEmpty(userId))
            {
                _logger.LogWarning("GetTasksForUserAsync: Požadavek s prázdným userId");
                return new List<TaskItem>();
            }

            try
            {
                // Deklarujeme jako Query od začátku
                Query query = _firestoreDb
                    .Collection(TASKS_COLLECTION)
                    .Document(userId)
                    .Collection(USER_TASKS_COLLECTION);

                // Přidáme filtr projektu, pokud byl zadán
                if (!string.IsNullOrEmpty(projectId))
                {
                    query = query.WhereEqualTo("ProjectId", projectId);
                }

                var snapshot = await query.GetSnapshotAsync();

                _logger.LogDebug("Firestore vrátil {Count} úkolů pro uživatele {UserId}",
                    snapshot.Count, userId);

                var tasks = new List<TaskItem>();

                foreach (var doc in snapshot.Documents)
                {
                    try
                    {
                        var task = doc.ConvertTo<TaskItem>();
                        tasks.Add(task);
                    }
                    catch (Exception convEx)
                    {
                        _logger.LogError(convEx, "Chyba při konverzi dokumentu {DocId}", doc.Id);
                        
                    }
                }

                _logger.LogInformation("Úspěšně načteno {Count} úkolů z Firestore pro uživatele {UserId}",
                    tasks.Count, userId);

                return tasks;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při načítání úkolů z Firestore pro uživatele {UserId}. Typ chyby: {ExType}, Zpráva: {ExMessage}",
                    userId, ex.GetType().Name, ex.Message);

                return new List<TaskItem>();
            }
        }

        // Získáme úkoly podle stavu
        public async Task<List<TaskItem>> GetTasksByStatusAsync(string userId, TaskItemStatus status)
        {
            try
            {
                _logger.LogInformation("Načítám úkoly se stavem {Status} pro uživatele {UserId}", status, userId);

                Query query = _firestoreDb
                    .Collection(TASKS_COLLECTION)
                    .Document(userId)
                    .Collection(USER_TASKS_COLLECTION)
                    .WhereEqualTo("Status", status);

                var snapshot = await query.GetSnapshotAsync();

                var tasks = new List<TaskItem>();
                foreach (var doc in snapshot.Documents)
                {
                    try
                    {
                        var task = doc.ConvertTo<TaskItem>();
                        tasks.Add(task);
                    }
                    catch (Exception convEx)
                    {
                        _logger.LogError(convEx, "Chyba při konverzi dokumentu {DocId}", doc.Id);
                    }
                }

                _logger.LogInformation("Načteno {Count} úkolů se stavem {Status} z Firestore", tasks.Count, status);
                return tasks;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při načítání úkolů podle stavu z Firestore");
                return new List<TaskItem>();
            }
        }

        // Smažeme úkol z Firestore
        public async Task DeleteTaskAsync(string taskId, string userId)
        {
            try
            {
                await _firestoreDb
                    .Collection(TASKS_COLLECTION)
                    .Document(userId)
                    .Collection(USER_TASKS_COLLECTION)
                    .Document(taskId)
                    .DeleteAsync();

                _logger.LogInformation("Úkol {Id} úspěšně smazán z Firestore pro uživatele {UserId}", taskId, userId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při mazání úkolu {Id} z Firestore", taskId);
                throw;
            }
        }

        // Smažeme všechny úkoly pro konkrétního uživatele
        public async Task DeleteTasksForUserAsync(string userId)
        {
            try
            {
                var snapshot = await _firestoreDb
                    .Collection(TASKS_COLLECTION)
                    .Document(userId)
                    .Collection(USER_TASKS_COLLECTION)
                    .GetSnapshotAsync();

                _logger.LogInformation("Nalezeno {Count} úkolů k vymazání z Firestore", snapshot.Count);

                // Batch size limit ve Firestore je 500, rozdělíme mazání do dávek
                const int batchSize = 450;

                for (int i = 0; i < snapshot.Count; i += batchSize)
                {
                    var batch = _firestoreDb.StartBatch();

                    foreach (var doc in snapshot.Documents.Skip(i).Take(batchSize))
                    {
                        batch.Delete(doc.Reference);
                    }

                    await batch.CommitAsync();
                }

                _logger.LogInformation("Všechny úkoly uživatele {UserId} byly vymazány z Firestore", userId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při mazání úkolů z Firestore pro uživatele {UserId}", userId);
                throw;
            }
        }
    }
}
