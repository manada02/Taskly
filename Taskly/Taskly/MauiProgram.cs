// MauiProgram.cs - Konfigurační soubor aplikace Taskly pro MAUI
using Microsoft.Extensions.Logging;
using MudBlazor.Services;
using Taskly.Firebase_config;
using Taskly.Services.Auth;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using MudBlazor;
using Firebase.Auth;
using Firebase.Auth.Providers;
using Taskly.Services.Cache;
using Taskly.Services.Notification;
using Taskly.Services.Connectivity;
using Taskly.Services.Tasks;
using Taskly.Services.Projects;
using Taskly.Services.UI;
using Taskly.Components.Core;
using Taskly.Services.Notification.LocalNotification;
using Plugin.LocalNotification.AndroidOption;
using Plugin.LocalNotification;
using Microsoft.AspNetCore.Components.WebView.Maui;

namespace Taskly
{
    // Vstupní bod aplikace a konfigurace služeb
    public static class MauiProgram
    {
        // INICIALIZACE APLIKACE

        // Vytvoříme a nakonfigurujeme aplikaci Taskly jako MAUI aplikaci
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()

                // KONFIGURACE SYSTÉMOVÝCH NOTIFIKACÍ
#if ANDROID
                // Konfigurace lokálních notifikací pro Android
                .UseLocalNotification(config =>
                {
                    config.AddAndroid(androidConfig =>
                    {
                        androidConfig.AddChannel(new NotificationChannelRequest
                        {
                            Id = "taskly_reminders",
                            Name = "Připomínky Taskly",
                            Importance = AndroidImportance.High,
                            Description = "Notifikace pro připomínky v aplikaci Taskly" ,
            
                        });
                    });
                })
#endif
                // Registrace fontů pro všechny platformy
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                });

            // NASTAVENÍ WEBVIEW PODLE PLATFORMY

#if ANDROID
            Microsoft.Maui.Handlers.WebViewHandler.Mapper.AppendToMapping("SetBackground", (handler, view) =>
            {
                handler.PlatformView.SetBackgroundColor(Android.Graphics.Color.Transparent);
            });

            BlazorWebViewHandler.BlazorWebViewMapper.AppendToMapping("SetBackground", (handler, view) =>
            {
                handler.PlatformView.SetBackgroundColor(Android.Graphics.Color.Transparent);
            });
#endif

#if WINDOWS
            // Nastavení pozadí pro WebView2 na Windows
            Microsoft.Maui.Handlers.WebViewHandler.Mapper.AppendToMapping("SetBackground", (handler, view) =>
            {
                if (handler.PlatformView is Microsoft.UI.Xaml.Controls.WebView2 webView2)
                {
                    webView2.DefaultBackgroundColor = Windows.UI.Color.FromArgb(255, 81, 43, 212); // #512BD4
                }
            });

            // Nastavení pozadí i pro BlazorWebView (ten používá WebView2 uvnitř)
            BlazorWebViewHandler.BlazorWebViewMapper.AppendToMapping("SetBackground", (handler, view) =>
            {
                if (handler.PlatformView is Microsoft.UI.Xaml.Controls.WebView2 webView2)
                {
                    webView2.DefaultBackgroundColor = Windows.UI.Color.FromArgb(255, 81, 43, 212); // #512BD4
                }
            });
#endif



            // NAČTENÍ KONFIGURACE

            // Načtení konfigurace ze souboru appsettings.json
            using var stream = FileSystem.OpenAppPackageFileAsync("appsettings.json").Result;
            using var reader = new StreamReader(stream);
            var config = new ConfigurationBuilder()
                .AddJsonStream(stream)
                .Build();

            // Vytvoření instance konfigurace Firebase
            var firebaseConfig = new TasklyFirebaseConfig();
            config.GetSection("Firebase").Bind(firebaseConfig);

            // Konfigurace Firebase autentizace
            builder.Services.AddSingleton<FirebaseAuthClient>(sp => {
                var config = new FirebaseAuthConfig
                {
                    ApiKey = firebaseConfig.ApiKey,
                    AuthDomain = firebaseConfig.AuthDomain,
                    Providers = [new EmailProvider()]
                };

                return new FirebaseAuthClient(config);
            });

            // REGISTRACE MAUI SLUŽEB

            // Přidáním Blazor WebView (umožňujeme použití Blazor v MAUI)
            builder.Services.AddMauiBlazorWebView();

            // Registrace knihovny MudBlazor pro UI komponenty
            builder.Services.AddMudServices(config =>
            {
                config.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.TopRight;
                config.SnackbarConfiguration.ShowCloseIcon = true;
                // Toto je výchozí hodnota, která bude přepsána v NotificationService
                config.SnackbarConfiguration.VisibleStateDuration = 4000;
                config.SnackbarConfiguration.MaxDisplayedSnackbars = 3;
            });

            // REGISTRACE APLIKAČNÍCH SLUŽEB

            // Registrace konfiguračních služeb
            builder.Services.AddSingleton(firebaseConfig);

            // Registrace autentizačních služeb
            builder.Services.AddSingleton<IAuthService, FirebaseAuthService>();  // Služba pro autentizaci, existuje po celou dobu běhu aplikace
            builder.Services.AddSingleton<FirebaseAuthService>();

            // Registrace služeb pro data
            builder.Services.AddSingleton<LocalStorage.LiteDbConfig>();  // Lokální databáze
            builder.Services.AddSingleton<ICacheService, CacheService>();  // Služba pro cachování dat

            // Registrace služeb stavu
            builder.Services.AddSingleton<ConnectivityService>();  // Služba pro sledování stavu připojení
            builder.Services.AddSingleton<IThemeService, ThemeService>();  // Služba pro správu témat

            // Registrace komponent
            builder.Services.AddSingleton<App>();
            builder.Services.AddSingleton<MainPage>();
            builder.Services.AddSingleton<DashboardInitializer>();  // Inicializace dashboardu

            // Registrace služeb pro notifikace
            builder.Services.AddScoped<Taskly.Services.Notification.INotificationService, Taskly.Services.Notification.NotificationService>();
            builder.Services.AddScoped<FirestoreNotificationService>();
            builder.Services.AddSingleton<ILocalNotificationSchedulerService, LocalNotificationSchedulerService>();

            // Registrace služeb pro správu úkolů
            builder.Services.AddScoped<FirestoreTaskService>();
            builder.Services.AddScoped<ITaskService, TaskService>();
            builder.Services.AddScoped<TaskReminderService>();

            // Registrace služeb pro správu projektů
            builder.Services.AddScoped<FirestoreProjectService>();
            builder.Services.AddScoped<IProjectService, ProjectService>();

            // PLATFORMNĚ SPECIFICKÁ SLUŽBA PRO SYSTÉMOVÉ NOTIFIKACE

#if ANDROID
            // Registrace služby pro lokální notifikace na Androidu
            builder.Services.AddSingleton<Plugin.LocalNotification.INotificationService>(LocalNotificationCenter.Current);
#endif

            // VÝVOJOVÉ NÁSTROJE

#if DEBUG
           
            builder.Services.AddBlazorWebViewDeveloperTools();
            builder.Logging.AddDebug();
#endif
            return builder.Build();
        }
    }
}
