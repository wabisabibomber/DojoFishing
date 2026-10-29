using System.IO;
using NAudio.Dsp;
using DojoFishing.Audio;

namespace DojoFishing.Detection;

public record Analysis(double AudioSeconds, double Score, double Db, double ComputeMs);
public sealed record SoundTemplate(float[][] Frames, float[] Weights, double DurationMs, string Name)
{
    public static SoundTemplate LoadEmbedded()
    {
        using var stream = typeof(SoundTemplate).Assembly.GetManifestResourceStream("DojoFishing.TargetTemplate")
            ?? throw new InvalidDataException("組み込み検出データが見つかりません。");
        return Read(stream);
    }

    public static SoundTemplate Read(Stream stream)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        if (reader.ReadUInt32() != 0x31544644 || reader.ReadInt32() != 1 ||
            reader.ReadInt32() != SampleConverter.TargetRate || reader.ReadInt32() != SpectralDetector.Window ||
            reader.ReadInt32() != SpectralDetector.Hop || reader.ReadInt32() != SpectralDetector.Bands)
            throw new InvalidDataException("検出データの形式が解析処理と一致しません。");
        int count = reader.ReadInt32();
        double duration = reader.ReadDouble();
        if (count is < 4 or > 3000 || !double.IsFinite(duration) || duration is <= 0 or > 30000)
            throw new InvalidDataException("検出データの長さが不正です。");
        var frames = new float[count][];
        var weights = new float[count];
        for (int t = 0; t < count; t++)
        {
            weights[t] = ReadValue(reader);
            frames[t] = new float[SpectralDetector.Bands];
            for (int b = 0; b < SpectralDetector.Bands; b++) frames[t][b] = ReadValue(reader);
        }
        if (!weights.Any(x => x > 0) || stream.ReadByte() != -1) throw new InvalidDataException("検出データが不正です。");
        return new(frames, weights, duration, "組み込み効果音");
    }

    private static float ReadValue(BinaryReader reader)
    {
        float value = reader.ReadSingle();
        if (!float.IsFinite(value) || value is < 0 or > 1.00001f) throw new InvalidDataException("検出特徴量が不正です。");
        return value;
    }
}

/// <summary>Gain-invariant spectral shape + temporal alignment. No input injection.</summary>
public sealed class SpectralDetector(SoundTemplate template)
{
    public const int Window = 1024, Hop = 240, Bands = 96;
    private readonly List<float> pending = new();
    private readonly Queue<(float[] Feature, double Energy)> history = new();
    private long totalSamples;
    public void Reset() { pending.Clear(); history.Clear(); }
    public IEnumerable<Analysis> Push(float[] samples)
    {
        pending.AddRange(samples);
        totalSamples += samples.Length;
        var output = new List<Analysis>();
        int position = 0;
        while (pending.Count - position >= Window)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var frame = pending.GetRange(position, Window).ToArray();
            double energy = frame.Sum(x => (double)x * x) / Window;
            history.Enqueue((Feature(frame), energy));
            if (history.Count > template.Frames.Length) history.Dequeue();
            double db = 10 * Math.Log10(history.Average(x => x.Energy) + 1e-24);
            double score = 0;
            if (history.Count == template.Frames.Length)
            {
                var live = history.ToArray();
                double sum = 0, weights = 0;
                for (int t = 0; t < live.Length; t++)
                {
                    var target = template.Frames[t];
                    double weight = template.Weights[t];
                    if (weight <= 0) continue;
                    double best = 0;
                    // A 10ms local tolerance avoids alignment-dependent misses between capture hops.
                    for (int j = Math.Max(0, t - 1); j <= Math.Min(live.Length - 1, t + 1); j++)
                    {
                        double dot = 0;
                        for (int b = 0; b < Bands; b++) dot += target[b] * live[j].Feature[b];
                        best = Math.Max(best, dot);
                    }
                    sum += Math.Clamp(best, 0, 1) * weight;
                    weights += weight;
                }
                score = weights > 0 ? sum / weights : 0;
            }
            double seconds = (totalSamples - pending.Count + position + Window) / (double)SampleConverter.TargetRate;
            output.Add(new Analysis(seconds, score, db, clock.Elapsed.TotalMilliseconds));
            position += Hop;
        }
        if (position > 0) pending.RemoveRange(0, position);
        return output;
    }

    public static float[] Feature(ReadOnlySpan<float> samples)
    {
        var fft = new Complex[Window];
        for (int i = 0; i < Window; i++) fft[i].X = (float)(samples[i] * (.5 - .5 * Math.Cos(2 * Math.PI * i / (Window - 1))));
        FastFourierTransform.FFT(true, 10, fft);
        var bands = new float[Bands];
        for (int i = 3; i < Window / 2; i++)
        {
            double hz = i * SampleConverter.TargetRate / (double)Window;
            int band = Math.Clamp((int)(Math.Log(hz / 60) / Math.Log(12000.0 / 60) * Bands), 0, Bands - 1);
            bands[band] += fft[i].X * fft[i].X + fft[i].Y * fft[i].Y;
        }
        // Relative compression preserves gain invariance. Local spectral contrast suppresses smooth backgrounds.
        float max = bands.Max();
        if (max < 1e-16) return new float[Bands];
        var log = bands.Select(x => (float)Math.Log(1 + 100 * x / max)).ToArray();
        for (int b = 0; b < Bands; b++)
        {
            float local = 0;
            int n = 0;
            for (int j = Math.Max(0, b - 4); j <= Math.Min(Bands - 1, b + 4); j++) { local += log[j]; n++; }
            bands[b] = Math.Max(0, log[b] - .7f * local / n);
        }
        double norm = Math.Sqrt(bands.Sum(x => (double)x * x));
        if (norm > 1e-10) for (int b = 0; b < Bands; b++) bands[b] /= (float)norm;
        return bands;
    }
}
