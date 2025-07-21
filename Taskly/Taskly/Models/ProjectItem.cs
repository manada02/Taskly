// ProjectItem.cs - Model projektu v aplikaci Taskly
using Google.Cloud.Firestore;
using LiteDB;

namespace Taskly.Models
{
    // Model reprezentující projekt v aplikaci, který může obsahovat více úkolů
    [FirestoreData]
    public class ProjectItem
    {
        // ZÁKLADNÍ VLASTNOSTI

        // Unikátní identifikátor projektu - používá se v LiteDB i Firestore
        [FirestoreProperty]
        [BsonId]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        // Název projektu - povinný údaj
        [FirestoreProperty]
        public string Name { get; set; } = string.Empty;

        // Volitelný popis projektu
        [FirestoreProperty]
        public string? Description { get; set; }

        // ČASOVÉ ÚDAJE
        // Proměnná pro datum vytvoření - vždy v UTC formátu
        private DateTime _createdAt = DateTime.UtcNow;

        // Datum a čas vytvoření projektu
        [FirestoreProperty]
        public DateTime CreatedAt
        {
            get => _createdAt;
            set => _createdAt = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        }

        // Privátní pole pro uchování termínu dokončení - zajišťuje uložení v UTC formátu
        private DateTime? _dueDate;

        // Termín dokončení projektu - volitelný údaj
        [FirestoreProperty]
        public DateTime? DueDate
        {
            get => _dueDate;
            set => _dueDate = value.HasValue
                ? (value.Value.Kind == DateTimeKind.Utc ? value : value.Value.ToUniversalTime())
                : null;
        }

        // VIZUÁLNÍ VLASTNOSTI

        // Barva projektu pro vizuální odlišení v UI
        [FirestoreProperty]
        public string? Color { get; set; } = "#3f51b5"; // Výchozí barva (MudBlazor Primary)

        // REFERENCE A VAZBY

        // ID uživatele, kterému projekt patří
        [FirestoreProperty]
        public string? UserId { get; set; }

        // SYNCHRONIZACE

        // Příznak, který indikuje potřebu synchronizace při práci offline
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

        // Vytvoří kopii objektu projektu - užitečné pro editaci bez ovlivnění originálu
        public ProjectItem Clone()
        {
            return (ProjectItem)this.MemberwiseClone();
        }
    }
}