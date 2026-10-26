using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Alife.Foundation;

namespace presents.Video;

/// <summary>视频基础信息（由 ffmpeg 探测得到）</summary>
public record VideoInfo(string FilePath, double DurationSeconds, int Width, int Height, string VideoCodec, string AudioCodec)
{
    public bool HasAudio => string.IsNullOrWhiteSpace(AudioCodec) == false;
}

/// <summary>
/// ffmpeg 工具封装：定位可执行文件、探测视频信息、按时间点抽取单帧、分块流式解码音轨。
/// 所有操作都在子进程中流式完成，视频文件永远不会被整体读入内存。
/// </summary>
public static class FfmpegTool
{
    /// <summary>定位 ffmpeg：配置路径 → 系统 PATH → python imageio-ffmpeg → 常见安装目录</summary>
    public static string Find(string? configuredPath = null)
    {
        string key = configuredPath ?? string.Empty;
        lock (findLock)
        {
            if (ffmpegCache.TryGetValue(key, out string? cached))
                return cached;
        }

        string? found = null;
        if (string.IsNullOrWhiteSpace(configuredPath) == false)
        {
            string cfg = configuredPath.Trim().Trim('"');
            if (Directory.Exists(cfg)) //允许直接填解压目录，自动补 bin/ffmpeg.exe
                cfg = Path.Combine(cfg, "bin", "ffmpeg.exe");
            if (File.Exists(cfg))
                found = cfg;
        }
        found ??= FindInPath();
        found ??= FindByPython();
        found ??= FindInCommonDirectories();

        if (found == null)
            throw new Exception(
                "未找到 ffmpeg。请在插件配置中填写 ffmpeg 路径，或在系统 PATH 中安装 ffmpeg" +
                "（插件已声明 imageio-ffmpeg，Alife 会自动安装，通常由“听觉能力/音频识别”插件一并带来）。");

        lock (findLock)
            ffmpegCache[key] = found;
        return found;
    }

    /// <summary>探测视频信息（时长、分辨率、编码）。时长解析失败时回退为整片扫描。</summary>
    public static async Task<VideoInfo> ProbeAsync(string ffmpeg, string filePath, CancellationToken ct = default)
    {
        //无输出文件时 ffmpeg 必定返回非零，信息全在错误流中
        ProcessResult result = await RunAsync(ffmpeg, ["-hide_banner", "-i", filePath], 60000, ct);
        string stderr = result.StandardError;

        double duration = ParseDuration(stderr);
        if (duration <= 0)
            duration = await ProbeDurationByScanAsync(ffmpeg, filePath, ct);

        Match size = Regex.Match(stderr, @"Video:.*?(\d{2,5})x(\d{2,5})");
        int width = size.Success ? int.Parse(size.Groups[1].Value) : 0;
        int height = size.Success ? int.Parse(size.Groups[2].Value) : 0;

        string videoCodec = MatchCodec(stderr, "Video");
        string audioCodec = MatchCodec(stderr, "Audio");

        return new VideoInfo(filePath, duration, width, height, videoCodec, audioCodec);
    }

    /// <summary>
    /// 沿时间轴抽取单帧（前定位 -ss 快速 seek，只解码一帧即刻退出）。
    /// 内存占用与视频大小无关，输出只是一张 jpg。
    /// </summary>
    public static async Task ExtractFrameAsync(
        string ffmpeg, string filePath, double timestamp, int maxWidth, string outputPath, CancellationToken ct = default)
    {
        string time = timestamp.ToString("0.###", CultureInfo.InvariantCulture);
        List<string> args =
        [
            "-hide_banner", "-loglevel", "error",
            "-ss", time, "-i", filePath,
            "-frames:v", "1", "-q:v", "2", "-y", outputPath
        ];
        if (maxWidth > 0)
            args.InsertRange(7, ["-vf", $"scale='min({maxWidth},iw)':-2"]);

        ProcessResult result = await RunAsync(ffmpeg, args, 180000, ct);
        if (IsFileReady(outputPath))
            return;

        //老版本 ffmpeg 可能不支持 scale 表达式，去掉过滤器再试一次
        if (maxWidth > 0)
        {
            args.RemoveRange(7, 2);
            result = await RunAsync(ffmpeg, args, 180000, ct);
            if (IsFileReady(outputPath))
                return;
        }

        throw new Exception($"抽帧失败（t={time}s）：{TrimError(result.StandardError)}");
    }

    /// <summary>
    /// 流式解码音轨为 16kHz 单声道 float，并按块回调（每块 chunkSamples 个采样）。
    /// 采样缓冲区复用，任意时刻内存中只有一块音频，与视频时长无关。
    /// </summary>
    /// <returns>解码出的采样总数</returns>
    public static async Task<long> DecodeAudioChunksAsync(
        string ffmpeg, string filePath, int chunkSamples,
        Func<float[], int, double, Task> onChunk, CancellationToken ct = default)
    {
        string[] args =
        [
            "-hide_banner", "-loglevel", "error",
            "-i", filePath, "-vn", "-ar", "16000", "-ac", "1", "-f", "f32le", "pipe:1"
        ];

        byte[] byteBuffer = new byte[chunkSamples * 4];
        float[] sampleBuffer = new float[chunkSamples];
        long totalSamples = 0;
        int filled = 0;
        string? error = null;

        await RunAsync(ffmpeg, args, 7200000, ct, async reader =>
        {
            Stream stdout = reader.BaseStream; //绕过文本解码，直接读取原始 PCM
            while (true)
            {
                int read = await stdout.ReadAsync(byteBuffer.AsMemory(filled, byteBuffer.Length - filled), ct);
                if (read <= 0)
                    break;
                filled += read;
                if (filled < byteBuffer.Length)
                    continue;

                totalSamples = await Push(totalSamples);
                filled = 0;
            }

            if (filled >= 4)
                totalSamples = await Push(totalSamples);

            filled = 0;

            async Task<long> Push(long total)
            {
                int bytes = filled - filled % 4;
                Buffer.BlockCopy(byteBuffer, 0, sampleBuffer, 0, bytes);
                int count = bytes / 4;
                double startSeconds = total / 16000.0;
                await onChunk(sampleBuffer, count, startSeconds);
                return total + count;
            }
        }, stderr => error = stderr);

        if (totalSamples <= 0)
            throw new Exception($"音轨解码失败（可能该视频没有音轨）：{TrimError(error)}");

        return totalSamples;
    }

    /// <summary>把一段音频转码为 mp3（用于在线转写上传，码率越高识别越准）</summary>
    public static async Task<bool> TranscodeToMp3Async(
        string ffmpeg, string inputPath, string outputPath, int bitrateKbps, CancellationToken ct = default)
    {
        ProcessResult result = await RunAsync(ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-y", "-i", inputPath, "-b:a", $"{bitrateKbps}k", outputPath],
            120000, ct);
        return result.ExitCode == 0 && IsFileReady(outputPath);
    }

    /// <summary>把 16kHz 单声道 float 采样写成 wav 文件（用于上传转写）</summary>
    public static async Task WriteWavAsync(string path, float[] samples, int length, CancellationToken ct = default)
    {
        const int SampleRate = 16000;
        await using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);

        byte[] header = new byte[44];
        void Write(int offset, string text) => Encoding.ASCII.GetBytes(text).CopyTo(header, offset);
        void WriteInt(int offset, int value) => BitConverter.GetBytes(value).CopyTo(header, offset);

        int dataLength = length * 2;
        Write(0, "RIFF");
        WriteInt(4, 36 + dataLength);
        Write(8, "WAVE");
        Write(12, "fmt ");
        WriteInt(16, 16); //fmt chunk 长度
        WriteInt(20, 1); //PCM
        WriteInt(22, 1); //单声道
        WriteInt(24, SampleRate);
        WriteInt(28, SampleRate * 2); //字节率
        WriteInt(32, 2); //块对齐
        WriteInt(34, 16); //位深
        Write(36, "data");
        WriteInt(40, dataLength);
        await stream.WriteAsync(header, ct);

        int blockSamples = 16000;
        byte[] buffer = new byte[blockSamples * 2];
        for (int offset = 0; offset < length; offset += blockSamples)
        {
            int count = Math.Min(blockSamples, length - offset);
            for (int i = 0; i < count; i++)
            {
                float sample = Math.Clamp(samples[offset + i], -1f, 1f);
                short value = (short)(sample * short.MaxValue);
                buffer[i * 2] = (byte)(value & 0xFF);
                buffer[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
            }

            await stream.WriteAsync(buffer.AsMemory(0, count * 2), ct);
        }
    }

    internal record ProcessResult(int ExitCode, string StandardError);

    static readonly Lock findLock = new();
    static readonly Dictionary<string, string> ffmpegCache = new();

    internal static async Task<ProcessResult> RunAsync(
        string ffmpeg, IEnumerable<string> args, int timeoutMs, CancellationToken ct,
        Func<StreamReader, Task>? stdoutConsumer = null, Action<string>? stderrSink = null)
    {
        ProcessStartInfo psi = new(ffmpeg) {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = stdoutConsumer != null,
            StandardErrorEncoding = Encoding.UTF8,
            // StandardOutputEncoding 仅在重定向标准输出时有效；无消费者时不重定向，
            // 必须置 null，否则 Process.Start 抛“StandardOutputEncoding is only supported...”
            StandardOutputEncoding = stdoutConsumer != null ? Encoding.UTF8 : null,
        };
        foreach (string arg in args)
            psi.ArgumentList.Add(arg);

        using Process process = new() { StartInfo = psi };
        process.Start();

        using CancellationTokenSource timeoutSource = new(timeoutMs);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutSource.Token);
        try
        {
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            Task? stdoutTask = stdoutConsumer?.Invoke(process.StandardOutput);

            await process.WaitForExitAsync(linked.Token);

            string stderr = await stderrTask;
            if (stdoutTask != null)
                await stdoutTask;
            stderrSink?.Invoke(stderr);

            return new ProcessResult(process.ExitCode, stderr);
        }
        catch (OperationCanceledException)
        {
            SafeKill(process);
            if (ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);
            throw new TimeoutException($"ffmpeg 执行超时（{timeoutMs} ms）：{string.Join(' ', args)}");
        }
        finally
        {
            SafeKill(process);
        }
    }

    static void SafeKill(Process process)
    {
        try
        {
            if (process.HasExited == false)
                process.Kill(true);
        }
        catch
        {
            // 进程可能刚退出，忽略
        }
    }

    static double ParseDuration(string stderr)
    {
        Match match = Regex.Match(stderr, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
        if (match.Success == false)
            return 0;
        if (match.Groups[1].Value == "N/A")
            return 0;

        double hours = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        double minutes = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        double seconds = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        return hours * 3600 + minutes * 60 + seconds;
    }

    /// <summary>兜底方案：整片空转解码（输出到 null），从进度信息中取最终时间。不占内存，只耗 CPU。</summary>
    static async Task<double> ProbeDurationByScanAsync(string ffmpeg, string filePath, CancellationToken ct)
    {
        long lastMs = 0;
        await RunAsync(ffmpeg,
            ["-hide_banner", "-nostats", "-progress", "pipe:1", "-i", filePath, "-f", "null", "-"],
            7200000, ct,
            async stdout => {
                while (await stdout.ReadLineAsync(ct) is { } line)
                {
                    if (line.StartsWith("out_time_ms=", StringComparison.Ordinal))
                    {
                        if (long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out long ms))
                            lastMs = Math.Max(lastMs, ms);
                    }
                    else if (line.StartsWith("out_time=", StringComparison.Ordinal))
                    {
                        Match match = Regex.Match(line, @"(\d+):(\d+):(\d+(?:\.\d+)?)");
                        if (match.Success)
                        {
                            double value = double.Parse(match.Groups[1].Value) * 3600
                                           + double.Parse(match.Groups[2].Value) * 60
                                           + double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
                            lastMs = Math.Max(lastMs, (long)(value * 1000));
                        }
                    }
                }
            });

        return lastMs / 1000.0;
    }

    static string MatchCodec(string stderr, string streamType)
    {
        Match match = Regex.Match(stderr, streamType + @":\s*([a-z0-9_]+)");
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    static bool IsFileReady(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    static string TrimError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return "无错误信息";
        error = error.Trim();
        return error.Length > 300 ? error[^300..] : error;
    }

    static string? FindInPath()
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
            return null;

        foreach (string dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(dir.Trim().Trim('"'), "ffmpeg.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
                // 忽略无效路径
            }
        }

        return null;
    }

    static string? FindByPython()
    {
        try
        {
            // ★ 本机补丁：优先用 ALIFE_PYTHON 指定的解释器（裸名 python 会落到 3.15，没装 imageio_ffmpeg）
            ProcessStartInfo psi = new(System.Environment.GetEnvironmentVariable("ALIFE_PYTHON") ?? "python",
                "-c \"import imageio_ffmpeg; print(imageio_ffmpeg.get_ffmpeg_exe())\"") {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using Process process = Process.Start(psi)!;
            string? output = process.StandardOutput.ReadLine()?.Trim();
            process.WaitForExit(15000);
            if (process.ExitCode == 0 && string.IsNullOrEmpty(output) == false && File.Exists(output))
                return output;
        }
        catch
        {
            // 未安装 python 或 imageio-ffmpeg
        }

        return null;
    }

    static string? FindInCommonDirectories()
    {
        string[] roots = [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages"),
            Path.Combine(AlifePath.RuntimeFolderPath, "Tools"),
            @"C:\ffmpeg\bin",
            @"C:\Program Files\ffmpeg\bin",
            @"C:\tools\ffmpeg\bin"
        ];

        foreach (string root in roots)
        {
            string? hit = ScanForFfmpeg(root);
            if (hit != null)
                return hit;
        }

        //各盘根目录下的 ffmpeg* 解压目录（如 E:\ffmpeg-9.0.2-essentials_build\bin\ffmpeg.exe）
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Network))
                continue;
            try
            {
                foreach (string dir in Directory.EnumerateDirectories(drive.RootDirectory.FullName, "ffmpeg*"))
                {
                    string? hit = ScanForFfmpeg(dir);
                    if (hit != null)
                        return hit;
                }
            }
            catch
            {
                // 忽略无权限/未就绪盘符
            }
        }

        return null;
    }

    /// <summary>在某个目录内查找 ffmpeg 可执行文件：优先 bin 子目录，再平铺，再整目录递归一层兜底。</summary>
    static string? ScanForFfmpeg(string root)
    {
        if (Directory.Exists(root) == false)
            return null;

        string[] candidates =
        [
            Path.Combine(root, "bin", "ffmpeg.exe"),
            Path.Combine(root, "ffmpeg.exe"),
            Path.Combine(root, "bin", "ffmpeg"),
            Path.Combine(root, "ffmpeg")
        ];
        foreach (string candidate in candidates)
            if (File.Exists(candidate))
                return candidate;

        try
        {
            return Directory.EnumerateFiles(root, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }
}
