using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

namespace ExtractEmbeddedSources;

internal class Program
{
    // ↓に Guid の定義がある
    // https://github.com/dotnet/roslyn/blob/main/src/Dependencies/CodeAnalysis.Debugging/PortableCustomDebugInfoKinds.cs
    private static readonly Guid s_embeddedSourceGuid = new("0E8A571B-6926-466E-B4AD-8AB04611F5FE");

    private static readonly Encoding s_outputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
    private const string Usage = "Usage: ExtractEmbeddedSources.exe [-o=<output directory>] <input file or directory>";

    static void Main(string[] args)
    {
        // 引数処理
        var parameters = args
            .Select(x => x.Split('=', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToLookup(x => x is [var key, _] ? key : string.Empty, x => x?.LastOrDefault(string.Empty));

        var input = parameters[string.Empty].LastOrDefault();
        var output = parameters["--output"].LastOrDefault() ?? parameters["-o"].LastOrDefault() ?? Environment.CurrentDirectory;
        var excludes = parameters["--exclude"].SelectMany(x => x?.Split(new[] { ',', ';' }) ?? Array.Empty<string>()).ToHashSet();
        var searchOption = parameters["--recursive"].Any() ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        // 必須引数がないと終了
        if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(output))
        {
            Console.WriteLine(Usage);
            return;
        }

        try
        {
            if (File.Exists(input))
            {
                ProcessFile(input, output);
            }
            else if (Directory.Exists(input))
            {
                var files = Directory.EnumerateFiles(input, "*.*", searchOption)
                    .Where(x => HasTargetExtension(x) && !excludes.Any(x.Contains));

                foreach (var file in files)
                {
                    ProcessFile(file, output);
                }

                static bool HasTargetExtension(string path) => Path.GetExtension(path) is ".dll" or ".pdb";
            }
            else
            {
                Console.WriteLine($"ExtractEmbeddedSources.exe : file or directory '{input}' is not found.");
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"ExtractEmbeddedSources.exe : an error occured, {e.Message}");
            Environment.Exit(1);
        }
    }

    private static void ProcessFile(string input, string output)
    {
        try
        {
            var extension = Path.GetExtension(input);
            if (extension is ".dll")
            {
                using var stream = File.OpenRead(input);
                ProcessDllEmbeddedPortablePdb(stream, output);
            }
            else if (extension is ".pdb")
            {
                using var stream = File.OpenRead(input);
                ProcessPortablePdb(stream, output);
            }
            else
            {
                Console.WriteLine($"Skip: {input} (not dll or pdb)");
            }
        }
        catch (BadImageFormatException)
        {
            // PortablePdb 以外が対象になった場合 BadImageFormat
            Console.WriteLine($"Skip: {input} (not supported image)");
        }
    }

    /// <summary>PortablePdb を処理する</summary>
    /// <exception cref="BadImageFormatException">フォーマット違い</exception>
    private static void ProcessPortablePdb(Stream stream, string outputDir)
    {
        var mrp = MetadataReaderProvider.FromPortablePdbStream(stream);
        var reader = mrp.GetMetadataReader(MetadataReaderOptions.Default);
        OutputEmbeddedSources(reader, outputDir);
    }

    /// <summary>PortablePdb を埋め込んだ Dll を処理する</summary>
    /// <exception cref="BadImageFormatException">フォーマット違い</exception>
    private static void ProcessDllEmbeddedPortablePdb(Stream stream, string outputDir)
    {
        using var peReader = new PEReader(stream);

        // dll のエントリから埋め込まれた pdb を探してくる
        var eppEntries = peReader.ReadDebugDirectory().Where(x => x.Type == DebugDirectoryEntryType.EmbeddedPortablePdb);
        foreach (var entry in eppEntries)
        {
            var provider = peReader.ReadEmbeddedPortablePdbDebugDirectoryData(entry);
            OutputEmbeddedSources(provider.GetMetadataReader(), outputDir);
        }
    }

    /// <summary>pdb に埋め込まれたソースファイルを抽出して出力する</summary>
    private static void OutputEmbeddedSources(MetadataReader metadataReader, string outputDir)
    {
        foreach (var documentHandle in metadataReader.Documents)
        {
            // ドキュメントごとに名前と中身を抽出
            var document = metadataReader.GetDocument(documentHandle);
            var documentName = metadataReader.GetString(document.Name);
            var source = GetEmbeddedSource(metadataReader, documentHandle);

            if (string.IsNullOrEmpty(documentName) || string.IsNullOrEmpty(source)) continue;

            var outputFile = documentName;
            try
            {
                // 表示用にフルパス化しておく
                outputFile = Path.GetFullPath(Path.Combine(outputDir, documentName));

                // File.WriteAllText は配置先のディレクトリ作成はしない
                var dir = Path.GetDirectoryName(outputFile);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir!);

                File.WriteAllText(outputFile, source);
                Console.WriteLine($"Extract: {outputFile}");
            }
            catch (Exception e)
            {
                Console.WriteLine($"Failed: {outputFile}, {e.Message}");
            }
        }
    }

    private static string? GetEmbeddedSource(MetadataReader metadataReader, DocumentHandle documentHandle)
    {
        foreach (var cdiHandle in metadataReader.GetCustomDebugInformation(documentHandle))
        {
            var cdi = metadataReader.GetCustomDebugInformation(cdiHandle);

            // CustomDebugInformation のうち EmbeddedSource を選び出す
            // EmbeddedSource の Guid は Roslyn 内（上記）にある
            if (metadataReader.GetGuid(cdi.Kind) != s_embeddedSourceGuid) continue;

            var blob = metadataReader.GetBlobBytes(cdi.Value);

            // blob の先頭 4byte は非圧縮の場合 0, 圧縮済みの場合は展開後のファイルサイズ
            var decompressedSize = BitConverter.ToInt32(blob);
            if (decompressedSize == 0) return s_outputEncoding.GetString(blob.AsSpan(sizeof(int))); // 4byte 読んでるのでずらす

            // 4byte 読んでるのでずらす
            using var blobStream = new MemoryStream(blob, sizeof(int), blob.Length - sizeof(int), false);
            using var deflateStream = new DeflateStream(blobStream, CompressionMode.Decompress, leaveOpen: true);
            using var inputStreamReader = new StreamReader(deflateStream, s_outputEncoding, true, decompressedSize, leaveOpen: true);
            return inputStreamReader.ReadToEnd();
        }

        return null;
    }
}

