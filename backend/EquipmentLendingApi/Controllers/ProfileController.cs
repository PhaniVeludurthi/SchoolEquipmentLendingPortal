using EquipmentLendingApi.Data;
using EquipmentLendingApi.Dtos;
using EquipmentLendingApi.Model;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace EquipmentLendingApi.Controllers
{
    [ApiController, Route("api/profile")]
    [Authorize]
    public class ProfileController(AppDbContext db, ILogger<ProfileController> logger) : ControllerBase
    {
        private readonly AppDbContext _db = db;
        private readonly ILogger<ProfileController> _logger = logger;
        [HttpGet("me")]
        public async Task<IActionResult> GetMyProfile()
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            if (string.IsNullOrEmpty(userId))
            {
                return Unauthorized("User ID not found in token");
            }

            var userProfile = await _db.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == userId);

            if (userProfile == null)
            {
                return NotFound(ApiResponse<ProfileDto>.NotFoundResponse("User not found"));
            }

            return Ok(ApiResponse<ProfileDto>.SuccessResponse(new ProfileDto
            {
                Id = userProfile.Id,
                Email = userProfile.Email,
                FullName = userProfile.FullName,
                Role = userProfile.Role,
            }));
        }
    }
}
