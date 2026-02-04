namespace HidControl.Core;

// Shared per-process lock for device key operations.
/// <summary>
/// Core model for DeviceKeyLockHolder.
/// </summary>
internal static class DeviceKeyLockHolder
{
    internal static readonly SemaphoreSlim Lock = new(1, 1);
}
