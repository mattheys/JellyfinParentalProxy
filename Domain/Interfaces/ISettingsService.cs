using System.Threading.Tasks;
using Domain.Models;

namespace SettingsService;

public interface ISettingsService
{
    Task<ProxyOptions> GetSettingsAsync();
    Task SetSettingsAsync(ProxyOptions options);
    Task InitializeAsync();
}