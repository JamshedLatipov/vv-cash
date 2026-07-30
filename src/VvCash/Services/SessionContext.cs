namespace VvCash.Services;

public class SessionContext : ISessionContext
{
    public string? WarehouseId { get; set; }
    public string? CashId { get; set; }
}
