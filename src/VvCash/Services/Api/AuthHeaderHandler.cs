using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace VvCash.Services.Api;

public class AuthHeaderHandler : DelegatingHandler
{
    private readonly ISettingsService _settingsService;

    public AuthHeaderHandler(ISettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = _settingsService.CashRegisterToken;
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.TryAddWithoutValidation("Cash-Authorization", token);
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
