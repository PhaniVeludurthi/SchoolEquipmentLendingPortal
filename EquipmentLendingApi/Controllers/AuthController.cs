using EquipmentLendingApi.Data;
using EquipmentLendingApi.Dtos;
using EquipmentLendingApi.Model;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace EquipmentLendingApi.Controllers
{
    [ApiController, Route("api/auth")]
    public class AuthController(AppDbContext db, IConfiguration config, ILogger<AuthController> logger) : ControllerBase
    {
        private readonly AppDbContext _db = db;
        private readonly IConfiguration _config = config;
        private readonly ILogger<AuthController> _logger = logger;

        [HttpPost("register")]
        public async Task<IActionResult> Register(UserRegisterDto dto)
        {
            try
            {
                _logger.LogInformation("Registration attempt for email: {Email}", dto.Email);

                if (_db.Users.Any(u => u.Email == dto.Email))
                {
                    _logger.LogWarning("Registration failed - Email already exists: {Email}", dto.Email);
                    return BadRequest("Email already exists");
                }

                var user = new User
                {
                    Name = dto.Name,
                    Email = dto.Email,
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword(dto.Password),
                    Role = dto.Role
                };

                await _db.Users.AddAsync(user);
                await _db.SaveChangesAsync();

                _logger.LogInformation("User registered successfully: {Email}, Role: {Role}", dto.Email, dto.Role);
                return Ok(ApiResponse<object>.SuccessResponse(
                    new { userId = user.Id, email = user.Email, role = user.Role },
                    "User registered successfully"
                ));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during registration for email: {Email}", dto.Email);
                throw;
            }
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login(UserLoginDto dto)
        {
            try
            {
                _logger.LogInformation("Login attempt for email: {Email}", dto.Email);

                var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == dto.Email);
                if (user == null || !BCrypt.Net.BCrypt.Verify(dto.Password, user.PasswordHash))
                {
                    _logger.LogWarning("Login failed for email: {Email}", dto.Email);
                    return Unauthorized(new { message = "Invalid credentials" });
                }

                var claims = new[]
                {
                    new Claim(ClaimTypes.Name, user.Email),
                    new Claim(ClaimTypes.Role, user.Role),
                    new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())
                };
                var securityKey = _config["Jwt:Key"];
                ArgumentException.ThrowIfNullOrEmpty(securityKey, "JWT");
                var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(securityKey));
                var cred = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
                var token = new JwtSecurityToken(claims: claims, expires: DateTime.Now.AddMinutes(30), signingCredentials: cred);

                _logger.LogInformation("User logged in successfully: {Email}, Role: {Role}", user.Email, user.Role);

                return Ok(ApiResponse<object>.SuccessResponse(
                    new
                    {
                        token = new JwtSecurityTokenHandler().WriteToken(token),
                        user = new { id = user.Id, name = user.Name, email = user.Email, role = user.Role }
                    },
                    "Login successful"
                ));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during login for email: {Email}", dto.Email);
                throw;
            }
        }
    }
}
