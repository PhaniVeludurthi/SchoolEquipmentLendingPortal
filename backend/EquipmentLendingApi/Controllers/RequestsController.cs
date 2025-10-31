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
        public async Task<IActionResult> List([FromQuery] string? status = null)
        {
            var userEmail = User.Identity?.Name;
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var userRole = User.FindFirst(ClaimTypes.Role)?.Value;

            _logger.LogInformation("Fetching requests for user: {Email}, Role: {Role}", userEmail, userRole);

            IQueryable<Request> query = _db.Requests
            .Include(r => r.Equipment)
            .Include(r => r.User)
            .Include(r => r.Approver);

            // Filter by role
            if (userRole != "admin" && userRole != "staff")
            {
                query = query.Where(r => r.UserId == userId);
            }

            // Filter by status if provided
            if (!string.IsNullOrEmpty(status))
            {
                query = query.Where(r => r.Status.ToLower() == status.ToLower());
            }

            var requests = await query.OrderByDescending(x => x.RequestedAt).ToListAsync();

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

            await using var transaction = await _db.Database.BeginTransactionAsync();

            try
            {
                // Check if equipment exists and lock the row
                var equipment = await _db.Equipment
                    .FromSqlRaw(@"
                    SELECT * FROM ""Equipment"" 
                    WHERE ""Id"" = {0} 
                    FOR UPDATE", dto.EquipmentId)
                    .FirstOrDefaultAsync();

                if (equipment == null || equipment.IsDeleted)
                {
                    _logger.LogWarning("Equipment not found: {EquipmentId}", dto.EquipmentId);
                    return NotFound(ApiResponse<Request>.NotFoundResponse("Equipment not found"));
                }

                // Check quantity availability
                if (dto.Quantity <= 0)
                {
                    return BadRequest(ApiResponse<Request>.ErrorResponse("Quantity must be greater than 0", 400));
                }

                if (dto.Quantity > equipment.Quantity)
                {
                    return BadRequest(ApiResponse<Request>.ErrorResponse($"Requested quantity exceeds total available ({equipment.Quantity})", 400));
                }

                var hasPendingRequest = await _db.Requests.AnyAsync(r =>
                                r.UserId == userId &&
                                r.EquipmentId == dto.EquipmentId &&
                                r.Status.ToLower() == "pending");

                if (hasPendingRequest)
                {
                    _logger.LogWarning("User already has pending request for equipment: {EquipmentId}", dto.EquipmentId);
                    return BadRequest(ApiResponse<Request>.ErrorResponse("You already have a pending request for this equipment", 400));
                }

                // Check if user already has approved/issued request for same equipment
                var hasActiveRequest = await _db.Requests.AnyAsync(r =>
                    r.UserId == userId &&
                    r.EquipmentId == dto.EquipmentId &&
                    (r.Status.ToLower() == "approved" || r.Status.ToLower() == "issued"));

                if (hasActiveRequest)
                {
                    _logger.LogWarning("User already has active request for equipment: {EquipmentId}", dto.EquipmentId);
                    return BadRequest(ApiResponse<Request>.ErrorResponse("You already have an active request for this equipment", 400));
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
                await transaction.CommitAsync();

                // Load navigation properties for response
                request.Equipment = equipment;
                request.User = new User { Id = user.Id, FullName = user.FullName, Email = user.Email, Role = user.Role };

                _logger.LogInformation("Borrow request created successfully: {RequestId}", request.Id);
                return Ok(ApiResponse<Request>.SuccessResponse(request, "Borrow request submitted successfully"));
            }
            catch (DbUpdateConcurrencyException)
            {
                await transaction.RollbackAsync();
                _logger.LogWarning("Concurrency conflict when creating request for equipment: {EquipmentId}", dto.EquipmentId);
                return Conflict(ApiResponse<Request>.ErrorResponse("Equipment is being updated. Please try again.", 409));
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Error creating borrow request");
                return StatusCode(500, ApiResponse<Request>.ErrorResponse("An error occurred while processing your request", 500));
            }
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
                .OrderBy(r => r.RequestedAt)
                .ToListAsync();

            _logger.LogInformation("Retrieved {Count} pending requests", pending.Count);
            return Ok(ApiResponse<List<Request>>.SuccessResponse(pending, "Pending requests retrieved successfully"));
        }

        [HttpPut("{id}")]
        [Authorize(Policy = "StaffOrAdmin")]
        public async Task<ActionResult<RequestDto>> UpdateRequest(string id, [FromBody] UpdateRequestDto update)
        {
            var currentUserId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            // Use transaction with appropriate isolation level
            await using var transaction = await _db.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.ReadCommitted);
            try
            {
                var request = await _db.Requests
                    .Include(r => r.Equipment)
                    .Include(r => r.User)
                    .Include(r => r.Approver)
                    .FirstOrDefaultAsync(r => r.Id == id);

                if (request == null)
                {
                    return NotFound(ApiResponse<Request>.NotFoundResponse("Request not found"));
                }
                var currentStatus = request.Status.ToLowerInvariant();
                Equipment? equipment = null;

                // Handle status changes
                if (update.Status != null)
                {
                    var newStatus = update.Status.ToLowerInvariant();

                    // Validate status transition
                    if (!IsValidStatusTransition(currentStatus, newStatus))
                    {
                        return BadRequest(ApiResponse<Request>.ErrorResponse(
                            $"Invalid status transition from {request.Status} to {update.Status}", 400));
                    }

                    // Lock equipment row if we're going to modify quantity
                    if (RequiresEquipmentUpdate(currentStatus, newStatus))
                    {
                        // Use pessimistic locking to prevent race conditions
                        equipment = await _db.Equipment
                                                .FromSqlRaw(@"
                            SELECT * FROM ""Equipment"" 
                            WHERE ""Id"" = {0} 
                            FOR UPDATE", request.EquipmentId)
                                                .FirstOrDefaultAsync();

                        if (equipment == null || equipment.IsDeleted)
                        {
                            return NotFound(ApiResponse<Request>.NotFoundResponse("Equipment not found"));
                        }
                    }

                    // Handle quantity changes based on status transition
                    var quantityChange = CalculateQuantityChange(currentStatus, newStatus, request.Quantity);

                    if (quantityChange != 0 && equipment != null)
                    {
                        var newAvailableQuantity = equipment.AvailableQuantity + quantityChange;

                        // Validate quantity bounds
                        if (newAvailableQuantity < 0)
                        {
                            return BadRequest(ApiResponse<Request>.ErrorResponse(
                                $"Insufficient equipment quantity available. Available: {equipment.AvailableQuantity}, " +
                                $"Requested: {request.Quantity}", 400));
                        }

                        if (newAvailableQuantity > equipment.Quantity)
                        {
                            return BadRequest(ApiResponse<Request>.ErrorResponse(
                                "Available quantity cannot exceed total quantity", 400));
                        }

                        equipment.AvailableQuantity = newAvailableQuantity;
                        _logger.LogInformation(
                            "Equipment {EquipmentId} quantity changed by {Change}. New available: {Available}",
                            equipment.Id, quantityChange, equipment.AvailableQuantity);
                    }

                    // Update request status
                    var oldStatus = request.Status;
                    request.Status = update.Status;

                    // Set status-specific fields
                    switch (newStatus)
                    {
                        case "approved":
                            request.ApprovedAt = update.ApprovedAt ?? DateTime.UtcNow;
                            request.ApprovedBy = currentUserId;
                            request.DueDate = update.DueDate;
                            break;

                        case "rejected":
                            request.RejectedAt = DateTime.UtcNow;
                            request.RejectedBy = currentUserId;
                            break;

                        case "issued":
                            request.IssuedAt = update.IssuedAt ?? DateTime.UtcNow;
                            break;

                        case "returned":
                            request.ReturnedAt = update.ReturnedAt ?? DateTime.UtcNow;
                            break;
                    }

                    _logger.LogInformation(
                        "Request {RequestId} status changed from {OldStatus} to {NewStatus} by {UserId}",
                        id, oldStatus, newStatus, currentUserId);
                }

                // Update other fields if provided
                if (update.ApprovedAt.HasValue && request.ApprovedAt == null)
                {
                    request.ApprovedAt = update.ApprovedAt;
                }

                if (update.ApprovedBy != null && request.ApprovedBy == null)
                {
                    request.ApprovedBy = update.ApprovedBy;
                }

                if (update.IssuedAt.HasValue && request.IssuedAt == null)
                {
                    request.IssuedAt = update.IssuedAt;
                }

                if (update.DueDate.HasValue)
                {
                    request.DueDate = update.DueDate;
                }

                if (update.ReturnedAt.HasValue && request.ReturnedAt == null)
                {
                    request.ReturnedAt = update.ReturnedAt;
                }

                if (update.AdminNotes != null)
                {
                    request.AdminNotes = update.AdminNotes;
                }

                // Save changes and commit transaction
                await _db.SaveChangesAsync();
                await transaction.CommitAsync();

                // Reload navigation properties for response
                await _db.Entry(request).ReloadAsync();
                await _db.Entry(request).Reference(r => r.Equipment).LoadAsync();
                await _db.Entry(request).Reference(r => r.User).LoadAsync();
                await _db.Entry(request).Reference(r => r.Approver).LoadAsync();

                _logger.LogInformation("Request {RequestId} updated successfully", id);
                return Ok(ApiResponse<RequestResponseDto>.SuccessResponse(
                    MapToDto(request), "Request updated successfully"));
            }
            catch (DbUpdateConcurrencyException ex)
            {
                await transaction.RollbackAsync();
                _logger.LogWarning(ex, "Concurrency conflict updating request {RequestId}", id);
                return Conflict(ApiResponse<Request>.ErrorResponse(
                    "Request or equipment was updated by another user. Please refresh and try again.", 409));
            }
            catch (DbUpdateException ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Database error updating request {RequestId}", id);
                return StatusCode(500, ApiResponse<Request>.ErrorResponse(
                    "Error updating request: " + ex.Message, 500));
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Unexpected error updating request {RequestId}", id);
                return StatusCode(500, ApiResponse<Request>.ErrorResponse(
                    "An unexpected error occurred while updating the request", 500));
            }
        }
        private static bool RequiresEquipmentUpdate(string currentStatus, string newStatus)
        {
            // Reserve quantity: pending → approved
            if (currentStatus == "pending" && newStatus == "approved")
                return true;

            // Return quantity: approved/issued → returned/cancelled
            if ((currentStatus == "approved" || currentStatus == "issued") &&
                (newStatus == "returned" || newStatus == "cancelled"))
                return true;

            return false;
        }

        private static int CalculateQuantityChange(string currentStatus, string newStatus, int requestQuantity)
        {
            // Reserve quantity (decrease available): pending → approved
            if (currentStatus == "pending" && newStatus == "approved")
                return -requestQuantity;

            // Return quantity (increase available): approved/issued → returned/cancelled
            if ((currentStatus == "approved" || currentStatus == "issued") &&
                (newStatus == "returned" || newStatus == "cancelled"))
                return requestQuantity;

            return 0;
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
