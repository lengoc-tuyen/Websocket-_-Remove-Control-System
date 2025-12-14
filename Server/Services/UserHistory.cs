using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq; // Thư viện cần thiết cho ToDictionary và ToList

namespace Server.Services
{
    // Lớp DTO đơn giản để lưu thông tin người dùng
    public class UserCredential
    {
        // Tên người dùng (Key để tra cứu)
        public string Username { get; set; }
        // Mật khẩu (Không Hash cho mục đích demo)
        public string PasswordHash { get; set; } 
    }
    
    // Quản lý việc lưu trữ và truy xuất người dùng từ file users.json
    // Mỗi Server chạy sẽ có một file users.json riêng biệt.
    public class UserRepository
    {
        // Đường dẫn file lưu trữ tài khoản
        private const string UsersFilePath = "users.json"; 
        
        // Dictionary lưu trữ tài khoản trong RAM để truy xuất nhanh
        private ConcurrentDictionary<string, UserCredential> _users;
        
        // Cấu hình JSON serializer
        private readonly JsonSerializerOptions _options = new JsonSerializerOptions { WriteIndented = true };

        public UserRepository()
        {
            // Tải dữ liệu khi Service được khởi tạo
            LoadUsersFromFile();
        }

        private void LoadUsersFromFile()
        {
            if (File.Exists(UsersFilePath))
            {
                try
                {
                    string json = File.ReadAllText(UsersFilePath);
                    var userList = JsonSerializer.Deserialize<List<UserCredential>>(json, _options); 
                    
                    // Chuyển List thành Dictionary để tra cứu nhanh bằng Username
                    _users = new ConcurrentDictionary<string, UserCredential>(userList.ToDictionary(u => u.Username, u => u));
                    Console.WriteLine($"[UserRepository] Đã tải {userList.Count} tài khoản từ file.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Lỗi tải file người dùng: {ex.Message}");
                    _users = new ConcurrentDictionary<string, UserCredential>();
                }
            }
            else
            {
                _users = new ConcurrentDictionary<string, UserCredential>();
                Console.WriteLine("[UserRepository] Không tìm thấy file users.json. Khởi tạo danh sách rỗng.");
            }
        }

        private async Task SaveUsersToFileAsync()
        {
            // Chuyển Dictionary thành List trước khi Serialize
            var userList = _users.Values.ToList();
            string json = JsonSerializer.Serialize(userList, _options);
            await File.WriteAllTextAsync(UsersFilePath, json);
        }

        public UserCredential GetUser(string username)
        {
            _users.TryGetValue(username, out UserCredential user);
            return user;
        }

        public bool IsUsernameTaken(string username)
        {
            return _users.ContainsKey(username);
        }

        public async Task<bool> AddUserAsync(string username, string password)
        {
            if (IsUsernameTaken(username))
            {
                return false;
            }

            var newUser = new UserCredential { Username = username, PasswordHash = password };
            if (_users.TryAdd(username, newUser))
            {
                await SaveUsersToFileAsync();
                return true;
            }
            return false;
        }

        public bool IsAnyUserRegistered()
        {
            return !_users.IsEmpty;
        }
    }
}