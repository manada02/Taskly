// NotificationItem.cs - Model související s notifikacemi v aplikaci
using LiteDB;
using Google.Cloud.Firestore;

namespace Taskly.Models
{
    // VÝČTOVÉ TYPY
    // Definuje typ notifikace, který určuje její vizuální styl a důležitost
    [FirestoreData]
    public enum NotificationType
    {
        Success,    // Úspěšná operace - zelená barva
        Info,       // Informativní zpráva - modrá barva
        Warning,    // Varování - oranžová barva
        Error       // Chyba - červená barva
    }

    // Definuje kategorii notifikace, která určuje její zdroj a kontext
    [FirestoreData]
    public enum NotificationCategory
    {
        System,          // Systémové notifikace (přihlášení, odhlášení)
        Project,         // Projektové notifikace (změny v projektech)
        Task,            // Úkolové notifikace (změny v úkolech)
        TaskReminder,    // Upozornění na termíny úkolů
        ProjectReminder  // Upozornění na termíny projektů
    }

    // HLAVNÍ MODEL NOTIFIKACE

    // Reprezentuje jednu notifikaci v systému, ukládá se lokálně i v cloudu
    [FirestoreData]
    public class NotificationItem
    {
        // Unikátní identifikátor notifikace - používá se v LiteDB i Firestore
        [FirestoreProperty]
        [BsonId]  // Používá se pro LiteDB
        public string Id { get; set; } = Guid.NewGuid().ToString();

        // Hlavní text notifikace
        [FirestoreProperty]
        public string? Message { get; set; }

        // Časová značka vytvoření notifikace
        [FirestoreProperty]
        public DateTime? Timestamp { get; set; }

        // Typ notifikace určující její závažnost a vizuální styl
        [FirestoreProperty]
        public NotificationType Type { get; set; }

        // Kategorie notifikace určující její zdroj a kontext
        [FirestoreProperty]
        public NotificationCategory Category { get; set; } = NotificationCategory.System;

        // Volitelný nadpis notifikace
        [FirestoreProperty]
        public string? Title { get; set; }

        // ID entity, které se notifikace týká (úkol, projekt)
        [FirestoreProperty]
        public string? EntityId { get; set; }

        // ID projektu, ke kterému notifikace patří (pokud relevantní)
        [FirestoreProperty]
        public string? ProjectId { get; set; }

        // ID uživatele, kterému notifikace patří
        [FirestoreProperty]
        public string? UserId { get; set; }

        // Indikátor, zda notifikace čeká na synchronizaci s cloudem
        [FirestoreProperty]
        public bool NeedsSynchronization { get; set; } = false;

        // VYPOČÍTÁVANÉ VLASTNOSTI

        // Určuje, zda je notifikace spojená s úkolem
        // Není označeno jako FirestoreProperty, protože se vypočítává dynamicky
        public bool IsTask => Category == NotificationCategory.Task || Category == NotificationCategory.TaskReminder;

        // Určuje, zda je notifikace spojená s projektem
        // Není označeno jako FirestoreProperty, protože se vypočítává dynamicky
        public bool IsProject => Category == NotificationCategory.Project || Category == NotificationCategory.ProjectReminder;
    }
}