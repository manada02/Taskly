// FirestoreNotificationService.cs - Služba pro práci s notifikacemi v databázi Firestore
using Microsoft.Extensions.Logging;
using Google.Cloud.Firestore;
using Taskly.Models;
using Taskly.Firebase_config;

namespace Taskly.Services.Notification
{
    public class FirestoreNotificationService
    {
        // PROMĚNNÉ A ZÁVISLOSTI
        private readonly FirestoreDb _firestoreDb;
        private readonly ILogger<FirestoreNotificationService> _logger;
        private const string NOTIFICATIONS_COLLECTION = "notifications";
        private const string USER_NOTIFICATIONS_COLLECTION = "userNotifications"; // Název subkolekce

        // KONSTRUKTOR
        public FirestoreNotificationService(ILogger<FirestoreNotificationService> logger)
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

        // OPERACE S NOTIFIKACEMI
        // Uložíme notifikaci do Firestore
        public async Task SaveNotificationAsync(NotificationItem notification)
        {
            try
            {
                // Konvertujeme Timestamp na UTC, pokud není null a není již v UTC
                if (notification.Timestamp.HasValue && notification.Timestamp.Value.Kind != DateTimeKind.Utc)
                {
                    notification.Timestamp = notification.Timestamp.Value.ToUniversalTime();
                }

                _logger.LogInformation("Začínám ukládat notifikaci do Firestore: ID={Id}, Message={Message}, UserId={UserId}",
                    notification.Id, notification.Message, notification.UserId);

                // Kontrola, zda notifikace má všechny potřebné hodnoty
                if (string.IsNullOrEmpty(notification.UserId))
                {
                    _logger.LogWarning("Ukládání notifikace do Firestore selhalo: Chybí UserId");
                    return; // Nelze uložit notifikaci bez uživatele
                }

                // Kontrola zprávy o termínu úkolu před uložením
                if (notification.Message != null && notification.Message.Contains("měl být dokončen"))
                {
                    _logger.LogInformation("Zajišťuji správnou kategorii upozornění na termín před uložením: {Message}", notification.Message);
                    notification.Category = NotificationCategory.TaskReminder;
                    notification.Type = NotificationType.Warning;
                }

                // Vytvoříme slovník s explicitně převedenými enum hodnotami
                var notificationData = new Dictionary<string, object>
                {
                    ["Id"] = notification.Id,
                    ["Message"] = notification.Message ?? string.Empty,
                    ["Timestamp"] = notification.Timestamp.HasValue ? notification.Timestamp.Value : FieldValue.ServerTimestamp,
                    ["Type"] = (int)notification.Type,  // Explicitně jako int
                    ["Category"] = (int)notification.Category,  // Explicitně jako int
                    ["Title"] = notification.Title ?? string.Empty,
                    ["EntityId"] = notification.EntityId ?? string.Empty,
                    ["ProjectId"] = notification.ProjectId ?? string.Empty,
                    ["UserId"] = notification.UserId,
                    ["NeedsSynchronization"] = notification.NeedsSynchronization
                };

                // Uložíme notifikaci do subkolekce uživatele
                await _firestoreDb
                    .Collection(NOTIFICATIONS_COLLECTION) // Hlavní kolekce
                    .Document(notification.UserId) // ID dokumentu = userId
                    .Collection(USER_NOTIFICATIONS_COLLECTION) // Subkolekce pro notifikace uživatele
                    .Document(notification.Id)
                    .SetAsync(notificationData);  // Použijeme slovník místo objektu

                _logger.LogInformation("Notifikace úspěšně uložena do Firestore: {Id} pro uživatele {UserId}",
                    notification.Id, notification.UserId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při ukládání notifikace do Firestore: {Id}, Typ chyby: {ExType}, Zpráva: {ExMessage}",
                    notification.Id, ex.GetType().Name, ex.Message);
                throw;
            }
        }

        // Získáme notifikace pro konkrétního uživatele
        public async Task<List<NotificationItem>> GetNotificationsForUserAsync(string userId)
        {
            _logger.LogInformation("Začínám načítat notifikace z Firestore pro uživatele {UserId}", userId);

            if (string.IsNullOrEmpty(userId))
            {
                _logger.LogWarning("GetNotificationsForUserAsync: Požadavek s prázdným userId");
                return new List<NotificationItem>();
            }

            try
            {
                _logger.LogDebug("Začínám dotaz na kolekci {Collection} s filtrem UserId={UserId}",
                    NOTIFICATIONS_COLLECTION, userId);

                var snapshot = await _firestoreDb
                    .Collection(NOTIFICATIONS_COLLECTION)
                    .Document(userId)
                    .Collection(USER_NOTIFICATIONS_COLLECTION)
                    .OrderByDescending("Timestamp")
                    .LimitToLast(50)
                    .GetSnapshotAsync();

                _logger.LogDebug("Firestore vrátil {Count} dokumentů pro uživatele {UserId}",
                    snapshot.Count, userId);

                var notifications = new List<NotificationItem>();

                foreach (var doc in snapshot.Documents)
                {
                    try
                    {
                        // Použijeme ruční deserializaci místo doc.ConvertTo<NotificationItem>()
                        var dict = doc.ToDictionary();

                        // Vytvoříme novou instanci s manuálně převedenými hodnotami
                        var notification = new NotificationItem
                        {
                            Id = dict.TryGetValue("Id", out var id) ? (id?.ToString() ?? doc.Id) : doc.Id,
                            Message = dict.TryGetValue("Message", out var message) ? message?.ToString() : null,
                            Title = dict.TryGetValue("Title", out var title) ? title?.ToString() : null,
                            EntityId = dict.TryGetValue("EntityId", out var entityId) ? entityId?.ToString() : null,
                            ProjectId = dict.TryGetValue("ProjectId", out var projectId) ? projectId?.ToString() : null,
                            UserId = dict.TryGetValue("UserId", out var userId2) ? userId2?.ToString() : userId,
                            NeedsSynchronization = dict.TryGetValue("NeedsSynchronization", out var needsSync) && needsSync is bool b && b
                        };

                        // Zpracování Timestamp
                        if (dict.TryGetValue("Timestamp", out var timestamp))
                        {
                            if (timestamp is Google.Cloud.Firestore.Timestamp ts)
                            {
                                notification.Timestamp = ts.ToDateTime();
                            }
                            else if (timestamp is DateTime dt)
                            {
                                notification.Timestamp = dt;
                            }
                        }

                        // Zpracování Type - získání int hodnoty a převod na enum
                        if (dict.TryGetValue("Type", out var typeObj))
                        {
                            if (typeObj is long typeInt)
                            {
                                notification.Type = (NotificationType)typeInt;
                            }
                            else if (typeObj is int typeInt2)
                            {
                                notification.Type = (NotificationType)typeInt2;
                            }
                            else
                            {
                                // Pro případy, kdy máme slovník nebo jiný neočekávaný formát
                                _logger.LogWarning("Neznámý formát Type hodnoty: {TypeObj}", typeObj);
                            }
                        }

                        // Zpracování Category - získání int hodnoty a převod na enum
                        if (dict.TryGetValue("Category", out var catObj))
                        {
                            if (catObj is long catInt)
                            {
                                notification.Category = (NotificationCategory)catInt;
                            }
                            else if (catObj is int catInt2)
                            {
                                notification.Category = (NotificationCategory)catInt2;
                            }
                            else
                            {
                                // Pro případy, kdy máme slovník nebo jiný neočekávaný formát
                                _logger.LogWarning("Neznámý formát Category hodnoty: {CatObj}", catObj);
                            }
                        }

                        // Kontrola zprávy o termínu úkolu pro jistotu
                        if (notification.Message != null &&
                            notification.Message.Contains("měl být dokončen") &&
                            notification.Category == NotificationCategory.System)
                        {
                            notification.Category = NotificationCategory.TaskReminder;
                            notification.Type = NotificationType.Warning;
                            _logger.LogInformation("Opravena kategorie upozornění na termín z 'System' na 'TaskReminder'");
                        }

                        _logger.LogDebug("Finální hodnoty pro {DocId}: Type={Type}, Category={Category}",
                            doc.Id, notification.Type, notification.Category);

                        notifications.Add(notification);
                    }
                    catch (Exception convEx)
                    {
                        _logger.LogError(convEx, "Chyba při konverzi dokumentu {DocId}", doc.Id);
                    }
                }

                _logger.LogInformation("Úspěšně načteno {Count} notifikací z Firestore pro uživatele {UserId}",
                    notifications.Count, userId);

                return notifications;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při načítání notifikací z Firestore pro uživatele {UserId}. Typ chyby: {ExType}, Zpráva: {ExMessage}",
                    userId, ex.GetType().Name, ex.Message);

                return new List<NotificationItem>();
            }
        }

        // Smažeme notifikaci z Firestore
        public async Task DeleteNotificationAsync(string id, string userId)
        {
            try
            {
                await _firestoreDb
                    .Collection(NOTIFICATIONS_COLLECTION)
                    .Document(userId)
                    .Collection(USER_NOTIFICATIONS_COLLECTION)
                    .Document(id)
                    .DeleteAsync();

                _logger.LogInformation("Notifikace {Id} úspěšně smazána z Firestore pro uživatele {UserId}", id, userId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při mazání notifikace {Id} z Firestore", id);
                throw;
            }
        }

        // Smažeme všechny notifikace pro konkrétního uživatele
        public async Task DeleteNotificationsForUserAsync(string userId)
        {
            try
            {
                var snapshot = await _firestoreDb
                    .Collection(NOTIFICATIONS_COLLECTION)
                    .Document(userId)
                    .Collection(USER_NOTIFICATIONS_COLLECTION)
                    .GetSnapshotAsync();

                _logger.LogInformation("Nalezeno {Count} notifikací k vymazání z Firestore", snapshot.Count);

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

                _logger.LogInformation("Všechny notifikace uživatele {UserId} byly vymazány z Firestore", userId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při mazání notifikací z Firestore pro uživatele {UserId}", userId);
                throw;
            }
        }
    }
}
