// ThemeService.cs - Služba pro správu vzhledu aplikace
using MudBlazor;
using MudBlazor.Utilities;
using Microsoft.Extensions.Logging;

namespace Taskly.Services.UI
{
    // ROZHRANÍ PRO SLUŽBU VZHLEDU
    public interface IThemeService
    {
        // Vlastnosti pro přístup k aktuálnímu nastavení
        bool IsDarkMode { get; }          
        string PrimaryColor { get; }      
        string Density { get; }           
        MudTheme CurrentTheme { get; }     // Aktuální MudBlazor téma

        // Události pro informování o změnách nastavení
        event Action<bool> DarkModeChanged;       // Změna tmavého režimu
        event Action<string> PrimaryColorChanged; // Změna primární barvy
        event Action<string> DensityChanged;      // Změna hustoty UI

        // Metody pro manipulaci s nastavením
        Task ToggleDarkModeAsync();                 // Přepnutí tmavého režimu
        Task SetDarkModeAsync(bool isDarkMode);     // Nastavení tmavého režimu
        Task SetPrimaryColorAsync(string colorName); // Změna primární barvy
        Task SetDensityAsync(string density);       // Změna hustoty UI
        Task InitializeAsync();                     // Inicializace služby
    }

    public class ThemeService : IThemeService
    {
        // PROMĚNNÉ A VLASTNOSTI
        private readonly ILogger<ThemeService> _logger;
        private bool _isDarkMode;
        private string _primaryColor = "primary";
        private string _density = "default"; 
        private bool _isInitialized = false;

        // Klíče pro Preferences API
        private const string DARK_MODE_KEY = "theme_dark_mode";
        private const string PRIMARY_COLOR_KEY = "theme_primary_color";
        private const string DENSITY_KEY = "theme_density";

        // Veřejné vlastnosti pro přístup k nastavení
        public bool IsDarkMode => _isDarkMode;
        public string PrimaryColor => _primaryColor;
        public string Density => _density;
        public MudTheme CurrentTheme { get; private set; }

        // Události pro informování o změnách nastavení
        public event Action<bool> DarkModeChanged = delegate { };
        public event Action<string> PrimaryColorChanged = delegate { };
        public event Action<string> DensityChanged = delegate { };

        // Mapování názvů barev na MudBlazor barevné objekty
        private readonly Dictionary<string, MudColor> _colorMap = new()
        {
            { "primary", new MudColor("#594AE2") }, // originální fialová
            { "indigo", new MudColor("#3F51B5") },
            { "purple", new MudColor("#9C27B0") },
            { "teal", new MudColor("#009688") },
            { "orange", new MudColor("#FF9800") }
        };

        // KONSTRUKTOR
        public ThemeService(ILogger<ThemeService> logger)
        {
            _logger = logger;
            CurrentTheme = new MudTheme();
            _logger.LogInformation("ThemeService vytvořen");
        }

        // VEŘEJNÉ METODY
        // Inicializace služby a načtení nastavení
        public Task InitializeAsync()
        {
            if (_isInitialized)
            {
                _logger.LogInformation("ThemeService již inicializován");
                return Task.CompletedTask;
            }

            try
            {
                _logger.LogInformation("Inicializace ThemeService z Preferences");

                // Načtení hodnot z Preferences API
                _isDarkMode = Preferences.Default.Get(DARK_MODE_KEY, false);
                _primaryColor = Preferences.Default.Get(PRIMARY_COLOR_KEY, "primary");
                _density = Preferences.Default.Get(DENSITY_KEY, "default");

                _logger.LogInformation($"Načteno z Preferences: darkMode={_isDarkMode}, primaryColor={_primaryColor}, density={_density}");

                // Aktualizovat téma podle načtených hodnot
                UpdateTheme();
                _isInitialized = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při inicializaci ThemeService");
                // výchozí hodnoty
                _isDarkMode = false;
                _primaryColor = "primary";
                _density = "default";
                UpdateTheme();
            }

            return Task.CompletedTask;
        }

        // Přepnutí mezi světlým a tmavým režimem
        public Task ToggleDarkModeAsync()
        {
            return SetDarkModeAsync(!_isDarkMode);
        }

        // Nastavení tmavého/světlého režimu
        public Task SetDarkModeAsync(bool isDarkMode)
        {
            _logger.LogInformation($"ThemeService.SetDarkModeAsync volána s hodnotou: {isDarkMode}");

            if (_isDarkMode == isDarkMode)
            {
                _logger.LogInformation("Hodnota je stejná, žádná změna");
                return Task.CompletedTask;
            }

            _isDarkMode = isDarkMode;

            // Uložení do Preferences API
            try
            {
                Preferences.Default.Set(DARK_MODE_KEY, _isDarkMode);
                _logger.LogInformation($"Uloženo do Preferences: darkMode={_isDarkMode}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při ukládání nastavení tmavého režimu");
            }

            UpdateTheme();

            // Informujeme o změně tmavého režimu
            _logger.LogInformation($"Informujeme o změně tmavého režimu: {_isDarkMode}");
            DarkModeChanged?.Invoke(_isDarkMode);

            return Task.CompletedTask;
        }

        // Nastavení primární barvy aplikace
        public Task SetPrimaryColorAsync(string colorName)
        {
            if (_primaryColor == colorName) return Task.CompletedTask;

            if (!_colorMap.ContainsKey(colorName))
                colorName = "primary";

            _logger.LogInformation($"Nastavuji primární barvu: {colorName}");
            _primaryColor = colorName;

            // Uložíme do Preferences API
            try
            {
                Preferences.Default.Set(PRIMARY_COLOR_KEY, _primaryColor);
                _logger.LogInformation($"Uloženo do Preferences: primaryColor={_primaryColor}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při ukládání nastavení primární barvy");
            }

            UpdateTheme();

            // Informujeme o změně primární barvy
            PrimaryColorChanged?.Invoke(_primaryColor);

            return Task.CompletedTask;
        }

        // Nastavení hustoty uživatelského rozhraní
        public Task SetDensityAsync(string density)
        {
            if (_density == density) return Task.CompletedTask;

            _logger.LogInformation($"Nastavuji hustotu: {density}");
            _density = density;

            // Uložení do Preferences API
            try
            {
                Preferences.Default.Set(DENSITY_KEY, _density);
                _logger.LogInformation($"Uloženo do Preferences: density={_density}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chyba při ukládání nastavení hustoty");
            }

            // Aktualizace tématu
            UpdateTheme();

            // Informujeme o změně hustoty
            DensityChanged?.Invoke(_density);

            return Task.CompletedTask;
        }

        // POMOCNÉ METODY
        // Aktualizace tématu podle aktuálního nastavení
        private void UpdateTheme()
        {
            // Vytvoření nového tématu
            var theme = new MudTheme();

            // Nastavení primární barvy
            if (_colorMap.TryGetValue(_primaryColor, out var primaryColor))
            {
                theme.PaletteLight.Primary = primaryColor;
                theme.PaletteDark.Primary = primaryColor;
                _logger.LogInformation($"Nastavena primární barva tématu: {_primaryColor}");
            }

            // Nastavení pro tmavý režim
            if (_isDarkMode)
            {
                // Tyto barvy jsou kritické pro správné fungování tmavého režimu
                theme.PaletteDark.Background = "#2c2c38";         // Pozadí stránky
                theme.PaletteDark.Surface = "#373740";            // Pozadí karet a komponent
                theme.PaletteDark.DrawerBackground = "#27272f"; // Pozadí bočního menu
                theme.PaletteDark.AppbarBackground = "#27272f"; // Pozadí horní lišty

                // Tyto barvy ovlivňují text a další prvky
                theme.PaletteDark.TextPrimary = "rgba(255,255,255, 0.70)";
                theme.PaletteDark.TextSecondary = "rgba(255,255,255, 0.50)";
                theme.PaletteDark.ActionDefault = "#adadb1";

                // Pozadí pro MudPaper (odlišné od hlavního pozadí)
                theme.PaletteDark.BackgroundGray = "#27272f";

                // Další barvy pro úplný tmavý režim
                theme.PaletteDark.DrawerText = "rgba(255,255,255, 0.75)";
                theme.PaletteDark.AppbarText = "rgba(255,255,255, 0.70)";
                theme.PaletteDark.ActionDisabled = "rgba(255,255,255, 0.26)";
                theme.PaletteDark.ActionDisabledBackground = "rgba(255,255,255, 0.12)";
            }
            else
            {
                theme.PaletteLight.Background = "#f5f5f5";
                _logger.LogInformation("Nastaveno světlé téma");
            }

            // Nastavení hustoty
            switch (_density)
            {
                case "comfortable":
                    theme.LayoutProperties.AppbarHeight = "70px"; // Vyšší AppBar pro pohodlné rozestupy
                    theme.LayoutProperties.DrawerWidthLeft = "280px"; // Širší Drawer pro pohodlný přístup
                    theme.LayoutProperties.DrawerWidthRight = "280px"; 
                    theme.LayoutProperties.DrawerMiniWidthLeft = "220px"; // Užší Mini Drawer
                    theme.LayoutProperties.DrawerMiniWidthRight = "220px"; 
                    break;

                case "compact":
                    theme.LayoutProperties.AppbarHeight = "48px"; // Menší AppBar pro kompaktní režim
                    theme.LayoutProperties.DrawerWidthLeft = "200px"; // Užší Drawer pro kompaktní rozestupy
                    theme.LayoutProperties.DrawerWidthRight = "200px"; 
                    theme.LayoutProperties.DrawerMiniWidthLeft = "180px"; // Užší Mini Drawer
                    theme.LayoutProperties.DrawerMiniWidthRight = "180px"; 
                    break;

                default:
                    theme.LayoutProperties.AppbarHeight = "70px"; // Standardní AppBar
                    theme.LayoutProperties.DrawerWidthLeft = "250px"; // Standardní Drawer
                    theme.LayoutProperties.DrawerWidthRight = "250px"; 
                    theme.LayoutProperties.DrawerMiniWidthLeft = "200px"; // Standardní Mini Drawer
                    theme.LayoutProperties.DrawerMiniWidthRight = "200px"; 
                    break;
            }

            CurrentTheme = theme;
        }
    }
}




