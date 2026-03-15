namespace VvCash.Services;

public interface ISettingsService
{
    string BackendUrl { get; set; }
    string CashRegisterToken { get; set; }
    string AuthToken { get; set; }
    void Save();
}
