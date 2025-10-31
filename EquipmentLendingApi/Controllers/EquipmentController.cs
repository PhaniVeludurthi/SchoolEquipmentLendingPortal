using EquipmentLendingApi.Data;
using EquipmentLendingApi.Dtos;
using EquipmentLendingApi.Model;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EquipmentLendingApi.Controllers
{
    [ApiController, Route("api/equipment")]
    public class EquipmentController(AppDbContext db, ILogger<EquipmentController> logger) : ControllerBase
    {
        private readonly AppDbContext _db = db;
        private readonly ILogger<EquipmentController> _logger = logger;

        [HttpGet]
        [Authorize]
        public async Task<IActionResult> List()
        {
            _logger.LogInformation("Fetching equipment list");
            var equipment = await _db.Equipment.Where(x => x.IsDeleted == false).ToListAsync();
            _logger.LogInformation("Retrieved {Count} equipment items", equipment.Count);
            return Ok(ApiResponse<List<Equipment>>.SuccessResponse(equipment, "Equipment list retrieved successfully"));
        }

        [HttpGet("{id}")]
        [Authorize]
        public async Task<IActionResult> GetById(string id)
        {
            _logger.LogInformation("Fetching equipment with ID: {Id}", id);
            var equipment = await _db.Equipment.FindAsync(id);

            if (equipment == null || equipment.IsDeleted)
            {
                _logger.LogWarning("Equipment not found with ID: {Id}", id);
                return NotFound(ApiResponse<Equipment>.NotFoundResponse($"Equipment with ID {id} not found"));
            }

            return Ok(ApiResponse<Equipment>.SuccessResponse(equipment, "Equipment retrieved successfully"));
        }

        [HttpPost]
        [Authorize(Policy = "AdminOnly")]
        public async Task<IActionResult> Add(EquipmentDto dto)
        {
            _logger.LogInformation("Adding new equipment: {Name}", dto.Name);

            if (await _db.Equipment.AnyAsync(e => e.Name.ToLower() == dto.Name.ToLower()))
            {
                _logger.LogWarning("Equipment with name already exists: {Name}", dto.Name);
                return BadRequest(ApiResponse<Equipment>.ErrorResponse("Equipment with this name already exists", 400));
            }

            var equipment = new Equipment
            {
                Name = dto.Name,
                Category = dto.Category,
                Condition = dto.Condition,
                AvailableQuantity = dto.AvailableQuantity,
                Description = dto.Description,
                Quantity = dto.Quantity,
            };

            await _db.Equipment.AddAsync(equipment);
            await _db.SaveChangesAsync();

            _logger.LogInformation("Equipment added successfully with ID: {Id}", equipment.Id);
            return Ok(ApiResponse<Equipment>.SuccessResponse(equipment, "Equipment added successfully"));
        }

        [HttpPut("{id}")]
        [Authorize(Policy = "AdminOnly")]
        public async Task<IActionResult> Update(string id, EquipmentDto dto)
        {
            _logger.LogInformation("Updating equipment with ID: {Id}", id);

            var equipment = await _db.Equipment.FindAsync(id);
            if (equipment == null || equipment.IsDeleted)
            {
                _logger.LogWarning("Equipment not found with ID: {Id}", id);
                return NotFound(ApiResponse<Equipment>.NotFoundResponse($"Equipment with ID {id} not found"));
            }

            if (await _db.Equipment.AnyAsync(e => e.Name.ToLower() == dto.Name.ToLower() && e.Id != id))
            {
                _logger.LogWarning("Equipment with name already exists: {Name}", dto.Name);
                return BadRequest(ApiResponse<Equipment>.ErrorResponse("Equipment with this name already exists", 400));
            }

            equipment.Name = dto.Name;
            equipment.Category = dto.Category;
            equipment.Quantity = dto.Quantity;
            equipment.Description = dto.Description;
            equipment.Condition = dto.Condition;

            await _db.SaveChangesAsync();

            _logger.LogInformation("Equipment updated successfully: {Id}", id);
            return Ok(ApiResponse<Equipment>.SuccessResponse(equipment, "Equipment updated successfully"));
        }

        [HttpDelete("{id}")]
        [Authorize(Policy = "AdminOnly")]
        public async Task<IActionResult> Delete(string id)
        {
            _logger.LogInformation("Attempting to delete equipment with ID: {Id}", id);

            if (string.IsNullOrWhiteSpace(id))
            {
                _logger.LogWarning("Delete request failed: Empty equipment ID provided.");
                return BadRequest(ApiResponse<object>.ErrorResponse("Equipment ID is required.", 400));
            }

            var equipment = await _db.Equipment.FindAsync(id);

            if (equipment == null)
            {
                _logger.LogWarning("Delete request failed: Equipment not found with ID: {Id}", id);
                return NotFound(ApiResponse<object>.NotFoundResponse($"Equipment with ID '{id}' not found."));
            }

            // Validation: Check for active or pending requests
            var hasActiveRequests = await _db.Requests.AnyAsync(r =>
                r.EquipmentId == id &&
                (r.Status.ToLower() == "pending" || r.Status.ToLower() == "approved"));

            if (hasActiveRequests)
            {
                _logger.LogWarning("Delete request blocked: Equipment {Id} has active requests.", id);
                return BadRequest(ApiResponse<object>.ErrorResponse(
                    "Cannot delete equipment that has active or pending requests. Please close or cancel them first.",
                    400));
            }

            // Validation: Check if already deleted or inactive (optional)
            if (equipment.IsDeleted)
            {
                _logger.LogWarning("Delete request ignored: Equipment {Id} already marked as deleted.", id);
                return BadRequest(ApiResponse<object>.ErrorResponse(
                    "This equipment record is already deleted.", 400));
            }

            // Perform soft delete if needed
            equipment.IsDeleted = true;
            equipment.DeletedAt = DateTime.UtcNow;
            equipment.DeletedBy = User.Identity?.Name ?? "System";

            _db.Equipment.Update(equipment);
            await _db.SaveChangesAsync();

            _logger.LogInformation("Equipment deleted successfully: {Id}", id);
            return Ok(ApiResponse<object>.SuccessResponse(null, $"Equipment '{equipment.Name}' deleted successfully."));
        }

    }
}
