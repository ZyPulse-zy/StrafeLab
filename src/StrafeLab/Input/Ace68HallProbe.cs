using HidSharp;
using StrafeLab.Core;

namespace StrafeLab.Input;

public sealed class Ace68HallProbeResult
{
    public bool DeviceFound { get; init; }
    public string InputMode { get; init; } = "Raw Input fallback";
    public string Explanation { get; init; } = string.Empty;
    public Ace68DeviceIdentity? Device { get; init; }
    public List<Ace68InterfaceReport> Interfaces { get; init; } = [];
}

public sealed class Ace68DeviceIdentity
{
    public int VendorId { get; init; }
    public int ProductId { get; init; }
    public string ProductName { get; init; } = string.Empty;
    public string Manufacturer { get; init; } = string.Empty;
    public string? SerialNumber { get; init; }
    public string DevicePath { get; init; } = string.Empty;
}

public sealed class Ace68InterfaceReport
{
    public string DevicePath { get; init; } = string.Empty;
    public int VendorId { get; init; }
    public int ProductId { get; init; }
    public string ProductName { get; init; } = string.Empty;
    public int InputReportLength { get; init; }
    public int OutputReportLength { get; init; }
    public int FeatureReportLength { get; init; }
    public string DescriptorHex { get; init; } = "";
    public bool HasKeyboardUsage { get; init; }
    public bool HasVendorUsage { get; init; }
    public int NonEmptyReportsObserved { get; init; }
    public string? Error { get; init; }
}

/// <summary>Inventory only; continuous availability is established by decoded A0 reports.</summary>
public sealed class Ace68HallProbe
{
    public Task<Ace68HallProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
        => Task.Run(() => {
            var reports = new List<Ace68InterfaceReport>();
            Ace68DeviceIdentity? identity = null;
            foreach (var d in DeviceList.Local.GetHidDevices(0x41E4))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var name = d.GetProductName() ?? "MCHOSE";
                    if (!name.Contains("Ace", StringComparison.OrdinalIgnoreCase)) continue;
                    identity ??= new() {VendorId=d.VendorID,ProductId=d.ProductID,ProductName=name,DevicePath=d.DevicePath};
                    var raw = d.GetRawReportDescriptor();
                    reports.Add(new() {DevicePath=d.DevicePath,VendorId=d.VendorID,ProductId=d.ProductID,ProductName=name,
                        InputReportLength=d.GetMaxInputReportLength(),OutputReportLength=d.GetMaxOutputReportLength(),
                        FeatureReportLength=d.GetMaxFeatureReportLength(),DescriptorHex=Convert.ToHexString(raw)});
                }
                catch(Exception ex) {reports.Add(new() {DevicePath=d.DevicePath,Error=ex.Message});}
            }
            return new Ace68HallProbeResult {DeviceFound=identity!=null,Device=identity,Interfaces=reports,
                InputMode="Raw Input + Hall 探测", Explanation=identity == null ? "未发现 ACE68，使用 Raw Input" : $"{identity.ProductName} · {reports.Count} 个 HID collection"};
        },cancellationToken);
}
