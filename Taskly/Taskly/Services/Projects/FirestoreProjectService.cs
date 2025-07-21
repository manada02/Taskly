// FirestoreProjectService.cs - Služba pro práci s projekty v databázi Firestore
using Google.Cloud.Firestore;
using Microsoft.Extensions.Logging;
using Taskly.Firebase_config;
using Taskly.Models;

namespace Taskly.Services.Projects
{
    public class FirestoreProjectService
    {
        // PROMĚNNÉ A ZÁVISLOSTI
        private readonly FirestoreDb _firestoreDb;
        private readonly ILogger<FirestoreProjectService> _logger;
        private const string PROJECTS_COLLECTION = "projects";
        private const string USER_PROJECTS_COLLECTION = "userProjects"; // Název subkolekce

        // KONSTRUKTOR
        public FirestoreProjectService(ILogger<FirestoreProjectService> logger)
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
                throw new InvalidOperationException("Kritická chyba při inicializaci Firestore", ex);
            }
        }

        // OPERACE S PROJEKTY
        // Uložíme projekt do Firestore
        public async Task<ProjectItem> SaveProjectAsync(ProjectItem project)
        {
            try
            {
                // Kontrola, zda projekt má všechny potřebné hodnoty
                if (string.IsNullOrEmpty(project.UserId))
                {
                    _logger.LogWarning("Ukládání projektu do Firestore selhalo: Chybí UserId");
                    return project; // Nelze uložit projekt bez uživatele
                }

                // Zajistíme, aby timestamp byl v UTC
                if (project.CreatedAt.Kind != DateTimeKind.Utc)
                {
                    project.CreatedAt = project.CreatedAt.ToUniversalTime();
                }

                _logger.LogInformation("Začínám ukládat projekt do Firestore: ID={Id}, Name={Name}, UserId={UserId}",
                    project.Id, project.Name, project.UserId);

                // Uložíme projekt do subkolekce uživatele
                await _firestoreDb
                    .Collection(PROJECTS_COLLECTION)
                    .Document(project.UserId)
                    .Collection(USER_PROJECTS_COLLECTION)
                    .Document(project.Id)
                    .SetAsync(project);

                _logger.LogInformation("Projekt úspěšně uložen do Firestore: {Id} pro uživatele {UserId}",
                    project.Id, project.UserId);

                return project;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při ukládání projektu do Firestore: {Id}", project.Id);
                throw;
            }
        }

        // Získáme jeden projekt podle ID
        public async Task<ProjectItem?> GetProjectAsync(string projectId, string userId)
        {
            try
            {
                _logger.LogInformation("Načítám projekt {ProjectId} pro uživatele {UserId}", projectId, userId);

                var docRef = _firestoreDb
                    .Collection(PROJECTS_COLLECTION)
                    .Document(userId)
                    .Collection(USER_PROJECTS_COLLECTION)
                    .Document(projectId);

                var snapshot = await docRef.GetSnapshotAsync();

                if (!snapshot.Exists)
                {
                    _logger.LogWarning("Projekt {ProjectId} pro uživatele {UserId} nebyl nalezen", projectId, userId);
                    return null;
                }

                var project = snapshot.ConvertTo<ProjectItem>();
                _logger.LogInformation("Projekt {ProjectId} úspěšně načten z Firestore", projectId);
                return project;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při načítání projektu {ProjectId} z Firestore", projectId);
                return null;
            }
        }

        // Získáme všechny projekty pro konkrétního uživatele
        public async Task<List<ProjectItem>> GetProjectsForUserAsync(string userId)
        {
            _logger.LogInformation("Začínám načítat projekty z Firestore pro uživatele {UserId}", userId);

            if (string.IsNullOrEmpty(userId))
            {
                _logger.LogWarning("GetProjectsForUserAsync: Požadavek s prázdným userId");
                return new List<ProjectItem>();
            }

            try
            {
                var query = _firestoreDb
                    .Collection(PROJECTS_COLLECTION)
                    .Document(userId)
                    .Collection(USER_PROJECTS_COLLECTION);

                var snapshot = await query.GetSnapshotAsync();

                _logger.LogDebug("Firestore vrátil {Count} projektů pro uživatele {UserId}",
                    snapshot.Count, userId);

                var projects = new List<ProjectItem>();

                foreach (var doc in snapshot.Documents)
                {
                    try
                    {
                        var project = doc.ConvertTo<ProjectItem>();
                        projects.Add(project);
                    }
                    catch (Exception convEx)
                    {
                        _logger.LogError(convEx, "Chyba při konverzi dokumentu {DocId}", doc.Id);
                        // Pokračujeme s dalšími položkami
                    }
                }

                _logger.LogInformation("Úspěšně načteno {Count} projektů z Firestore pro uživatele {UserId}",
                    projects.Count, userId);

                return projects;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při načítání projektů z Firestore pro uživatele {UserId}",
                    userId);

                return new List<ProjectItem>();
            }
        }

        // Smažeme projekt z Firestore
        public async Task DeleteProjectAsync(string projectId, string userId)
        {
            try
            {
                await _firestoreDb
                    .Collection(PROJECTS_COLLECTION)
                    .Document(userId)
                    .Collection(USER_PROJECTS_COLLECTION)
                    .Document(projectId)
                    .DeleteAsync();

                _logger.LogInformation("Projekt {Id} úspěšně smazán z Firestore pro uživatele {UserId}", projectId, userId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při mazání projektu {Id} z Firestore", projectId);
                throw;
            }
        }

        // Smažeme všechny projekty pro konkrétního uživatele
        public async Task DeleteProjectsForUserAsync(string userId)
        {
            try
            {
                var snapshot = await _firestoreDb
                    .Collection(PROJECTS_COLLECTION)
                    .Document(userId)
                    .Collection(USER_PROJECTS_COLLECTION)
                    .GetSnapshotAsync();

                _logger.LogInformation("Nalezeno {Count} projektů k vymazání z Firestore", snapshot.Count);

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

                _logger.LogInformation("Všechny projekty uživatele {UserId} byly vymazány z Firestore", userId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při mazání projektů z Firestore pro uživatele {UserId}", userId);
                throw;
            }
        }
    }
}
