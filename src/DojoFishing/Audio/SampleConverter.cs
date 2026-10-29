using NAudio.Wave;

namespace DojoFishing.Audio;

/// <summary>Packet-independent mono conversion and windowed-sinc resampling.</summary>
public sealed class SampleConverter
{
    public const int TargetRate = 24000;
    private readonly int sourceRate;
    private readonly Queue<float> history = new();
    private long offset;
    private double next;
    private const int Radius = 16;

    public SampleConverter(int sourceRate) { this.sourceRate = sourceRate; Reset(); }
    public void Reset()
    {
        history.Clear();
        for (int i = 0; i < Radius; i++) history.Enqueue(0);
        offset = -Radius;
        next = 0;
    }
    public float[] Convert(ReadOnlySpan<float> mono)
    {
        var output = new List<float>((int)(mono.Length * (double)TargetRate / sourceRate) + 1);
        foreach (var value in mono)
        {
            history.Enqueue(float.IsFinite(value) ? value : 0);
            while (next + Radius < offset + history.Count)
            {
                var samples = history.ToArray();
                double sum = 0, weight = 0;
                var center = (long)Math.Floor(next);
                double cutoff = .46 * Math.Min(1, (double)TargetRate / sourceRate);
                for (long i = center - Radius + 1; i <= center + Radius; i++)
                {
                    double distance = i - next;
                    double x = 2 * cutoff * distance;
                    double sinc = Math.Abs(x) < 1e-10 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);
                    double w = 2 * cutoff * sinc * (.5 + .5 * Math.Cos(Math.PI * distance / Radius));
                    sum += samples[(int)(i - offset)] * w;
                    weight += w;
                }
                output.Add((float)(sum / weight));
                next += (double)sourceRate / TargetRate;
                long keep = (long)Math.Floor(next) - Radius + 1;
                while (offset < keep && history.Count > 0) { history.Dequeue(); offset++; }
            }
        }
        return output.ToArray();
    }

    public static float[] Decode(ReadOnlySpan<byte> data, WaveFormat format)
    {
        var standard = format is WaveFormatExtensible extensible ? extensible.ToStandardWaveFormat() : format;
        int bytes = standard.BitsPerSample / 8;
        bool floating = standard.Encoding == WaveFormatEncoding.IeeeFloat;
        if ((!floating && standard.Encoding != WaveFormatEncoding.Pcm) ||
            (floating && bytes != 4) || bytes is < 2 or > 4)
            throw new NotSupportedException($"未対応の音声形式: {format}");
        var result = new float[data.Length / format.BlockAlign];
        for (int frame = 0; frame < result.Length; frame++)
        {
            double value = 0;
            for (int channel = 0; channel < standard.Channels; channel++)
            {
                int p = frame * format.BlockAlign + channel * bytes;
                value += floating ? BitConverter.ToSingle(data.Slice(p, 4)) : bytes switch
                {
                    2 => BitConverter.ToInt16(data.Slice(p, 2)) / 32768f,
                    3 => ((data[p] | data[p + 1] << 8 | data[p + 2] << 16) << 8 >> 8) / 8388608f,
                    4 => BitConverter.ToInt32(data.Slice(p, 4)) / 2147483648f,
                    _ => 0
                };
            }
            result[frame] = (float)(value / standard.Channels);
        }
        return result;
    }
}
