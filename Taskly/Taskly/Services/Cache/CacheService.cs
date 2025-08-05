// CacheService.cs - Služba pro práci s mezipamětí
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text.Json;
using Microsoft.Maui.Storage;
using Microsoft.Extensions.Logging;

namespace Taskly.Services.Cache
{
    public class CacheService : ICacheService
    {
        // PROMĚNNÉ A VLASTNOSTI
        private readonly Dictionary<string, (object Data, DateTime Expiry)> _cache = new();
        private readonly TimeSpan _defaultExpiry = TimeSpan.FromHours(24);

        // POMOCNÉ TŘÍDY
        // Třída pro serializaci dat ukládaných na disk
        private class CacheItem<T>
        {
            public T? Data { get; set; }
            public DateTime Expiry { get; set; }
        }

        // VEŘEJNÉ METODY
        // Získáme data z mezipaměti nebo vytvoříme pomocí factory funkce
        public async Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory, TimeSpan? expiry = null)
        {
            if (string.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key));

            // 1. Zkusíme najít v RAM cache
            if (TryGet<T>(key, out var memoryData))
                return memoryData;

            // 2. Zkusíme načíst z disku
            try
            {
                string? storedJson = await SecureStorage.GetAsync($"cache_{key}");

                if (!string.IsNullOrEmpty(storedJson))
                {
                    var stored = JsonSerializer.Deserialize<CacheItem<T>>(storedJson);
                    if (stored != null && stored.Expiry > DateTime.UtcNow)
                    {
                        // Uložíme i do RAM pro příště
                        _cache[key] = (stored.Data!, stored.Expiry);
                        return stored.Data!;
                    }
                }
            }
            catch { /* Ignorujeme chyby při čtení z disku */ }

            // 3. Načteme data ze zdroje
            var data = await factory();

            // 4. Uložíme do RAM i na disk
            var expiryTime = DateTime.UtcNow.Add(expiry ?? _defaultExpiry);
            _cache[key] = (data!, expiryTime);

            try
            {
                var cacheItem = new CacheItem<T> { Data = data, Expiry = expiryTime };
                string json = JsonSerializer.Serialize(cacheItem);
                await SecureStorage.SetAsync($"cache_{key}", json);
            }
            catch { /* Ignorujeme chyby při zápisu na disk */ }

            return data;
        }

        // Přímo uložíme data do mezipaměti (RAM i disku)
        public async Task SetAsync<T>(string key, T data, TimeSpan? expiry = null)
        {
            if (string.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key));

            var expiryTime = DateTime.UtcNow.Add(expiry ?? _defaultExpiry);

            // Uložíme do RAM
            _cache[key] = (data!, expiryTime);

            // Uložíme na disk
            try
            {
                var cacheItem = new CacheItem<T> { Data = data, Expiry = expiryTime };
                string json = JsonSerializer.Serialize(cacheItem);
                await SecureStorage.SetAsync($"cache_{key}", json);
            }
            catch { /* Ignorujeme chyby při zápisu na disk */ }
        }

        // Synchronně vymažeme data z RAM mezipaměti
        public void Clear(string? prefix = null)
        {
            if (prefix == null)
            {
                _cache.Clear();
            }
            else
            {
                var keysToRemove = _cache.Keys.Where(k => k.StartsWith(prefix)).ToList();
                foreach (var key in keysToRemove)
                {
                    _cache.Remove(key);
                }
            }
        }

        // Asynchronně vymažeme data z RAM i diskové mezipaměti
        public async Task ClearAsync(string? prefix = null)
        {
            // Vymažeme z RAM
            Clear(prefix);

            // Vymažeme z disku (jen pro konkrétní prefix)
            if (prefix != null)
            {
                try
                {
                    // Získáme všechny cache klíče - toto je zjednodušené
                    var allKeys = await GetAllCacheKeysAsync();
                    foreach (var key in allKeys.Where(k => k.StartsWith($"cache_{prefix}")))
                    {
                        await SecureStorage.SetAsync(key, null!);
                    }
                }
                catch { /* Ignorujeme chyby */ }
            }
        }

        // POMOCNÉ METODY
        // Pokusíme se získat data z RAM mezipaměti
        private bool TryGet<T>(string key, out T data)
        {
            data = default!;

            if (string.IsNullOrEmpty(key))
                return false;

            if (_cache.TryGetValue(key, out var cachedItem) && cachedItem.Expiry > DateTime.UtcNow)
            {
                if (cachedItem.Data is T typedData)
                {
                    data = typedData;
                    return true;
                }
            }
            return false;
        }

        // Pomocná metoda pro získání všech klíčů z mezipaměti
        private Task<IEnumerable<string>> GetAllCacheKeysAsync()
        {
            // Implementace závisí na platformě - pro demonstrační účely stačí prázdný seznam
            return Task.FromResult<IEnumerable<string>>(new List<string>());
        }
    }
}