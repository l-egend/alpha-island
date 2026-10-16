using Windows.System.Power;

namespace alpha_island.Services;

internal readonly record struct NativeBatterySnapshot(byte ACLineStatus, byte BatteryFlag, byte BatteryLifePercent,
    byte SystemStatusFlag, int BatteryLifeTime);
internal sealed record BatteryHardwareReport(BatteryStatus Status, int? ChargeRateInMilliwatts,
    int? RemainingCapacityInMilliwattHours, int? FullChargeCapacityInMilliwattHours);
