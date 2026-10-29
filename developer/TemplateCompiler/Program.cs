using DojoFishing.Developer;

if (args.Length != 2) throw new ArgumentException("TemplateCompiler <source.wav> <output.bin>");
var template = TemplateExtractor.Extract(args[0]);
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
using (var stream = File.Create(args[1])) TemplateExtractor.Write(template, stream);
Console.WriteLine($"特徴量生成: {template.Frames.Length} frames / {template.DurationMs:F1}ms / {new FileInfo(args[1]).Length} bytes");
