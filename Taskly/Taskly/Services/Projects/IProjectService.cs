// IProjectService.cs - Rozhraní pro službu práce s projekty
using System; 
using System.Collections.Generic; 
using System.Threading.Tasks;
using Taskly.Models;

namespace Taskly.Services.Projects
{
    public interface IProjectService : IDisposable
    {
        // UDÁLOSTI A VLASTNOSTI
        // Událost informující o změně projektů
        event Action? OnProjectsChanged;

        // ZÁKLADNÍ OPERACE
        // Vytvoříme nový projekt v databázi
        Task<ProjectItem> CreateProjectAsync(ProjectItem project);

        // Získáme projekt podle ID
        Task<ProjectItem?> GetProjectAsync(string id);

        // Získáme seznam všech projektů
        Task<List<ProjectItem>> GetProjectsAsync();

        // Aktualizujeme existující projekt
        Task<ProjectItem> UpdateProjectAsync(ProjectItem project);

        // Smažeme projekt podle ID
        Task DeleteProjectAsync(string id);

        // Smažeme všechny projekty
        Task ClearAllProjectsAsync();

        // SYNCHRONIZACE A NAČÍTÁNÍ
        // Synchronizujeme projekty po přihlášení uživatele
        Task SynchronizeProjectsOnLoginAsync(string userId);

        // Synchronizujeme projekty po obnovení připojení k internetu
        Task SynchronizeProjectsOnConnectionRestoredAsync();

        // Předběžně načteme všechny projekty do cache
        Task PreloadAllProjectsAsync();

        // STATISTIKY
        // Získáme počty úkolů pro všechny projekty
        Task<Dictionary<string, int>> GetTaskCountsForAllProjectsAsync();

        // Získáme počet úkolů pro konkrétní projekt
        Task<int> GetTaskCountForProjectAsync(string projectId);

        // POMOCNÉ METODY
        // Vyvoláme událost o změně projektů
        void RaiseProjectsChangedEvent();

        // Vymažeme interní cache služby
        void ClearCache();
    }
}
