// TaskReminderService.cs - Služba pro správu připomínek a upozornění úkolů
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using Taskly.Models;
using Taskly.Services.Notification;
using Taskly.Services.Projects;

namespace Taskly.Services.Tasks
{
    public class TaskReminderService : IDisposable
    {
        // PROMĚNNÉ A ZÁVISLOSTI
        private readonly ITaskService _taskService;
        private readonly INotificationService _notificationService;
        private readonly ILogger<TaskReminderService> _logger;
        private readonly IProjectService _projectService;
        private readonly HashSet<string> _processedReminders = new HashSet<string>();
        private readonly ConcurrentDictionary<string, Timer> _timers = new ConcurrentDictionary<string, Timer>();

        // Synchronizace inicializace
        private bool _isInitializing = false;
        private readonly object _lockObject = new object();

        private const string MASTER_NOTIFICATIONS_KEY = "app_notifications_enabled";
        private const string REMINDER_NOTIFICATIONS_KEY = "app_reminder_notifications_enabled";

        private static readonly TimeSpan[] _reminderIntervals = new[]
        {
            TimeSpan.FromDays(1),
            TimeSpan.FromHours(2),
            TimeSpan.FromMinutes(30)
        };

        // KONSTRUKTOR
        public TaskReminderService(
            ITaskService taskService,
            IProjectService projectService,
            INotificationService notificationService,
            ILogger<TaskReminderService> logger)
        {
            _taskService = taskService;
            _projectService = projectService;
            _notificationService = notificationService;
            _logger = logger;

            _taskService.OnTasksChanged += OnTasksChanged;
            _projectService.OnProjectsChanged += OnProjectsChanged;

            // Spustíme inicializaci asynchronně
            Task.Run(InitializeAsync);

            _logger.LogInformation("TaskReminderService inicializován s jednorázovými timery");
        }

        // INICIALIZACE A KONTROLA
        // Inicializace služby a naplánování připomínky
        private async Task InitializeAsync()
        {
            lock (_lockObject)
            {
                _isInitializing = true;
            }

            try
            {
                // Vyčistíme staré expirační klíče při startu
                await CleanupExpiredReminders();
                // Zkontrolujeme expirované položky a aktualizujeme _processedReminders (bez toastů)
                await CheckExpirationsAsync(showToast: false);
                // Počkáme na dokončení CheckExpirationsAsync před vytvořením souhrnné notifikace
                await Task.Yield(); // Zajistíme asynchronní konzistenci
                // Zobrazíme souhrnnou notifikaci při startu
                await CreateSummaryNotificationAsync();
                // Naplánujeme timery pro budoucí úkoly a projekty
                await ScheduleAllRemindersAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při inicializaci TaskReminderService");
            }
            finally
            {
                lock (_lockObject)
                {
                    _isInitializing = false;
                }
                _logger.LogInformation("Inicializace TaskReminderService dokončena");
            }
        }

        // Vyčistíme zastaralé expirační klíče při inicializaci
        private async Task CleanupExpiredReminders()
        {
            try
            {
                var tasks = await _taskService.GetTasksAsync();
                var projects = await _projectService.GetProjectsAsync();
                var now = DateTime.UtcNow;

                // Najdeme všechny skutečně expirované položky
                var currentExpiredTasks = tasks.Where(t =>
                    t.Status != TaskItemStatus.Completed &&
                    t.Status != TaskItemStatus.Cancelled &&
                    t.Status != TaskItemStatus.Postponed &&
                    t.DueDate.HasValue &&
                    t.DueDate.Value <= now).Select(t => $"{t.Id}_overdue");

                var currentExpiredProjects = projects.Where(p =>
                    p.DueDate.HasValue &&
                    p.DueDate.Value <= now).Select(p => $"project_{p.Id}_overdue");

                // Ponecháme jen klíče pro skutečně expirované položky
                var validKeys = currentExpiredTasks.Concat(currentExpiredProjects).ToHashSet();
                var keysToRemove = _processedReminders.Where(key =>
                    key.EndsWith("_overdue") && !validKeys.Contains(key)).ToList();

                foreach (var key in keysToRemove)
                {
                    _processedReminders.Remove(key);
                }

                if (keysToRemove.Any())
                {
                    _logger.LogInformation("Vyčištěno {Count} zastaralých expíračních klíčů při inicializaci", keysToRemove.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při čištění zastaralých klíčů");
            }
        }

        // Kontrola položek s vypršeným termínem
        private async Task CheckExpirationsAsync(bool showToast)
        {
            try
            {
                bool masterEnabled = Preferences.Default.Get(MASTER_NOTIFICATIONS_KEY, true);
                bool reminderEnabled = Preferences.Default.Get(REMINDER_NOTIFICATIONS_KEY, true);
                if (!masterEnabled || !reminderEnabled)
                {
                    _logger.LogDebug("Kontrola expirací přeskočena - notifikace jsou vypnuty");
                    return;
                }

                var tasks = await _taskService.GetTasksAsync();
                var projects = await _projectService.GetProjectsAsync();
                var now = DateTime.UtcNow;

                _logger.LogInformation("Načteno {TaskCount} úkolů a {ProjectCount} projektů pro kontrolu expirací", tasks.Count, projects.Count);

                // Zpracování expirovaných úkolů
                foreach (var task in tasks.Where(t =>
                    t.Status != TaskItemStatus.Completed &&
                    t.Status != TaskItemStatus.Cancelled &&
                    t.Status != TaskItemStatus.Postponed &&
                    t.DueDate.HasValue &&
                    t.DueDate.Value <= now))
                {
                    string overdueKey = $"{task.Id}_overdue";
                    if (!_processedReminders.Contains(overdueKey))
                    {
                        // Vytvoříme notifikaci, toast podle parametru
                        await CreateOverdueNotification(task, showToast);
                        _processedReminders.Add(overdueKey);
                        _logger.LogDebug("Zpracován expirovaný úkol {TaskId}, DueDate: {DueDate}, showToast: {ShowToast}", task.Id, task.DueDate, showToast);
                    }
                    else
                    {
                        _logger.LogDebug("Úkol {TaskId} již zpracován, DueDate: {DueDate}", task.Id, task.DueDate);
                    }
                }

                // Zpracujeme expirované projekty
                foreach (var project in projects.Where(p =>
                    p.DueDate.HasValue &&
                    p.DueDate.Value <= now))
                {
                    string overdueKey = $"project_{project.Id}_overdue";
                    if (!_processedReminders.Contains(overdueKey))
                    {
                        await CreateProjectOverdueNotification(project, showToast);
                        _processedReminders.Add(overdueKey);
                        _logger.LogDebug("Zpracován expirovaný projekt {ProjectId}, DueDate: {DueDate}, showToast: {ShowToast}", project.Id, project.DueDate, showToast);
                    }
                    else
                    {
                        _logger.LogDebug("Projekt {ProjectId} již zpracován, DueDate: {DueDate}", project.Id, project.DueDate);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při kontrole expirací");
            }
        }

        // Vytvoříme souhrnnou notifikaci o všech vypršených termínech
        private async Task CreateSummaryNotificationAsync()
        {
            try
            {
                bool masterEnabled = Preferences.Default.Get(MASTER_NOTIFICATIONS_KEY, true);
                bool reminderEnabled = Preferences.Default.Get(REMINDER_NOTIFICATIONS_KEY, true);
                if (!masterEnabled || !reminderEnabled)
                {
                    _logger.LogDebug("Souhrnná notifikace přeskočena - notifikace jsou vypnuty");
                    return;
                }

                var tasks = await _taskService.GetTasksAsync();
                var projects = await _projectService.GetProjectsAsync();
                var now = DateTime.UtcNow; // Používáme UTC pro konzistentní porovnání

                // Logování načtených úkolů a projektů
                _logger.LogInformation("Načteno {TaskCount} úkolů a {ProjectCount} projektů pro souhrnnou notifikaci, aktuální čas (UTC): {Now}",
                    tasks.Count, projects.Count, now);

                // Logování detailů všech úkolů
                foreach (var task in tasks)
                {
                    bool hasDueDate = task.DueDate.HasValue;
                    bool isExpired = hasDueDate &&
                                     task.Status != TaskItemStatus.Completed &&
                                     task.Status != TaskItemStatus.Cancelled &&
                                     task.DueDate!.Value.ToUniversalTime() <= now;
                    _logger.LogDebug("Úkol {TaskId}, DueDate: {DueDate}, HasDueDate: {HasDueDate}, Expired: {IsExpired}, Status: {Status}",
                        task.Id, task.DueDate, hasDueDate, isExpired, task.Status);
                }

                // Logování detailů všech projektů
                foreach (var project in projects)
                {
                    bool hasDueDate = project.DueDate.HasValue;
                    bool isExpired = hasDueDate && project.DueDate!.Value.ToUniversalTime() <= now;
                    _logger.LogDebug("Projekt {ProjectId}, DueDate: {DueDate}, HasDueDate: {HasDueDate}, Expired: {IsExpired}",
                        project.Id, project.DueDate, hasDueDate, isExpired);
                }

                // Počet expirovaných položek
                int expiredTasks = tasks.Count(t =>
                    t.DueDate.HasValue &&
                    t.Status != TaskItemStatus.Completed &&
                    t.Status != TaskItemStatus.Cancelled &&
                    t.Status != TaskItemStatus.Postponed &&
                    t.DueDate!.Value.ToUniversalTime() <= now);

                int expiredProjects = projects.Count(p =>
                    p.DueDate.HasValue &&
                    p.DueDate!.Value.ToUniversalTime() <= now);

                int totalExpired = expiredTasks + expiredProjects;

                _logger.LogInformation("Počet expirovaných položek: {ExpiredTasks} úkolů, {ExpiredProjects} projektů, celkem: {TotalExpired}",
                    expiredTasks, expiredProjects, totalExpired);

                if (totalExpired > 0)
                {
                    string message = totalExpired == 1
                        ? "Máte 1 expirovanou položku. Klikněte na 🔔 pro zobrazení detailů."
                        : $"Máte {totalExpired} expirovaných položek. Klikněte na 🔔 pro zobrazení detailů.";

                    _notificationService.ShowToast(message, NotificationType.Warning);
                    _logger.LogInformation("Zobrazena souhrnná notifikace: {Message}", message);
                }
                else
                {
                    _logger.LogInformation("Žádné expirované položky nenalezeny");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při vytváření souhrnné notifikace");
            }
        }

        // SPRÁVA PŘIPOMÍNEK
        // Naplánujeme připomínky pro všechny úkoly a projekty
        private async Task ScheduleAllRemindersAsync()
        {
            try
            {
                // Zrušíme všechny stávající timery
                ClearAllTimers();

                bool masterEnabled = Preferences.Default.Get(MASTER_NOTIFICATIONS_KEY, true);
                bool reminderEnabled = Preferences.Default.Get(REMINDER_NOTIFICATIONS_KEY, true);
                if (!masterEnabled || !reminderEnabled)
                {
                    _logger.LogDebug("Plánování timerů přeskočeno - notifikace jsou vypnuty");
                    return;
                }

                var tasks = await _taskService.GetTasksAsync();
                var projects = await _projectService.GetProjectsAsync();
                var now = DateTime.UtcNow;

                // Naplánujeme timery pro úkoly
                foreach (var task in tasks.Where(t =>
                    t.Status != TaskItemStatus.Completed &&
                    t.Status != TaskItemStatus.Cancelled &&
                    t.Status != TaskItemStatus.Postponed &&
                    t.DueDate.HasValue &&
                    t.DueDate.Value > now))
                {
                    ScheduleTaskReminders(task);
                }

                // Naplánujeme timery pro projekty
                foreach (var project in projects.Where(p =>
                    p.DueDate.HasValue &&
                    p.DueDate.Value > now))
                {
                    ScheduleProjectReminders(project);
                }

                _logger.LogInformation("Naplánováno {TimerCount} timerů pro úkoly a projekty", _timers.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při plánování připomínek");
            }
        }

        // Naplánujeme připomínky pro konkrétní úkol
        private void ScheduleTaskReminders(TaskItem task)
        {
            if (!task.DueDate.HasValue) return;

            var now = DateTime.UtcNow;
            var dueDate = task.DueDate.Value;

            // Vymažeme záznamy připomínek pro tento úkol (bez expirací)
            var keysToRemove = _processedReminders
                .Where(key => key.StartsWith($"{task.Id}_") && !key.EndsWith("_overdue"))
                .ToList();

            foreach (var key in keysToRemove)
            {
                _processedReminders.Remove(key);
            }

            if (keysToRemove.Any())
            {
                _logger.LogInformation("Resetuji {Count} připomínek (bez expirací) pro úkol {TaskId}",
                    keysToRemove.Count, task.Id);
            }

            _logger.LogDebug("Plánuji timery pro úkol {TaskId}, DueDate: {DueDate}, EnableDay: {EnableDay}, EnableHour: {EnableHour}, EnableMinute: {EnableMinute}",
                task.Id, dueDate, task.EnableDayReminder, task.EnableHourReminder, task.EnableMinuteReminder);

            // Plánování timeru pro expiraci
            string overdueKey = $"{task.Id}_overdue";
            if (!_processedReminders.Contains(overdueKey))
            {
                ScheduleTimer($"task_{task.Id}_overdue", dueDate, async () =>
                {
                    if (!_processedReminders.Contains(overdueKey))
                    {
                        await CreateOverdueNotification(task, true);
                        _processedReminders.Add(overdueKey);
                    }
                });
            }

            // Plánování timerů pro připomínky
            if (!task.EnableDayReminder)
            {
                ScheduleReminder(task.Id, "day", dueDate - TimeSpan.FromDays(1));
            }
            if (!task.EnableHourReminder)
            {
                ScheduleReminder(task.Id, "hour", dueDate - TimeSpan.FromHours(2));
            }
            if (!task.EnableMinuteReminder)
            {
                ScheduleReminder(task.Id, "minute", dueDate - TimeSpan.FromMinutes(30));
            }
        }

        // Naplánujeme připomínky pro konkrétní projekt
        private void ScheduleProjectReminders(ProjectItem project)
        {
            if (!project.DueDate.HasValue) return;

            var now = DateTime.UtcNow;
            var dueDate = project.DueDate.Value;

            // Vymažeme záznamy připomínek pro tento projekt (bez expirací)
            var keysToRemove = _processedReminders
                .Where(key => key.StartsWith($"project_{project.Id}_") && !key.EndsWith("_overdue"))
                .ToList();

            foreach (var key in keysToRemove)
            {
                _processedReminders.Remove(key);
            }

            if (keysToRemove.Any())
            {
                _logger.LogInformation("Resetuji {Count} připomínek (bez expirací) pro projekt {ProjectId}",
                    keysToRemove.Count, project.Id);
            }

            _logger.LogDebug("Plánuji timery pro projekt {ProjectId}, DueDate: {DueDate}, EnableDay: {EnableDay}, EnableHour: {EnableHour}, EnableMinute: {EnableMinute}",
                project.Id, dueDate, project.EnableDayReminder, project.EnableHourReminder, project.EnableMinuteReminder);

            // Plánování timeru pro expiraci
            string overdueKey = $"project_{project.Id}_overdue";
            if (!_processedReminders.Contains(overdueKey))
            {
                ScheduleTimer($"project_{project.Id}_overdue", dueDate, async () =>
                {
                    if (!_processedReminders.Contains(overdueKey))
                    {
                        await CreateProjectOverdueNotification(project, true);
                        _processedReminders.Add(overdueKey);
                    }
                });
            }

            // Plánování timerů pro připomínky
            if (!project.EnableDayReminder)
            {
                ScheduleProjectReminder(project.Id, "day", dueDate - TimeSpan.FromDays(1));
            }
            if (!project.EnableHourReminder)
            {
                ScheduleProjectReminder(project.Id, "hour", dueDate - TimeSpan.FromHours(2));
            }
            if (!project.EnableMinuteReminder)
            {
                ScheduleProjectReminder(project.Id, "minute", dueDate - TimeSpan.FromMinutes(30));
            }
        }

        // Naplánujeme připomínku pro úkol
        private void ScheduleReminder(string taskId, string intervalLabel, DateTime triggerTime)
        {
            string reminderKey = $"{taskId}_{intervalLabel}";
            if (!_processedReminders.Contains(reminderKey))
            {
                ScheduleTimer($"task_{taskId}_{intervalLabel}", triggerTime, async () =>
                {
                    if (!_processedReminders.Contains(reminderKey))
                    {
                        var task = await _taskService.GetTaskAsync(taskId);
                        if (task != null &&
                            task.Status != TaskItemStatus.Completed &&
                            task.Status != TaskItemStatus.Cancelled &&
                            task.Status != TaskItemStatus.Postponed)
                        {
                            TimeSpan interval = intervalLabel switch
                            {
                                "day" => TimeSpan.FromDays(1),
                                "hour" => TimeSpan.FromHours(2),
                                "minute" => TimeSpan.FromMinutes(30),
                                _ => TimeSpan.Zero
                            };
                            await CreateReminderNotification(task, interval);
                            _processedReminders.Add(reminderKey);
                        }
                    }
                });
            }
        }

        // Naplánujeme připomínku pro projekt
        private void ScheduleProjectReminder(string projectId, string intervalLabel, DateTime triggerTime)
        {
            string reminderKey = $"project_{projectId}_{intervalLabel}";
            if (!_processedReminders.Contains(reminderKey))
            {
                ScheduleTimer($"project_{projectId}_{intervalLabel}", triggerTime, async () =>
                {
                    if (!_processedReminders.Contains(reminderKey))
                    {
                        var project = await _projectService.GetProjectAsync(projectId);
                        if (project != null)
                        {
                            TimeSpan interval = intervalLabel switch
                            {
                                "day" => TimeSpan.FromDays(1),
                                "hour" => TimeSpan.FromHours(2),
                                "minute" => TimeSpan.FromMinutes(30),
                                _ => TimeSpan.Zero
                            };
                            await CreateProjectReminderNotification(project, interval);
                            _processedReminders.Add(reminderKey);
                        }
                    }
                });
            }
        }

        // Naplánujeme konkrétní timer
        private void ScheduleTimer(string timerKey, DateTime triggerTime, Action callback)
        {
            var now = DateTime.UtcNow;
            if (triggerTime <= now)
            {
                _logger.LogDebug("Timer {TimerKey} nebyl naplánován, protože čas spuštění je v minulosti: {TriggerTime}", timerKey, triggerTime);
                return;
            }

            bool masterEnabled = Preferences.Default.Get(MASTER_NOTIFICATIONS_KEY, true);
            bool reminderEnabled = Preferences.Default.Get(REMINDER_NOTIFICATIONS_KEY, true);
            if (!masterEnabled || !reminderEnabled)
            {
                _logger.LogDebug("Timer {TimerKey} nebyl naplánován, protože notifikace jsou vypnuty", timerKey);
                return;
            }

            TimeSpan delay = triggerTime - now;
            if (delay.TotalMilliseconds < 0)
            {
                _logger.LogDebug("Timer {TimerKey} nebyl naplánován, protože čas spuštění je negativní", timerKey);
                return;
            }

            // Zrušíme případný stávající timer
            if (_timers.TryRemove(timerKey, out var oldTimer))
            {
                oldTimer?.Dispose();
                _logger.LogDebug("Zrušen starý timer {TimerKey}", timerKey);
            }

            // Vytvoříme nový timer
            var timer = new Timer(_ =>
            {
                try
                {
                    _logger.LogDebug("Spouštím timer {TimerKey}", timerKey);
                    callback();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Chyba při zpracování timeru {TimerKey}", timerKey);
                }
                finally
                {
                    // Po spuštění timer zrušíme
                    if (_timers.TryRemove(timerKey, out var removedTimer))
                    {
                        removedTimer?.Dispose();
                        _logger.LogDebug("Timer {TimerKey} zrušen po spuštění", timerKey);
                    }
                }
            }, null, delay, Timeout.InfiniteTimeSpan);

            if (_timers.TryAdd(timerKey, timer))
            {
                _logger.LogDebug("Timer {TimerKey} naplánován na {TriggerTime}", timerKey, triggerTime);
            }
            else
            {
                timer.Dispose();
                _logger.LogWarning("Nepodařilo se přidat timer {TimerKey} do seznamu", timerKey);
            }
        }

        // Zrušíme všechny naplánované timery
        private void ClearAllTimers()
        {
            foreach (var timer in _timers)
            {
                if (_timers.TryRemove(timer.Key, out var removedTimer))
                {
                    removedTimer?.Dispose();
                }
            }
            _logger.LogInformation("Všechny timery zrušeny, počet: {TimerCount}", _timers.Count);
        }

        // NOTIFIKACE
        // Vytvoříme připomínku pro úkol
        private async Task CreateReminderNotification(TaskItem task, TimeSpan interval)
        {
            var notification = new NotificationItem
            {
                Id = Guid.NewGuid().ToString(),
                Title = "Blížící se termín úkolu",
                Message = $"Úkol \"{task.Title}\" vyprší za {FormatTimeSpan(interval)}",
                Type = NotificationType.Warning,
                Category = NotificationCategory.TaskReminder,
                EntityId = task.Id,
                Timestamp = DateTime.UtcNow,
                UserId = task.UserId
            };

            await _notificationService.AddNotificationAsync(notification);
            _logger.LogInformation("Vytvořena připomínka pro úkol {TaskId}: {Message}", task.Id, notification.Message);
        }

        // Vytvoříme notifikaci o expiraci úkolu
        private async Task CreateOverdueNotification(TaskItem task, bool showToast = true)
        {
            var notification = new NotificationItem
            {
                Id = Guid.NewGuid().ToString(),
                Title = "Termín úkolu vypršel",
                Message = $"Úkol \"{task.Title}\" měl být dokončen {task.DueDate?.ToLocalTime():dd.MM.yyyy HH:mm}",
                Type = NotificationType.Error,
                Category = NotificationCategory.TaskReminder,
                EntityId = task.Id,
                Timestamp = DateTime.UtcNow,
                UserId = task.UserId
            };

            await _notificationService.AddNotificationAsync(notification, false, showToast);
            _logger.LogInformation("Vytvořena notifikace o expiraci úkolu {TaskId}: {Message}, showToast: {ShowToast}", task.Id, notification.Message, showToast);
        }

        // Vytvoříme připomínku pro projekt
        private async Task CreateProjectReminderNotification(ProjectItem project, TimeSpan interval)
        {
            var notification = new NotificationItem
            {
                Id = Guid.NewGuid().ToString(),
                Title = "Blížící se termín projektu",
                Message = $"Projekt \"{project.Name}\" vyprší za {FormatTimeSpan(interval)}",
                Type = NotificationType.Warning,
                Category = NotificationCategory.ProjectReminder,
                EntityId = project.Id,
                Timestamp = DateTime.UtcNow,
                UserId = project.UserId
            };

            await _notificationService.AddNotificationAsync(notification);
            _logger.LogInformation("Vytvořena připomínka pro projekt {ProjectId}: {Message}", project.Id, notification.Message);
        }

        // Vytvoříme notifikaci o expiraci projektu
        private async Task CreateProjectOverdueNotification(ProjectItem project, bool showToast = true)
        {
            var notification = new NotificationItem
            {
                Id = Guid.NewGuid().ToString(),
                Title = "Termín projektu vypršel",
                Message = $"Projekt \"{project.Name}\" měl být dokončen {project.DueDate?.ToLocalTime():dd.MM.yyyy}",
                Type = NotificationType.Error,
                Category = NotificationCategory.ProjectReminder,
                EntityId = project.Id,
                Timestamp = DateTime.UtcNow,
                UserId = project.UserId
            };

            await _notificationService.AddNotificationAsync(notification, false, showToast);
            _logger.LogInformation("Vytvořena notifikace o expiraci projektu {ProjectId}: {Message}, showToast: {ShowToast}", project.Id, notification.Message, showToast);
        }

        // POMOCNÉ METODY
        // Formátujeme časový interval pro zobrazení
        private string FormatTimeSpan(TimeSpan span)
        {
            if (span.TotalDays >= 1)
                return $"{(int)span.TotalDays} {PluralizeDay((int)span.TotalDays)}";
            else if (span.TotalHours >= 1)
                return $"{(int)span.TotalHours} {PluralizeHour((int)span.TotalHours)}";
            else
                return $"{(int)span.TotalMinutes} {PluralizeMinute((int)span.TotalMinutes)}";
        }

        // Pluralizace pro dny
        private string PluralizeDay(int count) => count == 1 ? "den" : (count >= 2 && count <= 4 ? "dny" : "dní");

        // Pluralizace pro hodiny
        private string PluralizeHour(int count) => count == 1 ? "hodinu" : (count >= 2 && count <= 4 ? "hodiny" : "hodin");

        // Pluralizace pro minuty
        private string PluralizeMinute(int count) => count == 1 ? "minutu" : (count >= 2 && count <= 4 ? "minuty" : "minut");

        // Vyčistíme seznam zpracovaných připomínek
        private void CleanupProcessedReminders()
        {
            if (_processedReminders.Count > 1000)
            {
                _processedReminders.Clear();
                _logger.LogInformation("Seznam zpracovaných připomínek vyčištěn kvůli překročení limitu 1000");
            }
        }

        // EVENT HANDLERY
        // Reagujeme na změny úkolů
        private async void OnTasksChanged()
        {
            try
            {
                lock (_lockObject)
                {
                    if (_isInitializing)
                    {
                        _logger.LogDebug("OnTasksChanged přeskočen - probíhá inicializace");
                        return;
                    }
                }

                // Zkontrolujeme nové expirované úkoly s toasty
                await CheckExpirationsAsync(showToast: true);
                // Aktualizujeme timery pro připomínky
                await ScheduleAllRemindersAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při zpracování změny úkolů");
            }
        }

        // Reagujeme na změny projektů
        private async void OnProjectsChanged()
        {
            try
            {
                lock (_lockObject)
                {
                    if (_isInitializing)
                    {
                        _logger.LogDebug("OnProjectsChanged přeskočen - probíhá inicializace");
                        return;
                    }
                }

                // Zkontrolujeme nové expirované projekty s toasty
                await CheckExpirationsAsync(showToast: true);
                // Aktualizujeme timery pro připomínky
                await ScheduleAllRemindersAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při zpracování změny projektů");
            }
        }

        // UVOLNĚNÍ ZDROJŮ
        // Uvolní zdroje komponenty - odregistruje event handlery pro předejití memory leaků
        public void Dispose()
        {
            _taskService.OnTasksChanged -= OnTasksChanged;
            _projectService.OnProjectsChanged -= OnProjectsChanged;
            ClearAllTimers();
            _processedReminders.Clear();
            _logger.LogInformation("TaskReminderService ukončen, všechny zdroje uvolněny");
        }
    }
}