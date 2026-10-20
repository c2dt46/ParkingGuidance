namespace ParkingGuidance.Web.Models;

/// <summary>Loại phương tiện nhận diện được (theo 4 lớp phương tiện của bộ COCO).</summary>
public enum VehicleType
{
    Car,
    Motorcycle,
    Bus,
    Truck
}

/// <summary>Trạng thái của 1 ô đỗ xe trên bản đồ tầng.</summary>
public enum SlotStatus
{
    Empty = 0,
    Occupied = 1,
    Error = 2
}
