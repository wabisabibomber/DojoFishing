using DojoFishing.Audio;
using DojoFishing.Detection;
using NAudio.Wave;

namespace DojoFishing.Developer;

// Development-only WAV processing. Never compiled into the application.
public static class TemplateExtractor
{
    public static SoundTemplate Extract(string path)
    {
        using var reader = new AudioFileReader(path);
        if (reader.TotalTime.TotalSeconds > 30) throw new InvalidDataException("効果音は30秒以内にしてください。");
        var mono = new List<float>();
        var buffer = new float[4096 * reader.WaveFormat.Channels];
        var provider = reader.ToSampleProvider();
        int read;
        while ((read = provider.Read(buffer.AsSpan())) > 0)
            for (int p = 0; p < read; p += reader.WaveFormat.Channels)
            {
                float sum = 0;
                for (int c = 0; c < reader.WaveFormat.Channels; c++) sum += buffer[p + c];
                mono.Add(sum / reader.WaveFormat.Channels);
            }
        var input = mono.ToArray();
        if (input.Length == 0 || input.Max(x => Math.Abs(x)) < 1e-6) throw new InvalidDataException("音源が無音です。");
        var samples = new SampleConverter(reader.WaveFormat.SampleRate).Convert(input.Concat(new float[32]).ToArray());
        var frames = new List<float[]>();
        var weights = new List<float>();
        for (int p = 0; p + SpectralDetector.Window <= samples.Length; p += SpectralDetector.Hop)
        {
            frames.Add(SpectralDetector.Feature(samples.AsSpan(p, SpectralDetector.Window)));
            double energy = 0;
            for (int i = p; i < p + SpectralDetector.Window; i++) energy += samples[i] * samples[i];
            weights.Add((float)Math.Sqrt(energy / SpectralDetector.Window));
        }
        if (frames.Count < 4) throw new InvalidDataException("効果音は約75ms以上必要です。");
        float maxWeight = weights.Max();
        return new(frames.ToArray(), weights.Select(x => x < maxWeight * .01f ? 0 : x / maxWeight).ToArray(),
            input.Length * 1000.0 / reader.WaveFormat.SampleRate, "組み込み効果音");
    }

    public static void Write(SoundTemplate template, Stream stream)
    {
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(0x31544644u);
        writer.Write(1);
        writer.Write(SampleConverter.TargetRate);
        writer.Write(SpectralDetector.Window);
        writer.Write(SpectralDetector.Hop);
        writer.Write(SpectralDetector.Bands);
        writer.Write(template.Frames.Length);
        writer.Write(template.DurationMs);
        for (int t = 0; t < template.Frames.Length; t++)
        {
            writer.Write(template.Weights[t]);
            foreach (float value in template.Frames[t]) writer.Write(value);
        }
    }
}
