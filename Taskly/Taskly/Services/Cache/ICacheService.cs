// ICacheService.cs - Rozhraní pro službu mezipaměti
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Taskly.Services.Cache
{
    // Definice rozhraní
    public interface ICacheService
    {
        // Uložení dat do mezipaměti
        Task SetAsync<T>(string key, T data, TimeSpan? expiry = null);

        // Získání dat z mezipaměti nebo jejich vytvoření pomocí factory
        Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory, TimeSpan? expiry = null);

        // Vyčištění mezipaměti synchronně
        void Clear(string? prefix = null);

        // Vyčištění mezipaměti asynchronně
        Task ClearAsync(string? prefix = null);
    }
}
