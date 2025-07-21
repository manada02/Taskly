// App.xaml.cs (WinUI) - Windows specifická implementace aplikace s podporou single instance a notifikací
using Microsoft.UI.Xaml;
using Microsoft.Maui.Controls;
using Microsoft.Extensions.DependencyInjection;
using System.Threading;
using System.IO.Pipes;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.Toolkit.Uwp.Notifications;

namespace Taskly.WinUI
{
    // Windows specifická implementace hlavní třídy aplikace
    public partial class App : MauiWinUIApplication
    {
        // PROMĚNNÉ A ZÁVISLOSTI

        // Poskytovatel služeb pro přístup k DI kontejneru
        public static IServiceProvider ServiceProvider { get; private set; } = null!;

        // Název pojmenované roury pro meziprocesovou komunikaci
        private const string PipeName = "TasklySingleInstancePipe";

        // Mutex pro zajištění běhu pouze jedné instance aplikace
        private static Mutex? _mutex;

        // Fronta dispatcheru pro UI operace
        private DispatcherQueue _dispatcherQueue;

        // INICIALIZACE

        // Konstruktor aplikace - inicializuje WinUI komponenty a registruje Microsoft Toolkit notifikace
        public App()
        {
            this.InitializeComponent();
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

            // Registrace Microsoft Toolkit notification handleru pro zpracování kliknutí na notifikace
            try
            {
                ToastNotificationManagerCompat.OnActivated += ToastNotificationActivated;
                System.Diagnostics.Debug.WriteLine("[Windows] Microsoft Toolkit notification handler registrován");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Windows] Chyba při registraci notification handleru: {ex.Message}");
            }
        }

        // Vytváříme a konfigurujeme hlavní MAUI aplikaci
        protected override MauiApp CreateMauiApp()
        {
            System.Diagnostics.Debug.WriteLine("[Windows] CreateMauiApp: Začátek");

            // Kontrola, zda již neběží jiná instance aplikace pomocí mutexu
            bool createdNew;
            try
            {
                _mutex = new Mutex(true, "TasklyMutex", out createdNew);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Windows] Chyba při vytváření Mutexu: {ex.Message}");
                createdNew = false;
            }

            // Pokud již existuje instance, pošleme jí signál k aktivaci a ukončíme tuto
            if (!createdNew)
            {
                SendActivationToExistingInstance();
                Environment.Exit(0);
            }

            // Spustíme naslouchání pro případné budoucí instance
            Task.Run(() => ListenForOtherInstances());

            // Vytvoříme MAUI aplikaci a uložíme si poskytovatele služeb
            var mauiApp = MauiProgram.CreateMauiApp();
            ServiceProvider = mauiApp.Services;
            System.Diagnostics.Debug.WriteLine("[Windows] CreateMauiApp: MauiApp vytvořen");
            System.Diagnostics.Debug.WriteLine("[Windows] CreateMauiApp: Dokončeno");
            return mauiApp;
        }

        // OBSLUHA AKTIVACE APLIKACE

        // Metoda volaná při spuštění aplikace Windows
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            base.OnLaunched(args);
            System.Diagnostics.Debug.WriteLine($"[Windows] OnLaunched volán s argumenty: {args.Arguments}");
            HandleActivation(args.Arguments);
        }

        // Zpracovává různé scénáře aktivace aplikace
        private void HandleActivation(string arguments)
        {
            // Běžné spuštění nebo URL protokol - notifikace se zpracovávají v ToastNotificationActivated
            if (string.IsNullOrEmpty(arguments) || arguments.Contains("taskly://"))
            {
                ActivateWindow();
            }
            else if (arguments.Contains("-ToastActivated"))
            {
                // Aktivace z toast notifikace přes COM - aplikace se již aktivuje v ToastNotificationActivated
                System.Diagnostics.Debug.WriteLine("[Windows] Aplikace aktivována toast notifikací");
                ActivateWindow();
            }
            else
            {
                // Jiné typy aktivace - zatím pouze aktivujeme okno
                System.Diagnostics.Debug.WriteLine($"[Windows] Neznámý typ aktivace: {arguments}");
                ActivateWindow();
            }
        }

        // Obsluha události kliknutí na Windows toast notifikaci pomocí Microsoft Toolkit
        private void ToastNotificationActivated(ToastNotificationActivatedEventArgsCompat args)
        {
            System.Diagnostics.Debug.WriteLine("[Windows] Toast notifikace kliknuta - otevírám aplikaci");

            _dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    // Jen aktivuj aplikaci - žádná navigace, žádný messaging
                    ActivateWindow();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Windows] Chyba při aktivaci aplikace: {ex.Message}");
                }
            });
        }

        // SPRÁVA OKNA APLIKACE

        // Aktivuje existující nebo vytvoří nové okno aplikace
        private void ActivateWindow()
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    var mauiWindow = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault();
                    if (mauiWindow != null)
                    {
                        var nativeWindow = mauiWindow.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
                        if (nativeWindow != null)
                        {
                            var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(nativeWindow);
                            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(windowHandle);
                            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

                            // Přidána kontrola null pro appWindow
                            if (appWindow != null)
                            {
                                appWindow.Show();
                                // Přenese okno do popředí pro lepší UX po kliknutí na toast notifikaci
                                nativeWindow.Activate();
                                System.Diagnostics.Debug.WriteLine("[Windows] Existující instance aktivována");
                            }
                            else
                            {
                                System.Diagnostics.Debug.WriteLine("[Windows] Nepodařilo se získat AppWindow");
                            }
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine("[Windows] Není dostupné nativní okno");
                        }
                    }
                    else
                    {
                        // Vytvoření nového okna pomocí existující aplikace
                        // Přidána kontrola null pro Application.Current
                        if (Microsoft.Maui.Controls.Application.Current != null)
                        {
                            var mainPage = ServiceProvider.GetService<MainPage>() ?? new MainPage();
                            var newWindow = new Microsoft.Maui.Controls.Window(mainPage);
                            Microsoft.Maui.Controls.Application.Current.OpenWindow(newWindow);
                            var nativeWindow = newWindow.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
                            nativeWindow?.Activate();
                            System.Diagnostics.Debug.WriteLine("[Windows] Nové okno otevřeno");
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine("[Windows] Application.Current není dostupné");
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Windows] Chyba při aktivaci okna: {ex.Message}");
                }
            });
        }

        // MEZIPROCESOVÁ KOMUNIKACE

        // Posílá signál existující instanci aplikace
        private void SendActivationToExistingInstance()
        {
            try
            {
                // Navážeme komunikaci s již běžící instancí aplikace pro její aktivaci
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(1000); // Timeout 1000ms
                var message = Encoding.UTF8.GetBytes("ACTIVATE");
                client.Write(message, 0, message.Length);
                System.Diagnostics.Debug.WriteLine("[Windows] Zpráva o aktivaci odeslána existující instanci");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Windows] Chyba při odesílání zprávy: {ex.Message}");
            }
        }

        // Naslouchá signálům od nových instancí aplikace
        private void ListenForOtherInstances()
        {
            while (true)
            {
                try
                {
                    // Čekáme na zprávy od dalších instancí aplikace, které se pokusí spustit
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In);
                    server.WaitForConnection();

                    // Čteme a zpracováváme příchozí zprávu
                    var buffer = new byte[1024];
                    var bytesRead = server.Read(buffer, 0, buffer.Length);
                    var message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    if (message == "ACTIVATE")
                    {
                        ActivateWindow();
                    }
                    server.Close();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Windows] Chyba v pipe serveru: {ex.Message}");
                }
            }
        }
    }
}