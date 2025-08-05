// ITaskService.cs - Rozhraní pro službu práce s úkoly
using Taskly.Models;

namespace Taskly.Services.Tasks
{
    public interface ITaskService : IDisposable
    {
        // UDÁLOSTI A VLASTNOSTI
        // Událost informující o změně úkolů
        event Action? OnTasksChanged;

        // ZÁKLADNÍ OPERACE
        // Vytvoříme nový úkol v databázi
        Task<TaskItem> CreateTaskAsync(TaskItem task);

        // Získáme úkol podle ID
        Task<TaskItem?> GetTaskAsync(string id);

        // Získáme seznam úkolů, volitelně filtrovaný podle projektu
        Task<List<TaskItem>> GetTasksAsync(string? projectId = null);

        // Aktualizujeme existující úkol
        Task<TaskItem> UpdateTaskAsync(TaskItem task);

        // Smažeme úkol podle ID
        Task DeleteTaskAsync(string id);

        // Smažeme úkoly podle ID projektu
        Task DeleteTasksByProjectIdAsync(string projectId);

        // Smažeme všechny úkoly, volitelně filtrované podle stavu
        Task ClearAllTasksAsync(string? filterStatus = null);

        // OPERACE S ÚKOLY
        // Získáme úkoly podle jejich stavu
        Task<List<TaskItem>> GetTasksByStatusAsync(TaskItemStatus status);

        // Získáme úkoly po termínu
        Task<List<TaskItem>> GetOverdueTasks();

        // Označíme úkol jako dokončený
        Task MarkTaskAsCompletedAsync(string id);

        // SYNCHRONIZACE A NAČÍTÁNÍ
        // Synchronizujeme úkoly po přihlášení uživatele
        Task SynchronizeTasksOnLoginAsync(string userId);

        // Synchronizujeme úkoly po obnovení připojení k internetu
        Task SynchronizeTasksOnConnectionRestoredAsync();

        // Předběžně načteme všechny úkoly do cache
        Task PreloadAllTasksAsync();

        // POMOCNÉ METODY
        // Vyvoláme událost o změně úkolů
        void RaiseTasksChangedEvent();

        // Vymažeme interní cache služby
        void ClearCache();
    }
}
