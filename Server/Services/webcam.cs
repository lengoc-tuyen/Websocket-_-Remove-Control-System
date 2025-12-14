using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Server.Configuration;
using Server.helper;

#if OPENCV
using OpenCvSharp;
#endif

namespace Server.Services
{
    /// <summary>
    /// Service for webcam and screen capture operations
    /// </summary>
    public class WebcamService : IDisposable
    {
        private readonly ILogger<WebcamService> _logger;
        private readonly WebcamSettings _settings;
#if OPENCV
        private VideoCapture? _webcamCapture;
#endif
        private readonly object _lock = new();
        private bool _disposed;

        // Windows-specific screen capture (only loaded on Windows)
        private static class Win32Native
        {
            [DllImport("user32.dll")]
            public static extern int GetSystemMetrics(int nIndex);
            
            public const int SM_CXSCREEN = 0;
            public const int SM_CYSCREEN = 1;
        }

        public WebcamService(ILogger<WebcamService> logger, IOptions<AppSettings> settings)
        {
            _logger = logger;
            _settings = settings.Value.Webcam;
        }

        #region Public API

        /// <summary>
        /// Capture screenshot of the current screen
        /// </summary>
        public byte[] CaptureScreen()
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    return CaptureScreenWindows();
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    return CaptureScreenMacOS();
                }
                else
                {
                    return CaptureScreenLinux();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error capturing screen");
                return Array.Empty<byte>();
            }
        }

        /// <summary>
        /// Open webcam and capture proof video frames
        /// </summary>
        public async Task<List<byte[]>> RequestWebcamProof(int frameRate, CancellationToken cancellationToken)
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    return await CaptureVideoFramesLinux(_settings.ProofDurationMs / 1000.0, frameRate, cancellationToken);
                }

#if OPENCV
                if (OpenWebcamInternal())
                {
                    return await CaptureVideoFramesOpenCv(_settings.ProofDurationMs, frameRate, cancellationToken);
                }
#endif

                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    _logger.LogWarning("OpenCV webcam unavailable; falling back to FFmpeg proof capture on macOS.");
                    return await CaptureVideoFramesMacOS(_settings.ProofDurationMs / 1000.0, frameRate, cancellationToken);
                }

                _logger.LogWarning("Webcam proof not supported on this platform.");
                return new List<byte[]>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error capturing webcam proof");
                return new List<byte[]>();
            }
        }

        /// <summary>
        /// Capture a short batch of webcam frames for streaming.
        /// On Windows uses OpenCV; on macOS/Linux uses FFmpeg batch capture.
        /// </summary>
        public async Task<List<byte[]>> CaptureWebcamFramesBatch(double durationSec, int frameRate, CancellationToken cancellationToken)
        {
            try
            {
                if (durationSec <= 0)
                {
                    durationSec = 1;
                }

                var durationMs = (int)Math.Round(durationSec * 1000);
                return await CaptureVideoFrames(durationMs, frameRate, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error capturing webcam batch");
                return new List<byte[]>();
            }
        }

        /// <summary>
        /// Try capture a single webcam frame (JPEG) using OpenCV when available.
        /// </summary>
        public byte[]? TryCaptureSingleWebcamFrameOpenCv()
        {
#if OPENCV
            if (!OpenWebcamInternal())
            {
                return null;
            }

            return CaptureFrame();
#else
            return null;
#endif
        }

        /// <summary>
        /// Capture batch frames using FFmpeg only (fallback for macOS/Linux).
        /// </summary>
        public async Task<List<byte[]>> CaptureWebcamFramesBatchFfmpeg(double durationSec, int frameRate, CancellationToken cancellationToken)
        {
            try
            {
                if (durationSec <= 0)
                {
                    durationSec = 1;
                }

                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    return await CaptureVideoFramesMacOS(durationSec, frameRate, cancellationToken);
                }

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    return await CaptureVideoFramesLinux(durationSec, frameRate, cancellationToken);
                }

                var durationMs = (int)Math.Round(durationSec * 1000);
                return await CaptureVideoFramesOpenCv(durationMs, frameRate, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error capturing webcam batch (FFmpeg fallback)");
                return new List<byte[]>();
            }
        }

        /// <summary>
        /// Stream webcam frames with backpressure: keeps only the latest frame (DropOldest, capacity=1).
        /// </summary>
        public ChannelReader<byte[]> StreamWebcamFramesFfmpegLatest(int fps, CancellationToken ct)
        {
            fps = Math.Clamp(fps, 1, 15);

            var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true
            });

            _ = Task.Run(async () =>
            {
                await StreamWebcamFramesFfmpegWorker(fps, channel.Writer, ct);
            }, CancellationToken.None);

            return channel.Reader;
        }

        private async Task StreamWebcamFramesFfmpegWorker(int fps, ChannelWriter<byte[]> writer, CancellationToken ct)
        {
            var startedAny = false;
            var processes = CreateFfmpegWebcamProcessCandidates(fps);

            for (var candidateIndex = 0; candidateIndex < processes.Count; candidateIndex++)
            {
                var process = processes[candidateIndex];
                var started = false;
                Task? stderrDrain = null;
                Task? readTask = null;
                var firstFrameTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                try
                {
                    if (process.StartInfo.FileName.Length == 0)
                    {
                        continue;
                    }

                    _logger.LogInformation("Starting FFmpeg webcam candidate {Index}/{Total}", candidateIndex + 1, processes.Count);
                    started = process.Start();
                    if (!started)
                    {
                        continue;
                    }

                    startedAny = true;
                    _logger.LogDebug("FFmpeg webcam cmd: {Cmd} {Args}", process.StartInfo.FileName, process.StartInfo.Arguments);
                    _logger.LogInformation("FFmpeg webcam stream started, PID: {PID}", process.Id);

                    stderrDrain = Task.Run(async () =>
                    {
                        try
                        {
                            var err = await process.StandardError.ReadToEndAsync(ct);
                            if (!string.IsNullOrWhiteSpace(err))
                            {
                                _logger.LogWarning("FFmpeg stderr: {Err}", err);
                            }
                        }
                        catch { }
                    }, CancellationToken.None);

                    readTask = ReadMjpegFramesFromStream(process.StandardOutput.BaseStream, writer, ct, firstFrameTcs);

                    // If no frame is produced quickly, try next candidate.
                    var handshake = await Task.WhenAny(firstFrameTcs.Task, Task.Delay(TimeSpan.FromSeconds(2), ct));

                    if (handshake != firstFrameTcs.Task)
                    {
                        // No frame in time; if process already exited, likely bad options.
                        if (process.HasExited)
                        {
                            continue;
                        }

                        // Still running but no frames; treat as failure for this candidate.
                        continue;
                    }

                    // Success: keep streaming until cancellation/end.
                    await readTask;
                    writer.TryComplete();
                    return;
                }
                catch (OperationCanceledException)
                {
                    writer.TryComplete();
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "FFmpeg webcam candidate failed");
                }
                finally
                {
                    try
                    {
                        if (started && !process.HasExited)
                        {
                            process.Kill(true);
                            await process.WaitForExitAsync(CancellationToken.None);
                        }
                    }
                    catch { }

                    if (readTask != null)
                    {
                        try { await readTask; } catch { }
                    }

                    try { process.Dispose(); } catch { }
                }
            }

            if (!startedAny)
            {
                writer.TryComplete(new InvalidOperationException("FFmpeg webcam stream could not start (no valid candidates)."));
                return;
            }

            writer.TryComplete(new InvalidOperationException("FFmpeg webcam stream started but produced no frames."));
        }

        private List<Process> CreateFfmpegWebcamProcessCandidates(int fps)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX) &&
                !RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return new List<Process> { new() { StartInfo = new ProcessStartInfo { FileName = "" } } };
            }

            var videoSize = $"{_settings.DefaultFrameWidth}x{_settings.DefaultFrameHeight}";

            // Note: avfoundation can be finicky; start with minimal args first, then add low-latency flags.
            var commonBase = "-hide_banner -loglevel error -nostdin";
            var commonLowLatency = $"{commonBase} -fflags nobuffer -flags low_delay";

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var input = _settings.MacAvFoundationInput;
                // Try a few combinations. avfoundation can be picky about framerate rational vs. exact 30/1.
                var candidates = new[]
                {
                    // Minimal (most likely to work)
                    $"{commonBase} -f avfoundation -framerate 30 -video_size {videoSize} -i \"{input}\" -vf fps={fps} -an -f image2pipe -vcodec mjpeg -q:v 12 -flush_packets 1 pipe:1",
                    // With explicit pixel format
                    $"{commonBase} -f avfoundation -framerate 30 -pixel_format nv12 -video_size {videoSize} -i \"{input}\" -vf fps={fps} -an -f image2pipe -vcodec mjpeg -q:v 12 -flush_packets 1 pipe:1",
                    // Low-latency variant
                    $"{commonLowLatency} -f avfoundation -framerate 30 -video_size {videoSize} -i \"{input}\" -vf fps={fps} -an -f image2pipe -vcodec mjpeg -q:v 12 -flush_packets 1 pipe:1",
                    // Fallbacks
                    $"{commonBase} -f avfoundation -video_size {videoSize} -i \"{input}\" -vf fps={fps} -an -f image2pipe -vcodec mjpeg -q:v 12 -flush_packets 1 pipe:1",
                    $"{commonBase} -f avfoundation -i \"{input}\" -vf fps={fps} -an -f image2pipe -vcodec mjpeg -q:v 12 -flush_packets 1 pipe:1"
                };

                return candidates.Select(args => new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = _settings.FfmpegPath,
                        Arguments = args,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                }).ToList();
            }

            // Linux
            var linuxArgs =
                $"{commonLowLatency} -f v4l2 -framerate {fps} -video_size {videoSize} -i \"{_settings.LinuxVideoDevice}\" " +
                "-an -f image2pipe -vcodec mjpeg -q:v 12 -flush_packets 1 pipe:1";

            return new List<Process>
            {
                new()
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = _settings.FfmpegPath,
                        Arguments = linuxArgs,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                }
            };
        }

        private async Task ReadMjpegFramesFromStream(
            Stream stdout,
            ChannelWriter<byte[]> writer,
            CancellationToken ct,
            TaskCompletionSource<bool>? firstFrameTcs = null)
        {
            var buffer = new byte[64 * 1024];
            MemoryStream? currentFrame = null;
            var inFrame = false;
            byte prev = 0;
            const int maxFrameBytes = 5 * 1024 * 1024;

            while (!ct.IsCancellationRequested)
            {
                var read = await stdout.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                if (read <= 0)
                {
                    break;
                }

                var span = buffer.AsSpan(0, read);
                var pos = 0;

                while (pos < span.Length)
                {
                    if (!inFrame)
                    {
                        // Cross-buffer SOI: prev=0xFF, current=0xD8
                        if (prev == 0xFF && span[pos] == 0xD8)
                        {
                            inFrame = true;
                            currentFrame?.Dispose();
                            currentFrame = new MemoryStream(capacity: 256 * 1024);
                            currentFrame.WriteByte(0xFF);
                            currentFrame.WriteByte(0xD8);
                            prev = 0xD8;
                            pos += 1;
                            continue;
                        }

                        var ffRel = span.Slice(pos).IndexOf((byte)0xFF);
                        if (ffRel < 0)
                        {
                            prev = span[^1];
                            break;
                        }

                        var ffPos = pos + ffRel;
                        if (ffPos + 1 < span.Length && span[ffPos + 1] == 0xD8)
                        {
                            inFrame = true;
                            currentFrame?.Dispose();
                            currentFrame = new MemoryStream(capacity: 256 * 1024);
                            currentFrame.Write(span.Slice(ffPos, 2));
                            prev = 0xD8;
                            pos = ffPos + 2;
                            continue;
                        }

                        prev = span[ffPos];
                        pos = ffPos + 1;
                        continue;
                    }

                    // inFrame == true
                    if (prev == 0xFF && span[pos] == 0xD9)
                    {
                        currentFrame!.WriteByte(0xD9);
                        var frameBytes = currentFrame.ToArray();
                        writer.TryWrite(frameBytes);
                        firstFrameTcs?.TrySetResult(true);

                        currentFrame.Dispose();
                        currentFrame = null;
                        inFrame = false;
                        prev = 0xD9;
                        pos += 1;
                        continue;
                    }

                    var ffRel2 = span.Slice(pos).IndexOf((byte)0xFF);
                    if (ffRel2 < 0)
                    {
                        currentFrame!.Write(span.Slice(pos));
                        prev = span[^1];

                        if (currentFrame.Length > maxFrameBytes)
                        {
                            _logger.LogWarning("Dropping oversized webcam frame (> {Max} bytes)", maxFrameBytes);
                            currentFrame.Dispose();
                            currentFrame = null;
                            inFrame = false;
                        }
                        break;
                    }

                    var ffPos2 = pos + ffRel2;
                    if (ffPos2 > pos)
                    {
                        currentFrame!.Write(span.Slice(pos, ffPos2 - pos));
                    }

                    if (currentFrame!.Length > maxFrameBytes)
                    {
                        _logger.LogWarning("Dropping oversized webcam frame (> {Max} bytes)", maxFrameBytes);
                        currentFrame.Dispose();
                        currentFrame = null;
                        inFrame = false;
                        prev = 0;
                        pos = ffPos2 + 1;
                        continue;
                    }

                    // Now at 0xFF
                    if (ffPos2 + 1 >= span.Length)
                    {
                        currentFrame.WriteByte(0xFF);
                        prev = 0xFF;
                        break;
                    }

                    var next = span[ffPos2 + 1];
                    currentFrame.WriteByte(0xFF);
                    currentFrame.WriteByte(next);

                    if (currentFrame.Length > maxFrameBytes)
                    {
                        _logger.LogWarning("Dropping oversized webcam frame (> {Max} bytes)", maxFrameBytes);
                        currentFrame.Dispose();
                        currentFrame = null;
                        inFrame = false;
                        prev = next;
                        pos = ffPos2 + 2;
                        continue;
                    }

                    if (next == 0xD9)
                    {
                        var frameBytes = currentFrame.ToArray();
                        writer.TryWrite(frameBytes);
                        firstFrameTcs?.TrySetResult(true);

                        currentFrame.Dispose();
                        currentFrame = null;
                        inFrame = false;
                        prev = 0xD9;
                        pos = ffPos2 + 2;
                        continue;
                    }

                    prev = next;
                    pos = ffPos2 + 2;
                }
            }

            currentFrame?.Dispose();
        }

        /// <summary>
        /// Open webcam
        /// </summary>
        public bool OpenWebcam()
        {
            return OpenWebcamInternal();
        }

        /// <summary>
        /// Close webcam and release resources
        /// </summary>
        public void CloseWebcam()
        {
            lock (_lock)
            {
                CloseWebcamInternal();
            }
        }

        #endregion

        #region Screen Capture Implementation

        [SupportedOSPlatform("windows")]
        private byte[] CaptureScreenWindows()
        {
            int screenWidth = Win32Native.GetSystemMetrics(Win32Native.SM_CXSCREEN);
            int screenHeight = Win32Native.GetSystemMetrics(Win32Native.SM_CYSCREEN);

            using var bitmap = new Bitmap(screenWidth, screenHeight);
            using var graphics = Graphics.FromImage(bitmap);
            
            graphics.CopyFromScreen(0, 0, 0, 0, 
                new System.Drawing.Size(screenWidth, screenHeight), 
                CopyPixelOperation.SourceCopy);

            using var ms = new MemoryStream();
            bitmap.Save(ms, ImageFormat.Jpeg);
            
            _logger.LogDebug("Captured Windows screenshot: {Width}x{Height}", screenWidth, screenHeight);
            return ms.ToArray();
        }

        private byte[] CaptureScreenMacOS()
        {
            var tempFile = Path.Combine(Path.GetTempPath(), $"screenshot_{Guid.NewGuid()}.jpg");
            
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "screencapture",
                    Arguments = $"-t jpg \"{tempFile}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };

                using var process = Process.Start(psi);
                process?.WaitForExit(5000);

                if (File.Exists(tempFile))
                {
                    var bytes = File.ReadAllBytes(tempFile);
                    _logger.LogDebug("Captured macOS screenshot: {Size} bytes", bytes.Length);
                    return bytes;
                }

                _logger.LogWarning("macOS screenshot file not created");
                return Array.Empty<byte>();
            }
            finally
            {
                // Clean up temp file
                try
                {
                    if (File.Exists(tempFile))
                    {
                        File.Delete(tempFile);
                    }
                }
                catch { }
            }
        }

        private byte[] CaptureScreenLinux()
        {
            var tempFile = Path.Combine(Path.GetTempPath(), $"screenshot_{Guid.NewGuid()}.jpg");
            
            try
            {
                // Try multiple screenshot tools
                var tools = new[]
                {
                    ("gnome-screenshot", $"-f \"{tempFile}\""),
                    ("scrot", $"\"{tempFile}\""),
                    ("import", $"-window root \"{tempFile}\"")  // ImageMagick
                };

                foreach (var (tool, args) in tools)
                {
                    try
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = tool,
                            Arguments = args,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };

                        using var process = Process.Start(psi);
                        process?.WaitForExit(5000);

                        if (File.Exists(tempFile))
                        {
                            var bytes = File.ReadAllBytes(tempFile);
                            _logger.LogDebug("Captured Linux screenshot using {Tool}: {Size} bytes", tool, bytes.Length);
                            return bytes;
                        }
                    }
                    catch
                    {
                        // Tool not available, try next
                    }
                }

                _logger.LogWarning("No screenshot tool available on Linux");
                return Array.Empty<byte>();
            }
            finally
            {
                try
                {
                    if (File.Exists(tempFile))
                    {
                        File.Delete(tempFile);
                    }
                }
                catch { }
            }
        }

        #endregion

        #region Webcam Implementation

        private bool OpenWebcamInternal()
        {
#if OPENCV
            lock (_lock)
            {
                // Already open
                if (_webcamCapture != null && _webcamCapture.IsOpened())
                {
                    return true;
                }

                // Dispose old instance if exists
                _webcamCapture?.Dispose();

                try
                {
                    if (OperatingSystem.IsMacOS())
                    {
                        _webcamCapture = new VideoCapture(0, VideoCaptureAPIs.AVFoundation);
                    }
                    else
                    {
                        _webcamCapture = new VideoCapture(0);
                    }

                    if (!_webcamCapture.IsOpened())
                    {
                        _logger.LogWarning("Failed to open webcam device");
                        _webcamCapture.Dispose();
                        _webcamCapture = null;
                        return false;
                    }

                    // Configure webcam
                    _webcamCapture.Set(VideoCaptureProperties.FrameWidth, _settings.DefaultFrameWidth);
                    _webcamCapture.Set(VideoCaptureProperties.FrameHeight, _settings.DefaultFrameHeight);

                    _logger.LogInformation("Webcam opened (OpenCV): {Width}x{Height}", 
                        _settings.DefaultFrameWidth, _settings.DefaultFrameHeight);
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error opening webcam");
                    _webcamCapture?.Dispose();
                    _webcamCapture = null;
                    return false;
                }
            }
#else
            // No OpenCV on this platform; FFmpeg batch capture will be used.
            _logger.LogInformation("OpenCV not enabled; webcam uses FFmpeg batch.");
            return true;
#endif
        }

        private void CloseWebcamInternal()
        {
#if OPENCV
            if (_webcamCapture != null)
            {
                try
                {
                    if (_webcamCapture.IsOpened())
                    {
                        _webcamCapture.Release();
                    }
                    _webcamCapture.Dispose();
                    _logger.LogInformation("Webcam closed");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error closing webcam");
                }
                finally
                {
                    _webcamCapture = null;
                }
            }
#endif
        }

        private byte[]? CaptureFrame()
        {
#if !OPENCV
            _logger.LogWarning("CaptureFrame not supported without OpenCV");
            return null;
#else
            lock (_lock)
            {
                if (_webcamCapture == null || !_webcamCapture.IsOpened())
                {
                    return null;
                }

                using var frame = new Mat();
                
                if (!_webcamCapture.Read(frame) || frame.Empty())
                {
                    _logger.LogWarning("Failed to read frame from webcam");
                    return null;
                }

                if (Cv2.ImEncode(".jpg", frame, out var encodedBytes))
                {
                    return encodedBytes;
                }

                return null;
            }
#endif
        }

        private async Task<List<byte[]>> CaptureVideoFrames(int durationMs, int frameRate, CancellationToken ct)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return await CaptureVideoFramesLinux(durationMs / 1000.0, frameRate, ct);
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
#if OPENCV
                if (OpenWebcamInternal())
                {
                    return await CaptureVideoFramesOpenCv(durationMs, frameRate, ct);
                }
#endif
                return await CaptureVideoFramesMacOS(durationMs / 1000.0, frameRate, ct);
            }

            return await CaptureVideoFramesOpenCv(durationMs, frameRate, ct);
        }

        private async Task<List<byte[]>> CaptureVideoFramesOpenCv(int durationMs, int frameRate, CancellationToken ct)
        {
            var frames = new List<byte[]>();
#if OPENCV
            if (!OpenWebcamInternal())
            {
                return frames;
            }
#endif
            var safeFps = Math.Clamp(frameRate, 1, 30);
            var delayMs = (int)Math.Round(1000.0 / safeFps);
            var stopwatch = Stopwatch.StartNew();

            try
            {
                while (stopwatch.ElapsedMilliseconds < durationMs && !ct.IsCancellationRequested)
                {
                    var frame = CaptureFrame();

                    if (frame != null && frame.Length > 0)
                    {
                        frames.Add(frame);
                    }

                    await Task.Delay(delayMs, ct);
                }

                _logger.LogInformation("Captured {FrameCount} webcam frames in {Duration}ms", frames.Count, stopwatch.ElapsedMilliseconds);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Frame capture cancelled after {FrameCount} frames", frames.Count);
            }

            return frames;
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    CloseWebcam();
                }
                _disposed = true;
            }
        }

        ~WebcamService()
        {
            Dispose(false);
        }

        #endregion

        #region macOS FFmpeg Batch Capture

        private async Task<List<byte[]>> CaptureVideoFramesMacOS(double durationSec, int frameRate, CancellationToken ct)
        {
            var frames = new List<byte[]>();
            var tempDir = Path.Combine(Path.GetTempPath(), $"webcam_{Guid.NewGuid()}");
            
            try
            {
                Directory.CreateDirectory(tempDir);
                var outputPattern = Path.Combine(tempDir, "frame_%03d.jpg");

                // FFmpeg: Open camera once, capture for duration, extract frames
                var safeFps = Math.Clamp(frameRate, 1, 15);
                var videoSize = $"{_settings.DefaultFrameWidth}x{_settings.DefaultFrameHeight}";
                var durationArg = durationSec.ToString(CultureInfo.InvariantCulture);

                var psi = new ProcessStartInfo
                {
                    FileName = _settings.FfmpegPath,
                    Arguments =
                        $"-nostdin -y -f avfoundation -framerate 30 -video_size {videoSize} -i \"0:none\" -t {durationArg} -vf fps={safeFps} -q:v 5 \"{outputPattern}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };

                _logger.LogInformation("Starting FFmpeg batch capture: {Duration}s @ {FPS} fps, tempDir: {TempDir}", durationSec, frameRate, tempDir);
                _logger.LogDebug("FFmpeg command: {Cmd} {Args}", psi.FileName, psi.Arguments);

                using var process = Process.Start(psi);
                if (process == null)
                {
                    _logger.LogError("Failed to start FFmpeg process");
                    return frames;
                }

                _logger.LogInformation("FFmpeg process started, PID: {PID}", process.Id);

                // Wait for FFmpeg to complete (camera init + capture + encoding takes time)
                var timeoutSec = Math.Max(30, durationSec + 15); // Minimum 30s timeout
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
                
                try
                {
                    var waitTask = process.WaitForExitAsync(cts.Token);
                    var stderrTask = process.StandardError.ReadToEndAsync();
                    
                    await waitTask;
                    var stderr = await stderrTask;
                    
                    _logger.LogInformation("FFmpeg completed with exit code: {Code}", process.ExitCode);
                    
                    if (process.ExitCode != 0)
                    {
                        _logger.LogError("FFmpeg failed! stderr: {Err}", stderr);
                    }
                    else
                    {
                        _logger.LogDebug("FFmpeg stderr: {Err}", stderr);
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("FFmpeg process timeout after {Timeout}s, killing...", timeoutSec);
                    try { process.Kill(true); } catch { }
                    throw;
                }

                // Read all generated frames
                var frameFiles = Directory.GetFiles(tempDir, "frame_*.jpg")
                    .OrderBy(f => f)
                    .ToList();

                _logger.LogInformation("FFmpeg captured {FrameCount} frames", frameFiles.Count);

                foreach (var file in frameFiles)
                {
                    if (ct.IsCancellationRequested) break;
                    
                    try
                    {
                        var bytes = await File.ReadAllBytesAsync(file, ct);
                        if (bytes.Length > 0)
                        {
                            frames.Add(bytes);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to read frame file: {File}", file);
                    }
                }

                return frames;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("macOS webcam capture cancelled");
                return frames;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during macOS batch capture");
                return frames;
            }
            finally
            {
                // Cleanup temp directory
                try
                {
                    if (Directory.Exists(tempDir))
                    {
                        Directory.Delete(tempDir, recursive: true);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to cleanup temp directory");
                }
            }
        }

        private async Task<List<byte[]>> CaptureVideoFramesLinux(double durationSec, int frameRate, CancellationToken ct)
        {
            var frames = new List<byte[]>();
            var tempDir = Path.Combine(Path.GetTempPath(), $"webcam_{Guid.NewGuid()}");

            try
            {
                Directory.CreateDirectory(tempDir);
                var outputPattern = Path.Combine(tempDir, "frame_%03d.jpg");

                var safeFps = Math.Clamp(frameRate, 1, 15);
                var videoSize = $"{_settings.DefaultFrameWidth}x{_settings.DefaultFrameHeight}";
                var durationArg = durationSec.ToString(CultureInfo.InvariantCulture);

                var psi = new ProcessStartInfo
                {
                    FileName = _settings.FfmpegPath,
                    Arguments =
                        $"-nostdin -y -f v4l2 -framerate 30 -video_size {videoSize} -i \"{_settings.LinuxVideoDevice}\" -t {durationArg} -vf fps={safeFps} -q:v 5 \"{outputPattern}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };

                _logger.LogInformation(
                    "Starting FFmpeg Linux capture: {Duration}s @ {FPS} fps, device: {Device}, tempDir: {TempDir}",
                    durationSec, safeFps, _settings.LinuxVideoDevice, tempDir);
                _logger.LogDebug("FFmpeg command: {Cmd} {Args}", psi.FileName, psi.Arguments);

                using var process = Process.Start(psi);
                if (process == null)
                {
                    _logger.LogError("Failed to start FFmpeg process on Linux");
                    return frames;
                }

                var timeoutSec = Math.Max(30, durationSec + 15);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

                try
                {
                    var waitTask = process.WaitForExitAsync(cts.Token);
                    var stderrTask = process.StandardError.ReadToEndAsync();

                    await waitTask;
                    var stderr = await stderrTask;

                    _logger.LogInformation("FFmpeg Linux capture completed with exit code: {Code}", process.ExitCode);

                    if (process.ExitCode != 0)
                    {
                        _logger.LogError("FFmpeg Linux capture failed! stderr: {Err}", stderr);
                    }
                    else
                    {
                        _logger.LogDebug("FFmpeg Linux stderr: {Err}", stderr);
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("FFmpeg Linux capture timeout after {Timeout}s, killing...", timeoutSec);
                    try { process.Kill(true); } catch { }
                    throw;
                }

                var frameFiles = Directory.GetFiles(tempDir, "frame_*.jpg")
                    .OrderBy(f => f)
                    .ToList();

                _logger.LogInformation("FFmpeg Linux captured {FrameCount} frames", frameFiles.Count);

                foreach (var file in frameFiles)
                {
                    if (ct.IsCancellationRequested) break;

                    try
                    {
                        var bytes = await File.ReadAllBytesAsync(file, ct);
                        if (bytes.Length > 0)
                        {
                            frames.Add(bytes);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to read Linux frame file: {File}", file);
                    }
                }

                return frames;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Linux webcam capture cancelled");
                return frames;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during Linux batch capture");
                return frames;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempDir))
                    {
                        Directory.Delete(tempDir, recursive: true);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to cleanup Linux temp directory");
                }
            }
        }

        #endregion
    }
}
