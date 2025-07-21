// IAuthService.cs - Rozhraní pro službu správy autentizace a uživatelských účtů
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Taskly.Models;
using Firebase.Auth;

namespace Taskly.Services.Auth
{
    public interface IAuthService
    {
        // UDÁLOSTI
        // Událost informující o změně stavu autentizace
        event Action<bool> AuthenticationStateChanged;

        // Událost informující o změně profilového obrázku
        event Action<string>? ProfileImageChanged;

        // Událost informující o aktualizaci uživatelského profilu
        event Action<AppUser> UserProfileUpdated;

        // Událost informující o přihlášení uživatele
        event Action<string>? UserLoggedIn;

        // Událost informující o vynuceném odhlášení
        event Action ForcedLogoutDetected;

        // AUTENTIZACE A SPRÁVA RELACE
        // Obnoví relaci uživatele z uložených dat
        Task<bool> TryRestoreSessionAsync();

        // Zaregistruje nového uživatele
        Task<AppUser> RegisterAsync(UserRegistrationDto registrationDto);

        // Přihlásí uživatele pomocí Firebase tokenu
        Task<AppUser> LoginAsync(string firebaseToken);

        // Odhlásí uživatele podle jeho ID
        Task<bool> LogoutAsync(string userId);

        // Vynucené odhlášení uživatele
        Task ForcedLogoutAsync();

        // Zkontroluje, zda je uživatel přihlášen
        Task<bool> IsUserAuthenticated();

        // Notifikuje o změně stavu autentizace
        void NotifyAuthenticationStateChanged(bool isAuthenticated);

        // Notifikuje o přihlášení uživatele
        void NotifyUserLoggedIn(string userId);

        // SPRÁVA UŽIVATELSKÉHO PROFILU
        // Získá aktuálně přihlášeného uživatele
        Task<AppUser> GetCurrentUserAsync();

        // Získá ID aktuálně přihlášeného uživatele
        string? GetCurrentUserId();

        // Získá e-mail podle uživatelského jména
        Task<string> GetEmailByUsernameAsync(string username);

        // Obnoví heslo pro daný e-mail
        Task<bool> ResetPasswordAsync(string email);

        // Aktualizuje uživatelský profil
        Task<bool> UpdateUserProfileAsync(UserProfileDto updateDTO);

        // Deaktivuje uživatelský účet
        Task<bool> DeactivateAccountAsync(string userId);

        // Nahraje profilový obrázek
        Task<string> UploadProfileImageAsync(byte[] imageData, string fileName);

        // Smaže profilový obrázek
        Task DeleteProfileImageAsync(string imageUrl);

        // Zkontroluje, zda existuje obrázek na dané URL
        Task<bool> DoesImageExistAsync(string imageUrl);

        // SPRÁVA TOKENU
        // Obnoví token pomocí refresh tokenu
        Task<string> RefreshTokenAsync(string refreshToken);

        // Ověří platnost tokenu
        Task<bool> IsTokenValidAsync(string token);

        // Spustí časovač pro obnovu tokenu
        void StartTokenRefreshTimer();

        // SPRÁVA SESSiON
        // Zaregistruje aktivní relaci pro dané zařízení
        Task RegisterActiveSessionAsync(string userId, string deviceId);
    }
}