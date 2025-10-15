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
            var equipment = await _db.Equipment.ToListAsync();
            _logger.LogInformation("Retrieved {Count} equipment items", equipment.Count);
            return Ok(ApiResponse<List<Equipment>>.SuccessResponse(equipment, "Equipment list retrieved successfully"));
        }

        [HttpGet("{id}")]
        [Authorize]
        public async Task<IActionResult> GetById(int id)
        {
            _logger.LogInformation("Fetching equipment with ID: {Id}", id);
            var equipment = await _db.Equipment.FindAsync(id);

            if (equipment == null)
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
                Quantity = dto.Quantity
            };

            await _db.Equipment.AddAsync(equipment);
            await _db.SaveChangesAsync();

            _logger.LogInformation("Equipment added successfully with ID: {Id}", equipment.Id);
            return Ok(ApiResponse<Equipment>.SuccessResponse(equipment, "Equipment added successfully"));
        }

        [HttpPut("{id}")]
        [Authorize(Policy = "AdminOnly")]
        public async Task<IActionResult> Update(int id, EquipmentDto dto)
        {
            _logger.LogInformation("Updating equipment with ID: {Id}", id);

            var equipment = await _db.Equipment.FindAsync(id);
            if (equipment == null)
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
            await _db.SaveChangesAsync();

            _logger.LogInformation("Equipment updated successfully: {Id}", id);
            return Ok(ApiResponse<Equipment>.SuccessResponse(equipment, "Equipment updated successfully"));
        }

        [HttpDelete("{id}")]
        [Authorize(Policy = "AdminOnly")]
        public async Task<IActionResult> Delete(int id)
        {
            _logger.LogInformation("Deleting equipment with ID: {Id}", id);
            var equipment = await _db.Equipment.FindAsync(id);

            if (equipment == null)
            {
                _logger.LogWarning("Equipment not found with ID: {Id}", id);
                return NotFound(ApiResponse<object>.NotFoundResponse($"Equipment with ID {id} not found"));
            }

            var hasActiveRequests = await _db.Requests.AnyAsync(r =>
                r.EquipmentId == id &&
                (r.Status == "Pending" || r.Status == "Approved"));

            if (hasActiveRequests)
            {
                _logger.LogWarning("Cannot delete equipment with active requests: {Id}", id);
                return BadRequest(ApiResponse<object>.ErrorResponse("Cannot delete equipment with active or pending requests", 400));
            }

            _db.Equipment.Remove(equipment);
            await _db.SaveChangesAsync();

            _logger.LogInformation("Equipment deleted successfully: {Id}", id);
            return Ok(ApiResponse<object>.SuccessResponse(null, "Equipment deleted successfully"));
        }
    }
}
