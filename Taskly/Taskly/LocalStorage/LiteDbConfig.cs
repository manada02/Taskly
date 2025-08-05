// LiteDbConfig.cs - Konfigurace a správa lokální LiteDB databáze pro offline ukládání dat
using LiteDB;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Threading;
using Taskly.Models;

namespace Taskly.LocalStorage
{
    // Třída pro konfiguraci a přístup k LiteDB lokální databázi
    public class LiteDbConfig
    {
        // PROMĚNNÉ A ZÁVISLOSTI

        // Logger pro diagnostiku a sledování operací s databází
        private readonly ILogger<LiteDbConfig> _logger;

        // Cesta k souboru databáze v adresáři aplikace
        public static string DatabasePath => Path.Combine(FileSystem.AppDataDirectory, "taskly.db");

        // Singleton instance databáze - stejná instance je sdílena mezi všemi klienty
        private static LiteDatabase? _instance;

        // Zámek pro thread-safe přístup k instanci databáze
        private static readonly object _lockObject = new object();

        // Konstanty pro opakování pokusů při selhání přístupu k databázi
        private const int MaxRetries = 3;
        private const int RetryDelayMs = 200;

        // KONSTRUKTOR

        // Inicializuje konfiguraci a zajistí existenci adresáře pro databázi
        public LiteDbConfig(ILogger<LiteDbConfig> logger)
        {
            _logger = logger;
            _logger.LogInformation("LiteDbConfig: Inicializuji s cestou {DbPath}", DatabasePath);

            // Kontrola a vytvoření adresáře, pokud neexistuje
            var directory = Path.GetDirectoryName(DatabasePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                _logger.LogInformation("LiteDbConfig: Vytvořen adresář {Directory}", directory);
            }
        }

        // SPRÁVA DATABÁZE

        // Získá instanci databáze - pokud neexistuje, vytvoří novou s opakovanými pokusy
        public LiteDatabase GetDatabase()
        {
            if (_instance != null) return _instance; // Rychlý návrat, pokud instance existuje

            lock (_lockObject)
            {
                if (_instance != null) return _instance; // Dvojitá kontrola pro thread safety

                int attempt = 0;
                Exception? lastException = null;

                while (attempt < MaxRetries)
                {
                    attempt++;
                    try
                    {
                        var connectionString = $"Filename={DatabasePath};Connection=shared";
                        _instance = new LiteDatabase(connectionString);
                        _logger.LogInformation("LiteDbConfig: Vytvořena nová sdílená instance LiteDatabase (Pokus {Attempt})", attempt);

                        // Inicializace indexů v databázi
                        InitializeDatabaseIndexes(_instance);

                        break; // Úspěch - ukončíme cyklus pokusů
                    }
                    catch (IOException ex)
                    {
                        lastException = ex;
                        _logger.LogWarning(ex, "IO výjimka při pokusu {Attempt} o otevření databáze, zkusím znovu za {Delay}ms", attempt, RetryDelayMs);
                        Thread.Sleep(RetryDelayMs * attempt); // Exponenciální čekání mezi pokusy
                    }
                    catch (LiteException ex)
                    {
                        lastException = ex;
                        _logger.LogWarning(ex, "LiteDB výjimka při pokusu {Attempt} o otevření databáze, zkusím znovu za {Delay}ms", attempt, RetryDelayMs);
                        Thread.Sleep(RetryDelayMs * attempt);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Neočekávaná chyba při otevírání databáze");
                        throw; // Při neočekávané chybě okamžitě selháme
                    }
                }

                // Pokud se nepodařilo otevřít databázi ani po MaxRetries pokusech
                if (_instance == null)
                {
                    _logger.LogError(lastException, "Nepodařilo se otevřít databázi po {MaxRetries} pokusech", MaxRetries);
                    throw lastException ?? new InvalidOperationException($"Nepodařilo se otevřít databázi: {DatabasePath}");
                }
            }
            return _instance;
        }

        // Uzavřeme databázi a uvolníme prostředky - volá se při ukončení aplikace
        public void CloseDatabase()
        {
            lock (_lockObject)
            {
                if (_instance != null)
                {
                    _logger.LogWarning("Explicitní uzavírání sdílené instance LiteDatabase...");
                    try
                    {
                        _instance.Checkpoint(); // Zajistíme, že všechna data jsou zapsána na disku
                        _instance.Dispose(); // Uvolníme prostředky
                        _logger.LogInformation("LiteDbConfig: Instance databáze úspěšně Dispose() voláno.");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Chyba při Dispose() instance databáze, instance bude přesto nastavena na null.");
                    }
                    finally
                    {
                        // Spolehlivé nastavení na null pro zajištění opětovné inicializace
                        _instance = null;
                        _logger.LogInformation("LiteDbConfig: Singleton instance nastavena na null.");
                    }
                }
                else
                {
                    _logger.LogInformation("LiteDbConfig: Instance databáze již byla null, není co zavírat.");
                }
            }
        }

        // Smažeme soubor databáze - používáme  pro úplný reset dat
        public bool WipeDatabaseFile()
        {
            _logger.LogWarning("Pokus o smazání souboru databáze: {DatabasePath}", DatabasePath);

            // Nejprve uzavřeme a zrušíme instanci
            CloseDatabase();

            // Poté smažeme soubor
            try
            {
                if (File.Exists(DatabasePath))
                {
                    File.Delete(DatabasePath);
                    _logger.LogInformation("Soubor databáze {DatabasePath} úspěšně smazán.", DatabasePath);

                    // Smazání také WAL souboru - write-ahead log používaný pro transakce
                    string walPath = DatabasePath + "-wal";
                    if (File.Exists(walPath))
                    {
                        try
                        {
                            File.Delete(walPath);
                            _logger.LogInformation("Soubor WAL {WalPath} úspěšně smazán.", walPath);
                        }
                        catch (Exception walEx)
                        {
                            _logger.LogWarning(walEx, "Nepodařilo se smazat soubor WAL {WalPath}.", walPath);
                        }
                    }
                    return true;
                }
                else
                {
                    _logger.LogInformation("Soubor databáze {DatabasePath} neexistoval, není co mazat.", DatabasePath);
                    return true;
                }
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "IO chyba při mazání souboru databáze {DatabasePath}.", DatabasePath);
                return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogError(ex, "Chyba oprávnění při mazání souboru databáze {DatabasePath}.", DatabasePath);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Obecná chyba při mazání souboru databáze {DatabasePath}.", DatabasePath);
                return false;
            }
        }

        // INICIALIZACE STRUKTUR

        // Inicializuje indexy pro kolekce v databázi - zrychlí vyhledávání
        private void InitializeDatabaseIndexes(LiteDatabase db)
        {
            try
            {
                // Indexy pro kolekci úkolů
                var tasksCollection = db.GetCollection<TaskItem>("tasks");
                tasksCollection.EnsureIndex(t => t.Id, unique: true); // Primární index
                tasksCollection.EnsureIndex(t => t.UserId); // Pro filtrování podle uživatele
                tasksCollection.EnsureIndex(t => t.Status); // Pro filtrování podle stavu
                tasksCollection.EnsureIndex(t => t.ProjectId); // Pro filtrování podle projektu

                // Indexy pro kolekci projektů
                var projectsCollection = db.GetCollection<ProjectItem>("projects");
                projectsCollection.EnsureIndex(p => p.Id, unique: true);  // Primární index 
                projectsCollection.EnsureIndex(p => p.UserId);  // Pro filtrování podle uživatele

                _logger.LogInformation("LiteDbConfig: Indexy pro kolekce zajištěny/aktualizovány.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při zajišťování indexů databáze");
            }
        }
    }
}
