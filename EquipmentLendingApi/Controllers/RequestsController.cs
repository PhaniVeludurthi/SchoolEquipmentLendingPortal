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
    [Authorize]
    public class RequestsController(AppDbContext db, ILogger<RequestsController> logger) : ControllerBase
    {
        private readonly AppDbContext _db = db;
        private readonly ILogger<RequestsController> _logger = logger;

        [HttpGet]
        public async Task<IActionResult> List()
        {
            var userEmail = User.Identity?.Name;
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var userRole = User.FindFirst(ClaimTypes.Role)?.Value;

            _logger.LogInformation("Fetching requests for user: {Email}, Role: {Role}", userEmail, userRole);

            var requests = userRole == "admin" || userRole == "staff"
                ? await _db.Requests.Include(r => r.Equipment).Include(r => r.User).Include(x => x.Approver).ToListAsync()
                : await _db.Requests.Include(r => r.Equipment).Include(r => r.User).Include(x => x.Approver).Where(r => r.UserId == userId).ToListAsync();

            _logger.LogInformation("Retrieved {Count} requests", requests.Count);
            return Ok(ApiResponse<List<Request>>.SuccessResponse(requests, "Requests retrieved successfully"));
        }

        [HttpPost]
        public async Task<IActionResult> Borrow(RequestDto dto)
        {
            var userEmail = User.Identity?.Name;
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
            {
                return NotFound(ApiResponse<Request>.NotFoundResponse("User not found"));
            }
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
            if (equipment.AvailableQuantity < 1)
            {
                _logger.LogWarning("Equipment unavailable: {EquipmentId}", dto.EquipmentId);
                return BadRequest(ApiResponse<Request>.ErrorResponse("Equipment is currently unavailable", 400));
            }

            // Check for overlapping approved requests
            var hasOverlap = await _db.Requests.AnyAsync(r =>
                r.EquipmentId == dto.EquipmentId &&
                r.Status.ToLower() == "approved");

            if (hasOverlap)
            {
                _logger.LogWarning("Overlapping request detected for equipment: {EquipmentId}", dto.EquipmentId);
                return BadRequest(ApiResponse<Request>.ErrorResponse("Equipment is already borrowed for the selected dates", 400));
            }

            // Check if user has pending requests for same equipment
            var hasPendingRequest = await _db.Requests.AnyAsync(r =>
                r.UserId == userId &&
                r.EquipmentId == dto.EquipmentId &&
                r.Status.ToLower() == "pending");

            if (hasPendingRequest)
            {
                _logger.LogWarning("User already has pending request for equipment: {EquipmentId}", dto.EquipmentId);
                return BadRequest(ApiResponse<Request>.ErrorResponse("You already have a pending request for this equipment", 400));
            }

            var request = new Request
            {
                UserId = userId,
                EquipmentId = dto.EquipmentId,
                RequestedAt = DateTime.UtcNow,
                Quantity = dto.Quantity,
                Notes = dto.Notes,
                Status = "pending"
            };

            await _db.Requests.AddAsync(request);
            await _db.SaveChangesAsync();

            // Load navigation properties
            request.Equipment = equipment;
            request.User = new() { Id = user.Id, FullName = user.FullName, Email = user.Email, Role = user.Role };

            _logger.LogInformation("Borrow request created successfully: {RequestId}", request.Id);
            return Ok(ApiResponse<Request>.SuccessResponse(request, "Borrow request submitted successfully"));
        }

        [HttpGet("pending")]
        [Authorize(Policy = "StaffOrAdmin")]
        public async Task<IActionResult> Pending()
        {
            _logger.LogInformation("Fetching pending requests");

            var pending = await _db.Requests
                .Where(r => r.Status.ToLowerInvariant() == "pending")
                .Include(r => r.Equipment)
                .Include(r => r.User)
                .ToListAsync();

            _logger.LogInformation("Retrieved {Count} pending requests", pending.Count);
            return Ok(ApiResponse<List<Request>>.SuccessResponse(pending, "Pending requests retrieved successfully"));
        }

        [HttpPut("{id}/approve")]
        [Authorize(Policy = "StaffOrAdmin")]
        public async Task<IActionResult> Approve(string id, ApproveDto dto)
        {
            _logger.LogInformation("Processing approval for request: {RequestId}, Approve: {Approve}", id, dto.Approve);

            var request = await _db.Requests.Include(r => r.Equipment).Include(r => r.User).FirstOrDefaultAsync(r => r.Id == id);
            if (request == null)
            {
                _logger.LogWarning("Request not found: {RequestId}", id);
                return NotFound(ApiResponse<Request>.NotFoundResponse($"Request with ID {id} not found"));
            }

            if (request.Status.ToLowerInvariant() != "pending")
            {
                _logger.LogWarning("Request already processed: {RequestId}, Status: {Status}", id, request.Status);
                return BadRequest(ApiResponse<Request>.ErrorResponse($"Request is already {request.Status.ToLower()}", 400));
            }

            request.Status = dto.Approve ? "approved" : "rejected";
            await _db.SaveChangesAsync();

            var message = dto.Approve ? "Request approved successfully" : "Request rejected successfully";
            _logger.LogInformation("Request {RequestId} {Status}", id, request.Status);

            return Ok(ApiResponse<Request>.SuccessResponse(request, message));
        }

        [HttpPut("{id}/return")]
        public async Task<IActionResult> Return(string id)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var userRole = User.FindFirst(ClaimTypes.Role)?.Value;

            _logger.LogInformation("Processing return for request: {RequestId} by user: {UserId}", id, userId);

            var request = await _db.Requests.Include(r => r.Equipment).Include(r => r.User).FirstOrDefaultAsync(r => r.Id == id);
            if (request == null)
            {
                _logger.LogWarning("Request not found: {RequestId}", id);
                return NotFound(ApiResponse<Request>.NotFoundResponse($"Request with ID {id} not found"));
            }

            // Only the requestor or staff/admin can mark as returned
            if (request.UserId != userId && userRole != "staff" && userRole != "admin")
            {
                _logger.LogWarning("Unauthorized return attempt for request: {RequestId} by user: {UserId}", id, userId);
                return Forbid();
            }

            if (request.Status.ToLowerInvariant() != "approved")
            {
                _logger.LogWarning("Cannot return request that is not approved: {RequestId}, Status: {Status}", id, request.Status);
                return BadRequest(ApiResponse<Request>.ErrorResponse("Only approved requests can be returned", 400));
            }

            request.Status = "returned";
            await _db.SaveChangesAsync();

            _logger.LogInformation("Request marked as returned: {RequestId}", id);
            return Ok(ApiResponse<Request>.SuccessResponse(request, "Equipment returned successfully"));
        }
        [HttpPut("{id}")]
        public async Task<ActionResult<RequestDto>> UpdateRequest(string id, [FromBody] UpdateRequestDto update)
        {
            var request = await _db.Requests
                .Include(r => r.Equipment)
                .Include(r => r.User)
                .Include(r => r.Approver)
                .FirstOrDefaultAsync(r => r.Id == id);

            if (request == null)
            {
                return NotFound(new { message = "Request not found" });
            }

            // Update only the provided fields
            if (update.Status != null)
            {
                var currentStatus = request.Status.ToLowerInvariant();
                var newStatus = update.Status.ToLowerInvariant();

                if (!IsValidStatusTransition(currentStatus, newStatus))
                {
                    return BadRequest(new { message = $"Invalid status transition from {request.Status} to {update.Status}" });
                }

                var oldStatus = request.Status;
                request.Status = update.Status;

                // Handle equipment quantity when status changes
                if (newStatus == "approved" && oldStatus == "pending")
                {
                    // Reserve equipment quantity
                    if (request.Equipment != null)
                    {
                        if (request.Equipment.AvailableQuantity < request.Quantity)
                        {
                            return BadRequest(new { message = "Insufficient equipment quantity available" });
                        }
                        request.Equipment.AvailableQuantity -= request.Quantity;
                    }
                }
                else if (update.Status == "returned" || update.Status == "cancelled")
                {
                    // Return equipment quantity
                    if (request.Equipment != null && (oldStatus == "approved" || oldStatus == "issued"))
                    {
                        request.Equipment.AvailableQuantity += request.Quantity;
                    }
                }
            }

            if (update.ApprovedAt.HasValue)
            {
                request.ApprovedAt = update.ApprovedAt;
            }

            if (update.ApprovedBy != null)
            {
                request.ApprovedBy = update.ApprovedBy;
            }

            if (update.IssuedAt.HasValue)
            {
                request.IssuedAt = update.IssuedAt;
            }

            if (update.DueDate.HasValue)
            {
                request.DueDate = update.DueDate;
            }

            if (update.ReturnedAt.HasValue)
            {
                request.ReturnedAt = update.ReturnedAt;
            }

            if (update.AdminNotes != null)
            {
                request.AdminNotes = update.AdminNotes;
            }

            try
            {
                await _db.SaveChangesAsync();
            }
            catch (DbUpdateException ex)
            {
                return StatusCode(500, new { message = "Error updating request", error = ex.Message });
            }

            // Reload to get updated navigation properties
            await _db.Entry(request).ReloadAsync();
            await _db.Entry(request).Reference(r => r.Equipment).LoadAsync();
            await _db.Entry(request).Reference(r => r.User).LoadAsync();
            await _db.Entry(request).Reference(r => r.Approver).LoadAsync();

            return Ok(MapToDto(request));
        }

        private bool IsValidStatusTransition(string currentStatus, string newStatus)
        {
            // Define valid status transitions
            var validTransitions = new Dictionary<string, List<string>>
        {
            { "pending", new List<string> { "approved", "rejected", "cancelled" } },
            { "approved", new List<string> { "issued", "cancelled" } },
            { "issued", new List<string> { "returned", "overdue" } },
            { "overdue", new List<string> { "returned" } },
            { "returned", new List<string>() },
            { "rejected", new List<string>() },
            { "cancelled", new List<string>() }
        };

            return validTransitions.ContainsKey(currentStatus) &&
                   validTransitions[currentStatus].Contains(newStatus);
        }

        private RequestResponseDto MapToDto(Request request)
        {
            return new RequestResponseDto
            {
                Id = request.Id,
                UserId = request.UserId,
                EquipmentId = request.EquipmentId,
                Quantity = request.Quantity,
                IssuedAt = request.IssuedAt,
                DueDate = request.DueDate,
                Status = request.Status,
                RequestedAt = request.RequestedAt,
                ApprovedAt = request.ApprovedAt,
                ApprovedBy = request.ApprovedBy,
                ReturnedAt = request.ReturnedAt,
                Notes = request.Notes,
                AdminNotes = request.AdminNotes,
                User = request.User != null ? new UserDto
                {
                    Id = request.User.Id,
                    // Map other user properties
                } : null,
                Approver = request.Approver != null ? new UserDto
                {
                    Id = request.Approver.Id,
                    // Map other user properties
                } : null,
                Equipment = request.Equipment != null ? new EquipmentResponseDto
                {
                    Id = request.Equipment.Id,
                    Name = request.Equipment.Name,
                    Category = request.Equipment.Category,
                    Quantity = request.Equipment.Quantity,
                    AvailableQuantity = request.Equipment.AvailableQuantity,
                    Description = request.Equipment.Description,
                    Condition = request.Equipment.Condition
                } : null
            };
        }
    }
}
