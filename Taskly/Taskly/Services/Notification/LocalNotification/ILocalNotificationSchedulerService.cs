// ILocalNotificationSchedulerService.cs - Rozhraní pro plánování lokálních systémových notifikací
using Taskly.Models;

namespace Taskly.Services.Notification.LocalNotification
{
    public interface ILocalNotificationSchedulerService
    {
        // PLÁNOVÁNÍ NOTIFIKACÍ
        // Naplánujeme všechny potřebné notifikace pro úkol (připomínky + vypršení)
        Task ScheduleRemindersForTaskAsync(TaskItem task);

        // Naplánujeme všechny potřebné notifikace pro projekt
        Task ScheduleRemindersForProjectAsync(ProjectItem project);

        // RUŠENÍ NOTIFIKACÍ
        // Zrušíme všechny notifikace pro úkol
        void CancelRemindersForTaskAsync(string taskId);

        // Zrušíme všechny notifikace pro projekt
        void CancelRemindersForProjectAsync(string projectId);
    }
}
