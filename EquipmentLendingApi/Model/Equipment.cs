namespace EquipmentLendingApi.Model
{
    public class Equipment
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Category { get; set; } = "";
        public int Quantity { get; set; }
    }

}
