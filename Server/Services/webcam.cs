using System;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Drawing.Imaging;
using OpenCvSharp;
using Server.Helper;
using static System.Runtime.InteropServices.RuntimeInformation;

namespace Server.Services
{
    public class WebcamService
    {
        [DllImport("user32.dll")]
        static extern int GetSystemMetrics(int nIndex);

        const int SM_CXSCREEN = 0;
        const int SM_CYSCREEN = 1;

        public byte[] CaptureScreen()
        {   
            return helperCapScr();
        }

        public void closeWebcam()
        {
            helperCloseWebcam();
        }

        public bool OpenWebcam()
        {
            return helperOpenWebcam();
        }

        public async Task<List<byte[]>> videoMakerManager(int frameRate, CancellationToken cancellationToken)
        {
            return await helperVideoMakerManager(frameRate, cancellationToken);
        }


        // hàm này là để chụp màn hình 
        private byte[] helperCapScr()
        {
            try
            {
                if (IsOSPlatform(OSPlatform.Windows))
                {
                    int screenWidth = GetSystemMetrics(SM_CXSCREEN);
                    int screenHeight = GetSystemMetrics(SM_CYSCREEN);

                    using (Bitmap bmp = new Bitmap(screenWidth, screenHeight))
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.CopyFromScreen(0, 0, 0, 0, new System.Drawing.Size(screenWidth, screenHeight), CopyPixelOperation.SourceCopy);
                        
                        using (MemoryStream ms = new MemoryStream())
                        {
                            bmp.Save(ms, ImageFormat.Jpeg);
                            return ms.ToArray();
                        }
                    }
                }
                else if (IsOSPlatform(OSPlatform.OSX) || IsOSPlatform(OSPlatform.Linux))
                {
                    // Logic cho macOS/Linux (Dùng lệnh Shell gốc)
                    string tempFileName = Path.Combine(Path.GetTempPath(), $"screenshot_{Guid.NewGuid()}.jpg");
                    string shellCommand;

                    if (IsOSPlatform(OSPlatform.OSX))
                    {
                        // Lệnh macOS: screencapture -t jpg [filepath]
                        shellCommand = $"screencapture -t jpg \"{tempFileName}\"";
                    }
                    else
                    {
                        // Lệnh Linux: Dùng gnome-screenshot (yêu cầu môi trường desktop)
                        shellCommand = $"gnome-screenshot -f \"{tempFileName}\"";
                    }

                    ShellUtils.ExecuteShellCommand(shellCommand); 
                    
                    // Đọc file ảnh tạm thời và xóa nó
                    if (File.Exists(tempFileName))
                    {
                        byte[] imageBytes = File.ReadAllBytes(tempFileName);
                        File.Delete(tempFileName);
                        return imageBytes;
                    }
                    else
                    {
                        Console.WriteLine($"Error: Shell command failed to create screenshot file: {tempFileName}");
                        return Encoding.UTF8.GetBytes("SCREEN_CAPTURE_FAILED");
                    }
                }
                else
                {
                    return Encoding.UTF8.GetBytes("OS_NOT_SUPPORTED");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error capturing screen: {ex.Message}");
            }
            return null;
        }

        private void helperCloseWebcam()
        {
            if (_webcamCapture != null)
            {
                if (_webcamCapture.IsOpened())
                {
                    _webcamCapture.Release();
                }
                
                _webcamCapture.Dispose();
                _webcamCapture = null;
                Console.WriteLine("Webcam closed successfully.");
            }
        }

        private bool helperOpenWebcam()
        {
            if (_webcamCapture != null)
            {
                if (_webcamCapture.IsOpened())
                {
                    return true;
                }
                _webcamCapture.Dispose();
            }

            _webcamCapture = new VideoCapture(0);   

            if (!_webcamCapture.IsOpened())
            {
                _webcamCapture.Dispose();
                _webcamCapture = null;
                return false;
            }

            _webcamCapture.Set(VideoCaptureProperties.FrameWidth, 640);
            _webcamCapture.Set(VideoCaptureProperties.FrameHeight, 480);
            
            return true;
        }


        // Hàm này capture 1 frame từ webcam
        private byte[] captureForVideo()
        {
            if (_webcamCapture == null || !_webcamCapture.IsOpened())
            {
                return null;
            }
            
            using (Mat frame = new Mat())
            {
                if (!_webcamCapture.Read(frame) || frame.Empty())
                {
                    Console.WriteLine("Failed to read frame from webcam.");
                    return null;
                }

                OpenCvSharp.Mat encodedFrame = new OpenCvSharp.Mat();
                Cv2.ImEncode(".jpg", frame, encodedFrame);
                
                return encodedFrame.ToBytes();
            }
        }

        private async Task<List<byte[]>> helperVideoMakerManager(int frameRate, CancellationToken cancellationToken)
        {
            if (!OpenWebcam())
            {
                Console.WriteLine("Failed to open webcam for proof.");
                return new List<byte[]>();
            }

            int durationMs = 3000; // 3 giây
            List<byte[]> proofFrames = await videoMaker(durationMs, frameRate, cancellationToken);            
            return proofFrames;
        }

        private async Task<List<byte[]>> videoMaker(int durationMs, int frameRate, CancellationToken cancellationToken)
        {
            List<byte[]> frames = new List<byte[]>();
            
            if (_webcamCapture == null || !_webcamCapture.IsOpened()) return frames;

            int delayMs = 1000 / frameRate; // này là để coi đợi bao lâu thì chụp tiếp
            Stopwatch stopwatch = Stopwatch.StartNew();

            try
            {
                while (stopwatch.ElapsedMilliseconds < durationMs && !cancellationToken.IsCancellationRequested)
                {
                    byte[] frameData = captureForVideo();
                    if (frameData != null && frameData.Length > 0) frames.Add(frameData);
                    await Task.Delay(delayMs, cancellationToken);
                }
            }
            finally
            {
                stopwatch.Stop();
            }

            return frames;
        }
        
        
    }
}