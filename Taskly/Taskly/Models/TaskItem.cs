// TaskItem.cs - Model úkolu v aplikaci Taskly
using Google.Cloud.Firestore;
using LiteDB;

namespace Taskly.Models
{
    // VÝČTOVÉ TYPY

    // Priorita úkolu - určuje důležitost úkolu
    [FirestoreData]
    public enum TaskPriority
    {
        Low,        // Nízká priorita
        Medium,     // Střední priorita
        High,       // Vysoká priorita
        Critical    // Kritická priorita
    }

    // Status úkolu - určuje v jaké fázi zpracování se úkol nachází
    [FirestoreData]
    public enum TaskItemStatus
    {
        New,        // Nový úkol
        InProgress, // Úkol v řešení
        Completed,  // Dokončený úkol
        Postponed,  // Odložený úkol
        Cancelled   // Zrušený úkol
    }

    // HLAVNÍ MODEL ÚKOLU

    // Model reprezentující úkol v aplikaci včetně všech jeho vlastností
    [FirestoreData]
    public class TaskItem
    {
        // ZÁKLADNÍ VLASTNOSTI

        // Unikátní identifikátor úkolu - používá se v LiteDB i Firestore
        [FirestoreProperty]
        [BsonId]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        // Název úkolu - povinný údaj
        [FirestoreProperty]
        public string Title { get; set; } = string.Empty;

        // Volitelný popis úkolu s podrobnějšími informacemi
        [FirestoreProperty]
        public string? Description { get; set; }

        // ČASOVÉ ÚDAJE

        // Datum a čas vytvoření úkolu
        [FirestoreProperty]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Termín dokončení úkolu - volitelný údaj
        [FirestoreProperty]
        public DateTime? DueDate { get; set; }

        // Datum a čas skutečného dokončení úkolu
        [FirestoreProperty]
        public DateTime? CompletedAt { get; set; }

        // STAV A PRIORITA

        // Priorita úkolu - bylo použito pouze v C# kódu, v Firestore ukládáno jako text
        public TaskPriority Priority { get; set; } = TaskPriority.Medium;

        // Pomocná vlastnost pro ukládání Priority do Firestore jako text
        [FirestoreProperty("Priority")]
        public string PriorityAsString
        {
            get => Priority.ToString();
            set => Priority = Enum.Parse<TaskPriority>(value);
        }

        // Status úkolu - bylo použito pouze v C# kódu, v Firestore ukládáno jako text
        public TaskItemStatus Status { get; set; } = TaskItemStatus.New;

        // Pomocná vlastnost pro ukládání Status do Firestore jako text
        [FirestoreProperty("Status")]
        public string StatusAsString
        {
            get => Status.ToString();
            set => Status = Enum.Parse<TaskItemStatus>(value);
        }

        // Indikátor dokončení úkolu s automatickou úpravou statusu a data dokončení
        [FirestoreProperty]
        public bool IsCompleted
        {
            get => Status == TaskItemStatus.Completed;
            set
            {
                if (value)
                {
                    Status = TaskItemStatus.Completed;
                    CompletedAt = DateTime.UtcNow;
                }
                else if (Status == TaskItemStatus.Completed)
                {
                    Status = TaskItemStatus.InProgress;
                    CompletedAt = null;
                }
            }
        }

        // REFERENCE A VAZBY

        // ID projektu, do kterého úkol patří
        [FirestoreProperty]
        public string? ProjectId { get; set; }

        // ID uživatele, kterému úkol patří
        [FirestoreProperty]
        public string? UserId { get; set; }

        // SYNCHRONIZACE

        // Příznak indikující potřebu synchronizace při práci offline
        [FirestoreProperty]
        public bool NeedsSynchronization { get; set; } = false;

        // NASTAVENÍ UPOZORNĚNÍ

        // Povolení upozornění 1 den před termínem dokončení
        [FirestoreProperty]
        public bool EnableDayReminder { get; set; } = true;

        // Povolení upozornění 2 hodiny před termínem dokončení
        [FirestoreProperty]
        public bool EnableHourReminder { get; set; } = true;

        // Povolení upozornění 30 minut před termínem dokončení
        [FirestoreProperty]
        public bool EnableMinuteReminder { get; set; } = true;

        // VYPOČÍTÁVANÉ VLASTNOSTI

        // Indikuje, zda je povoleno alespoň jedno upozornění - vypočítáváno dynamicky, neukládá se
        [BsonIgnore] // Atribut označující vlastnost, která se nemá ukládat do LiteDB
        public bool HasAnyRemindersEnabled => EnableDayReminder || EnableHourReminder || EnableMinuteReminder;

        // METODY

        // Vytvoří kopii objektu úkolu - užitečné pro editaci bez ovlivnění originálu
        public TaskItem Clone()
        {
            return (TaskItem)this.MemberwiseClone();
        }
    }
}