// ConnectivityService.cs - Služba pro monitorování stavu připojení k internetu
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Maui.Networking; 
using Taskly.Services.Connectivity;

namespace Taskly.Services.Connectivity
{
    public class ConnectivityService : IDisposable
    {
        // PROMĚNNÉ A ZÁVISLOSTI
        private readonly ILogger<ConnectivityService> _logger;
        private bool _lastKnownState;
        private Timer? _connectivityCheckTimer;

        // UDÁLOSTI A VLASTNOSTI
        // Událost pro oznámení změny připojení
        public event Action<bool>? ConnectivityChanged;

        // Property pro přímý přístup ke stavu připojení
        public bool IsConnected => IsOnline();

        // KONSTRUKTOR
        public ConnectivityService(ILogger<ConnectivityService> logger)
        {
            _logger = logger;
            _lastKnownState = IsOnline();

            // Spuštění timeru pro kontrolu připojení
            _connectivityCheckTimer = new Timer(CheckConnectivityStatus, null, 0, 5000); // Kontrola každých 5 sekund
        }

        // VEŘEJNÉ METODY
        // Zjištění aktuálního stavu připojení k internetu
        public bool IsOnline()
        {
            return Microsoft.Maui.Networking.Connectivity.Current.NetworkAccess == Microsoft.Maui.Networking.NetworkAccess.Internet;
        }

        // SOUKROMÉ METODY
        // Periodická kontrola stavu připojení a vyvolání události při změně
        private void CheckConnectivityStatus(object? state)
        {
            bool currentState = IsOnline();

            // Pokud se stav změnil, vyvolat událost
            if (currentState != _lastKnownState)
            {
                _logger.LogInformation("Změna stavu připojení: {State}", currentState ? "Online" : "Offline");
                _lastKnownState = currentState;

                // Vyvoláme událost na hlavním vlákně
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    ConnectivityChanged?.Invoke(currentState);
                });
            }
        }

        // UVOLNĚNÍ ZDROJŮ
        // Uvolní zdroje komponenty - odregistruje event handlery pro předejití memory leaků
        public void Dispose()
        {
            _connectivityCheckTimer?.Dispose();
            _connectivityCheckTimer = null;
        }
    }
}
