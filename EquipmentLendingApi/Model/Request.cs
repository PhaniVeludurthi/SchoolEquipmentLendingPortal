namespace EquipmentLendingApi.Model
{
    public class Request
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public int EquipmentId { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public string Status { get; set; } = "Pending"; // Pending, Approved, Rejected, Returned

        public User? User { get; set; }
        public Equipment? Equipment { get; set; }
    }
}
