using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Server.helper; 


// Luôn dùng OpenCvSharp
using OpenCvSharp;

// Thêm thư viện
//using System.Drawing;
//using System.Drawing.Imaging;
// using System.IO;
// using System.Linq;
// using System.Windows.Forms; // Chỉ cần trên Windows

namespace Server.Services
{

        public class AppSettings
    {
        public WebcamSettings Webcam { get; set; } = new();
    }

    public class WebcamSettings
    {
        public int DefaultFrameWidth { get; set; } = 640;
        public int DefaultFrameHeight { get; set; } = 480;
        public int ProofDurationMs { get; set; } = 3000;
        public int DefaultFrameRate { get; set; } = 10;
        
        public string FfmpegPath { get; set; } = "ffmpeg";

        public string MacAvFoundationInput { get; set; } = "0:none";
        public string LinuxVideoDevice { get; set; } = "/dev/video0";
    }

    public class WebcamService : IDisposable
    {
        private readonly ILogger<WebcamService> _logger;
        private readonly WebcamSettings _settings;
        
        // Biến giữ kết nối Webcam OpenCV
        private VideoCapture? _webcamCapture;
        
        private readonly object _lock = new();
        private bool _disposed;

        // Helper P/Invoke cho Windows Screenshot
        private static class Win32Native
        {
            [DllImport("user32.dll")]
            public static extern int GetSystemMetrics(int nIndex);
            
            public const int SM_CXSCREEN = 0;
            public const int SM_CYSCREEN = 1;
            public const int SM_XVIRTUALSCREEN = 76;  // Tọa độ X gốc của không gian ảo
            public const int SM_YVIRTUALSCREEN = 77;  // Tọa độ Y gốc của không gian ảo
            public const int SM_CXVIRTUALSCREEN = 78; // Chiều rộng tổng của không gian ảo
            public const int SM_CYVIRTUALSCREEN = 79; // Chiều cao tổng của không gian ảo

        }

        public WebcamService(ILogger<WebcamService> logger, IOptions<AppSettings> settings)
        {
            _logger = logger;
            _settings = settings.Value.Webcam;
        }

        #region Public API (Giao diện chính cho ControlHub)

        // Wrapper viết thường để tương thích với code cũ
        public byte[] captureScreen() => CaptureScreen();
        public void closeWebcam() => CloseWebcam();

        /// <summary>
        /// Chụp ảnh màn hình hiện tại
        /// </summary>
        public byte[] CaptureScreen()
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return CaptureScreenWindows();
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return CaptureScreenMacOS();
                else return CaptureScreenLinux();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi chụp màn hình");
                return Array.Empty<byte>();
            }
        }

        /// <summary>
        /// Mở Webcam và quay video bằng chứng (Mặc định 3s)
        /// </summary>
        public async Task<List<byte[]>> RequestWebcam(int frameRate, CancellationToken cancellationToken)
        {
            try
            {
                // [NOTE] Gán cứng thời gian là 10000ms (10 giây)
                int durationMs = 10000; 
                
                // Nếu muốn ưu tiên file cấu hình thì bỏ comment dòng dưới:
                // if (_settings.ProofDurationMs > 0) durationMs = _settings.ProofDurationMs;

                _logger.LogInformation($"Bắt đầu quay Webcam trong {durationMs}ms...");

                // Linux
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    return await CaptureVideoFramesLinux(durationMs / 1000.0, frameRate, cancellationToken);
                }

                // Windows/Mac OpenCV
                if (OpenWebcamInternal())
                {
                    // Truyền 10000ms vào đây
                    return await CaptureVideoFramesOpenCv(durationMs, frameRate, cancellationToken);
                }

                // Mac fallback
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    return await CaptureVideoFramesMacOS(durationMs / 1000.0, frameRate, cancellationToken);
                }

                _logger.LogWarning("Không hỗ trợ quay video trên nền tảng này.");
                return new List<byte[]>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi quay video bằng chứng");
                return new List<byte[]>();
            }
        }

        /// <summary>
        /// Quay một đoạn video ngắn (Batch) - Dành cho tính năng Streaming sau này
        /// </summary>
        public async Task<List<byte[]>> CaptureWebcamFramesBatch(double durationSec, int frameRate, CancellationToken cancellationToken)
        {
            try
            {
                if (durationSec <= 0) durationSec = 1;
                var durationMs = (int)Math.Round(durationSec * 1000);
                
                // Tự động chọn OpenCV hoặc Fallback
                if (OpenWebcamInternal()) return await CaptureVideoFramesOpenCv(durationMs, frameRate, cancellationToken);
                
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) 
                    return await CaptureVideoFramesMacOS(durationSec, frameRate, cancellationToken);
                
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    return await CaptureVideoFramesLinux(durationSec, frameRate, cancellationToken);

                return new List<byte[]>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi quay batch video");
                return new List<byte[]>();
            }
        }

        /// <summary>
        /// Thử chụp 1 tấm ảnh duy nhất từ Webcam (Snapshot)
        /// </summary>
        public byte[]? TryCaptureSingleWebcamFrameOpenCv()
        {
            if (!OpenWebcamInternal()) return null;
            return CaptureFrame();
        }

        // Đóng Webcam công khai
        public void CloseWebcam()
        {
            lock (_lock)
            {
                CloseWebcamInternal();
            }
        }

        #endregion

        #region Webcam Implementation (OpenCV Logic)

        private bool OpenWebcamInternal()
        {
            lock (_lock)
            {
                if (_webcamCapture != null && _webcamCapture.IsOpened()) return true;

                _webcamCapture?.Dispose();

                try
                {
                    // 1. Chọn Backend tối ưu cho từng OS
                    VideoCaptureAPIs backend = VideoCaptureAPIs.ANY;
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) backend = VideoCaptureAPIs.DSHOW;
                    else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) backend = VideoCaptureAPIs.AVFOUNDATION;
                    else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) backend = VideoCaptureAPIs.V4L2;

                    _webcamCapture = new VideoCapture(0, backend);

                    // 2. Nếu Backend xịn lỗi, thử lại với Auto
                    if (!_webcamCapture.IsOpened())
                    {
                        _webcamCapture.Dispose();
                        _webcamCapture = new VideoCapture(0);
                    }

                    if (!_webcamCapture.IsOpened())
                    {
                        _logger.LogWarning("Không thể mở thiết bị Webcam (Index 0).");
                        _webcamCapture.Dispose();
                        _webcamCapture = null;
                        return false;
                    }

                    // 3. Cấu hình độ phân giải
                    _webcamCapture.Set(VideoCaptureProperties.FrameWidth, _settings.DefaultFrameWidth);
                    _webcamCapture.Set(VideoCaptureProperties.FrameHeight, _settings.DefaultFrameHeight);

                    _logger.LogInformation("Webcam đã mở (OpenCV): {W}x{H}", _settings.DefaultFrameWidth, _settings.DefaultFrameHeight);
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi khi mở Webcam OpenCV");
                    _webcamCapture?.Dispose();
                    _webcamCapture = null;
                    return false;
                }
            }
        }

        private void CloseWebcamInternal()
        {
            if (_webcamCapture != null)
            {
                try
                {
                    if (_webcamCapture.IsOpened()) _webcamCapture.Release();
                    _webcamCapture.Dispose();
                    _logger.LogInformation("Webcam đã đóng.");
                }
                catch { }
                finally { _webcamCapture = null; }
            }
        }

        private byte[]? CaptureFrame()
        {
            lock (_lock)
            {
                if (_webcamCapture == null || !_webcamCapture.IsOpened()) return null;

                using var frame = new Mat();
                
                if (!_webcamCapture.Read(frame) || frame.Empty())
                {
                    _logger.LogWarning("Đọc frame thất bại.");
                    CloseWebcamInternal(); 
                    return null;
                }

                if (Cv2.ImEncode(".jpg", frame, out var encodedBytes))
                {
                    return encodedBytes;
                }
                return null;
            }
        }

        private async Task<List<byte[]>> CaptureVideoFramesOpenCv(int durationMs, int frameRate, CancellationToken ct)
        {
            var frames = new List<byte[]>();
            if (!OpenWebcamInternal()) return frames;

            var safeFps = Math.Clamp(frameRate, 1, 30);
            var delayMs = (int)Math.Round(1000.0 / safeFps);
            var stopwatch = Stopwatch.StartNew();

            try
            {
                while (stopwatch.ElapsedMilliseconds < durationMs && !ct.IsCancellationRequested)
                {
                    var frame = CaptureFrame();
                    if (frame != null && frame.Length > 0) frames.Add(frame);
                    await Task.Delay(delayMs, ct);
                }
                _logger.LogInformation("OpenCV: Đã quay {Count} frames.", frames.Count);
            }
            finally 
            {
                CloseWebcamInternal();
            }
            return frames;
        }

        #endregion

        #region FFmpeg Streaming Logic (StreamWebcamFramesFfmpegLatest)
        // Đây là phần logic nâng cao để stream liên tục từ FFmpeg thông qua Pipe
        // Rất hữu ích khi cần live stream mượt mà

        public ChannelReader<byte[]> StreamWebcamFramesFfmpegLatest(int fps, CancellationToken ct)
        {
            fps = Math.Clamp(fps, 1, 15);
            var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest, // Chỉ giữ frame mới nhất
                SingleReader = true,
                SingleWriter = true
            });

            _ = Task.Run(async () => { await StreamWebcamFramesFfmpegWorker(fps, channel.Writer, ct); }, CancellationToken.None);
            return channel.Reader;
        }

        private async Task StreamWebcamFramesFfmpegWorker(int fps, ChannelWriter<byte[]> writer, CancellationToken ct)
        {
            var processes = CreateFfmpegWebcamProcessCandidates(fps);
            bool startedAny = false;

            foreach(var process in processes)
            {
                try
                {
                    if (process.StartInfo.FileName.Length == 0) continue;
                    
                    _logger.LogInformation("Khởi động FFmpeg Stream (Candidate)...");
                    if (!process.Start()) continue;
                    
                    startedAny = true;
                    // Đọc stream MJPEG từ StandardOutput
                    await ReadMjpegFramesFromStream(process.StandardOutput.BaseStream, writer, ct);
                    
                    // Nếu chạy tới đây là xong hoặc bị hủy
                    writer.TryComplete();
                    try { if (!process.HasExited) process.Kill(); } catch {}
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError("Lỗi luồng FFmpeg: " + ex.Message);
                    try { if (!process.HasExited) process.Kill(); } catch {}
                }
            }

            if (!startedAny) writer.TryComplete(new Exception("Không thể khởi động FFmpeg stream."));
        }

        private List<Process> CreateFfmpegWebcamProcessCandidates(int fps)
        {
            var list = new List<Process>();
            var size = $"{_settings.DefaultFrameWidth}x{_settings.DefaultFrameHeight}";
            var baseArgs = "-hide_banner -loglevel error -nostdin -fflags nobuffer -flags low_delay";

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var input = _settings.MacAvFoundationInput;
                // Thử nhiều cấu hình khác nhau cho Mac
                var args = $"{baseArgs} -f avfoundation -framerate 30 -video_size {size} -i \"{input}\" -vf fps={fps} -an -f image2pipe -vcodec mjpeg -q:v 10 pipe:1";
                list.Add(new Process { StartInfo = CreatePsi(args) });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var args = $"{baseArgs} -f v4l2 -framerate {fps} -video_size {size} -i \"{_settings.LinuxVideoDevice}\" -an -f image2pipe -vcodec mjpeg -q:v 10 pipe:1";
                list.Add(new Process { StartInfo = CreatePsi(args) });
            }
            return list;
        }

        private ProcessStartInfo CreatePsi(string args)
        {
            return new ProcessStartInfo
            {
                FileName = _settings.FfmpegPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true, // Quan trọng để đọc stream
                RedirectStandardError = true,
                CreateNoWindow = true
            };
        }

        private async Task ReadMjpegFramesFromStream(Stream stdout, ChannelWriter<byte[]> writer, CancellationToken ct)
        {
            // Logic đọc stream MJPEG (Tìm header 0xFF 0xD8 và footer 0xFF 0xD9)
            // Code này được tối ưu để đọc binary stream liên tục
            var buffer = new byte[64 * 1024];
            using var ms = new MemoryStream();
            bool inFrame = false;
            byte prev = 0;

            while (!ct.IsCancellationRequested)
            {
                int read = await stdout.ReadAsync(buffer, 0, buffer.Length, ct);
                if (read <= 0) break;

                for (int i = 0; i < read; i++)
                {
                    byte b = buffer[i];
                    
                    if (!inFrame)
                    {
                        if (prev == 0xFF && b == 0xD8) // SOI (Start of Image)
                        {
                            inFrame = true;
                            ms.SetLength(0);
                            ms.WriteByte(0xFF);
                            ms.WriteByte(0xD8);
                        }
                    }
                    else
                    {
                        ms.WriteByte(b);
                        if (prev == 0xFF && b == 0xD9) // EOI (End of Image)
                        {
                            writer.TryWrite(ms.ToArray());
                            inFrame = false;
                        }
                    }
                    prev = b;
                }
            }
        }

        #endregion

        #region FFmpeg Fallback (Mac/Linux - Quay 1 cục video rồi tách frame)

        private async Task<List<byte[]>> CaptureVideoFramesMacOS(double durationSec, int frameRate, CancellationToken ct)
        {
            var frames = new List<byte[]>();
            var tempDir = Path.Combine(Path.GetTempPath(), $"webcam_{Guid.NewGuid()}");
            try
            {
                Directory.CreateDirectory(tempDir);
                var pattern = Path.Combine(tempDir, "frame_%03d.jpg");
                var size = $"{_settings.DefaultFrameWidth}x{_settings.DefaultFrameHeight}";
                var dur = durationSec.ToString(CultureInfo.InvariantCulture);

                var psi = new ProcessStartInfo
                {
                    FileName = _settings.FfmpegPath,
                    Arguments = $"-nostdin -y -f avfoundation -framerate 30 -video_size {size} -i \"{_settings.MacAvFoundationInput}\" -t {dur} -vf fps={frameRate} -q:v 5 \"{pattern}\"",
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true
                };

                using var proc = Process.Start(psi);
                if(proc != null) await proc.WaitForExitAsync(ct);

                var files = Directory.GetFiles(tempDir, "frame_*.jpg").OrderBy(f => f).ToList();
                // [FIX] Đã sửa lỗi biến vòng lặp từ 'f' thành 'file' để khớp với biến bên trong
                foreach (var file in files) frames.Add(await File.ReadAllBytesAsync(file, ct));
                _logger.LogInformation($"FFmpeg (Mac) đã quay {frames.Count} frames.");
            }
            catch (Exception ex) { _logger.LogError("FFmpeg MacOS Error: " + ex.Message); }
            finally { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); }
            return frames;
        }

        private async Task<List<byte[]>> CaptureVideoFramesLinux(double durationSec, int frameRate, CancellationToken ct)
        {
            var frames = new List<byte[]>();
            var tempDir = Path.Combine(Path.GetTempPath(), $"webcam_{Guid.NewGuid()}");
            try 
            {
                Directory.CreateDirectory(tempDir);
                var pattern = Path.Combine(tempDir, "frame_%03d.jpg");
                var size = $"{_settings.DefaultFrameWidth}x{_settings.DefaultFrameHeight}";
                var dur = durationSec.ToString(CultureInfo.InvariantCulture);
                
                var psi = new ProcessStartInfo 
                {
                    FileName = _settings.FfmpegPath,
                    Arguments = $"-nostdin -y -f v4l2 -framerate 30 -video_size {size} -i \"{_settings.LinuxVideoDevice}\" -t {dur} -vf fps={frameRate} -q:v 5 \"{pattern}\"",
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true
                };
                
                using var proc = Process.Start(psi);
                if(proc != null) await proc.WaitForExitAsync(ct);
                
                var files = Directory.GetFiles(tempDir, "frame_*.jpg").OrderBy(f => f).ToList();
                // [FIX] Đã sửa lỗi biến vòng lặp từ 'f' thành 'file'
                foreach (var file in files) frames.Add(await File.ReadAllBytesAsync(file, ct));
            }
            catch (Exception ex) { _logger.LogError("FFmpeg Linux Error: " + ex.Message); }
            finally { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); }
            return frames;
        }

        #endregion

        #region Screen Capture Implementation (Screenshot)

        [SupportedOSPlatform("windows")]
        // private byte[] CaptureScreenWindows()
        // {
        //     try {
        //         int w = Win32Native.GetSystemMetrics(Win32Native.SM_CXSCREEN);
        //         int h = Win32Native.GetSystemMetrics(Win32Native.SM_CYSCREEN);
        //         using var bmp = new Bitmap(w, h);
        //         using var g = Graphics.FromImage(bmp);
        //         g.CopyFromScreen(0, 0, 0, 0, new System.Drawing.Size(w, h), CopyPixelOperation.SourceCopy);
        //         using var ms = new MemoryStream();
        //         bmp.Save(ms, ImageFormat.Jpeg);
        //         return ms.ToArray();
        //     } catch { return Array.Empty<byte>(); }
        // }


        private byte[] CaptureScreenWindows()
        {
            try
            {
                const int w = 1920; 
                const int h = 1080;
                const int x = 0;
                const int y = 0;

                using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb); 
                using var g = Graphics.FromImage(bmp);

                // Chụp từ góc (0,0) - màn hình chính
                g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(w, h),
                    CopyPixelOperation.SourceCopy
                );

                using var ms = new MemoryStream();
                bmp.Save(ms, ImageFormat.Jpeg);

                return ms.ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CaptureScreenWindows failed");
                return Array.Empty<byte>();
            }
        }


        private byte[] CaptureScreenMacOS()
        {
            var temp = Path.Combine(Path.GetTempPath(), $"scr_{Guid.NewGuid()}.jpg");
            try
            {
                var psi = new ProcessStartInfo { FileName = "screencapture", Arguments = $"-t jpg -x \"{temp}\"", UseShellExecute = false, CreateNoWindow = true };
                using var process = Process.Start(psi);
                process?.WaitForExit(3000);
                if (File.Exists(temp)) return File.ReadAllBytes(temp);
                return Array.Empty<byte>();
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        private byte[] CaptureScreenLinux() 
        {
            var temp = Path.Combine(Path.GetTempPath(), $"scr_{Guid.NewGuid()}.jpg");
            try {
                var tools = new[] { ("gnome-screenshot", $"-f \"{temp}\""), ("scrot", $"\"{temp}\"") };
                foreach(var (tool, args) in tools) {
                    try {
                        var psi = new ProcessStartInfo { FileName = tool, Arguments = args, UseShellExecute = false, CreateNoWindow = true };
                        using var process = Process.Start(psi);
                        process?.WaitForExit(3000);
                        if (File.Exists(temp)) return File.ReadAllBytes(temp);
                    } catch {}
                }
                return Array.Empty<byte>();
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        #endregion

        #region IDisposable
        public void Dispose() { Dispose(true); GC.SuppressFinalize(this); }
        protected virtual void Dispose(bool disposing) { if (!_disposed) { if (disposing) CloseWebcamInternal(); _disposed = true; } }
        ~WebcamService() { Dispose(false); }
        #endregion
    }
}