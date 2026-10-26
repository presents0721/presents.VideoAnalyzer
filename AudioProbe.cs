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
using Microsoft.Extensions.Logging;

namespace presents.Video;

/// <summary>声学侦察结论：先判断「这条音轨里到底有没有人声」，再去转写</summary>
public record AudioProbeResult(
    bool HasAudio,
    double MeanVolumeDb,
    double MaxVolumeDb,
    AudioVerdict Verdict,
    string Summary,
    AudioFeatures? Features = null,
    IReadOnlyList<double>? SpeechOnsets = null)
{
    /// <summary>音轨不像人声时（静音/纯音/器乐为主），转写结果一定是幻觉，直接别跑</summary>
    public bool CanHaveSpeech => HasAudio && Verdict is AudioVerdict.Audible or AudioVerdict.VeryQuiet;
}

public enum AudioVerdict
{
    /// <summary>无音轨</summary>
    NoAudio,
    /// <summary>基本静音（90% 以上帧低于 -50 dBFS）</summary>
    Silent,
    /// <summary>音量很低，多半只有底噪或极轻的环境音</summary>
    VeryQuiet,
    /// <summary>纯音/提示音：某一谱峰吃满能量且无节奏，既不是音乐也不是人声</summary>
    Tonal,
    /// <summary>器乐/音乐为主，未见人声特征（纯 BGM）</summary>
    Musical,
    /// <summary>有声音，且声学量测未否定人声（是否清晰人声交给转写与幻觉过滤）</summary>
    Audible,
}

/// <summary>
/// 声学特征（与参考实现 audio_probe 同一套物理量，只做量测、不做识别）。
/// 这些数字会写进报告，方便事后解释「为什么判定为纯 BGM」。
/// </summary>
public record AudioFeatures(
    double SpectralFlatness,
    double SpectralCentroidHz,
    double Rolloff85Hz,
    double SpeechBandRatio,
    double TonalConcentration,
    double BeatBpm,
    double BeatAutocorrPeak,
    double LoudnessSwingDb,
    double SyllabicModulationRatio,
    double ActiveFrameRatio,
    double SilenceRatio);

/// <summary>
/// 声学侦察：用 ffmpeg 把音轨解成 16k 单声道，直接在 C# 里做短时傅里叶分析，
/// 零模型调用、零 token，判断音轨里是语音、音乐还是静音。
///
/// 为什么不能只看音量：纯 BGM 的音量和人声一样大，ASR 在没有语音的音轨上
/// 不会说「我没听到」，而是编出一句看着很像真的台词（参考实现实测三个互不重叠
/// 的时间窗输出了完全相同的伪造字幕）。所以必须有与语言模型无关的声学证据。
///
/// 判据（都是可复算的量）：
///   谱平坦度   = 功率谱几何均值 / 算术均值。噪声接近 1，乐音/语音有谐波 → 远小于 1
///   节拍自相关 = 起始强度包络在 60–200 BPM 延迟内的最大峰。音乐有稳定节拍 → 峰高
///   逐秒响度波动 = 连续音乐床近乎直线，语音有起停 → 波动大
///   85% 谱滚降 = 过低说明几乎没有高频瞬态
///   音节率调制 = 2–8 Hz 能量占比，语音偏高
/// </summary>
public static class AudioProbe
{
    const int SampleRate = 16000;   // 分析采样率（与参考实现一致）
    const int WindowSize = 2048;    // FFT 窗长
    const int Hop = 512;            // 帧移
    const double FrameRate = SampleRate / (double)Hop;  // 31.25 帧/秒
    const double SilenceDb = -50;   // 低于此值的帧算「静音帧」，不参与谱统计

    public static async Task<AudioProbeResult> ProbeAsync(
        string ffmpeg,
        string videoPath,
        bool hasAudioStream,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        if (hasAudioStream == false)
            return new AudioProbeResult(false, 0, 0, AudioVerdict.NoAudio, "该文件没有音轨，转写与听音环节已跳过。");

        try
        {
            Analyzer analyzer = new();
            bool decoded = await DecodeAndAnalyzeAsync(ffmpeg, videoPath, analyzer, ct);
            AudioFeatures? features = decoded ? analyzer.BuildFeatures() : null;

            if (features == null)
            {
                logger?.LogWarning("[视频理解] 声学侦察没解出足够音频，按「有声音」处理");
                return new AudioProbeResult(true, 0, 0, AudioVerdict.Audible,
                    "声学侦察未取到足够的音频数据，已按有声音继续。");
            }

            (AudioVerdict verdict, string reason, string confidence) = Judge(features, analyzer.MeanDb, analyzer.MaxDb);
            string metrics = features.ActiveFrameRatio > 0 && verdict != AudioVerdict.Musical
                ? $"谱平坦度 {features.SpectralFlatness:0.###}，节拍 {features.BeatBpm:0} BPM/峰 {features.BeatAutocorrPeak:0.##}，"
                  + $"响度波动 {features.LoudnessSwingDb:0.#} dB，85% 滚降 {features.Rolloff85Hz:0} Hz，"
                : "";
            string summary =
                $"{reason}（{metrics}均值 {analyzer.MeanDb:0.0} dB / 峰值 {analyzer.MaxDb:0.0} dB，置信 {confidence}）";

            logger?.LogInformation("[视频理解] 声学侦察：{Verdict}；{Summary}", verdict, summary);
            return new AudioProbeResult(true, analyzer.MeanDb, analyzer.MaxDb, verdict, summary, features,
                analyzer.FindSpeechOnsets());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            logger?.LogWarning(e, "[视频理解] 声学侦察失败，按有声音继续");
            return new AudioProbeResult(true, 0, 0, AudioVerdict.Audible, $"声学侦察失败（{e.Message}），已按有声音继续。");
        }
    }

    /// <summary>解码 + 逐帧量测；返回是否至少分析到 4 帧</summary>
    static async Task<bool> DecodeAndAnalyzeAsync(
        string ffmpeg, string videoPath, Analyzer analyzer, CancellationToken ct)
    {
        ProcessStartInfo startInfo = new(ffmpeg) {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in new[] {
                     "-hide_banner", "-nostats", "-loglevel", "error",
                     "-i", videoPath,
                     "-map", "0:a:0", "-vn",
                     "-ac", "1", "-ar", SampleRate.ToString(CultureInfo.InvariantCulture),
                     "-f", "s16le", "-"
                 })
            startInfo.ArgumentList.Add(argument);

        using Process process = Process.Start(startInfo) ?? throw new Exception("无法启动 ffmpeg 进程");
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(ct);

        byte[] buffer = new byte[16384];
        int carry = -1;   // 跨块被切断的半个采样（低字节）
        try
        {
            Stream stdout = process.StandardOutput.BaseStream;
            while (true)
            {
                int read = await stdout.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                if (read <= 0)
                    break;
                for (int i = 0; i < read; i++)
                {
                    int b = buffer[i];
                    if (carry < 0)
                    {
                        carry = b;
                        continue;
                    }
                    short sample = (short)((carry & 0xFF) | (b << 8));
                    carry = -1;
                    analyzer.Feed(sample / 32768f);
                }
            }

            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (process.HasExited == false)
                    process.Kill(true);
            }
            catch
            {
                // 进程已退出或无法终止
            }
            throw;
        }

        _ = await stderrTask;
        return analyzer.FrameCount >= 4;
    }

    /// <summary>判型：优先用与语言模型无关的声学证据，证据不足时保守地交给转写</summary>
    static (AudioVerdict Verdict, string Reason, string Confidence) Judge(
        AudioFeatures f, double meanDb, double maxDb)
    {
        if (f.SilenceRatio > 0.9)
            return (AudioVerdict.Silent,
                "音轨基本静音（90% 以上帧低于 -50 dBFS），已跳过语音转写——这种音轨上强行转写只会产出伪造台词",
                "高");

        // 纯音/提示音：单一谱峰吃满能量且毫无节拍起伏，既不是音乐也不是人声
        if (f.TonalConcentration >= 0.25 && f.BeatAutocorrPeak < 0.3)
            return (AudioVerdict.Tonal,
                "音轨是纯音/提示音（单一谱峰占主导、无节奏结构），已跳过语音转写",
                "中");

        List<string> musical = new();
        if (f.BeatAutocorrPeak >= 0.5)
            musical.Add($"有稳定节拍（{f.BeatBpm:0} BPM，自相关峰 {f.BeatAutocorrPeak:0.##}）");
        if (f.SpectralFlatness <= 0.02)
            musical.Add($"强谐波结构（谱平坦度 {f.SpectralFlatness:0.####}，噪声趋近 1）");
        if (f.LoudnessSwingDb <= 10)
            musical.Add($"逐秒响度波动仅 {f.LoudnessSwingDb:0.#} dB（像连续音乐床，缺少语音起停）");
        if (f.Rolloff85Hz <= 1200)
            musical.Add($"85% 谱滚降只有 {f.Rolloff85Hz:0} Hz（几乎无高频瞬态）");

        if (musical.Count >= 3)
            return (AudioVerdict.Musical,
                $"音轨是器乐/音乐为主，未见人声特征，已跳过语音转写：{string.Join("；", musical)}",
                musical.Count >= 4 ? "高" : "中");

        if (meanDb <= -35)
            return (AudioVerdict.VeryQuiet,
                $"音轨音量很低（均值 {meanDb:0.0} dB / 峰值 {maxDb:0.0} dB），可能只有底噪或极轻的环境音，转写结果请谨慎采信",
                "中");

        if (musical.Count == 2)
            return (AudioVerdict.Audible,
                $"音轨疑似音乐（{string.Join("；", musical)}），证据不足以下定论，仍交给转写并以文本/VAD 复核",
                "低");

        return (AudioVerdict.Audible,
            "声学量测未否定人声，已交给转写；是否为清晰人声以转写文本与幻觉过滤为准",
            "低");
    }

    /// <summary>流式短时分析器：逐样点喂入，按 2048 窗 / 512 帧移计算特征</summary>
    sealed class Analyzer
    {
        readonly float[] window = new float[WindowSize];
        readonly float[] real = new float[WindowSize];
        readonly float[] imag = new float[WindowSize];
        readonly float[] hann = new float[WindowSize];
        readonly float[] previousMagnitude = new float[WindowSize / 2 + 1];
        readonly List<double> flux = new();
        readonly List<double> frameDb = new();

        int filled;
        double sumFlatness;
        double sumCentroid;
        double sumRolloff;
        double sumSpeechBand;
        double sumTonal;
        double sumZcr;
        int activeFrames;

        public Analyzer()
        {
            for (int i = 0; i < WindowSize; i++)
                hann[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (WindowSize - 1)));
        }

        public int FrameCount => frameDb.Count;
        public double MeanDb => frameDb.Count == 0 ? -99 : frameDb.Average();
        public double MaxDb => frameDb.Count == 0 ? -99 : frameDb.Max();

        /// <summary>
        /// 语音起始点（秒）：响度在 300ms 内抬升超过 6 dB 的时刻。
        /// 只用来把抽帧对齐到「讲师开口讲这一页」的瞬间，不额外增加帧数。
        /// </summary>
        public IReadOnlyList<double> FindSpeechOnsets(double minGapSeconds = 1.5)
        {
            List<double> onsets = new();
            int baselineFrames = Math.Max(1, (int)Math.Round(0.3 * FrameRate));
            if (frameDb.Count <= baselineFrames)
                return onsets;

            const double floorDb = -45;   // 低于此值算底噪，不当开口
            const double riseDb = 6;      // 相对前 300ms 的抬升量
            double last = -minGapSeconds;
            for (int i = baselineFrames; i < frameDb.Count; i++)
            {
                double db = frameDb[i];
                if (db < floorDb)
                    continue;

                double baseline = 0;
                for (int j = i - baselineFrames; j < i; j++)
                    baseline += frameDb[j];
                baseline /= baselineFrames;
                if (db - baseline < riseDb)
                    continue;

                double time = i * Hop / (double)SampleRate;
                if (time - last < minGapSeconds)
                    continue;

                onsets.Add(time);
                last = time;
            }
            return onsets;
        }

        public void Feed(float sample)
        {
            window[filled++] = sample;
            if (filled < WindowSize)
                return;
            ProcessFrame();
            Array.Copy(window, Hop, window, 0, WindowSize - Hop);
            filled = WindowSize - Hop;
        }

        void ProcessFrame()
        {
            double sumSquares = 0;
            int zeroCrossings = 0;
            bool previousPositive = window[0] > 0;
            for (int i = 0; i < WindowSize; i++)
            {
                float v = window[i];
                sumSquares += v * v;
                bool positive = v > 0;
                if (i > 0 && positive != previousPositive)
                    zeroCrossings++;
                previousPositive = positive;
                real[i] = v * hann[i];
                imag[i] = 0;
            }

            Fft.Transform(real, imag);

            double rms = Math.Sqrt(sumSquares / WindowSize + 1e-12);
            double db = 20 * Math.Log10(rms + 1e-12);
            frameDb.Add(db);

            int bins = WindowSize / 2 + 1;
            double total = 0;
            for (int k = 0; k < bins; k++)
            {
                double re = real[k];
                double im = imag[k];
                double power = re * re + im * im + 1e-12;
                previousMagnitude[k] = (float)Math.Sqrt(power);
                total += power;
            }

            // 起始强度（正谱通量）：这一帧比上一帧「多出来」的能量，音乐/语音的起音都在这里
            double currentFlux = 0;
            for (int k = 0; k < bins; k++)
                currentFlux += Math.Max(0, previousMagnitude[k] - lastMagnitude[k]);
            for (int k = 0; k < bins; k++)
                lastMagnitude[k] = previousMagnitude[k];
            flux.Add(currentFlux);

            if (db <= SilenceDb)
                return;

            activeFrames++;
            double logSum = 0;
            double weighted = 0;
            double peak = 0;
            double bandEnergy = 0;
            double cumulative = 0;
            int rolloffIndex = 0;
            bool rolloffFound = false;
            double binHz = SampleRate / (double)WindowSize;

            for (int k = 0; k < bins; k++)
            {
                double power = real[k] * real[k] + imag[k] * imag[k] + 1e-12;
                logSum += Math.Log(power);
                weighted += power * k * binHz;
                if (power > peak)
                    peak = power;
                double frequency = k * binHz;
                if (frequency >= 300 && frequency <= 3400)
                    bandEnergy += power;
                cumulative += power;
                if (!rolloffFound && cumulative >= 0.85 * total)
                {
                    rolloffIndex = k;
                    rolloffFound = true;
                }
            }

            sumFlatness += Math.Exp(logSum / bins) / (total / bins);
            sumCentroid += weighted / total;
            sumRolloff += rolloffIndex * binHz;
            sumSpeechBand += bandEnergy / total;
            sumTonal += peak / total;
            sumZcr += zeroCrossings / (double)WindowSize;
        }

        readonly float[] lastMagnitude = new float[WindowSize / 2 + 1];

        public AudioFeatures? BuildFeatures()
        {
            if (frameDb.Count < 4)
                return null;

            int total = frameDb.Count;
            int active = Math.Max(1, activeFrames);
            double silenceRatio = frameDb.Count(d => d < SilenceDb) / (double)total;
            double activeRatio = activeFrames / (double)total;

            // 逐秒响度波动：语音有起停，连续音乐床几乎是直线
            int perSecond = Math.Max(1, (int)Math.Round(FrameRate));
            var seconds = new List<double>();
            for (int i = 0; i < frameDb.Count; i += perSecond)
            {
                double sum = 0;
                int count = 0;
                for (int j = i; j < Math.Min(frameDb.Count, i + perSecond); j++)
                {
                    sum += frameDb[j];
                    count++;
                }
                seconds.Add(sum / count);
            }
            double swing = 0;
            if (seconds.Count > 2)
            {
                seconds.Sort();
                swing = Percentile(seconds, 0.95) - Percentile(seconds, 0.05);
            }

            double beatPeak = 0;
            double bpm = 0;
            double syllabicRatio = 0;
            if (flux.Count >= 16)
            {
                double mean = flux.Average();
                double[] centred = flux.Select(v => v - mean).ToArray();

                // 自相关用 FFT 算（直接 O(n²) 对长视频太慢）
                double[] correlation = Fft.Autocorrelate(centred);
                double zero = correlation.Length > 0 ? correlation[0] : 0;
                if (zero > 0)
                {
                    int lo = Math.Max(1, (int)Math.Round(FrameRate * 60 / 200));
                    int hi = Math.Min(correlation.Length - 1, Math.Max(lo + 1, (int)Math.Round(FrameRate * 60 / 60)));
                    int bestLag = lo;
                    double best = 0;
                    for (int lag = lo; lag <= hi; lag++)
                    {
                        if (correlation[lag] > best)
                        {
                            best = correlation[lag];
                            bestLag = lag;
                        }
                    }
                    beatPeak = best / zero;
                    bpm = beatPeak > 0 ? 60 * FrameRate / bestLag : 0;
                }

                // 音节率调制：起始包络在 2–8 Hz 的能量占比（语音偏高，慢节拍音乐偏低）
                double[] spectrum = Fft.MagnitudeSpectrum(centred);
                double syllable = 0;
                double rhythm = 0;
                // 实数信号补零后的谱长 = 补零长度/2 + 1，因此每个 bin 对应 fps / 补零长度
                double binHz = FrameRate / ((spectrum.Length - 1) * 2.0);
                for (int i = 1; i < spectrum.Length; i++)
                {
                    double frequency = i * binHz;
                    if (frequency >= 2 && frequency <= 8)
                        syllable += spectrum[i];
                    else if (frequency >= 0.5 && frequency < 2)
                        rhythm += spectrum[i];
                }
                syllabicRatio = syllable / (syllable + rhythm + 1e-12);
            }

            return new AudioFeatures(
                sumFlatness / active,
                sumCentroid / active,
                sumRolloff / active,
                sumSpeechBand / active,
                sumTonal / active,
                bpm,
                beatPeak,
                swing,
                syllabicRatio,
                activeRatio,
                silenceRatio);
        }

        static double Percentile(List<double> sorted, double q)
        {
            if (sorted.Count == 0)
                return 0;
            double position = (sorted.Count - 1) * q;
            int lower = (int)Math.Floor(position);
            int upper = Math.Min(sorted.Count - 1, lower + 1);
            double weight = position - lower;
            return sorted[lower] * (1 - weight) + sorted[upper] * weight;
        }
    }
}

/// <summary>
/// 最小 FFT 实现（radix-2），只为本插件做声学量测用，不引入第三方依赖。
/// </summary>
static class Fft
{
    /// <summary>原地基 2 快速傅里叶变换（长度必须是 2 的幂）</summary>
    public static void Transform(float[] real, float[] imag)
    {
        int n = real.Length;
        if (n <= 1)
            return;

        // 位反转置换
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; j >= bit; bit >>= 1)
                j -= bit;
            j += bit;
            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imag[i], imag[j]) = (imag[j], imag[i]);
            }
        }

        for (int length = 2; length <= n; length <<= 1)
        {
            double angle = -2 * Math.PI / length;
            double wRe = Math.Cos(angle);
            double wIm = Math.Sin(angle);
            for (int i = 0; i < n; i += length)
            {
                double curRe = 1;
                double curIm = 0;
                for (int j = 0; j < length / 2; j++)
                {
                    int evenIndex = i + j;
                    int oddIndex = i + j + length / 2;
                    float oddRe = (float)(real[oddIndex] * curRe - imag[oddIndex] * curIm);
                    float oddIm = (float)(real[oddIndex] * curIm + imag[oddIndex] * curRe);
                    real[oddIndex] = real[evenIndex] - oddRe;
                    imag[oddIndex] = imag[evenIndex] - oddIm;
                    real[evenIndex] += oddRe;
                    imag[evenIndex] += oddIm;
                    double nextRe = curRe * wRe - curIm * wIm;
                    curIm = curRe * wIm + curIm * wRe;
                    curRe = nextRe;
                }
            }
        }
    }

    /// <summary>自相关：补零到 2 的幂，正变换 → 幅度平方 → 逆变换，取前半段</summary>
    public static double[] Autocorrelate(double[] signal)
    {
        int size = NextPowerOfTwo(signal.Length * 2);
        float[] real = new float[size];
        float[] imag = new float[size];
        for (int i = 0; i < signal.Length; i++)
            real[i] = (float)signal[i];

        Transform(real, imag);
        for (int i = 0; i < size; i++)
        {
            double power = real[i] * (double)real[i] + imag[i] * (double)imag[i];
            real[i] = (float)power;
            imag[i] = 0;
        }
        Inverse(real, imag);

        double[] result = new double[signal.Length];
        for (int i = 0; i < result.Length; i++)
            result[i] = real[i];
        return result;
    }

    /// <summary>幅度谱（补零到 2 的幂）</summary>
    public static double[] MagnitudeSpectrum(double[] signal)
    {
        int size = NextPowerOfTwo(signal.Length);
        float[] real = new float[size];
        float[] imag = new float[size];
        for (int i = 0; i < signal.Length; i++)
            real[i] = (float)signal[i];

        Transform(real, imag);
        double[] magnitude = new double[size / 2 + 1];
        for (int i = 0; i < magnitude.Length; i++)
            magnitude[i] = Math.Sqrt(real[i] * (double)real[i] + imag[i] * (double)imag[i]);
        return magnitude;
    }

    static void Inverse(float[] real, float[] imag)
    {
        // 共轭 → 正变换 → 共轭 → 除以长度
        for (int i = 0; i < imag.Length; i++)
            imag[i] = -imag[i];
        Transform(real, imag);
        for (int i = 0; i < imag.Length; i++)
            imag[i] = -imag[i] / real.Length;
        for (int i = 0; i < real.Length; i++)
            real[i] /= real.Length;
    }

    static int NextPowerOfTwo(int value)
    {
        int size = 1;
        while (size < value)
            size <<= 1;
        return size;
    }
}

/// <summary>
/// 转写幻觉过滤。两类证据都会让转写整段不可信：
/// 1) 整行就是聊天模型常见的模板收尾（「请不吝点赞订阅转发」这类），或整行只有一个字/标点；
/// 2) 互不重叠的时间窗输出了完全相同的文本——正常说话不可能这样复读（哪怕只出现两次）。
/// </summary>
public static class TranscriptSanitizer
{
    static readonly string[] Templates = {
        "请不吝点赞", "点赞订阅", "订阅转发", "打赏", "字幕由", "字幕志愿者", "字幕组",
        "amara.org", "ming pao", "明镜", "点点栏目", "感谢观看", "谢谢观看", "谢谢收看",
        "thanks for watching", "subtitles by", "subscribe", "like and subscribe",
        "请订阅", "关注我", "一键三连",
    };

    public static bool IsBoilerplate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return true;

        string normalized = Normalize(text);
        if (normalized.Length <= 1)
            return true;
        if (normalized.Length <= 40)
        {
            foreach (string template in Templates)
                if (normalized.Contains(template, StringComparison.OrdinalIgnoreCase))
                    return true;
        }

        //全行同一个字（「啊啊啊啊」这种驻波幻觉）或没有实际字词
        return normalized.Length > 1 && normalized.All(character => character == normalized[0]);
    }

    /// <summary>丢掉模板句与跨窗口复读，返回过滤后的结果与说明</summary>
    public static TranscriptResult Sanitize(TranscriptResult result)
    {
        if (result.Lines.Count == 0)
            return result;

        List<TranscriptLine> kept = result.Lines
            .Where(line => IsBoilerplate(line.Text) == false)
            .ToList();
        int boilerplateCount = result.Lines.Count - kept.Count;

        // 正常说话不会一字不差地复读：够长（≥8 字）的文本出现两次就足够可疑，
        // 短句（「对」「嗯」）允许重复一次，出现三次才丢——避免误删正常的口头禅。
        var duplicateGroups = kept
            .GroupBy(line => Normalize(line.Text))
            .Where(group => group.Count() >= 3 || (group.Count() >= 2 && group.Key.Length >= 8))
            .ToList();
        int duplicatedCount = duplicateGroups.Sum(group => group.Count());
        if (duplicatedCount > 0)
        {
            HashSet<string> hallucinated = duplicateGroups.Select(group => group.Key).ToHashSet();
            kept = kept.Where(line => hallucinated.Contains(Normalize(line.Text)) == false).ToList();
        }

        List<string> notes = new();
        if (string.IsNullOrWhiteSpace(result.Note) == false)
            notes.Add(result.Note);
        if (boilerplateCount > 0)
            notes.Add($"已丢弃 {boilerplateCount} 行模板化幻觉字幕");
        if (duplicatedCount > 0)
            notes.Add($"已丢弃 {duplicatedCount} 行跨时间窗复读的幻觉字幕（正常说话不会一字不差地重复）");

        return result with { Lines = kept, Note = notes.Count == 0 ? null : string.Join("；", notes) };
    }

    static string Normalize(string text)
    {
        StringBuilder builder = new(text.Length);
        foreach (char character in text)
        {
            if (char.IsWhiteSpace(character))
                continue;
            if (char.IsPunctuation(character) || char.IsSymbol(character))
                continue;
            builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString();
    }
}
