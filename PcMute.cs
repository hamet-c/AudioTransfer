using NAudio.CoreAudioApi;

namespace PhoneSpeaker;

/// <summary>
/// Silences the PC's own speakers while streaming, and puts them back exactly as they were.
/// Turns the device volume down to zero instead of using mute, because some drivers also
/// silence the loopback stream when the device is muted.
/// </summary>
sealed class PcMute
{
    MMDevice? _device;
    float _savedVolume;
    bool _savedMute;

    public bool Active { get; private set; }

    public void Toggle()
    {
        if (Active) Restore();
        else Engage();
    }

    void Engage()
    {
        using var enumerator = new MMDeviceEnumerator();
        _device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var vol = _device.AudioEndpointVolume;
        _savedVolume = vol.MasterVolumeLevelScalar;
        _savedMute = vol.Mute;
        vol.MasterVolumeLevelScalar = 0f;
        Active = true;
    }

    public void Restore()
    {
        if (!Active || _device == null) return;
        try
        {
            var vol = _device.AudioEndpointVolume;
            vol.MasterVolumeLevelScalar = _savedVolume;
            vol.Mute = _savedMute;
        }
        catch { }
        Active = false;
    }
}
