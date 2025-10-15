namespace EquipmentLendingApi.Dtos
{
    public record RequestDto(int EquipmentId, DateTime StartDate, DateTime EndDate);
    public record ApproveDto(bool Approve);
}
