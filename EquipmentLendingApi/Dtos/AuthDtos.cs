namespace EquipmentLendingApi.Dtos
{
    public record UserRegisterDto(string Name, string Email, string Password, string Role);
    public record UserLoginDto(string Email, string Password);
}
