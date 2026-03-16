namespace VvCash.Services;

public interface ISettingsService
{
    string BackendUrl { get; set; }
    string CashRegisterToken { get; set; }
    string AuthToken { get; set; }
    int SyncIntervalMinutes { get; set; }
    void Save();
}
