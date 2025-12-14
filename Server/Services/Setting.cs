namespace Server.Configuration
{
    public class AppSettings
    {
        public AuthSettings Auth { get; set; } = new();
        public CorsSettings Cors { get; set; } = new();
        public WebcamSettings Webcam { get; set; } = new();
        public LoggingSettings Logging { get; set; } = new();
    }

    public class AuthSettings
    {
        /// <summary>
        /// Secret key for JWT token generation (minimum 32 characters)
        /// </summary>
        public string SecretKey { get; set; } = "YourSuperSecretKeyHere_ChangeThis_MinLength32Chars!";
        
        /// <summary>
        /// Token expiration time in hours
        /// </summary>
        public int TokenExpirationHours { get; set; } = 24;
        
        /// <summary>
        /// List of valid usernames and passwords
        /// In production, use a proper user store
        /// </summary>
        public List<UserCredential> Users { get; set; } = new()
        {
            new UserCredential { Username = "admin", Password = "admin123", Role = "Admin" },
            new UserCredential { Username = "user", Password = "user123", Role = "User" }
        };
    }

    public class UserCredential
    {
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string Role { get; set; } = "User";
    }

    public class CorsSettings
    {
        /// <summary>
        /// Allowed origins for CORS. Use specific origins in production.
        /// </summary>
        public string[] AllowedOrigins { get; set; } = { "http://localhost:3000", "http://127.0.0.1:5500" };
    }

    public class WebcamSettings
    {
        public int DefaultFrameWidth { get; set; } = 640;
        public int DefaultFrameHeight { get; set; } = 480;
        public int ProofDurationMs { get; set; } = 3000;
        public int DefaultFrameRate { get; set; } = 10;
        
        /// <summary>
        /// FFmpeg binary path (defaults to PATH lookup)
        /// </summary>
        public string FfmpegPath { get; set; } = "ffmpeg";

        /// <summary>
        /// macOS avfoundation input selector, e.g. "0:none" (videoIndex:audioIndex)
        /// </summary>
        public string MacAvFoundationInput { get; set; } = "0:none";

        /// <summary>
        /// Linux webcam device path (v4l2), e.g. /dev/video0
        /// </summary>
        public string LinuxVideoDevice { get; set; } = "/dev/video0";
    }

    public class LoggingSettings
    {
        public bool EnableDetailedErrors { get; set; } = false;
        public string LogLevel { get; set; } = "Information";
    }
}
