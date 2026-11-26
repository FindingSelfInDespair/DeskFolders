using System;
using System.IO;
using System.Runtime.InteropServices;
using DeskFolders.Models;
using DeskFolders.Native;

namespace DeskFolders.Services;

public sealed class SoundService : IDisposable
{
    private readonly AppConfig _config;
    private readonly PinnedWave _pop;
    private readonly PinnedWave _tick;
    private readonly PinnedWave _snap;
    private readonly PinnedWave _launchpad;
    private bool _disposed;

    public SoundService(AppConfig config)
    {
        _config = config;
        _pop = new PinnedWave(SynthesizeSweep(370, 610, 26, 0.14, 10.0));
        _tick = new PinnedWave(SynthesizeSweep(540, 340, 15, 0.10, 15.0));
        _snap = new PinnedWave(SynthesizeSweep(240, 130, 22, 0.14, 12.0));
        _launchpad = new PinnedWave(SynthesizeChord(440, 660, 44, 0.11));
    }

    public void PlayPop() => Play(_pop);
    public void PlayTick() => Play(_tick);
    public void PlaySnap() => Play(_snap);
    public void PlayLaunchpad() => Play(_launchpad);

    private void Play(PinnedWave wave)
    {
        if (_disposed || !_config.EnableSounds)
            return;

        NativeMethods.PlaySound(
            wave.Pointer,
            IntPtr.Zero,
            NativeMethods.SND_MEMORY | NativeMethods.SND_ASYNC | NativeMethods.SND_NODEFAULT);
    }

    private static byte[] SynthesizeSweep(double fStart, double fEnd, int durationMs, double volume, double decay)
    {
        const int sampleRate = 44100;
        int sampleCount = sampleRate * durationMs / 1000;
        var samples = new short[sampleCount];

        double phase = 0;
        for (int i = 0; i < sampleCount; i++)
        {
            double t = (double)i / sampleCount;
            double freq = fStart + (fEnd - fStart) * t;
            phase += 2.0 * Math.PI * freq / sampleRate;

            double attack = i < sampleCount * 0.12 ? i / (sampleCount * 0.12) : 1.0;
            double env = attack * Math.Exp(-decay * t);
            double val = Math.Sin(phase) * env * volume;
            samples[i] = (short)Math.Clamp(val * short.MaxValue, short.MinValue, short.MaxValue);
        }

        return WrapWav(samples, sampleRate);
    }

    private static byte[] SynthesizeChord(double f1, double f2, int durationMs, double volume)
    {
        const int sampleRate = 44100;
        int sampleCount = sampleRate * durationMs / 1000;
        var samples = new short[sampleCount];

        for (int i = 0; i < sampleCount; i++)
        {
            double t = (double)i / sampleCount;
            double sec = (double)i / sampleRate;
            double attack = i < sampleCount * 0.15 ? i / (sampleCount * 0.15) : 1.0;
            double env = attack * Math.Exp(-6.5 * t);
            double wave = (Math.Sin(2.0 * Math.PI * f1 * sec) * 0.6 + Math.Sin(2.0 * Math.PI * f2 * sec) * 0.4);
            samples[i] = (short)Math.Clamp(wave * env * volume * short.MaxValue, short.MinValue, short.MaxValue);
        }

        return WrapWav(samples, sampleRate);
    }

    private static byte[] WrapWav(short[] samples, int sampleRate)
    {
        int dataBytes = samples.Length * 2;
        using var ms = new MemoryStream(44 + dataBytes);
        using var bw = new BinaryWriter(ms);

        bw.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
        bw.Write(36 + dataBytes);
        bw.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E' });
        bw.Write(new[] { (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
        bw.Write(16);
        bw.Write((short)1);
        bw.Write((short)1);
        bw.Write(sampleRate);
        bw.Write(sampleRate * 2);
        bw.Write((short)2);
        bw.Write((short)16);
        bw.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
        bw.Write(dataBytes);

        for (int i = 0; i < samples.Length; i++)
            bw.Write(samples[i]);

        return ms.ToArray();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _pop.Dispose();
        _tick.Dispose();
        _snap.Dispose();
        _launchpad.Dispose();
    }

    private sealed class PinnedWave : IDisposable
    {
        private readonly byte[] _buffer;
        private GCHandle _handle;

        public IntPtr Pointer => _handle.AddrOfPinnedObject();

        public PinnedWave(byte[] buffer)
        {
            _buffer = buffer;
            _handle = GCHandle.Alloc(_buffer, GCHandleType.Pinned);
        }

        public void Dispose()
        {
            if (_handle.IsAllocated)
                _handle.Free();
        }
    }
}