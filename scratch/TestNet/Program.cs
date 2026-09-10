using System.Net.NetworkInformation;

foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
{
    try
    {
        var props = nic.GetIPProperties();
        Console.WriteLine($"OK: {nic.Name} ({props.UnicastAddresses.Count} unicast, {props.GatewayAddresses.Count} gw, {props.DnsAddresses.Count} dns)");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL: {nic.Name} - {ex.GetType().Name}: {ex.Message}");
    }
}
