// LocalNotificationSchedulerService.cs - Služba pro plánování systémových notifikací
using Microsoft.Extensions.Logging;
using Plugin.LocalNotification;
using Plugin.LocalNotification.AndroidOption;
using Taskly.Models;
using Taskly.Services.Projects;
using Taskly.Services.Tasks;

#if ANDROID
using Android.Content;
using Android.OS;
using Android.Provider;
using Microsoft.Maui.ApplicationModel;
#endif

#if WINDOWS
using Microsoft.Toolkit.Uwp.Notifications;
#endif

namespace Taskly.Services.Notification.LocalNotification
{
    public class LocalNotificationSchedulerService : ILocalNotificationSchedulerService
    {
        // PROMĚNNÉ A KONSTANTY
        private readonly Plugin.LocalNotification.INotificationService? _notificationServicePlugin;
        private readonly ILogger<LocalNotificationSchedulerService> _logger;
        private readonly IServiceProvider _serviceProvider;

        private const string MASTER_NOTIFICATIONS_KEY = "app_notifications_enabled";
        private const string LOCAL_NOTIFICATIONS_KEY = "app_local_notifications_enabled";

        private const int DAY_REMINDER_TYPE = 1;
        private const int HOUR_REMINDER_TYPE = 2;
        private const int MINUTE_REMINDER_TYPE = 3;
        private const int EXPIRY_NOTICE_TYPE = 4;

        // KONSTRUKTOR
        public LocalNotificationSchedulerService(
            ILogger<LocalNotificationSchedulerService> logger,
            IServiceProvider serviceProvider,
            Plugin.LocalNotification.INotificationService? notificationServicePlugin = null)
        {
            _notificationServicePlugin = notificationServicePlugin;
            _logger = logger;
            _serviceProvider = serviceProvider;

            _logger.LogInformation("LocalNotificationSchedulerService inicializován.");

#if ANDROID
            Task.Run(async () =>
            {
                try
                {
                    await RequestPermissionsAsync();
                    await CheckAndRequestExactAlarmPermissionAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Chyba při inicializaci oprávnění pro notifikace na Androidu");
                }
            });
#endif
        }

        // PLÁNOVÁNÍ NOTIFIKACÍ
        // Naplánujeme všechny potřebné notifikace pro úkol
        public async Task ScheduleRemindersForTaskAsync(TaskItem task)
        {
            await CheckAndRequestExactAlarmPermissionAsync();
            if (task == null || string.IsNullOrEmpty(task.Id))
            {
                _logger.LogWarning("ScheduleRemindersForTaskAsync: Přijat neplatný úkol.");
                return;
            }

            if (task.Status == TaskItemStatus.Completed || task.Status == TaskItemStatus.Cancelled)
            {
                _logger.LogDebug("Plánování připomínek pro úkol {TaskId} přeskočeno - úkol je dokončený nebo zrušený (aktuální stav: {Status}).", task.Id, task.Status);
                CancelRemindersForTaskAsync(task.Id);
                return;
            }

            if (!AreLocalNotificationsEnabled())
            {
                _logger.LogDebug("Plánování připomínek pro úkol {TaskId} přeskočeno - zakázáno.", task.Id);
                CancelRemindersForTaskAsync(task.Id);
                return;
            }

            if (!task.DueDate.HasValue || task.DueDate.Value <= DateTime.UtcNow)
            {
                _logger.LogDebug("Plánování připomínek pro úkol {TaskId} přeskočeno - neplatný/prošlý termín ({DueDate}).", task.Id, task.DueDate);
                CancelRemindersForTaskAsync(task.Id);
                return;
            }

            DateTime validDueDateUtc = task.DueDate.Value.ToUniversalTime();
            CancelRemindersForTaskAsync(task.Id);

            _logger.LogInformation("Plánuji notifikace pro úkol ID: {TaskId}, Termín UTC: {DueDateUtc}", task.Id, validDueDateUtc.ToString("o"));

#if WINDOWS
            // Naplánuje Windows toast notifikaci pomocí Microsoft Toolkit s podporou scheduled notifications
            void ScheduleWindowsToast(string title, string message, DateTime notifyTime, int notificationId, string taskId, string itemType)
            {
                try
                {
                    // Pokud je čas v minulosti nebo současnosti, zobrazíme notifikaci okamžitě
                    if (notifyTime <= DateTime.Now)
                    {
                        ShowWindowsToastImmediately(title, message, notificationId, taskId, itemType);
                        return;
                    }

                    // Vytvoříme scheduled toast notifikaci pomocí Microsoft Toolkit
                    new ToastContentBuilder()
                        .AddText(title)
                        .AddText(message)
                        .AddArgument("action", "openItem")
                        .AddArgument("itemType", itemType)
                        .AddArgument("itemId", taskId)
                        .AddButton(new ToastButton()
                            .SetContent("Otevřít")
                            .AddArgument("action", "openItem")
                            .AddArgument("itemType", itemType)
                            .AddArgument("itemId", taskId))
                        .Schedule(notifyTime, toast =>
                        {
                            toast.Tag = notificationId.ToString();
                        });
                    
                    _logger.LogInformation("Úspěšně naplánována Windows scheduled notifikace: {ItemType}Id={ItemId}, NotifyTime={NotifyTime}", itemType, taskId, notifyTime);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Chyba při plánování Windows scheduled notifikace pro {ItemType} {ItemId}", itemType, taskId);
                }
            }
#else
            async Task ScheduleNotification(NotificationRequest request)
            {
                if (_notificationServicePlugin == null)
                {
                    _logger.LogError("NotificationServicePlugin není dostupný pro tuto platformu.");
                    return;
                }
                if (request.Schedule.NotifyTime.HasValue && request.Schedule.NotifyTime.Value > DateTime.Now)
                {
                    try
                    {
                        await _notificationServicePlugin.Show(request);
                        _logger.LogInformation("Úspěšně naplánována notifikace: TaskId={TaskId}, NotifyTime={NotifyTime}", task.Id, request.Schedule.NotifyTime);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Chyba při plánování notifikace pro úkol {TaskId}", task.Id);
                    }
                }
            }
#endif

            // Naplánujeme denní připomínku (1 den před termínem)
            if (!task.EnableDayReminder)
            {
                DateTime dayReminderTimeUtc = validDueDateUtc.Subtract(TimeSpan.FromDays(1));
                if (dayReminderTimeUtc > DateTime.UtcNow)
                {
                    DateTime reminderTimeLocal = dayReminderTimeUtc.ToLocalTime();
#if WINDOWS
                    ScheduleWindowsToast("Blížící se termín úkolu", $"Úkol \"{task.Title}\" vyprší zítra.", reminderTimeLocal, GetNotificationId(task.Id, DAY_REMINDER_TYPE), task.Id, "task");
#else
                    var dayRequest = new NotificationRequest
                    {
                        NotificationId = GetNotificationId(task.Id, DAY_REMINDER_TYPE),
                        Title = "Blížící se termín úkolu",
                        Description = $"Úkol \"{task.Title}\" vyprší zítra.",
                        ReturningData = $"task:{task.Id}",
                        Schedule = new NotificationRequestSchedule { NotifyTime = reminderTimeLocal },
                        Android = new AndroidOptions { ChannelId = "taskly_reminders" }
                    };
                    await ScheduleNotification(dayRequest);
#endif
                }
            }

            // Naplánujeme hodinovou připomínku (2 hodiny před termínem)
            if (!task.EnableHourReminder)
            {
                DateTime hourReminderTimeUtc = validDueDateUtc.Subtract(TimeSpan.FromHours(2));
                if (hourReminderTimeUtc > DateTime.UtcNow)
                {
                    DateTime reminderTimeLocal = hourReminderTimeUtc.ToLocalTime();
#if WINDOWS
                    ScheduleWindowsToast("Blížící se termín úkolu", $"Úkol \"{task.Title}\" vyprší za 2 hodiny.", reminderTimeLocal, GetNotificationId(task.Id, HOUR_REMINDER_TYPE), task.Id, "task");
#else
                    var hourRequest = new NotificationRequest
                    {
                        NotificationId = GetNotificationId(task.Id, HOUR_REMINDER_TYPE),
                        Title = "Blížící se termín úkolu",
                        Description = $"Úkol \"{task.Title}\" vyprší za 2 hodiny.",
                        ReturningData = $"task:{task.Id}",
                        Schedule = new NotificationRequestSchedule { NotifyTime = reminderTimeLocal },
                        Android = new AndroidOptions { ChannelId = "taskly_reminders" }
                    };
                    await ScheduleNotification(hourRequest);
#endif
                }
            }

            // Naplánujeme minutovou připomínku (30 minut před termínem)
            if (!task.EnableMinuteReminder)
            {
                DateTime minuteReminderTimeUtc = validDueDateUtc.Subtract(TimeSpan.FromMinutes(30));
                if (minuteReminderTimeUtc > DateTime.UtcNow)
                {
                    DateTime reminderTimeLocal = minuteReminderTimeUtc.ToLocalTime();
#if WINDOWS
                    ScheduleWindowsToast("Blížící se termín úkolu", $"Úkol \"{task.Title}\" vyprší za 30 minut.", reminderTimeLocal, GetNotificationId(task.Id, MINUTE_REMINDER_TYPE), task.Id, "task");
#else
                    var minuteRequest = new NotificationRequest
                    {
                        NotificationId = GetNotificationId(task.Id, MINUTE_REMINDER_TYPE),
                        Title = "Blížící se termín úkolu",
                        Description = $"Úkol \"{task.Title}\" vyprší za 30 minut.",
                        ReturningData = $"task:{task.Id}",
                        Schedule = new NotificationRequestSchedule { NotifyTime = reminderTimeLocal },
                        Android = new AndroidOptions { ChannelId = "taskly_reminders" }
                    };
                    await ScheduleNotification(minuteRequest);
#endif
                }
            }

            // Naplánujeme notifikaci o vypršení termínu
            DateTime dueTimeLocal = validDueDateUtc.ToLocalTime();
#if WINDOWS
            ScheduleWindowsToast("Termín úkolu vypršel", $"Termín úkolu \"{task.Title}\" právě vypršel.", dueTimeLocal, GetNotificationId(task.Id, EXPIRY_NOTICE_TYPE), task.Id, "task");
#else
            var expiryRequest = new NotificationRequest
            {
                NotificationId = GetNotificationId(task.Id, EXPIRY_NOTICE_TYPE),
                Title = "Termín úkolu vypršel",
                Description = $"Termín úkolu \"{task.Title}\" právě vypršel.",
                ReturningData = $"task:{task.Id}",
                Schedule = new NotificationRequestSchedule { NotifyTime = dueTimeLocal },
                Android = new AndroidOptions { ChannelId = "taskly_reminders" }
            };
            await ScheduleNotification(expiryRequest);
#endif
        }

        // Naplánujeme všechny potřebné notifikace pro projekt
        public async Task ScheduleRemindersForProjectAsync(ProjectItem project)
        {
            await CheckAndRequestExactAlarmPermissionAsync();
            if (project == null || string.IsNullOrEmpty(project.Id))
            {
                _logger.LogWarning("ScheduleRemindersForProjectAsync: Přijat neplatný projekt.");
                return;
            }

            if (!AreLocalNotificationsEnabled())
            {
                _logger.LogDebug("Plánování připomínek pro projekt {ProjectId} přeskočeno - zakázáno.", project.Id);
                CancelRemindersForProjectAsync(project.Id);
                return;
            }

            if (!project.DueDate.HasValue || project.DueDate.Value <= DateTime.UtcNow)
            {
                _logger.LogDebug("Plánování připomínek pro projekt {ProjectId} přeskočeno - neplatný/prošlý termín ({DueDate}).", project.Id, project.DueDate);
                CancelRemindersForProjectAsync(project.Id);
                return;
            }

            DateTime validDueDateUtc = project.DueDate.Value.ToUniversalTime();
            CancelRemindersForProjectAsync(project.Id);

            _logger.LogInformation("Plánuji notifikace pro projekt ID: {ProjectId}, Termín UTC: {DueDateUtc}", project.Id, validDueDateUtc.ToString("o"));

#if WINDOWS
            // Naplánuje Windows toast notifikaci pro projekt pomocí Microsoft Toolkit s podporou scheduled notifications
            void ScheduleWindowsProjectToast(string title, string message, DateTime notifyTime, int notificationId, string projectId, string itemType)
            {
                try
                {
                    // Pokud je čas v minulosti nebo současnosti, zobrazíme notifikaci okamžitě
                    if (notifyTime <= DateTime.Now)
                    {
                        ShowWindowsToastImmediately(title, message, notificationId, projectId, itemType);
                        return;
                    }

                    // Vytvoříme scheduled toast notifikaci pomocí Microsoft Toolkit
                    new ToastContentBuilder()
                        .AddText(title)
                        .AddText(message)
                        .AddArgument("action", "openItem")
                        .AddArgument("itemType", itemType)
                        .AddArgument("itemId", projectId)
                        .AddButton(new ToastButton()
                            .SetContent("Otevřít")
                            .AddArgument("action", "openItem")
                            .AddArgument("itemType", itemType)
                            .AddArgument("itemId", projectId))
                        .Schedule(notifyTime, toast =>
                        {
                            toast.Tag = notificationId.ToString();
                        });

                    _logger.LogInformation("Úspěšně naplánována Windows scheduled notifikace: {ItemType}Id={ItemId}, NotifyTime={NotifyTime}", itemType, projectId, notifyTime);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Chyba při plánování Windows scheduled notifikace pro {ItemType} {ItemId}", itemType, projectId);
                }
            }
#else
            async Task ScheduleNotification(NotificationRequest request)
            {
                if (_notificationServicePlugin == null)
                {
                    _logger.LogError("NotificationServicePlugin není dostupný pro tuto platformu.");
                    return;
                }
                if (request.Schedule.NotifyTime.HasValue && request.Schedule.NotifyTime.Value > DateTime.Now)
                {
                    try
                    {
                        await _notificationServicePlugin.Show(request);
                        _logger.LogInformation("Úspěšně naplánována notifikace: ProjectId={ProjectId}, NotifyTime={NotifyTime}", project.Id, request.Schedule.NotifyTime);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Chyba při plánování notifikace pro projekt {ProjectId}", project.Id);
                    }
                }
            }
#endif

            // Naplánujeme denní připomínku (1 den před termínem)
            if (!project.EnableDayReminder)
            {
                DateTime dayReminderTimeUtc = validDueDateUtc.Subtract(TimeSpan.FromDays(1));
                if (dayReminderTimeUtc > DateTime.UtcNow)
                {
                    DateTime reminderTimeLocal = dayReminderTimeUtc.ToLocalTime();
#if WINDOWS
                    ScheduleWindowsProjectToast("Blížící se termín projektu", $"Projekt \"{project.Name}\" vyprší zítra.", reminderTimeLocal, GetProjectNotificationId(project.Id, DAY_REMINDER_TYPE), project.Id, "project");
#else
                    var dayRequest = new NotificationRequest
                    {
                        NotificationId = GetProjectNotificationId(project.Id, DAY_REMINDER_TYPE),
                        Title = "Blížící se termín projektu",
                        Description = $"Projekt \"{project.Name}\" vyprší zítra.",
                        ReturningData = $"project:{project.Id}",
                        Schedule = new NotificationRequestSchedule { NotifyTime = reminderTimeLocal },
                        Android = new AndroidOptions { ChannelId = "taskly_reminders" }
                    };
                    await ScheduleNotification(dayRequest);
#endif
                }
            }

            // Naplánujeme hodinovou připomínku (2 hodiny před termínem)
            if (!project.EnableHourReminder)
            {
                DateTime hourReminderTimeUtc = validDueDateUtc.Subtract(TimeSpan.FromHours(2));
                if (hourReminderTimeUtc > DateTime.UtcNow)
                {
                    DateTime reminderTimeLocal = hourReminderTimeUtc.ToLocalTime();
#if WINDOWS
                    ScheduleWindowsProjectToast("Blížící se termín projektu", $"Projekt \"{project.Name}\" vyprší za 2 hodiny.", reminderTimeLocal, GetProjectNotificationId(project.Id, HOUR_REMINDER_TYPE), project.Id, "project");
#else
                    var hourRequest = new NotificationRequest
                    {
                        NotificationId = GetProjectNotificationId(project.Id, HOUR_REMINDER_TYPE),
                        Title = "Blížící se termín projektu",
                        Description = $"Projekt \"{project.Name}\" vyprší za 2 hodiny.",
                        ReturningData = $"project:{project.Id}",
                        Schedule = new NotificationRequestSchedule { NotifyTime = reminderTimeLocal },
                        Android = new AndroidOptions { ChannelId = "taskly_reminders" }
                    };
                    await ScheduleNotification(hourRequest);
#endif
                }
            }

            // Naplánujeme minutovou připomínku (30 minut před termínem)
            if (!project.EnableMinuteReminder)
            {
                DateTime minuteReminderTimeUtc = validDueDateUtc.Subtract(TimeSpan.FromMinutes(30));
                if (minuteReminderTimeUtc > DateTime.UtcNow)
                {
                    DateTime reminderTimeLocal = minuteReminderTimeUtc.ToLocalTime();
#if WINDOWS
                    ScheduleWindowsProjectToast("Blížící se termín projektu", $"Projekt \"{project.Name}\" vyprší za 30 minut.", reminderTimeLocal, GetProjectNotificationId(project.Id, MINUTE_REMINDER_TYPE), project.Id, "project");
#else
                    var minuteRequest = new NotificationRequest
                    {
                        NotificationId = GetProjectNotificationId(project.Id, MINUTE_REMINDER_TYPE),
                        Title = "Blížící se termín projektu",
                        Description = $"Projekt \"{project.Name}\" vyprší za 30 minut.",
                        ReturningData = $"project:{project.Id}",
                        Schedule = new NotificationRequestSchedule { NotifyTime = reminderTimeLocal },
                        Android = new AndroidOptions { ChannelId = "taskly_reminders" }
                    };
                    await ScheduleNotification(minuteRequest);
#endif
                }
            }

            // Naplánujeme notifikaci o vypršení termínu
            DateTime dueTimeLocal = validDueDateUtc.ToLocalTime();
#if WINDOWS
            ScheduleWindowsProjectToast("Termín projektu vypršel", $"Termín projektu \"{project.Name}\" právě vypršel.", dueTimeLocal, GetProjectNotificationId(project.Id, EXPIRY_NOTICE_TYPE), project.Id, "project");
#else
            var expiryRequest = new NotificationRequest
            {
                NotificationId = GetProjectNotificationId(project.Id, EXPIRY_NOTICE_TYPE),
                Title = "Termín projektu vypršel",
                Description = $"Termín projektu \"{project.Name}\" právě vypršel.",
                ReturningData = $"project:{project.Id}",
                Schedule = new NotificationRequestSchedule { NotifyTime = dueTimeLocal },
                Android = new AndroidOptions { ChannelId = "taskly_reminders" }
            };
            await ScheduleNotification(expiryRequest);
#endif
        }

        // RUŠENÍ NOTIFIKACÍ
        // Zrušíme všechny notifikace pro úkol
        public void CancelRemindersForTaskAsync(string taskId)
        {
            if (string.IsNullOrEmpty(taskId)) return;
            _logger.LogDebug("Ruším naplánované notifikace pro úkol ID: {TaskId}", taskId);

#if WINDOWS
            try
            {
                // Zrušíme scheduled toast notifikace pomocí Microsoft Toolkit
                var taskNotificationIds = new[]
                {
                    GetNotificationId(taskId, DAY_REMINDER_TYPE).ToString(),
                    GetNotificationId(taskId, HOUR_REMINDER_TYPE).ToString(),
                    GetNotificationId(taskId, MINUTE_REMINDER_TYPE).ToString(),
                    GetNotificationId(taskId, EXPIRY_NOTICE_TYPE).ToString()
                };

                // Používáme ToastNotificationManagerCompat pro správu scheduled notifikací
                foreach (var notificationId in taskNotificationIds)
                {
                    try
                    {
                        ToastNotificationManagerCompat.History.Remove(notificationId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Nepodařilo se zrušit notifikaci s ID {NotificationId}", notificationId);
                    }
                }
                
                _logger.LogInformation("Scheduled notifikace pro úkol {TaskId} zrušeny na Windows.", taskId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při rušení scheduled notifikací pro úkol {TaskId} na Windows.", taskId);
            }
#else
            if (_notificationServicePlugin == null)
            {
                _logger.LogWarning("NotificationServicePlugin není dostupný, rušení notifikací přeskočeno.");
                return;
            }
            try
            {
                _notificationServicePlugin.Cancel(GetNotificationId(taskId, DAY_REMINDER_TYPE));
                _notificationServicePlugin.Cancel(GetNotificationId(taskId, HOUR_REMINDER_TYPE));
                _notificationServicePlugin.Cancel(GetNotificationId(taskId, MINUTE_REMINDER_TYPE));
                _notificationServicePlugin.Cancel(GetNotificationId(taskId, EXPIRY_NOTICE_TYPE));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při rušení notifikací pro úkol ID: {TaskId}", taskId);
            }
#endif
        }

        // Zrušíme všechny notifikace pro projekt
        public void CancelRemindersForProjectAsync(string projectId)
        {
            if (string.IsNullOrEmpty(projectId)) return;
            _logger.LogDebug("Ruším naplánované notifikace pro projekt ID: {ProjectId}", projectId);

#if WINDOWS
            try
            {
                // Zrušíme scheduled toast notifikace pomocí Microsoft Toolkit
                var projectNotificationIds = new[]
                {
                    GetProjectNotificationId(projectId, DAY_REMINDER_TYPE).ToString(),
                    GetProjectNotificationId(projectId, HOUR_REMINDER_TYPE).ToString(),
                    GetProjectNotificationId(projectId, MINUTE_REMINDER_TYPE).ToString(),
                    GetProjectNotificationId(projectId, EXPIRY_NOTICE_TYPE).ToString()
                };

                // Používáme ToastNotificationManagerCompat pro správu scheduled notifikací
                foreach (var notificationId in projectNotificationIds)
                {
                    try
                    {
                        ToastNotificationManagerCompat.History.Remove(notificationId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Nepodařilo se zrušit notifikaci s ID {NotificationId}", notificationId);
                    }
                }
                
                _logger.LogInformation("Scheduled notifikace pro projekt {ProjectId} zrušeny na Windows.", projectId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při rušení scheduled notifikací pro projekt {ProjectId} na Windows.", projectId);
            }
#else
            if (_notificationServicePlugin == null)
            {
                _logger.LogWarning("NotificationServicePlugin není dostupný, rušení notifikací přeskočeno.");
                return;
            }
            try
            {
                _notificationServicePlugin.Cancel(GetProjectNotificationId(projectId, DAY_REMINDER_TYPE));
                _notificationServicePlugin.Cancel(GetProjectNotificationId(projectId, HOUR_REMINDER_TYPE));
                _notificationServicePlugin.Cancel(GetProjectNotificationId(projectId, MINUTE_REMINDER_TYPE));
                _notificationServicePlugin.Cancel(GetProjectNotificationId(projectId, EXPIRY_NOTICE_TYPE));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při rušení notifikací pro projekt ID: {ProjectId}", projectId);
            }
#endif
        }

        // POMOCNÉ METODY
        // Získáme stabilní ID pro notifikaci úkolu
        private int GetNotificationId(string taskId, int type)
        {
            var combined = $"{taskId}_{type}";
            var hash = 0;
            foreach (char c in combined)
            {
                hash = hash * 31 + c;
            }
            return Math.Abs(hash);
        }

        // Získáme stabilní ID pro notifikaci projektu
        private int GetProjectNotificationId(string projectId, int type)
        {
            var combined = $"{projectId ?? "default"}_{type}";
            var hash = 0;
            foreach (char c in combined)
            {
                hash = hash * 31 + c;
            }
            return Math.Abs(hash) + 50000;
        }

#if WINDOWS
        // Zobrazí Windows toast notifikaci okamžitě pomocí Microsoft Toolkit
        private void ShowWindowsToastImmediately(string title, string message, int notificationId, string itemId, string itemType)
        {
            try
            {
                new ToastContentBuilder()
                    .AddText(title)
                    .AddText(message)
                    .AddArgument("action", "openItem")
                    .AddArgument("itemType", itemType)
                    .AddArgument("itemId", itemId)
                    .AddButton(new ToastButton()
                        .SetContent("Otevřít")
                        .AddArgument("action", "openItem")
                        .AddArgument("itemType", itemType)
                        .AddArgument("itemId", itemId))
                    .Show(toast =>
                    {
                        toast.Tag = notificationId.ToString();
                    });
                
                _logger.LogInformation("Windows toast notifikace zobrazena okamžitě: {ItemType}Id={ItemId}, Title={Title}", itemType, itemId, title);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při zobrazení okamžité Windows toast notifikace pro {ItemType} {ItemId}", itemType, itemId);
            }
        }
#endif

        // Zjistíme, zda jsou lokální notifikace povoleny
        private bool AreLocalNotificationsEnabled()
        {
            try
            {
                bool masterEnabled = Preferences.Default.Get(MASTER_NOTIFICATIONS_KEY, true);
                bool localEnabled = Preferences.Default.Get(LOCAL_NOTIFICATIONS_KEY, false);
                return masterEnabled && localEnabled;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při čtení nastavení notifikací.");
                return false;
            }
        }

        // PLATFORMOVĚ SPECIFICKÉ METODY
#if ANDROID
        // Zkontrolujeme a vyžádáme oprávnění pro přesné alarmy na Android 12+
        private async Task CheckAndRequestExactAlarmPermissionAsync()
        {
            try
            {
                if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.S)
                {
                    _logger.LogInformation("Kontrola oprávnění pro přesné alarmy na Android 12+");
                    var context = Android.App.Application.Context;
                    var alarmManager = context.GetSystemService(Context.AlarmService) as Android.App.AlarmManager;

                    if (alarmManager != null)
                    {
                        bool canSchedule = true;
                        try
                        {
                            var method = alarmManager.GetType().GetMethod("CanScheduleExactAlarms");
                            if (method != null)
                            {
                                var result = method.Invoke(alarmManager, null);
                                if (result != null)
                                {
                                    canSchedule = (bool)result;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Nelze zjistit stav oprávnění přesných alarmů, předpokládáme že je povoleno");
                            canSchedule = true;
                        }

                        if (!canSchedule)
                        {
                            _logger.LogWarning("Aplikace nemá oprávnění pro přesné alarmy");
                            var intent = new Intent("android.settings.REQUEST_SCHEDULE_EXACT_ALARM");
                            intent.AddFlags(ActivityFlags.NewTask);
                            await MainThread.InvokeOnMainThreadAsync(() => context.StartActivity(intent));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při kontrole oprávnění pro přesné alarmy: {Message}", ex.Message);
            }
        }

        // Vyžádáme oprávnění pro notifikace
        private async Task RequestPermissionsAsync()
        {
            try
            {
                if (_notificationServicePlugin == null)
                {
                    _logger.LogError("NotificationServicePlugin není dostupný pro žádost o oprávnění.");
                    return;
                }
                if (await _notificationServicePlugin.AreNotificationsEnabled() == false)
                {
                    _logger.LogInformation("Žádám o oprávnění k zobrazování notifikací.");
                    await _notificationServicePlugin.RequestNotificationPermission();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při žádosti o oprávnění pro notifikace.");
            }
        }
#else
        // Prázdné metody pro ostatní platformy
        private Task CheckAndRequestExactAlarmPermissionAsync() => Task.CompletedTask;
        private Task RequestPermissionsAsync() => Task.CompletedTask;
#endif
    }
}
