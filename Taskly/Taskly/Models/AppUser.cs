// AppUser.cs - Model související s uživatelskými účty v aplikaci
using Google.Cloud.Firestore;
using System.ComponentModel.DataAnnotations;

namespace Taskly.Models
{
    // HLAVNÍ MODEL UŽIVATELE
    [FirestoreData]
    public class AppUser
    {
        // Unikátní identifikátor dokumentu ve Firestore (odpovídá UID z Firebase Authentication)
        [FirestoreDocumentId]
        public string? DocumentId { get; set; }

        // Email uživatele - používá se pro přihlášení i notifikace
        [FirestoreProperty("email")]
        public string Email { get; set; } = string.Empty;

        // Uživatelské jméno - používá se pro identifikaci v aplikaci
        [FirestoreProperty("username")]
        [Required(ErrorMessage = "Uživatelské jméno je povinné")]
        [StringLength(50, MinimumLength = 3, ErrorMessage = "Uživatelské jméno musí být dlouhé 3-50 znaků")]
        public string Username { get; set; } = string.Empty;

        // Křestní jméno uživatele - není povinné
        [FirestoreProperty("firstName")]
        public string? FirstName { get; set; }

        // Příjmení uživatele - není povinné
        [FirestoreProperty("lastName")]
        public string? LastName { get; set; }

        // Časová zóna uživatele - využíváme pro správné zobrazení časů
        [FirestoreProperty("timeZone")]
        public string? TimeZone { get; set; }

        // Datum a čas vytvoření účtu
        [FirestoreProperty("createdAt")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Datum a čas posledního přihlášení
        [FirestoreProperty("lastLoginAt")]
        public DateTime? LastLoginAt { get; set; }

        // Označuje, zda je účet stále aktivní nebo byl deaktivován
        [FirestoreProperty("isActive")]
        public bool IsActive { get; set; } = true;

        // Udává, zda je uživatel přihlášený a aktivně používá aplikaci
        [FirestoreProperty("isOnline")]
        public bool IsOnline { get; set; }

        // URL adresa profilového obrázku uživatele uloženého ve Firebase Storage
        [FirestoreProperty("ProfileImageUrl")]
        public string? ProfileImageUrl { get; set; }
    }

    // DTO MODELY PRO RŮZNÉ OPERACE

    // DTO pro registraci - obsahuje pouze údaje potřebné k vytvoření nového účtu
    public class UserRegistrationDto
    {
        // Uživatelské jméno s validačními pravidly
        [Required(ErrorMessage = "Uživatelské jméno je povinné")]
        [StringLength(50, MinimumLength = 3, ErrorMessage = "Uživatelské jméno musí být dlouhé 3-50 znaků")]
        [RegularExpression(@"^[a-zA-Z0-9_-]+$", ErrorMessage = "Uživatelské jméno může obsahovat pouze písmena, čísla, podtržítko a pomlčku")]
        public string Username { get; set; } = string.Empty;

        // Email s validací formátu
        [Required(ErrorMessage = "Email je povinný")]
        [EmailAddress(ErrorMessage = "Neplatný formát emailu")]
        public string Email { get; set; } = string.Empty;

        // Heslo s komplexními požadavky na bezpečnost
        [Required(ErrorMessage = "Heslo je povinné")]
        [StringLength(100, MinimumLength = 8, ErrorMessage = "Heslo musí být dlouhé alespoň 8 znaků")]
        [RegularExpression(@"^(?=.*[a-z])(?=.*[A-Z])(?=.*\d)(?=.*[^\da-zA-Z]).{8,}$",
            ErrorMessage = "Heslo musí obsahovat alespoň jedno velké písmeno, malé písmeno, číslo a speciální znak")]
        public string Password { get; set; } = string.Empty;

        // Potvrzení hesla s kontrolou shody
        [Required(ErrorMessage = "Potvrzení hesla je povinné")]
        [Compare("Password", ErrorMessage = "Hesla se neshodují")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }

    // DTO pro přihlášení - obsahuje identifikátor a heslo
    public class UserLoginDto
    {
        // Identifikátor může použít email nebo uživatelské jméno
        [Required(ErrorMessage = "Email nebo uživatelské jméno je povinné")]
        [StringLength(100, MinimumLength = 3, ErrorMessage = "Identifikátor musí být dlouhý 3-100 znaků")]
        [RegularExpression(@"^[a-zA-Z0-9@._-]+$",
            ErrorMessage = "Identifikátor obsahuje nepovolené znaky")]
        public string LoginIdentifier { get; set; } = string.Empty;

        // Heslo pro přihlášení
        [Required(ErrorMessage = "Heslo je povinné")]
        [StringLength(100, MinimumLength = 8, ErrorMessage = "Neplatná délka hesla")]
        public string Password { get; set; } = string.Empty;

        // Příznak pro zapamatování přihlášení
        public bool RememberMe { get; set; } = false;
    }

    // DTO pro zapomenuté heslo - obsahuje pouze email
    public class ForgotPasswordDto
    {
        // Email uživatele pro obnovu hesla
        [Required(ErrorMessage = "Email je povinný")]
        [EmailAddress(ErrorMessage = "Neplatný formát emailu")]
        [StringLength(100, ErrorMessage = "Email je příliš dlouhý")]
        public string Email { get; set; } = string.Empty;
    }

    // DTO pro úpravu profilu - obsahuje editovatelné údaje profilu
    public class UserProfileDto
    {
        // Uživatelské jméno s validací
        [Required(ErrorMessage = "Uživatelské jméno je povinné")]
        [StringLength(50, ErrorMessage = "Uživatelské jméno může mít maximálně 50 znaků")]
        public string UserName { get; set; } = string.Empty;

        // Email s validací
        [Required(ErrorMessage = "Email je povinný")]
        [EmailAddress(ErrorMessage = "Neplatný formát emailové adresy")]
        public string Email { get; set; } = string.Empty;

        // Volitelné jméno uživatele
        [StringLength(50, ErrorMessage = "Jméno může mít maximálně 50 znaků")]
        public string? FirstName { get; set; } = string.Empty;

        // Volitelné příjmení uživatele
        [StringLength(50, ErrorMessage = "Příjmení může mít maximálně 50 znaků")]
        public string? LastName { get; set; } = string.Empty;

        // URL adresa profilového obrázku
        public string? ProfileImageUrl { get; set; }
    }
}
