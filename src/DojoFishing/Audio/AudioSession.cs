using System.Diagnostics;
using System.Threading.Channels;
using DojoFishing.Automation;
using DojoFishing.Detection;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DojoFishing.Audio;

public sealed class AudioSession : IAsyncDisposable
{
    private readonly Settings settings;
    private readonly int targetPid;
    private readonly SpectralDetector detector;
    private readonly DetectionGate gate;
    private readonly SequenceRunner sequence;
    private sealed record Packet(float[] Samples, bool Gap, double CapturedSeconds, double SilenceMs);
    private readonly Channel<Packet> packets = Channel.CreateBounded<Packet>(32);
    private readonly CancellationTokenSource cancellation = new();
    private WasapiRecorder? recorder;
    private Task? worker;
    private int reset, failed, sourceRate;
    private bool stopping;
    private double previousEndSeconds;
    public Analysis Latest { get; private set; } = new(0, 0, -120, 0);
    public int Detections { get; private set; }
    public bool Busy => sequence.Busy;
    public bool Armed => gate.Armed;
    public double QueueDelayMs { get; private set; }
    public event Action<string>? Log;
    public event Action<string>? Faulted;

    public AudioSession(Settings settings, SoundTemplate template, int targetPid, Func<bool>? foreground = null)
    {
        this.settings = settings;
        this.targetPid = targetPid;
        detector = new(template);
        gate = new(settings);
        sequence = new(targetPid, message => Log?.Invoke(message), foreground: foreground);
        sequence.Completed += () => Interlocked.Exchange(ref reset, 1);
    }

    public async Task StartLiveAsync(CancellationToken token)
    {
        var builder = new WasapiRecorderBuilder().WithBufferLength(20)
            .WithProcessLoopback((uint)targetPid, ProcessLoopbackMode.IncludeTargetProcessTree);
        var build = Task.Run(async () => await builder.BuildAsync());
        try { recorder = await build.WaitAsync(TimeSpan.FromSeconds(15), token); }
        catch
        {
            _ = build.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); }, TaskScheduler.Default);
            throw;
        }
        token.ThrowIfCancellationRequested();
        sourceRate = recorder.WaveFormat.SampleRate;
        worker = Task.Run(ProcessAsync);
        recorder.DataAvailable += OnData;
        recorder.RecordingStopped += (_, e) => { if (!stopping) Fail(e.Exception?.Message ?? "音声キャプチャが終了しました。"); };
        recorder.StartRecording();
        Log?.Invoke($"キャプチャ開始: {recorder.WaveFormat}");
    }

    private void OnData(ReadOnlySpan<byte> data, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        if (cancellation.IsCancellationRequested) return;
        try
        {
            var mono = (flags & AudioClientBufferFlags.Silent) != 0
                ? new float[data.Length / recorder!.WaveFormat.BlockAlign] : SampleConverter.Decode(data, recorder!.WaveFormat);
            bool gap = (flags & AudioClientBufferFlags.DataDiscontinuity) != 0;
            double stamp = (flags & AudioClientBufferFlags.TimestampError) == 0 && qpcPosition > 0
                ? qpcPosition / 10000000.0 : Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            double silenceMs = previousEndSeconds > 0 ? Math.Max(0, (stamp - previousEndSeconds) * 1000) : 0;
            if (silenceMs > 50) gap = true;
            previousEndSeconds = stamp + mono.Length / (double)sourceRate;
            if (!packets.Writer.TryWrite(new(mono, gap, stamp, silenceMs)))
                Fail("解析キューが満杯になりました。古い音声でクリックしないよう停止します。");
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    private async Task ProcessAsync()
    {
        try
        {
            var converter = new SampleConverter(sourceRate);
            await foreach (var packet in packets.Reader.ReadAllAsync(cancellation.Token))
            {
                cancellation.Token.ThrowIfCancellationRequested();
                QueueDelayMs = Math.Max(0, (Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency - packet.CapturedSeconds) * 1000);
                if (QueueDelayMs > 250) { Fail($"キャプチャ音声が{QueueDelayMs:F0}ms遅延したため停止します。"); break; }
                if (Interlocked.Exchange(ref reset, 0) != 0) { detector.Reset(); gate.RequireRelease(); }
                if (packet.Gap)
                {
                    detector.Reset(); converter.Reset(); gate.RequireRelease();
                    int releaseFrames = Math.Min(settings.ReleaseMs / 10 + 1, (int)(Math.Min(packet.SilenceMs, 10000) / 10));
                    for (int i = 0; i < releaseFrames; i++) gate.Update(0, -120, sequence.Busy);
                    Log?.Invoke("音声の不連続を検出。照合履歴を破棄しました。");
                }
                foreach (var analysis in detector.Push(converter.Convert(packet.Samples)))
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    Latest = analysis;
                    bool detected = gate.Update(analysis.Score, analysis.Db, sequence.Busy);
                    if (detected)
                    {
                        Detections++;
                        Log?.Invoke($"検出 #{Detections}: {analysis.AudioSeconds:F3}s / score={analysis.Score:F3}");
                        sequence.TryStart([new LeftClickStep(), new DelayStep(settings.IntervalMs), new LeftClickStep()]);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Fail(ex.Message); }
    }

    private void Fail(string message)
    {
        if (Interlocked.Exchange(ref failed, 1) != 0) return;
        cancellation.Cancel();
        _ = sequence.StopAsync();
        Faulted?.Invoke(message);
    }

    public async ValueTask DisposeAsync()
    {
        stopping = true;
        cancellation.Cancel();
        await sequence.StopAsync();
        try
        {
            if (recorder != null)
            {
                recorder.DataAvailable -= OnData;
                await recorder.DisposeAsync();
            }
        }
        finally
        {
            packets.Writer.TryComplete();
            try { if (worker != null) await worker; }
            finally { cancellation.Dispose(); }
        }
    }
}
