namespace TestDetta.LicenseService;

/// <summary>What <see cref="TrialStore.ReserveAsync"/> decided.</summary>
public enum TrialReservation
{
    /// <summary>The trial is reserved; its license goes out next.</summary>
    Reserved,

    /// <summary>The address or one of the owners had a trial within the last 12 months.</summary>
    HadOne,

    /// <summary>The daily ceiling over all trials is reached.</summary>
    PausedForToday,
}
