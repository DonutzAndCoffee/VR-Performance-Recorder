using Microsoft.Win32;

namespace openXRTK_Graph;

/// <summary>Optional audible feedback when a recording starts or stops.</summary>
public static class RecordingIndicator
{
    private const string SoundValue = "RecordingSound";

    public static bool SoundEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(global::XrPerf.Contracts.ControlProtocol.AppRegistryKey);
                return key?.GetValue(SoundValue) is int v ? v != 0 : true;
            }
            catch (Exception) { return true; }
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(global::XrPerf.Contracts.ControlProtocol.AppRegistryKey);
                key.SetValue(SoundValue, value ? 1 : 0, RegistryValueKind.DWord);
            }
            catch (Exception) { }
        }
    }

    public static void Play(bool started)
    {
        if (!SoundEnabled) return;
        Task.Run(() =>
        {
            try
            {
                byte[] wav = started ? StartWav.Value : StopWav.Value;
                using var ms = new System.IO.MemoryStream(wav);
                using var player = new System.Media.SoundPlayer(ms);
                player.PlaySync();
            }
            catch (Exception) { }
        });
    }

    // Console.Beep is unreliable in a WPF app (no console / PC speaker routing), so tones are synthesized as WAV.
    private static readonly Lazy<byte[]> StartWav = new(() => BuildWav((880, 120), (1320, 180)));
    private static readonly Lazy<byte[]> StopWav = new(() => BuildWav((1320, 120), (660, 250)));

    private static byte[] BuildWav(params (double Freq, int Ms)[] tones)
    {
        const int rate = 44100;
        var samples = new List<short>();
        foreach (var (freq, ms) in tones)
        {
            int n = rate * ms / 1000;
            int fade = Math.Min(n / 4, rate / 200);
            for (int i = 0; i < n; i++)
            {
                double env = Math.Min(1.0, Math.Min(i, n - 1 - i) / (double)Math.Max(1, fade));
                samples.Add((short)(Math.Sin(2 * Math.PI * freq * i / rate) * env * 0.35 * short.MaxValue));
            }
        }

        using var ms2 = new System.IO.MemoryStream();
        using var w = new System.IO.BinaryWriter(ms2);
        int dataLen = samples.Count * 2;
        w.Write("RIFF"u8); w.Write(36 + dataLen); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(dataLen);
        foreach (var s in samples) w.Write(s);
        w.Flush();
        return ms2.ToArray();
    }
}
