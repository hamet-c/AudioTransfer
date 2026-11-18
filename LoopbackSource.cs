using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace PhoneSpeaker;

/// <summary>
/// Captures whatever the default Windows output device is playing and emits it as
/// 10 ms frames of 48 kHz, 16-bit, interleaved stereo PCM.
/// </summary>
sealed class LoopbackSource : IDisposable
{
    public const int SampleRate = 48000;
    public const int FrameSamples = 480; // per channel, 10 ms
    const int FrameFloats = FrameSamples * 2;

    public event Action<byte[]>? Frame;
    public string DeviceName { get; private set; } = "";

    readonly object _gate = new();
    readonly MMDeviceEnumerator _enumerator = new();
    readonly DeviceWatcher _watcher;
    WasapiLoopbackCapture? _capture;
    WasapiOut? _keepAlive;
    BufferedWaveProvider? _buffer;
    ISampleProvider? _chain;
    readonly float[] _readBuf = new float[8192];
    readonly float[] _pending = new float[FrameFloats];
    int _pendingCount;
    float _peak;
    int _restartQueued;
    bool _disposed;

    public LoopbackSource()
    {
        _watcher = new DeviceWatcher(QueueRestart);
        _enumerator.RegisterEndpointNotificationCallback(_watcher);
    }

    /// <summary>Peak level (0..1) since the last call.</summary>
    public float TakePeak()
    {
        var p = _peak;
        _peak = 0;
        return p;
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed) return;
            StopLocked();

            var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            DeviceName = device.FriendlyName;

            var capture = new WasapiLoopbackCapture(device);
            var format = capture.WaveFormat is WaveFormatExtensible ext
                ? ext.ToStandardWaveFormat()
                : capture.WaveFormat;

            _buffer = new BufferedWaveProvider(format)
            {
                ReadFully = false,
                BufferDuration = TimeSpan.FromSeconds(2),
                DiscardOnBufferOverflow = true,
            };

            ISampleProvider chain = _buffer.ToSampleProvider();
            chain = format.Channels switch
            {
                1 => new MonoToStereoSampleProvider(chain),
                2 => chain,
                _ => new FirstTwoChannels(chain),
            };
            if (chain.WaveFormat.SampleRate != SampleRate)
                chain = new WdlResamplingSampleProvider(chain, SampleRate);
            _chain = chain;
            _pendingCount = 0;

            capture.DataAvailable += OnData;
            capture.RecordingStopped += (_, e) =>
            {
                if (e.Exception != null) QueueRestart();
            };

            // Loopback capture delivers nothing while the device is idle, which would leave
            // gaps in the stream. Playing silence keeps the device clock running so frames
            // keep flowing at a steady rate.
            _keepAlive = new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
            _keepAlive.Init(new SilenceProvider(capture.WaveFormat));
            _keepAlive.Play();

            _capture = capture;
            capture.StartRecording();
        }
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        if (_buffer == null || _chain == null || e.BytesRecorded == 0) return;
        _buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);

        int n;
        while ((n = _chain.Read(_readBuf, 0, _readBuf.Length)) > 0)
        {
            for (int i = 0; i < n; i++)
            {
                var s = _readBuf[i];
                var a = Math.Abs(s);
                if (a > _peak) _peak = a;
                _pending[_pendingCount++] = s;
                if (_pendingCount == FrameFloats)
                {
                    Frame?.Invoke(ToPcm16(_pending));
                    _pendingCount = 0;
                }
            }
        }
    }

    static byte[] ToPcm16(float[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            var v = (short)(Math.Clamp(samples[i], -1f, 1f) * short.MaxValue);
            bytes[i * 2] = (byte)v;
            bytes[i * 2 + 1] = (byte)(v >> 8);
        }
        return bytes;
    }

    void QueueRestart()
    {
        if (Interlocked.Exchange(ref _restartQueued, 1) == 1) return;
        Task.Run(async () =>
        {
            // Let Windows finish switching devices before reopening.
            await Task.Delay(500);
            Interlocked.Exchange(ref _restartQueued, 0);
            for (int attempt = 0; attempt < 10 && !_disposed; attempt++)
            {
                try
                {
                    Start();
                    return;
                }
                catch
                {
                    await Task.Delay(1000);
                }
            }
        });
    }

    void StopLocked()
    {
        if (_capture != null)
        {
            _capture.DataAvailable -= OnData;
            try { _capture.StopRecording(); } catch { }
            _capture.Dispose();
            _capture = null;
        }
        if (_keepAlive != null)
        {
            try { _keepAlive.Stop(); } catch { }
            _keepAlive.Dispose();
            _keepAlive = null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            StopLocked();
        }
        try { _enumerator.UnregisterEndpointNotificationCallback(_watcher); } catch { }
        _enumerator.Dispose();
    }

    /// <summary>Keeps the front-left/front-right channels of a multichannel stream.</summary>
    sealed class FirstTwoChannels(ISampleProvider source) : ISampleProvider
    {
        readonly int _channels = source.WaveFormat.Channels;
        float[] _scratch = [];

        public WaveFormat WaveFormat { get; } =
            WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = count / 2;
            int needed = frames * _channels;
            if (_scratch.Length < needed) _scratch = new float[needed];
            int got = source.Read(_scratch, 0, needed) / _channels;
            for (int f = 0; f < got; f++)
            {
                buffer[offset + f * 2] = _scratch[f * _channels];
                buffer[offset + f * 2 + 1] = _scratch[f * _channels + 1];
            }
            return got * 2;
        }
    }

    sealed class DeviceWatcher(Action onDefaultChanged) : IMMNotificationClient
    {
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Render && role == Role.Multimedia) onDefaultChanged();
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}
