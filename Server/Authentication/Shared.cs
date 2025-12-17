namespace Server.Shared
{
    public static class ConnectionStatus
    {
        public const string RegistrationRequired = "REGISTRATION_REQUIRED"; 
        public const string Authenticated = "AUTHENTICATED"; 
        public const string LoginRequired = "LOGIN_REQUIRED"; 
    }

    public static class StatusType
    {
        public const string Auth = "AUTH";
        public const string App = "APP";
        public const string Keylog = "KEYLOG";
        public const string Screen = "SCREENSHOT";
        public const string Webcam = "WEBCAM";
        public const string System = "SYSTEM";
    }

    /// <summary>
    /// Các thông báo lỗi tiêu chuẩn.
    /// </summary>
    public static class ErrorMessages
    {
        public const string SetupCodeInvalid = "Mã Master Code không đúng.";
        public const string RegistrationNotAllowed = "Bạn chưa nhập Master Code hoặc đã hoàn tất đăng ký.";
        public const string UsernameTaken = "Tên đăng nhập đã tồn tại.";
        public const string InvalidCredentials = "Tên đăng nhập hoặc mật khẩu không đúng.";
    }
}