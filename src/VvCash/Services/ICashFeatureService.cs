using System.Threading.Tasks;
using VvCash.Models;

namespace VvCash.Services;

/// <summary>Single read point for the register's feature flags. Exists so that
/// view models ask one small thing "is this on" instead of each reaching into
/// storage and each getting the offline default subtly wrong.</summary>
public interface ICashFeatureService
{
    /// <summary>Always answerable, synchronously, from the moment the service is
    /// constructed — the POS screen binds visibility to it during construction,
    /// before any await has resolved.</summary>
    CashFeatures Current { get; }

    /// <summary>False until <see cref="RefreshAsync"/> has completed once, i.e. until
    /// <see cref="Current"/> reflects the register's real cached map rather than the
    /// all-enabled default. Most callers should ignore this: an unknown flag reading as
    /// enabled is the right trade for a shop floor. The customer-facing display is the
    /// exception — guessing wrong there shows a paying customer a screen the store
    /// deliberately switched off, so PosViewModel keeps that one hidden until this is
    /// true.</summary>
    bool HasLoaded { get; }

    /// <summary>Reloads the cached map from local storage. Deliberately not called
    /// at application start: the local database is created by
    /// PosViewModel.InitializeAsync, so any earlier read would query a schema that
    /// does not exist yet. The register refreshes this once its storage is ready,
    /// and SyncService rewrites the underlying cache on every sync pass.</summary>
    Task RefreshAsync();
}
