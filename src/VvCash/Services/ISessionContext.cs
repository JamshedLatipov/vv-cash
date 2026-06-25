namespace VvCash.Services;

/// <summary>In-memory state of the current cash session (not persisted).</summary>
public interface ISessionContext
{
    string? WarehouseId { get; set; }
}
