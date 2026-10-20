namespace ParkingGuidance.Web.Models;

/// <summary>
/// Modbus TCP/IP chế độ SERVER: chương trình mở 1 cổng, thiết bị ngoài (PLC/SCADA) kết nối tới để đọc/ghi.
/// </summary>
public class ModbusServerEntry
{
    /// <summary>Số thứ tự của khai báo, bắt đầu từ 0. Xoá giữa chừng thì khai báo mới lấp lại số nhỏ nhất còn trống.</summary>
    public int Id { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>Cổng TCP chương trình sẽ mở (mặc định Modbus: 502).</summary>
    public int Port { get; set; } = 502;
    public string Description { get; set; } = "";

    public ModbusServerEntry Clone() => new() { Id = Id, Enabled = Enabled, Port = Port, Description = Description };
}

/// <summary>1 dòng bản đồ: giá trị biến MS được ghi vào 1 thanh ghi (holding register, 16 bit không dấu) của thiết bị.</summary>
public class ModbusMapEntry
{
    /// <summary>Tên biến MS (MS0, MS1…).</summary>
    public string Variable { get; set; } = "";
    /// <summary>Địa chỉ thanh ghi theo giao thức (tính từ 0; địa chỉ 0 ứng với thanh ghi 40001 trong cách gọi cũ).</summary>
    public int Address { get; set; }

    public ModbusMapEntry Clone() => new() { Variable = Variable, Address = Address };
}

/// <summary>
/// Modbus TCP/IP chế độ CLIENT: chương trình chủ động kết nối tới thiết bị ngoài theo địa chỉ IP + cổng của thiết bị,
/// cứ mỗi <see cref="IntervalMs"/> ghi giá trị các biến MS đã gán vào các thanh ghi tương ứng của thiết bị.
/// </summary>
public class ModbusClientEntry
{
    public int Id { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>Địa chỉ IPv4 của thiết bị.</summary>
    public string Ip { get; set; } = "";
    /// <summary>Cổng TCP của thiết bị (mặc định Modbus: 502).</summary>
    public int Port { get; set; } = 502;
    /// <summary>Unit ID (slave ID) 0–255; mặc định 1.</summary>
    public int UnitId { get; set; } = 1;
    /// <summary>Chu kỳ gửi (ms): cứ chừng này thời gian ghi lại toàn bộ biến đã gán tới thiết bị. 100 ms – 1 giờ.</summary>
    public int IntervalMs { get; set; } = 1000;
    public string Description { get; set; } = "";
    /// <summary>Bản đồ biến MS → địa chỉ thanh ghi của thiết bị này.</summary>
    public List<ModbusMapEntry> Mappings { get; set; } = new();

    public ModbusClientEntry Clone() => new()
    {
        Id = Id, Enabled = Enabled, Ip = Ip, Port = Port, UnitId = UnitId, IntervalMs = IntervalMs, Description = Description,
        Mappings = (Mappings ?? new()).Where(m => m is not null).Select(m => m.Clone()).ToList()
    };
}

/// <summary>Toàn bộ khai báo Modbus TCP/IP (lưu ở data/modbus-config.json).</summary>
public class ModbusConfig
{
    public List<ModbusServerEntry> Servers { get; set; } = new();
    public List<ModbusClientEntry> Clients { get; set; } = new();
}
