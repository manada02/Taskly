// FirebaseAuthService.cs - Služba pro autentizaci a správu uživatelských účtů
using Google.Cloud.Firestore;
using Taskly.Services.Cache;
using Taskly.Models;
using Taskly.Firebase_config;
using FirebaseAdmin.Auth;
using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using Firebase.Storage;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using MudBlazor;
using Taskly.Services.Notification;
using Taskly.Services.Connectivity;

namespace Taskly.Services.Auth
{
    public class FirebaseAuthService : IAuthService
    {
        // PROMĚNNÉ A ZÁVISLOSTI
        private readonly FirebaseAuth _firebaseAuth;
        private readonly FirestoreDb _firestoreDb;
        private readonly TasklyFirebaseConfig _firebaseConfig;
        private readonly ILogger<FirebaseAuthService> _logger;

        private UserRecord? _currentUser;  // Data z Firebase Authentication (např. UID, e-mail, stav účtu). Potřebuju k validaci přihlášení, protože Firebase Authentication spravuje uživatelská oprávnění.  Slouží k ověřování uživatele (z Firebase Authentication)

        private readonly FirebaseStorage _firebaseStorage; 
        private string? _currentUserToken;
        private readonly HttpClient _httpClient;

        private Timer? _tokenRefreshTimer;
        private const int TokenRefreshInterval = 55 * 60 * 1000; // 55 minut 

        private bool _isRestoringSession = false;  // Zabraňuje duplicitní restore
        private static bool _isAuthOperationInProgress = false; // Zabraňuje duplicitní auth operace  

        //příznaky pro práci s více zařízeními
        private readonly string _deviceId;
        private FirestoreChangeListener? _activeSessionListener;

        //pro cachování paměti
        private readonly ICacheService _cacheService;

        private readonly ConnectivityService _connectivityService;

        // UDÁLOSTI
        //eventy pro změnu
        public event Action<AppUser>? UserProfileUpdated;

        public event Action<bool>? AuthenticationStateChanged;

        // event pro změnu profilového obrázku
        public event Action<string>? ProfileImageChanged;

        public event Action<string>? UserLoggedIn;

        public event Action? ForcedLogoutDetected;

        // KONSTRUKTOR
        public FirebaseAuthService(TasklyFirebaseConfig firebaseConfig, ILogger<FirebaseAuthService> logger, ICacheService cacheService, ConnectivityService connectivityService)
        {
            _firebaseConfig = firebaseConfig;
            _logger = logger;
            _httpClient = new HttpClient();

            // Inicializace Device ID pro sledování přihlášení
            _deviceId = GetDeviceId();

            _cacheService = cacheService;

            _connectivityService = connectivityService;

            // Nastavení cesty k Firebase credentials
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS")))
            {
                Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS",
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Firebase_config/firebase-credentials.json"));
            }

            // Inicializace FirebaseApp (pokud není již inicializováno)
            if (FirebaseApp.DefaultInstance == null)
            {
                string credentialsPath;

#if ANDROID
                    // Na Androidu vytvoříme cestu přímo do cache adresáře
                    credentialsPath = Path.Combine(FileSystem.CacheDirectory, "firebase-credentials.json");
    
                    // Zkopírujeme soubor z assetů do cache, pokud tam ještě není
                    if (!File.Exists(credentialsPath))
                    {
                        try
                        {
                            // Zkusíme cestu s podsložkou Firebase_config
                            using var assetStream = FileSystem.OpenAppPackageFileAsync("Firebase_config/firebase-credentials.json").Result;
                            using var fileStream = File.Create(credentialsPath);
                            assetStream.CopyTo(fileStream);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Chyba při načítání souboru: {ex}");

                            // Pokud selže, zkusíme ještě alternativní přístup s embedded resource
                            var assembly = typeof(App).Assembly;
                            var resourceName = assembly.GetManifestResourceNames()
                                .FirstOrDefault(n => n.EndsWith("firebase-credentials.json"));
                
                            if (resourceName != null)
                            {
                                using var stream = assembly.GetManifestResourceStream(resourceName);
                                using var fileStream = File.Create(credentialsPath);
                                stream?.CopyTo(fileStream);
                            }
                            else
                            {
                                throw new FileNotFoundException("Nepodařilo se najít soubor firebase-credentials.json", ex);
                            }
                        }
                    }
#else
                // Na Windows použijeme původní cestu
                credentialsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Firebase_config/firebase-credentials.json");
#endif
                // Ověření platnosti credentials a konfigurace před inicializací Firebase
                ValidateCredentialsFile(credentialsPath);
                ValidateFirebaseConfig();

                FirebaseApp.Create(new AppOptions
                {
                    Credential = GoogleCredential.FromFile(credentialsPath),
                    ProjectId = _firebaseConfig.ProjectId
                });
            }

            // Inicializace Firebase Auth a Firestore
            _firebaseAuth = FirebaseAuth.DefaultInstance;
            _firestoreDb = FirestoreConfig.GetFirestoreDatabase();

            // Inicializace Firebase Storage v konstruktoru s použitím správné URL
            _firebaseStorage = new FirebaseStorage(
                _firebaseConfig.StorageBucket,
                new FirebaseStorageOptions
                {
                    AuthTokenAsyncFactory = () => Task.FromResult(_currentUserToken),
                    ThrowOnCancel = true
                }
            );

            logger.LogInformation($"Použitý Firebase Storage bucket: {firebaseConfig.StorageBucket}");
            logger.LogInformation($"Inicializováno zařízení s ID: {_deviceId}");
        }



        // NOTIFIKACE
        // Informujeme o změně profilového obrázku
        public void NotifyProfileImageChanged(string newImageUrl)
        {
            ProfileImageChanged?.Invoke(newImageUrl);
        }

        // Informujeme o aktualizaci uživatelského profilu
        public void NotifyUserProfileUpdated(AppUser updatedUser)
        {
            UserProfileUpdated?.Invoke(updatedUser);
        }

        // Informujeme o přihlášení uživatele
        public void NotifyUserLoggedIn(string userId)
        {
            UserLoggedIn?.Invoke(userId);
        }

        // Informujeme o změně stavu autentizace
        public void NotifyAuthenticationStateChanged(bool isAuthenticated)
        {
            AuthenticationStateChanged?.Invoke(isAuthenticated);
        }

        // AUTENTIZACE A SPRÁVA RELACE
        // Pokuším se obnovit relaci uživatele po restartu aplikace
        public async Task<bool> TryRestoreSessionAsync()
        {
            // Počkejte, dokud není dostupné ověření
            while (_isAuthOperationInProgress)
            {
                await Task.Delay(100);
                _logger.LogInformation("TryRestoreSessionAsync: Čekám na dokončení jiné autentizační operace");
            }

            _isAuthOperationInProgress = true;

            // Zabráníme souběžnému volání
            if (_isRestoringSession)
            {
                _logger.LogInformation("TryRestoreSessionAsync: Již probíhá obnova session, čekám");
                await Task.Delay(5000);
                return _currentUser != null;  
            }

            _isRestoringSession = true;

            try
            {
                var callingMethod = new System.Diagnostics.StackTrace().GetFrame(1)?.GetMethod()?.Name;
                _logger.LogInformation($"TryRestoreSessionAsync voláno z: {callingMethod}");
                _logger.LogInformation("TryRestoreSessionAsync: Začátek kontroly session");

                // Načteme tokeny
                _logger.LogInformation("TryRestoreSessionAsync: Načítám tokeny");
                var idToken = await SecureStorage.GetAsync("firebase_token");
                var refreshToken = await SecureStorage.GetAsync("refresh_token");
                var userLoggedIn = await SecureStorage.GetAsync("user_logged_in");

                // Kontrola offline režimu
                bool isConnected = _connectivityService.IsConnected;

                // Pokud jsme offline a máme uložené tokeny, udržet offline přihlášení
                if (!isConnected && userLoggedIn == "true" && !string.IsNullOrEmpty(refreshToken))
                {
                    _logger.LogInformation("TryRestoreSessionAsync: Detekován offline režim s uloženými tokeny");

                    // Nastavíme příznak offline přihlášení
                    await SecureStorage.SetAsync("is_offline_authenticated", "true");
                    await SecureStorage.SetAsync("is_offline_mode", "true");

                    // Pokusíme se načíst uživatele z cache
                    var cachedUser = await _cacheService.GetOrCreateAsync<AppUser?>("currentUser", () => {

                        // V offline režimu nemůžeme vytvářet nová data, vrátíme null
                        return Task.FromResult<AppUser?>(null);
                    });

                    if (cachedUser != null)
                    {
                        _logger.LogInformation($"TryRestoreSessionAsync: Použití cachovaného uživatele {cachedUser.Username} v offline režimu");

                        // Vyvoláme událost pro informování ostatních částí aplikace
                        NotifyAuthenticationStateChanged(true);

                        _isRestoringSession = false;
                        _isAuthOperationInProgress = false;
                        return true;
                    }
                    else
                    {
                        _logger.LogWarning("TryRestoreSessionAsync: Nenalezen cachovaný uživatel v offline režimu");
                        //  v případě chybějícího cache zachováme tokeny
                        _isRestoringSession = false;
                        _isAuthOperationInProgress = false;
                        return false;
                    }
                }

                // Standardní online kontrola tokenů
                if (string.IsNullOrEmpty(refreshToken))
                {
                    _logger.LogInformation("TryRestoreSessionAsync: Chybí refresh token");
                    _isRestoringSession = false;
                    _isAuthOperationInProgress = false;
                    return false;
                }

                try
                {
                    // Pokračujeme standardním flow pro online režim 
                    if (!string.IsNullOrEmpty(idToken))
                    {
                        _logger.LogInformation("TryRestoreSessionAsync: Pokus o použití existujícího ID tokenu");
                        try
                        {
                            var user = await LoginAsync(idToken);
                            if (user != null)
                            {
                                _logger.LogInformation("TryRestoreSessionAsync: Úspěšné přihlášení s existujícím tokenem");
                                _currentUser = await _firebaseAuth.GetUserAsync(user.DocumentId);
                                await SetUserOnline();

                                // Uložíme uživatele do cache - sjednocení klíčů
                                await _cacheService.SetAsync("currentUser", user, TimeSpan.FromHours(24));

                                // Pokud má uživatel profilový obrázek
                                if (!string.IsNullOrEmpty(user.ProfileImageUrl))
                                {
                                    bool imageExists = await DoesImageExistAsync(user.ProfileImageUrl);
                                    await _cacheService.SetAsync("currentUserImageExists", imageExists, TimeSpan.FromHours(24));
                                    await _cacheService.SetAsync("currentUserImageUrl", user.ProfileImageUrl, TimeSpan.FromHours(24));
                                }

                                // Nastavení flagu pro rychlé přihlášení
                                await SecureStorage.SetAsync("user_logged_in", "true");

                                // Odstraníme příznak offline režimu, pokud existoval
                                SecureStorage.Remove("is_offline_authenticated");

                                _isRestoringSession = false;
                                _isAuthOperationInProgress = false;
                                return true;
                            }
                        }
                        catch (Exception tokenException)
                        {
                            _logger.LogWarning(tokenException, "TryRestoreSessionAsync: Token vypršel, zkusím refresh");
                        }
                    }

                    _logger.LogInformation("TryRestoreSessionAsync: Pokus o refresh tokenu");
                    var newIdToken = await RefreshTokenAsync(refreshToken);

                    if (!string.IsNullOrEmpty(newIdToken))
                    {
                        _logger.LogInformation("TryRestoreSessionAsync: Token úspěšně obnoven");
                        await SecureStorage.SetAsync("firebase_token", newIdToken);
                        var user = await LoginAsync(newIdToken);
                        if (user != null)
                        {
                            _logger.LogInformation("TryRestoreSessionAsync: Úspěšné přihlášení s novým tokenem");
                            _currentUser = await _firebaseAuth.GetUserAsync(user.DocumentId);
                            await SetUserOnline();

                            // Sjednocení klíčů pro cache
                            await _cacheService.SetAsync("currentUser", user, TimeSpan.FromHours(24));

                            // pokud má uživatel profilový obrázek
                            if (!string.IsNullOrEmpty(user.ProfileImageUrl))
                            {
                                bool imageExists = await DoesImageExistAsync(user.ProfileImageUrl);
                                await _cacheService.SetAsync("currentUserImageExists", imageExists, TimeSpan.FromHours(24));
                                await _cacheService.SetAsync("currentUserImageUrl", user.ProfileImageUrl, TimeSpan.FromHours(24));
                            }

                            // Nastavíme flag pro rychlé přihlášení
                            await SecureStorage.SetAsync("user_logged_in", "true");

                            // Odstraníme příznak offline režimu, pokud existoval
                            SecureStorage.Remove("is_offline_authenticated");

                            _isRestoringSession = false;
                            _isAuthOperationInProgress = false;
                            return true;
                        }
                    }

                    // Jsme online, ale autentizace selhala - nyní můžeme vyčistit tokeny
                    if (isConnected)
                    {
                        _logger.LogInformation("TryRestoreSessionAsync: Online režim, ale autentizace selhala - čištění tokenů");
                        SecureStorage.Remove("firebase_token");
                        SecureStorage.Remove("refresh_token");
                        SecureStorage.Remove("user_logged_in");
                        await SetUserOffline();
                    }
                    else
                    {
                        // Jsme offline a autentizace selhala, ale necháme tokeny pro budoucí použití
                        _logger.LogInformation("TryRestoreSessionAsync: Offline režim, autentizace selhala, ale zachováme tokeny");
                        await SecureStorage.SetAsync("is_offline_authenticated", "true");
                    }

                    return false;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "TryRestoreSessionAsync: Chyba při obnově session");

                    // Kontrola připojení před čištěním tokenů
                    if (isConnected)
                    {
                        _logger.LogInformation("TryRestoreSessionAsync: Čištění tokenů (online režim)");
                        SecureStorage.Remove("firebase_token");
                        SecureStorage.Remove("refresh_token");
                        SecureStorage.Remove("user_logged_in");
                        await SetUserOffline();
                    }
                    else
                    {
                        _logger.LogInformation("TryRestoreSessionAsync: Zachovávám tokeny (offline režim)");
                        await SecureStorage.SetAsync("is_offline_authenticated", "true");
                    }

                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TryRestoreSessionAsync: Neočekávaná chyba při obnově relace");

                // Kontrola připojení před čištěním tokenů
                if (_connectivityService.IsConnected)
                {
                    SecureStorage.Remove("user_logged_in");
                    await SetUserOffline();
                }

                return false;
            }
            finally
            {
                _isRestoringSession = false;
                _isAuthOperationInProgress = false;
            }
        }

        // Zaregistrujeme nového uživatele
        public async Task<AppUser> RegisterAsync(UserRegistrationDto registrationDto)
        {
            _logger.LogInformation("Zahajuji registraci uživatele s e-mailem {Email}", registrationDto.Email);

            // Nejdřív uděláme validaci modelu
            if (!Validator.TryValidateObject(registrationDto, new ValidationContext(registrationDto), null, true))
            {
                throw new ApplicationException("Neplatná data pro registraci.");
            }

            // Kontrolujeme existenci username
            var usernameQuery = await _firestoreDb.Collection("users")
                .WhereEqualTo("username", registrationDto.Username)
                .GetSnapshotAsync();

            if (usernameQuery.Documents.Count > 0)
            {
                throw new ApplicationException("Toto uživatelské jméno je již obsazené.");
            }

            try
            {
                // Vytvoření uživatele pomocí Firebase Admin SDK
                var userArgs = new UserRecordArgs
                {
                    Email = registrationDto.Email,
                    Password = registrationDto.Password,
                    DisplayName = registrationDto.Username,
                    EmailVerified = false
                };

                var userRecord = await _firebaseAuth.CreateUserAsync(userArgs);

                // Vytvoření uživatele v Firestore
                var appUser = new AppUser
                {
                    DocumentId = userRecord.Uid,
                    Email = registrationDto.Email,
                    Username = registrationDto.Username,
                    CreatedAt = DateTime.UtcNow,
                    IsActive = true,
                    IsOnline = false,

                };

                await _firestoreDb.Collection("users").Document(appUser.DocumentId).SetAsync(appUser);

                _logger.LogInformation("Registrace uživatele {Email} byla úspěšně dokončena", registrationDto.Email);
                return appUser;
            }
            catch (FirebaseAdmin.Auth.FirebaseAuthException ex)
            {
                _logger.LogError(ex, "Chyba Firebase při registraci uživatele {Email}", registrationDto.Email);

                string errorMessage = ex.AuthErrorCode switch
                {
                    AuthErrorCode.EmailAlreadyExists => "Email je již registrován",


                    _ => $"Chyba při registraci: {ex.Message}"
                };

                throw new ApplicationException(errorMessage);
            }
        }

        // Přihlásíme uživatele pomocí Firebase tokenu
        public async Task<AppUser> LoginAsync(string firebaseToken)
        {
            string loginIdentifier = firebaseToken;

            // Nejdřív validujeme model
            if (!Validator.TryValidateObject(loginIdentifier, new ValidationContext(loginIdentifier), null, true))
            {
                throw new ApplicationException("Neplatná data pro přihlášení.");
            }
            try
            {
                // Ověříme Firebase token
                var decodedToken = await _firebaseAuth.VerifyIdTokenAsync(firebaseToken);
                var uid = decodedToken.Uid;

                // Získání stavu uživatele z Firebase Auth
                var firebaseUser = await _firebaseAuth.GetUserAsync(uid);

                // Načtení dat uživatele z Firestore
                var userDocument = await _firestoreDb.Collection("users")
                    .Document(uid)
                    .GetSnapshotAsync();
                if (!userDocument.Exists)
                {
                    throw new ApplicationException("Uživatelská data nebyla nalezena.");
                }

                //automaticky převádí dokument z Firestore na objekt AppUser.
                var user = userDocument.ConvertTo<AppUser>(); 

                // Aktualizace posledního přihlášení
                var updates = new Dictionary<string, object>    
        {
            { "lastLoginAt", DateTime.UtcNow },
            { "isActive", !firebaseUser.Disabled },   // Nastaví isActive podle stavu v Auth
            { "isOnline", true }
        };
                await _firestoreDb.Collection("users")
                    .Document(uid)
                    .UpdateAsync(updates);

                // Aktualizace lokálního objektu s novými hodnotami
                user.LastLoginAt = DateTime.UtcNow;
                user.IsActive = !firebaseUser.Disabled;
                user.IsOnline = true;

                _currentUser = await _firebaseAuth.GetUserAsync(uid);  // Data z Firebase Authentication
                _currentUserToken = firebaseToken;

                // Po úspěšném přihlášení zaregistrujte aktivní session
                await RegisterActiveSessionAsync(_currentUser.Uid, _deviceId);


                // Spuštění real-time listeneru pro sledování změn v aktivních sessions
                // Listener automaticky detekuje přihlášení na jiném zařízení a provede vynucené odhlášení
                StartActiveSessionListener(_currentUser.Uid);

                // Spuštění timeru pro refresh tokenů
                StartTokenRefreshTimer();

                // Vyvolá událost informující ostatní části aplikace o změně stavu přihlášení
                // Parametr 'true' značí, že uživatel je nyní přihlášen. Tuto událost mohou odchytit
                // například UI komponenty pro aktualizaci zobrazení nebo navigace
                AuthenticationStateChanged?.Invoke(true);

                // Uložíme uživatele do cache
                await _cacheService.SetAsync($"user:{user.DocumentId}", user, TimeSpan.FromHours(24));
                _logger.LogInformation($"LoginAsync: Uživatel {user.Username} (ID: {user.DocumentId}) uložen do cache");

                //  pokud má uživatel profilový obrázek, uložit i informaci o jeho existenci
                if (!string.IsNullOrEmpty(user.ProfileImageUrl))
                {
                    bool imageExists = await DoesImageExistAsync(user.ProfileImageUrl);
                    await _cacheService.SetAsync($"imageExists:{user.ProfileImageUrl}", imageExists, TimeSpan.FromHours(24));
                    _logger.LogInformation($"LoginAsync: Info o obrázku profilu ({user.ProfileImageUrl}) uloženo do cache, existence: {imageExists}");
                }

                await SecureStorage.SetAsync("user_logged_in", "true");
                _logger.LogInformation("LoginAsync: Flag rychlého přihlášení nastaven na 'true'");


                // Synchronizace notifikací pro přihlášeného uživatele
                _logger.LogInformation("Vyvolávám událost UserLoggedIn pro uživatele {UserId}", _currentUser.Uid);
                UserLoggedIn?.Invoke(_currentUser.Uid);

                return user;
            }
            catch (FirebaseAdmin.Auth.FirebaseAuthException ex)
            {
                _logger.LogError(ex, "Chyba Firebase při přihlášení uživatele s tokenem {FirebaseToken}", loginIdentifier);
                string errorMessage = ex.AuthErrorCode switch
                {
                    AuthErrorCode.UserNotFound => "Uživatel s tímto tokenem neexistuje.",
                    AuthErrorCode.EmailNotFound => "Uživatel s tímto emailem neexistuje.",

                    _ => $"Chyba při přihlášení: {ex.Message}"
                };
                throw new ApplicationException(errorMessage);
            }
        }

        // Odhlásíme uživatele podle jeho ID
        public async Task<bool> LogoutAsync(string userId)
        {
            _logger.LogInformation("Zahájení odhlášení pro uživatele s ID {UserId}", userId);
            try
            {
                if (string.IsNullOrEmpty(userId))
                {
                    _logger.LogWarning("Odhlášení se nepodařilo: userId je prázdné");
                    return false;
                }

                // Vždy se pokusíme získat aktuálního uživatele z Firebase Auth
                try
                {
                    _currentUser = await _firebaseAuth.GetUserAsync(userId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Nelze získat uživatele z Firebase Auth");
                    // Pokračujeme dál, protože chceme dokončit odhlášení i když selže získání uživatele
                }

                // Aktualizace Firestore bez ohledu na stav _currentUser
                try
                {
                    await _firestoreDb.Collection("users")
                        .Document(userId)
                        .UpdateAsync(new Dictionary<string, object>
                        {
                    { "isOnline", false },
                    { "lastLoginAt", Timestamp.FromDateTime(DateTime.UtcNow) } // Uložíme poslední aktivitu
                        });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Chyba při aktualizaci online stavu v Firestore");
                }

                // část pro odstranění session v LogoutAsync
                try
                {
                    var sessionQuery = _firestoreDb.Collection("active_sessions")
                        .WhereEqualTo("userId", userId)
                        .WhereEqualTo("deviceId", _deviceId);

                    var snapshot = await sessionQuery.GetSnapshotAsync();

                    if (snapshot.Count > 0)
                    {
                        var sessionDoc = snapshot.Documents[0];
                        await _firestoreDb.Collection("active_sessions").Document(sessionDoc.Id).DeleteAsync();
                        _logger.LogInformation($"Odstraněna aktivní session pro uživatele {userId} na zařízení {_deviceId}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Chyba při odstraňování aktivní session: {Message}", ex.Message);
                }

                // Pokus o revokaci tokenů
                try
                {
                    await _firebaseAuth.RevokeRefreshTokensAsync(userId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Chyba při revokaci refresh tokenů");
                }

                // Vyčištění lokálního stavu
                _currentUser = null;
                _currentUserToken = null;

                // Oznámení změny stavu autentizace
                AuthenticationStateChanged?.Invoke(false);

                // Vyčištění secure storage
                try
                {
                    SecureStorage.Remove("firebase_token");
                    SecureStorage.Remove("refresh_token");
                    SecureStorage.Remove("user_logged_in");

                    //  Odstraníme příznaky offline režimu
                    SecureStorage.Remove("is_offline_authenticated");
                    SecureStorage.Remove("is_offline_mode");
                    _logger.LogInformation("LogoutAsync: Vyčištěny příznaky offline režimu");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Chyba při mazání tokenů a příznaků ze secure storage");
                }

                // Zastavení refresh timeru
                if (_tokenRefreshTimer != null)
                {
                    _tokenRefreshTimer.Dispose();
                    _tokenRefreshTimer = null;
                }

                // Smazat data z cache - použít obecné klíče
                try
                {
                    await _cacheService.ClearAsync("currentUser");
                    await _cacheService.ClearAsync("currentUserImageUrl");
                    await _cacheService.ClearAsync("currentUserImageExists");

                    _logger.LogInformation("LogoutAsync: Vyčištěna cache pro uživatele");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Chyba při čištění cache při odhlášení");
                }

                // Zastavení real-time listeneru pro sledování změn v aktivních sessions
                // Při odhlášení již nepotřebujeme sledovat změny v sessions
                StopActiveSessionListener();

                _logger.LogInformation("Uživatel s ID {UserId} byl úspěšně odhlášen", userId);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Neočekávaná chyba při odhlášení uživatele s ID {UserId}", userId);
                return false;
            }
        }

        // Provedeme nucené odhlášení uživatele
        public async Task ForcedLogoutAsync()
        {
            try
            {
                _logger.LogInformation("Zahájeno nucené odhlášení uživatele");

                // Získání ID uživatele před odhlášením
                string? userId = GetCurrentUserId();
                if (string.IsNullOrEmpty(userId))
                {
                    _logger.LogWarning("Nelze provést nucené odhlášení - žádný přihlášený uživatel");
                    return;
                }

                // Zastavení listeneru před odhlášením
                // (důležité pro prevenci opakovaného volání této funkce z listeneru)
                StopActiveSessionListener();

                // Provedení standardního odhlášení
                await LogoutAsync(userId);

                // Vyčištění lokálních dat (můžeme přidat i specifické čištění pro nucené odhlášení)
                _cacheService.Clear("user_data"); // vyčištění cache specifické pro nucené odhlášení

                _logger.LogInformation("Nucené odhlášení dokončeno, vyvolávám událost ForcedLogoutDetected");

                // Vyvolání specifické události pro nucené odhlášení
                ForcedLogoutDetected?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Chyba při nuceném odhlášení: {ex.Message}");

                //  v případě chyby se pokusíme vyvolat událost, aby UI vědělo,
                // že došlo k problému s autentizací
                ForcedLogoutDetected?.Invoke();
            }
        }

        // Zkontrolujeme, zda je uživatel přihlášen
        public async Task<bool> IsUserAuthenticated()
        {
            // Max čas čekání na odemknutí - předejde nekonečné smyčce
            int maxWaitMs = 5000;
            int waitedMs = 0;

            while (_isAuthOperationInProgress && waitedMs < maxWaitMs)
            {
                await Task.Delay(100);
                waitedMs += 100;
                _logger.LogInformation("IsUserAuthenticated: Čekám na dokončení jiné autentizační operace");
            }
            // Pokud jsme stále zamknuti po maxWaitMs, vynuceně odemkneme
            if (_isAuthOperationInProgress && waitedMs >= maxWaitMs)
            {
                _logger.LogWarning("IsUserAuthenticated: Vynucené odemknutí po timeoutu");
                _isAuthOperationInProgress = false;
            }

            // Použijeme try-finally pro zajištění odemčení i při výjimce
            _isAuthOperationInProgress = true;

            try
            {
                // Kontrola offline přihlášení 
                bool isConnected = _connectivityService.IsConnected;
                var userLoggedIn = await SecureStorage.GetAsync("user_logged_in");
                var offlineAuth = await SecureStorage.GetAsync("is_offline_authenticated");

                // Pokud jsme v offline režimu a máme příznak přihlášení
                if (!isConnected && (userLoggedIn == "true" || offlineAuth == "true"))
                {
                    _logger.LogInformation("IsUserAuthenticated: Používám offline přihlášení");
                    return true;
                }

                // Online kontrola přihlášení
                if (_currentUser == null)
                {
                    // Kontrola existence tokenů
                    var idToken = await SecureStorage.GetAsync("firebase_token");
                    var refreshToken = await SecureStorage.GetAsync("refresh_token");

                    if (string.IsNullOrEmpty(idToken) || string.IsNullOrEmpty(refreshToken))
                    {
                        _isAuthOperationInProgress = false;
                        return false;
                    }

                    // Kontaktujeme Firebase pouze když jsme online
                    if (isConnected)
                    {
                        try
                        {
                            // Použijeme lehčí metody pro ověření tokenu
                            if (await IsTokenValidAsync(idToken))
                            {
                                // Získání základních informací o uživateli
                                var decodedToken = await _firebaseAuth.VerifyIdTokenAsync(idToken);
                                var firebaseUser = await _firebaseAuth.GetUserAsync(decodedToken.Uid);
                                if (firebaseUser != null && !firebaseUser.Disabled)
                                {
                                    _currentUser = firebaseUser;  // Uložení do cache
                                    _isAuthOperationInProgress = false;
                                    return true;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "IsUserAuthenticated: Chyba při online ověření tokenu");
                        }

                        _isAuthOperationInProgress = false;
                        return false;
                    }
                    else if (userLoggedIn == "true")
                    {
                        // Offline režim s platným přihlášením
                        _logger.LogInformation("IsUserAuthenticated: Offline režim s platným přihlášením");
                        _isAuthOperationInProgress = false;
                        return true;
                    }

                    _isAuthOperationInProgress = false;
                    return false;
                }

                // Ověříme, zda je uživatel stále platný (pouze když jsme online)
                if (_currentUser != null)
                {
                    if (isConnected)
                    {
                        try
                        {
                            var firebaseUser = await _firebaseAuth.GetUserAsync(_currentUser.Uid);
                            _isAuthOperationInProgress = false;
                            return firebaseUser != null && !firebaseUser.Disabled;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "IsUserAuthenticated: Chyba při ověřování Firebase uživatele");

                            // Pokud došlo k chybě a máme příznak přihlášení, důvěřujeme lokálnímu stavu
                            if (userLoggedIn == "true")
                            {
                                _isAuthOperationInProgress = false;
                                return true;
                            }
                        }
                    }
                    else
                    {
                        // V offline režimu důvěřujeme existujícímu _currentUser
                        _isAuthOperationInProgress = false;
                        return true;
                    }
                }

                _isAuthOperationInProgress = false;
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "IsUserAuthenticated: Neočekávaná chyba při ověřování autentizace");
                return false;
            }

            finally
            {
                // Toto se vykoná vždy - při úspěchu i při výjimce
                _isAuthOperationInProgress = false;
            }
        }

        // SPRÁVA UŽIVATELSKÉHO PROFILU
        // Získáme údaje přihlášeného uživatele z Firestore databáze
        public async Task<AppUser> GetCurrentUserAsync()
        {
            if (_currentUser == null)
            {
                throw new ApplicationException("Žádný uživatel není přihlášen");
            }

            _logger.LogInformation("GetCurrentUserAsync: Hledám data pro UID {Uid}", _currentUser.Uid);


            var userDoc = await _firestoreDb.Collection("users")
                .Document(_currentUser.Uid)
                .GetSnapshotAsync();

            if (!userDoc.Exists)
            {
                _logger.LogWarning("GetCurrentUserAsync: Data pro UID {Uid} nebyla nalezena ve Firestore", _currentUser.Uid);
                throw new ApplicationException("Data uživatele nebyla nalezena");
            }

            return userDoc.ConvertTo<AppUser>();
        }

        // Získáme ID okamžitě z paměti, bez volání databáze
        public string? GetCurrentUserId()
        {
            return _currentUser?.Uid;
        }

        // Získáme e-mail podle uživatelského jména
        public async Task<string> GetEmailByUsernameAsync(string username)
        {
            try
            {
                var snapshot = await _firestoreDb.Collection("users")
                    .WhereEqualTo("username", username)
                    .GetSnapshotAsync();

                if (snapshot.Count == 0)
                    throw new ApplicationException("Uživatel nebyl nalezen.");

                if (snapshot.Count > 1)
                    throw new ApplicationException("Nalezeno více uživatelů se stejným username.");

                var user = snapshot.Documents[0].ConvertTo<AppUser>();
                return user.Email;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při hledání emailu podle username");
                throw;
            }
        }

        // Obnovíme heslo pro daný e-mail
        public async Task<bool> ResetPasswordAsync(string email)
        {
            _logger.LogInformation("Spouštím proces resetu hesla pro e-mail {Email}", email);

            try
            {
                // URL pro odeslání resetu hesla (API klíč je z konfigurace)
                string url = $"https://identitytoolkit.googleapis.com/v1/accounts:sendOobCode?key={_firebaseConfig.ApiKey}";

                // Tělo požadavku
                var requestBody = new
                {
                    requestType = "PASSWORD_RESET",  // Určujeme typ požadavku
                    email  // E-mail pro reset hesla
                };

                // Odeslání POST požadavku na Firebase API
                using var client = new HttpClient();
                var response = await client.PostAsJsonAsync(url, requestBody);

                // Zpracování odpovědi
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("E-mail pro reset hesla byl úspěšně odeslán na {Email}", email);
                    return true;  // Pokud je odpověď úspěšná, vrátí true
                }

                // Pokud odpověď není úspěšná, zpracujeme chybu
                var error = await response.Content.ReadAsStringAsync();
                _logger.LogError("Chyba při resetu hesla: {Error}", error);
                throw new ApplicationException("Chyba při resetu hesla: " + error);  // Vyvolání výjimky, pokud požadavek selže
            }


            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "Chyba při pokusu o připojení k API (možná není připojení k internetu).");
                throw new ApplicationException("Nemohu se připojit k internetu. Zkontrolujte připojení k síti.");
            }

            catch (Exception ex)
            {
                // Pokud dojde k neočekávané chybě, logujeme ji a vyvoláme výjimku
                _logger.LogError(ex, "Neočekávaná chyba při resetu hesla pro e-mail {Email}", email);
                throw new ApplicationException("Došlo k neočekávané chybě. Zkuste to prosím znovu.");
            }
        }

        // Aktualizujeme uživatelský profil
        public async Task<bool> UpdateUserProfileAsync(UserProfileDto updateDTO)
        {
            try
            {
                if (_currentUser == null)
                {
                    return false;
                }
                var usersRef = _firestoreDb.Collection("users");
                var updateData = new Dictionary<string, object>();
                var currentUserData = await GetCurrentUserAsync();
                var originalEmail = _currentUser.Email;
                bool needsAuthUpdate = false;

                // Kontrola username
                if (!string.IsNullOrEmpty(updateDTO.UserName) && updateDTO.UserName != currentUserData.Username)
                {
                    var usernameQuery = await usersRef.WhereEqualTo("username", updateDTO.UserName).Limit(1).GetSnapshotAsync();
                    if (usernameQuery.Documents.Any() && usernameQuery.Documents[0].Id != _currentUser.Uid)
                    {
                        throw new Exception("Uživatel se stejným uživatelským jménem už existuje.");
                    }
                    updateData["username"] = updateDTO.UserName;
                    needsAuthUpdate = true;
                }

                // Kontrola email
                if (!string.IsNullOrEmpty(updateDTO.Email) && updateDTO.Email != originalEmail)
                {
                    var emailQuery = await usersRef.WhereEqualTo("email", updateDTO.Email).Limit(1).GetSnapshotAsync();
                    if (emailQuery.Documents.Any() && emailQuery.Documents[0].Id != _currentUser.Uid)
                    {
                        throw new Exception("Uživatel s tímto emailem už existuje.");
                    }
                    await _firebaseAuth.UpdateUserAsync(new UserRecordArgs
                    {
                        Uid = _currentUser.Uid,
                        Email = updateDTO.Email
                    });
                    updateData["email"] = updateDTO.Email;
                    needsAuthUpdate = true;
                }

                // Ostatní změny
                if (updateDTO.FirstName != null && updateDTO.FirstName != currentUserData.FirstName)
                    updateData["firstName"] = updateDTO.FirstName;
                if (updateDTO.LastName != null && updateDTO.LastName != currentUserData.LastName)
                    updateData["lastName"] = updateDTO.LastName;
                if (updateDTO.ProfileImageUrl != null && updateDTO.ProfileImageUrl != currentUserData.ProfileImageUrl)
                    updateData["ProfileImageUrl"] = updateDTO.ProfileImageUrl;

                // Aktualizace Firestore
                if (updateData.Count > 0)
                {
                    var userRef = usersRef.Document(_currentUser.Uid);
                    await userRef.UpdateAsync(updateData);

                    // Aktualizace Firebase Auth a session při kritických změnách
                    if (needsAuthUpdate)
                    {
                        _currentUser = await _firebaseAuth.GetUserAsync(_currentUser.Uid);
                        await TryRestoreSessionAsync();
                    }

                    // Kontrola _currentUser po jakékoliv změně
                    if (_currentUser == null)
                    {
                        _currentUser = await _firebaseAuth.GetUserAsync(currentUserData.DocumentId);
                    }

                    var updatedUser = new AppUser
                    {
                        Email = updateDTO.Email ?? currentUserData.Email,
                        Username = updateDTO.UserName ?? currentUserData.Username,
                        FirstName = updateDTO.FirstName ?? currentUserData.FirstName,
                        LastName = updateDTO.LastName ?? currentUserData.LastName,
                        ProfileImageUrl = updateDTO.ProfileImageUrl ?? currentUserData.ProfileImageUrl,
                        DocumentId = currentUserData.DocumentId,
                        IsActive = currentUserData.IsActive,
                        IsOnline = currentUserData.IsOnline,
                        LastLoginAt = currentUserData.LastLoginAt
                    };

                    // Aktualizujeme cache
                    if (_cacheService != null)
                    {
                        await _cacheService.SetAsync("currentUser", updatedUser, TimeSpan.FromHours(24));
                        _logger.LogInformation("Cache uživatele aktualizována po úpravě profilu");

                        // Pokud došlo ke změně profilového obrázku, aktualizujeme i cache obrázku
                        if (updateDTO.ProfileImageUrl != null && updateDTO.ProfileImageUrl != currentUserData.ProfileImageUrl)
                        {
                            await _cacheService.SetAsync("currentUserImageUrl", updateDTO.ProfileImageUrl, TimeSpan.FromHours(24));
                            await _cacheService.SetAsync("currentUserImageExists", true, TimeSpan.FromHours(24));
                            _logger.LogInformation("Cache obrázku aktualizována po úpravě profilu");
                        }
                    }


                    NotifyUserProfileUpdated(updatedUser);
                }
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Chyba při aktualizaci profilu: {ex.Message}");
                throw;
            }
        }

        // Deaktivujeme uživatelský účet
        public async Task<bool> DeactivateAccountAsync(string userId)
        {
            _logger.LogInformation("Zahájení deaktivace účtu pro uživatele s ID {UserId}", userId);
            try
            {
                if (string.IsNullOrEmpty(userId))
                {
                    _logger.LogWarning("Deaktivace se nepodařila: userId je prázdné");
                    return false;
                }

                // 1. Deaktivace uživatele v Firebase Authentication
                try
                {
                    await _firebaseAuth.UpdateUserAsync(new UserRecordArgs
                    {
                        Uid = userId,
                        Disabled = true // Tímto se uživatel deaktivuje, ale neodstraní
                    });
                    _logger.LogInformation("Uživatel {UserId} byl deaktivován v Firebase Authentication", userId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Chyba při deaktivaci uživatele v Firebase Authentication");
                    throw;
                }

                // 2. Označení uživatele jako neaktivního v Firestore
                try
                {
                    await _firestoreDb.Collection("users")
                        .Document(userId)
                        .UpdateAsync(new Dictionary<string, object>
                        {
                    { "isActive", false },
                    { "isOnline", false },
                    { "deactivatedAt", Timestamp.FromDateTime(DateTime.UtcNow) }
                        });
                    _logger.LogInformation("Uživatel {UserId} byl označen jako neaktivní v Firestore", userId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Chyba při označení uživatele jako neaktivního v Firestore");
                    throw;
                }

                // 3. Odstranění všech aktivních sessions uživatele
                try
                {
                    var sessionQuery = _firestoreDb.Collection("active_sessions")
                        .WhereEqualTo("userId", userId);

                    var snapshot = await sessionQuery.GetSnapshotAsync();

                    foreach (var sessionDoc in snapshot.Documents)
                    {
                        await _firestoreDb.Collection("active_sessions").Document(sessionDoc.Id).DeleteAsync();
                    }
                    _logger.LogInformation("Odstraněny všechny aktivní sessions pro uživatele {UserId}", userId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Chyba při odstraňování aktivních sessions");

                }

                // 4. Revokace všech refresh tokenů
                try
                {
                    await _firebaseAuth.RevokeRefreshTokensAsync(userId);
                    _logger.LogInformation("Revokovány všechny refresh tokeny pro uživatele {UserId}", userId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Chyba při revokaci refresh tokenů");
                    // Pokračujeme i když se nepodaří revokovat tokeny
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Neočekávaná chyba při deaktivaci účtu uživatele s ID {UserId}", userId);
                return false;
            }
        }

        // Nahrajeme profilový obrázek
        public async Task<string> UploadProfileImageAsync(byte[] imageData, string fileName)
        {
            try
            {
                if (_currentUser == null)
                    throw new ApplicationException("Žádný uživatel není přihlášen");

                // Nejdřív získáme aktuální profil uživatele
                var currentProfile = await GetCurrentUserAsync();

                // Pokud existuje starý profilový obrázek, smažeme ho
                if (!string.IsNullOrEmpty(currentProfile.ProfileImageUrl))
                {
                    try
                    {
                        await DeleteProfileImageAsync(currentProfile.ProfileImageUrl);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Nepodařilo se smazat starý profilový obrázek");
                        // Pokračujeme dál i když se mazání nezdaří
                    }
                }

                //  Kontrola tokenu s pokusem o obnovu
                if (string.IsNullOrEmpty(_currentUserToken))
                {
                    _logger.LogWarning("Chybí token, pokus o obnovení session");

                    // Pokus o obnovení tokenu
                    var restored = await TryRestoreSessionAsync();

                    if (!restored || string.IsNullOrEmpty(_currentUserToken))
                    {
                        _logger.LogWarning("Pokus o upload souboru bez platného autentizačního tokenu");
                        throw new ApplicationException("Chybí platný autentizační token");
                    }

                    _logger.LogInformation("Token úspěšně obnoven");
                }

                _logger.LogInformation($"Začátek nahrávání obrázku pro uživatele {_currentUser.Uid}");

                // Vytvoření celé cesty včetně podsložek
                var task = await _firebaseStorage
                    .Child("profile-images")
                    .Child(_currentUser.Uid)
                    .Child(fileName)
                    .PutAsync(new MemoryStream(imageData), new CancellationToken(), "image/jpeg");

                // Získání URL
                var downloadUrl = await _firebaseStorage
                    .Child("profile-images")
                    .Child(_currentUser.Uid)
                    .Child(fileName)
                    .GetDownloadUrlAsync();

                _logger.LogInformation($"Obrázek úspěšně nahrán, URL: {downloadUrl}");

                //  Aktualizace cache
                if (_cacheService != null)
                {
                    await _cacheService.SetAsync("currentUserImageUrl", downloadUrl, TimeSpan.FromHours(24));
                    await _cacheService.SetAsync("currentUserImageExists", true, TimeSpan.FromHours(24));
                    _logger.LogInformation("Cache obrázku aktualizována po úspěšném nahrání");
                }

                NotifyProfileImageChanged(downloadUrl);
                return downloadUrl;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při nahrávání profilového obrázku");
                throw new ApplicationException($"Detailní chyba při nahrávání: {ex.Message}", ex);
            }
        }

        // Smažeme profilový obrázek
        public async Task DeleteProfileImageAsync(string imageUrl)
        {
            try
            {
                if (_currentUser == null)
                    throw new ApplicationException("Žádný uživatel není přihlášen");

                // Získání relativní cesty z URL
                var path = GetPathFromUrl(imageUrl);
                if (string.IsNullOrEmpty(path))
                    throw new ArgumentException("Neplatné URL obrázku");

                _logger.LogInformation($"Mazání obrázku na cestě: {path}");

                try
                {
                    // Smazání souboru
                    await _firebaseStorage
                        .Child(path)
                        .DeleteAsync();

                    _logger.LogInformation("Obrázek byl úspěšně smazán");
                }
                catch (Firebase.Storage.FirebaseStorageException ex) when (ex.ToString().Contains("404") || ex.ToString().Contains("Not Found"))
                {
                    // Speciální zpracování pro 404 - obrázek už neexistuje, což je v podstatě cíl
                    _logger.LogWarning("Obrázek již neexistuje, není třeba mazat: {Path}", path);
                    // Nepropagujeme tuto chybu dále, protože výsledek je stejný jako bychom obrázek smazali
                }

                // Aktualizace cache - bez ohledu na to, zda obrázek existoval nebo ne
                if (_cacheService != null)
                {
                    await _cacheService.ClearAsync("currentUserImageExists");
                    await _cacheService.ClearAsync("currentUserImageUrl");
                    _logger.LogInformation("Cache obrázku vyčištěna");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při mazání profilového obrázku: {Message}", ex.Message);
                throw new ApplicationException($"Chyba při mazání obrázku: {ex.Message}", ex);
            }
        }

        // Zjistíme, zda obrázek na URL existuje
        public async Task<bool> DoesImageExistAsync(string imageUrl)
        {
            if (string.IsNullOrWhiteSpace(imageUrl))
                return false;

            try
            {
                var response = await _httpClient.SendAsync(new HttpRequestMessage(HttpMethod.Head, imageUrl));
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        // SPRÁVA TOKENU
        // Obnovíme token pomocí refresh tokenu
        public async Task<string> RefreshTokenAsync(string refreshToken)
        {
            try
            {
                // Vytvoření požadavku na Firebase Auth REST API pro obnovení tokenu
                var requestData = new Dictionary<string, string>
        {
            { "grant_type", "refresh_token" },
            { "refresh_token", refreshToken }
        };

                var response = await _httpClient.PostAsync(
                    $"https://securetoken.googleapis.com/v1/token?key={_firebaseConfig.ApiKey}",
                    new FormUrlEncodedContent(requestData)
                );

                if (response.IsSuccessStatusCode)
                {
                    var result = await response.Content.ReadFromJsonAsync<JsonElement>();
                    var newIdToken = result.GetProperty("id_token").GetString();

                    if (string.IsNullOrEmpty(newIdToken))
                    {
                        throw new InvalidOperationException("Získaný token je prázdný");
                    }

                    return newIdToken;
                }
                else
                {
                    var error = await response.Content.ReadAsStringAsync();
                    throw new InvalidOperationException($"Chyba při obnovení tokenu: {error}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při obnovování tokenu");
                throw;
            }
        }

        // Ověříme platnost tokenu
        public async Task<bool> IsTokenValidAsync(string token)
        {
            try
            {
                // Pouze ověření Firebase tokenu bez dalších operací
                var decodedToken = await _firebaseAuth.VerifyIdTokenAsync(token);
                return decodedToken != null;
            }
            catch
            {
                return false;
            }
        }

        // Spustíme časovač pro obnovu tokenu
        public void StartTokenRefreshTimer()
        {
            _tokenRefreshTimer?.Dispose();
            _tokenRefreshTimer = new Timer(async _ =>
            {
                try
                {
                    var refreshToken = await SecureStorage.GetAsync("refresh_token");
                    if (!string.IsNullOrEmpty(refreshToken))
                    {
                        var newIdToken = await RefreshTokenAsync(refreshToken);
                        if (!string.IsNullOrEmpty(newIdToken))
                        {
                            await SecureStorage.SetAsync("firebase_token", newIdToken);
                            _currentUserToken = newIdToken;

                            // Obnovení session v Firestore
                            string? userId = GetCurrentUserId();
                            if (!string.IsNullOrEmpty(userId))
                            {
                                await RefreshSessionAsync(userId);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Chyba při automatickém obnovení tokenu");
                }
            }, null, TokenRefreshInterval, TokenRefreshInterval);
        }

        // SPRÁVA RELACÍ
        // Zaregistrujeme aktivní relaci pro dané zařízení
        public async Task RegisterActiveSessionAsync(string userId, string deviceId)
        {
            try
            {
                _logger.LogInformation($"Registrace aktivní session pro uživatele {userId} na zařízení {deviceId}");

                // Kontrola existující session
                var sessionQuery = _firestoreDb.Collection("active_sessions")
                    .WhereEqualTo("userId", userId);
                var snapshot = await sessionQuery.GetSnapshotAsync();

                // Vyčištění případných duplicitních sessions
                if (snapshot.Count > 1)
                {
                    _logger.LogWarning($"Nalezeno {snapshot.Count} aktivních sessions pro uživatele {userId}. Ponechávám pouze nejnovější.");

                    // Seřazení dokumentů podle času poslední aktivity
                    var sortedDocs = snapshot.Documents
                        .Where(doc => doc.TryGetValue<DateTime>("lastLoginTime", out _))
                        .OrderByDescending(doc => doc.GetValue<DateTime>("lastLoginTime"))
                        .ToList();

                    // Odstranění všech kromě nejnovějšího (pokud existuje)
                    for (int i = 1; i < sortedDocs.Count; i++)
                    {
                        await _firestoreDb.Collection("active_sessions").Document(sortedDocs[i].Id).DeleteAsync();
                        _logger.LogInformation($"Odstraněna duplicitní session {sortedDocs[i].Id}");
                    }

                    // Aktualizace nejnovější session
                    if (sortedDocs.Count > 0)
                    {
                        await _firestoreDb.Collection("active_sessions").Document(sortedDocs[0].Id)
                            .UpdateAsync(new Dictionary<string, object>
                            {
                        { "deviceId", deviceId },
                        { "lastLoginTime", DateTime.UtcNow },


                            });
                        _logger.LogInformation($"Aktualizována nejnovější session pro uživatele {userId}");
                        return;
                    }
                }

                // Standardní zpracování jedné existující session nebo vytvoření nové
                if (snapshot.Count == 1)
                {
                    // Existující session - aktualizujeme
                    var sessionDoc = snapshot.Documents[0];
                    await _firestoreDb.Collection("active_sessions").Document(sessionDoc.Id)
                        .UpdateAsync(new Dictionary<string, object>
                        {
                    { "deviceId", deviceId },
                    { "lastLoginTime", DateTime.UtcNow }
                        });
                    _logger.LogInformation($"Aktualizována existující session pro uživatele {userId}");
                }
                else
                {
                    // Nová session (když count == 0)
                    await _firestoreDb.Collection("active_sessions").AddAsync(new Dictionary<string, object>
            {
                { "userId", userId },
                { "deviceId", deviceId },
                { "lastLoginTime", DateTime.UtcNow }
            });
                    _logger.LogInformation($"Vytvořena nová session pro uživatele {userId}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Chyba při registraci session: {ex.Message}");
            }
        }

        // POMOCNÉ METODY
        // Obnovíme aktivní session pro uživatele
        public async Task RefreshSessionAsync(string userId)
        {
            try
            {
                var sessionQuery = _firestoreDb.Collection("active_sessions")
                    .WhereEqualTo("userId", userId);
                var snapshot = await sessionQuery.GetSnapshotAsync();

                if (snapshot.Count > 0)
                {
                    await _firestoreDb.Collection("active_sessions").Document(snapshot.Documents[0].Id)
                        .UpdateAsync(new Dictionary<string, object>
                        {
                    { "lastLoginTime", DateTime.UtcNow },
                    { "expiresAt", DateTime.UtcNow.AddDays(14) }
                        });
                    _logger.LogInformation($"Obnovena session pro uživatele {userId}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Chyba při obnovení session: {ex.Message}");
            }
        }

        // Nastavíme uživatele jako online v databázi
        public async Task SetUserOnline()
        {
            var userId = _currentUser?.Uid;
            if (!string.IsNullOrEmpty(userId))
            {
                await _firestoreDb.Collection("users").Document(userId)
                    .UpdateAsync(new Dictionary<string, object>
                    {
                { "isOnline", true },
                { "lastLoginAt", Timestamp.FromDateTime(DateTime.UtcNow) }
                    });
            }
        }

        // Nastavíme uživatele jako offline v databázi
        public async Task SetUserOffline()
        {
            var userId = _currentUser?.Uid;
            if (!string.IsNullOrEmpty(userId))
            {
                await _firestoreDb.Collection("users").Document(userId)
                    .UpdateAsync(new Dictionary<string, object>
                    {
                { "isOnline", false },
                { "lastLoginAt", Timestamp.FromDateTime(DateTime.UtcNow) } // Uloží poslední aktivitu
                    });
            }
        }

        // Spustíme posluchač změn aktivních relací
        private void StartActiveSessionListener(string userId)
        {
            try
            {
                // Zrušíme předchozí listener pokud existuje
                _activeSessionListener?.StopAsync();

                // Vytvoříme referenci na dokument session
                var sessionQuery = _firestoreDb.Collection("active_sessions")
                    .WhereEqualTo("userId", userId);

                // Nastavíme nový listener
                _activeSessionListener = sessionQuery.Listen(snapshot =>
                {
                    try
                    {
                        if (snapshot == null || snapshot.Count == 0)
                        {
                            _logger.LogInformation($"Žádná aktivní session pro uživatele {userId}");
                            return;
                        }

                        var sessionDoc = snapshot.Documents[0];

                        // Kontrola, zda dokument obsahuje potřebná data
                        if (!sessionDoc.TryGetValue<string>("deviceId", out string activeDeviceId))
                        {
                            _logger.LogWarning($"Session dokument neobsahuje pole deviceId");
                            return;
                        }

                        // Pokud se změnilo aktivní zařízení, odhlásit uživatele
                        if (!string.IsNullOrEmpty(activeDeviceId) && activeDeviceId != _deviceId)
                        {
                            _logger.LogInformation($"Detekována změna aktivního zařízení v reálném čase");

                            //  Provedeme nucené odhlášení na hlavním vlákně
                            MainThread.BeginInvokeOnMainThread(async () => {
                                await ForcedLogoutAsync();
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"Chyba při zpracování změny session: {ex.Message}");
                    }
                });

                _logger.LogInformation($"Zahájen monitoring aktivní session pro uživatele {userId}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Chyba při inicializaci listeneru: {ex.Message}");
            }
        }

        // Zastavíme posluchač změn aktivních relací
        private void StopActiveSessionListener()
        {
            _activeSessionListener?.StopAsync();
            _activeSessionListener = null;
        }

        // Získáme ID zařízení pro identifikaci relace
        private string GetDeviceId()
        {
            try
            {
#if WINDOWS
        // Pro Windows zařízení použijeme název počítače
        return Environment.MachineName ?? Guid.NewGuid().ToString();
#elif ANDROID
        // Pro Android použijeme Android ID
        var androidId = Android.Provider.Settings.Secure.GetString(
            Android.App.Application.Context.ContentResolver,
            Android.Provider.Settings.Secure.AndroidId);
        return string.IsNullOrEmpty(androidId) ? Guid.NewGuid().ToString() : androidId;
#else
                // Fallback pro jiné platformy
                return Guid.NewGuid().ToString();
#endif
            }
            catch (Exception ex)
            {
                _logger.LogError($"Chyba při získávání ID zařízení: {ex.Message}");

                // Vytvoříme náhodné ID jako fallback
                return Guid.NewGuid().ToString();
            }
        }

        // Získáme cestu k souboru z URL pro mazání obrázků
        private static string GetPathFromUrl(string url)
        {
            try
            {
                // URL bude ve formátu podobném:
                // https://firebasestorage.googleapis.com/v0/b/BUCKET/o/profile-images%2FuserID%2Ffilename.jpg
                var uri = new Uri(url);
                var segments = uri.Segments;

                // Hledáme část za "o/" v URL
                var startIndex = Array.IndexOf(segments, "o/");
                if (startIndex == -1) return string.Empty;

                // Spojíme všechny segmenty po "o/"
                var path = string.Join("", segments.Skip(startIndex + 1));

                // Odstraníme případné query parametry
                var queryIndex = path.IndexOf('?');
                if (queryIndex != -1)
                {
                    path = path.Substring(0, queryIndex);
                }

                // Dekódujeme URL-encoded znaky
                return Uri.UnescapeDataString(path);
            }
            catch
            {
                return string.Empty;
            }
        }

        // Pomocná metoda pro ověření existence a platnosti souboru s Firebase credentials
        private static void ValidateCredentialsFile(string credentialsPath)
        {
            // Kontrola existence souboru
            if (!File.Exists(credentialsPath))
            {
                throw new InvalidOperationException(
                    "FIREBASE CREDENTIALS SOUBOR NENALEZEN! \n\n" +
                    $"Soubor neexistuje: {credentialsPath}\n\n" +
                    "Pro spuštění aplikace:\n" +
                    "1. Vytvořte Firebase projekt na console.firebase.google.com\n" +
                    "2. Aktivujte Firestore Database\n" +
                    "3. Stáhněte firebase-credentials.json z Project Settings → Service Accounts\n" +
                    "4. Umístěte soubor do Firebase_config/firebase-credentials.json\n\n" +
                    "Pro hodnotitele bakalářské práce: Kontaktujte autora pro testovací credentials nebo si je nahraďte vlastními"
                );
            }

            // Kontrola, zda credentials soubor neobsahuje placeholder texty místo skutečných údajů
            try
            {
                string credentialsContent = File.ReadAllText(credentialsPath);

                if (credentialsContent.Contains("nahraďte-") ||
                    credentialsContent.Contains("váš-projekt") ||
                    credentialsContent.Contains("vlastním-"))
                {
                    throw new InvalidOperationException(
                        "FIREBASE NENÍ NAKONFIGUROVÁN! \n\n" +
                        "Credentials soubor obsahuje placeholder hodnoty.\n\n" +
                        "Pro spuštění aplikace:\n" +
                        "1. Vytvořte Firebase projekt na console.firebase.google.com\n" +
                        "2. Aktivujte Firestore Database\n" +
                        "3. Stáhněte firebase-credentials.json z Project Settings → Service Accounts\n" +
                        "4. Nahraďte placeholder hodnoty reálnými údaji z Firebase\n" +
                        "5. Také nahraďte hodnoty v resources/raw/appsettings.json\n\n" +
                        "Pro hodnotitele bakalářské práce: Kontaktujte autora pro testovací credentials nebo si je nahraďte vlastními"
                    );
                }
            }
            catch (InvalidOperationException)
            {
               
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Nepodařilo se přečíst credentials soubor: {credentialsPath}", ex);
            }
        }

        // Validace, že konfigurace Firebase neobsahuje placeholder hodnoty (v appsettings.json)
        private void ValidateFirebaseConfig()
        {
            if (_firebaseConfig.ProjectId.Contains("nahraďte-") ||
                _firebaseConfig.ApiKey.Contains("nahraďte-") ||
                _firebaseConfig.AuthDomain.Contains("váš-projekt"))
            {
                throw new InvalidOperationException(
                    "FIREBASE KONFIGURACE NENÍ NASTAVENA! \n\n" +
                    "Soubor appsettings.json obsahuje placeholder hodnoty.\n\n" +
                    "Nahraďte placeholder hodnoty v resources/raw/appsettings.json reálnými údaji z Firebase projektu."
                );
            }
        }

    }
}


