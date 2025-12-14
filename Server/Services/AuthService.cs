using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq; 
using System; // <--- [FIX] Thêm using System để dùng StringComparison

namespace Server.Services
{
    // Cung cấp dịch vụ quản lý phiên đăng nhập và xác thực kết nối
    public class AuthService
    {
        private const string MASTER_SETUP_CODE = "datdinhtuyen"; 
        
        // Key: ConnectionId của Client, Value: Username đã đăng nhập
        private readonly ConcurrentDictionary<string, string> _authenticatedConnections = 
            new ConcurrentDictionary<string, string>(); 
        
        // Key: ConnectionId đã nhập đúng Setup Code, chờ đăng ký tài khoản Admin
        // Đây là trạng thái tạm thời giữa Bước 1 và Bước 2 của quy trình Setup
        private readonly ConcurrentDictionary<string, bool> _setupPendingConnections =
            new ConcurrentDictionary<string, bool>();
        
        private readonly UserRepository _userRepository;

        public AuthService(UserRepository userRepository) // Inject UserRepository
        {
            _userRepository = userRepository;
        }
        
        // --- LOGIC QUẢN LÝ TRẠNG THÁI KHỞI ĐỘNG ---

        /// <summary>
        /// Kiểm tra Mã Khóa Chủ cho lần khởi động Server đầu tiên.
        /// </summary>
        /// <param name="connectionId">ID kết nối hiện tại.</param>
        /// <param name="code">Mã khóa chủ do người dùng nhập.</param>
        /// <returns>True nếu mã đúng và Server chưa có tài khoản.</returns>
        public bool ValidateSetupCode(string connectionId, string code)
        {
            // 1. Chỉ cho phép dùng mã Setup nếu chưa có tài khoản nào được đăng ký
            if (_userRepository.IsAnyUserRegistered()) return false;

            // 2. So sánh mã (Dùng StringComparison.Ordinal để so sánh chuỗi an toàn hơn)
            if (string.Equals(code, MASTER_SETUP_CODE, StringComparison.Ordinal)) // <--- [CẬP NHẬT] Dùng string.Equals
            {
                // Đánh dấu Client này đã được phép ĐĂNG KÝ (tạm thời)
                _setupPendingConnections.TryAdd(connectionId, true);
                return true;
            }
            return false;
        }
        
        /// <summary>
        /// Kiểm tra xem kết nối này có đang ở trạng thái chờ ĐĂNG KÝ ADMIN không.
        /// </summary>
        public bool IsRegistrationAllowed(string connectionId)
        {
            // Cho phép đăng ký nếu Server chưa có user nào VÀ đã nhập đúng Setup Code
            return _userRepository.IsAnyUserRegistered() == false && _setupPendingConnections.ContainsKey(connectionId);
        }

        // --- LOGIC ĐĂNG KÝ/ĐĂNG NHẬP ---
        
        /// <summary>
        /// Thử đăng ký người dùng mới.
        /// </summary>
        public async Task<bool> TryRegisterAsync(string connectionId, string username, string password)
        {
            // Chỉ cho phép Đăng ký nếu đây là lần đầu và đã nhập đúng Master Code
            if (!IsRegistrationAllowed(connectionId)) return false; 
            
            if (await _userRepository.AddUserAsync(username, password))
            {
                // Sau khi đăng ký thành công, xóa trạng thái Setup Code (chìa khóa đã dùng xong)
                _setupPendingConnections.TryRemove(connectionId, out _);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Xác thực thông tin và lưu trạng thái đăng nhập.
        /// </summary>
        public bool TryAuthenticate(string connectionId, string username, string password)
        {
            var user = _userRepository.GetUser(username);
            
            if (user == null) return false;

            // Kiểm tra mật khẩu (So sánh Plain Text an toàn)
            if (string.Equals(user.PasswordHash, password, StringComparison.Ordinal)) // <--- [CẬP NHẬT] Dùng string.Equals
            {
                _authenticatedConnections.TryAdd(connectionId, username);
                return true;
            }
            return false;
        }
        
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