using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq; 
using System; 
using Server.Models; // Cần thiết để gán Role User

namespace Server.Services
{
    /// <summary>
    /// Service quản lý phiên đăng nhập và xác thực theo cơ chế Master Code -> Register -> Login.
    /// Master Code được yêu cầu cho MỌI LẦN ĐĂNG KÝ TÀI KHOẢN.
    /// </summary>
    public class AuthService
    {
        // [CẤU HÌNH] Master Code cứng
        private const string MASTER_SETUP_CODE = "lengoctuyen"; 
        
        // Key: ConnectionId của Client, Value: Username đã đăng nhập
        private readonly ConcurrentDictionary<string, string> _authenticatedConnections = 
            new ConcurrentDictionary<string, string>(); 
        
        // Key: ConnectionId đã nhập đúng Setup Code, chờ đăng ký tài khoản (TẠM THỜI)
        private readonly ConcurrentDictionary<string, bool> _setupPendingConnections =
            new ConcurrentDictionary<string, bool>();
        
        private readonly UserRepository _userRepository;

        public AuthService(UserRepository userRepository) // Inject UserRepository
        {
            _userRepository = userRepository;
        }
        
        // --- LOGIC QUẢN LÝ TRẠNG THÁI KHỞI ĐỘNG/MASTER CODE ---

        /// <summary>
        /// Kiểm tra Mã Khóa Chủ. Mã này có thể dùng nhiều lần để đăng ký tài khoản.
        /// </summary>
        public bool ValidateSetupCode(string connectionId, string code)
        {
            // So sánh mã
            if (string.Equals(code, MASTER_SETUP_CODE, StringComparison.Ordinal)) 
            {
                // Đánh dấu Client này đã được phép ĐĂNG KÝ (tạm thời)
                _setupPendingConnections.TryAdd(connectionId, true);
                return true;
            }
            return false;
        }
        
        /// <summary>
        /// Kiểm tra xem kết nối này có đang ở trạng thái chờ ĐĂNG KÝ không (đã nhập Master Code).
        /// </summary>
        public bool IsRegistrationAllowed(string connectionId)
        {
            // Chỉ cần kiểm tra xem Client đã nhập đúng Master Code chưa
            return _setupPendingConnections.ContainsKey(connectionId);
        }

        // --- LOGIC ĐĂNG KÝ/ĐĂNG NHẬP ---
        
        /// <summary>
        /// Thử đăng ký người dùng mới (Yêu cầu phải nhập Master Code trước).
        /// </summary>
        public async Task<bool> TryRegisterAsync(string connectionId, string username, string password)
        {
            // [BẮT BUỘC] Phải nhập Master Code trước khi đăng ký
            if (!IsRegistrationAllowed(connectionId)) return false; 

            // [TIÊU THỤ KEY] Sau khi xác nhận được phép đăng ký, xóa trạng thái Setup Code 
            // Điều này buộc người dùng phải nhập lại Master Code nếu muốn đăng ký tài khoản khác
            _setupPendingConnections.TryRemove(connectionId, out _);
            
            // 1. Kiểm tra username đã tồn tại chưa
            if (_userRepository.IsUsernameTaken(username)) return false;

            // 2. Thêm người dùng mới vào UserRepository
            if (await _userRepository.AddUserAsync(username, password))
            {
                return true;
            }
            return false;
        }

        /// <summary>
        /// Xác thực thông tin và lưu trạng thái đăng nhập.
        /// </summary>
        public bool TryAuthenticate(string connectionId, string username, string password)
        {
            // 1. Kiểm tra Server đã được Setup chưa (nếu chưa thì không thể đăng nhập)
            if (!_userRepository.IsAnyUserRegistered()) return false; 

            var user = _userRepository.GetUser(username);
            
            if (user == null) return false;

            // 2. Kiểm tra mật khẩu (Giả định PasswordHash là mật khẩu Plain Text)
            if (string.Equals(user.PasswordHash, password, StringComparison.Ordinal)) 
            {
                _authenticatedConnections.TryAdd(connectionId, username);
                return true;
            }
            return false;
        }
        
        // --- LOGIC TRUY VẤN VÀ QUẢN LÝ PHIÊN ---
        
        public bool IsUsernameTaken(string username)
        {
            return _userRepository.IsUsernameTaken(username);
        }
        
        public bool IsAnyUserRegistered()
        {
            return _userRepository.IsAnyUserRegistered();
        }

        /// <summary>
        /// Kiểm tra xem kết nối hiện tại đã được xác thực chưa.
        /// </summary>
        public bool IsAuthenticated(string connectionId)
        {
            return _authenticatedConnections.ContainsKey(connectionId);
        }

        /// <summary>
        /// Xóa trạng thái xác thực khi Client ngắt kết nối.
        /// </summary>
        public void Logout(string connectionId)
        {
            _authenticatedConnections.TryRemove(connectionId, out _);
            _setupPendingConnections.TryRemove(connectionId, out _); // Xóa luôn cả trạng thái Setup tạm thời
        }
    }
}