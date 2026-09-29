namespace ArtNet.Shared.Services;

/// <summary>
/// Android filters broadcast/multicast packets on Wi-Fi unless a multicast lock is held.
/// No-op on other platforms.
/// </summary>
public sealed class MulticastLockHolder
{
#if ANDROID
    private Android.Net.Wifi.WifiManager.MulticastLock? _lock;
#endif

    public void Acquire()
    {
#if ANDROID
        if (_lock is not null) return;
        var wifi = Android.App.Application.Context.GetSystemService(Android.Content.Context.WifiService) as Android.Net.Wifi.WifiManager;
        _lock = wifi?.CreateMulticastLock("artnet");
        if (_lock is null) return;
        _lock.SetReferenceCounted(false);
        _lock.Acquire();
#endif
    }

    public void Release()
    {
#if ANDROID
        if (_lock is null) return;
        if (_lock.IsHeld) _lock.Release();
        _lock = null;
#endif
    }
}
