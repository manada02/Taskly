// INotificationService.cs - Rozhraní pro službu správy notifikací
using Taskly.Models;

namespace Taskly.Services.Notification
{
    public interface INotificationService : IDisposable
    {
        // UDÁLOSTI A VLASTNOSTI
        // Událost informující o změně notifikací
        event Action? OnNotificationsChanged;

        // SPRÁVA NOTIFIKACÍ
        // Přidáme novou notifikaci do historie
        Task AddNotificationAsync(NotificationItem notification, bool showAfterForceLoad = false, bool showToast = true);

        // Získáme seznam všech notifikací
        Task<List<NotificationItem>> GetNotificationsAsync();

        // Vymažeme všechny notifikace z historie
        Task ClearNotificationsAsync();

        // Smažeme konkrétní notifikaci podle ID
        Task DeleteNotificationAsync(string id);

        // Vymažeme interní cache služby
        void ClearCache();

        // ZOBRAZENÍ TOASTŮ
        // Zobrazíme jednoduchý toast bez ukládání do historie
        void ShowToast(string message, NotificationType type = NotificationType.Info);

        // Zobrazíme trvalý toast, který zůstane viditelný
        void ShowPersistentToast(string message, NotificationType type = NotificationType.Info, bool showCloseIcon = false);

        // Odstraníme trvalý toast
        void RemovePersistentToast();

        // Zobrazíme toast po přesměrování (force load) stránky
        Task ShowToastAfterForceLoadAsync(string message, NotificationType type = NotificationType.Info);

        // Zkontrolujeme, zda existují čekající toasty po přesměrování
        Task CheckForPendingToastAsync();

        // SYNCHRONIZACE
        // Synchronizujeme notifikace po přihlášení uživatele
        Task SynchronizeNotificationsOnLoginAsync(string userId);

        // Synchronizujeme notifikace po obnovení připojení k internetu
        Task SynchronizeNotificationsOnConnectionRestoredAsync();
    }
}