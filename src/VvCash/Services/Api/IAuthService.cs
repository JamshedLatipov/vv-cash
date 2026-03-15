using System.Threading.Tasks;

namespace VvCash.Services.Api;

public interface IAuthService
{
    Task<bool> LoginAsync(string email, string password);
}
