using EquipmentLendingApi.Data;
using EquipmentLendingApi.Dtos;
using EquipmentLendingApi.Model;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace EquipmentLendingApi.Controllers
{
    [ApiController, Route("api/requests")]
    public class RequestsController(AppDbContext db, ILogger<RequestsController> logger) : ControllerBase
    {
        private readonly AppDbContext _db = db;
        private readonly ILogger<RequestsController> _logger = logger;

        [HttpGet]
        [Authorize]
        public async Task<IActionResult> List()
        {
            var userEmail = User.Identity?.Name;
            var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");
            var userRole = User.FindFirst(ClaimTypes.Role)?.Value;

            _logger.LogInformation("Fetching requests for user: {Email}, Role: {Role}", userEmail, userRole);

            var requests = userRole == "Admin" || userRole == "Staff"
                ? await _db.Requests.Include(r => r.Equipment).Include(r => r.User).ToListAsync()
                : await _db.Requests.Include(r => r.Equipment).Where(r => r.UserId == userId).ToListAsync();

            _logger.LogInformation("Retrieved {Count} requests", requests.Count);
            return Ok(ApiResponse<List<Request>>.SuccessResponse(requests, "Requests retrieved successfully"));
        }

        [HttpPost]
        [Authorize]
        public async Task<IActionResult> Borrow(RequestDto dto)
        {
            var userEmail = User.Identity?.Name;
            var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");
            var user = await _db.Users.FindAsync(userId);
            if (user == null)
            {
                _logger.LogInformation("User {User} not found", userId);
                return Unauthorized(ApiResponse<Request>.UnauthorizedResponse("User not found"));
            }
            _logger.LogInformation("Borrow request for equipment {EquipmentId} by user {UserId}", dto.EquipmentId, userId);

            // Check if equipment exists
            var equipment = await _db.Equipment.FindAsync(dto.EquipmentId);
            if (equipment == null)
            {
                _logger.LogWarning("Equipment not found: {EquipmentId}", dto.EquipmentId);
                return NotFound(ApiResponse<Request>.NotFoundResponse("Equipment not found"));
            }

            // Check quantity availability
            if (equipment.Quantity < 1)
            {
                _logger.LogWarning("Equipment unavailable: {EquipmentId}", dto.EquipmentId);
                return BadRequest(ApiResponse<Request>.ErrorResponse("Equipment is currently unavailable", 400));
            }

            // Check for overlapping approved requests
            var hasOverlap = await _db.Requests.AnyAsync(r =>
                r.EquipmentId == dto.EquipmentId &&
                r.Status == "Approved" &&
                r.StartDate < dto.EndDate && dto.StartDate < r.EndDate);

            if (hasOverlap)
            {
                _logger.LogWarning("Overlapping request detected for equipment: {EquipmentId}", dto.EquipmentId);
                return BadRequest(ApiResponse<Request>.ErrorResponse("Equipment is already borrowed for the selected dates", 400));
            }

            // Check if user has pending requests for same equipment
            var hasPendingRequest = await _db.Requests.AnyAsync(r =>
                r.UserId == userId &&
                r.EquipmentId == dto.EquipmentId &&
                r.Status == "Pending");

            if (hasPendingRequest)
            {
                _logger.LogWarning("User already has pending request for equipment: {EquipmentId}", dto.EquipmentId);
                return BadRequest(ApiResponse<Request>.ErrorResponse("You already have a pending request for this equipment", 400));
            }

            var request = new Request
            {
                UserId = userId,
                EquipmentId = dto.EquipmentId,
                StartDate = dto.StartDate,
                EndDate = dto.EndDate,
                Status = "Pending"
            };

            await _db.Requests.AddAsync(request);
            await _db.SaveChangesAsync();

            // Load navigation properties
            request.Equipment = equipment;
            request.User = new() { Id = user.Id, Name = user.Name, Email = user.Email, Role = user.Role };

            _logger.LogInformation("Borrow request created successfully: {RequestId}", request.Id);
            return Ok(ApiResponse<Request>.SuccessResponse(request, "Borrow request submitted successfully"));
        }

        [HttpGet("pending")]
        [Authorize(Policy = "StaffOrAdmin")]
        public async Task<IActionResult> Pending()
        {
            _logger.LogInformation("Fetching pending requests");

            var pending = await _db.Requests
                .Where(r => r.Status == "Pending")
                .Include(r => r.Equipment)
                .Include(r => r.User)
                .ToListAsync();

            _logger.LogInformation("Retrieved {Count} pending requests", pending.Count);
            return Ok(ApiResponse<List<Request>>.SuccessResponse(pending, "Pending requests retrieved successfully"));
        }

        [HttpPut("{id}/approve")]
        [Authorize(Policy = "StaffOrAdmin")]
        public async Task<IActionResult> Approve(int id, ApproveDto dto)
        {
            _logger.LogInformation("Processing approval for request: {RequestId}, Approve: {Approve}", id, dto.Approve);

            var request = await _db.Requests.Include(r => r.Equipment).Include(r => r.User).FirstOrDefaultAsync(r => r.Id == id);
            if (request == null)
            {
                _logger.LogWarning("Request not found: {RequestId}", id);
                return NotFound(ApiResponse<Request>.NotFoundResponse($"Request with ID {id} not found"));
            }

            if (request.Status != "Pending")
            {
                _logger.LogWarning("Request already processed: {RequestId}, Status: {Status}", id, request.Status);
                return BadRequest(ApiResponse<Request>.ErrorResponse($"Request is already {request.Status.ToLower()}", 400));
            }

            request.Status = dto.Approve ? "Approved" : "Rejected";
            await _db.SaveChangesAsync();

            var message = dto.Approve ? "Request approved successfully" : "Request rejected successfully";
            _logger.LogInformation("Request {RequestId} {Status}", id, request.Status);

            return Ok(ApiResponse<Request>.SuccessResponse(request, message));
        }

        [HttpPut("{id}/return")]
        [Authorize]
        public async Task<IActionResult> Return(int id)
        {
            var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");
            var userRole = User.FindFirst(ClaimTypes.Role)?.Value;

            _logger.LogInformation("Processing return for request: {RequestId} by user: {UserId}", id, userId);

            var request = await _db.Requests.Include(r => r.Equipment).Include(r => r.User).FirstOrDefaultAsync(r => r.Id == id);
            if (request == null)
            {
                _logger.LogWarning("Request not found: {RequestId}", id);
                return NotFound(ApiResponse<Request>.NotFoundResponse($"Request with ID {id} not found"));
            }

            // Only the requestor or staff/admin can mark as returned
            if (request.UserId != userId && userRole != "Staff" && userRole != "Admin")
            {
                _logger.LogWarning("Unauthorized return attempt for request: {RequestId} by user: {UserId}", id, userId);
                return Forbid();
            }

            if (request.Status != "Approved")
            {
                _logger.LogWarning("Cannot return request that is not approved: {RequestId}, Status: {Status}", id, request.Status);
                return BadRequest(ApiResponse<Request>.ErrorResponse("Only approved requests can be returned", 400));
            }

            request.Status = "Returned";
            await _db.SaveChangesAsync();

            _logger.LogInformation("Request marked as returned: {RequestId}", id);
            return Ok(ApiResponse<Request>.SuccessResponse(request, "Equipment returned successfully"));
        }
    }
}
