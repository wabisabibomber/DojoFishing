using System.Diagnostics;
using System.Text.Json;
using DojoFishing;
using DojoFishing.Audio;
using DojoFishing.Automation;
using DojoFishing.Detection;
using DojoFishing.Processes;
using DojoFishing.Developer;
using NAudio.Wave;

int failures = 0;
void Check(bool condition, string name)
{
    Console.WriteLine($"{(condition ? "PASS" : "FAIL")} {name}");
    if (!condition) failures++;
}

var settings = new Settings();
Check(settings.Threshold == .55 && Math.Abs(settings.ReleaseThreshold - .495) < 1e-10, "default thresholds: .55 / .495");
var changed = settings with { Threshold = .7 };
Check(Math.Abs(changed.ReleaseThreshold - .63) < 1e-10, "release threshold follows detection threshold");
var fromJson = JsonSerializer.Deserialize<Settings>("""{"Version":2,"Threshold":0.6,"ReleaseThreshold":0.1,"MinimumDb":0,"ConfirmFrames":20,"ReleaseMs":1}""")!;
Check(Math.Abs(fromJson.ReleaseThreshold - .54) < 1e-10 && fromJson.MinimumDb == -80 && fromJson.ConfirmFrames == 2 && fromJson.ReleaseMs == 200, "JSON cannot override fixed detection conditions");
Check(settings.IntervalMs == 1800 && settings.ProcessFilter == "VRChat.exe", "interval / search defaults");
string jsonPath = Path.Combine(AppContext.BaseDirectory, "checks.settings.json");
var jsonSettings = settings with { ProcessFilter = " 音声=\"VRChat\" ", Threshold = .63, IntervalMs = 1801 };
jsonSettings.Save(jsonPath);
Check(Settings.Load(jsonPath) == jsonSettings, "JSON round-trip preserves Unicode, quotes, equals and spaces");
string previousJson = File.ReadAllText(jsonPath);
try { (jsonSettings with { Threshold = double.NaN }).Save(jsonPath); }
catch (InvalidDataException) { }
Check(File.ReadAllText(jsonPath) == previousJson, "invalid settings leave last valid JSON intact");
File.WriteAllText(jsonPath, """{"Threshold":0.55,"ProcessFilter":"","ReleaseThreshold":0.1,"MinimumDb":0,"ConfirmFrames":20,"ReleaseMs":1}""");
var reloaded = Settings.Load(jsonPath);
Check(reloaded.ProcessFilter == "" && Math.Abs(reloaded.ReleaseThreshold - .495) < 1e-10 && reloaded.MinimumDb == -80 && reloaded.ConfirmFrames == 2 && reloaded.ReleaseMs == 200, "JSON accepts empty search and cannot override fixed conditions");
var priorCulture = System.Globalization.CultureInfo.CurrentCulture;
try
{
    System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
    jsonSettings.Save(jsonPath);
    Check(Settings.Load(jsonPath) == jsonSettings && File.ReadAllText(jsonPath).Contains("0.63"), "JSON numeric values are locale independent");
}
finally { System.Globalization.CultureInfo.CurrentCulture = priorCulture; }
string migrationDirectory = Path.Combine(AppContext.BaseDirectory, "migration-check-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(migrationDirectory);
string migrationJson = Path.Combine(migrationDirectory, "settings.json");
string migrationIni = Path.Combine(migrationDirectory, "settings.ini");
settings.Save(migrationJson);
File.WriteAllText(migrationIni, "[DojoFishing]\nVersion=2\nThreshold=0.64\nIntervalMs=1900\nProcessFilter=\"VRChat.exe\"\nHotkey=\"Ctrl+Alt+F10\"\n");
File.SetLastWriteTimeUtc(migrationJson, DateTime.UtcNow.AddMinutes(-5));
var migrated = Settings.LoadCurrent(migrationDirectory);
Check(migrated.Threshold == .64 && migrated.IntervalMs == 1900 && Settings.Load(migrationJson) == migrated, "newer INI migrates over stale JSON");
var afterMigration = migrated with { Threshold = .65 };
afterMigration.Save(migrationJson);
Check(Settings.LoadCurrent(migrationDirectory) == afterMigration, "subsequent startup automatically loads updated JSON");
Check(ProcessChoice.Matches("VRChat.exe", "VRChat.exe", ""), "exe suffix search");
Check(ProcessChoice.Matches(" vrCHAT ", "VRChat.exe", ""), "case-insensitive partial search / whitespace");
Check(ProcessChoice.Matches("world", "VRChat.exe", "My World"), "window title search");
Check(ProcessChoice.Matches("", "other.exe", "") && !ProcessChoice.Matches("VRChat.exe", "other.exe", ""), "empty keyword / nonmatching process");

string sound = Path.GetFullPath("assets/sounds/sound.wav");
var template = SoundTemplate.LoadEmbedded();
var extracted = TemplateExtractor.Extract(sound);
Check(template.Frames.SelectMany(x => x).SequenceEqual(extracted.Frames.SelectMany(x => x)) && template.Weights.SequenceEqual(extracted.Weights), "embedded features exactly match development WAV extraction");
var resources = typeof(SoundTemplate).Assembly.GetManifestResourceNames();
Check(resources.Contains("DojoFishing.TargetTemplate") && !resources.Any(x => x.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)), "template resource present, no embedded WAV");
using var reader = new AudioFileReader(sound);
Check(Math.Abs(template.DurationMs - reader.TotalTime.TotalMilliseconds) < 1, "whole file duration retained, including quiet tail");
var provider = reader.ToSampleProvider();
var buffer = new float[4096 * reader.WaveFormat.Channels];
var mono = new List<float>();
int count;
while ((count = provider.Read(buffer.AsSpan())) > 0)
    for (int i = 0; i < count; i += reader.WaveFormat.Channels)
        mono.Add(buffer.AsSpan(i, reader.WaveFormat.Channels).ToArray().Average());
float[] samples = new SampleConverter(reader.WaveFormat.SampleRate).Convert(mono.Concat(new float[32]).ToArray());
foreach (double gain in new[] { 1.0, .25, .05, .01 })
{
    var input = new float[24000 + samples.Length];
    for (int i = 0; i < samples.Length; i++) input[12073 + i] = (float)(samples[i] * gain);
    var detector = new SpectralDetector(template);
    var gate = new DetectionGate(settings);
    int hits = 0;
    double peak = 0;
    foreach (var chunk in input.Chunk(480))
        foreach (var frame in detector.Push(chunk))
        {
            peak = Math.Max(peak, frame.Score);
            if (gate.Update(frame.Score, frame.Db, false)) hits++;
        }
    Check(hits == 1, $"whole-file detection gain={gain:F2}: hits={hits}, peak={peak:F3}");
}
var gateHold = new DetectionGate(settings);
gateHold.RequireRelease();
for (int i = 0; i < 100; i++) gateHold.Update(.52, -30, false);
Check(!gateHold.Armed, "release waits below automatically derived .495");
for (int i = 0; i < 20; i++) gateHold.Update(.49, -30, false);
Check(gateHold.Armed, "200ms below derived release threshold rearms");

int clicks = 0;
var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var sequence = new SequenceRunner(1, _ => { }, () => { Interlocked.Increment(ref clicks); first.TrySetResult(); }, () => true);
Check(sequence.TryStart([new LeftClickStep(), new DelayStep(200), new LeftClickStep()]), "sequence starts");
Check(!sequence.TryStart([new LeftClickStep()]), "duplicate sequence rejected");
await first.Task.WaitAsync(TimeSpan.FromSeconds(2));
await sequence.StopAsync();
await Task.Delay(250);
Check(clicks == 1, "stop cancels second click");
var focus = new SequenceRunner(1, _ => { }, () => clicks++, () => false);
focus.TryStart([new LeftClickStep()]);
await Task.Delay(100);
await focus.StopAsync();
Check(clicks == 1, "foreground guard prevents input");

if (args.Contains("--live"))
{
    string captures = Path.Combine(AppContext.BaseDirectory, "captures");
    string[] before = Directory.Exists(captures) ? Directory.GetFiles(captures).Order().ToArray() : [];
    // Exercise real process capture, but force the foreground guard false: no input can be sent.
    await using var live = new AudioSession(settings, template, Environment.ProcessId, () => false);
    string? fault = null;
    live.Log += Console.WriteLine;
    live.Faulted += x => fault = x;
    await live.StartLiveAsync(CancellationToken.None);
    await Task.Delay(500);
    using var audio = new AudioFileReader(sound);
    using var player = new WasapiPlayerBuilder().WithLatency(40).Build();
    player.Init(audio);
    player.Play();
    await Task.Delay(2000);
    player.Stop();
    Check(fault == null && live.Detections == 1, $"process-only capture + whole-file detection: hits={live.Detections}, fault={fault}");
    string[] after = Directory.Exists(captures) ? Directory.GetFiles(captures).Order().ToArray() : [];
    Check(before.SequenceEqual(after), "monitoring creates no CSV or settings snapshots");
}
Console.WriteLine($"Failures: {failures}");
return failures == 0 ? 0 : 1;
