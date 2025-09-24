using Microsoft.AspNetCore.Mvc;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace DnsServerCore.ApplicationCommon.Controllers
{
    [ApiController]
    [Route("api/auth")]
    public class AuthController : ControllerBase
    {
        private static readonly ConcurrentDictionary<string, UserRecord> Users = new();

        [HttpPost("register")]
        public IActionResult Register([FromForm] string username, [FromForm] string email, [FromForm] string password)
        {
            if (Users.ContainsKey(username.ToLower()))
                return BadRequest("Username already exists.");

            var salt = GenerateSalt();
            var hash = HashPassword(password, salt);

            Users[username.ToLower()] = new UserRecord
            {
                Username = username,
                Email = email,
                Salt = salt,
                PasswordHash = hash,
                Token = GenerateToken(username)
            };

            // TODO: Create zone garet.rslvd.net with restricted permissions
            return Ok(new { message = "Registered successfully", subdomain = $"{username}.rslvd.net" });
        }

        [HttpPost("login")]
        public IActionResult Login([FromForm] string username, [FromForm] string password)
        {
            if (!Users.TryGetValue(username.ToLower(), out var user))
                return Unauthorized("Invalid credentials.");

            var hash = HashPassword(password, user.Salt);
            if (hash != user.PasswordHash)
                return Unauthorized("Invalid credentials.");

            return Ok(new { token = user.Token, subdomain = $"{username}.rslvd.net" });
        }

        private static string GenerateSalt()
        {
            var bytes = new byte[16];
            RandomNumberGenerator.Fill(bytes);
            return Convert.ToBase64String(bytes);
        }

        private static string HashPassword(string password, string salt)
        {
            var combined = Encoding.UTF8.GetBytes(password + salt);
            using var sha256 = SHA256.Create();
            return Convert.ToBase64String(sha256.ComputeHash(combined));
        }

        private static string GenerateToken(string username)
        {
            var raw = $"{username}:{DateTime.UtcNow.Ticks}";
            using var sha256 = SHA256.Create();
            return Convert.ToBase64String(sha256.ComputeHash(Encoding.UTF8.GetBytes(raw)));
        }

        private class UserRecord
        {
            public string Username { get; set; }
            public string Email { get; set; }
            public string Salt { get; set; }
            public string PasswordHash { get; set; }
            public string Token { get; set; }
        }
    }
}