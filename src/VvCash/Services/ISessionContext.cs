namespace VvCash.Services;

/// <summary>In-memory state of the current cash session (not persisted).</summary>
public interface ISessionContext
{
    string? WarehouseId { get; set; }

    /// <summary>Id of the cash this register is signed in as, learned from the shift
    /// state/open reply. Every other call infers it from the token; only the till
    /// payout (POST /documents/money/expense/create/) makes the client name it.</summary>
    string? CashId { get; set; }
}
